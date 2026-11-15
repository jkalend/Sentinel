#Requires -RunAsAdministrator
<#
.SYNOPSIS
Installs the Sentinel V0.1 agent under a dedicated non-admin reader account.

.DESCRIPTION
Copies the agent/ctl/rules/schemas layout, creates a local reader, restricts
the Sysmon Operational channel to Administrators/SYSTEM/reader, grants the
reader write access only to data/capture, and registers a startup scheduled
task that runs the agent as the reader.

Unless -NoRetentionTask is used, it also registers a daily SentinelRetention
task that runs `sentinelctl purge --apply` as SYSTEM with 30-day normalized
events, 7-day raw XML, and 90-day alerts/source-health/gaps. Use the day
parameters to change the policy; -NoTask skips both scheduled tasks.

The installer does not install Sysmon itself. Install and configure Sysmon
first; see docs/v01-windows-vm-runbook.md.

.PARAMETER ReaderPassword
SecureString for the local reader. The password is also stored by Task
Scheduler so the agent can start without an interactive logon.

.EXAMPLE
$pw = Read-Host -AsSecureString
.\install-sentinel-agent.ps1 -SourceRoot .\deploy -ReaderPassword $pw -Start
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$SourceRoot,
    [string]$InstallRoot = 'C:\Sentinel',
    [string]$ReaderUser = 'sentinel-reader',
    [Parameter(Mandatory)]
    [SecureString]$ReaderPassword,
    [string]$Channel = 'Microsoft-Windows-Sysmon/Operational',
    [string]$TaskName = 'SentinelAgent',
    [string]$RetentionTaskName = 'SentinelRetention',
    [int]$EventDays = 30,
    [int]$RawDays = 7,
    [int]$AlertDays = 90,
    [int]$HealthDays = 90,
    [int]$GapDays = 90,
    [switch]$Start,
    [switch]$NoTask,
    [switch]$NoRetentionTask
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Assert-Administrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw 'Run this installer from an elevated PowerShell session.'
    }
}

function Invoke-Icacls {
    param([string[]]$Arguments)
    $output = & icacls.exe @Arguments 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "icacls failed ($LASTEXITCODE): $($output -join ' ')"
    }
    return $output
}

function Set-ProtectedTree {
    param(
        [string]$Path,
        [string]$ReadGrant = '*S-1-5-32-545:(OI)(CI)RX'
    )
    Invoke-Icacls @($Path, '/inheritance:r', '/grant:r',
        '*S-1-5-32-544:(OI)(CI)F',
        '*S-1-5-18:(OI)(CI)F',
        $ReadGrant) | Out-Null
}

function Set-ReaderDataTree {
    param([string]$Path, [string]$Sid)
    Invoke-Icacls @($Path, '/inheritance:r', '/grant:r',
        '*S-1-5-32-544:(OI)(CI)F',
        '*S-1-5-18:(OI)(CI)F',
        ("*" + $Sid + ":(OI)(CI)M")) | Out-Null
}

function Grant-BatchLogonRight {
    param([string]$Sid)
    $inf = Join-Path $env:TEMP 'sentinel-user-rights.inf'
    $db = Join-Path $env:TEMP 'sentinel-user-rights.sdb'
    & secedit.exe /export /cfg $inf /areas USER_RIGHTS | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw "secedit export failed with $LASTEXITCODE"
    }
    $original = Join-Path $install 'user-rights-before.inf'
    Copy-Item -LiteralPath $inf -Destination $original -Force
    $lines = @(Get-Content -LiteralPath $inf)
    $index = -1
    for ($i = 0; $i -lt $lines.Count; $i++) {
        if ($lines[$i] -match '^SeBatchLogonRight\s*=') {
            $index = $i
            break
        }
    }
    if ($index -ge 0) {
        if ($lines[$index] -notmatch [regex]::Escape("*$Sid")) {
            $lines[$index] = $lines[$index].TrimEnd() + ",*$Sid"
        }
    }
    else {
        $section = -1
        for ($i = 0; $i -lt $lines.Count; $i++) {
            if ($lines[$i].Trim() -eq '[Privilege Rights]') {
                $section = $i
                break
            }
        }
        if ($section -lt 0) {
            throw 'Exported security policy has no [Privilege Rights] section.'
        }
        $lines = @($lines[0..$section]) + @("SeBatchLogonRight = *$Sid") + @($lines[($section + 1)..($lines.Count - 1)])
    }
    Set-Content -LiteralPath $inf -Value $lines -Encoding Unicode
    & secedit.exe /configure /db $db /cfg $inf /areas USER_RIGHTS /quiet
    if ($LASTEXITCODE -ne 0) {
        throw "secedit configure failed with $LASTEXITCODE"
    }
}

