# V0.2 first slice: clean store and reboot-resume

**Status: lab pass, 2026-11-03.** The V0.1 harness was extended with an explicit
pre-reboot snapshot and a SYSTEM continuation task. It now proves that the
installed reader task survives a reboot, resumes from its persisted bookmark,
stores new events, and can still be torn down cleanly.

## What changed

- `sentinelctl status --db <path>` reports event, alert, gap, and bookmark
  state without rendering the full timeline.
- The harness clears `C:\sentinel\data` and `C:\sentinel\capture` before a run.
- After installing `SentinelAgent` as `sentinel-reader`, the harness records
  the reader database count/bookmark, stops the task, registers `SentinelResume`
  as `SYSTEM` at startup, and reboots.
- On boot, `SentinelAgent` starts as the reader again. `SentinelResume` waits
  for the task and an event-count increase, generates one controlled process,
  verifies the database, copies the installed DB, tears down the agent, and
  writes the result.
- `-NoReboot` retains the older direct install/teardown path for diagnostics.

## Results

| Check | Result |
|---|---|
| Main store | **2,239 events**; 8 alerts; 0 gaps |
| Clean start | 50-event first run + 2,189-event resume; 0 duplicates; generation 5 |
| Host verify | 0 schema errors |
| Host replay | 8 live / 8 replay, matches |
| Capture | 2,317 XML files on the SATA result volume |
| Installed task before reboot | 143 events; bookmark record 143, generation 2; Running |
| Installed task after reboot | 414 events at check (419 final); bookmark record 417; Running |
| Continuity | +271 events at check (+276 final); same incarnation `0ddc986c-0fed-4799-8c0c-f11391251316` |
| Resume task last result | `267009` (task running) |
| Installed verify after reboot | 0 schema errors |
| Teardown | channel ACL, user rights, reader, task, and install restored/removed |
| Final harness result | **`ok:true`** |

The main store was cleared before the run, so the 2,239 events are from one
clean enrollment rather than a retained database. `main-result.json` is the
pre-reboot snapshot and intentionally has `ok:false`; the authoritative final
result is `result.json` from the resume phase.

## Evidence

| Artifact | SHA-256 |
|---|---|
| Final result JSON (`vm/results-v02-reboot/result.json`) | `a5c2ea9555587b80eb3514a1b2938c86a0ff36470c43916c4556dadaa5b6982f` |
| Main database (`vm/results-v02-reboot/DATA/SENTINEL.DB`) | `43bb5d27a7744e624705af842e80838c98061b663eaf0ddcc6cf37089784f5f3` |
| Installed-task database (`vm/results-v02-reboot/DATA/installed.db`) | `36381b4c6095dac9074feed5f55e06d0214320759c85dc653d141230443741c6` |
| Pre-reboot main snapshot (`vm/results-v02-reboot/main-result.json`) | `e3e0b996c7813b0d1ed9dc2c038370540a97ff0c9d4f15e50953a36008988aac` |
| Pre-reboot state (`vm/results-v02-reboot/reboot-state.json`) | `f36b91209d8714a0236cb691805a78bd5134c1d296b5e14d7a9fe3755a7d8e0f` |
| Capture manifest (`vm/results-v02-reboot/CAPTURE/fixture.json`) | `0dfb50d1392ee2d0c6a026dd6eee3a04594c6a793daa4a18c111572fce95d306` |
| Test ISO | `1e3b8d07ff92f9001ab9619106324dbe1248f1f1ad48bba2afde1d2bbfc0d023` |
| `sentinelctl` binary | `9aac8265b33612b249a62f1e67d2462e96cee08e59bc1e03193ee9fee292031a` |
| `sentinel-agent` binary | `85e1fbc9ca5a2b15ce26e514df81cd0ed97332ed50014e2b6de35367d525dc77` |
| Sysmon profile | `da778a7639ab26df80924c24c79930e5df1fbde6d3716b462979995abd6a6949` |

The result directories and test media are intentionally ignored by Git.

## What this proves

- A clean harness run starts without prior event rows or capture files.
- The installed `sentinel-reader` task can be stopped, rebooted, and restarted
  by Task Scheduler under the reader identity.
- The persisted Event Log bookmark survives the reboot and the same sensor
  incarnation continues; new boot/process activity is appended without a
  new enrollment.
- The reader task can still be verified and removed after the reboot, restoring
  the channel ACL and user-rights policy.
- `sentinelctl status` is usable while the agent is running and exposes the
  count/bookmark needed by the lifecycle test.

## Limits

- The resumed check waits for task running plus event growth; it does not yet
  assert exact losslessness against the Event Log's record IDs beyond the
  bookmark and schema verification.
- `SentinelResume` remains registered in the disposable VM; the VM shuts down
  after the check. A production lifecycle must remove it or use a one-shot
  self-deleting continuation.
- The continuation runs as SYSTEM, not under the reader account; it is a test
  driver, not part of the installed agent.
- The installed agent still stores the reader password and depends on the local
  batch-logon right; a service installation remains future work.
- The VM is disposable BIOS/QEMU with a lab credential; no independent hardware,
  performance, retention, or fleet claim is made.

## Next

1. Retention/purge/backup on the clean-store slice.
2. Explicit multi-sensor enrollment identity and per-sensor sequences.
3. Rule-pack packaging and pinned replay.
4. Local configuration/apply/rollback; fleet transport remains gated.
5. Overhead and dataset-growth measurements.
