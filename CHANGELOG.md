# Changelog

All notable changes to Sentinel. Versions are `0.x.y`; each release covers the
whole repository (agent, control CLI, rules, and schemas). Release artifacts and
their verification limits are described in [docs/release.md](docs/release.md).

## [0.9.1] - 2026-11-16

### Fixed

- Sigma import fails closed on duplicate output names, existing output rules,
  repeated mapped fields, wildcard/CIDR patterns, and missing/unsupported
  logsource categories. Process-creation, network-connection, and
  process-termination rules now carry an `event.id` guard; list
  `startswith`/`endswith` modifiers map to native operators.
- Run and backup signing keys are validated before output artifacts are
  written; empty keys fail explicitly, and malformed verification keys surface
  as invalid-manifest errors.
- `--dbs` rejects empty lists and deduplicates normalized paths; `config-show`
  opens stores read-only, reports missing/uninitialized stores, and does not
  create files.
- Legacy fleet-config hashes are recognized as already active when tenant and
  extended-retention fields are absent.
- Corrected the Sigma list-support wording in the changelog.
- Sigma import rejects empty list alternatives and unsupported logsource
  services, and creates destination files with CreateNew unless --force.
- Legacy config apply returns the stored legacy digest rather than reporting a
  different digest for the same generation.
- Multi-store path deduplication is case-sensitive on case-sensitive
  filesystems.

## [0.9.0] - 2026-11-16

Signed evidence handoff (bounded).

### Added

- `run --signing-key <pem>` signs the run manifest with ECDSA P-256; the public
  key is embedded and `verify-run` validates the signature and manifest hash.
- `run --custody-ref <uri;uri>` pins external custody references in
  `export.custody_refs`. Sentinel records them; it does not contact or verify the
  external system.
- `backup-keygen` remains the local key generator for backup and run signatures.

### Limits

- Custody references are metadata; no WORM, retention enforcement, access
  control, legal admissibility, or external custody integration is implemented.
- Keys are local files with operator-managed permissions; no key rotation or
  trust-anchor lifecycle.
- Run signatures cover the manifest, not every payload line; payload hashes are
  pinned separately.
- SSO, tenant isolation, remote fleet rollout, and live VM execution remain
  external/unrun.

## [0.8.0] - 2026-11-16

Fleet rollout and tenant metadata (bounded).

### Added

- `config-apply`, `config-rollback`, and `config-drift` accept `--dbs` and apply
  the same document to multiple local stores.
- `config-show --dbs` renders generation, hash, tenant, and config id per store.
- `FleetConfig.TenantId` and the fleet table/JSON carry a tenant label sourced
  from the active config. This is metadata, not isolation, SSO, or tenant policy.

### Limits

- Multi-tenancy isolation, SSO, and remote control-plane behavior remain with
  the consuming platform; Sentinel does not enforce tenant boundaries.
- Fleet rollout is local and fail-fast; no remote distribution or rollback
  orchestration.
- Tenant labels do not affect access, storage separation, or retention.

## [0.7.0] - 2026-11-16

Detection and retention breadth (bounded).

### Added

- `sigma-import --input <dir> --output <dir>` converts a single-selection Sigma
  subset (Image, CommandLine, Destination/Source IP/port, Protocol) into native
  Sentinel rules. Unsupported fields, modifiers, and correlation forms are
  rejected, not approximated; supported list `contains`/`startswith`/`endswith`
  modifiers are imported.
- `RetentionPolicy.ExtendedRetention` with `RawDays=-1` and
  `purge --extended-retention` keeps raw XML indefinitely by explicit opt-in.
  No encryption at rest is provided.

### Limits

- Sigma import is a tested subset, not full Sigma compatibility; author/license
  metadata is copied but per-rule license enforcement is not implemented.
- Extended retention is unencrypted and local; it is not a WORM or custody
  feature.
- Additional export integrations were not added in this slice.

## [0.6.0] - 2026-11-16

Event coverage and evidence hardening.

### Added

- Sysmon Event ID 5 (process termination) maps to OCSF Process Activity /
  Terminate, with timeline context and a dedicated fixture.
- `backup-keygen` and `backup --signing-key` produce ECDSA P-256 signed backup
  manifests; restore verifies the signature and fails closed.
- `run --redact raw_xml;process.cmd_line;process.path;user.name` exports
  redacted payloads, records the field list, omits raw XML, and counts redacted
  records on verification.
- `BackupService.RecoverInterruptedRestore` repairs a target/previous pair left
  by a kill between restore renames.

### Changed

- Run manifests pin `export.redacted_fields`; payloads carry `redacted_fields`.
- Event-ID 5 records are data events, not source-health markers.

### Limits

- Bookmark-age retention is not applicable while the store keeps one active
  bookmark per source; no bookmark-history table exists.
- A second endpoint/network provider and the parser framework are not part of
  this slice; no parser bypasses OCSF.
