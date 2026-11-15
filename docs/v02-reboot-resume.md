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
| Main store | **2,710 events**; 14 alerts; 0 gaps |
| Clean start | 50-event first run + 2,660-event resume; 0 duplicates; generation 8 |
| Host verify | 0 schema errors |
| Host replay | 14 live / 14 replay, matches |
| Capture | 2,972 XML files on the SATA result volume |
| Installed task before reboot | 12 events; bookmark record 12, generation 2; Running |
| Installed task after reboot | 308 events at check (310 final); bookmark record 308; Running |
| Continuity | +296 events at check (+298 final); same incarnation `5a960400-001a-461a-b8fc-d862f38eb07a` |
| Resume task last result | `267009` (task running) |
| Installed verify after reboot | 0 schema errors |
| Teardown | channel ACL, user rights, reader, agent task, resume task, and install restored/removed |
| Final harness result | **`ok:true`** |

The main store was cleared before the run, so the 2,710 events are from one
clean enrollment rather than a retained database. `main-result.json` is the
pre-reboot snapshot and intentionally has `ok:false`; the authoritative final
result is `result.json` from the resume phase. The installed database grew by
two more rows between the 308-event continuation check and teardown, while the
agent was still running before removal; the continuity assertions use the
308-event checkpoint.

## Evidence

| Artifact | SHA-256 |
|---|---|
| Final result JSON (`vm/results-v02-reboot2/result.json`) | `f96d6d98814fd0a40dd9acb227f74fa98ee9083616201586911310e548baae03` |
| Main database (`vm/results-v02-reboot2/DATA/SENTINEL.DB`) | `6311a025df0313656e44ed93a4f1512926d8eed194d7843e54eadcb93f6198e2` |
| Installed-task database (`vm/results-v02-reboot2/DATA/installed.db`) | `afe0146e0ea1ef1a31f76760fda980ac2bb50c3c390878145b267d712f2f1f0c` |
| Pre-reboot main snapshot (`vm/results-v02-reboot2/main-result.json`) | `1fdd4d32714d2bfd9b231995f8727f67e953b97fe8a1e2bdf87273971c587507` |
| Pre-reboot state (`vm/results-v02-reboot2/reboot-state.json`) | `d7842b9e378834c627c57fd17262883872927764286110ee0af4ae2cca56a7e2` |
| Capture manifest (`vm/results-v02-reboot2/CAPTURE/fixture.json`) | `7098886dd380b13224fbc82080427154c204c048c669510cd57f0a211f874d31` |
| Test ISO | `76165560236b46f0522b8b99f1edeee07a6103d22e6d48696a71a0ac6d37be64` |
| `sentinelctl` binary | `632ec4e749e7543b4d65c3f03db309403a87390cdb3a0a20f25b3668067713f6` |
| `sentinel-agent` binary | `e257fa9af83a4d0164e3c73b358ba6edd81853c60787a8254d9a9b43bddc750e` |
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
- `SentinelResume` unregisters itself at the start of the continuation run;
  the final result records `resume_task_removed:true`. The continuation still
  needs an operator- or policy-created startup task for a real reboot, and the
  harness verifies removal only on this disposable image.
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
