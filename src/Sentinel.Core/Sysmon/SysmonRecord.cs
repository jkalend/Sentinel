using System.Globalization;
using System.Xml.Linq;

namespace Sentinel.Core.Sysmon;

public sealed record SysmonRecord(
    int EventId,
    long EventRecordId,
    string Channel,
    string Provider,
    string? ProviderGuid,
    string Computer,
    DateTimeOffset TimeCreated,
    string RawXml,
    IReadOnlyDictionary<string, string> Data)
{
    public string? Get(string name) =>
        Data.TryGetValue(name, out var value) ? value : null;

    public int? GetInt(string name) =>
        int.TryParse(Get(name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;

    public bool? GetBool(string name) =>
        bool.TryParse(Get(name), out var value) ? value : null;
}

public sealed class SourceRecordException : Exception
{
    public SourceRecordException(string message, Exception? inner = null)
        : base(message, inner)
    {
    }
}

public static class SysmonXmlReader
{
    public const string SysmonChannel = "Microsoft-Windows-Sysmon/Operational";

    public static SysmonRecord Parse(string rawXml)
    {
        if (string.IsNullOrWhiteSpace(rawXml))
        {
            throw new SourceRecordException("Event XML is empty.");
        }

        XDocument document;
        try
        {
            document = XDocument.Parse(rawXml, LoadOptions.PreserveWhitespace);
        }
        catch (Exception ex) when (ex is System.Xml.XmlException or InvalidOperationException)
        {
            throw new SourceRecordException("Event XML is malformed.", ex);
        }

        var system = document.Descendants().FirstOrDefault(e => e.Name.LocalName == "System")
            ?? throw new SourceRecordException("Event XML has no System element.");

        var provider = system.Elements().FirstOrDefault(e => e.Name.LocalName == "Provider");
        var eventIdText = ChildValue(system, "EventID");
        var recordIdText = ChildValue(system, "EventRecordID");
        var channel = ChildValue(system, "Channel");
        var computer = ChildValue(system, "Computer");
        var timeCreatedText = system.Elements()
            .FirstOrDefault(e => e.Name.LocalName == "TimeCreated")
            ?.Attribute("SystemTime")?.Value;

        if (provider is null)
        {
            throw new SourceRecordException("Event XML has no Provider element.");
        }

        if (!int.TryParse(eventIdText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var eventId))
        {
            throw new SourceRecordException($"EventID is not an integer: '{eventIdText}'.");
        }

        if (!long.TryParse(recordIdText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var recordId))
        {
            throw new SourceRecordException($"EventRecordID is not an integer: '{recordIdText}'.");
        }

        if (string.IsNullOrWhiteSpace(channel))
        {
            throw new SourceRecordException("Event XML has no Channel.");
        }

        if (string.IsNullOrWhiteSpace(computer))
        {
            throw new SourceRecordException("Event XML has no Computer.");
        }

        var timeCreated = ParseTimestamp(timeCreatedText, "TimeCreated");

        var data = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var eventData = document.Descendants().FirstOrDefault(e => e.Name.LocalName == "EventData");
        if (eventData is not null)
        {
            foreach (var element in eventData.Elements().Where(e => e.Name.LocalName == "Data"))
            {
                var name = element.Attribute("Name")?.Value;
                if (string.IsNullOrEmpty(name))
                {
                    continue;
                }

                if (!data.TryAdd(name, element.Value))
                {
                    throw new SourceRecordException($"Duplicate EventData field '{name}'.");
                }
            }
        }

        return new SysmonRecord(
            eventId,
            recordId,
            channel,
            provider.Attribute("Name")?.Value ?? string.Empty,
            provider.Attribute("Guid")?.Value,
            computer,
            timeCreated,
            rawXml,
            data);
    }

    public static DateTimeOffset ParseTimestamp(string? value, string field)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new SourceRecordException($"{field} is missing.");
        }

        if (!DateTimeOffset.TryParse(
                value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind | DateTimeStyles.AssumeUniversal,
                out var parsed))
        {
            throw new SourceRecordException($"{field} is not a timestamp: '{value}'.");
        }

        return parsed;
    }

    private static string? ChildValue(XElement parent, string localName) =>
        parent.Elements().FirstOrDefault(e => e.Name.LocalName == localName)?.Value;
}
