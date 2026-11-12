using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace PostalService.BuildingBlocks.Infrastructure.Messaging;

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

    public HttpPushTransport(IHttpClientFactory httpClientFactory, IOptions<OutboxOptions> options)
    {
        _httpClientFactory = httpClientFactory;
        _options = options;
    }

    public async Task SendAsync(IntegrationEventEnvelope envelope, CancellationToken cancellationToken)
    {
        var client = _httpClientFactory.CreateClient(HttpClientName);
        foreach (var subscriber in _options.Value.Subscribers)
        {
            using var response = await client.PostAsJsonAsync(subscriber, envelope, OutboxJson.Options, cancellationToken);
            response.EnsureSuccessStatusCode();
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
        if (!_options.Value.Enabled) return;

        var interval = TimeSpan.FromSeconds(Math.Max(1, _options.Value.PollingIntervalSeconds));
        using var timer = new PeriodicTimer(interval);
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
                _logger.LogError(ex, "Outbox dispatch cycle failed");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
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

        var delivered = 0;
        foreach (var message in batch)
        {
            if (message.NextAttemptAtUtc > now) break; // keep ordering: wait for the head of the queue

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
        return delivered;
    }
}
