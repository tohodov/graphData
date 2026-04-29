param(
    [string]$InstallRoot = "$env:USERPROFILE\.lmstudio\graphdata-mcp-server",
    [string]$RepoLink = "$env:USERPROFILE\.lmstudio\graphdata-repo",
    [string]$McpJsonPath = "$env:USERPROFILE\.lmstudio\mcp.json",
    [string]$ServerName = "graphdata",
    [switch]$DebugWait,
    [switch]$SkipPublish,
    [switch]$NoStop
)

$ErrorActionPreference = "Stop"

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = Resolve-Path (Join-Path $scriptDir "..")
$projectPath = Join-Path $repoRoot "Mcp\Mcp.csproj"
$graphDataRoot = Join-Path $RepoLink "graph-data"
$internalSyncPath = Join-Path (Split-Path -Parent $McpJsonPath) ".internal\last-synced-mcp-state.json"

function Stop-InstalledMcp {
    param([string]$Root)

    $escapedRoot = [regex]::Escape($Root)
    $processes = Get-CimInstance Win32_Process -Filter "Name = 'Mcp.exe' OR Name = 'dotnet.exe'" |
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

if (-not $NoStop) {
    Stop-InstalledMcp -Root $InstallRoot
}

if (Test-Path -LiteralPath $RepoLink) {
    $item = Get-Item -LiteralPath $RepoLink -Force
    if (-not ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw "$RepoLink exists and is not a junction/reparse point."
    }
} else {
    New-Item -ItemType Junction -Path $RepoLink -Target $repoRoot | Out-Null
}

if (-not $SkipPublish) {
    dotnet publish $projectPath -c Release -o $InstallRoot
}

$config = Get-OrCreateJsonObject -Path $McpJsonPath
if ($null -eq $config.PSObject.Properties["mcpServers"]) {
    Set-JsonProperty -Object $config -Name "mcpServers" -Value ([PSCustomObject]@{})
}

$mcpServers = $config.mcpServers
$args = @(
    (Join-Path $InstallRoot "Mcp.dll"),
    "--GraphStorage:RootPath=$graphDataRoot"
)

if ($DebugWait) {
    $args += "--debug-wait"
}

$serverConfig = [PSCustomObject]@{
    command = "dotnet"
    args = $args
    cwd = $RepoLink
}

Set-JsonProperty -Object $mcpServers -Name $ServerName -Value $serverConfig
ConvertTo-JsonFile -Value $config -Path $McpJsonPath

if (Test-Path -LiteralPath (Split-Path -Parent $internalSyncPath)) {
    ConvertTo-JsonFile -Value $config -Path $internalSyncPath
}

Write-Host "Installed $ServerName MCP server for LM Studio."
Write-Host "InstallRoot: $InstallRoot"
Write-Host "RepoLink: $RepoLink"
Write-Host "McpJsonPath: $McpJsonPath"
if ($DebugWait) {
    Write-Host "Debug wait is enabled. Attach Visual Studio to Mcp.exe after LM Studio starts the server."
}
