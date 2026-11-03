param(
    [string]$Output = ".\probes.json",
    [int]$ListenPort = 18443,
    [int]$RefusedPort = 9
)

$ErrorActionPreference = "Stop"
$results = [ordered]@{
    started_utc = [DateTimeOffset]::UtcNow.ToString("O")
    listen_port = $ListenPort
    refused_port = $RefusedPort
    steps = @()
}

$listenerCommand = @"
`$listener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, $ListenPort)
`$listener.Start()
try {
    `$client = `$listener.AcceptTcpClient()
    `$client.Close()
    Write-Output "accepted"
} catch {
    Write-Output ("listener-error: " + `$_.Exception.Message)
} finally {
    `$listener.Stop()
}
"@

$listener = Start-Process -FilePath "powershell.exe" `
    -ArgumentList @("-NoProfile", "-Command", $listenerCommand) `
    -PassThru -WindowStyle Hidden

$ready = $false
for ($attempt = 0; $attempt -lt 40; $attempt++) {
    if (Get-NetTCPConnection -LocalPort $ListenPort -State Listen -ErrorAction SilentlyContinue) {
        $ready = $true
        break
    }
    Start-Sleep -Milliseconds 250
}

$results.listener_process_id = $listener.Id
$results.listener_ready = $ready
$results.steps += [ordered]@{
    step = "listener-start"
    at_utc = [DateTimeOffset]::UtcNow.ToString("O")
    process_id = $listener.Id
    outcome = if ($ready) { "listening" } else { "not-listening" }
}

$client = [System.Net.Sockets.TcpClient]::new()
try {
    $client.Connect("127.0.0.1", $ListenPort)
    $results.steps += [ordered]@{
        step = "outbound-connect"
        at_utc = [DateTimeOffset]::UtcNow.ToString("O")
        process_id = $PID
        outcome = "connected"
    }
} catch {
    $results.steps += [ordered]@{
        step = "outbound-connect"
        at_utc = [DateTimeOffset]::UtcNow.ToString("O")
        process_id = $PID
        outcome = "connect-error: " + $_.Exception.Message
    }
} finally {
    $client.Close()
}

if (-not $listener.WaitForExit(10000)) {
    $listener.Kill()
    $results.steps += [ordered]@{
        step = "listener-exit"
        at_utc = [DateTimeOffset]::UtcNow.ToString("O")
        outcome = "timeout"
    }
} else {
    $results.steps += [ordered]@{
        step = "listener-exit"
        at_utc = [DateTimeOffset]::UtcNow.ToString("O")
        exit_code = $listener.ExitCode
        outcome = "exited"
    }
}

$refused = [System.Net.Sockets.TcpClient]::new()
try {
    $refused.Connect("127.0.0.1", $RefusedPort)
    $results.steps += [ordered]@{
        step = "refused-connect"
        at_utc = [DateTimeOffset]::UtcNow.ToString("O")
        process_id = $PID
        outcome = "unexpectedly-connected"
    }
} catch {
    $results.steps += [ordered]@{
        step = "refused-connect"
        at_utc = [DateTimeOffset]::UtcNow.ToString("O")
        process_id = $PID
        outcome = "refused-or-failed: " + $_.Exception.Message
    }
} finally {
    $refused.Close()
}

$results.finished_utc = [DateTimeOffset]::UtcNow.ToString("O")
$results | ConvertTo-Json -Depth 6 | Set-Content -Path $Output -Encoding UTF8
Write-Output ("wrote " + (Resolve-Path $Output))
