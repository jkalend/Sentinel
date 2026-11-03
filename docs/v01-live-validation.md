# V0.1 live-source validation

**Status: lab pass with least-privileged reader and installer, 2026-11-03.** One disposable Windows 11 Enterprise Evaluation VM ran the live Event Log reader, OCSF mapping, three-rule evaluator, SQLite store, replay, timeline, schema verifier, capture, and the local install/teardown flow. The final harness result is `ok:true`; the installed task ran as `sentinel-reader`, the channel ACL contained only Administrators/SYSTEM/reader, an outsider query was denied, and teardown restored the channel ACL and local user rights. This is a disposable synthetic lab, not a supported release, a clean-enrollment measurement, or a reboot-resume qualification.

## Environment

| Item | Value |
|---|---|
| Image | `Windows11EnterpriseEval-26H2-x64-en-us.iso` |
| ISO SHA-256 | `bc3f24086ebadc94489066b5ad78089e2cf5c3491e90e790bb81a2b199c10e38` (verified) |
| Guest | Microsoft Windows 11 Enterprise Evaluation, version `10.0.26300`, build `26300` |
| Hypervisor | Docker Desktop QEMU 7.2, KVM, SeaBIOS BIOS, 4 vCPU, 6 GiB RAM, 80 GiB qcow2 |
| Firmware caveat | BIOS install with the disposable `LabConfig` TPM/Secure Boot bypass; not a supported production configuration |
| Sysmon | Built-in optional feature enabled; service `Running`; config schema `4.90`, Sysmon schema `4.91` |
| Channel | `Microsoft-Windows-Sysmon/Operational`, 64 MiB, `Circular`, enabled |
| Reader identity | Main live reader ran as local administrator (`sentinel`); installed task ran as `sentinel-reader`, a local non-admin with read-only channel access |

## Runs

| Run | Channel at start | Accepted | Source health | Alerts | Gaps | Bookmark | Capture | Verify | Replay |
|---|---|---|---|---|---|---|---|---|---|
| Bounded (300 cap) | 414 | 300 | 98 | 2 | 0 | 300, generation 2 | 452 | 0 errors | 2/2 match |
| Full channel (5000 cap) | 851 | 873 | 301 | 6 | 0 | 873, generation 4 | 898 | 0 errors | 6/6 match |
| Final snapshot boundary | 1561 | 1573 | 455 | 10 | 0 | 1573, generation 6 | 1622 | 0 errors | 10/10 match |
| Latest live + installer | 1416 | 650 this run (1,428 stored) | 4 | 6 | 0 | 1428, generation 4 | 1,448 | 0 errors | 6/6 match |
| Installed reader task | 1,416 | 117 | 0 | 2 | 0 | 117, generation 2 | n/a | 0 errors | n/a |

The latest run used an **existing** store: the 50-record first run resumed at the existing bookmark and the 600-record run continued to record 1,428, so the 1,428 events are not all from one clean enrollment. The code now clears `C:\sentinel\data` and `capture` before each harness run, but that clean-store path was added after this result and is not itself measured here. `source_health` includes Sysmon service/config records and all non-1/3 channel records; it is not an error count.

The SATA result-volume path now works. The latest run's `result.json`, main database, capture manifest, logs, and installed-task database were copied to the SATA FAT volume, extracted, and host-verified; see the evidence hashes below. The older 873/1573-run outputs remain serial-only historical runs from before the SATA fix. The 300-record run's database and capture were extracted from its FAT volume and host-verified:

- 300 events, 2 alerts (`sentinel.process.encoded-powershell`, `sentinel.process.temp-executable`). Event IDs present: 1 (144), 3 (58), 4 (2), 5 (95), 16 (1). The recorded 300-run profile therefore also emitted ProcessTerminate; those records were retained as `source_health`, not normalized. The current profile adds an empty `ProcessTerminate` include, and the final run applied it, but the final run's DB was not extracted, so suppression was not host-inspected.
- 0 gaps; bookmark record 300, config generation 2, coverage start 1, serialized bookmark XML 125 bytes.
- All normalized Event ID 3 rows inspected are OCSF `activity_id=0` (Unknown), including TCP/UDP and loopback probe records. No live network alert was produced under the strict decision.
- `sentinelctl verify` reports 0 errors; `replay` reports 2 live, 2 replay, matches.
- Capture contains 452 XML files at the source; 444 lowercase `.xml` files were counted in the extracted copy. The discrepancy is in the FAT copy/name enumeration, not the capture command's count.

