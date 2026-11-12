using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PostalService.BuildingBlocks.Infrastructure.Messaging;
using PostalService.BuildingBlocks.Infrastructure.Web;
using PostalService.Shipments.Application;

namespace PostalService.Shipments.Api.Controllers;

/// <summary>
/// Service-to-service endpoints. Not routed by the API gateway; protected by the internal API key.
/// </summary>
[ApiController]
[Route("internal")]
[AllowAnonymous]
[InternalApiKey]
[ApiExplorerSettings(IgnoreApi = true)]
public sealed class InternalController : ControllerBase
{
    private readonly ShipmentService _shipments;
    private readonly PostOfficeEventHandler _postOfficeEvents;
    private readonly ILogger<InternalController> _logger;

    public InternalController(ShipmentService shipments, PostOfficeEventHandler postOfficeEvents,
        ILogger<InternalController> logger)
    {
        _shipments = shipments;
        _postOfficeEvents = postOfficeEvents;
        _logger = logger;
    }

    /// <summary>Used by the PostOffices delete saga.</summary>
    [HttpGet("postoffices/{zip}/dependencies")]
    public Task<PostOfficeDependencies> Dependencies(string zip, CancellationToken ct) =>
        _shipments.GetDependenciesAsync(zip, ct);

    /// <summary>Integration event inbox (HTTP push transport). Always idempotent.</summary>
    [HttpPost("events")]
    public async Task<IActionResult> Receive([FromBody] IntegrationEventEnvelope envelope, CancellationToken ct)
    {
        switch (envelope.EventType)
        {
            case PostOfficeChangedV1.EventType:
                var payload = envelope.Payload.Deserialize<PostOfficeChangedV1>(OutboxJson.Options)
                              ?? throw new BadHttpRequestException("Empty payload.");
                var applied = await _postOfficeEvents.HandleAsync(envelope.EventId, payload, ct);
                _logger.LogInformation("Event {EventId} {EventType} for {Zip} {Result}", envelope.EventId,
                    envelope.EventType, payload.ZipCode, applied ? "applied" : "ignored (duplicate)");
                return Accepted();
            default:
                // Unknown events are acknowledged so a newer publisher never blocks an older consumer.
                _logger.LogDebug("Ignoring unknown event type {EventType}", envelope.EventType);
                return Accepted();
        }
    }
}
