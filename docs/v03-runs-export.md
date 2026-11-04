# V0.3 reproducible runs and bounded export

**Status: fixture/host pass, 2026-11-04.** This slice adds immutable
investigation run manifests and local export verification. It has been exercised
on synthetic Sysmon fixtures; it is not a signature, source-authenticity, WORM,
or legal-custody mechanism.

## Run manifest

`sentinelctl run` writes a new directory containing:

- `manifest.json` — canonicalizable run record;
- `manifest.sha256` — SHA-256 of the manifest with `manifest_sha256` empty;
- `payload.jsonl` — only with `--include-payload`.

The manifest pins:

- run id and creation time;
- engine, local contract, OCSF, mapper, schema-tree hash, rule-set hash, and
  each rule id/version/file hash;
- source identity and bookmark/coverage range per source;
- event count, alert count, gap count, and first/last sequence and source time;
- each event's source key, sequence, record kind, Event Log record id, source
  and logged timestamps, clock status, raw hash, normalized OCSF hash, and
  raw-purge marker;
- alert ids, rule identities, event references, and details hashes;
- gap ids, ranges, confidence, and details hashes;
- export mode and optional payload file hash.

Manifest and content hashes use a small canonical JSON writer: object keys are
ordinal-sorted, whitespace is insignificant, and changed semantic content
changes the hash. `content_sha256` excludes the volatile run id, creation time,
and ingest-time fields listed under limits, so a fixture re-run under the same
versions produces the same content hash. Each run still gets a unique run id
and manifest hash.

## Export

Default export is **manifest-only**: no raw XML or command-line payload is
copied. `--include-payload` explicitly writes one canonical JSON line per event
to `payload.jsonl`; each event pin then carries its payload hash.

`sentinelctl verify-run --manifest <dir>/manifest.json [--db <path>]` checks:

1. the sidecar and embedded manifest hash;
2. the content hash;
3. payload-file existence and file hash;
4. per-event payload presence and per-line hash;
5. when `--db` is supplied, whether each source key still exists and whether its
   raw XML is still present.

It reports `included`, `missing`, `changed`, and `redacted` separately and exits
nonzero for missing/changed content, hash failures, or invalid payload lines.
Deleting a source row after a run makes the run report `expired source key`;
it does not block configured retention.

## Commands

```bash
# Manifest-only run; explicit output directory must be new/empty.
dotnet run --project src/sentinelctl -- run --db data/sentinel.db --output runs/2026-11-04-a

# Explicit payload export.
dotnet run --project src/sentinelctl -- run --db data/sentinel.db --output runs/2026-11-04-b --include-payload

# Verify manifest integrity, and optionally the live source keys.
dotnet run --project src/sentinelctl -- verify-run --manifest runs/2026-11-04-b/manifest.json --db data/sentinel.db
```

## Verification run

Fixture: 10 synthetic Sysmon records, 10 events, 3 alerts, 1 source-health
record, 0 gaps.

| Check | Result |
|---|---|
| Two runs over the same store | same `content_sha256`; different run ids/manifest hashes |
| Two fresh fixture ingests | same `content_sha256` |
| Manifest property reordering | manifest still validates |
| Changed run id | manifest hash validation fails |
| Manifest-only default | no `payload.jsonl` |
| Explicit payload | 10 canonical JSONL records; verifies |
| Payload byte change | `payload_valid:false`, `changed` event reported |
| Payload truncation | `payload_valid:false`, `missing` events reported |
| Purge then verify | 9 source references reported missing; verification fails |
| Changed rule version | rule hash and content hash change |
| Test suite | 77 tests passed |
| Live V0.2 database | 2,710 events across 7 source/incarnation groups, 14 alerts; manifest-only and 17 MiB payload export verified |

## Limits

- `--include-payload` currently exports all events in the run, not an arbitrary
  source-key selection. The option is explicit but not a per-event selector.
- The run is immutable by content, not by filesystem permissions. A writer can
  replace files; verification detects replacement through hashes, not
  authenticity.
- The manifest is not signed and no external trust anchor is configured.
- OCSF `metadata.processed_time` (an ingest timestamp) is removed from the
  normalized OCSF hash and payload copy so fixture re-runs are reproducible.
  Alert creation and gap-noted timestamps are pinned in the manifest but
  excluded from `content_sha256` for the same reason.
- Expiry checks require `--db`; without a database the verifier can only prove
  manifest/payload integrity, not source availability.
- This is local file export. There is no remote recipient, retention pin,
  WORM, source-authenticity, or custody claim.
- The V0.3 VM harness still validates collection/retention; run/export was
  exercised from host fixture databases.