function Get-ChannelAcl {
    param([string]$Name)
    $line = & wevtutil.exe gl $Name 2>&1 | Select-String -Pattern '^\s*channelAccess:\s*' | Select-Object -First 1
    if (-not $line) {
        throw "Could not read channelAccess for $Name"
    }
    return ($line.ToString() -replace '^\s*channelAccess:\s*', '').Trim()
}

Assert-Administrator
$source = (Resolve-Path -LiteralPath $SourceRoot).Path
$install = [IO.Path]::GetFullPath($InstallRoot)

foreach ($name in @('agent', 'ctl', 'rules', 'schemas')) {
    $from = Join-Path $source $name
    if (-not (Test-Path -LiteralPath $from)) {
        throw "Source layout is missing $from"
    }
}

if (-not [string]::Equals($source.TrimEnd('\'), $install.TrimEnd('\'), [StringComparison]::OrdinalIgnoreCase)) {
    New-Item -ItemType Directory -Path $install -Force | Out-Null
    foreach ($name in @('agent', 'ctl', 'rules', 'schemas')) {
        Copy-Item -LiteralPath (Join-Path $source $name) -Destination $install -Recurse -Force
    }
}

$data = Join-Path $install 'data'
$capture = Join-Path $install 'capture'
New-Item -ItemType Directory -Path $data -Force | Out-Null
New-Item -ItemType Directory -Path $capture -Force | Out-Null

$user = Get-LocalUser -Name $ReaderUser -ErrorAction SilentlyContinue
if ($user) {
    Set-LocalUser -Name $ReaderUser -Password $ReaderPassword
}
else {
    New-LocalUser -Name $ReaderUser -Password $ReaderPassword `
        -FullName 'Sentinel event reader' `
        -Description 'Sentinel event channel reader.' `
        -PasswordNeverExpires -AccountNeverExpires | Out-Null
}
$reader = Get-LocalUser -Name $ReaderUser
$readerSid = $reader.SID.Value

foreach ($name in @('agent', 'ctl', 'rules', 'schemas')) {
    Set-ProtectedTree -Path (Join-Path $install $name)
}
Set-ProtectedTree -Path $install
Set-ReaderDataTree -Path $data -Sid $readerSid
Set-ReaderDataTree -Path $capture -Sid $readerSid
Grant-BatchLogonRight -Sid $readerSid

$aclRecord = Join-Path $install 'channel-acl-before.txt'
$oldAcl = Get-ChannelAcl -Name $Channel
Set-Content -LiteralPath $aclRecord -Value $oldAcl -Encoding ASCII
$newAcl = "O:BAG:SYD:(A;;0x1;;;S-1-5-32-544)(A;;0x1;;;S-1-5-18)(A;;0x1;;;$readerSid)"

try {
    & wevtutil.exe sl $Channel "/ca:$newAcl"
    if ($LASTEXITCODE -ne 0) {
        throw "wevtutil sl $Channel failed with $LASTEXITCODE"
    }

    $dbPath = Join-Path $data 'sentinel.db'

    if (-not $NoTask) {
        $agentExe = Join-Path $install 'agent\sentinel-agent.exe'
        $rulesPath = Join-Path $install 'rules'
        $agentLog = Join-Path $data 'agent.log'
        $commandLine = '/c ""' + $agentExe + '" run --db "' + $dbPath + '" --rules "' + $rulesPath + '" > "' + $agentLog + '" 2>&1"'
        $action = New-ScheduledTaskAction `
            -Execute (Join-Path $env:SystemRoot 'System32\cmd.exe') `
            -Argument $commandLine `
            -WorkingDirectory $install
        $trigger = New-ScheduledTaskTrigger -AtStartup
        $settings = New-ScheduledTaskSettingsSet `
            -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries `
            -ExecutionTimeLimit ([TimeSpan]::Zero) `
            -RestartCount 3 -RestartInterval (New-TimeSpan -Minutes 1) `
            -MultipleInstances IgnoreNew -StartWhenAvailable

        $bstr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($ReaderPassword)
        try {
            $plain = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($bstr)
            Register-ScheduledTask -TaskName $TaskName -Action $action -Trigger $trigger `
                -Settings $settings -User $ReaderUser -Password $plain `
                -RunLevel Limited -Force | Out-Null
        }
        finally {
            [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr)
        }

        if ($Start) {
            Start-ScheduledTask -TaskName $TaskName
            Start-Sleep -Seconds 3
            $info = Get-ScheduledTaskInfo -TaskName $TaskName -ErrorAction SilentlyContinue
            if ($info -and $info.LastTaskResult -ne 0 -and $info.LastTaskResult -ne 267009) {
                & schtasks.exe /Run /TN $TaskName | Out-Null
                Start-Sleep -Seconds 3
                $info = Get-ScheduledTaskInfo -TaskName $TaskName -ErrorAction SilentlyContinue
                if ($info -and $info.LastTaskResult -ne 0 -and $info.LastTaskResult -ne 267009) {
                    throw "Task $TaskName did not start (last result $($info.LastTaskResult)); see $agentLog"
                }
            }
        }
    }

    if (-not $NoTask -and -not $NoRetentionTask) {
        $ctlExe = Join-Path $install 'ctl\sentinelctl.exe'
        $retentionLog = Join-Path $data 'retention.log'
        $purgeArguments = '/c ""' + $ctlExe + '" purge --db "' + $dbPath + '"' +
            ' --event-days ' + $EventDays +
            ' --raw-days ' + $RawDays +
            ' --alert-days ' + $AlertDays +
            ' --health-days ' + $HealthDays +
            ' --gap-days ' + $GapDays +
            ' --keep-events 1000 --max-bytes 10737418240 --apply' +
            ' > "' + $retentionLog + '" 2>&1"'
        $retentionAction = New-ScheduledTaskAction `
            -Execute (Join-Path $env:SystemRoot 'System32\cmd.exe') `
            -Argument $purgeArguments `
            -WorkingDirectory $install
        $retentionTrigger = New-ScheduledTaskTrigger -Daily -At '03:15'
        $retentionSettings = New-ScheduledTaskSettingsSet `
            -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries `
            -ExecutionTimeLimit (New-TimeSpan -Hours 2) `
            -MultipleInstances IgnoreNew -StartWhenAvailable
        $retentionPrincipal = New-ScheduledTaskPrincipal `
            -UserId 'SYSTEM' -LogonType ServiceAccount -RunLevel Highest
        Register-ScheduledTask -TaskName $RetentionTaskName `
            -Action $retentionAction -Trigger $retentionTrigger `
            -Settings $retentionSettings -Principal $retentionPrincipal -Force | Out-Null
    }
}
catch {
    & wevtutil.exe sl $Channel "/ca:$oldAcl" | Out-Null
    throw
}

$state = Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue |
    Select-Object TaskName, State
$taskInfo = Get-ScheduledTaskInfo -TaskName $TaskName -ErrorAction SilentlyContinue |
    Select-Object LastRunTime, LastTaskResult, NextRunTime, NumberOfMissedRuns
[ordered]@{
    installed = $install
    reader = $ReaderUser
    reader_sid = $readerSid
    channel = $Channel
    channel_acl_before = $oldAcl
    channel_acl_after = Get-ChannelAcl -Name $Channel
    task = $state
    task_info = $taskInfo
    retention_task = if ($NoTask -or $NoRetentionTask) {
        $null
    }
    else {
        Get-ScheduledTask -TaskName $RetentionTaskName -ErrorAction SilentlyContinue |
            Select-Object TaskName, State
    }
    agent_log = Join-Path $data 'agent.log'
    retention_log = Join-Path $data 'retention.log'
    data = $data
    capture = $capture
    acl_record = $aclRecord
} | ConvertTo-Json -Depth 4
