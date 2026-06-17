[CmdletBinding()]
param(
    [string]$BaselineRef = "HEAD",
    [string]$ArtifactsDir = "artifacts/test-impact",
    [switch]$NoBuild,
    [switch]$KeepBaselineWorktree
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Invoke-Checked {
    param(
        [Parameter(Mandatory = $true)]
        [string]$FilePath,

        [Parameter(Mandatory = $true)]
        [string[]]$Arguments,

        [Parameter(Mandatory = $true)]
        [string]$WorkingDirectory
    )

    Write-Host "> $FilePath $($Arguments -join ' ')"
    Push-Location -LiteralPath $WorkingDirectory

    try {
        & $FilePath @Arguments
        $exitCode = $LASTEXITCODE
    }
    finally {
        Pop-Location
    }

    if ($exitCode -ne 0) {
        throw "Command failed with exit code $exitCode`: $FilePath $($Arguments -join ' ')"
    }
}

function Get-GitLines {
    param(
        [Parameter(Mandatory = $true)]
        [string[]]$Arguments,

        [Parameter(Mandatory = $true)]
        [string]$WorkingDirectory
    )

    $output = & git -C $WorkingDirectory @Arguments

    if ($LASTEXITCODE -ne 0) {
        throw "git $($Arguments -join ' ') failed with exit code $LASTEXITCODE"
    }

    @($output | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
}

function Join-UnderRoot {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Root,

        [Parameter(Mandatory = $true)]
        [string]$RelativePath
    )

    [System.IO.Path]::GetFullPath((Join-Path $Root $RelativePath))
}

function Add-FullRunReason {
    param(
        [Parameter(Mandatory = $true)]
        [hashtable]$ReasonsBySuite,

        [Parameter(Mandatory = $true)]
        [string]$SuiteName,

        [Parameter(Mandatory = $true)]
        [string]$Reason
    )

    if (-not $ReasonsBySuite.ContainsKey($SuiteName)) {
        $ReasonsBySuite[$SuiteName] = New-Object System.Collections.Generic.List[string]
    }

    if (-not $ReasonsBySuite[$SuiteName].Contains($Reason)) {
        $ReasonsBySuite[$SuiteName].Add($Reason)
    }
}

function Test-PathPrefix {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,

        [Parameter(Mandatory = $true)]
        [string]$Prefix
    )

    $Path.StartsWith($Prefix, [System.StringComparison]::OrdinalIgnoreCase)
}

$scriptRoot = Split-Path -Parent $PSCommandPath
$repoRootCandidate = [System.IO.Path]::GetFullPath((Join-Path $scriptRoot ".."))
$repoRoot = (& git -C $repoRootCandidate rev-parse --show-toplevel)

if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($repoRoot)) {
    throw "Could not resolve repository root."
}

$repoRoot = [System.IO.Path]::GetFullPath($repoRoot.Trim())
$toolProject = Join-UnderRoot -Root $repoRoot -RelativePath "TestImpactOnCoverage\src\relevant-tests\relevant-tests.csproj"

