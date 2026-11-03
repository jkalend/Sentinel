# Sysmon V0.1 live capture fixture

Selected raw Sysmon events from the disposable Windows 11 Enterprise Evaluation
26H2 VM used for [V0.1 live validation](../../docs/v01-live-validation.md).
Source: `vm/results-reviewed/CAPTURE/EVENTS` from the 2026-11-03 run with
built-in Sysmon 4.91 and `scripts/sysmon-sentinel-v01.xml`.

The files are copied unchanged from the capture (only renamed). They contain
laboratory host names, paths, PIDs, and GUIDs from the disposable VM; no real
user data was collected.

| File | Event ID | Purpose |
|---|---|---|
| `process-create.xml` | 1 | process identity and command line |
| `network-outbound.xml` | 3 | initiated TCP connection |
| `network-inbound.xml` | 3 | non-initiated TCP connection |
| `process-terminate.xml` | 5 | unmapped source event retained as raw |
| `config-change.xml` | 16 | config generation/source health |
| `sysmon-error.xml` | 255 | Sysmon error retained as source health |
