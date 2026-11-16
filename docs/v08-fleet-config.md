# V0.8: local fleet rollout and tenant metadata

**Status: implemented; fixture-verified.** This is local config rollout and a
tenant label, not multi-tenant isolation or SSO.

## Multi-store config

```powershell
sentinelctl config-apply    --dbs "data/a.db;data/b.db" --file config.json
sentinelctl config-show     --dbs "data/a.db;data/b.db"
sentinelctl config-drift    --dbs "data/a.db;data/b.db"
sentinelctl config-rollback --dbs "data/a.db;data/b.db"
```

The same document is applied to each store. Omit `sensor_id`/`host_id` for a
fleet document unless every store has the same enrollment. Each store keeps its
own generation/history; rollout is fail-fast and local. A later failure leaves
earlier stores applied, so re-run drift/apply after fixing the cause.

## Tenant metadata

`FleetConfig` accepts an optional `tenant_id` (max 128 printable characters).
It is pinned in the config hash and shown by `config-show` and `fleet`:

```text
TENANT       HOST            SENSOR            EVENTS ALERTS GAPS ...
tenant-a     8a4d...         2f7c...              10      3    0 ...
```

The label is descriptive. It does not isolate databases, filter queries, change
retention, or authenticate anyone. SSO and tenant policy remain platform-owned.

## Limits

- No remote distribution, enrollment API, or fleet-wide transaction.
- No tenant isolation, per-tenant keys, or access control.
- No remote rollback orchestration; run rollback per store.
- Live VM/harness remains unrun.

## Verification

`dotnet test Sentinel.slnx -c Release` covers tenant hashing/config and fleet
tenant rows; CLI smoke applies two config generations to two stores, shows
tenants, drifts clean, rolls back, and renders the tenant column.