### Installed reader task

The latest run then installed the same agent under `C:\SentinelInstalled` from the self-contained publish:

- `scripts/install-sentinel-agent.ps1` created the local non-admin `sentinel-reader`, granted only `Read` on `Microsoft-Windows-Sysmon/Operational` to Administrators, SYSTEM, and that SID, granted the reader modify access only under `data`/`capture`, saved the prior channel ACL, granted `SeBatchLogonRight`, and registered `SentinelAgent`.
- The task ran as `sentinel-reader` (state `Running`, last result `267009`), stored 117 events and 2 alerts in its own database, and the installed database verified with 0 schema errors.
- The channel ACL after install was `O:BAG:SYD:(A;;0x1;;;S-1-5-32-544)(A;;0x1;;;S-1-5-18)(A;;0x1;;;S-1-5-21-...-1005)`; `reader_acl_ok` was true.
- A second local user `sentinel-outsider` ran `wevtutil qe` with the same channel and exited 5 (access denied).
- `scripts/uninstall-sentinel-agent.ps1` removed the task, restored the original channel ACL and user-rights policy, removed the reader, and removed `C:\SentinelInstalled`; the harness recorded `installer_ok: true`.
- The task was started with `Start-ScheduledTask`/`schtasks /Run`, not by a reboot. The installer stores the reader password and grants the batch-logon right for non-interactive startup; both are local-lab limitations, not a recommendation for unmanaged production.

