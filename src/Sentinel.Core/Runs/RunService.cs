using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Sentinel.Core.Model;
using Sentinel.Core.Rules;
using Sentinel.Core.Store;

namespace Sentinel.Core.Runs;

public static class RunService
{
    private const string Format = "sentinel.run/v1";
    private const string PayloadFileName = "payload.jsonl";

    private static readonly JsonSerializerOptions ManifestOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true
    };

    public static RunManifest Create(
        SqliteEventStore store,
        RuleSet rules,
        string schemaRoot,
        string outputDirectory,
        bool includePayload = false,
        IReadOnlyCollection<string>? selectedSourceKeys = null,
        IReadOnlyCollection<string>? redactedFields = null,
        string? signingPrivateKeyPem = null,
        IReadOnlyCollection<string>? custodyRefs = null)
    {
        var redactions = NormalizeRedactions(redactedFields);
        var custody = NormalizeCustodyRefs(custodyRefs);
        if (redactions.Count > 0 && !includePayload)
        {
            throw new ArgumentException("redaction requires payload export.");
        }

        var output = Path.GetFullPath(outputDirectory);
        if (Directory.Exists(output) && Directory.EnumerateFileSystemEntries(output).Any())
        {
            throw new IOException($"run output directory is not empty: {output}");
        }

        var events = store.ReadEvents();
        var bookmarks = store.ReadBookmarks();
        var alerts = store.ReadAlerts();
        var gaps = store.ReadGaps();
        var identity = BuildIdentity(rules, schemaRoot);
        var sources = BuildSources(events, bookmarks);
        var coverage = new RunCoverage(
            sources.Count,
            events.Count,
            alerts.Count,
            gaps.Count,
            events.Count == 0 ? null : events.Min(value => value.SensorSequence),
            events.Count == 0 ? null : events.Max(value => value.SensorSequence),
            events.Count == 0 ? null : Timestamp(events.MinBy(value => value.Time)!.Time),
            events.Count == 0 ? null : Timestamp(events.MaxBy(value => value.Time)!.Time));

        var eventPins = events
            .OrderBy(value => value.SensorSequence)
            .Select(EventPin)
            .ToList();
        var alertPins = alerts
            .OrderBy(value => value.SensorSequence)
            .ThenBy(value => value.AlertId, StringComparer.Ordinal)
            .Select(value => new RunAlertPin(
                value.AlertId,
                value.RuleId,
                value.RuleVersion,
                value.EngineVersion,
                value.SourceKey,
                value.SensorSequence,
                Timestamp(value.CreatedAt),
                CanonicalJson.HashText(value.DetailsJson),
                value.PackId,
                value.PackSha256))
            .ToList();
        var gapPins = gaps
            .OrderBy(value => value.NotedAt)
            .ThenBy(value => value.GapId, StringComparer.Ordinal)
            .Select(value => new RunGapPin(
                value.GapId,
                value.SourceId,
                value.IncarnationId,
                value.Kind.ToString(),
                value.Confidence.ToString(),
                value.StartRecordId,
                value.EndRecordId,
                Timestamp(value.NotedAt),
                CanonicalJson.HashText(value.Details)))
            .ToList();

        Directory.CreateDirectory(output);
        string? payloadFile = null;
        string? payloadHash = null;
        var selection = selectedSourceKeys is null
            ? null
            : new HashSet<string>(selectedSourceKeys, StringComparer.Ordinal);
        if (selection is not null)
        {
            var missing = selection.Except(eventPins.Select(pin => pin.SourceKey), StringComparer.Ordinal).ToList();
            if (missing.Count > 0)
            {
                throw new ArgumentException($"unknown source keys: {string.Join(", ", missing)}");
            }

            if (selection.Count == 0)
            {
                throw new ArgumentException("at least one source key is required for a selected payload.");
            }
        }

        if (includePayload)
        {
            payloadFile = PayloadFileName;
            var path = Path.Combine(output, payloadFile);
            var payloadByKey = events.ToDictionary(value => value.SourceKey, StringComparer.Ordinal);
            using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
            {
                foreach (var pin in eventPins.Where(pin => selection is null || selection.Contains(pin.SourceKey)))
                {
                    var payload = Payload(payloadByKey[pin.SourceKey], redactions);
                    writer.WriteLine(CanonicalJson.Serialize(payload, ManifestOptions));
                }
            }

            eventPins = eventPins
                .Select(pin =>
                {
                    if (selection is not null && !selection.Contains(pin.SourceKey))
                    {
                        return pin;
                    }

                    return pin with
                    {
                        PayloadIncluded = true,
                        PayloadSha256 = CanonicalJson.HashText(CanonicalJson.Serialize(Payload(payloadByKey[pin.SourceKey], redactions), ManifestOptions))
                    };
                })
                .ToList();
            payloadHash = CanonicalJson.HashFile(path);
        }

        var export = new RunExport(
            includePayload ? "selected-payload" : "manifest-only",
            includePayload,
            payloadFile,
            payloadHash,
            selection?.Order(StringComparer.Ordinal).ToList(),
            redactions.Count == 0 ? null : redactions,
            custody.Count == 0 ? null : custody);
        var manifest = new RunManifest(
            Format,
            Guid.NewGuid().ToString("D"),
            Timestamp(DateTimeOffset.UtcNow),
            identity,
            coverage,
            sources,
            eventPins,
            alertPins,
            gapPins,
            export,
            string.Empty,
            string.Empty);
        manifest = manifest with { ContentSha256 = CanonicalJson.Hash(Content(manifest)) };
        manifest = manifest with { ManifestSha256 = CanonicalJson.Hash(ForManifestHash(manifest)) };

        if (!string.IsNullOrWhiteSpace(signingPrivateKeyPem))
        {
            using var key = ECDsa.Create();
            key.ImportFromPem(signingPrivateKeyPem);
            manifest = manifest with { SigningPublicKeyPem = key.ExportSubjectPublicKeyInfoPem() };
            var payload = Encoding.UTF8.GetBytes(CanonicalJson.Serialize(manifest with { Signature = null }));
            var signature = key.SignData(
                payload,
                HashAlgorithmName.SHA256,
                DSASignatureFormat.Rfc3279DerSequence);
            manifest = manifest with { Signature = Convert.ToBase64String(signature) };
        }

        var manifestPath = Path.Combine(output, "manifest.json");
        File.WriteAllText(
            manifestPath,
            JsonSerializer.Serialize(manifest, ManifestOptions),
            new UTF8Encoding(false));
        File.WriteAllText(
            Path.Combine(output, "manifest.sha256"),
            manifest.ManifestSha256 + Environment.NewLine,
            new UTF8Encoding(false));
        return manifest;
    }

    public static RunManifest ReadManifest(string manifestPath)
    {
        var fullPath = Path.GetFullPath(manifestPath);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException($"run manifest not found: {fullPath}");
        }

        var manifest = JsonSerializer.Deserialize<RunManifest>(
            File.ReadAllText(fullPath),
            ManifestOptions) ?? throw new InvalidDataException("run manifest is empty.");
        if (!string.Equals(manifest.Format, Format, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"unsupported run format: {manifest.Format}");
        }

        var hashPath = Path.Combine(Path.GetDirectoryName(fullPath)!, "manifest.sha256");
        if (!File.Exists(hashPath))
        {
            throw new InvalidDataException("manifest.sha256 is missing.");
        }

        var sidecar = File.ReadAllText(hashPath).Trim();
        ValidateSignature(manifest);
        var expected = CanonicalJson.Hash(ForManifestHash(manifest));
        if (!string.Equals(sidecar, expected, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(manifest.ManifestSha256, expected, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("run manifest hash does not match the manifest content.");
        }

        if (!string.Equals(manifest.ContentSha256, CanonicalJson.Hash(Content(manifest)), StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("run content hash does not match the manifest content.");
        }

        return manifest;
    }

    public static RunVerification Verify(
        RunManifest manifest,
        string manifestDirectory,
        SqliteEventStore? store = null)
    {
        var messages = new List<string>();
        var manifestValid = true;
        var contentValid = true;
        var payloadValid = true;
        var included = 0;
        var missing = 0;
        var changed = 0;
        var redacted = 0;

        try
        {
            ValidateSignature(manifest);
        }
        catch (InvalidDataException error)
        {
            manifestValid = false;
            messages.Add(error.Message);
        }

        var expectedManifest = CanonicalJson.Hash(ForManifestHash(manifest));
        if (!string.Equals(expectedManifest, manifest.ManifestSha256, StringComparison.OrdinalIgnoreCase))
        {
            manifestValid = false;
            messages.Add("manifest hash mismatch");
        }

        if (!string.Equals(CanonicalJson.Hash(Content(manifest)), manifest.ContentSha256, StringComparison.OrdinalIgnoreCase))
        {
            contentValid = false;
            messages.Add("content hash mismatch");
        }

        Dictionary<string, (RunPayload Payload, string Canonical)>? payloads = null;
        if (manifest.Export.PayloadIncluded)
        {
            var payloadPath = Path.Combine(manifestDirectory, manifest.Export.PayloadFile
                ?? throw new InvalidDataException("payload export has no payload_file."));
            if (!File.Exists(payloadPath))
            {
                payloadValid = false;
                messages.Add($"payload file missing: {payloadPath}");
            }
            else
            {
                var fileHash = CanonicalJson.HashFile(payloadPath);
                if (!string.Equals(fileHash, manifest.Export.PayloadSha256, StringComparison.OrdinalIgnoreCase))
                {
                    payloadValid = false;
                    messages.Add("payload file hash mismatch");
                }

                payloads = new Dictionary<string, (RunPayload Payload, string Canonical)>(StringComparer.Ordinal);
                var lineNumber = 0;
                foreach (var line in File.ReadLines(payloadPath))
                {
                    lineNumber++;
                    if (string.IsNullOrWhiteSpace(line))
                    {
                        payloadValid = false;
                        messages.Add($"blank payload line {lineNumber}");
                        continue;
                    }

                    try
                    {
                        var payload = JsonSerializer.Deserialize<RunPayload>(line, ManifestOptions)
                            ?? throw new InvalidDataException("empty payload");
                        var canonical = CanonicalJson.Serialize(payload, ManifestOptions);
                        if (!payloads.TryAdd(payload.SourceKey, (payload, canonical)))
                        {
                            payloadValid = false;
                            messages.Add($"duplicate payload source key {payload.SourceKey}");
                        }
                    }
                    catch (Exception error)
                    {
                        payloadValid = false;
                        messages.Add($"invalid payload line {lineNumber}: {error.Message}");
                    }
                }
            }
        }

        var databaseEvents = store?.ReadEvents().ToDictionary(value => value.SourceKey, StringComparer.Ordinal);
        foreach (var pin in manifest.Events)
        {
            if (pin.PayloadIncluded)
            {
                if (payloads is null || !payloads.TryGetValue(pin.SourceKey, out var payload))
                {
                    missing++;
                    messages.Add($"missing payload: {pin.SourceKey}");
                    continue;
                }

                if (!string.Equals(CanonicalJson.HashText(payload.Canonical), pin.PayloadSha256, StringComparison.OrdinalIgnoreCase))
                {
                    changed++;
                    messages.Add($"changed payload: {pin.SourceKey}");
                    continue;
                }

                if (payload.Payload.RawPurgedAt is not null
                    || payload.Payload.RedactedFields is { Count: > 0 })
                {
                    redacted++;
                }
                else
                {
                    included++;
                }
            }
            else if (databaseEvents is not null)
            {
                if (!databaseEvents.TryGetValue(pin.SourceKey, out var stored))
                {
                    missing++;
                    messages.Add($"expired source key: {pin.SourceKey}");
                }
                else if (stored.RawPurgedAt is not null)
                {
                    redacted++;
                }
                else
                {
                    included++;
                }
            }
            else
            {
                included++;
            }
        }

        return new RunVerification(
            manifestValid,
            contentValid,
            payloadValid,
            included,
            missing,
            changed,
            redacted,
            messages);
    }

    private static RunContent Content(RunManifest manifest) => new(
        manifest.Identity,
        manifest.Coverage,
        manifest.Sources,
        manifest.Events,
        manifest.Alerts
            .Select(value => value with { CreatedAt = string.Empty })
            .ToList(),
        manifest.Gaps
            .Select(value => value with { NotedAt = string.Empty })
            .ToList(),
        manifest.Export);

    public static RunPayload CreatePayload(LocalEvent value) => Payload(value, []);

    private static RunIdentity BuildIdentity(RuleSet rules, string schemaRoot)
    {
        var rulePins = rules.Rules
            .OrderBy(value => value.Id, StringComparer.Ordinal)
            .Select(value => new RunRulePin(
                value.Id,
                value.Version,
                value.Sha256 ?? CanonicalJson.HashFile(value.SourcePath)))
            .ToList();
        return new RunIdentity(
            SentinelVersions.EngineVersion,
            SentinelVersions.ContractVersion,
            SentinelVersions.OcsfVersion,
            SentinelVersions.MapperVersion,
            TreeHash(schemaRoot),
            CanonicalJson.Hash(rulePins),
            rulePins,
            rules.PackId,
            rules.PackSha256);
    }

    private static IReadOnlyList<RunSourcePin> BuildSources(
        IReadOnlyList<LocalEvent> events,
        IReadOnlyList<Bookmark> bookmarks)
    {
        return events
            .GroupBy(value => new
            {
                value.SensorId,
                value.HostId,
                value.Source.Channel,
                value.Source.IncarnationId,
                value.Source.ConfigGeneration
            })
            .Select(group =>
            {
                var bookmark = bookmarks.FirstOrDefault(value =>
                    string.Equals(value.SourceId, group.Key.Channel, StringComparison.Ordinal)
                    && string.Equals(value.IncarnationId, group.Key.IncarnationId, StringComparison.Ordinal));
                return new RunSourcePin(
                    group.Key.SensorId,
                    group.Key.HostId,
                    group.Key.Channel,
                    group.Key.IncarnationId,
                    group.Key.ConfigGeneration,
                    bookmark?.EventRecordId,
                    bookmark?.CoverageStartRecordId,
                    bookmark?.SensorSequence,
                    group.Count(),
                    group.Min(value => value.SensorSequence),
                    group.Max(value => value.SensorSequence));
            })
            .OrderBy(value => value.SensorId, StringComparer.Ordinal)
            .ThenBy(value => value.Channel, StringComparer.Ordinal)
            .ThenBy(value => value.IncarnationId, StringComparer.Ordinal)
            .ToList();
    }

    private static RunEventPin EventPin(LocalEvent value) => new(
        value.SourceKey,
        value.SensorSequence,
        value.Kind.ToString(),
        value.Source.EventRecordId,
        Timestamp(value.Time),
        value.LoggedTime is null ? null : Timestamp(value.LoggedTime.Value),
        value.ClockStatus,
        value.OcsfClassUid,
        value.OcsfActivityId,
        value.RawSha256,
        value.OcsfJson is null ? null : CanonicalJson.HashText(NormalizeOcsf(value.OcsfJson)),
        value.RawPurgedAt is null ? null : Timestamp(value.RawPurgedAt.Value),
        false,
        null);

    private static RunManifest ForManifestHash(RunManifest manifest) => manifest with
    {
        ManifestSha256 = string.Empty,
        SigningPublicKeyPem = null,
        Signature = null
    };

    private static void ValidateSignature(RunManifest manifest)
    {
        if (manifest.SigningPublicKeyPem is null && manifest.Signature is null)
        {
            return;
        }

        if (manifest.SigningPublicKeyPem is null || manifest.Signature is null)
        {
            throw new InvalidDataException("run manifest has an incomplete signature.");
        }

        byte[] signature;
        try
        {
            signature = Convert.FromBase64String(manifest.Signature);
        }
        catch (FormatException ex)
        {
            throw new InvalidDataException("run manifest signature is not valid base64.", ex);
        }

        using var key = ECDsa.Create();
        key.ImportFromPem(manifest.SigningPublicKeyPem);
        var payload = Encoding.UTF8.GetBytes(CanonicalJson.Serialize(manifest with { Signature = null }));
        if (!key.VerifyData(
                payload,
                signature,
                HashAlgorithmName.SHA256,
                DSASignatureFormat.Rfc3279DerSequence))
        {
            throw new InvalidDataException("run manifest signature does not match.");
        }
    }

    private static IReadOnlyList<string> NormalizeCustodyRefs(IReadOnlyCollection<string>? refs)
    {
        if (refs is null || refs.Count == 0)
        {
            return [];
        }

        var result = refs
            .Select(value => value.Trim())
            .Where(value => value.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToList();
        if (result.Any(value => value.Length > 512 || value.Any(char.IsControl)))
        {
            throw new ArgumentException("custody references must be at most 512 printable characters.");
        }

        return result;
    }

    private static RunPayload Payload(LocalEvent value, IReadOnlyCollection<string> redactions)
    {
        var redacted = redactions.Count > 0;
        return new RunPayload(
            value.SourceKey,
            value.SensorSequence,
            value.Kind.ToString(),
            Timestamp(value.Time),
            value.LoggedTime is null ? null : Timestamp(value.LoggedTime.Value),
            value.ClockStatus,
            redacted ? null : value.RawPurgedAt is null ? value.RawXml : null,
            value.OcsfJson is null ? null : RedactOcsf(NormalizeOcsf(value.OcsfJson), redactions),
            CanonicalJson.Serialize(value.Unmapped),
            value.RawPurgedAt is null ? null : Timestamp(value.RawPurgedAt.Value),
            redacted ? redactions.OrderBy(item => item, StringComparer.Ordinal).ToList() : null);
    }

    private static readonly HashSet<string> SupportedRedactions = new(StringComparer.Ordinal)
    {
        "raw_xml",
        "process.cmd_line",
        "process.path",
        "user.name"
    };

    private static IReadOnlyList<string> NormalizeRedactions(IReadOnlyCollection<string>? fields)
    {
        if (fields is null || fields.Count == 0)
        {
            return [];
        }

        var result = fields
            .Select(value => value.Trim())
            .Where(value => value.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToList();
        var unknown = result.Where(value => !SupportedRedactions.Contains(value)).ToList();
        if (unknown.Count > 0)
        {
            throw new ArgumentException(
                $"unsupported redaction field(s): {string.Join(", ", unknown)}; "
                + $"supported: {string.Join(", ", SupportedRedactions.Order())}.");
        }

        return result;
    }

    private static string RedactOcsf(string json, IReadOnlyCollection<string> redactions)
    {
        if (redactions.Count == 0)
        {
            return json;
        }

        var root = JsonNode.Parse(json) as JsonObject
            ?? throw new InvalidDataException("OCSF JSON is not an object.");
        var process = root["process"] as JsonObject;
        if (redactions.Contains("process.cmd_line") && process is not null)
        {
            process["cmd_line"] = "[redacted]";
        }

        if (redactions.Contains("process.path") && process?["file"] is JsonObject file)
        {
            file["path"] = "[redacted]";
        }

        if (redactions.Contains("user.name"))
        {
            RedactUser(root["user"] as JsonObject);
            RedactUser(process?["user"] as JsonObject);
            if (root["actor"] is JsonObject actor)
            {
                RedactUser(actor["user"] as JsonObject);
                if (actor["process"] is JsonObject actorProcess)
                {
                    RedactUser(actorProcess["user"] as JsonObject);
                }
            }
        }

        return CanonicalJson.Serialize(root);
    }

    private static void RedactUser(JsonObject? user)
    {
        if (user is not null)
        {
            user["name"] = "[redacted]";
        }
    }

    private static string NormalizeOcsf(string json)
    {
        var root = System.Text.Json.Nodes.JsonNode.Parse(json)
            ?? throw new InvalidDataException("OCSF JSON is empty.");
        if (root is System.Text.Json.Nodes.JsonObject value
            && value["metadata"] is System.Text.Json.Nodes.JsonObject metadata)
        {
            metadata.Remove("processed_time");
        }

        return CanonicalJson.Serialize(root);
    }

    private static string Timestamp(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static string TreeHash(string root)
    {
        var directory = new DirectoryInfo(root);
        if (!directory.Exists)
        {
            throw new DirectoryNotFoundException($"schema directory not found: {root}");
        }

        using var hash = SHA256.Create();
        foreach (var file in directory.EnumerateFiles("*", SearchOption.AllDirectories)
                     .OrderBy(value => Path.GetRelativePath(root, value.FullName), StringComparer.Ordinal))
        {
            var relative = Path.GetRelativePath(root, file.FullName).Replace('\\', '/');
            var relativeBytes = Encoding.UTF8.GetBytes(relative + "\n" + file.Length + "\n");
            hash.TransformBlock(relativeBytes, 0, relativeBytes.Length, null, 0);
            var bytes = File.ReadAllBytes(file.FullName);
            hash.TransformBlock(bytes, 0, bytes.Length, null, 0);
        }

        hash.TransformFinalBlock([], 0, 0);
        return Convert.ToHexString(hash.Hash!).ToLowerInvariant();
    }
}
