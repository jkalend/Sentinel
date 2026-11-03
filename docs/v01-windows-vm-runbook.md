# V0.1 Windows 11 VM runbook

**Status: the automated QEMU/KVM lab harness passed on 2026-11-03 with the least-privileged reader and installer** (see [V0.1 live validation](v01-live-validation.md)); this manual VMware procedure remains unrun. The automated harness is in [`vm/`](../vm/README.md), adapted from PeerVault's passing BIOS drill. The manual steps below remain for a host with VMware Workstation Pro (vTPM and snapshots); VMware Workstation Player cannot add a vTPM or take snapshots. This runbook changes host configuration and must run only on a disposable copy of the Windows 11 VM. Record the VM path, backup method, Windows build, locale, and vCPU/RAM/disk before starting. Do not run it on a shared or working VM.

The profile below is reviewed against the Microsoft Sysmon configuration documentation: an empty `onmatch="exclude"` filter includes every event of that type. It enables only process creation and network connections, leaves channel size/retention unchanged, and does not bundle or redistribute Sysinternals binaries.

## 0. Preflight (administrator PowerShell)

```powershell
Get-ComputerInfo | Select-Object WindowsProductName, WindowsVersion, OsBuildNumber, OsArchitecture
Get-WindowsOptionalFeature -Online -FeatureName Sysmon | Select-Object FeatureName, State
Get-Service sysmon* -ErrorAction SilentlyContinue
wevtutil gl Microsoft-Windows-Sysmon/Operational
```

Abort if standalone Sysmon is installed, any Sysmon service/configuration already exists, or the feature state cannot be restored. Record the channel `maxSize` and `retention` values; Sentinel captures but does not change them.

## 1. Rollback point

With VMware Workstation Pro, take a snapshot named `sentinel-v01-pre`. With Player, power the VM off and copy its whole directory to a separate powered-off backup; record both paths. Restore the snapshot or directory if Windows requests a restart or any state differs from the preflight record.

## 2. Enable built-in Sysmon

```powershell
Enable-WindowsOptionalFeature -Online -FeatureName Sysmon
```

If Windows requests a restart, stop and restore the snapshot rather than continuing.

## 3. Apply the reviewed profile

Copy `scripts/sysmon-sentinel-v01.xml` to `C:\Sentinel\sysmon-sentinel-v01.xml` and run:

```powershell
sysmon -i C:\Sentinel\sysmon-sentinel-v01.xml
```

If the service is already installed by the feature, use `sysmon -c C:\Sentinel\sysmon-sentinel-v01.xml` instead. Record the output and the effective configuration.

## 4. Verify Event ID 1 and 3

```powershell
Get-WinEvent -LogName Microsoft-Windows-Sysmon/Operational -MaxEvents 10 |
  Select-Object Id, RecordId, TimeCreated, ProviderName
```

Generate a harmless process (`notepad.exe`) and confirm Event ID 1. Run the probe script and confirm Event ID 3 records. Do not infer success/outcome semantics from the field name or from an absent event.

## 5. Reader identity and channel ACL

Create or select a local standard account for the Sentinel reader. Record the channel access descriptor before changing anything:

```powershell
wevtutil gl Microsoft-Windows-Sysmon/Operational
```

Grant only channel `Read` (`0x1`) through the channel access descriptor (`EvtChannelConfigAccess`), preserving existing ACEs. Do not grant `Write` (`0x2`), `Clear` (`0x4`), log-clear rights, or administrative membership. Record the resulting descriptor. Test an actual query as the reader account; access denied must appear as a collection gap, not as "no events."

## 6. Publish and copy the agent

On the development host:

```bash
dotnet publish src/sentinel-agent -c Release -r win-x64 --self-contained true \
  -p:PublishSingleFile=true -o publish/win-x64
```

Copy `publish/win-x64`, `rules/`, and `scripts/` into `C:\Sentinel` on the VM. As the reader account:

```powershell
C:\Sentinel\win-x64\sentinel-agent.exe channel
C:\Sentinel\win-x64\sentinel-agent.exe run --db C:\Sentinel\data\sentinel.db --max-records 1
```

