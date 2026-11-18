using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace PostalService.BuildingBlocks.Infrastructure.Messaging;

internal static class OutboxTelemetry
{
    public const string ActivitySourceName = "PostalService.Outbox";
    public static readonly ActivitySource ActivitySource = new(ActivitySourceName);
}

public sealed class OutboxOptions
{
    public const string SectionName = "Outbox";

    /// <summary>Name of the publishing service, stamped on every envelope.</summary>
    public string Source { get; set; } = "unknown";

    /// <summary>
    /// Subscriber endpoints (<c>POST {url}</c>). Locally this is HTTP push between services; in production
    /// swap <see cref="IIntegrationEventTransport"/> for Azure Service Bus / RabbitMQ (ADR-0007).
    /// </summary>
    public List<string> Subscribers { get; set; } = new();

    public int PollingIntervalSeconds { get; set; } = 2;
    public int BatchSize { get; set; } = 50;
    public int MaxAttempts { get; set; } = 10;
    public bool Enabled { get; set; } = true;
}

/// <summary>Delivery mechanism for integration events. Swappable for a real broker.</summary>
public interface IIntegrationEventTransport
{
    Task SendAsync(IntegrationEventEnvelope envelope, CancellationToken cancellationToken);
}

public sealed class HttpPushTransport : IIntegrationEventTransport
{
    public const string HttpClientName = "outbox";
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IOptions<OutboxOptions> _options;
    private readonly ILogger<HttpPushTransport> _logger;

    public HttpPushTransport(IHttpClientFactory httpClientFactory, IOptions<OutboxOptions> options,
        ILogger<HttpPushTransport> logger)
    {
        _httpClientFactory = httpClientFactory;
        _options = options;
        _logger = logger;
    }

    public async Task SendAsync(IntegrationEventEnvelope envelope, CancellationToken cancellationToken)
    {
        var client = _httpClientFactory.CreateClient(HttpClientName);
        var subscribers = _options.Value.Subscribers;
        for (var index = 0; index < subscribers.Count; index++)
        {
            var stopwatch = Stopwatch.StartNew();
            using var request = new HttpRequestMessage(HttpMethod.Post, subscribers[index])
            {
                Content = JsonContent.Create(envelope, options: OutboxJson.Options),
            };
            request.Headers.TryAddWithoutValidation("X-Correlation-ID", envelope.EventId.ToString("N"));
            if (Activity.Current is { } activity)
                request.Headers.TryAddWithoutValidation("X-Trace-ID", activity.TraceId.ToString());
            _logger.LogInformation("Outbox subscriber delivery started {EventId} {SubscriberIndex}",
                envelope.EventId, index);
            int? statusCode = null;
            try
            {
                using var response = await client.SendAsync(request, cancellationToken);
                statusCode = (int)response.StatusCode;
                response.EnsureSuccessStatusCode();
                _logger.LogInformation(
                    "Outbox subscriber delivery completed {EventId} {SubscriberIndex} with {StatusCode} in {DurationMs}ms",
                    envelope.EventId, index, statusCode, stopwatch.Elapsed.TotalMilliseconds);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                _logger.LogInformation(
                    "Outbox subscriber delivery cancelled {EventId} {SubscriberIndex} after {DurationMs}ms",
                    envelope.EventId, index, stopwatch.Elapsed.TotalMilliseconds);
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Outbox subscriber delivery failed {EventId} {SubscriberIndex} with {StatusCode} after {DurationMs}ms",
                    envelope.EventId, index, statusCode, stopwatch.Elapsed.TotalMilliseconds);
                throw;
            }
        }
    }
}

