using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PostalService.BuildingBlocks.Application;
using PostalService.Shipments.Application;

namespace PostalService.Shipments.Api.Controllers;

[ApiController]
[Route("api/shipments")]
[Produces("application/json")]
public sealed class ShipmentsController : ControllerBase
{
    private readonly ShipmentService _service;

    public ShipmentsController(ShipmentService service) => _service = service;

    /// <summary>List shipments with paging, sorting and combinable filters.</summary>
    [HttpGet]
    [Authorize(Policy = Permissions.ShipmentsRead)]
    [ProducesResponseType<PagedResult<ShipmentDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    public Task<PagedResult<ShipmentDto>> Search([FromQuery] ShipmentSearchCriteria criteria, CancellationToken ct) =>
        _service.SearchAsync(criteria, ct);

    [HttpGet("statistics")]
    [Authorize(Policy = Permissions.ShipmentsRead)]
    public Task<ShipmentStatistics> Statistics(CancellationToken ct) => _service.GetStatisticsAsync(ct);

    [HttpGet("{id:guid}")]
    [Authorize(Policy = Permissions.ShipmentsRead)]
    [ProducesResponseType<ShipmentDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public Task<ShipmentDto> Get(Guid id, CancellationToken ct) => _service.GetAsync(id, ct);

    [HttpGet("{id:guid}/history")]
    [Authorize(Policy = Permissions.ShipmentsRead)]
    public Task<IReadOnlyList<ShipmentHistoryDto>> History(Guid id, CancellationToken ct) => _service.GetHistoryAsync(id, ct);

    /// <summary>
    /// Register a shipment. Send an Idempotency-Key header (any unique string per submission) so that a
    /// retried request never creates a duplicate.
    /// </summary>
    [HttpPost]
    [Authorize(Policy = Permissions.ShipmentsWrite)]
    [ProducesResponseType<ShipmentDto>(StatusCodes.Status201Created)]
    [ProducesResponseType<ShipmentDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> Create([FromBody] CreateShipmentRequest request,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey, CancellationToken ct)
    {
        if (idempotencyKey is { Length: > 100 })
            return Problem(statusCode: 400, title: "Idempotency-Key must be at most 100 characters.");

        var (shipment, created) = await _service.CreateAsync(request, idempotencyKey, ct);
        return created
            ? CreatedAtAction(nameof(Get), new { id = shipment.Id }, shipment)
            : Ok(shipment);
    }

    [HttpPut("{id:guid}")]
    [Authorize(Policy = Permissions.ShipmentsWrite)]
    [ProducesResponseType<ShipmentDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity)]
    public Task<ShipmentDto> Update(Guid id, [FromBody] UpdateShipmentRequest request, CancellationToken ct) =>
        _service.UpdateAsync(id, request, ct);

    [HttpDelete("{id:guid}")]
    [Authorize(Policy = Permissions.ShipmentsDelete)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> Delete(Guid id, [FromQuery] int? version, CancellationToken ct)
    {
        await _service.DeleteAsync(id, version, ct);
        return NoContent();
    }

    /// <summary>Record the next lifecycle status (scan). Rejected with 409 if <c>version</c> is stale.</summary>
    [HttpPatch("{id:guid}/status")]
    [Authorize(Policy = Permissions.ShipmentsUpdateStatus)]
    [ProducesResponseType<ShipmentDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity)]
    public Task<ShipmentDto> ChangeStatus(Guid id, [FromBody] ChangeStatusRequest request, CancellationToken ct) =>
        _service.ChangeStatusAsync(id, request, ct);

    /// <summary>Authorised correction of a status, e.g. out of Delivered. Administrators only.</summary>
    [HttpPost("{id:guid}/status/corrections")]
    [Authorize(Policy = Permissions.ShipmentsCorrectStatus)]
    [ProducesResponseType<ShipmentDto>(StatusCodes.Status200OK)]
    public Task<ShipmentDto> CorrectStatus(Guid id, [FromBody] CorrectStatusRequest request, CancellationToken ct) =>
        _service.CorrectStatusAsync(id, request, ct);
}

[ApiController]
[Route("api/tracking")]
[Produces("application/json")]
public sealed class TrackingController : ControllerBase
{
    private readonly ShipmentService _service;

    public TrackingController(ShipmentService service) => _service = service;

    [HttpGet("{shipmentNumber}")]
    [Authorize(Policy = Permissions.ShipmentsRead)]
    [ProducesResponseType<TrackingDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public Task<TrackingDto> Track(string shipmentNumber, CancellationToken ct) => _service.TrackAsync(shipmentNumber, ct);

    [HttpGet("{shipmentNumber}/history")]
    [Authorize(Policy = Permissions.ShipmentsRead)]
    public async Task<IReadOnlyList<ShipmentHistoryDto>> History(string shipmentNumber, CancellationToken ct) =>
        (await _service.TrackAsync(shipmentNumber, ct)).History;
}
