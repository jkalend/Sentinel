# Sentinel V0.2 design notes

## Slice status

The first bounded slice, clean store plus reboot-resume, passed on 2026-11-03; see [V0.2 reboot-resume validation](v02-reboot-resume.md). The retention/backup first implementation passed fixture/host checks on 2026-11-04; see [retention/backup validation](v02-retention-backup.md). Size-cap physical compaction, crash-injection restore, and live-store purge remain open.

V0.1 is a single-host, local-store, file-rule slice. These notes define the
next bounded problems; they do not add fleet management, retention, or rule
packaging to V0.1.

## Retention, purge, and backup

Evidence must remain explainable after deletion. V0.1 has no retention and a
single SQLite file; that is acceptable only for bounded lab runs.

- **Policy classes.** Raw source XML, normalized events, alerts, source-health
  records, bookmarks, and capture exports get independent age/size limits.
  Defaults:
  - normalized events: 30 days or 10 GiB, whichever comes first;
  - raw XML: 7 days, or omitted when `--no-raw` is configured;
  - alerts: 90 days;
  - source-health and bookmarks: 90 days.
- **Delete only sealed history.** Purge operates on whole sequence ranges and
  never deletes the newest segment or the bookmark. Emit a `retention_purge`
  audit record (range, counts, policy hash, timestamp) before deleting. Keep a
  tombstone so a timeline can say "events 1–N purged by policy" rather than
  presenting a false gap.
- **No renumbering.** Sequence numbers remain monotonic. Deleting old rows must
  not rewrite the bookmark or reuse ids. Foreign keys from alerts to events are
  nullable/denormalized enough that an alert can survive its raw event.
- **Backup.** Use SQLite's online backup API or `VACUUM INTO` while the agent is
  running; never copy a live DB file. Write a manifest with schema version,
  SHA-256, row counts, and coverage range. Restore must run `PRAGMA
  integrity_check`, schema compatibility, and hash checks before opening the
  DB for writes. Test restore after a forced process kill and with a corrupted
  page; both should fail closed and preserve the original.
- **Purge verification.** Tests: delete at exact boundary, oversized DB, active
  bookmark, alert referencing a purged event, interrupted purge, and backup
  restore. Disk-space exhaustion is a first-class failure, not best effort.
- **Redaction.** V0.2 keeps the V0.1 rule that command lines are evidence, not
  secrets; provide an explicit `--redact` list and record that redaction
  happened. Never silently rewrite raw XML.

## Multi-sensor identity

A sensor id is not a hostname and a host id is not a sensor id.

- **Identity tuple.** `(sensor_id, host_id, incarnation_id, config_generation)`.
  `sensor_id` is an enrollment identifier minted by the local installer and
  persisted; `host_id` is a stable machine identity; `incarnation_id` changes
  on reinstall or explicit reset. V0.1 currently derives host/sensor values from
  the store; V0.2 makes them explicit enrollment fields.
- **Sequence scope.** Sequence is per sensor incarnation. Never merge sequence
  numbers across sensors, and never use sequence as a global event id. The
  dedup key remains `(sensor_id, incarnation_id, channel, event_record_id)`.
- **Source health is per sensor.** A gap or last-record timestamp from one
  sensor must not mask another. A merged investigation shows source coverage
  per sensor and never interpolates across a missing sensor.
- **Clock and ordering.** Event time is source metadata; ingest order is not
  event order. Keep `time_source`, `processed_utc`, and the raw timestamp
  separately. V0.2 sorts timelines by event time with a deterministic tie break
  and exposes clock-skew warnings.
- **Enrollment and rotation.** Enrollment is a local command with an operator
  confirmation; sensor id rotation is an explicit new incarnation, not an
  overwrite. Revocation must stop ingestion but preserve old evidence.
- **Tests.** Two sensors with colliding event-record ids, replayed sensor,
  reinstalled host, out-of-order arrival, and one sensor offline while the
  other continues.

## Rule packaging and versioning

V0.1 loads one YAML directory at startup. V0.2 introduces a package boundary
so rules can be reviewed, tested, pinned, and rolled back.

- **Package contents.** A rule pack is a versioned directory/archive:
  `manifest.json` (pack id, semantic version, engine compatibility, OCSF
  contract, created-by, SHA-256), `rules/*.yaml`, `fixtures/`, and `tests/`.
  The manifest is canonicalized before hashing.
- **Immutability.** Installing a pack adds a content-addressed version; it does
  not mutate an existing version. Activation binds the agent's
  `config_generation` to the pack id/version/hash. Rollback is an explicit
  activation of the previous version.
- **Evaluation provenance.** Every alert records pack id, rule id, rule
  version, pack hash, engine version, and evaluation generation. Replay uses
  the pinned pack, not "whatever is in the directory now".
- **Test gate.** A pack is installable only if its own fixtures pass and its
  rules parse against the declared OCSF contract. The repository's three V0.1
  rules become the first pack.
- **Signing.** V0.2 local mode may use a local signing key; distribution later
  verifies signatures. Never auto-enable an unsigned remote pack.
- **Tests.** Duplicate rule ids across packs, missing fixture, incompatible
  engine, tampered pack, activation/rollback, and replay after rollback.

## Fleet configuration

V0.1 configuration is the command line. V0.2 needs a small, auditable
configuration document for one host or a handful of hosts; it does not need a
control plane or multi-tenant SaaS.

- **Config document.** Versioned JSON: enrolled sensor identity, selected
  channels/profile hash, Sysmon config hash, rule pack pins, retention policy,
  capture/export policy, and allowed local endpoints. It is signed and
  content-addressed; the agent stores the active generation.
- **Apply semantics.** Pull, verify, stage, then activate atomically. An invalid
  or incompatible config leaves the previous generation active and records a
  failed-apply source-health event. Never partially apply across channels,
  rules, and retention.
- **Drift and offline.** The agent reports its active config hash and rule pack
  hash in local status. A host that has been offline keeps collecting under its
  last valid generation; the operator can see the divergence.
- **Rollout.** For V0.2, rollout is an operator command to one or more named
  hosts with a dry-run diff, plus explicit rollback. No autonomous update
  channel, no remote shell, no response actions.
- **Ownership gate.** The unresolved Sentinel/DataSentinel seam still decides
  where fleet configuration and multi-host investigation live. Until that is
  settled, V0.2 stays local-first and exports a documented evidence package
  rather than inventing a control plane.
- **Tests.** Invalid signature, incompatible rule pack, partial network failure,
  restart during apply, drift report, rollback, and offline collection.

## Sequencing

1. Retention/backup on the single-sensor store, because it changes deletion and
   evidence semantics first.
2. Explicit enrollment identity and per-sensor tests.
3. Rule pack package + pinned replay.
4. Local config document/apply/rollback.
5. Only then revisit fleet transport and the DataSentinel ownership seam.
