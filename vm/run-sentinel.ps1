#requires -Version 5.1
<#
.SYNOPSIS
  Disposable Sentinel V0.1 live-source validation.

.DESCRIPTION
  Runs from the SENTINEL_TEST read-only media on first logon. Enables built-in
  Sysmon (rebooting once if required), applies the reviewed profile, generates
  controlled activity, runs the live reader, verifies/replays the store, captures
  raw XML, copies results to the SENTINELOUT FAT volume, writes a serial
  result, and shuts down. No manual OOBE or click-through is accepted.
#>
param([switch]$Continue, [switch]$Resume, [switch]$NoReboot)

$ErrorActionPreference = 'Stop'
$root = 'C:\sentinel'
$logPath = Join-Path $root 'run.log'
$resultPath = Join-Path $root 'result.json'
$stagePath = Join-Path $root 'stage.txt'
New-Item -ItemType Directory -Force -Path $root | Out-Null

function Write-Log {
    param([string]$Message)
    $line = '[{0:O}] {1}' -f [DateTimeOffset]::UtcNow, $Message
    Add-Content -LiteralPath $logPath -Value $line
    Write-Host $line
}

function Write-Serial {
    param([string]$Message)
    try {
        $port = New-Object System.IO.Ports.SerialPort COM1,115200,None,8,one
        $port.Open()
        $port.WriteLine($Message)
        $port.Close()
    }
    catch {
        Write-Log "serial write failed: $($_.Exception.Message)"
    }
}

function Limit-Text {
    param([string]$Text, [int]$Max = 8000)
    if ($null -eq $Text) { return '' }
    if ($Text.Length -le $Max) { return $Text }
    return $Text.Substring(0, $Max) + '...[truncated]'
}

function Get-StatusValue {
    param([string]$Text, [string]$Name)
    if ($Text -match "(?m)^$([regex]::Escape($Name)):\s*(.+)$") {
        return $Matches[1].Trim()
    }
    return ''
}

function Get-StatusNumber {
    param([string]$Text, [string]$Name)
    $value = Get-StatusValue -Text $Text -Name $Name
    $parsed = 0
    if ([int]::TryParse($value, [ref]$parsed)) { return $parsed }
    return -1
}

function Invoke-Captured {
    param(
        [Parameter(Mandatory = $true)][string]$FilePath,
        [Parameter(Mandatory = $true)][string[]]$Arguments
    )

    # Windows PowerShell 5.1 turns a native stderr line into a terminating
    # NativeCommandError when ErrorActionPreference is Stop. Keep capturing
    # output and the exit code instead of losing the diagnostics.
    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $output = & $FilePath @Arguments 2>&1
        $code = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $previous
    }

    if ($null -eq $code) { $code = -1 }
    return [pscustomobject]@{
        ExitCode = [int]$code
        Output = (($output | ForEach-Object { $_.ToString() }) -join "`n")
    }
}

function Get-TestVolume {
    Get-Volume | Where-Object { $_.FileSystemLabel -eq 'SENTINEL_TEST' } | Select-Object -First 1
}

function Get-OutputVolume {
    Update-HostStorageCache -ErrorAction SilentlyContinue
    foreach ($disk in @(Get-Disk -ErrorAction SilentlyContinue)) {
        if ($disk.IsOffline) {
            Set-Disk -Number $disk.Number -IsOffline $false -ErrorAction SilentlyContinue
        }
        if ($disk.IsReadOnly) {
            Set-Disk -Number $disk.Number -IsReadOnly $false -ErrorAction SilentlyContinue
        }
    }

    $volume = Get-Volume | Where-Object { $_.FileSystemLabel -eq 'SENTINELOUT' } | Select-Object -First 1
    if ($volume -and [string]::IsNullOrWhiteSpace($volume.DriveLetter)) {
        try {
            $partition = Get-Partition -Volume $volume -ErrorAction Stop | Select-Object -First 1
            Add-PartitionAccessPath -DiskNumber $partition.DiskNumber -PartitionNumber $partition.PartitionNumber -AssignDriveLetter -ErrorAction Stop
            Start-Sleep -Seconds 2
            $volume = Get-Volume | Where-Object { $_.FileSystemLabel -eq 'SENTINELOUT' } | Select-Object -First 1
        }
        catch {
            Write-Log "output volume mount failed: $($_.Exception.Message)"
        }
    }

    if (-not $volume) {
        $partition = Get-Partition -ErrorAction SilentlyContinue |
            Where-Object { $_.DiskNumber -ne 0 -and $_.Size -gt 1GB -and -not $_.DriveLetter } |
            Select-Object -First 1
        if ($partition) {
            try {
                Add-PartitionAccessPath -DiskNumber $partition.DiskNumber -PartitionNumber $partition.PartitionNumber -AssignDriveLetter -ErrorAction Stop
                Start-Sleep -Seconds 2
                $volume = Get-Volume | Where-Object { $_.FileSystemLabel -eq 'SENTINELOUT' } | Select-Object -First 1
            }
            catch {
                Write-Log "fallback output volume mount failed: $($_.Exception.Message)"
            }
        }
    }

    return $volume
}