/// <summary>
/// Polls the outbox and delivers events in order (at-least-once). A failure stops the batch so that
/// later events for the same post office / shipment are never delivered before earlier ones.
/// </summary>
public sealed class OutboxDispatcher<TContext> : BackgroundService
    where TContext : DbContext
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IOptions<OutboxOptions> _options;
    private readonly TimeProvider _clock;
    private readonly ILogger<OutboxDispatcher<TContext>> _logger;

    public OutboxDispatcher(IServiceScopeFactory scopeFactory, IOptions<OutboxOptions> options, TimeProvider clock,
        ILogger<OutboxDispatcher<TContext>> logger)
    {
        _scopeFactory = scopeFactory;
        _options = options;
        _clock = clock;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Value.Enabled)
        {
            _logger.LogInformation("Outbox dispatcher is disabled for {DbContext}", typeof(TContext).Name);
            return;
        }

        var interval = TimeSpan.FromSeconds(Math.Max(1, _options.Value.PollingIntervalSeconds));
        _logger.LogInformation("Outbox dispatcher started for {DbContext} with interval {PollingInterval}",
            typeof(TContext).Name, interval);
        using var timer = new PeriodicTimer(interval);
        try
        {
            do
            {
                try
                {
                    await DispatchBatchAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Outbox dispatch cycle failed for {DbContext}", typeof(TContext).Name);
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            _logger.LogInformation("Outbox dispatcher stopped for {DbContext}", typeof(TContext).Name);
        }
    }

    public async Task<int> DispatchBatchAsync(CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TContext>();
        var transport = scope.ServiceProvider.GetRequiredService<IIntegrationEventTransport>();
        var options = _options.Value;
        var now = _clock.GetUtcNow().UtcDateTime;

        var batch = await db.Set<OutboxMessage>()
            .Where(m => m.ProcessedAtUtc == null && !m.IsDeadLettered)
            .OrderBy(m => m.OccurredAtUtc)
            .Take(options.BatchSize)
            .ToListAsync(cancellationToken);

        if (batch.Count == 0) return 0;
        _logger.LogInformation("Outbox batch started for {DbContext} with {MessageCount} messages",
            typeof(TContext).Name, batch.Count);
        var delivered = 0;
        foreach (var message in batch)
        {
            if (message.NextAttemptAtUtc > now) break; // keep ordering: wait for the head of the queue

            using var activity = OutboxTelemetry.ActivitySource.StartActivity("outbox.publish", ActivityKind.Producer);
            activity?.SetTag("messaging.system", "postal-outbox");
            activity?.SetTag("messaging.message.id", message.Id.ToString("N"));
            activity?.SetTag("messaging.destination", message.EventType);
            var traceId = activity?.TraceId.ToString() ?? ActivityTraceId.CreateRandom().ToString();
            using var logScope = _logger.BeginScope(new Dictionary<string, object>
            {
                ["CorrelationId"] = message.Id.ToString("N"),
                ["TraceId"] = traceId,
                ["OutboxMessageId"] = message.Id,
                ["EventType"] = message.EventType,
            });
            var messageStopwatch = Stopwatch.StartNew();
            _logger.LogInformation("Outbox message dispatch started {MessageId} {EventType}",
                message.Id, message.EventType);
            try
            {
                if (options.Subscribers.Count > 0)
                {
                    using var payload = JsonDocument.Parse(message.Payload);
                    await transport.SendAsync(new IntegrationEventEnvelope(message.Id, message.EventType,
                        message.OccurredAtUtc, options.Source, payload.RootElement.Clone()), cancellationToken);
                }

                message.ProcessedAtUtc = now;
                message.LastError = null;
                delivered++;
                _logger.LogInformation("Outbox message dispatched {MessageId} {EventType} in {DurationMs}ms",
                    message.Id, message.EventType, messageStopwatch.Elapsed.TotalMilliseconds);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                message.Attempts++;
                message.LastError = ex.Message.Length > 1000 ? ex.Message[..1000] : ex.Message;
                if (message.Attempts >= options.MaxAttempts)
                {
                    message.IsDeadLettered = true;
                    _logger.LogError(ex, "Outbox message {MessageId} ({EventType}) dead-lettered after {Attempts} attempts",
                        message.Id, message.EventType, message.Attempts);
                    continue; // poison message: move on so the queue is not blocked forever
                }

                var backoff = TimeSpan.FromSeconds(Math.Min(300, Math.Pow(2, message.Attempts)));
                message.NextAttemptAtUtc = now.Add(backoff);
                _logger.LogWarning("Outbox message {MessageId} ({EventType}) failed, retry in {Backoff}: {Error}",
                    message.Id, message.EventType, backoff, message.LastError);
                break;
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        _logger.LogInformation("Outbox batch completed for {DbContext}; dispatched {DeliveredCount} messages",
            typeof(TContext).Name, delivered);
        return delivered;
    }
}
