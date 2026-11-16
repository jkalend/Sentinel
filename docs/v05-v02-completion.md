# V0.5: V0.2 completion

**Status: implemented; fixture-verified. Live installer and VM checks remain
pending.** This slice closes the core V0.2 design items: rule packs, fleet
configuration, identity lifecycle, and compaction. It adds no remote control
plane.

## Rule packs

A rule pack is a directory with `manifest.json` and the YAML files it lists:

```text
manifest.json
encoded-powershell.yaml
script-host-nonstandard-egress.yaml
temp-executable.yaml
```

The manifest records `format: sentinel.rule-pack/v1`, pack id/version, engine and
OCSF compatibility, creation time, and each rule's id, version, file name, and
SHA-256. The pack hash is the canonical hash of the manifest excluding
`created_at`, so rebuilding unchanged rules keeps the same hash. Loading verifies
every file hash, duplicate/unlisted YAML files, and the rule id/version declared
inside each file.

```powershell
sentinelctl rule-pack-build    --rules rules --id sentinel.base --version 1 --output packs/base-1
sentinelctl rule-pack-install  --pack packs/base-1 --db data/sentinel.db
sentinelctl rule-pack-activate --pack sentinel.base@1 --db data/sentinel.db
sentinelctl rule-pack-rollback --db data/sentinel.db
sentinelctl rule-pack-list     --db data/sentinel.db
sentinelctl rule-pack-show     --pack sentinel.base@1 --db data/sentinel.db
```

The SQLite store keeps pack contents, install source, activation history, and the
active pack. Alerts record `pack_id` and `pack_sha256`; run manifests pin both.
`replay --pack <ref>` and `run --pack <ref>` use that pack rather than the
current directory. The live agent uses an explicit `--pack`, else the active
config pin, else the active pack, else `--rules`.

Packs are local and unsigned in this slice. There is no signature check or
remote distribution.

## Fleet configuration

A config document is versioned JSON:

```json
{
  "format": "sentinel.fleet-config/v1",
  "config_id": "local-default",
  "sensor_id": "<uuid>",
  "host_id": "<uuid>",
  "rule_pack_sha256": "<pack hash or null>",
  "retention": {
    "event_days": 30,
    "raw_days": 7,
    "alert_days": 90,
    "source_health_days": 90,
    "gap_days": 90,
    "keep_events": 1000,
    "max_bytes": 10737418240
  },
  "allowed_collectors": ["http://127.0.0.1:18099/"]
}
```

```powershell
sentinelctl config-apply    --db data/sentinel.db --file config.json
sentinelctl config-show     --db data/sentinel.db
sentinelctl config-history  --db data/sentinel.db
sentinelctl config-rollback --db data/sentinel.db
sentinelctl config-drift    --db data/sentinel.db
```

Apply validates format, retention, sensor/host against enrollment, installed
rule pack, and absolute http(s) collectors. Re-applying the same canonical
document is idempotent. Rollback writes a new generation containing the previous
document; history is never rewritten. Applying a rule-pack pin activates that
pack. Drift reports identity mismatch or a missing pinned pack.

Enforcement in this slice:

- `purge` uses the active retention policy when no retention flag is supplied.
- `push` rejects a collector outside `allowed_collectors`.
- `run`, `replay`, and the live agent use the pinned rule pack.
- `sentinel-agent` uses the active config generation for a new bookmark.

Config documents are local and unsigned. No remote apply, signature check, or
autonomous update exists.

## Identity lifecycle

`sensor_incarnations` records every incarnation with sensor/host, creation,
optional end, revocation, and reason. The installer mints or reuses
`data/identity.json` and passes `--sensor-id`/`--host-id` to the agent; explicit
`-SensorId`/`-HostId` override it. Reinstalling without `-RemoveData` keeps the
identity.

```powershell
sentinelctl identity-list   --db data/sentinel.db
sentinelctl identity-rotate --db data/sentinel.db --reason "planned rotation"
sentinelctl identity-revoke --db data/sentinel.db --incarnation <uuid> --reason "lost host"
```

Rotation closes the previous incarnation, creates a new one, and moves the
bookmark to the new incarnation without re-reading retained records. Revocation
marks the incarnation unusable; the agent refuses to start or resume it.
Existing events and alerts are not deleted.

## Retention compaction

`purge --apply --compact` runs SQLite `VACUUM` after the audited purge. The byte
cap remains an estimated-payload limit; compaction does not make the file
smaller than its live pages and does not change retention eligibility.

## Schema changes

Additive only: alert `pack_id`/`pack_sha256` columns; `rule_packs`,
`rule_pack_activations`, `config_generations`, and `sensor_incarnations` tables.
`Initialize` applies the additive columns to existing databases.

## Limits

- Rule packs and config documents are unsigned; local trust only.
- Redaction lists, signed backup manifests, bookmark-age retention, and
  crash-injection restore remain unimplemented.
- No remote config distribution, multi-tenant control plane, or response action.
- Installer identity/task changes and the VM harness are not live-executed in
  this slice; fixture and parser checks only.

## Verification

```powershell
dotnet build Sentinel.slnx -c Release
dotnet test Sentinel.slnx -c Release
```

Fixture coverage includes pack hash stability, tamper rejection, activation/
rollback, alert and run provenance, config apply/rollback/drift, collector
allowlist, incarnation rotation/revocation, revoked-agent refusal, and compaction
counts/size. The full suite passes 116 tests in this slice.
