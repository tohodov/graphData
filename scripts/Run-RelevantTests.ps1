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

function Read-JsonFile {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    Get-Content -LiteralPath $Path -Encoding UTF8 -Raw | ConvertFrom-Json
}

function Write-JsonFile {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,

        [Parameter(Mandatory = $true)]
        [object]$Value
    )

    $directory = Split-Path -Parent $Path

    if (-not [string]::IsNullOrWhiteSpace($directory)) {
        New-Item -ItemType Directory -Force -Path $directory | Out-Null
    }

    $json = $Value | ConvertTo-Json -Depth 100
    $encoding = New-Object System.Text.UTF8Encoding($false)
    [System.IO.File]::WriteAllText($Path, $json + [Environment]::NewLine, $encoding)
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

function ConvertTo-SafeName {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Name
    )

    $Name -replace '[^A-Za-z0-9_.-]', '_'
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

$productProjects = @(
    [pscustomobject]@{ Assembly = "Abstractions"; Project = "Abstractions\Abstractions.csproj" },
    [pscustomobject]@{ Assembly = "Domain"; Project = "Domain\Domain.csproj" },
    [pscustomobject]@{ Assembly = "SymLinkStorage"; Project = "SymLinkStorage\SymLinkStorage.csproj" },
    [pscustomobject]@{ Assembly = "Api"; Project = "Api\Api.csproj" },
    [pscustomobject]@{ Assembly = "Mcp"; Project = "Mcp\Mcp.csproj" }
)

$testSuites = @(
    [pscustomobject]@{
        Name = "DomainTests"
        Project = "DomainTests\DomainTests.csproj"
        TargetAssemblies = @("Abstractions", "Domain", "SymLinkStorage")
    },
    [pscustomobject]@{
        Name = "ApiTests"
        Project = "ApiTests\ApiTests.csproj"
        TargetAssemblies = @("Abstractions", "Domain", "SymLinkStorage", "Api", "Mcp")
    }
)

$artifactsRoot = if ([System.IO.Path]::IsPathRooted($ArtifactsDir)) {
    [System.IO.Path]::GetFullPath($ArtifactsDir)
}
else {
    Join-UnderRoot -Root $repoRoot -RelativePath $ArtifactsDir
}

$runStamp = Get-Date -Format "yyyyMMdd-HHmmss"
$runArtifactsRoot = Join-UnderRoot -Root $artifactsRoot -RelativePath $runStamp
$impactArtifactsRoot = Join-UnderRoot -Root $runArtifactsRoot -RelativePath "impact"
$usageArtifactsRoot = Join-UnderRoot -Root $runArtifactsRoot -RelativePath "usage"
$planArtifactsRoot = Join-UnderRoot -Root $runArtifactsRoot -RelativePath "plans"

New-Item -ItemType Directory -Force -Path $impactArtifactsRoot | Out-Null
New-Item -ItemType Directory -Force -Path $usageArtifactsRoot | Out-Null
New-Item -ItemType Directory -Force -Path $planArtifactsRoot | Out-Null

$changedFiles = @()
$changedFiles += Get-GitLines -WorkingDirectory $repoRoot -Arguments @("diff", "--name-only", "--relative", $BaselineRef, "--", ".")
$changedFiles += Get-GitLines -WorkingDirectory $repoRoot -Arguments @("ls-files", "--others", "--exclude-standard")
$changedFiles = @($changedFiles | Sort-Object -Unique)

$fullRunReasonsBySuite = @{}

foreach ($suite in $testSuites) {
    $fullRunReasonsBySuite[$suite.Name] = New-Object System.Collections.Generic.List[string]
}

$productProjectSuites = @{}

