# V0.2 retention and backup: first implementation

**Status: fixture/host and live-store pass, 2026-11-04.** This slice adds
age/size retention with tombstones and audited raw-XML purge, plus online
SQLite backup and fail-closed restore. It is fixture/host-verified and was
exercised against a running `sentinel-reader` task in the disposable VM;
multi-gigabyte behavior is still unmeasured.

## Retention semantics

`sentinelctl purge` is a dry run unless `--apply` is supplied. Defaults:

| Class | Default |
|---|---|
| Data events | 30 days or 10 GiB of normalized-event payload |
| Raw XML | 7 days |
| Alerts | 90 days |
| Source-health records | 90 days |
| Gaps | 90 days |
| Newest events always preserved | 1,000 |

The six policy fields (`--event-days`, `--raw-days`, `--alert-days`,
`--health-days`, `--gap-days`, `--keep-events`, `--max-bytes`) are hashed as a
canonical policy and stored in every audit row.

- Purge only selects whole sequence ranges below the newest protected segment.
  It never renumbers sequences, rewrites the bookmark, or deletes the active
  bookmark.
- A `retention_purges` row is inserted in the same transaction before rows are
  deleted. It records policy hash, cutoffs, counts, and a `purged_ranges`
  tombstone with the deleted sequence range and first/last event times.
  `timeline` renders that tombstone before the remaining events.
- Alerts are deleted by their own age and otherwise survive deletion of the
  event they reference; the schema has no alert-to-event foreign key.
- Raw XML older than `--raw-days` is cleared and marked with `raw_purged_at`
  while the normalized event remains for its own retention period. The original
  `raw_sha256` is retained. `verify` treats a marked row as explicitly purged,
  checks that the raw text is empty, and reports a `raw_purged` count instead
  of recomputing the hash.
- The byte cap is an estimate of retained data-event payload: raw XML plus
  normalized OCSF, unmapped fields, command lines, and per-row overhead. It is
  not SQLite file size; ordinary deletes do not shrink the file. If the
  protected newest segment alone exceeds the cap, the report keeps it and sets
  `over_limit:true` rather than deleting the newest evidence.

## Commands

```bash
# Inspect the decision without changing the database.
dotnet run --project src/sentinelctl -- purge --db data/sentinel.db --now 2026-12-31T00:00:00Z

# Apply the default policy.
dotnet run --project src/sentinelctl -- purge --db data/sentinel.db --apply

# List audits.
dotnet run --project src/sentinelctl -- purges --db data/sentinel.db
```

`status`, `timeline`, and `verify` remain usable after a purge. Raw-purged
events keep their OCSF records and process/network context; an event whose whole
row was deleted appears only through the timeline tombstone and retention audit.

## Backup and restore

`backup` uses SQLite's online backup API while the agent may be running. It
writes a partial database, validates its integrity and schema metadata, hashes
the final file, then writes a manifest beside it. The manifest records the
database hash and size, contract/OCSF/engine versions, row counts, bookmark
identity and coverage, and creation time.

`restore` validates, in order:

1. manifest format;
2. database SHA-256 against the manifest;
3. `PRAGMA integrity_check`;
4. row counts and schema metadata against the manifest;
5. exact local contract compatibility.

Only then does it copy to a partial file, verify the copy hash, and replace the
target. A corrupt or mismatched backup fails before the target is touched.

```bash
dotnet run --project src/sentinelctl -- backup --db data/sentinel.db --output backups/sentinel-2026-11-04.db
dotnet run --project src/sentinelctl -- restore --backup backups/sentinel-2026-11-04.db --db data/sentinel.db
```

## Verification run

Fixture: 10 synthetic Sysmon records, 10 accepted, 3 alerts, 1 source-health
record.

| Check | Result |
|---|---|
| Dry run with `--keep-events 2` | 8 data rows selected, no database changes |
| Apply the same policy | 8 data rows deleted, 2 raw XML payloads purged, 3 alerts preserved |
| Bookmark after purge | record 1010 / coverage 1001 unchanged |
| Timeline | tombstone for sequences 1–8, two remaining events |
| `verify` | 0 schema errors; `raw_purged: 2` |
| Audit | one policy-hash row and one range 1–8 |
| Size cap (12,000 bytes, keep newest 1) | older data deleted; retained payload under cap; newest event present |
| Backup/restore | 10 events, 3 alerts, bookmark 1010 preserved |
| Corrupt backup restore | fails on hash mismatch; target hash unchanged |
| Test suite | 71 tests passed |

The exact-boundary test keeps an event whose timestamp equals the cutoff and
deletes the one one second older. Dry-run and apply are separate assertions so
a failed dry run cannot hide an accidental deletion.

## Live store check

The final disposable-VM run used a shortened 800-record main collection to
keep the retention check bounded, then tested the installed task while it was
running:

| Check | Result |
|---|---|
| Installed task before retention | 118 events; Running; bookmark 118 |
| Online backup while reader active | 119 events; 2 alerts; bookmark 119; 520,192 bytes; SHA-256 `259b3da5efacd696fa1f579897942a92e4a8315dd83d1c023b6dfeae600e2db5` |
| Concurrent purge | 196→87 events; 109 data rows deleted; 87 raw payloads purged; 2 alerts preserved; same incarnation |
| Agent continued appending | 139 events after purge |
| Restore to separate DB | 119 events; manifest and restored hashes match |
| Reboot/resume after retention | bookmark advanced 118→249; same incarnation; verify 0 schema errors; resume `ok:true` |
| Final harness | `ok:true`, `main_ok:true`, `retention_ok:true`; teardown restored reader, tasks, ACL and user rights |

Artifact hashes:

| Artifact | SHA-256 |
|---|---|
| Final VM result | `5acf7aa42fcb3c6808034e50fd65753948a28c3cf72b173fd1ca10df90115a30` |
| Pre-reboot main snapshot | `a522d37488b3c24e4e0624b65511ef2c9c65846d43d764cf7c74026628c3fc69` |
| Backup database and restored copy | `259b3da5efacd696fa1f579897942a92e4a8315dd83d1c023b6dfeae600e2db5` |
| Backup manifest | `5da5045825601f09502bccb9f6f37c0caee921a2a2ae15dc89f8c85211893a55` |

The first live attempt exposed a harness assertion that compared post-purge
counts without accounting for concurrent appends; the assertion was corrected
and the final run passed. The product behavior in that first attempt already
showed successful backup, purge, append continuation, and restore.

## Limits

- The byte cap is a payload estimate and does not run `VACUUM`; use an operator
  maintenance window when physical file size matters.
- Purge is age/size based, not segment-based; the newest `--keep-events` rows are
  protected, but older rows can be deleted by size before their age cutoff.
- The active bookmark is always preserved. Old bookmark rows are not a separate
  table yet, so bookmark-age retention is not implemented.
- Backup validation checks integrity and exact schema versions, but the local
  manifest is not signed. `restore` requires an exact contract match and does
  not migrate older databases.
- Restore is an offline replacement of the target file. It is fail-closed for
  hash, integrity, schema, and count mismatches, but there is no crash-injection
  test for a process kill between target rename steps.
- The live VM evidence uses a shortened 800-record collection and a synthetic
  disposable endpoint; multi-gigabyte size, physical compaction, crash injection
  between restore renames, and production retention periods remain unverified.
- Redaction lists are not implemented; command lines remain evidence unless a
  future explicit redaction pass records its own audit.
