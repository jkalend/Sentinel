using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Sentinel.Core.Model;
using Sentinel.Core.Rules;
using Sentinel.Core.Store;

namespace Sentinel.Core.Transport;

public sealed class TransportCollector : IDisposable
{
    public const string Format = "sentinel.transport/v1";
    private static readonly JsonSerializerOptions JsonOptions = TransportJson.Options;

    private readonly HttpListener _listener = new();
    private readonly string _root;
    private readonly RuleSet? _rules;
    private readonly string? _token;
    private readonly IReadOnlySet<string>? _allowedSensors;
    private readonly CancellationTokenSource _shutdown = new();
    private Task? _loop;
    private bool _started;

    public TransportCollector(
        string prefix,
        string root,
        RuleSet? rules = null,
        string? token = null,
        IEnumerable<string>? allowedSensors = null)
    {
        if (!prefix.EndsWith('/'))
        {
            prefix += "/";
        }

        _listener.Prefixes.Add(prefix);
        _root = Path.GetFullPath(root);
        Directory.CreateDirectory(_root);
        _rules = rules;
        _token = token;
        _allowedSensors = allowedSensors is null
            ? null
            : new HashSet<string>(allowedSensors, StringComparer.Ordinal);
        Prefix = prefix;
    }

    public string Prefix { get; }

    public void Start()
    {
        if (_started)
        {
            throw new InvalidOperationException("collector is already started.");
        }

        _listener.Start();
        _started = true;
        _loop = Task.Run(LoopAsync);
    }

    public async Task StopAsync()
    {
        if (!_started)
        {
            return;
        }

        _shutdown.Cancel();
        _listener.Stop();
        if (_loop is not null)
        {
            try
            {
                await _loop;
            }
            catch (HttpListenerException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
        }

        _started = false;
    }

    public async Task<TransportAck> HandleAsync(TransportBatch batch, string? token = null)
    {
        if (!string.IsNullOrEmpty(_token)
            && !string.Equals(_token, token, StringComparison.Ordinal))
        {
            throw new UnauthorizedAccessException("transport token is invalid.");
        }

        if (_allowedSensors is not null && !_allowedSensors.Contains(batch.SensorId))
        {
            throw new UnauthorizedAccessException($"sensor is not enrolled: {batch.SensorId}");
        }

        ValidateBatch(batch);
        var path = Path.Combine(_root, SafeName(batch.SensorId) + ".db");
        using var store = new SqliteEventStore(path);
        store.Initialize();
        store.EnsureIdentity(batch.SensorId, batch.HostId);
        store.Enroll(
            batch.SensorId,
            batch.HostId,
            configGeneration: batch.ConfigGeneration,
            clockOffsetMs: batch.ClockOffsetMs,
            clockUncertaintyMs: batch.ClockUncertaintyMs);

        foreach (var gap in batch.Gaps)
        {
            store.InsertGap(gap);
        }

        var accepted = 0;
        var duplicates = 0;
        for (var index = 0; index < batch.Events.Count; index++)
        {
            var value = batch.Events[index];
            var alerts = _rules is null ? [] : RuleEvaluator.Evaluate(value, _rules);
            var bookmark = index == batch.Events.Count - 1 ? batch.Bookmark : null;
            var result = store.Append(value, alerts, bookmark);
            if (result.Status == AppendStatus.Accepted)
            {
                accepted++;
            }
            else
            {
                duplicates++;
            }
        }

        return new TransportAck(
            Format,
            batch.BatchId,
            accepted,
            duplicates,
            batch.Gaps.Count,
            batch.LastSensorSequence,
            "committed");
    }

    private async Task LoopAsync()
    {
        while (!_shutdown.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync();
            }
            catch (HttpListenerException) when (_shutdown.IsCancellationRequested)
            {
                break;
            }
            catch (ObjectDisposedException) when (_shutdown.IsCancellationRequested)
            {
                break;
            }

            _ = Task.Run(() => HandleContextAsync(context));
        }
    }