The installed agent log was empty (the agent's console output was not flushed before the task was stopped); the installed database is the evidence that the task ran. The harness's `installed_db_exists` field was evaluated after teardown and therefore reads false; the extracted `installed.db` was host-verified with 117 events and 0 schema errors. The harness now captures that boolean before teardown.

## Evidence

| Artifact | SHA-256 |
|---|---|
| Latest result JSON (`vm/results-final-task/result.json`) | `7faf92e4df368abf705c128b05b3f898a89616a35fd5b1d7624907293a9e8601` |
| Latest main database (`vm/results-final-task/DATA/SENTINEL.DB`) | `f3d54822f9dc52ec22a7dd2cb7803915e353fafee99ec4b9cd440ad851193862` |
| Installed-task database (`vm/results-final-task/DATA/installed.db`) | `95f32616950986c61c0f9ce7768dbdb174bd50cb55b6fcdb8d67599999993a01` |
| Latest capture manifest (`vm/results-final-task/CAPTURE/fixture.json`) | `82745c463290d591af0b157519f2767c00173f70d76a9dd4b67d080d3dd8b1d6` |
| Latest install log (`vm/results-final-task/INSTALL.TXT`) | `9b90c7b2f3876bd3a2f1a2b698539582ac7d13db5b0b173227267f529839c1e3` |
| Latest uninstall log (`vm/results-final-task/uninstall.txt`) | `557bc2ac1402de6a177f7b618eb58641ad4df3b1f61459c1eaa55c7e5f7ee727` |
| 300-run result JSON (`vm/results-live2/result.json`) | `328ec23712cb808886611b580bd18f54ff03879134ff99eb59818fe362fbabe9` |
| 300-run database (`vm/results-live2/DATA/SENTINEL.DB`) | `954ab7b1dab301c2122370ed2cf136d8c50285214ae526ee6fd62e0a937a23a4` |
| 300-run capture manifest (`vm/results-live2/CAPTURE/fixture.json`) | `8695aff8bf142c6f5b3279367de29641c5198a3b79fcb14ce5b723a4c3839d82` |
| Full-run serial result (`vm/results-full/serial-full.log`) | `784fb9ebf779465a459be79437e21ffd62133f0c9f5d5d9aa37f28999e1ed59e` |
| Final-run serial result (`vm/results-final/serial-final.log`) | `288bfff298f2801899f0bda0011f13c17a0673bdc788790a3b47b5d727b98a02` |
| Run test ISO for the 873-run (`sentinel-test-run.iso`) | `e635dd55294a6f2414e055154a1e7cca89f8ac579967f4da47946ff3e7241dbe` |
| Final snapshot-boundary test ISO | `510544e123736192be818ab0658e4a60833fb2c4e439c34223385269ec9979c5` |
| Latest install/teardown test ISO | `9bd059702e885590c9d16df1361d8da53dff86ca006c8a074257ff5a5ce153e2` |
| Latest self-contained agent binary | `85e1fbc9ca5a2b15ce26e514df81cd0ed97332ed50014e2b6de35367d525dc77` |
| Current Sysmon profile | `da778a7639ab26df80924c24c79930e5df1fbde6d3716b462979995abd6a6949` |

`vm/results-live2/`, `vm/results-final-task/`, `vm/results-full/`, and the VM images are intentionally ignored by Git. A six-event golden subset from the live capture is sanitized by selection only (laboratory host, PIDs, paths, and GUIDs remain) in [`fixtures/sysmon-v01-live/`](../fixtures/sysmon-v01-live/); its regression test parses the real XML.

## What this proves

- The built-in Sysmon feature can be enabled and configured on this image, the service starts, and the reader consumes the live channel.
- The serialized bookmark and config generation survive in the store; the retained-store run resumed from the existing bookmark, and a later clean-data path is implemented but not measured here.
- Event ID 1/3 map to OCSF, source-health records are retained, and the schema verifier reports no structural errors.
- The two single-event process rules fire on controlled live activity; replay under the same rules matches.
- Live Event ID 3 without independent outcome evidence stays Unknown; the strict policy is observable, not just documented.
- The `MaxEventRecordId` snapshot boundary and bookmark resume prevent the reader from following the live tail to the 5000 cap; the latest run accepted 600 records from its 1,416-record start snapshot.
- The installer created a non-admin reader, restricted the channel ACL, started the scheduled task as that reader (117 stored events), denied an outsider query, and teardown restored the original channel ACL and user-rights policy.
- The SATA result volume now receives the database, capture, installed-task database, and logs; the latest FAT image was extracted and host-verified.
- The controlled probe now reliably observes a successful loopback listener/connect and a refused connection; strict Event ID 3 mapping still requires independent ground truth.

## Limits

- The main live reader still ran as a local administrator; the least-privileged path was exercised only through the installed task, not the main harness's direct reader.
- The installed task was started with `Start-ScheduledTask`/`schtasks /Run`; no reboot-resume run was performed. The task stores the reader password and depends on the local batch-logon right; both are lab compromises.
- The latest main store retained prior events; the clean-data clear path was added after this run, so a clean first-enrollment measurement is still open.
- The installed task's console log was empty; its database is the evidence. No task crash/restart or reboot recovery is measured.
- The VM uses BIOS with a TPM/Secure Boot bypass and a disposable local credential; it is not a production baseline.
- No retention cap, purge, backup/restore, export, or investigation-run record is implemented yet.
- One build, one VM, one boot configuration; no independent hardware, performance, or reference-data calibration.
- The historical 873/1573-run DB/capture were not extracted; only their serial results are retained. The latest and 300-record DBs/captures were host-verified.
- No claim of complete Sysmon coverage, false-positive/negative rate, network outcome inference, or legal evidence.

## Next

1. Re-run the harness with the clean-data clear path and then perform one reboot-resume test; compare the task's bookmark/generation before and after reboot.
2. Measure reader/task overhead and dataset growth at a fixed event rate; add the result to the release notes.
3. Implement the V0.2 retention/backup slice described in [V0.2 design notes](v02-plan.md): bounded retention, tombstones, online backup, and restore/corruption tests.
4. Add explicit multi-sensor enrollment identity and rule-pack pinning before any fleet work.
