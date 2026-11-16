using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Sentinel.Core.Model;
using Sentinel.Core.Sysmon;

namespace Sentinel.Core.Ocsf;

public sealed record MappingContext(
    string SensorId,
    string HostId,
    string IncarnationId,
    DateTimeOffset ProcessedTime);

public sealed record MappedRecord(
    DateTimeOffset Time,
    RecordKind Kind,
    string? OcsfJson,
    int? ClassUid,
    int? ActivityId,
    IReadOnlyList<UnmappedField> Unmapped,
    ProcessContext? Process,
    NetworkContext? Network,
    string? HostName,
    string? UserName);

public static class OcsfMapper
{
    public const int ProcessActivityClassUid = 1007;
    public const int NetworkActivityClassUid = 4001;
    public const int ProcessLaunchActivityId = 1;
    public const int ProcessTerminateActivityId = 2;
    public const int NetworkOpenActivityId = 1;
    public const int NetworkUnknownActivityId = 0;

    private static readonly string[] SysmonTimeFormats =
    [
        "yyyy-MM-dd HH:mm:ss.fff",
        "yyyy-MM-dd HH:mm:ss.fffffff",
        "yyyy-MM-dd HH:mm:ss",
        "yyyy-MM-dd'T'HH:mm:ss.fff'Z'",
        "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'"
    ];

    private static readonly Dictionary<string, (int Id, string Name)> HashAlgorithms = new(StringComparer.OrdinalIgnoreCase)
    {
        ["MD5"] = (1, "MD5"),
        ["SHA1"] = (2, "SHA-1"),
        ["SHA256"] = (3, "SHA-256"),
        ["SHA512"] = (4, "SHA-512"),
        ["IMPHASH"] = (18, "Imphash")
    };

    public static MappedRecord Map(SysmonRecord record, MappingContext context, NetworkOutcome outcome)
    {
        if (!string.Equals(record.Channel, SysmonXmlReader.SysmonChannel, StringComparison.OrdinalIgnoreCase))
        {
            throw new SourceRecordException(
                $"Unsupported channel '{record.Channel}'; V0.1 reads only '{SysmonXmlReader.SysmonChannel}'.");
        }

        return record.EventId switch
        {
            1 => MapProcess(record, context),
            3 => MapNetwork(record, context, outcome),
            5 => MapProcessTerminate(record, context),
            _ => MapSourceHealth(record)
        };
    }

