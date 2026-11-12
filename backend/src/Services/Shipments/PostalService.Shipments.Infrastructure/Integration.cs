using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Logging;
using Polly.CircuitBreaker;
using Polly.Timeout;
using PostalService.BuildingBlocks.Application;
using PostalService.BuildingBlocks.Domain;
using PostalService.BuildingBlocks.Infrastructure.Messaging;
using PostalService.BuildingBlocks.Infrastructure.Persistence;
using PostalService.BuildingBlocks.Infrastructure.Web;
using PostalService.Shipments.Application;
using PostalService.Shipments.Domain;
using PostalService.Shipments.Infrastructure.Persistence;

namespace PostalService.Shipments.Infrastructure;

/// <summary>
/// Synchronous call to the PostOffices service (the owner of post office data) behind the standard
/// resilience pipeline: timeout, retry with jitter, circuit breaker. Every failure surfaces as
/// <see cref="ServiceUnavailableException"/> so the API answers 503 and the client may retry.
/// </summary>
public sealed class HttpPostOfficeDirectory : IPostOfficeDirectory
{
    private readonly HttpClient _http;
    private readonly ILogger<HttpPostOfficeDirectory> _logger;

    public HttpPostOfficeDirectory(HttpClient http, ILogger<HttpPostOfficeDirectory> logger)
    {
        _http = http;
        _logger = logger;
    }

    public async Task<PostOfficeInfo?> FindAsync(ZipCode zip, CancellationToken ct)
    {
        try
        {
            using var response = await _http.GetAsync($"internal/postoffices/{Uri.EscapeDataString(zip.Value)}", ct);
            if (response.StatusCode == HttpStatusCode.NotFound) return null;
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadFromJsonAsync<PostOfficeInfo>(cancellationToken: ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or BrokenCircuitException or TimeoutRejectedException
                                       or TaskCanceledException && !ct.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "PostOffices service unavailable while validating {Zip}", zip.Value);
            throw new ServiceUnavailableException(
                "Post office data cannot be verified right now, so the shipment was not saved. Please retry shortly.", ex);
        }
    }
}

public sealed class ShipmentIntegrationEventMapper : IIntegrationEventMapper
{
    public (string EventType, object Payload, string? AggregateId)? Map(IDomainEvent domainEvent) => domainEvent switch
    {
        ShipmentCreated e => ("shipment.created.v1", e, e.ShipmentId.ToString()),
        ShipmentUpdated e => ("shipment.updated.v1", e, e.ShipmentId.ToString()),
        ShipmentDeleted e => ("shipment.deleted.v1", e, e.ShipmentId.ToString()),
        ShipmentStatusChanged e => ("shipment.status-changed.v1", e, e.ShipmentId.ToString()),
        _ => null,
    };
}

public static class DependencyInjection
{
    public static IServiceCollection AddShipmentsInfrastructure(this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddSqliteDbContext<ShipmentsDbContext>(
            configuration.GetConnectionString("Shipments") ?? "Data Source=App_Data/shipments.db");
        services.AddScoped<IUnitOfWork>(sp => sp.GetRequiredService<ShipmentsDbContext>());
        services.AddScoped<IShipmentRepository, ShipmentRepository>();
        services.AddScoped<IShipmentQueries, ShipmentQueries>();
        services.AddScoped<IPostOfficeReferenceRepository, PostOfficeReferenceRepository>();
        services.AddScoped<IInbox, EfInbox>();
        services.AddScoped<IIdempotencyStore, EfIdempotencyStore>();
        services.AddSingleton<IIntegrationEventMapper, ShipmentIntegrationEventMapper>();

        var postOfficesUrl = configuration["Services:PostOffices"] ?? "http://localhost:5102/";
        services.AddHttpClient<IPostOfficeDirectory, HttpPostOfficeDirectory>(c =>
                c.BaseAddress = new Uri(postOfficesUrl.EndsWith('/') ? postOfficesUrl : postOfficesUrl + "/"))
            .AddHttpMessageHandler<CorrelationIdDelegatingHandler>()
            .AddHttpMessageHandler<InternalApiKeyHandler>()
            .AddStandardResilienceHandler(o =>
            {
                o.AttemptTimeout.Timeout = TimeSpan.FromMilliseconds(800);
                o.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(3);
                o.Retry.MaxRetryAttempts = 2;
                o.CircuitBreaker.SamplingDuration = TimeSpan.FromSeconds(10);
                o.CircuitBreaker.BreakDuration = TimeSpan.FromSeconds(15);
            });

        // Outbox: domain events -> integration events (no subscribers in this repo yet; see ADR-0007).
        services.Configure<OutboxOptions>(configuration.GetSection(OutboxOptions.SectionName));
        services.AddHttpClient(HttpPushTransport.HttpClientName, c => c.Timeout = TimeSpan.FromSeconds(5))
            .AddHttpMessageHandler<InternalApiKeyHandler>();
        services.AddScoped<IIntegrationEventTransport, HttpPushTransport>();
        services.AddHostedService<OutboxDispatcher<ShipmentsDbContext>>();
        return services;
    }
}
