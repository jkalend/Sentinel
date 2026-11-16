# Release process

## Versioning

- One version covers the repository: `sentinel-agent`, `sentinelctl`, rules, and
  schemas. The version lives in `Directory.Build.props` and is repeated in
  `CHANGELOG.md`.
- Tags use `vX.Y.Z` and must match the version in `Directory.Build.props`.
  `build-release.ps1` rejects a `-Version` that differs from the shared value;
  bump the props version before tagging.
- `0.x` means lab/preview: the local event contract is frozen at
  `sentinel.local-event/v1`, but operational behavior, installer defaults, and
  transport may change between minor versions.
- The engine, mapper, and contract carry separate identity strings
  (`sentinel.engine/0.1.0`, `sentinel.sysmon-mapper/0.1.0`,
  `sentinel.local-event/v1`) independent of the release version.

## Build

Prerequisites: the exact .NET SDK pinned by `global.json` (10.0.115) on Windows.
`rollForward` is `disable`, and both CI and `build-release.ps1` fail if
`dotnet --version` differs.

```powershell
./scripts/build-release.ps1 -Version 0.4.4
```

The script publishes both executables self-contained for `win-x64`, prunes
debug symbols, copies `rules/` and `schemas/`, and writes:

```text
publish/release-<version>/
├─ deploy/
│  ├─ agent/sentinel-agent.exe + e_sqlite3.dll
│  ├─ ctl/sentinelctl.exe + e_sqlite3.dll
│  ├─ rules/*.yaml
│  └─ schemas/ocsf-1.9.0/
├─ packages/
│  ├─ Sentinel.Core.<version>.nupkg
│  └─ Sentinel.Windows.<version>.nupkg
├─ sentinel-<version>-win-x64.zip
├─ SHA256SUMS.txt
└─ release.json      (version, runtime, commit, dirty flag, SDK, packages, hashes)
```

`deploy/` is the layout `scripts/install-sentinel-agent.ps1 -SourceRoot` expects.
Artifacts are unsigned; publish and install only from a checkout or bundle whose
`SHA256SUMS.txt` you verified.

## Library packages

The release attaches two preview NuGet packages:

- `Sentinel.Core` (`net10.0`) — parser, mapper, rule evaluator, SQLite store,
  replay, timeline, runs, fleet, and transport.
- `Sentinel.Windows` (`net10.0-windows`) — the `IEventSource` adapter over the
  Windows Event Log; depends on `Sentinel.Core` at the same version.

They are attached to the GitHub release, not published to nuget.org. Download
the `.nupkg` files and add the containing directory as a local package source.
The `0.x` package API is not frozen: types may change between minor versions.
`sentinel.local-event/v1` remains the stable data contract.

## CI

`.github/workflows/ci.yml` runs on Windows for pushes to `main`, pull requests,
and `v*` tags: verify the pinned SDK, restore, Release build (warnings are
errors), and the full test suite. Tag builds additionally run
`build-release.ps1`, upload the zip, packages, checksums, and `release.json`,
and create the GitHub release.

CI does not run the QEMU/KVM lab harness or any live Event Log collection. A
green build is not evidence of live-source behavior.

## Verification checklist for a release

Run and record the commands actually used:

1. `dotnet build Sentinel.slnx -c Release` — zero warnings.
2. `dotnet test Sentinel.slnx -c Release` — all tests pass.
3. Fixture path on a clean database: `ingest`, `timeline`, `replay`, `verify`,
   `backup`, `restore`, `run`, `verify-run`, `fleet`.
4. `scripts/build-release.ps1`, then re-check hashes with
   `certutil -hashfile <file> SHA256` (or `Get-FileHash` where available), and
   run the bundled `ctl` against a fixture.
5. Installer: parse-check both PowerShell scripts; on a disposable host, install,
   confirm both scheduled tasks, uninstall, and confirm the channel ACL and
   user-rights policy are restored.
6. Update `CHANGELOG.md` with the released version and any changed limits.

## What a release claims

A Sentinel release is a lab-validated detection and evidence slice, not a
production EDR/SIEM. The following are deliberately unclaimed:

- Performance, throughput, dataset growth, and rule false-positive/false-negative
  rates. No numbers exist.
- Signed or tamper-proof evidence, WORM storage, custody, or legal admissibility.
- Transport security: the HTTP transport has no TLS or identity attestation; the
  optional token is a shared secret.
- Crash-injection restore, physical compaction (`VACUUM`), schema migration on
  restore, or redaction lists.
- Live-source coverage beyond Windows 11 Enterprise Evaluation 26H2 with built-in
  Sysmon Event IDs 1 and 3, exercised on one disposable VM.
- A frozen library API or package support policy. `Sentinel.Core` and
  `Sentinel.Windows` are `0.x` preview packages; only the local-event contract
  is frozen.

Known limits are maintained in
[V0.2 retention/backup](v02-retention-backup.md),
[V0.4.1 transport](v04.1-remote-transport.md),
[V0.4.2 in-guest harness](v04.2-in-guest-transport.md), and
[V0.1 live validation](v01-live-validation.md).