    private static MappedRecord MapProcess(SysmonRecord record, MappingContext context)
    {
        var unmapped = new List<UnmappedField>();
        var eventTime = ResolveEventTime(record, unmapped);
        var image = record.Get("Image");
        var user = record.Get("User");
        var processGuid = record.Get("ProcessGuid");
        var processId = record.GetInt("ProcessId");
        var processName = image is null ? null : WindowsPath.FileName(image);

        var target = BuildProcess(
            processGuid,
            processId,
            image,
            record.Get("CommandLine"),
            user,
            record.Get("CurrentDirectory"),
            record.Get("IntegrityLevel"),
            record.Get("Hashes"),
            record.Get("FileVersion"),
            record.Get("Description"),
            record.Get("Product"),
            record.Get("Company"),
            record.Get("OriginalFileName"),
            unmapped);

        var parentGuid = record.Get("ParentProcessGuid");
        var parentPid = record.GetInt("ParentProcessId");
        var parentImage = record.Get("ParentImage");
        var parentProcess = BuildProcess(
            parentGuid,
            parentPid,
            parentImage,
            record.Get("ParentCommandLine"),
            record.Get("ParentUser"),
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            unmapped);

        var actor = new JsonObject
        {
            ["process"] = parentProcess
        };
        var parentUser = record.Get("ParentUser");
        if (!string.IsNullOrEmpty(parentUser))
        {
            actor["user"] = UserObject(parentUser);
        }

        var ocsf = new JsonObject
        {
            ["activity_id"] = ProcessLaunchActivityId,
            ["activity_name"] = "Launch",
            ["category_uid"] = 1,
            ["category_name"] = "System Activity",
            ["class_uid"] = ProcessActivityClassUid,
            ["class_name"] = "Process Activity",
            ["type_uid"] = ProcessActivityClassUid * 100 + ProcessLaunchActivityId,
            ["type_name"] = "Process Activity: Launch",
            ["severity_id"] = 1,
            ["severity"] = "Informational",
            ["time"] = EpochMillis(eventTime),
            ["metadata"] = Metadata(record, context),
            ["device"] = Device(record.Computer),
            ["actor"] = actor,
            ["process"] = target
        };

        AddUnmapped(unmapped, record, "RuleName", "Detection metadata; not an OCSF event field.");
        AddUnmapped(unmapped, record, "LogonGuid", "No logon-session identity is mapped in this profile.");
        AddUnmapped(unmapped, record, "LogonId", "No logon-session identity is mapped in this profile.");
        AddUnmapped(unmapped, record, "TerminalSessionId", "No terminal-session identity is mapped in this profile.");

        var process = new ProcessContext(
            processGuid,
            processId,
            processName,
            record.Get("CommandLine"),
            image,
            parentGuid,
            parentPid,
            parentImage is null ? null : WindowsPath.FileName(parentImage),
            record.Get("ParentCommandLine"),
            user);

        return new MappedRecord(
            eventTime,
            RecordKind.Data,
            ocsf.ToJsonString(),
            ProcessActivityClassUid,
            ProcessLaunchActivityId,
            unmapped,
            process,
            null,
            record.Computer,
            user);
    }

    private static MappedRecord MapProcessTerminate(SysmonRecord record, MappingContext context)
    {
        var unmapped = new List<UnmappedField>();
        var eventTime = ResolveEventTime(record, unmapped);
        var image = record.Get("Image");
        var user = record.Get("User");
        var processGuid = record.Get("ProcessGuid");
        var processId = record.GetInt("ProcessId");
        var processName = image is null ? null : WindowsPath.FileName(image);

        var target = BuildProcess(
            processGuid,
            processId,
            image,
            null,
            user,
            null,
            null,
            record.Get("Hashes"),
            record.Get("FileVersion"),
            record.Get("Description"),
            record.Get("Product"),
            record.Get("Company"),
            record.Get("OriginalFileName"),
            unmapped);

        var ocsf = new JsonObject
        {
            ["activity_id"] = ProcessTerminateActivityId,
            ["activity_name"] = "Terminate",
            ["category_uid"] = 1,
            ["category_name"] = "System Activity",
            ["class_uid"] = ProcessActivityClassUid,
            ["class_name"] = "Process Activity",
            ["type_uid"] = ProcessActivityClassUid * 100 + ProcessTerminateActivityId,
            ["type_name"] = "Process Activity: Terminate",
            ["severity_id"] = 1,
            ["severity"] = "Informational",
            ["time"] = EpochMillis(eventTime),
            ["metadata"] = Metadata(record, context),
            ["device"] = Device(record.Computer),
            ["actor"] = new JsonObject { ["process"] = target.DeepClone() },
            ["process"] = target
        };

        AddUnmapped(unmapped, record, "RuleName", "Detection metadata; not an OCSF event field.");

        var process = new ProcessContext(
            processGuid,
            processId,
            processName,
            null,
            image,
            null,
            null,
            null,
            null,
            user);

        return new MappedRecord(
            eventTime,
            RecordKind.Data,
            ocsf.ToJsonString(),
            ProcessActivityClassUid,
            ProcessTerminateActivityId,
            unmapped,
            process,
            null,
            record.Computer,
            user);
    }