if (-not (Test-Path -LiteralPath $toolProject)) {
    Invoke-Checked `
        -FilePath "git" `
        -Arguments @("submodule", "update", "--init", "--recursive", "TestImpactOnCoverage") `
        -WorkingDirectory $repoRoot
}

if (-not (Test-Path -LiteralPath $toolProject)) {
    throw "TestImpactOnCoverage submodule is not initialized."
}

$testSuites = @(
    [pscustomobject]@{
        Name = "DomainTests"
        Project = "DomainTests\DomainTests.csproj"
    },
    [pscustomobject]@{
        Name = "ApiTests"
        Project = "ApiTests\ApiTests.csproj"
    }
)

$productProjectSuites = @{
    "Abstractions/Abstractions.csproj" = @("DomainTests", "ApiTests")
    "Domain/Domain.csproj" = @("DomainTests", "ApiTests")
    "SymLinkStorage/SymLinkStorage.csproj" = @("DomainTests", "ApiTests")
    "Api/Api.csproj" = @("ApiTests")
    "Mcp/Mcp.csproj" = @("ApiTests")
}

$artifactsRoot = if ([System.IO.Path]::IsPathRooted($ArtifactsDir)) {
    [System.IO.Path]::GetFullPath($ArtifactsDir)
}
else {
    Join-UnderRoot -Root $repoRoot -RelativePath $ArtifactsDir
}

$runStamp = Get-Date -Format "yyyyMMdd-HHmmss"
$runArtifactsRoot = Join-UnderRoot -Root $artifactsRoot -RelativePath $runStamp
New-Item -ItemType Directory -Force -Path $runArtifactsRoot | Out-Null

$changedFiles = @()
$changedFiles += Get-GitLines -WorkingDirectory $repoRoot -Arguments @("diff", "--name-only", "--relative", $BaselineRef, "--", ".")
$changedFiles += Get-GitLines -WorkingDirectory $repoRoot -Arguments @("ls-files", "--others", "--exclude-standard")
$changedFiles = @($changedFiles | Sort-Object -Unique)

$fullRunReasonsBySuite = @{}

foreach ($suite in $testSuites) {
    $fullRunReasonsBySuite[$suite.Name] = New-Object System.Collections.Generic.List[string]
}

foreach ($changedFile in $changedFiles) {
    $path = $changedFile.Replace("\", "/")

    if (Test-PathPrefix -Path $path -Prefix "DomainTests/") {
        Add-FullRunReason -ReasonsBySuite $fullRunReasonsBySuite -SuiteName "DomainTests" -Reason "DomainTests changed"
    }

    if (Test-PathPrefix -Path $path -Prefix "ApiTests/") {
        Add-FullRunReason -ReasonsBySuite $fullRunReasonsBySuite -SuiteName "ApiTests" -Reason "ApiTests changed"
    }

    if (Test-PathPrefix -Path $path -Prefix "TestSupport/") {
        Add-FullRunReason -ReasonsBySuite $fullRunReasonsBySuite -SuiteName "DomainTests" -Reason "shared test support changed"
        Add-FullRunReason -ReasonsBySuite $fullRunReasonsBySuite -SuiteName "ApiTests" -Reason "shared test support changed"
    }

    if (Test-PathPrefix -Path $path -Prefix "Api/wwwroot/") {
        Add-FullRunReason -ReasonsBySuite $fullRunReasonsBySuite -SuiteName "ApiTests" -Reason "Api UI assets changed"
    }

    if ($productProjectSuites.ContainsKey($path)) {
        foreach ($suiteName in $productProjectSuites[$path]) {
            Add-FullRunReason -ReasonsBySuite $fullRunReasonsBySuite -SuiteName $suiteName -Reason "$path changed"
        }
    }

    if ($path -eq "GraphData.sln" -or $path -like "Directory.Build.*" -or $path -like "Directory.Packages.*" -or $path -like "*.runsettings") {
        Add-FullRunReason -ReasonsBySuite $fullRunReasonsBySuite -SuiteName "DomainTests" -Reason "test infrastructure changed"
        Add-FullRunReason -ReasonsBySuite $fullRunReasonsBySuite -SuiteName "ApiTests" -Reason "test infrastructure changed"
    }
}

$baselineRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("graphdata-test-impact-" + [System.Guid]::NewGuid().ToString("N"))
$worktreeCreated = $false

try {
    Invoke-Checked `
        -FilePath "git" `
        -Arguments @("worktree", "add", "--detach", $baselineRoot, $BaselineRef) `
        -WorkingDirectory $repoRoot

    $worktreeCreated = $true

    Invoke-Checked `
        -FilePath "git" `
        -Arguments @("-C", $baselineRoot, "submodule", "update", "--init", "--recursive") `
        -WorkingDirectory $repoRoot

    foreach ($suite in $testSuites) {
        $testArguments = @("test", (Join-UnderRoot -Root $repoRoot -RelativePath $suite.Project))
        $suiteArtifactsDir = Join-UnderRoot -Root $runArtifactsRoot -RelativePath $suite.Name

        if ($NoBuild) {
            $testArguments += "--no-build"
        }

        if ($fullRunReasonsBySuite[$suite.Name].Count -gt 0) {
            Write-Host "Running full $($suite.Name): $($fullRunReasonsBySuite[$suite.Name] -join '; ')"
            $testArguments += "-p:RelevantTestsEnabled=false"
        }
        else {
            $testArguments += "-p:RelevantTestsEnabled=true"
            $testArguments += "-p:RelevantTestsBaselineRoot=$baselineRoot"
            $testArguments += "-p:RelevantTestsCurrentRoot=$repoRoot"
            $testArguments += "-p:RelevantTestsArtifactsDir=$suiteArtifactsDir"
        }

        Invoke-Checked -FilePath "dotnet" -Arguments $testArguments -WorkingDirectory $repoRoot
    }

    Write-Host "Relevant test artifacts: $runArtifactsRoot"
}
finally {
    if ($worktreeCreated -and -not $KeepBaselineWorktree) {
        try {
            Invoke-Checked `
                -FilePath "git" `
                -Arguments @("worktree", "remove", "--force", $baselineRoot) `
                -WorkingDirectory $repoRoot
        }
        catch {
            Write-Warning "Failed to remove baseline worktree '$baselineRoot': $_"
        }
    }
    elseif ($worktreeCreated) {
        Write-Host "Baseline worktree kept at: $baselineRoot"
    }
}
