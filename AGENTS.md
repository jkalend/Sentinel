# Sentinel agent guide

## Start here

Read [README.md](README.md) and, when present, `../AGENTS.md` for workspace conventions. For feature or architecture work, read the [canonical design](https://github.com/jkalend/project-planning/blob/main/projects/sentinel.md), available locally at `../../project-planning/projects/sentinel.md`.

The repository contains a C# engine slice: `src/Sentinel.Core` (Sysmon XML reader, OCSF mapping, rule evaluator, SQLite store, replay, timeline, agent orchestration), `src/Sentinel.Windows` (live Event Log reader, Windows-only), `src/sentinelctl` (fixture CLI), `src/sentinel-agent` (live run/capture CLI), `tests/`, `rules/`, `fixtures/`, `scripts/` (including local install/uninstall), and pinned OCSF schemas under `schemas/ocsf-1.9.0`. Fixture commands are verified. The latest unattended Windows 11 Enterprise Evaluation 26H2 run returned `ok:true`: 650 accepted records across first/resume against a retained 1,428-event store, 6 alerts, 0 gaps, 0 schema errors, replay 6/6, a 1,448-file capture on the SATA result volume, and an installed `sentinel-reader` task that stored 117 events. The channel ACL, outsider denial, teardown, and user-rights restore passed. Reboot-resume, a clean-store rerun, retention/backup, and performance measurements remain open. C# is the only implementation language for the first artifact; Rust/Go/Java source does not belong here without a new accepted decision and a named provider slice.

## Scope and first slice

Sentinel owns security collection, normalization, detection, and investigation evidence. Quarry owns general analytical storage/query; response and host containment are outside V0.

Start with one Windows user-mode agent collecting process/network events, versioned OCSF normalization, three tested detection rules, and a timeline showing process ancestry and network context. Keep fleet management, custom protocol parsing, and advanced baselining behind that local flow.

## Design invariants

- Preserve sensor identity, local sequence, source timestamps, schema version, and a raw-evidence reference under an explicit retention/privacy policy. Validate normalization and preserve unmapped source semantics deliberately.
- Treat process identity as more than a PID: include host and process-start identity to survive PID reuse. Keep event time and ingest time distinct; handle late/out-of-order observations.
- Bound collection overhead, queues, retries, and storage. Expose drop counters, backpressure, and gaps; missing telemetry must not masquerade as absence of activity.
- Detection rules are versioned and tested. Alerts reference the exact rule version and contributing event/batch IDs. Replay evaluates the same rule semantics as live processing.
- Evidence bundles verify content hashes and chain of custody. Schema migrations and enrichment must not silently rewrite original evidence.
- Keep telemetry local by default with bounded retention and redaction. Raw payloads, command lines, and credentials require deliberate handling; synthetic fixtures are the default.
- Agent installation and collection lifecycle must be observable, reversible, and resource-capped. Develop against fixture sources before changing live host configuration.
- Store entity aliases and relationships simply until real queries justify a more complex graph representation.
- Component code lives in `../Components/`, not in this repository ([D-0012](../../project-planning/decisions/0012-component-repositories.md)); consume packages as pinned dependencies. An adapter that depends on this ecosystem belongs in the consuming superplatform shell.


## Validation priorities

- Replay benign and positive synthetic process/network scenarios for each of the three rules; assert ancestry, context, and evidence references, not just alert counts.
- Test malformed/schema-drifted events, PID reuse, duplicate delivery, late events, buffer exhaustion, and sink outage/recovery.
- Verify evidence tampering fails and retention/redaction does not leave broken or misleading references.
- Separate deterministic pipeline tests from opt-in Windows ETW/EventLog collection tests. Measure overhead and dataset size for throughput/query claims.

## Working and completion

Run Git and tooling from this repository and preserve unrelated changes. Discover commands from actual manifests/scripts; document required host permissions and real build/test/demo plus uninstall commands with the first agent. Keep real endpoint telemetry and evidence bundles outside Git. Report checks actually run, any live host changes, and gaps in platform/provider coverage.