    private static MappedRecord MapNetwork(SysmonRecord record, MappingContext context, NetworkOutcome outcome)
    {
        var unmapped = new List<UnmappedField>();
        var eventTime = ResolveEventTime(record, unmapped);
        var protocol = record.Get("Protocol");
        var initiated = record.GetBool("Initiated");
        var established = outcome == NetworkOutcome.EstablishedOutbound
            && initiated == true
            && string.Equals(protocol, "tcp", StringComparison.OrdinalIgnoreCase);

        var activityId = established ? NetworkOpenActivityId : NetworkUnknownActivityId;
        var activityName = established ? "Open" : "Unknown";
        var directionId = established
            ? 2
            : outcome == NetworkOutcome.InboundListener ? 1 : 0;
        var direction = established ? "Outbound" : outcome == NetworkOutcome.InboundListener ? "Inbound" : "Unknown";

        if (!established)
        {
            unmapped.Add(new UnmappedField(
                "network.activity",
                $"activity_id=Unknown: outcome={outcome}, initiated={initiated?.ToString() ?? "missing"}, protocol={protocol ?? "missing"}."));
        }

        var image = record.Get("Image");
        var processGuid = record.Get("ProcessGuid");
        var processId = record.GetInt("ProcessId");
        var processName = image is null ? null : WindowsPath.FileName(image);
        var user = record.Get("User");

        var process = new JsonObject();
        if (processId.HasValue)
        {
            process["pid"] = processId.Value;
        }

        if (processGuid is not null)
        {
            process["uid"] = processGuid;
        }

        if (processName is not null)
        {
            process["name"] = processName;
        }

        if (image is not null)
        {
            process["file"] = FileObject(image, null, null, null, null, null, null, unmapped);
        }

        if (!string.IsNullOrEmpty(user))
        {
            process["user"] = UserObject(user);
        }

        var connectionInfo = new JsonObject
        {
            ["direction_id"] = directionId,
            ["direction"] = direction
        };

        if (protocol is not null)
        {
            connectionInfo["protocol_name"] = protocol;
            var protocolNumber = protocol.ToLowerInvariant() switch
            {
                "tcp" => 6,
                "udp" => 17,
                _ => (int?)null
            };
            if (protocolNumber.HasValue)
            {
                connectionInfo["protocol_num"] = protocolNumber.Value;
            }
        }

        var srcEndpoint = Endpoint(
            record.Get("SourceIp"),
            record.GetInt("SourcePort"),
            record.Get("SourceHostname"),
            record.Get("SourcePortName"));
        var dstEndpoint = Endpoint(
            record.Get("DestinationIp"),
            record.GetInt("DestinationPort"),
            record.Get("DestinationHostname"),
            record.Get("DestinationPortName"));

        var ocsf = new JsonObject
        {
            ["activity_id"] = activityId,
            ["activity_name"] = activityName,
            ["category_uid"] = 4,
            ["category_name"] = "Network Activity",
            ["class_uid"] = NetworkActivityClassUid,
            ["class_name"] = "Network Activity",
            ["type_uid"] = NetworkActivityClassUid * 100 + activityId,
            ["type_name"] = $"Network Activity: {activityName}",
            ["severity_id"] = 1,
            ["severity"] = "Informational",
            ["time"] = EpochMillis(eventTime),
            ["metadata"] = Metadata(record, context, profiles: ["host"]),
            ["device"] = Device(record.Computer),
            ["actor"] = BuildActor(process, user),
            ["src_endpoint"] = srcEndpoint,
            ["dst_endpoint"] = dstEndpoint,
            ["initiator_id"] = established ? 1 : 0,
            ["initiator"] = established ? "Source Endpoint" : "Unknown",
            ["connection_info"] = connectionInfo
        };

        AddUnmapped(unmapped, record, "RuleName", "Detection metadata; not an OCSF event field.");
        AddUnmapped(unmapped, record, "SourceIsIpv6", "IPv6 flag; the endpoint carries the address, not the flag.");
        AddUnmapped(unmapped, record, "DestinationIsIpv6", "IPv6 flag; the endpoint carries the address, not the flag.");

        var network = new NetworkContext(
            protocol,
            initiated,
            record.Get("SourceIp"),
            record.GetInt("SourcePort"),
            record.Get("SourceHostname"),
            record.Get("DestinationIp"),
            record.GetInt("DestinationPort"),
            record.Get("DestinationHostname"),
            outcome);

        var processContext = new ProcessContext(
            processGuid,
            processId,
            processName,
            null,
            image,
            null,
            null,
            null,
            null,
            user);

        return new MappedRecord(
            eventTime,
            RecordKind.Data,
            ocsf.ToJsonString(),
            NetworkActivityClassUid,
            activityId,
            unmapped,
            processContext,
            network,
            record.Computer,
            user);
    }

