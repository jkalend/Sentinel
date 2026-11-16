# V0.9: signed run exports and custody references

**Status: implemented; fixture-verified.** This is a signed manifest and custody
metadata, not WORM storage or a legal-custody claim.

## Signed runs

```powershell
sentinelctl backup-keygen --output keys/sentinel-run-key.pem
sentinelctl run --db data/sentinel.db --output runs/case-1 `
  --signing-key keys/sentinel-run-key.pem `
  --custody-ref "custody://casebox/case-1"
sentinelctl verify-run --manifest runs/case-1/manifest.json --db data/sentinel.db
```

The manifest embeds an ECDSA P-256 public key and a DER signature over the
canonical manifest with the signature field empty. `ManifestSha256` is computed
with signature fields cleared, so unsigned and signed manifests share the same
content-hash rules. `ReadManifest`/`verify-run` reject an incomplete or invalid
signature before trusting the manifest; payload hashes remain separate pins.

`--custody-ref` accepts one or more `;`-separated printable references (max 512
characters each). They are sorted, deduplicated, and recorded in
`export.custody_refs`. Sentinel does not call, authenticate, or verify the
external system; a reference is operator metadata.

## Limits

- No WORM storage, immutable retention, access control, tamper-proof claim, or
  legal admissibility.
- No key rotation, revocation, trust anchor, or external identity.
- Signatures cover the manifest, not each payload byte; payload SHA-256 pins are
  the content check.
- Custody references are not verified; external custody remains Casebox/Sable/
  operator-owned.
- SSO, tenant isolation, remote rollout, and live VM execution remain unrun or
  external.

## Verification

`dotnet test Sentinel.slnx -c Release` covers signed-manifest verification,
tamper rejection, custody pinning, and existing unsigned/payload tests. Full
suite: 134 tests after the 0.9.1 fixes.