The first command must show the actual channel size/mode; the second must ingest at least one record and persist a serialized bookmark. If the channel cannot be read, record the failure and stop.

### Automated local install (after steps 0–5)

The reviewable installer is [`scripts/install-sentinel-agent.ps1`](../scripts/install-sentinel-agent.ps1). It creates the local `sentinel-reader`, restricts the Sysmon channel to Administrators/SYSTEM/reader, grants the reader write access only under `data`/`capture`, and registers a startup task that runs the agent as that reader. It does **not** install Sysmon or download binaries.

```powershell
$pw = Read-Host -AsSecureString
.\install-sentinel-agent.ps1 -SourceRoot C:\Sentinel\deploy -ReaderPassword $pw -Start
```

`C:\Sentinel\deploy` must contain `agent/`, `ctl/`, `rules/`, and `schemas/ocsf-1.9.0/`. The task stores the reader password so it can start without an interactive logon; treat that as a local secret and rotate it. `-NoTask` applies the account/ACL only. The saved original channel descriptor is `channel-acl-before.txt` in the install root.

Teardown (step 9) uses [`scripts/uninstall-sentinel-agent.ps1`](../scripts/uninstall-sentinel-agent.ps1):

```powershell
.\uninstall-sentinel-agent.ps1 -RemoveReader -RemoveData -RemoveInstall
```

Data is preserved unless `-RemoveData` is given; the original channel ACL is restored from the saved descriptor before the reader is removed.

## 7. Controlled probes and capture

Run the probe script as the reader account, writing its independent outcome log:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File C:\Sentinel\scripts\sysmon-probes.ps1 -Output C:\Sentinel\probes.json
```

Then capture the source records:

```powershell
C:\Sentinel\win-x64\sentinel-agent.exe capture --output C:\Sentinel\capture --max-records 1000
```

`capture` starts at the oldest retained record and writes exact rendered XML plus a manifest. Its `network_ground_truth` array is empty by design.

## 8. Ground truth and strict Event ID 3 mapping

Correlate each observed Event ID 3 with the probe log by process GUID, ports, and timestamps. Add only outcomes actually observed:

- `established_outbound` — the client connect succeeded and the listener accepted.
- `inbound_listener` — the listener-side record for the accepted connection.
- `refused` — the client observed refusal/failure.
- `unknown` — no independent outcome.

The mapper maps Event ID 3 to OCSF `Open` only for `established_outbound` with `Initiated=true` and `tcp`. Inbound, refused, UDP, and unknown outcomes stay `activity_id=0` (Unknown). Live Event ID 3 records without an accompanying ground-truth entry stay Unknown; the network rule therefore does not fire on them. This is the accepted strict behavior.

Copy the capture and probe log out of the VM. Do not commit real usernames, command lines, paths, or endpoint addresses; sanitize or replace them with synthetic values before adding fixtures to Git.

## 9. Teardown

If the local installer was used, unregister the task/ACL/reader first (data is preserved unless `-RemoveData`):

```powershell
.\uninstall-sentinel-agent.ps1 -RemoveReader
```

Then remove Sysmon if the lab installed it:

```powershell
sysmon -u
Disable-WindowsOptionalFeature -Online -FeatureName Sysmon
Get-WindowsOptionalFeature -Online -FeatureName Sysmon
Get-Service sysmon*
```

Verify the feature is disabled and no Sysmon service remains. Restore the pre-run snapshot if the state differs or a restart was requested. Record the teardown result.

## Evidence to record

- Windows edition/build, locale, VM vCPU/RAM/disk, VM path/snapshot.
- Sysmon feature state before/after, config file SHA-256, effective schema version.
- Channel maximum size, retention/overwrite mode, and access descriptor before/after.
- Reader identity/SID and actual query result.
- `sentinel-agent channel` and `run` output, bookmark record ID/generation, ingest counts.
- Capture manifest, probe log, and the exact Event ID 3 ground-truth mapping.
- Any restart request, abort, or state mismatch.

## What this does not prove

No claim of complete Sysmon coverage, successful live detection quality, remote-interface inbound semantics, UDP/failed-attempt coverage, or production retention. The accepted V0.1 scope is one disposable Windows 11 host and synthetic/controlled activity.
