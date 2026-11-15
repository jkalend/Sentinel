using System.Text.Json;
using Sentinel.Core.Model;
using Sentinel.Core.Runs;

namespace Sentinel.Core.Transport;

internal static class TransportJson
{
    public static JsonSerializerOptions Options { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true
    };

    public static string PayloadHash(
        IReadOnlyList<LocalEvent> events,
        IReadOnlyList<CoverageGap> gaps) =>
        CanonicalJson.Hash(new { events, gaps });
}
