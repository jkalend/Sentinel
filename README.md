# Sentinel

> Local-first security telemetry and detection: collect, normalize, detect, and investigate.

**Status: planning. No implementation yet.**

Canonical design: [sentinel](https://github.com/jkalend/project-planning/blob/main/projects/sentinel.md)

## What it is

Sentinel is the sensor and detection half of DataSentinel and the host-security layer of PersonalMesh. It treats the event model, schema normalization, and evidence chain as the product.

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

## Ecosystem relationships

| Relationship | Projects |
|---|---|
| Consumers | DataSentinel, PersonalMesh, ForgeCloud, OpsMesh |
| Depends on | C-EVENT, P-SCHEMAFORGE, P-DETECTA, P-SABLE, P-CASEBOX, E-QUARRY |
| Shared additions | Schemaforge, Detecta, Lens, Sable, Palisade, Sketel, Casebox, Rigel, Meridian |

Ecosystems are separate repositories. This repository owns its implementation, tests, releases, and versioning; superplatforms consume it as a pinned dependency.

## Planned repository layout

```text
Sentinel/
├─ agents/windows/
├─ agents/linux/
├─ crates/
├─ rules/
├─ schemas/
├─ tests/
├─ docs/
└─ README.md
```

## First demo (planned)

```bash
sentinel-agent install --profile developer
sentinelctl rules apply rules/encoded-powershell.yaml
sentinelctl query "select process.name, net.dst_ip from events where host='ws1' and time > now()-1h"
sentinelctl case open --alert latest
```

## Non-goals

- Full endpoint containment/response in V0
- Enterprise multi-tenancy
- Custom protocol parsers before OCSF works
- Storing raw payloads forever by default

## Planning and research

- [Ecosystem design](https://github.com/jkalend/project-planning/blob/main/projects/sentinel.md)
- [DataSentinel](https://github.com/jkalend/project-planning/blob/main/platforms/datasentinel.md)
- [PersonalMesh](https://github.com/jkalend/project-planning/blob/main/platforms/personalmesh.md)
- [Additions catalog](https://github.com/jkalend/project-planning/blob/main/components/additions-catalog.md)
- [DataSentinel research](https://github.com/jkalend/project-planning/blob/main/research/datasentinel-research.md)
- [Confidence assessment](https://github.com/jkalend/project-planning/blob/main/research/research-quality-2026-10-18.md)

## License

Private. All rights reserved until a license is chosen.
