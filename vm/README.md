# Sentinel V0.1 Windows VM harness

This directory drives one disposable Windows 11 Enterprise Eval 26H2 VM without manual OOBE input. It is adapted from PeerVault's proven BIOS/QEMU clean-client drill: a data-only test ISO supplies `autounattend.xml`, the Sentinel binaries, rules, schemas, and the probe script; a writable FAT disk receives results; COM1 receives a JSON result; the guest shuts down when done.

## Inputs

- Windows ISO: `G:\ISO\Windows11EnterpriseEval-26H2-x64-en-us.iso`
- SHA-256: `bc3f24086ebadc94489066b5ad78089e2cf5c3491e90e790bb81a2b199c10e38` (verified)
- Host toolchain used for the lab: Docker Desktop with `/dev/kvm` passthrough, a Debian bookworm container with QEMU 7.2, SeaBIOS, and `-accel kvm -cpu host`; `pycdlib`, `mkfs.vfat`.
- Native WSL QEMU could not use `/dev/kvm` (the WSL user is not in the `kvm` group). A TCG/BIOS attempt with `-cpu Haswell` reached Windows PE but crashed with `SYSTEM_THREAD_EXCEPTION_NOT_HANDLED (0x7E)`; TCG is not the working path here.

## Validation status

The automated harness passed with `ok:true` on 2026-11-03 on Windows 11 Enterprise Evaluation, build 26300. The latest run accepted 650 records across first/resume against a retained 1,428-event store, produced 6 alerts and 0 gaps, verified with 0 schema errors, replayed 6/6, and copied a 1,448-file capture plus logs to the SATA result volume. It then installed the agent under `sentinel-reader`: the task ran and stored 117 events/2 alerts, the channel ACL contained only Administrators/SYSTEM/reader, an outsider query was denied (exit 5), and teardown restored the channel ACL and user rights. See [`docs/v01-live-validation.md`](../docs/v01-live-validation.md) for evidence hashes and limits.

The SATA result volume path works; earlier FAT/copy results are historical. The latest install/teardown test ISO has SHA-256 `9bd059702e885590c9d16df1361d8da53dff86ca006c8a074257ff5a5ce153e2`; the self-contained agent binary is `85e1fbc9ca5a2b15ce26e514df81cd0ed97332ed50014e2b6de35367d525dc77`, and the current profile hash is `da778a7639ab26df80924c24c79930e5df1fbde6d3716b462979995abd6a6949`. The harness now clears `data`/`capture` before a run, but the latest result predates that clear path; a clean-store rerun and a reboot-resume test remain open.

## Build

1. Publish the Windows binaries:

```bash
dotnet publish src/sentinel-agent -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o publish/win-x64
dotnet publish src/sentinelctl -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o publish/win-x64-ctl
```

2. Stage `vm/test-media/` with `autounattend.xml`, `run-sentinel.ps1`, `sysmon-sentinel-v01.xml`, `scripts/`, `rules/`, `schemas/ocsf-1.9.0/`, `agent/` (the full publish folder, including `e_sqlite3.dll`), and `ctl/`.

3. Build the data-only ISO:

```bash
python3 vm/build-test-iso.py --source vm/test-media --output /path/sentinel-test.iso
```

4. Create the 80 GiB Windows disk and the 2 GiB FAT result disk:

```bash
qemu-img create -f qcow2 sentinel.qcow2 80G
truncate -s 2G sentinel-out.img
mkfs.vfat -F 32 -n SENTINELOUT sentinel-out.img
```

5. Build the QEMU container:

```powershell
docker build -t sentinel-qemu vm
```

6. Put `sentinel.qcow2`, `sentinel-test.iso`, and `sentinel-out.img` in `G:\VM\Sentinel-QEMU`, then run:

```powershell
.\vm\run-docker.ps1
```

The container maps `G:\VM\Sentinel-QEMU` to `/vm` and `G:\ISO` to `/iso:ro`, exposes VNC on `127.0.0.1:5903`, and writes `serial.log` into the VM directory.

## Guest flow

`autounattend.xml` wipes Disk 0, installs image index 1, bypasses the TPM/Secure Boot checks for this disposable BIOS lab, creates the local `sentinel` administrator, auto-logs on, and runs `run-sentinel.ps1` from the `SENTINEL_TEST` volume.

`run-sentinel.ps1`:

1. copies the agent, control CLI, rules, schemas, and scripts to `C:\sentinel`, then clears any prior `data`/`capture`;
2. enables the built-in Sysmon optional feature (one reboot if required, continued through `RunOnce`);
3. applies `sysmon-sentinel-v01.xml`;
4. runs the loopback connect/accept/refuse probes, a temp-directory executable, and an encoded PowerShell command;
5. runs the live reader, `sentinelctl verify`, `timeline`, and `replay`;
6. captures raw XML;
7. installs the reader/task via `scripts/install-sentinel-agent.ps1`, checks the channel ACL, starts the task as the reader, verifies its database, attempts an outsider query, then uninstalls and restores ACLs/user rights;
8. copies the database, capture, installed-task database, and logs to the `SENTINELOUT` SATA/FAT volume;
9. writes a JSON result between `SENTINEL_RESULT_BEGIN`/`SENTINEL_RESULT_END` on COM1 and shuts down.

The raw FAT disk can be read on the host with `pyfatfs` without mounting it:

```bash
python3 vm/extract-results.py --image /path/sentinel-out.img --output vm/results
```

`vm/results/` is ignored by Git. The serial log also contains the JSON result between the `SENTINEL_RESULT_BEGIN` and `SENTINEL_RESULT_END` markers.

## VMware note

The available VMware Workstation 17 **Player** does not support vTPM devices or snapshots. A UEFI attempt stopped at the VMware Boot Manager, and an orphaned VMX process later blocked new VM pipes. The QEMU/SeaBIOS path is the proven lab path (same as PeerVault's passing BIOS drill); it is not a VMware/UEFI qualification.

## Limits

This is a disposable synthetic lab. It does not prove independent hardware, production retention, full Sysmon coverage, reboot-resume, or legal evidence. The installed task currently starts from a manual `Start-ScheduledTask`/`schtasks /Run`, stores the reader password, and needs the local batch-logon right; a service installation and reboot test remain open. Live Event ID 3 records without an independently observed outcome remain OCSF Unknown by the accepted strict decision.
