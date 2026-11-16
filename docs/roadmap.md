# Sentinel roadmap

Version numbers are repo-wide (agent, CLI, packages, rules, schemas). `0.x`
means preview: the local event contract is frozen, operational behavior is not.
Each minor release is one capability slice with fixtures and explicit limits;
nothing is promoted from this document by implication.

Current release: **0.9.1** — V0.1–V0.9 fixture-validated slices: engine, release
tooling, V0.2 completion, event/evidence hardening, Sigma/retention, local fleet
config, and signed evidence handoff.

## 0.5 — V0.2 completion

**Implemented in 0.5.0; pack-hash fix in 0.5.1.** Redaction, signed backups,
and interrupted-restore recovery followed in 0.6.0. Bookmark-age retention is
not applicable while one active bookmark per source is stored. The core V0.2
design items from [v02-plan.md](v02-plan.md):

- Rule packs: manifest, content-addressed install, activation, rollback, and
  alert provenance (`pack_id`, pack hash, rule version, engine version). Pinned
  replay uses the activated pack, not the current directory.
- Fleet configuration: versioned config document covering sensor/host identity,
  rule-pack pin, retention policy, allowed collectors, and export policy; apply,
  history, rollback, and drift reporting. Apply is local and auditable; no
  control plane.
- Identity lifecycle: explicit sensor incarnation records, rotation, and
  revocation. Rotation starts a new coverage boundary and preserves old
  evidence; revocation stops new ingestion without rewriting history.
- Retention hardening: physical compaction (`VACUUM`) after purge, and explicit
  redaction/backup limits documented where still unimplemented.
- Multi-sensor fixture coverage: colliding source record IDs, rotation,
  reinstall, out-of-order arrival, and one sensor offline while another
  continues.

Exit evidence: fixture tests for install/activate/rollback and pinned replay;
config apply/rollback/drift; rotation/revocation; compaction after purge; no
silent fallback to a different pack or identity.

## 0.6 — Provider expansion

**Partially implemented in 0.6.0:** Sysmon Event ID 5 (process termination)
coverage, signed backup manifests, run-export redaction, and interrupted-restore
recovery. A second endpoint/network provider and the parser framework remain
gated.

- One new provider per release, starting with Linux (auditd or eBPF) or Windows
  event IDs beyond 1/3; each needs a provider/build matrix, field mapping,
  permission model, and loss tests.
- Network metadata source (flow summaries, not packet payloads).
- Parser framework for additional protocols, but only after the provider emits
  an OCSF-mappable event. No parser bypasses OCSF normalization or the raw
  reference.

## 0.7 — Detection and retention breadth

**Partially implemented in 0.7.0:** bounded Sigma subset importer and opt-in
extended raw retention. Full Sigma conformance, encryption at rest, quota/audit
controls, and additional exports remain open.

- Sigma importer with per-modifier and correlation conformance, pinned
  converter/pipeline versions, per-rule license/attribution retention, and a
  truth table. "Full Sigma compatibility" remains a non-claim until every
  supported construct is conformance-tested.
- Extended retention mode: explicit opt-in, encrypted at rest, quota/audit
  controlled, and never the default. Indefinite raw retention requires a
  deliberate policy and local key/access decision.
- Additional export integrations, each with an explicit evidence/custody limit.

## 0.8 — Fleet and enterprise integration

**Partially implemented in 0.8.0:** local multi-store config apply/rollback/
drift and tenant labels in config and fleet output. Tenant isolation, SSO,
remote rollout, and control-plane behavior remain external.

- Multi-tenant/fleet control-plane integration for environments that need it.
  Isolation, SSO, and tenant policy belong to the consuming platform unless a
  new decision assigns them here; Sentinel remains the local engine.
- Larger fleet comparison/rollout surfaces, still without autonomous update or
  remote shell.

## 0.9 — External custody and signing

**Partially implemented in 0.9.0; review fixes in 0.9.1:** ECDSA-signed run manifests and pinned
custody references. WORM enforcement, external custody verification, and key
lifecycle remain external.

- Signed exports and external WORM/custody integration through Casebox, Sable,
  or an operator-provided archive. Sentinel records hashes, signatures, and
  custody references; it does not claim legal admissibility, tamper-proof
  storage, or a complete key lifecycle.

## Not Sentinel releases

| Item | Owner / disposition |
|---|---|
| Endpoint containment/response (isolate host, kill process, block network) | OpsMesh action lifecycle, exposed through an adapter. Not a Sentinel capability ([D-0015](../../../project-planning/decisions/0015-sentinel-engine-ownership.md)). |
| Enterprise multi-tenancy as a core property | Consuming platform/control plane; Sentinel can carry tenant identifiers only through a new accepted decision. |
| WORM/legal-custody claims | External custody integration (0.9); no admissibility claim at any version. |
| Full Sigma compatibility | 0.7 importer is a tested subset; full compatibility is not promised. |
| Custom protocol parsers before OCSF | 0.6 parser framework requires OCSF mapping first; no parser shortcut. |
| Indefinite raw retention by default | 0.7 opt-in mode only; default remains bounded. |

## Gates

Every version starts only with a named consumer or lab slice, fixtures,
permission/privacy review, and a written limit list. Ownership changes require a
new decision. The parked enforcement options remain parked; containment stays
outside this repository.
