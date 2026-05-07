param(
    [string]$InstallRoot = "$env:USERPROFILE\.lmstudio\graphdata-mcp-server",
    [string]$GraphStorageRoot = "",
    [string]$McpJsonPath = "$env:USERPROFILE\.lmstudio\mcp.json",
    [string]$ServerName = "graphdata",
    [switch]$DebugWait,
    [switch]$SkipPublish,
    [switch]$NoStop
)

$ErrorActionPreference = "Stop"

function Invoke-GitUtf8 {
    param(
        [Parameter(Mandatory = $true)][string]$Arguments,
        [Parameter(Mandatory = $true)][string]$WorkingDirectory
    )

    $startInfo = [System.Diagnostics.ProcessStartInfo]::new("git", $Arguments)
    $startInfo.WorkingDirectory = $WorkingDirectory
    $startInfo.UseShellExecute = $false
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $startInfo.StandardOutputEncoding = [System.Text.Encoding]::UTF8
    $startInfo.StandardErrorEncoding = [System.Text.Encoding]::UTF8

    $process = [System.Diagnostics.Process]::Start($startInfo)
    $output = $process.StandardOutput.ReadToEnd().Trim()
    $errorOutput = $process.StandardError.ReadToEnd().Trim()
    $process.WaitForExit()

    if ($process.ExitCode -ne 0) {
        throw "git $Arguments failed: $errorOutput"
    }

    return $output
}

$repoRoot = Invoke-GitUtf8 -Arguments "rev-parse --show-toplevel" -WorkingDirectory (Get-Location).Path
$projectPath = Join-Path $repoRoot "Mcp\Mcp.csproj"
$trayProjectPath = Join-Path $repoRoot "McpTray\McpTray.csproj"
$internalSyncPath = Join-Path (Split-Path -Parent $McpJsonPath) ".internal\last-synced-mcp-state.json"

if ([string]::IsNullOrWhiteSpace($GraphStorageRoot)) {
    $GraphStorageRoot = Join-Path $repoRoot "graph-data"
}

function Stop-InstalledMcp {
    param([string]$Root)

    $escapedRoot = [regex]::Escape($Root)
    $processes = Get-CimInstance Win32_Process -Filter "Name = 'Mcp.exe' OR Name = 'McpTray.exe' OR Name = 'dotnet.exe'" |
        Where-Object { $_.CommandLine -match $escapedRoot -or $_.CommandLine -match "graphdata-mcp-server" }

    foreach ($process in $processes) {
        Stop-Process -Id $process.ProcessId -Force -ErrorAction SilentlyContinue
    }
}

function ConvertTo-JsonFile {
    param(
        [Parameter(Mandatory = $true)]$Value,
        [Parameter(Mandatory = $true)][string]$Path
    )

    $directory = Split-Path -Parent $Path
    if ($directory) {
        New-Item -ItemType Directory -Force -Path $directory | Out-Null
    }

    $json = $Value | ConvertTo-Json -Depth 20
    [System.IO.File]::WriteAllText($Path, $json + "`n", [System.Text.UTF8Encoding]::new($false))
}

function Get-OrCreateJsonObject {
    param([string]$Path)

    if (Test-Path -LiteralPath $Path) {
        $text = [System.IO.File]::ReadAllText($Path)
        if (-not [string]::IsNullOrWhiteSpace($text)) {
            return $text | ConvertFrom-Json
        }
    }

    return [PSCustomObject]@{}
}

function Set-JsonProperty {
    param(
        [Parameter(Mandatory = $true)]$Object,
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)]$Value
    )

    if ($null -ne $Object.PSObject.Properties[$Name]) {
        $Object.PSObject.Properties.Remove($Name)
    }

    $Object | Add-Member -MemberType NoteProperty -Name $Name -Value $Value
}

function Remove-LegacyStatusDirectory {
    param([string]$StatusDirectory)

    if (Test-Path -LiteralPath $StatusDirectory) {
        Remove-Item -LiteralPath $StatusDirectory -Recurse -Force -ErrorAction SilentlyContinue
    }
}

if (-not $NoStop) {
    Stop-InstalledMcp -Root $InstallRoot
}

if (-not $SkipPublish) {
    dotnet publish $projectPath -c Release -o $InstallRoot
    dotnet publish $trayProjectPath -c Release -o $InstallRoot
}

$appSettings = [PSCustomObject]@{
    GraphStorage = [PSCustomObject]@{
        RootPath = $GraphStorageRoot
        MetadataFileName = "node.json"
    }
    McpTray = [PSCustomObject]@{
        Enabled = $true
        PipeName = "GraphDataMcpTray"
    }
}

Remove-LegacyStatusDirectory -StatusDirectory (Join-Path $InstallRoot "status")
ConvertTo-JsonFile -Value $appSettings -Path (Join-Path $InstallRoot "appsettings.json")

$config = Get-OrCreateJsonObject -Path $McpJsonPath
if ($null -eq $config.PSObject.Properties["mcpServers"]) {
    Set-JsonProperty -Object $config -Name "mcpServers" -Value ([PSCustomObject]@{})
}

$mcpServers = $config.mcpServers
$args = @(
    (Join-Path $InstallRoot "Mcp.dll")
)

if ($DebugWait) {
    $args += "--debug-wait"
}

$serverConfig = [PSCustomObject]@{
    command = "dotnet"
    args = $args
    cwd = $InstallRoot
}

Set-JsonProperty -Object $mcpServers -Name $ServerName -Value $serverConfig
ConvertTo-JsonFile -Value $config -Path $McpJsonPath

if (Test-Path -LiteralPath (Split-Path -Parent $internalSyncPath)) {
    ConvertTo-JsonFile -Value $config -Path $internalSyncPath
}

Write-Host "Installed $ServerName MCP server for LM Studio."
Write-Host "InstallRoot: $InstallRoot"
Write-Host "GraphStorageRoot: $GraphStorageRoot"
Write-Host "McpJsonPath: $McpJsonPath"
if ($DebugWait) {
    Write-Host "Debug wait is enabled. Attach Visual Studio to Mcp.exe after LM Studio starts the server."
}
