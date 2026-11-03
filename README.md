# Sentinel

> Local-first security telemetry and detection: collect, normalize, detect, and investigate.

**Status: V0.1 live-source lab pass; V0.2 clean-store + reboot-resume slice pass.** A disposable Windows 11 Enterprise Evaluation 26H2 VM (build 26300, QEMU/KVM, built-in Sysmon) now runs the V0.2 first slice end to end. The latest harness cleared `data`/`capture` and returned `ok:true`: 2,239 clean-store events, 8 alerts, 0 gaps, 0 schema errors, replay 8/8, and a 2,317-file SATA capture. The installed `sentinel-reader` task stored 143 events before reboot and 419 after, resumed the same sensor incarnation/bookmark, remained Running, and was torn down with the channel ACL and user rights restored. Earlier V0.1 evidence includes the 650-record retained-store run, the 1,428-event store, and the reader/task/installer checks. Retention/backup, multi-sensor identity, rule packs, fleet configuration, and overhead measurements remain open. Engine ownership and the C#-only first artifact are decided ([D-0015](https://github.com/jkalend/project-planning/blob/main/decisions/0015-sentinel-engine-ownership.md), [D-0014](https://github.com/jkalend/project-planning/blob/main/decisions/0014-language-artifact-policy.md)).

Canonical design: [sentinel](https://github.com/jkalend/project-planning/blob/main/projects/sentinel.md)

## What it is

Sentinel is the sensor and detection half of DataSentinel and the host-security layer of PersonalMesh. It treats the event model, schema normalization, and evidence chain as the product.

## Language

V0.1 is one C# artifact: the Windows Sysmon/Event Log collector, OCSF mapper, rule evaluator, SQLite store, and timeline. There is no Rust, Go, or Java source in this repository. A future Linux or network sensor would be a separate artifact with its own named slice, conformance fixtures, and process/file seam; no mixed-language solution or FFI is planned.

## Subprojects

- Sentinel Agent
- Sentinel Linux
- Sentinel Network
- Sentinel Stream
- Sentinel Store
- Sentinel Rules
- Sentinel Inventory
- Sentinel Lab
- Sentinel Intelligence
- Sentinel Console

## First useful slice

Windows agent collects process and network events, normalizes them to OCSF, evaluates three detection rules, and renders an investigation timeline with process ancestry.

Implemented against synthetic Event Log XML: Event ID 1/3 parsing, OCSF 1.9.0 mapping, `sentinel.local-event/v1` SQLite persistence with atomic event/bookmark commit, gap state, three versioned rules, replay parity, and a `ProcessGuid` timeline. The live Windows reader passed one disposable-VM lab run; see "Live collection" below.

## Ecosystem relationships

| Relationship | Projects |
|---|---|
| Consumers | DataSentinel, PersonalMesh, ForgeCloud, OpsMesh |
| Depends on | C-EVENT, P-SCHEMAFORGE, P-DETECTA, P-SABLE, P-CASEBOX, E-QUARRY |
| Shared additions | Schemaforge, Detecta, Lens, Sable, Palisade, Sketel, Casebox, Rigel, Meridian |

Ecosystems are separate repositories. This repository owns its implementation, tests, releases, and versioning; superplatforms consume it as a pinned dependency.

## Components

Component libraries live in [`Components/`](../../Components/README.md), one workspace package per component ([D-0012](../../project-planning/decisions/0012-component-repositories.md)). This repository consumes them as pinned dependencies and contains no component source.

| Package | Component | Spec |
|---|---|---|
| `backfire` | Backfire | [spec](../../project-planning/components/backfire.md) |
| `bailiff` | Bailiff | [spec](../../project-planning/components/bailiff.md) |
| `casebox` | Casebox | [spec](../../project-planning/components/casebox.md) |
| `detecta` | Detecta | [spec](../../project-planning/components/detecta.md) |
| `lens` | Lens | [spec](../../project-planning/components/lens.md) |
| `palisade` | Palisade | [spec](../../project-planning/components/palisade.md) |
| `sable` | Sable | [spec](../../project-planning/components/sable.md) |
| `schemaforge` | Schemaforge | [spec](../../project-planning/components/schemaforge.md) |
| `sketel` | Sketel | [spec](../../project-planning/components/sketel.md) |
| `teller` | Teller | [spec](../../project-planning/components/teller.md) |

V0.1 imports none of these packages; the mapper, rule evaluator, and store are in-repository C# modules until a D-0012 extraction trigger is met.


## Repository layout

```text
Sentinel/
├─ src/
│  ├─ Sentinel.Core/          mapper, rules, store, replay, timeline, agent orchestration
│  ├─ Sentinel.Windows/       Windows Event Log reader (Windows-only)
│  ├─ sentinelctl/            fixture ingest, timeline, replay, gaps, verify
│  └─ sentinel-agent/         live run and XML capture (Windows-only)
├─ rules/                     three versioned YAML rules
├─ schemas/ocsf-1.9.0/        pinned OCSF classes and required objects
├─ fixtures/sysmon-v01/       synthetic Sysmon XML + ground truth
├─ fixtures/sysmon-v01-live/  selected raw XML from the lab capture
├─ tests/Sentinel.Core.Tests/
├─ scripts/                   Sysmon profile, probes, local install/uninstall
├─ vm/                        unattended Windows lab harness (QEMU/BIOS)
├─ docs/local-event-v1.md     frozen local contract
├─ docs/v01-windows-vm-runbook.md
├─ docs/v02-plan.md           retention, multi-sensor, rule packs, fleet config
└─ README.md
```

Linux and network agents are gated later providers; their source would be a separate artifact, not a directory mixed into the C# solution.

## First demo (verified on fixture data)

```bash
dotnet test
dotnet run --project src/sentinelctl -- ingest --fixture fixtures/sysmon-v01 --db data/sentinel.db
dotnet run --project src/sentinelctl -- timeline --db data/sentinel.db
dotnet run --project src/sentinelctl -- replay --db data/sentinel.db
dotnet run --project src/sentinelctl -- verify --db data/sentinel.db
```

The fixture contains 10 records: 3 alerts, 1 source-health record, 0 gaps. Re-ingesting the same fixture reports 10 duplicates and leaves the event, alert, and gap counts unchanged. `verify` recomputes each raw XML hash and validates the stored OCSF event against the pinned 1.9.0 schema requirements.

## Install (local Windows)

After Sysmon is installed and the reviewed profile is applied, an elevated
PowerShell session can provision the reader and task:

```powershell
$pw = Read-Host -AsSecureString
.\scripts\install-sentinel-agent.ps1 -SourceRoot .\deploy -ReaderPassword $pw -Start
```

`deploy` must contain `agent/`, `ctl/`, `rules/`, and `schemas/ocsf-1.9.0/`.
The installer creates the local `sentinel-reader`, restricts the
`Microsoft-Windows-Sysmon/Operational` channel to Administrators/SYSTEM/reader,
grants the reader modify access only under `data`/`capture`, and registers a
startup scheduled task and grants the reader the local batch-logon right needed
for a non-interactive start. The task stores the reader password; treat it as a
local secret. Data is preserved when uninstalling unless `-RemoveData` is
explicit, and teardown restores the saved channel ACL and user-rights policy:

```powershell
.\scripts\uninstall-sentinel-agent.ps1 -RemoveReader
```

The scripts are in [`scripts/`](scripts/); the full manual procedure remains in
[the V0.1 Windows runbook](docs/v01-windows-vm-runbook.md).

## Live collection (lab-validated; reader ACL/installer exercised)

`sentinel-agent` reads the Sysmon Operational channel through `EventLogReader`, starts first enrollment at the oldest retained record, and persists the serialized Event Log bookmark in the same SQLite transaction as the event. On Windows:

```powershell
dotnet run --project src/sentinel-agent -- channel
dotnet run --project src/sentinel-agent -- run --db data/live.db --max-records 20
dotnet run --project src/sentinel-agent -- capture --output data/capture --max-records 1000
dotnet run --project src/sentinelctl -- status --db data/live.db
```

The reader, bookmark resume, channel configuration capture, config-generation handling, live rule evaluation, install/teardown, reboot-resume, and a least-privileged reader task are exercised on a disposable Windows 11 Enterprise Evaluation 26H2 VM. The unattended lab harness is in [`vm/`](vm/README.md): it boots the VM under QEMU/KVM with SeaBIOS, applies `autounattend.xml`, installs and configures built-in Sysmon, runs the live reader/probes, provisions the reader task with [`scripts/install-sentinel-agent.ps1`](scripts/install-sentinel-agent.ps1), checks an outsider denial, reboots, verifies bookmark/event continuity, and tears it down. Results go to the SATA FAT volume and COM1. The clean-store/reboot evidence is in [V0.2 reboot-resume validation](docs/v02-reboot-resume.md); V0.1 evidence and limits are in [V0.1 live validation](docs/v01-live-validation.md); the manual procedure is in [the V0.1 Windows VM runbook](docs/v01-windows-vm-runbook.md). The selected real events are in [`fixtures/sysmon-v01-live/`](fixtures/sysmon-v01-live/), and the next bounded problems are in [V0.2 design notes](docs/v02-plan.md).

**Strict Event ID 3:** Sysmon Event ID 3 has no success field. Live records without independently observed ground truth stay OCSF `activity_id=0` (Unknown); only a controlled, confirmed outbound TCP connection maps to `Open`. The network rule therefore does not fire on unproven live traffic.

Query and case commands are not part of V0.1; incident state belongs to OpsMesh.

## Non-goals

- Full endpoint containment/response in V0
- Enterprise multi-tenancy
- Custom protocol parsers before OCSF works
- Storing raw payloads forever by default

## Secondary direction (parked, non-priority)

Bespoke enforcement — host firewall, antivirus, EDR containment, IDS/IPS, WAF, packet filters, kernel drivers — is recorded in the [canonical plan](https://github.com/jkalend/project-planning/blob/main/projects/sentinel.md#secondary-bespoke-control-plane-options-parked-non-priority) as secondary, low-importance exploration. It is not part of V0.1, has no milestone, dependency, or release gate, and would be separate artifacts if ever pursued. Sentinel remains the detection/evidence engine; response belongs to OpsMesh. Native controls and adapters are preferred over replacement engines.

## Planning and research

- [Local V0.2 design notes](docs/v02-plan.md)
- [Ecosystem design](https://github.com/jkalend/project-planning/blob/main/projects/sentinel.md)
- [Engine ownership decision](https://github.com/jkalend/project-planning/blob/main/decisions/0015-sentinel-engine-ownership.md)
- [DataSentinel](https://github.com/jkalend/project-planning/blob/main/platforms/datasentinel.md)
- [PersonalMesh](https://github.com/jkalend/project-planning/blob/main/platforms/personalmesh.md)
- [Additions catalog](https://github.com/jkalend/project-planning/blob/main/components/additions-catalog.md)
- [DataSentinel research](https://github.com/jkalend/project-planning/blob/main/research/datasentinel-research.md)
- [Confidence assessment](https://github.com/jkalend/project-planning/blob/main/research/research-quality-2026-10-18.md)

## License

Licensed under the Apache License, Version 2.0. See [LICENSE](LICENSE).

Copyright 2026 jkalend. Third-party dependencies retain their own licenses.