    private static MappedRecord MapSourceHealth(SysmonRecord record)
    {
        var time = ResolveEventTime(record, []);
        return new MappedRecord(
            time,
            RecordKind.SourceHealth,
            null,
            null,
            null,
            [new UnmappedField($"EventID:{record.EventId}", "Retained as source-health context; no OCSF data mapping in V0.1.")],
            null,
            null,
            record.Computer,
            null);
    }

    private static JsonObject BuildProcess(
        string? guid,
        int? pid,
        string? image,
        string? commandLine,
        string? user,
        string? workingDirectory,
        string? integrity,
        string? hashes,
        string? fileVersion,
        string? description,
        string? product,
        string? company,
        string? originalFileName,
        List<UnmappedField> unmapped)
    {
        var process = new JsonObject();
        if (pid.HasValue)
        {
            process["pid"] = pid.Value;
        }

        if (guid is not null)
        {
            process["uid"] = guid;
        }

        if (image is not null)
        {
            process["name"] = WindowsPath.FileName(image);
            process["file"] = FileObject(
                image,
                hashes,
                fileVersion,
                description,
                product,
                company,
                originalFileName,
                unmapped);
        }

        if (commandLine is not null)
        {
            process["cmd_line"] = commandLine;
        }

        if (workingDirectory is not null)
        {
            process["working_directory"] = workingDirectory;
        }

        if (integrity is not null)
        {
            process["integrity"] = integrity;
        }

        if (!string.IsNullOrEmpty(user))
        {
            process["user"] = UserObject(user);
        }

        return process;
    }

    private static JsonObject FileObject(
        string path,
        string? hashes,
        string? version,
        string? description,
        string? product,
        string? company,
        string? originalFileName,
        List<UnmappedField> unmapped)
    {
        var extension = WindowsPath.Extension(path) ?? string.Empty;
        var executable = extension.Equals(".exe", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".dll", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".sys", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".scr", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".com", StringComparison.OrdinalIgnoreCase);

        var file = new JsonObject
        {
            ["name"] = WindowsPath.FileName(path),
            ["path"] = path,
            ["type_id"] = executable ? 8 : 1,
            ["type"] = executable ? "Executable File" : "Regular File"
        };

        if (version is not null)
        {
            file["version"] = version;
        }

        if (description is not null)
        {
            file["desc"] = description;
        }

        if (company is not null)
        {
            file["company_name"] = company;
        }

        if (originalFileName is not null)
        {
            file["internal_name"] = originalFileName;
        }

        if (product is not null)
        {
            file["product"] = new JsonObject { ["name"] = product };
        }

        if (hashes is not null)
        {
            var fingerprints = ParseHashes(hashes, unmapped);
            if (fingerprints.Count > 0)
            {
                file["hashes"] = fingerprints;
            }
        }

        return file;
    }