- Live lab, VM harness, and DataSentinel shell work remain outside this release.

## [0.5.1] - 2026-11-16

### Fixed

- Rule-pack and run rule hashes normalize CRLF to LF, so the same YAML produces
  the same pack identity on Windows and LF checkouts. `.gitattributes` forces LF
  for checked-out text; existing CRLF pack manifests remain loadable.
- Corrected the roadmap's relative planning-decision link.

## [0.5.0] - 2026-11-16

V0.2 completion: rule packs, fleet configuration, identity lifecycle, and
retention compaction. No remote control plane.

### Added

- Content-addressed rule packs with manifest verification, install, activation,
  rollback, and alert/run provenance. `rule-pack-*` commands; `--pack` on
  `ingest`, `replay`, `run`, and the live agent.
- Versioned fleet config documents with apply/history/rollback/drift. Config
  pins the rule pack, retention policy, and allowed collectors; `purge`, `push`,
  and rule loading enforce those fields.
- `sensor_incarnations` with rotation/revocation; the agent refuses a revoked
  incarnation. The installer mints or reuses a protected install-root
  `identity.json` and passes
  explicit sensor/host ids to the task.
- `purge --apply --compact` for SQLite `VACUUM` after an audited purge.

### Changed

- Run manifests pin pack id/hash and alert pack provenance.
- Existing databases receive additive pack/config/incarnation columns and
  tables during `Initialize`.
- `run`/`replay`/agent rule selection: explicit pack, explicit rules directory,
  config pin, active pack, then default directory.

### Limits

- Rule packs and config documents are unsigned; redaction lists, signed backup
  manifests, bookmark-age retention, and crash-injection restore remain open.
- Installer task/identity changes are parser- and fixture-verified only; the VM
  harness still needs KVM.

## [0.4.4] - 2026-11-16

Patch release for the deep-review and CodeRabbit findings against 0.4.3.

### Fixed

- `-NoRetentionTask` now stops and unregisters an existing `SentinelRetention`
  task instead of leaving the previous policy active while reporting it absent.
- Retention runs as the least-privileged reader account instead of SYSTEM, so a
  reader-writable database or log path no longer creates a SYSTEM write boundary.
- `build-release.ps1` rejects a `-Version` that differs from
  `Directory.Build.props`, refuses to recursively delete a non-release directory
  or a path containing the checkout, and normalizes a trailing output separator.
- CLI option parsing rejects duplicate options; `--apply false --apply` can no
  longer turn a flag violation into an applied purge.
- Assembly and file versions derive from the single `Version` property, so a
  later tag cannot ship binaries that report the previous release.
- `global.json` uses `rollForward: disable`, making the exact-version CI check
  meaningful.

### Changed

- README qualifies the 10 GiB retention value as an estimated-payload cap, not a
  database file-size limit.

## [0.4.3] - 2026-11-15

Release-engineering and operational-hardening slice; no new collection capability.

### Added

- Pinned .NET SDK (`global.json`) and shared version/build metadata
  (`Directory.Build.props`); binaries now report `0.4.3`.
- `.github/workflows/ci.yml`: Windows build, test, and tag-only release bundle.
- `scripts/build-release.ps1`: publishes the self-contained agent and control
  CLI, packs preview `Sentinel.Core` and `Sentinel.Windows` NuGet packages,
  stages the installer `deploy/` layout, and writes SHA-256 checksums plus
  `release.json`.
- Tag builds create a GitHub release with the zip, both `.nupkg` files,
  `SHA256SUMS.txt`, and `release.json`.
- Installer registers a daily `SentinelRetention` task (SYSTEM, 03:15) that runs
  `sentinelctl purge --apply` with 30-day events, 7-day raw XML, and 90-day
  alerts/source-health/gaps; uninstall removes it. `-NoRetentionTask` opts out.
- CLI option validation for both executables: unknown options, missing values,
  and values on flags now fail instead of being ignored.
- Shared `SourceStartChecks` for bookmark validation, snapshot bounds, and
  record-ID regression, with unit tests.

### Changed

- README status, install notes, and repository layout updated for release
  tooling and scheduled retention.
- `docs/v02-plan.md` open-item list reconciled with the V0.2 retention evidence.
- Canonical planning status records the release-engineering slice and remaining
  validation gaps.

### Known limitations

- Live V0.4.2 in-guest transport remains unrun; there is no TLS or remote
  identity attestation.
- No performance/overhead or rule false-positive/false-negative measurements.
- Restore does not migrate older schemas; physical compaction, crash-injection
  restore, and redaction lists remain unimplemented.
- Windows Event Log reader behavior is lab-validated only; unit tests cover the
  shared resume/cursor rules, not the live channel.

## Earlier slices

V0.1 through V0.4.2 were lab/fixture validation slices recorded in
[docs/](docs/): live collection and V0.1 VM run, clean-store and reboot resume,
retention/backup, reproducible runs and export, local fleet view, and bounded
HTTP transport.
