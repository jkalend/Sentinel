#Requires -Version 5.1
<#
.SYNOPSIS
Builds the versioned Sentinel release bundle for a Windows x64 host.

.DESCRIPTION
Publishes sentinel-agent and sentinelctl as self-contained single-file
executables, stages the deploy/ layout consumed by install-sentinel-agent.ps1,
and writes SHA256SUMS.txt plus release.json. No signing is performed.

.EXAMPLE
.\scripts\build-release.ps1
.\scripts\build-release.ps1 -Version 0.9.1
#>
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [string]$Version,
    [string]$OutputDir,
    [string]$Runtime = 'win-x64',
    [switch]$NoZip
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repo = Split-Path -Parent $PSScriptRoot
$repoFull = [IO.Path]::GetFullPath($repo).TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)

[xml]$props = Get-Content -LiteralPath (Join-Path $repo 'Directory.Build.props')
$declaredVersion = @($props.Project.PropertyGroup.Version | Where-Object { $_ }) | Select-Object -First 1
if (-not $declaredVersion) {
    throw 'Directory.Build.props has no Version.'
}
$declaredVersion = $declaredVersion.Trim()

if ($Version) {
    $Version = $Version.Trim()
}
else {
    $Version = $declaredVersion
}

if ($Version -ne $declaredVersion) {
    throw "Version '$Version' does not match Directory.Build.props version '$declaredVersion'. Update the shared version before tagging or building."
}
if ($Version -notmatch '^[0-9]+\.[0-9]+\.[0-9]+([-+][0-9A-Za-z.-]+)?$') {
    throw "Version '$Version' is not a supported release version."
}