    private static JsonArray ParseHashes(string hashes, List<UnmappedField> unmapped)
    {
        var array = new JsonArray();
        foreach (var entry in hashes.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separator = entry.IndexOf('=');
            if (separator <= 0 || separator == entry.Length - 1)
            {
                unmapped.Add(new UnmappedField("Hashes", $"Unparsed hash entry '{entry}'."));
                continue;
            }

            var algorithm = entry[..separator].Trim();
            var value = entry[(separator + 1)..].Trim();
            if (!HashAlgorithms.TryGetValue(algorithm, out var mapped))
            {
                unmapped.Add(new UnmappedField($"Hashes.{algorithm}", "Unsupported hash algorithm."));
                continue;
            }

            array.Add(new JsonObject
            {
                ["algorithm_id"] = mapped.Id,
                ["algorithm"] = mapped.Name,
                ["value"] = value
            });
        }

        return array;
    }

    private static JsonObject Endpoint(string? ip, int? port, string? hostname, string? serviceName)
    {
        var endpoint = new JsonObject();
        if (!string.IsNullOrEmpty(ip))
        {
            endpoint["ip"] = ip;
        }

        if (port.HasValue)
        {
            endpoint["port"] = port.Value;
        }

        if (!string.IsNullOrEmpty(hostname))
        {
            endpoint["hostname"] = hostname;
        }

        if (!string.IsNullOrEmpty(serviceName))
        {
            endpoint["svc_name"] = serviceName;
        }

        return endpoint;
    }

    private static JsonObject BuildActor(JsonObject process, string? user)
    {
        var actor = new JsonObject
        {
            ["process"] = process
        };
        if (!string.IsNullOrEmpty(user))
        {
            actor["user"] = UserObject(user);
        }

        return actor;
    }

    private static JsonObject UserObject(string name) =>
        new() { ["name"] = name };

    private static JsonObject Device(string hostname) => new()
    {
        ["hostname"] = hostname,
        ["type_id"] = 0
    };

    private static JsonObject Metadata(SysmonRecord record, MappingContext context, string[]? profiles = null)
    {
        var metadata = new JsonObject
        {
            ["version"] = SentinelVersions.OcsfVersion,
            ["product"] = new JsonObject
            {
                ["name"] = "Sentinel",
                ["vendor_name"] = "Sentinel",
                ["version"] = "0.1.0"
            },
            ["uid"] = $"{context.SensorId}|{record.Channel}|{context.IncarnationId}|{record.EventRecordId}",
            ["original_event_uid"] = record.EventRecordId.ToString(CultureInfo.InvariantCulture),
            ["event_code"] = record.EventId.ToString(CultureInfo.InvariantCulture),
            ["log_name"] = record.Channel,
            ["log_provider"] = record.Provider,
            ["logged_time"] = EpochMillis(record.TimeCreated),
            ["processed_time"] = EpochMillis(context.ProcessedTime)
        };

        if (profiles is not null && profiles.Length > 0)
        {
            metadata["profiles"] = new JsonArray(profiles.Select(p => (JsonNode)JsonValue.Create(p)!).ToArray());
        }

        return metadata;
    }

    private static DateTimeOffset ResolveEventTime(SysmonRecord record, List<UnmappedField> unmapped)
    {
        var raw = record.Get("UtcTime");
        if (raw is not null)
        {
            if (DateTimeOffset.TryParseExact(
                    raw,
                    SysmonTimeFormats,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                    out var parsed))
            {
                return parsed;
            }

            if (DateTimeOffset.TryParse(
                    raw,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                    out parsed))
            {
                return parsed;
            }

            unmapped.Add(new UnmappedField("UtcTime", $"Unparseable source time '{raw}'; time mapped from TimeCreated."));
        }

        return record.TimeCreated.ToUniversalTime();
    }

    private static void AddUnmapped(List<UnmappedField> unmapped, SysmonRecord record, string field, string reason)
    {
        if (record.Get(field) is not null)
        {
            unmapped.Add(new UnmappedField(field, reason));
        }
    }

    private static long EpochMillis(DateTimeOffset value) => value.ToUnixTimeMilliseconds();
}
