# V0.6: event and evidence hardening

**Status: implemented; fixture-verified. Live lab and VM harness remain
unrun.** This slice adds one event type and three evidence controls. It is not a
second provider and does not add a parser framework.

## Sysmon Event ID 5

Event ID 5 (process termination) maps to OCSF Process Activity / Terminate:

- `class_uid` 1007, `activity_id` 2, `type_uid` 100702, `type_name`
  "Process Activity: Terminate";
- process guid/pid/name/image/user retained; no parent or command line exists in
  the source event;
- the fixture is `fixtures/sysmon-v06-terminate/`;
- the record is a data event, not source-health context.

Other Event IDs remain source-health markers. The mapping is validated against
the pinned OCSF 1.9.0 process-activity class.

## Signed backups

```powershell
sentinelctl backup-keygen --output backups/sentinel-backup-key.pem
sentinelctl backup --db data/sentinel.db --output backups/sentinel.db --signing-key backups/sentinel-backup-key.pem
sentinelctl restore --backup backups/sentinel.db --db data/restored.db
```

`backup-keygen` writes an ECDSA P-256 private key (PKCS#8 PEM). `backup
--signing-key` embeds the public key and an RSA-style DER ECDSA signature over
the canonical manifest (signature field empty) in the manifest. Restore verifies
the signature before hashing or opening the database and fails closed. Unsigned
manifests remain accepted; the key is a local file and access control is the
operator's responsibility.

## Redacted run exports

```powershell
sentinelctl run --db data/sentinel.db --output runs/redacted `
  --redact "raw_xml;process.cmd_line;user.name"
```

Supported fields: `raw_xml`, `process.cmd_line`, `process.path`, `user.name`.
Redaction applies only to the exported payload; stored raw XML and OCSF rows are
not rewritten. Any redaction omits `raw_xml` from every payload to avoid leaking
the unredacted source, records `redacted_fields` in each payload and in
`export.redacted_fields`, and `verify-run` counts those records as redacted.
Redaction requires payload export.

## Interrupted restore recovery

`BackupService.RecoverInterruptedRestore` repairs the target/`.pre-restore` pair
left by a process kill between restore renames: if the target is missing it moves
the previous file back, otherwise it removes the stale previous copy. `restore`
calls it before validating the new backup. The recovery path is fixture-tested;
a full process-kill harness is not part of this slice.

## Limits

- Bookmark-age retention is not applicable while the store keeps one active
  bookmark per source; there is no bookmark-history table.
- Rule packs and config documents remain unsigned.
- A second endpoint/network provider and the parser framework remain gated; no
  parser bypasses OCSF.
- No live VM run for Event ID 5, signed backups, or the installer changes.
- DataSentinel shell/integration work belongs to its repository.

## Verification

```powershell
dotnet build Sentinel.slnx -c Release
dotnet test Sentinel.slnx -c Release
```

The suite covers Event ID 5 mapping/schema validation and timeline context,
signed-manifest verification and tamper rejection, redacted payload omission and
verification counts, and interrupted-restore recovery. Full suite: 122 tests.
