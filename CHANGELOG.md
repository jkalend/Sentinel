# Changelog

All notable changes to Sentinel. Versions are `0.x.y`; each release covers the
whole repository (agent, control CLI, rules, and schemas). Release artifacts and
their verification limits are described in [docs/release.md](docs/release.md).

## [Unreleased]

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