function Copy-Media {
    $test = Get-TestVolume
    if (-not $test -or [string]::IsNullOrWhiteSpace($test.DriveLetter)) {
        throw 'SENTINEL_TEST volume missing or has no drive letter'
    }

    $media = "$($test.DriveLetter):\"
    foreach ($name in @('agent', 'ctl', 'rules', 'schemas', 'scripts')) {
        $source = Join-Path $media $name
        if (Test-Path -LiteralPath $source) {
            $destination = Join-Path $root $name
            if (Test-Path -LiteralPath $destination) {
                Remove-Item -LiteralPath $destination -Recurse -Force
            }
            Copy-Item -LiteralPath $source -Destination $destination -Recurse -Force
        }
    }

    foreach ($name in @('run-sentinel.ps1', 'sysmon-sentinel-v01.xml')) {
        Copy-Item -LiteralPath (Join-Path $media $name) -Destination (Join-Path $root $name) -Force
    }
}

function Finish {
    param([System.Collections.IDictionary]$Result)

    try {
        $json = $Result | ConvertTo-Json -Depth 10
        Set-Content -LiteralPath $resultPath -Value $json -Encoding UTF8

        $output = Get-OutputVolume
        if ($output -and -not [string]::IsNullOrWhiteSpace($output.DriveLetter)) {
            $Result['output_volume'] = "$($output.DriveLetter):"
            $destination = "$($output.DriveLetter):\"
            foreach ($name in @('data', 'capture')) {
                $source = Join-Path $root $name
                if (Test-Path -LiteralPath $source) {
                    Copy-Item -LiteralPath $source -Destination $destination -Recurse -Force
                }
            }

            foreach ($name in @('result.json', 'main-result.json', 'reboot-state.json', 'agent-run.txt', 'agent-run-first.txt', 'verify.txt', 'timeline.txt', 'replay.txt', 'probes.json', 'run.log', 'probes.out.txt', 'install.txt', 'uninstall.txt')) {
                $source = Join-Path $root $name
                if (Test-Path -LiteralPath $source) {
                    Copy-Item -LiteralPath $source -Destination $destination -Force
                }
            }
        }
        else {
            Write-Log 'SENTINELOUT volume missing; results remain only in the guest'
        }

        Write-Serial 'SENTINEL_RESULT_BEGIN'
        Write-Serial $json
        Write-Serial 'SENTINEL_RESULT_END'
    }
    catch {
        Write-Log "finish failed: $($_.Exception.Message)"
    }

    Start-Sleep -Seconds 2
    shutdown.exe /s /t 5 /f
    exit 0
}

