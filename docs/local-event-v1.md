# `sentinel.local-event/v1`

**Status:** frozen for the V0.1 fixture-driven slice. Owned by the Sentinel engine ([D-0015](https://github.com/jkalend/project-planning/blob/main/decisions/0015-sentinel-engine-ownership.md)).

This contract defines the row Sentinel persists for one rendered Windows Event Log record and the failure vocabulary that accompanies collection. It does not define a wire protocol, a fleet envelope, an OCSF replacement, or a legal evidence format.

## Layers

One SQLite database holds two related layers:

1. **Source layer.** The exact rendered XML, source identity, ordering, timestamps, and retention/gap state.
2. **Normalized layer.** An OCSF 1.9.0 event for the supported source types, plus an explicit unmapped-field report.

The stored OCSF event never replaces the source XML. `raw_xml` plus `raw_sha256` is the local reference; `ocsf` is a derived view with a declared mapping identity.

## Fields

| Field | Type | Contract |
|---|---|---|
| `contract_version` | string | Literal `sentinel.local-event/v1`; independent of the OCSF and engine versions. |
| `sensor_id` | string (UUID) | Random installation identifier persisted by Sentinel. Not a hardware fingerprint; lost on reinstall unless state is deliberately restored. |
| `host_id` | string (UUID) | Locally generated host identifier, separate from the Windows computer name. No cross-host identity service exists in V0.1. |
| `source.channel` | string | Windows channel, e.g. `Microsoft-Windows-Sysmon/Operational`. |
| `source.provider` | string | Provider name from `System/Provider@Name`. |
| `source.provider_guid` | string nullable | Provider GUID from `System/Provider@Guid`, when present. |
| `source.incarnation_id` | string (UUID) | Locally generated channel-incarnation identity. A confirmed channel clear/recreation or strict missing-record/reset boundary closes it and opens a new one; an unconfirmed boundary preserves an `unknown` gap instead. |
| `source.config_generation` | integer | Increments when the effective source configuration changes. A configuration update does not by itself reset the source incarnation. |
| `source.event_id` | integer | Windows Event ID. |
| `source.event_record_id` | integer | `System/EventRecordID`. A cursor within one channel incarnation, not the Sentinel sequence. |
| `source_key` | string | Derived: `(sensor_id, channel, source.incarnation_id, source.event_record_id)`. Unique within the database. |
| `sensor_sequence` | integer | Monotonically assigned local ingestion sequence committed with the row. Defines accepted-store order, not source order or causality. |
| `time` | integer | Event time in Unix epoch milliseconds, mapped from the source UTC time for supported data events. The original source string stays in `raw_xml`. |
| `logged_time` | integer nullable | Windows `System/TimeCreated@SystemTime` in epoch milliseconds. |
| `processed_time` | integer | Sentinel receipt/normalization time in epoch milliseconds. |
| `clock_status` | string | `wall_utc` in V0.1. Wall-clock time can step forward or backward; elapsed durations and cross-host order must not be inferred from it alone. |
| `record_kind` | string | `data` for normalized Event ID 1/3 records; `source_health` for other retained channel records or rejected mappings. |
| `ocsf` | JSON text nullable | OCSF 1.9.0 event object for `data` rows. Null for `source_health` rows. |
| `ocsf_class_uid` | integer nullable | Denormalized from `ocsf.class_uid` for indexing. |
| `ocsf_activity_id` | integer nullable | Denormalized from `ocsf.activity_id` for indexing. |
| `unmapped` | JSON text | Array of `{ "source_field", "reason" }` for every source property deliberately not represented in OCSF. Empty array when none. |
| `raw_xml` | text | Exact XML string returned by Windows Event Log for the record. |
| `raw_sha256` | string | SHA-256 of `raw_xml` encoded as UTF-8 without BOM, with no whitespace or Unicode normalization. Event Log rendered XML, not original provider binary or raw ETW bytes. |
| `gap_id` | string nullable | Links the row to the gap/reset marker that bounds it, when known. |

Stored OCSF events carry `metadata.version = "1.9.0"` and a mapper identity. The mapper identity is an engine version value, not an OCSF schema version.

### Bookmark and source state

The bookmark row stores the Windows Event Log bookmark XML (`bookmark_xml`) alongside `source_id`, `incarnation_id`, `config_generation`, `event_record_id`, `coverage_start_record_id`, and `sensor_sequence`. The XML is the resume cursor; `event_record_id` and `coverage_start_record_id` remain queryable coverage metadata. A resume without usable bookmark XML is a `bookmark_missing` gap and must not fall back to "now".

`source_state` records the channel configuration captured at enrollment or restart: `maximum_size_bytes`, `log_mode`, `is_enabled`, `log_file_path`, and `captured_at`. These values describe the source, not Sentinel's own retention.

Sysmon Event ID 16 (configuration change) is stored as a source-health row and advances `config_generation` for that row and subsequent accepted records. The raw Event 16 XML preserves the configuration identity; the numeric generation is a local ordering aid, not a hash.

### Denormalized source columns

`process_guid`, `process_id`, `process_name`, `process_cmd_line`, `process_path`, `parent_process_guid`, `parent_process_id`, `parent_process_name`, `parent_cmd_line`, `network_protocol`, `network_initiated`, `network_outcome`, `src_ip`, `src_port`, `src_hostname`, `dst_ip`, `dst_port`, and `dst_hostname` are derived copies of supported source fields. They exist so rules, replay, and `ProcessGuid` timeline joins do not re-parse XML. `raw_xml` remains authoritative; these columns are not an independent evidence source.

## Identity and ordering

- A row is keyed by `source_key`. Repeating a delivered record inside the same known incarnation is an idempotent no-op; it never creates a second row.
- A repeated `event_record_id` after a confirmed clear belongs to a new incarnation and is therefore a different source key.
- A PID is an attribute. Process identity is `(host_id, ProcessGuid)`; parent and child links use `ProcessGuid` only. A missing parent or late event stays visibly incomplete.
- `sensor_sequence` orders ingestion and replay. Displayed event time, logged time, and processed time remain separate columns.
- A duplicate, late, or out-of-order arrival may change a displayed timeline but cannot rewrite accepted source order.

## OCSF mapping rules for V0.1

- Event ID 1 → OCSF Process Activity (`class_uid=1007`), `activity_id=1` (Launch), category `system` (`category_uid=1`), `type_uid=100701`.
- Event ID 3 → OCSF Network Activity (`class_uid=4001`), category `network` (`category_uid=4`); `type_uid=400101` for Open and `type_uid=400100` for Unknown. `activity_id=1` (Open) is set **only** when the source fixture's independent ground truth establishes a successfully established outbound TCP connection, `Initiated=true`, and protocol `tcp`. Inbound-to-listener, refused, UDP, unknown direction, or unknown outcome use `activity_id=0` (Unknown) and `initiator_id=0` (Unknown), with an explicit unmapped reason. Network events declare the OCSF `host` profile so `actor.process` and `device` are valid attributes; the pinned 1.9.0 `network_activity` class itself has no process attribute.
- The mapper never infers a failed/refused outcome from an absent Event ID 3 record.
- Live collection has no independent outcome oracle. Without an entry in the fixture/run ground truth, every live Event ID 3 remains `activity_id=0` (Unknown); only a controlled, confirmed outbound TCP case maps to `Open`. The network rule therefore does not fire on unproven live records.
- Required OCSF fields are validated against the pinned 1.9.0 schema. Schema validity alone does not establish semantic fidelity; fixture assertions compare each intended source field with its target or an intentional-unmapped reason.

Pinned schema: OCSF v1.9.0 commit `856d462`, vendored under `schemas/ocsf-1.9.0/`. The verifier checks the required fields and declared object requirements in those files; it is a structural requirement check, not a full OCSF compiler or a semantic-fidelity proof.

## Atomic commit

One SQLite transaction appends:

1. the source row (`raw_xml`, hash, identity, timestamps),
2. the normalized record or source-health marker,
3. the assigned `sensor_sequence`,
4. any alert rows produced by the evaluated rule profile,
5. the updated bookmark, including its serialized Windows Event Log XML.

The bookmark advances only in the same successful commit. On `SQLITE_FULL`, I/O error, or commit failure: roll back, do not advance the bookmark, pause collection, and surface a store-pause gap. SQLite transactions provide atomic commit on one host; they do not protect against a hostile local administrator.

Duplicates are ignored before the transaction writes a new event; the bookmark may still advance if the duplicate is the next expected record.

## Failure and gap vocabulary

| Code | Meaning | Cause confidence |
|---|---|---|
| `channel_cleared` | The channel was cleared/recreated and the previous incarnation cannot be resumed. | known |
| `record_id_regression` | `EventRecordID` moved backwards within the same incarnation. | known |
| `bookmark_missing` | No usable saved bookmark exists. | known |
| `missing_records_reported` | The subscription/bookmark reports skipped records. | known |
| `reader_downtime` | The reader was not running across an interval with retained records. | known |
| `store_paused` | Local storage failed or reached its cap; collection paused. | known |
| `retention_expired` | Local retention deleted rows required by a run or reference. | known |
| `source_filter_unknown` | Records may have been excluded by source filtering; the reader cannot measure it. | unknown |
| `unknown` | A boundary is visible but its cause is not established. | unknown |

Coverage state is one of `covered`, `gap`, `paused`, `unknown`. A gap always has a start boundary; it may have an unknown end while collection is paused. Missing telemetry is never reported as absence of activity.

## Versioning and change rules

- Adding an optional column or a new gap code that consumers can ignore is a compatible change.
- Changing field meaning, deleting a field, changing the hash input, or changing a normalized field's semantics requires a new contract version.
- The OCSF schema version and the mapper/engine version are recorded separately. A schema or mapping change does not rewrite existing rows.
- Retention expiry and redaction must not leave a row claiming complete source content; affected runs become explicitly partial.

## Out of scope for V0.1

Remote transport, WEF/WEC, fleet identity, packet capture, case management, signed/WORM evidence, general Sigma compatibility, and legal admissibility. The local database is not tamper-proof.
