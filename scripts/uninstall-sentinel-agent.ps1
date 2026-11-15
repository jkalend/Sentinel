#Requires -RunAsAdministrator
<#
.SYNOPSIS
Removes the Sentinel V0.1 agent task, channel ACL, and reader account.

.DESCRIPTION
Stops and unregisters the task, restores the Sysmon Operational channel ACL
saved by the installer, optionally removes the local reader, and optionally
removes retained data or the entire install directory.

Data is preserved by default. Use -RemoveData to delete the database/capture
and -RemoveInstall to delete the install directory (which also removes data).

.EXAMPLE
.\uninstall-sentinel-agent.ps1
#>
[CmdletBinding()]
param(
    [string]$InstallRoot = 'C:\Sentinel',
    [string]$ReaderUser = 'sentinel-reader',
    [string]$Channel = 'Microsoft-Windows-Sysmon/Operational',
    [string]$TaskName = 'SentinelAgent',
    [string]$RetentionTaskName = 'SentinelRetention',
    [switch]$RemoveReader,
    [switch]$RemoveData,
    [switch]$RemoveInstall
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Assert-Administrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw 'Run this uninstaller from an elevated PowerShell session.'
    }
}

function Get-SavedChannelAcl {
    param([string]$Path)
    if (-not (Test-Path -LiteralPath $Path)) {
        return $null
    }
    $line = Get-Content -LiteralPath $Path |
        Select-String -Pattern '^\s*channelAccess:\s*' |
        Select-Object -First 1
    if (-not $line) {
        $line = Get-Content -LiteralPath $Path | Select-Object -First 1
    }
    if (-not $line) {
        return $null
    }
    return ($line.ToString() -replace '^\s*channelAccess:\s*', '').Trim()
}

Assert-Administrator
$install = [IO.Path]::GetFullPath($InstallRoot)
$aclRecord = Join-Path $install 'channel-acl-before.txt'

$task = Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue
if ($task) {
    Stop-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue
    Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false
}

$retentionTask = Get-ScheduledTask -TaskName $RetentionTaskName -ErrorAction SilentlyContinue
if ($retentionTask) {
    Stop-ScheduledTask -TaskName $RetentionTaskName -ErrorAction SilentlyContinue
    Unregister-ScheduledTask -TaskName $RetentionTaskName -Confirm:$false
}

$oldAcl = Get-SavedChannelAcl -Path $aclRecord
if ($oldAcl) {
    & wevtutil.exe sl $Channel "/ca:$oldAcl"
    if ($LASTEXITCODE -ne 0) {
        throw "Failed to restore channel ACL for $Channel (exit $LASTEXITCODE)"
    }
}
else {
    Write-Warning "No saved channel ACL at $aclRecord; leaving the current $Channel ACL unchanged."
}

$rightsRestored = $false
$rightsBefore = Join-Path $install 'user-rights-before.inf'
if (Test-Path -LiteralPath $rightsBefore) {
    $rightsDb = Join-Path $env:TEMP 'sentinel-uninstall-rights.sdb'
    & secedit.exe /configure /db $rightsDb /cfg $rightsBefore /areas USER_RIGHTS /quiet
    $rightsRestored = ($LASTEXITCODE -eq 0)
}

if ($RemoveReader) {
    $reader = Get-LocalUser -Name $ReaderUser -ErrorAction SilentlyContinue
    if ($reader) {
        Remove-LocalUser -Name $ReaderUser
    }
}

if ($RemoveData) {
    foreach ($name in @('data', 'capture')) {
        $path = Join-Path $install $name
        if (Test-Path -LiteralPath $path) {
            Remove-Item -LiteralPath $path -Recurse -Force
        }
    }
}

if ($RemoveInstall -and (Test-Path -LiteralPath $install)) {
    Remove-Item -LiteralPath $install -Recurse -Force
}

[ordered]@{
    task_removed = -not [bool](Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue)
    retention_task_removed = -not [bool](Get-ScheduledTask -TaskName $RetentionTaskName -ErrorAction SilentlyContinue)
    channel_acl_restored = [bool]$oldAcl
    user_rights_restored = $rightsRestored
    reader_removed = if ($RemoveReader) { -not [bool](Get-LocalUser -Name $ReaderUser -ErrorAction SilentlyContinue) } else { $false }
    data_removed = [bool]$RemoveData
    install_removed = if ($RemoveInstall) { -not (Test-Path -LiteralPath $install) } else { $false }
} | ConvertTo-Json -Depth 3