function Invoke-Resume {
    $result = [ordered]@{
        ok = $false
        stage = 'resume'
        timestamp_utc = [DateTimeOffset]::UtcNow.ToString('O')
        computer = $env:COMPUTERNAME
        errors = @()
    }

    try {
        $statePath = Join-Path $root 'reboot-state.json'
        if (-not (Test-Path -LiteralPath $statePath)) {
            throw "reboot state missing: $statePath"
        }
        $state = Get-Content -LiteralPath $statePath -Raw | ConvertFrom-Json
        $installedRoot = [string]$state.installed_root
        $installedDb = Join-Path $installedRoot 'data\sentinel.db'
        $installedCtl = Join-Path $installedRoot 'ctl\sentinelctl.exe'
        $installedSchema = Join-Path $installedRoot 'schemas\ocsf-1.9.0'
        $preEvents = [int]$state.pre_events
        $result.pre_events = $preEvents
        $result.pre_bookmark = [string]$state.pre_bookmark
        $result.pre_task_state = [string]$state.pre_task_state
        $result.installer_before_reboot = [bool]$state.installer_ok

        $taskState = 'missing'
        $events = -1
        $statusText = ''
        $probeSent = $false
        for ($attempt = 0; $attempt -lt 60; $attempt++) {
            $task = Get-ScheduledTask -TaskName 'SentinelAgent' -ErrorAction SilentlyContinue
            $taskState = if ($task) { $task.State.ToString() } else { 'missing' }
            if (Test-Path -LiteralPath $installedDb) {
                $statusResult = Invoke-Captured -FilePath $installedCtl -Arguments @('status', '--db', $installedDb)
                $statusText = $statusResult.Output
                $events = Get-StatusNumber -Text $statusText -Name 'events'
            }
            if ($taskState -eq 'Running' -and $events -gt $preEvents) {
                break
            }
            if (-not $probeSent -and $attempt -ge 5) {
                Start-Process -FilePath 'cmd.exe' -ArgumentList @('/c', 'echo reboot-resume-probe') -Wait | Out-Null
                $probeSent = $true
            }
            Start-Sleep -Seconds 2
        }

        $result.resume_task_state = $taskState
        $result.resume_events = $events
        $result.resume_delta = $events - $preEvents
        $result.resume_bookmark = Get-StatusValue -Text $statusText -Name 'bookmark'
        $result.resume_status = Limit-Text $statusText
        $result.main_ok = [bool]$state.main_ok
        $taskDetails = Get-ScheduledTaskInfo -TaskName 'SentinelAgent' -ErrorAction SilentlyContinue
        $result.resume_task_last_result = if ($taskDetails) { $taskDetails.LastTaskResult } else { $null }

        $preBookmarkText = [string]$state.pre_bookmark
        $postBookmarkText = [string]$result.resume_bookmark
        $preIncarnation = if ($preBookmarkText -match 'incarnation=(\S+)') { $Matches[1] } else { '' }
        $postIncarnation = if ($postBookmarkText -match 'incarnation=(\S+)') { $Matches[1] } else { '' }
        $preRecord = if ($preBookmarkText -match 'record=(\d+)') { [long]$Matches[1] } else { -1 }
        $postRecord = if ($postBookmarkText -match 'record=(\d+)') { [long]$Matches[1] } else { -1 }
        $sameIncarnation = ($preIncarnation -ne '' -and $preIncarnation -eq $postIncarnation)
        $bookmarkAdvanced = ($preRecord -ge 0 -and $postRecord -gt $preRecord)
        $result.resume_same_incarnation = $sameIncarnation
        $result.resume_bookmark_advanced = $bookmarkAdvanced

        Stop-ScheduledTask -TaskName 'SentinelAgent' -ErrorAction SilentlyContinue
        Start-Sleep -Seconds 2

        if (Test-Path -LiteralPath $installedDb) {
            New-Item -ItemType Directory -Path (Join-Path $root 'data') -Force | Out-Null
            Copy-Item -LiteralPath $installedDb -Destination (Join-Path $root 'data\installed.db') -Force
        }

        $verifyResult = Invoke-Captured -FilePath $installedCtl -Arguments @('verify', '--db', $installedDb, '--schema', $installedSchema)
        $verifyExit = $verifyResult.ExitCode
        $result.resume_verify_exit = $verifyExit
        $result.resume_verify = Limit-Text $verifyResult.Output

        $uninstallScript = Join-Path $root 'scripts\uninstall-sentinel-agent.ps1'
        $uninstallResult = Invoke-Captured -FilePath 'powershell.exe' -Arguments @(
            '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $uninstallScript,
            '-InstallRoot', $installedRoot, '-ReaderUser', 'sentinel-reader',
            '-TaskName', 'SentinelAgent', '-RemoveReader', '-RemoveData', '-RemoveInstall'
        )
        $uninstallExit = $uninstallResult.ExitCode
        $uninstallOutput = $uninstallResult.Output
        $uninstallOutput | Set-Content -LiteralPath (Join-Path $root 'uninstall.txt') -Encoding UTF8
        $result.resume_uninstall_exit = $uninstallExit
        $result.resume_uninstall = Limit-Text $uninstallOutput

        $uninstallJson = $null
        try { $uninstallJson = $uninstallOutput | ConvertFrom-Json }
        catch { $result.resume_uninstall_parse_error = $_.Exception.Message }
        $channelAfter = (& wevtutil.exe gl 'Microsoft-Windows-Sysmon/Operational' 2>&1 | Select-String -Pattern '^\s*channelAccess:\s*' | Select-Object -First 1)
        $channelAfterText = if ($channelAfter) { $channelAfter.ToString() } else { '' }
        $channelAfterAcl = ($channelAfterText -replace '^\s*channelAccess:\s*', '').Trim()
        $oldAcl = [string]$state.channel_acl_before
        $uninstallRestored = ([bool]($uninstallJson -and $uninstallJson.channel_acl_restored) -and $oldAcl -ne '' -and $channelAfterAcl -eq $oldAcl)
        $userRightsRestored = [bool]($uninstallJson -and $uninstallJson.user_rights_restored)
        $agentTaskRemoved = -not [bool](Get-ScheduledTask -TaskName 'SentinelAgent' -ErrorAction SilentlyContinue)
        $readerRemoved = -not [bool](Get-LocalUser -Name 'sentinel-reader' -ErrorAction SilentlyContinue)
        $installRemoved = -not (Test-Path -LiteralPath $installedRoot)
        $result.resume_uninstall_restored = $uninstallRestored
        $result.resume_user_rights_restored = $userRightsRestored
        $result.resume_agent_task_removed = $agentTaskRemoved
        $result.resume_reader_removed = $readerRemoved
        $result.resume_install_removed = $installRemoved

        $result.resume_ok = (
            $events -gt $preEvents -and
            $sameIncarnation -and
            $bookmarkAdvanced -and
            $taskState -eq 'Running' -and
            $verifyExit -eq 0 -and
            [bool]$state.main_ok -and
            [bool]$state.installer_ok -and
            $uninstallExit -eq 0 -and
            $uninstallRestored -and
            $userRightsRestored -and
            $agentTaskRemoved -and
            $readerRemoved -and
            $installRemoved
        )
        $result.ok = $result.resume_ok
    }
    catch {
        $result.ok = $false
        $result.error = $_.Exception.Message
        $result.stack = $_.ScriptStackTrace
        Write-Log "resume failed: $($_.Exception.Message)"
    }
    finally {
        Unregister-ScheduledTask -TaskName 'SentinelResume' -Confirm:$false -ErrorAction SilentlyContinue
        $result.resume_task_removed = -not [bool](Get-ScheduledTask -TaskName 'SentinelResume' -ErrorAction SilentlyContinue)
        Finish $result
    }
}

