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
.\scripts\build-release.ps1 -Version 0.4.3
#>
[CmdletBinding()]
param(
    [string]$Version,
    [string]$OutputDir,
    [string]$Runtime = 'win-x64',
    [switch]$NoZip
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repo = Split-Path -Parent $PSScriptRoot

if (-not $Version) {
    [xml]$props = Get-Content -LiteralPath (Join-Path $repo 'Directory.Build.props')
    $Version = @($props.Project.PropertyGroup.Version | Where-Object { $_ }) | Select-Object -First 1
    if (-not $Version) {
        throw 'Directory.Build.props has no Version; pass -Version.'
    }
}

if (-not $OutputDir) {
    $OutputDir = Join-Path $repo "publish\release-$Version"
}

$output = [IO.Path]::GetFullPath($OutputDir)
if (Test-Path -LiteralPath $output) {
    Remove-Item -LiteralPath $output -Recurse -Force
}

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
    & dotnet publish $source -c Release -r $Runtime --self-contained true `
        -p:PublishSingleFile=true -p:Version=$Version -o $Destination --nologo
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed for $Project (exit $LASTEXITCODE)"
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
    & dotnet pack (Join-Path $repo $project) -c Release -p:Version=$Version -o $packages --nologo
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet pack failed for $project (exit $LASTEXITCODE)"
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

$sdk = (& dotnet --version).Trim()
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

Write-Output "release: $output"
Write-Output "version: $Version"
Write-Output "commit: $commit$(if ($dirty) { ' (dirty)' } else { '' })"
Write-Output "zip: $(if ($zipName) { $zipName } else { 'none' })"
Write-Output "checksums: $checksumPath"
