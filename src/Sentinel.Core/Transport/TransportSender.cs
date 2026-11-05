using System.Net.Http.Json;
using System.Text.Json;
using Sentinel.Core.Model;
using Sentinel.Core.Store;

namespace Sentinel.Core.Transport;

public sealed class TransportSender
{
    private static readonly JsonSerializerOptions JsonOptions = TransportJson.Options;
    private readonly string _databasePath;
    private readonly Uri _collector;
    private readonly string? _token;
    private readonly int _batchSize;
    private readonly int _retries;
    private readonly HttpClient _client;

    public TransportSender(
        string databasePath,
        string collectorUrl,
        string? token = null,
        int batchSize = 100,
        int retries = 3,
        HttpClient? client = null)
    {
        if (!collectorUrl.EndsWith('/'))
        {
            collectorUrl += "/";
        }

        _databasePath = Path.GetFullPath(databasePath);
        _collector = new Uri(new Uri(collectorUrl), "v1/batches");
        _token = token;
        _batchSize = Math.Clamp(batchSize, 1, 10_000);
        _retries = Math.Clamp(retries, 0, 20);
        _client = client ?? new HttpClient { Timeout = TimeSpan.FromSeconds(120) };
    }

    public async Task<PushResult> PushAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_databasePath))
        {
            throw new FileNotFoundException($"source database not found: {_databasePath}");
        }

        using var store = new SqliteEventStore(_databasePath);
        store.Initialize();
        var stateKey = _collector.ToString();
        var ack = store.GetTransportAck(stateKey);
        var candidates = store.ReadEventsAfter(ack, _batchSize);
        if (candidates.Count == 0)
        {
            return new PushResult(0, 0, 0, ack);
        }

        var first = candidates[0];
        var events = candidates
            .TakeWhile(value => string.Equals(value.SensorId, first.SensorId, StringComparison.Ordinal)
                             && string.Equals(value.HostId, first.HostId, StringComparison.Ordinal)
                             && string.Equals(value.Source.Channel, first.Source.Channel, StringComparison.Ordinal)
                             && string.Equals(value.Source.IncarnationId, first.Source.IncarnationId, StringComparison.Ordinal))
            .ToList();
        var last = events[^1];

        var gaps = store.ReadGaps();
        var bookmark = store.GetBookmark(first.Source.Channel);
        var enrollment = store.GetEnrollment();
        var update = new BookmarkUpdate(
            first.Source.Channel,
            first.Source.IncarnationId,
            last.Source.ConfigGeneration,
            last.Source.EventRecordId,
            bookmark?.CoverageStartRecordId,
            bookmark?.BookmarkXml);
        var batch = new TransportBatch(
            TransportCollector.Format,
            Guid.NewGuid().ToString("D"),
            DateTimeOffset.UtcNow.ToString("O"),
            first.SensorId,
            first.HostId,
            first.Source.IncarnationId,
            first.Source.Channel,
            first.Source.ConfigGeneration,
            enrollment?.ClockOffsetMs ?? 0,
            enrollment?.ClockUncertaintyMs ?? 0,
            first.SensorSequence,
            last.SensorSequence,
            update,
            events,
            gaps,
            TransportJson.PayloadHash(events, gaps));

        Exception? lastError = null;
        for (var attempt = 0; attempt <= _retries; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, _collector)
            {
                Content = JsonContent.Create(batch, options: JsonOptions)
            };
            if (!string.IsNullOrEmpty(_token))
            {
                request.Headers.Add("X-Sentinel-Token", _token);
            }

            try
            {
                using var response = await _client.SendAsync(request, cancellationToken);
                if (!response.IsSuccessStatusCode)
                {
                    lastError = new TransportException($"collector returned {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync(cancellationToken)}");
                }
                else
                {
                    var ackValue = await response.Content.ReadFromJsonAsync<TransportAck>(JsonOptions, cancellationToken)
                        ?? throw new TransportException("collector returned an empty acknowledgement.");
                    if (!string.Equals(ackValue.Format, TransportCollector.Format, StringComparison.Ordinal)
                        || !string.Equals(ackValue.BatchId, batch.BatchId, StringComparison.Ordinal)
                        || ackValue.Accepted + ackValue.Duplicates != batch.Events.Count
                        || ackValue.LastSensorSequence != batch.LastSensorSequence)
                    {
                        throw new TransportException("collector acknowledgement does not match the batch.");
                    }

                    store.SetTransportAck(stateKey, first.Source.Channel, ackValue.LastSensorSequence);
                    return new PushResult(1, ackValue.Accepted, ackValue.Duplicates, ackValue.LastSensorSequence);
                }
            }
            catch (Exception error) when (!cancellationToken.IsCancellationRequested
                                          && (error is not TransportException || attempt < _retries))
            {
                lastError = error;
            }

            if (attempt < _retries)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(200 * (attempt + 1)), cancellationToken);
            }
        }

        throw new TransportException($"transport failed after {_retries + 1} attempts: {lastError?.Message}", lastError!);
    }

    public async Task<PushResult> PushAllAsync(CancellationToken cancellationToken = default)
    {
        var batches = 0;
        var accepted = 0;
        var duplicates = 0;
        long last = 0;
        while (true)
        {
            var result = await PushAsync(cancellationToken);
            if (result.Batches == 0)
            {
                return new PushResult(batches, accepted, duplicates, last);
            }

            batches += result.Batches;
            accepted += result.Accepted;
            duplicates += result.Duplicates;
            last = result.LastAckedSequence;
        }
    }
}