foreach ($product in $productProjects) {
    $normalizedProject = $product.Project.Replace("\", "/")
    $productProjectSuites[$normalizedProject] = @(
        $testSuites |
            Where-Object { $_.TargetAssemblies -contains $product.Assembly } |
            ForEach-Object { $_.Name }
    )
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

    $impactByAssembly = @{}

    foreach ($product in $productProjects) {
        $baselineProject = Join-UnderRoot -Root $baselineRoot -RelativePath $product.Project
        $currentProject = Join-UnderRoot -Root $repoRoot -RelativePath $product.Project
        $impactPath = Join-UnderRoot -Root $impactArtifactsRoot -RelativePath "$($product.Assembly).impact.json"

        if (-not (Test-Path -LiteralPath $baselineProject)) {
            foreach ($suite in $testSuites | Where-Object { $_.TargetAssemblies -contains $product.Assembly }) {
                Add-FullRunReason `
                    -ReasonsBySuite $fullRunReasonsBySuite `
                    -SuiteName $suite.Name `
                    -Reason "$($product.Project) is missing in baseline"
            }

            continue
        }

        Invoke-Checked `
            -FilePath "dotnet" `
            -Arguments @(
                "run",
                "--project", $toolProject,
                "--",
                "impact",
                "--baseline", $baselineProject,
                "--current", $currentProject,
                "--output", $impactPath
            ) `
            -WorkingDirectory $repoRoot

        $impactByAssembly[$product.Assembly] = Read-JsonFile -Path $impactPath
    }

    foreach ($suite in $testSuites) {
        foreach ($targetAssembly in $suite.TargetAssemblies) {
            $usagePath = Join-UnderRoot `
                -Root $usageArtifactsRoot `
                -RelativePath "$($suite.Name).$targetAssembly.usage.json"
            $testProject = Join-UnderRoot -Root $repoRoot -RelativePath $suite.Project

            Invoke-Checked `
                -FilePath "dotnet" `
                -Arguments @(
                    "run",
                    "--project", $toolProject,
                    "--",
                    "usage",
                    "--test-project", $testProject,
                    "--target-assembly", $targetAssembly,
                    "--output", $usagePath
                ) `
                -WorkingDirectory $repoRoot
        }
    }

    foreach ($suite in $testSuites) {
        $suiteSafeName = ConvertTo-SafeName -Name $suite.Name
        $combinedImpactPath = Join-UnderRoot -Root $impactArtifactsRoot -RelativePath "$suiteSafeName.combined-impact.json"
        $combinedUsagePath = Join-UnderRoot -Root $usageArtifactsRoot -RelativePath "$suiteSafeName.combined-usage.json"
        $planPath = Join-UnderRoot -Root $planArtifactsRoot -RelativePath "$suiteSafeName.plan.json"

        $membersById = [ordered]@{}

        foreach ($targetAssembly in $suite.TargetAssemblies) {
            if (-not $impactByAssembly.ContainsKey($targetAssembly)) {
                continue
            }

            foreach ($member in @($impactByAssembly[$targetAssembly].members)) {
                $id = [string]$member.symbol.id

                if (-not $membersById.Contains($id)) {
                    $membersById[$id] = $member
                }
            }
        }

        $combinedMembers = @(
            $membersById.GetEnumerator() |
                Sort-Object Key |
                ForEach-Object { $_.Value }
        )

        $combinedImpact = [ordered]@{
            version = 1
            generatedAtUtc = (Get-Date).ToUniversalTime().ToString("o")
            baselineProject = $BaselineRef
            currentProject = $repoRoot
            members = $combinedMembers
        }

        Write-JsonFile -Path $combinedImpactPath -Value $combinedImpact

        $testsByFullyQualifiedName = [ordered]@{}

        foreach ($targetAssembly in $suite.TargetAssemblies) {
            $usagePath = Join-UnderRoot `
                -Root $usageArtifactsRoot `
                -RelativePath "$($suite.Name).$targetAssembly.usage.json"
            $usage = Read-JsonFile -Path $usagePath

            foreach ($test in @($usage.tests)) {
                $fullyQualifiedName = [string]$test.fullyQualifiedName

                if (-not $testsByFullyQualifiedName.Contains($fullyQualifiedName)) {
                    $testsByFullyQualifiedName[$fullyQualifiedName] = [pscustomobject]@{
                        FullyQualifiedName = $fullyQualifiedName
                        DisplayName = [string]$test.displayName
                        SourceFile = [string]$test.sourceFile
                        Line = [int]$test.line
                        UsedApiById = [ordered]@{}
                    }
                }

                foreach ($api in @($test.usedApi)) {
                    $apiId = [string]$api.id
                    $testsByFullyQualifiedName[$fullyQualifiedName].UsedApiById[$apiId] = $api
                }
            }
        }

        $combinedTests = @(
            $testsByFullyQualifiedName.GetEnumerator() |
                Sort-Object Key |
                ForEach-Object {
                    $test = $_.Value
                    $usedApi = @(
                        $test.UsedApiById.GetEnumerator() |
                            Sort-Object Key |
                            ForEach-Object { $_.Value }
                    )

                    [ordered]@{
                        fullyQualifiedName = $test.FullyQualifiedName
                        displayName = $test.DisplayName
                        sourceFile = $test.SourceFile
                        line = $test.Line
                        usedApi = $usedApi
                    }
                }
        )

        $combinedUsage = [ordered]@{
            version = 1
            generatedAtUtc = (Get-Date).ToUniversalTime().ToString("o")
            testProject = (Join-UnderRoot -Root $repoRoot -RelativePath $suite.Project)
            targetAssemblyName = $null
            tests = $combinedTests
        }

        Write-JsonFile -Path $combinedUsagePath -Value $combinedUsage

        Invoke-Checked `
            -FilePath "dotnet" `
            -Arguments @(
                "run",
                "--project", $toolProject,
                "--",
                "plan",
                "--impact", $combinedImpactPath,
                "--usage", $combinedUsagePath,
                "--output", $planPath
            ) `
            -WorkingDirectory $repoRoot

        if ($fullRunReasonsBySuite[$suite.Name].Count -gt 0) {
            Write-Host "Running full $($suite.Name): $($fullRunReasonsBySuite[$suite.Name] -join '; ')"

            $testArguments = @("test", (Join-UnderRoot -Root $repoRoot -RelativePath $suite.Project))

            if ($NoBuild) {
                $testArguments += "--no-build"
            }

            Invoke-Checked -FilePath "dotnet" -Arguments $testArguments -WorkingDirectory $repoRoot

            continue
        }

        $runArguments = @(
            "run",
            "--project", $toolProject,
            "--",
            "run",
            "--test-project", (Join-UnderRoot -Root $repoRoot -RelativePath $suite.Project),
            "--impact", $combinedImpactPath,
            "--usage", $combinedUsagePath,
            "--plan-output", $planPath
        )

        if ($NoBuild) {
            $runArguments += "--no-build"
        }

        Invoke-Checked -FilePath "dotnet" -Arguments $runArguments -WorkingDirectory $repoRoot
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