$defaultOutput = [IO.Path]::GetFullPath((Join-Path $repo "publish\release-$Version")).TrimEnd(
    [IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
if (-not $OutputDir) {
    $OutputDir = $defaultOutput
}

$outputFull = [IO.Path]::GetFullPath($OutputDir)
$output = $outputFull.TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
if ([string]::IsNullOrWhiteSpace($output)) {
    throw 'OutputDir resolves to an empty path.'
}
$buildMarker = Join-Path $output '.sentinel-release-build'
$root = [IO.Path]::GetPathRoot($outputFull).TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
if ([string]::Equals($output, $root, [StringComparison]::OrdinalIgnoreCase)) {
    throw "refusing to use a filesystem root as -OutputDir: $output"
}
if ([string]::Equals($output, $repoFull, [StringComparison]::OrdinalIgnoreCase) -or
    $repoFull.StartsWith($output + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or
    $repoFull.StartsWith($output + [IO.Path]::AltDirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw "refusing -OutputDir '$output': it contains the repository checkout."
}
if (Test-Path -LiteralPath $output -PathType Leaf) {
    throw "-OutputDir '$output' points at a file."
}

$expectedSdk = (Get-Content -LiteralPath (Join-Path $repo 'global.json') | ConvertFrom-Json).sdk.version
$sdkExit = 1
Push-Location -LiteralPath $repo
try {
    $actualSdk = (& dotnet --version).Trim()
    $sdkExit = $LASTEXITCODE
}
finally {
    Pop-Location
}
if ($sdkExit -ne 0 -or $actualSdk -ne $expectedSdk) {
    throw "dotnet resolves SDK '$actualSdk' but global.json requires '$expectedSdk'."
}

if ($WhatIfPreference) {
    Write-Output "would build $Version into $output"
    return
}

if (Test-Path -LiteralPath $output) {
    $hasChildren = [bool](Get-ChildItem -LiteralPath $output -Force -ErrorAction Stop | Select-Object -First 1)
    $isReleaseDir = Test-Path -LiteralPath (Join-Path $output 'release.json')
    $isBuildDir = Test-Path -LiteralPath $buildMarker
    if ($hasChildren -and -not ($isReleaseDir -or $isBuildDir)) {
        throw "refusing to delete '$output': it is not empty and has no release marker. Use a new or empty directory, or remove it manually."
    }
    if ($PSCmdlet.ShouldProcess($output, 'Remove existing release directory')) {
        Remove-Item -LiteralPath $output -Recurse -Force
    }
}

New-Item -ItemType Directory -Path $output -Force | Out-Null
Set-Content -LiteralPath $buildMarker -Value $Version -Encoding ASCII

$deploy = Join-Path $output 'deploy'
New-Item -ItemType Directory -Path $deploy -Force | Out-Null

function Get-Sha256 {
    param([string]$Path)
    $stream = [IO.File]::OpenRead($Path)
    try {
        $sha = [Security.Cryptography.SHA256]::Create()
        try {
            return ([BitConverter]::ToString($sha.ComputeHash($stream)) -replace '-', '').ToLowerInvariant()
        }
        finally {
            $sha.Dispose()
        }
    }
    finally {
        $stream.Dispose()
    }
}

function Publish-Project {
    param([string]$Project, [string]$Destination)
    $source = Join-Path $repo $Project
    $exit = 1
    Push-Location -LiteralPath $repo
    try {
        & dotnet publish $source -c Release -r $Runtime --self-contained true `
            -p:PublishSingleFile=true -p:Version=$Version -o $Destination --nologo
        $exit = $LASTEXITCODE
    }
    finally {
        Pop-Location
    }
    if ($exit -ne 0) {
        throw "dotnet publish failed for $Project (exit $exit)"
    }
}

Publish-Project -Project 'src\sentinel-agent' -Destination (Join-Path $deploy 'agent')
Publish-Project -Project 'src\sentinelctl' -Destination (Join-Path $deploy 'ctl')

Get-ChildItem -Path $deploy -Recurse -File -Filter '*.pdb' | Remove-Item -Force
Copy-Item -LiteralPath (Join-Path $repo 'rules') -Destination (Join-Path $deploy 'rules') -Recurse
Copy-Item -LiteralPath (Join-Path $repo 'schemas') -Destination (Join-Path $deploy 'schemas') -Recurse

$packages = Join-Path $output 'packages'
New-Item -ItemType Directory -Path $packages -Force | Out-Null
foreach ($project in @('src\Sentinel.Core\Sentinel.Core.csproj', 'src\Sentinel.Windows\Sentinel.Windows.csproj')) {
    $exit = 1
    Push-Location -LiteralPath $repo
    try {
        & dotnet pack (Join-Path $repo $project) -c Release -p:Version=$Version -o $packages --nologo
        $exit = $LASTEXITCODE
    }
    finally {
        Pop-Location
    }
    if ($exit -ne 0) {
        throw "dotnet pack failed for $project (exit $exit)"
    }
}

$commit = 'unknown'
$dirty = $true
try {
    $commit = (& git -C $repo rev-parse HEAD 2>$null | Select-Object -First 1).Trim()
    $dirty = [bool](& git -C $repo status --porcelain 2>$null)
}
catch {
    $dirty = $true
}

$sdk = $actualSdk
$builtAt = (Get-Date).ToUniversalTime().ToString('o')

$zipName = $null
$zipHash = $null
if (-not $NoZip) {
    $zipName = "sentinel-$Version-$Runtime.zip"
    $zipPath = Join-Path $output $zipName
    Compress-Archive -Path (Join-Path $deploy '*') -DestinationPath $zipPath -CompressionLevel Optimal
    $zipHash = Get-Sha256 -Path $zipPath
}

$checksums = @()
$bundleFiles = @()
$bundleFiles += Get-ChildItem -Path $deploy -Recurse -File
$bundleFiles += Get-ChildItem -Path $packages -File
foreach ($file in $bundleFiles | Sort-Object FullName) {
    $relative = $file.FullName.Substring($output.Length + 1).Replace('\', '/')
    $hash = Get-Sha256 -Path $file.FullName
    $checksums += "$hash  $relative"
}

if ($zipName) {
    $checksums += "$zipHash  $zipName"
}

$checksumPath = Join-Path $output 'SHA256SUMS.txt'
[IO.File]::WriteAllText(
    $checksumPath,
    ($checksums -join "`n") + "`n",
    [Text.Encoding]::ASCII)

$releaseJson = [ordered]@{
    version = $Version
    runtime = $Runtime
    commit = $commit
    dirty = $dirty
    sdk = $sdk
    built_at = $builtAt
    zip = $zipName
    zip_sha256 = $zipHash
    packages = @(Get-ChildItem -Path $packages -File | Sort-Object Name | Select-Object -ExpandProperty Name)
} | ConvertTo-Json
[IO.File]::WriteAllText(
    (Join-Path $output 'release.json'),
    $releaseJson,
    (New-Object System.Text.UTF8Encoding($false)))
Remove-Item -LiteralPath $buildMarker -Force -ErrorAction SilentlyContinue

Write-Output "release: $output"
Write-Output "version: $Version"
Write-Output "commit: $commit$(if ($dirty) { ' (dirty)' } else { '' })"
Write-Output "zip: $(if ($zipName) { $zipName } else { 'none' })"
Write-Output "checksums: $checksumPath"