$result = [ordered]@{
    ok = $false
    stage = 'start'
    timestamp_utc = [DateTimeOffset]::UtcNow.ToString('O')
    computer = $env:COMPUTERNAME
    errors = @()
}
$rebootRequested = $false

if ($Resume) {
    Invoke-Resume
    return
}

try {
    Copy-Media
    foreach ($name in @('data', 'capture')) {
        $path = Join-Path $root $name
        if (Test-Path -LiteralPath $path) {
            Remove-Item -LiteralPath $path -Recurse -Force
        }
    }
    $result.os = Get-CimInstance Win32_OperatingSystem | Select-Object Caption, Version, BuildNumber
    Write-Log 'media copied; prior data cleared'

    $stage = if (Test-Path -LiteralPath $stagePath) {
        (Get-Content -LiteralPath $stagePath -Raw).Trim()
    }
    else {
        'enable'
    }
    $result.stage = $stage

    if (-not $Continue -and $stage -eq 'enable') {
        $feature = Get-WindowsOptionalFeature -Online -FeatureName Sysmon
        $result.sysmon_feature_before = $feature.State
        Write-Log "sysmon feature state: $($feature.State)"

        if ($feature.State -ne 'Enabled') {
            $enabled = Enable-WindowsOptionalFeature -Online -FeatureName Sysmon -NoRestart
            $result.sysmon_feature_restart_needed = [bool]$enabled.RestartNeeded
            Write-Log "sysmon enable restart needed: $($enabled.RestartNeeded)"

            if ($enabled.RestartNeeded) {
                Set-Content -LiteralPath $stagePath -Value 'continue' -Encoding ASCII
                $runOnce = 'HKLM:\Software\Microsoft\Windows\CurrentVersion\RunOnce'
                $command = 'powershell.exe -NoProfile -ExecutionPolicy Bypass -File C:\sentinel\run-sentinel.ps1 -Continue'
                New-ItemProperty -Path $runOnce -Name 'SentinelContinue' -Value $command -PropertyType String -Force | Out-Null
                Write-Serial 'SENTINEL_STAGE_REBOOT'
                $rebootRequested = $true
                Start-Sleep -Seconds 2
                shutdown.exe /r /t 5 /f
                return
            }
        }
    }

    $featureAfter = Get-WindowsOptionalFeature -Online -FeatureName Sysmon
    $result.sysmon_feature_after = $featureAfter.State

    $sysmon = (Get-Command sysmon.exe -ErrorAction SilentlyContinue).Source
    if (-not $sysmon) {
        foreach ($candidate in @('C:\Windows\System32\sysmon.exe', 'C:\Windows\SysWOW64\sysmon.exe')) {
            if (Test-Path -LiteralPath $candidate) {
                $sysmon = $candidate
                break
            }
        }
    }
    if (-not $sysmon) {
        throw 'sysmon.exe not found after enabling the optional feature'
    }

    $serviceBefore = Get-Service -Name 'Sysmon*' -ErrorAction SilentlyContinue | Select-Object -First 1
    $result.sysmon_service_before = if ($serviceBefore) { $serviceBefore.Status.ToString() } else { 'missing' }

    $apply = ''
    $applyExit = $null
    if ($stage -ne 'after-sysmon') {
        $config = Join-Path $root 'sysmon-sentinel-v01.xml'
        if (-not $serviceBefore) {
            $applyResult = Invoke-Captured -FilePath $sysmon -Arguments @('-i', $config)
            $apply = $applyResult.Output
            $applyExit = $applyResult.ExitCode
            Write-Log "sysmon install exit=$applyExit"
        }
        else {
            $applyResult = Invoke-Captured -FilePath $sysmon -Arguments @('-c', $config)
            $apply = $applyResult.Output
            $applyExit = $applyResult.ExitCode
            if ($applyExit -ne 0 -or $apply -match 'not installed') {
                $applyResult = Invoke-Captured -FilePath $sysmon -Arguments @('-i', $config)
                $apply = $applyResult.Output
                $applyExit = $applyResult.ExitCode
                Write-Log "sysmon reinstall exit=$applyExit"
            }
        }
    }
    else {
        $result.sysmon_apply_skipped = 'after-sysmon reboot'
    }

    $result.sysmon_apply_exit = $applyExit
    $result.sysmon_apply = Limit-Text $apply
    Write-Log "sysmon apply exit=$applyExit"

    if ($applyExit -ne 0 -and $stage -ne 'after-sysmon') {
        throw "sysmon apply failed: $apply"
    }

    $serviceAfter = Get-Service -Name 'Sysmon*' -ErrorAction SilentlyContinue | Select-Object -First 1
    $result.sysmon_service_after = if ($serviceAfter) { $serviceAfter.Status.ToString() } else { 'missing' }

    $channel = $null
    for ($attempt = 0; $attempt -lt 20 -and -not $channel; $attempt++) {
        Start-Sleep -Seconds 1
        $channel = Get-WinEvent -ListLog 'Microsoft-Windows-Sysmon/Operational' -ErrorAction SilentlyContinue
    }

    if (-not $channel -and $stage -ne 'after-sysmon') {
        Set-Content -LiteralPath $stagePath -Value 'after-sysmon' -Encoding ASCII
        $runOnce = 'HKLM:\Software\Microsoft\Windows\CurrentVersion\RunOnce'
        $command = 'powershell.exe -NoProfile -ExecutionPolicy Bypass -File C:\sentinel\run-sentinel.ps1 -Continue'
        New-ItemProperty -Path $runOnce -Name 'SentinelContinue' -Value $command -PropertyType String -Force | Out-Null
        Write-Serial 'SENTINEL_STAGE_REBOOT_AFTER_SYSMON'
        $rebootRequested = $true
        Start-Sleep -Seconds 2
        shutdown.exe /r /t 5 /f
        return
    }

    if (-not $channel) {
        throw 'Sysmon Operational channel not found after installing the service'
    }
    $result.channel = [ordered]@{
        record_count = $channel.RecordCount
        maximum_size_bytes = $channel.MaximumSizeInBytes
        log_mode = $channel.LogMode.ToString()
        is_enabled = $channel.IsEnabled
    }
    Write-Log "channel records=$($channel.RecordCount) max=$($channel.MaximumSizeInBytes) mode=$($channel.LogMode)"

    try { Add-MpPreference -ExclusionPath $root -ErrorAction SilentlyContinue } catch { }

    $probeOutput = Join-Path $root 'probes.json'
    $probeResult = Invoke-Captured -FilePath 'powershell.exe' -Arguments @(
        '-NoProfile', '-ExecutionPolicy', 'Bypass',
        '-File', (Join-Path $root 'scripts\sysmon-probes.ps1'),
        '-Output', $probeOutput
    )
    $probeResult.Output | Set-Content -LiteralPath (Join-Path $root 'probes.out.txt') -Encoding UTF8
    Start-Sleep -Seconds 2

    $tempExe = Join-Path $env:TEMP 'sentinel-temp-probe.exe'
    Copy-Item -LiteralPath (Join-Path $root 'agent\sentinel-agent.exe') -Destination $tempExe -Force
    Invoke-Captured -FilePath $tempExe -Arguments @('--help') | Out-Null

    Invoke-Captured -FilePath 'powershell.exe' -Arguments @(
        '-NoProfile', '-EncodedCommand',
        'VwByAGkAdABlAC0ATwB1AHQAcAB1AHQAIABzAGUAbgB0AGkAbgBlAGwA'
    ) | Out-Null
    Start-Sleep -Seconds 3

    $probeOk = $false
    Push-Location $root
    try {
        $agent = Join-Path $root 'agent\sentinel-agent.exe'
        $ctl = Join-Path $root 'ctl\sentinelctl.exe'
        $db = Join-Path $root 'data\sentinel.db'

        $firstRunResult = Invoke-Captured -FilePath $agent -Arguments @('run', '--db', $db, '--max-records', '50')
        $firstRunExit = $firstRunResult.ExitCode
        $firstRunOutput = $firstRunResult.Output
        $firstRunOutput | Set-Content -LiteralPath (Join-Path $root 'agent-run-first.txt') -Encoding UTF8

        $runResult = Invoke-Captured -FilePath $agent -Arguments @('run', '--db', $db, '--max-records', '5000')
        $runExit = $runResult.ExitCode
        $runOutput = $runResult.Output
        $runOutput | Set-Content -LiteralPath (Join-Path $root 'agent-run.txt') -Encoding UTF8

        $verifyResult = Invoke-Captured -FilePath $ctl -Arguments @('verify', '--db', $db, '--schema', (Join-Path $root 'schemas\ocsf-1.9.0'))
        $verifyExit = $verifyResult.ExitCode
        $verifyOutput = $verifyResult.Output
        $verifyOutput | Set-Content -LiteralPath (Join-Path $root 'verify.txt') -Encoding UTF8

        $timelineResult = Invoke-Captured -FilePath $ctl -Arguments @('timeline', '--db', $db)
        $timelineExit = $timelineResult.ExitCode
        $timelineOutput = $timelineResult.Output
        $timelineOutput | Set-Content -LiteralPath (Join-Path $root 'timeline.txt') -Encoding UTF8

        $replayResult = Invoke-Captured -FilePath $ctl -Arguments @('replay', '--db', $db, '--rules', (Join-Path $root 'rules'))
        $replayExit = $replayResult.ExitCode
        $replayOutput = $replayResult.Output
        $replayOutput | Set-Content -LiteralPath (Join-Path $root 'replay.txt') -Encoding UTF8

        $captureResult = Invoke-Captured -FilePath $agent -Arguments @('capture', '--output', (Join-Path $root 'capture'), '--max-records', '5000')
        $captureExit = $captureResult.ExitCode
        $captureOutput = $captureResult.Output

        $captureFiles = 0
        $captureDirectory = Join-Path $root 'capture\events'
        if (Test-Path -LiteralPath $captureDirectory) {
            $captureFiles = @(Get-ChildItem -LiteralPath $captureDirectory -Filter '*.xml' -File).Count
        }

        $result.first_run_exit = $firstRunExit
        $result.first_run = Limit-Text $firstRunOutput
        $result.run_exit = $runExit
        $result.run = Limit-Text $runOutput
        $result.verify_exit = $verifyExit
        $result.verify = Limit-Text (($verifyOutput | ForEach-Object { $_.ToString() }) -join "`n")
        $result.timeline_exit = $timelineExit
        $result.timeline = Limit-Text (($timelineOutput | ForEach-Object { $_.ToString() }) -join "`n")
        $result.replay_exit = $replayExit
        $result.replay = Limit-Text (($replayOutput | ForEach-Object { $_.ToString() }) -join "`n")
        $result.capture_exit = $captureExit
        $result.capture = Limit-Text (($captureOutput | ForEach-Object { $_.ToString() }) -join "`n")
        $result.capture_files = $captureFiles

        if (Test-Path -LiteralPath $probeOutput) {
            $probeData = Get-Content -LiteralPath $probeOutput -Raw | ConvertFrom-Json
            $result.probes = $probeData
            $connected = @($probeData.steps | Where-Object { $_.step -eq 'outbound-connect' -and $_.outcome -eq 'connected' })
            $listenerExited = @($probeData.steps | Where-Object { $_.step -eq 'listener-exit' -and $_.exit_code -eq 0 })
            $probeOk = ($connected.Count -gt 0 -and $listenerExited.Count -gt 0)
            $result.probe_ok = $probeOk
        }

        $installerOk = $false
        $installScript = Join-Path $root 'scripts\install-sentinel-agent.ps1'
        $uninstallScript = Join-Path $root 'scripts\uninstall-sentinel-agent.ps1'
        if ((Test-Path -LiteralPath $installScript) -and (Test-Path -LiteralPath $uninstallScript)) {
            $installedRoot = 'C:\SentinelInstalled'
            $readerPlain = 'Sentinel!' + [guid]::NewGuid().ToString('N')
            $installerCommand = "& '$installScript' -SourceRoot '$root' -InstallRoot '$installedRoot' -ReaderUser 'sentinel-reader' -ReaderPassword (ConvertTo-SecureString '$readerPlain' -AsPlainText -Force) -TaskName 'SentinelAgent' -Start"
            $installResult = Invoke-Captured -FilePath 'powershell.exe' -Arguments @(
                '-NoProfile', '-ExecutionPolicy', 'Bypass', '-Command', $installerCommand
            )
            $installExit = $installResult.ExitCode
            $installOutput = $installResult.Output
            $installOutput | Set-Content -LiteralPath (Join-Path $root 'install.txt') -Encoding UTF8
            $installData = $null
            $jsonStart = $installOutput.IndexOf('{')
            if ($jsonStart -ge 0) {
                try { $installData = $installOutput.Substring($jsonStart) | ConvertFrom-Json } catch { }
            }
            Start-Process -FilePath 'cmd.exe' -ArgumentList @('/c', 'echo installer-probe') -Wait | Out-Null
            Start-Sleep -Seconds 15

            $taskInfo = Get-ScheduledTask -TaskName 'SentinelAgent' -ErrorAction SilentlyContinue
            $taskState = if ($taskInfo) { $taskInfo.State.ToString() } else { 'missing' }
            $taskDetails = Get-ScheduledTaskInfo -TaskName 'SentinelAgent' -ErrorAction SilentlyContinue
            $taskLastResult = if ($taskDetails) { $taskDetails.LastTaskResult } else { $null }
            Stop-ScheduledTask -TaskName 'SentinelAgent' -ErrorAction SilentlyContinue
            Start-Sleep -Seconds 2

            $installedDb = Join-Path $installedRoot 'data\sentinel.db'
            $installedTimeline = ''
            $installedTimelineExit = -1
            $installedVerify = ''
            $installedVerifyExit = -1
            $installedEvents = 0
            $agentLogPath = Join-Path $installedRoot 'data\agent.log'
            $agentLog = if (Test-Path -LiteralPath $agentLogPath) { Get-Content -LiteralPath $agentLogPath -Raw } else { '' }
            $installedDbExists = Test-Path -LiteralPath $installedDb
            if (Test-Path -LiteralPath $installedDb) {
                $installedTimelineResult = Invoke-Captured -FilePath $ctl -Arguments @('timeline', '--db', $installedDb)
                $installedTimelineExit = $installedTimelineResult.ExitCode
                $installedTimeline = $installedTimelineResult.Output
                $installedEvents = @($installedTimeline -split "`n" | Where-Object { $_ -match '^\s*\d+\s' }).Count
                $installedVerifyResult = Invoke-Captured -FilePath $ctl -Arguments @('verify', '--db', $installedDb, '--schema', (Join-Path $root 'schemas\ocsf-1.9.0'))
                $installedVerifyExit = $installedVerifyResult.ExitCode
                $installedVerify = $installedVerifyResult.Output
                Copy-Item -LiteralPath $installedDb -Destination (Join-Path $root 'data\installed.db') -Force
            }

            $aclAfter = (& wevtutil.exe gl 'Microsoft-Windows-Sysmon/Operational' 2>&1 | Select-String -Pattern '^\s*channelAccess:\s*' | Select-Object -First 1)
            $aclAfterText = if ($aclAfter) { $aclAfter.ToString() } else { '' }
            $readerSid = if ($installData -and $installData.reader_sid) { $installData.reader_sid } else { '' }
            $readerAclOk = ($readerSid -and $aclAfterText -match [regex]::Escape($readerSid) -and $aclAfterText -notmatch 'S-1-5-32-545' -and $aclAfterText -notmatch 'S-1-1-0')

            $outsiderDenied = $false
            $outsiderExit = $null
            $outsiderPlain = 'Outsider!' + [guid]::NewGuid().ToString('N')
            $outsiderDir = 'C:\sentinel-outsider-test'
            try {
                New-Item -ItemType Directory -Path $outsiderDir -Force | Out-Null
                & icacls.exe $outsiderDir /grant '*S-1-5-32-545:(OI)(CI)M' | Out-Null
                $outsiderUser = Get-LocalUser -Name 'sentinel-outsider' -ErrorAction SilentlyContinue
                if (-not $outsiderUser) {
                    New-LocalUser -Name 'sentinel-outsider' -Password (ConvertTo-SecureString $outsiderPlain -AsPlainText -Force) -PasswordNeverExpires -AccountNeverExpires | Out-Null
                }
                else {
                    Set-LocalUser -Name 'sentinel-outsider' -Password (ConvertTo-SecureString $outsiderPlain -AsPlainText -Force)
                }
                $cred = New-Object System.Management.Automation.PSCredential(
                    'sentinel-outsider', (ConvertTo-SecureString $outsiderPlain -AsPlainText -Force))
                $outsider = Start-Process -FilePath 'wevtutil.exe' `
                    -ArgumentList @('qe', 'Microsoft-Windows-Sysmon/Operational', '/c:1', '/rd:true', '/f:text') `
                    -Credential $cred -Wait -PassThru -WorkingDirectory $outsiderDir `
                    -RedirectStandardOutput (Join-Path $outsiderDir 'out.txt') `
                    -RedirectStandardError (Join-Path $outsiderDir 'err.txt')
                $outsiderExit = $outsider.ExitCode
                $outsiderDenied = ($outsider.ExitCode -ne 0)
            }
            catch {
                $result.outsider_error = $_.Exception.Message
            }
            finally {
                Remove-LocalUser -Name 'sentinel-outsider' -ErrorAction SilentlyContinue
                Remove-Item -LiteralPath $outsiderDir -Recurse -Force -ErrorAction SilentlyContinue
            }

            $preInstallerOk = (
                $installExit -eq 0 -and
                $installedTimelineExit -eq 0 -and
                $installedVerifyExit -eq 0 -and
                $installedEvents -gt 0 -and
                $readerAclOk -and
                $outsiderDenied
            )

            $mainOk = (
                $firstRunExit -eq 0 -and
                $runExit -eq 0 -and
                $verifyExit -eq 0 -and
                $timelineExit -eq 0 -and
                $replayExit -eq 0 -and
                $captureExit -eq 0 -and
                $captureFiles -gt 0 -and
                $probeOk
            )

            if (-not $NoReboot) {
                $preStatus = Invoke-Captured -FilePath $ctl -Arguments @('status', '--db', $installedDb)
                $preEvents = Get-StatusNumber -Text $preStatus.Output -Name 'events'
                if ($preEvents -lt 0) { $preEvents = $installedEvents }
                $preBookmark = Get-StatusValue -Text $preStatus.Output -Name 'bookmark'
                $state = [ordered]@{
                    main_ok = $mainOk
                    pre_events = $preEvents
                    pre_bookmark = $preBookmark
                    pre_task_state = $taskState
                    installed_root = $installedRoot
                    installer_ok = $preInstallerOk
                    channel_acl_before = if ($installData) { $installData.channel_acl_before } else { '' }
                    reader_sid = $readerSid
                    timestamp_utc = [DateTimeOffset]::UtcNow.ToString('O')
                }
                $state | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $root 'reboot-state.json') -Encoding UTF8

                $resumeScript = Join-Path $root 'run-sentinel.ps1'
                $resumeAction = New-ScheduledTaskAction `
                    -Execute (Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe') `
                    -Argument "-NoProfile -ExecutionPolicy Bypass -File `"$resumeScript`" -Resume" `
                    -WorkingDirectory $root
                $resumeTrigger = New-ScheduledTaskTrigger -AtStartup
                $resumeSettings = New-ScheduledTaskSettingsSet `
                    -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries `
                    -ExecutionTimeLimit (New-TimeSpan -Minutes 15) `
                    -MultipleInstances IgnoreNew -StartWhenAvailable
                $resumePrincipal = New-ScheduledTaskPrincipal -UserId 'NT AUTHORITY\SYSTEM' -LogonType ServiceAccount -RunLevel Highest
                Register-ScheduledTask -TaskName 'SentinelResume' -Action $resumeAction -Trigger $resumeTrigger `
                    -Settings $resumeSettings -Principal $resumePrincipal -Force | Out-Null

                $result.reboot_resume = $true
                $result.main_ok = $mainOk
                $result.pre_events = $preEvents
                $result.pre_bookmark = $preBookmark
                $result.installer_before_reboot = $preInstallerOk
                $result.resume_task_registered = $true
                $result | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $root 'main-result.json') -Encoding UTF8
                Write-Log "resume task registered; pre_events=$preEvents; rebooting"
                Write-Serial 'SENTINEL_STAGE_RESUME_REBOOT'
                $rebootRequested = $true
                Start-Sleep -Seconds 2
                shutdown.exe /r /t 5 /f
                return
            }

            $uninstallResult = Invoke-Captured -FilePath 'powershell.exe' -Arguments @(
                '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $uninstallScript,
                '-InstallRoot', $installedRoot, '-ReaderUser', 'sentinel-reader',
                '-TaskName', 'SentinelAgent', '-RemoveReader', '-RemoveData', '-RemoveInstall'
            )
            $uninstallExit = $uninstallResult.ExitCode
            $uninstallOutput = $uninstallResult.Output
            $uninstallOutput | Set-Content -LiteralPath (Join-Path $root 'uninstall.txt') -Encoding UTF8
            $channelAfterUninstall = (& wevtutil.exe gl 'Microsoft-Windows-Sysmon/Operational' 2>&1 | Select-String -Pattern '^\s*channelAccess:\s*' | Select-Object -First 1)
            $channelAfterUninstallText = if ($channelAfterUninstall) { $channelAfterUninstall.ToString() } else { '' }
            $oldAcl = if ($installData -and $installData.channel_acl_before) { $installData.channel_acl_before } else { '' }
            $uninstallJson = $null
            try { $uninstallJson = $uninstallOutput | ConvertFrom-Json }
            catch { $result.uninstall_parse_error = $_.Exception.Message }
            $channelAfterUninstallAcl = ($channelAfterUninstallText -replace '^\s*channelAccess:\s*', '').Trim()
            $uninstallRestored = ([bool]($uninstallJson -and $uninstallJson.channel_acl_restored) -and $oldAcl -ne '' -and $channelAfterUninstallAcl -eq $oldAcl)
            $readerRemoved = -not [bool](Get-LocalUser -Name 'sentinel-reader' -ErrorAction SilentlyContinue)
            $installRemoved = -not (Test-Path -LiteralPath $installedRoot)

            $installerOk = (
                $installExit -eq 0 -and
                $installedTimelineExit -eq 0 -and
                $installedVerifyExit -eq 0 -and
                $installedEvents -gt 0 -and
                $readerAclOk -and
                $outsiderDenied -and
                $uninstallExit -eq 0 -and
                $uninstallRestored -and
                $readerRemoved -and
                $installRemoved
            )

            $result.installer_exit = $installExit
            $result.installer = Limit-Text $installOutput
            $result.installed_timeline_exit = $installedTimelineExit
            $result.installed_timeline = Limit-Text $installedTimeline
            $result.installed_events = $installedEvents
            $result.installed_verify_exit = $installedVerifyExit
            $result.installed_verify = Limit-Text $installedVerify
            $result.task_state = $taskState
            $result.task_last_result = $taskLastResult
            $result.installed_agent_log = Limit-Text $agentLog
            $result.installed_db_exists = $installedDbExists
            $result.channel_acl_after_install = $aclAfterText
            $result.reader_acl_ok = $readerAclOk
            $result.outsider_exit = $outsiderExit
            $result.outsider_denied = $outsiderDenied
            $result.uninstall_exit = $uninstallExit
            $result.uninstall = Limit-Text $uninstallOutput
            $result.uninstall_restored = $uninstallRestored
            $result.reader_removed = $readerRemoved
            $result.install_removed = $installRemoved
            $result.installer_ok = $installerOk
        }

        $result.ok = (
            $firstRunExit -eq 0 -and
            $runExit -eq 0 -and
            $verifyExit -eq 0 -and
            $timelineExit -eq 0 -and
            $replayExit -eq 0 -and
            $captureExit -eq 0 -and
            $captureFiles -gt 0 -and
            $probeOk -and
            $installerOk
        )
    }
    finally {
        Pop-Location
    }
}
catch {
    $result.ok = $false
    $result.error = $_.Exception.Message
    $result.stack = $_.ScriptStackTrace
    Write-Log "failed: $($_.Exception.Message)"
}
finally {
    if (-not $rebootRequested) {
        Finish $result
    }
}
