# V0.4 explicitly enrolled local fleet

**Status: host/fixture pass, 2026-11-04.** This is the local, file-backed fleet
slice: explicit enrollment metadata, independent per-host stores, and a
host-comparison/coverage view. It does not add a remote transport, WEF, or a
fleet control plane.

## Enrollment

`sentinelctl enroll` records:

- `sensor_id` — enrollment identity, not a hostname;
- `host_id` and optional `host_name`;
- enrollment time and config generation;
- clock offset and clock uncertainty in milliseconds.

The local store keeps one enrolled sensor identity. An attempt to enroll a
different sensor/host into the same database fails unless the operator uses a
new database or an explicit future rotation command. Existing databases are
migrated by adding the `sensors` table; legacy stores without a row fall back to
their stored sensor/host metadata.

```bash
dotnet run --project src/sentinelctl -- enroll \
  --db data/host-a.db --sensor-id sensor-a --host-id host-a --host-name alpha

dotnet run --project src/sentinelctl -- enroll \
  --db data/host-b.db --sensor-id sensor-b --host-id host-b --host-name beta \
  --clock-offset-ms 1500 --clock-uncertainty-ms 250
```

## Fleet view

`fleet` opens multiple local databases read-only and produces a side-by-side
coverage comparison:

```bash
dotnet run --project src/sentinelctl -- fleet --dbs "data/host-a.db;data/host-b.db"
dotnet run --project src/sentinelctl -- fleet --dbs "data/host-a.db;data/host-b.db" --format json
```

Columns and JSON fields include event/alert/gap counts, bookmark and coverage
start, first/last event and last processed time, and three explicit states:

- `coverage_status`: `complete`, `partial`, or `unknown`;
- `order_status`: `ordered`, `uncertain`, or `unknown`;
- `clock_status`: `nominal` or `uncertain`.

`order_status` becomes `uncertain` when source timestamps decrease relative to
local sensor sequence, or when enrollment declares clock offset/uncertainty.
`coverage_status` becomes `partial` when a gap exists or the stored coverage
start is later than the oldest event; no events/bookmark is `unknown`.
`missing_telemetry` is true for partial/unknown coverage.

The table ends with `No-activity claims: false` and every JSON host carries
`no_activity_claim:false`. Missing telemetry is never rendered as “no activity.”

## Multi-host fixture

`fixtures/fleet-v01/` contains `fleet.json`, two complete synthetic Sysmon
fixtures, independent sensor/host IDs, and clock offset/uncertainty metadata.
The fixture host IDs deliberately keep colliding Event Log record IDs so a
future merge cannot use record IDs as a global key.

The fixture is loaded by `FleetFixture.Load`; ingestion still targets one
database per enrolled sensor. Cross-host correlation, merged event identity, and
remote acknowledgement/retry semantics are not implemented here.

## Verification

| Check | Result |
|---|---|
| Two independent host stores with distinct sensor/host IDs | fleet rows returned separately |
| Host B clock offset/uncertainty | `clock_status:uncertain`, `order_status:uncertain` |
| Injected per-host gap | `coverage_status:partial`, `missing_telemetry:true` |
| Out-of-order source timestamps | `order_status:uncertain` |
| No-activity claim | always false in table and JSON |
| Multi-host fixture loader | two hosts, 10 records each, metadata preserved |
| Existing single-host tests | still pass |
| Test suite | 80 tests passed |

## Limits

- Database paths are local; there is no remote enrollment, WEF, transport,
  acknowledgement, retry, or outage contract. Those need a separately versioned
  protocol before any network claim.
- Clock offset/uncertainty is enrollment metadata; timestamps are not silently
  adjusted. The view flags uncertainty but does not reconstruct true ordering.
- Host comparison is a local, read-only view. It does not merge stores, resolve
  duplicate source keys, correlate processes across hosts, or claim fleet
  completeness.
- Sensor identity remains database-scoped. Rotation/revocation semantics and
  multi-sensor ingestion into one store are future work.
- `no_activity_claim:false` is a conservative presentation invariant, not proof
  that every activity is present.
