# V0.7: Sigma subset and extended retention

**Status: implemented; fixture-verified.** Bounded scope: one Sigma importer
subset and one explicit long-retention mode.

## Sigma subset importer

```powershell
sentinelctl sigma-import --input sigma-rules --output rules-imported [--force]
sentinelctl rule-pack-build --rules rules-imported --id sigma.local --version 1 --output packs/sigma-1
```

Supported Sigma fields:

| Sigma | Sentinel |
|---|---|
| `Image` | `process.file.path` |
| `CommandLine` | `process.cmd_line` |
| `DestinationIp` / `DestinationPort` | `network.dst.ip` / `network.dst.port` |
| `SourceIp` / `SourcePort` | `network.src.ip` / `network.src.port` |
| `Protocol` | `network.protocol` |

Supported values: scalar equality, string list (`in`), and scalar/list
`contains`, `startswith`, `endswith` modifiers (`contains_any`,
`starts_with`, `ends_with` for lists). The detection condition must be exactly
`selection`. `|all`, `|re`, modifier chains, correlation forms, repeated mapped
fields, wildcard (`*`/`?`) values, CIDR IP values, and unmapped fields fail
closed.

`logsource.category` is required: `process_creation` → `event.id=1`,
`network_connection` → 3, `process_termination` → 5; other categories and
non-Windows products are rejected. The generated rule keeps the original Sigma
id, title, author, license, and logsource fields; the native evaluator ignores
attribution metadata but enforces the `event.id` source guard.

Imports require an empty output directory unless `--force`; duplicate output
names and existing targets fail closed. The generated rule keeps the original
Sigma id (prefixed `sigma.`). This is not full Sigma compatibility and does not
establish a false-positive rate.

## Extended retention

```powershell
sentinelctl purge --db data/sentinel.db --raw-days -1 --extended-retention --apply
```

`RawDays=-1` is accepted only with `ExtendedRetention=true`; config documents
carry the same `extended_retention` field. The raw cutoff becomes
`DateTimeOffset.MinValue`, so raw XML is not purged by age. Event, alert, health,
and gap retention still apply, and size retention still protects the newest
events. The mode is local and unencrypted; it is not WORM, legal custody, or an
indefinite-retention default.

## Limits

- A single selection only; no Sigma correlations, aggregations, or pipelines.
- No per-rule license enforcement or SigmaHQ corpus import.
- Extended retention has no at-rest encryption or external custody.
- No additional export integrations in this slice.
- Live VM and harness remain unrun.

## Verification

`dotnet test Sentinel.slnx -c Release` covers supported Sigma import/evaluation,
unsupported condition/field, wildcard, duplicate-field, and collision rejection,
logsource guards, list modifiers, and extended-retention validation plus raw
preservation. Full suite at this slice: 126 tests; after 0.9.1 fixes: 134.