    private async Task HandleContextAsync(HttpListenerContext context)
    {
        try
        {
            if (!string.Equals(context.Request.HttpMethod, "POST", StringComparison.Ordinal)
                || !context.Request.Url!.AbsolutePath.EndsWith("/v1/batches", StringComparison.Ordinal))
            {
                await WriteAsync(context.Response, 404, new { error = "not found" });
                return;
            }

            var token = context.Request.Headers["X-Sentinel-Token"];
            string body;
            using (var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8))
            {
                body = await reader.ReadToEndAsync();
            }

            TransportBatch? batch;
            try
            {
                batch = JsonSerializer.Deserialize<TransportBatch>(body, JsonOptions);
            }
            catch (JsonException error)
            {
                await WriteAsync(context.Response, 400, new { error = error.Message });
                return;
            }

            if (batch is null)
            {
                await WriteAsync(context.Response, 400, new { error = "empty batch" });
                return;
            }

            try
            {
                var ack = await HandleAsync(batch, token);
                await WriteAsync(context.Response, 200, ack);
            }
            catch (UnauthorizedAccessException error)
            {
                await WriteAsync(context.Response, 401, new { error = error.Message });
            }
            catch (ArgumentException error)
            {
                await WriteAsync(context.Response, 400, new { error = error.Message });
            }
        }
        catch (Exception error)
        {
            try
            {
                await WriteAsync(context.Response, 500, new { error = error.Message });
            }
            catch
            {
            }
        }
    }

    private static async Task WriteAsync(HttpListenerResponse response, int status, object value)
    {
        response.StatusCode = status;
        response.ContentType = "application/json";
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value, JsonOptions));
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes);
        response.Close();
    }

    private static void ValidateBatch(TransportBatch batch)
    {
        if (!string.Equals(batch.Format, Format, StringComparison.Ordinal))
        {
            throw new ArgumentException($"unsupported transport format: {batch.Format}");
        }

        if (string.IsNullOrWhiteSpace(batch.SensorId)
            || string.IsNullOrWhiteSpace(batch.HostId)
            || string.IsNullOrWhiteSpace(batch.IncarnationId)
            || string.IsNullOrWhiteSpace(batch.Channel))
        {
            throw new ArgumentException("batch identity is incomplete.");
        }

        if (batch.Events.Count == 0 || batch.Events.Count > 10_000)
        {
            throw new ArgumentException("batch event count is outside the accepted range.");
        }

        if (batch.FirstSensorSequence < 1 || batch.LastSensorSequence < batch.FirstSensorSequence)
        {
            throw new ArgumentException("batch sequence range is invalid.");
        }

        var keys = new HashSet<string>(StringComparer.Ordinal);
        long previous = -1;
        foreach (var value in batch.Events)
        {
            if (!string.Equals(value.SensorId, batch.SensorId, StringComparison.Ordinal)
                || !string.Equals(value.HostId, batch.HostId, StringComparison.Ordinal))
            {
                throw new ArgumentException($"event identity does not match batch: {value.SourceKey}");
            }

            if (value.SensorSequence <= previous)
            {
                throw new ArgumentException("batch events are not in increasing sensor-sequence order.");
            }

            previous = value.SensorSequence;
            if (!keys.Add(value.SourceKey))
            {
                throw new ArgumentException($"batch contains duplicate source key: {value.SourceKey}");
            }
        }

        if (batch.Events[0].SensorSequence != batch.FirstSensorSequence
            || batch.Events[^1].SensorSequence != batch.LastSensorSequence)
        {
            throw new ArgumentException("batch sequence range does not match its events.");
        }

        var expected = TransportJson.PayloadHash(batch.Events, batch.Gaps);
        if (!string.Equals(expected, batch.PayloadSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("batch payload hash does not match.");
        }
    }

    private static string SafeName(string sensorId)
    {
        var clean = new string(sensorId.Where(character => char.IsLetterOrDigit(character) || character is '-' or '_').ToArray());
        if (clean.Length == 0)
        {
            clean = "sensor";
        }

        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sensorId)))
            .ToLowerInvariant()[..8];
        return $"{clean[..Math.Min(clean.Length, 48)]}-{hash}";
    }

    public void Dispose()
    {
        if (_started)
        {
            _shutdown.Cancel();
            _listener.Close();
        }

        _shutdown.Dispose();
    }
}
