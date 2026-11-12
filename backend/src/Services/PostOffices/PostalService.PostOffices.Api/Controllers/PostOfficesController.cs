using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PostalService.BuildingBlocks.Application;
using PostalService.BuildingBlocks.Infrastructure.Web;
using PostalService.PostOffices.Application;

namespace PostalService.PostOffices.Api.Controllers;

[ApiController]
[Route("api/postoffices")]
[Produces("application/json")]
public sealed class PostOfficesController : ControllerBase
{
    private readonly PostOfficeService _service;

    public PostOfficesController(PostOfficeService service) => _service = service;

    [HttpGet]
    [Authorize(Policy = Permissions.PostOfficesRead)]
    [ProducesResponseType<PagedResult<PostOfficeDto>>(StatusCodes.Status200OK)]
    public Task<PagedResult<PostOfficeDto>> Search([FromQuery] PostOfficeSearchCriteria criteria, CancellationToken ct) =>
        _service.SearchAsync(criteria, ct);

    [HttpGet("{id:guid}")]
    [Authorize(Policy = Permissions.PostOfficesRead)]
    [ProducesResponseType<PostOfficeDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public Task<PostOfficeDto> Get(Guid id, CancellationToken ct) => _service.GetAsync(id, ct);

    [HttpGet("by-zip/{zip}")]
    [Authorize(Policy = Permissions.PostOfficesRead)]
    [ProducesResponseType<PostOfficeDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public Task<PostOfficeDto> GetByZip(string zip, CancellationToken ct) => _service.GetByZipAsync(zip, ct);

    [HttpPost]
    [Authorize(Policy = Permissions.PostOfficesWrite)]
    [ProducesResponseType<PostOfficeDto>(StatusCodes.Status201Created)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Create([FromBody] CreatePostOfficeRequest request, CancellationToken ct)
    {
        var office = await _service.CreateAsync(request, ct);
        return CreatedAtAction(nameof(Get), new { id = office.Id }, office);
    }

    [HttpPut("{id:guid}")]
    [Authorize(Policy = Permissions.PostOfficesWrite)]
    [ProducesResponseType<PostOfficeDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public Task<PostOfficeDto> Update(Guid id, [FromBody] UpdatePostOfficeRequest request, CancellationToken ct) =>
        _service.UpdateAsync(id, request, ct);

    [HttpPost("{id:guid}/activate")]
    [Authorize(Policy = Permissions.PostOfficesWrite)]
    public Task<PostOfficeDto> Activate(Guid id, CancellationToken ct) => _service.SetActiveAsync(id, true, ct);

    [HttpPost("{id:guid}/deactivate")]
    [Authorize(Policy = Permissions.PostOfficesWrite)]
    public Task<PostOfficeDto> Deactivate(Guid id, CancellationToken ct) => _service.SetActiveAsync(id, false, ct);

    /// <summary>Deletes only when no active shipment references the post office (409 otherwise, 503 if unverifiable).</summary>
    [HttpDelete("{id:guid}")]
    [Authorize(Policy = Permissions.PostOfficesDelete)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        await _service.DeleteAsync(id, ct);
        return NoContent();
    }
}

/// <summary>Authoritative lookup used synchronously by the Shipments service. Not exposed by the gateway.</summary>
[ApiController]
[Route("internal/postoffices")]
[AllowAnonymous]
[InternalApiKey]
[ApiExplorerSettings(IgnoreApi = true)]
public sealed class InternalPostOfficesController : ControllerBase
{
    private readonly PostOfficeService _service;

    public InternalPostOfficesController(PostOfficeService service) => _service = service;

    [HttpGet("{zip}")]
    public async Task<IActionResult> Get(string zip, CancellationToken ct) =>
        await _service.FindInfoAsync(zip, ct) is { } info ? Ok(info) : NotFound();
}
