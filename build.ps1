[CmdletBinding()]
param(
    [string] $MyPowerToolsRepoRoot
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repositoryRoot = [System.IO.Path]::GetFullPath($PSScriptRoot)
$abstractionsProject = 'src\MyPowerTools.Abstractions\MyPowerTools.Abstractions.csproj'
$protocolProject = 'src\MyPowerTools.Protocol\MyPowerTools.Protocol.csproj'

function Test-MyPowerToolsRepository {
    param(
        [Parameter(Mandatory)]
        [string] $Path
    )

    return (
        (Test-Path -LiteralPath (Join-Path $Path $abstractionsProject) -PathType Leaf) -and
        (Test-Path -LiteralPath (Join-Path $Path $protocolProject) -PathType Leaf)
    )
}

function Resolve-MyPowerToolsRepository {
    param(
        [string] $ExplicitPath
    )

    if (-not [string]::IsNullOrWhiteSpace($ExplicitPath)) {
        $resolvedExplicitPath = [System.IO.Path]::GetFullPath($ExplicitPath)
        if (-not (Test-MyPowerToolsRepository -Path $resolvedExplicitPath)) {
            throw "MyPowerToolsRepoRoot '$resolvedExplicitPath' is invalid. Expected '$abstractionsProject' and '$protocolProject'."
        }

        return $resolvedExplicitPath
    }

    $repositoryDirectory = [System.IO.DirectoryInfo]::new($repositoryRoot)
    $candidatePaths = [System.Collections.Generic.List[string]]::new()

    if (-not [string]::IsNullOrWhiteSpace($env:MYPOWERTOOLS_REPO_ROOT)) {
        $candidatePaths.Add([System.IO.Path]::GetFullPath($env:MYPOWERTOOLS_REPO_ROOT))
    }

    if ($null -ne $repositoryDirectory.Parent -and $null -ne $repositoryDirectory.Parent.Parent) {
        $candidatePaths.Add($repositoryDirectory.Parent.Parent.FullName)
    }

    if ($null -ne $repositoryDirectory.Parent) {
        $candidatePaths.Add((Join-Path $repositoryDirectory.Parent.FullName 'MyPowerTools'))
    }

    foreach ($candidatePath in ($candidatePaths | Select-Object -Unique)) {
        $normalizedCandidate = [System.IO.Path]::GetFullPath($candidatePath)
        if (Test-MyPowerToolsRepository -Path $normalizedCandidate) {
            return $normalizedCandidate
        }
    }

    throw 'Unable to locate the MyPowerTools repository. Pass -MyPowerToolsRepoRoot with its absolute path.'
}

$resolvedMyPowerToolsRoot = Resolve-MyPowerToolsRepository -ExplicitPath $MyPowerToolsRepoRoot
$projectPath = Join-Path $repositoryRoot 'current-integration\src\AdbForwarder.MyPowerTools\AdbForwarder.MyPowerTools.csproj'
$modulePackageRoot = Join-Path $repositoryRoot 'current-integration\modules\adb-forwarder'
$artifactsRoot = Join-Path $repositoryRoot 'artifacts'
$artifactPackage = Join-Path $artifactsRoot 'package'

$dotnetCommand = Get-Command 'dotnet' -CommandType Application -ErrorAction Stop
$dotnetArguments = @(
    'build'
    $projectPath
    '--configuration'
    'Release'
    '--nologo'
    "-p:MyPowerToolsRepoRoot=$resolvedMyPowerToolsRoot"
)

& $dotnetCommand.Source @dotnetArguments
$dotnetExitCode = $LASTEXITCODE
if ($dotnetExitCode -ne 0) {
    throw "dotnet build failed with exit code $dotnetExitCode."
}

if (-not (Test-Path -LiteralPath $modulePackageRoot -PathType Container)) {
    throw "Expected module package '$modulePackageRoot' was not produced."
}

if (Test-Path -LiteralPath $artifactPackage) {
    $normalizedArtifactPackage = [System.IO.Path]::GetFullPath($artifactPackage)
    $normalizedArtifactsRoot = [System.IO.Path]::GetFullPath($artifactsRoot).TrimEnd([System.IO.Path]::DirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
    if (-not $normalizedArtifactPackage.StartsWith($normalizedArtifactsRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to remove artifact path outside '$artifactsRoot'."
    }

    Remove-Item -LiteralPath $artifactPackage -Recurse -Force
}

New-Item -ItemType Directory -Path $artifactsRoot -Force | Out-Null
Copy-Item -LiteralPath $modulePackageRoot -Destination $artifactPackage -Recurse -Force

$expectedAssembly = Join-Path $artifactPackage 'AdbForwarder.MyPowerTools.dll'
if (-not (Test-Path -LiteralPath $expectedAssembly -PathType Leaf)) {
    throw "Expected adapter assembly '$expectedAssembly' is missing from the staged package."
}

Write-Output "Release package staged at $artifactPackage"
