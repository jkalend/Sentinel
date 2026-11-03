# OCSF schema v1.9.0 (vendored)

Pinned from [ocsf/ocsf-schema](https://github.com/ocsf/ocsf-schema) at commit `856d462` (v1.9.0 tag line). License: Apache-2.0, see [LICENSE](LICENSE).

Vendored files are limited to the classes, category definitions, profiles, and objects this slice validates:

- `categories.json`
- `events/base_event.json`
- `events/system/system.json`, `events/system/process_activity.json`
- `events/network/network.json`, `events/network/network_activity.json`
- `profiles/host.json`
- `objects/` — actor, network connection info, device, endpoint, file, fingerprint, metadata, network endpoint, object, process, process entity, product, user, and `_entity`

These files are not modified. The validator reads their declared required fields; it does not compile the full OCSF schema or validate every profile, constraint, enum, or semantic rule.
