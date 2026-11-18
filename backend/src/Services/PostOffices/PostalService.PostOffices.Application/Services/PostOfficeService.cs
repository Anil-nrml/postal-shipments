using FluentValidation;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using PostalService.BuildingBlocks.Application;
using PostalService.BuildingBlocks.Domain;
using PostalService.PostOffices.Domain;

namespace PostalService.PostOffices.Application;

// ---------- Contracts ----------

public sealed record CreatePostOfficeRequest(string ZipCode, string Name, string Address, string City, string State,
    string Country, string ContactNumber);

public sealed record UpdatePostOfficeRequest(string Name, string Address, string City, string State, string Country,
    string ContactNumber, int Version);

public sealed record PostOfficeSearchCriteria
{
    public int? PageNumber { get; init; }
    public int? PageSize { get; init; }

    /// <summary>ZIP prefix or name fragment.</summary>
    public string? Search { get; init; }

    public bool? IsActive { get; init; }
}

public sealed record PostOfficeDto(Guid Id, string ZipCode, string Name, string Address, string City, string State,
    string Country, string ContactNumber, bool IsActive, bool CanAcceptShipments, DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc, int Version);

/// <summary>Minimal authoritative view served to other services.</summary>
public sealed record PostOfficeInfo(string ZipCode, string Name, string City, bool CanAcceptShipments, int Version);

public sealed record ShipmentDependencies(string ZipCode, int ActiveShipments, int TotalShipments);

// ---------- Ports ----------

public interface IPostOfficeRepository
{
    Task<PostOffice?> GetByIdAsync(Guid id, CancellationToken ct);
    Task<PostOffice?> GetByZipAsync(ZipCode zip, CancellationToken ct);
    Task<bool> ZipExistsAsync(ZipCode zip, CancellationToken ct);
    Task<PagedResult<PostOffice>> SearchAsync(PostOfficeSearchCriteria criteria, CancellationToken ct);
    void Add(PostOffice office);
    void Remove(PostOffice office);
}

/// <summary>Asks the Shipments service (synchronously) which shipments reference a ZIP.</summary>
public interface IShipmentDependencyChecker
{
    /// <exception cref="ServiceUnavailableException">Shipments service unreachable.</exception>
    Task<ShipmentDependencies> GetAsync(ZipCode zip, CancellationToken ct);
}

// ---------- Validators ----------

public sealed class CreatePostOfficeRequestValidator : AbstractValidator<CreatePostOfficeRequest>
{
    public CreatePostOfficeRequestValidator()
    {
        RuleFor(x => x.ZipCode).NotEmpty().Matches("^[A-Za-z0-9]{3,10}$")
            .WithMessage("ZIP code must be 3-10 letters or digits.");
        RuleFor(x => x.Name).NotEmpty().MaximumLength(150);
        RuleFor(x => x.Address).NotEmpty().MaximumLength(200);
        RuleFor(x => x.City).NotEmpty().MaximumLength(100);
        RuleFor(x => x.State).NotEmpty().MaximumLength(100);
        RuleFor(x => x.Country).NotEmpty().MaximumLength(100);
        RuleFor(x => x.ContactNumber).NotEmpty().Matches(@"^\+?[0-9 ()\-]{6,20}$")
            .WithMessage("Contact number may contain digits, spaces, '+', '-' and parentheses (6-20 characters).");
    }
}

public sealed class UpdatePostOfficeRequestValidator : AbstractValidator<UpdatePostOfficeRequest>
{
    public UpdatePostOfficeRequestValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(150);
        RuleFor(x => x.Address).NotEmpty().MaximumLength(200);
        RuleFor(x => x.City).NotEmpty().MaximumLength(100);
        RuleFor(x => x.State).NotEmpty().MaximumLength(100);
        RuleFor(x => x.Country).NotEmpty().MaximumLength(100);
        RuleFor(x => x.ContactNumber).NotEmpty().Matches(@"^\+?[0-9 ()\-]{6,20}$");
        RuleFor(x => x.Version).GreaterThan(0);
    }
}

public sealed class PostOfficeSearchCriteriaValidator : AbstractValidator<PostOfficeSearchCriteria>
{
    public PostOfficeSearchCriteriaValidator()
    {
        RuleFor(x => x.PageNumber).GreaterThan(0).When(x => x.PageNumber.HasValue);
        RuleFor(x => x.PageSize).InclusiveBetween(1, 100).When(x => x.PageSize.HasValue);
        RuleFor(x => x.Search).MaximumLength(100).Matches(@"^[\p{L}\p{N} .'\-]+$")
            .When(x => !string.IsNullOrEmpty(x.Search));
    }
}

// ---------- Use cases ----------

public sealed class PostOfficeService
{
    private readonly IPostOfficeRepository _offices;
    private readonly IShipmentDependencyChecker _dependencies;
    private readonly IUnitOfWork _unitOfWork;
    private readonly TimeProvider _clock;
    private readonly IValidator<CreatePostOfficeRequest> _createValidator;
    private readonly IValidator<UpdatePostOfficeRequest> _updateValidator;
    private readonly IValidator<PostOfficeSearchCriteria> _searchValidator;
    private readonly ILogger<PostOfficeService> _logger;

    public PostOfficeService(IPostOfficeRepository offices, IShipmentDependencyChecker dependencies,
        IUnitOfWork unitOfWork, TimeProvider clock, IValidator<CreatePostOfficeRequest> createValidator,
        IValidator<UpdatePostOfficeRequest> updateValidator, IValidator<PostOfficeSearchCriteria> searchValidator,
        ILogger<PostOfficeService> logger)
    {
        _offices = offices;
        _dependencies = dependencies;
        _unitOfWork = unitOfWork;
        _clock = clock;
        _createValidator = createValidator;
        _updateValidator = updateValidator;
        _searchValidator = searchValidator;
        _logger = logger;
    }

    private DateTime UtcNow => _clock.GetUtcNow().UtcDateTime;

    public async Task<PostOfficeDto> CreateAsync(CreatePostOfficeRequest request, CancellationToken ct)
    {
        await _createValidator.ValidateAndThrowAsync(request, ct);
        var zip = ZipCode.Create(request.ZipCode);

        // Fast, friendly check; the unique index is the real guarantee under concurrency.
        if (await _offices.ZipExistsAsync(zip, ct))
            throw new ConflictException("postoffice.zip-exists", $"A post office with ZIP code {zip} already exists.");

        var office = PostOffice.Create(zip, request.Name,
            Address.Create(request.Address, request.City, request.State, request.Country), request.ContactNumber, UtcNow);
        _offices.Add(office);
        await _unitOfWork.SaveChangesAsync(ct);
        _logger.LogInformation("Post office created {PostOfficeId}", office.Id);
        return ToDto(office);
    }

    public async Task<PostOfficeDto> GetAsync(Guid id, CancellationToken ct) => ToDto(await LoadAsync(id, ct));

    public async Task<PostOfficeDto> GetByZipAsync(string zip, CancellationToken ct)
    {
        var code = ZipCode.Create(zip);
        return ToDto(await _offices.GetByZipAsync(code, ct) ?? throw new NotFoundException("Post office", code.Value));
    }

    public async Task<PostOfficeInfo?> FindInfoAsync(string zip, CancellationToken ct)
    {
        if (!ZipCode.TryCreate(zip, out var code)) return null;
        var office = await _offices.GetByZipAsync(code!, ct);
        return office is null
            ? null
            : new PostOfficeInfo(office.ZipCode.Value, office.Name, office.Address.City, office.CanAcceptShipments,
                office.Version);
    }

    public async Task<PagedResult<PostOfficeDto>> SearchAsync(PostOfficeSearchCriteria criteria, CancellationToken ct)
    {
        await _searchValidator.ValidateAndThrowAsync(criteria, ct);
        var page = await _offices.SearchAsync(criteria, ct);
        return new PagedResult<PostOfficeDto>(page.Items.Select(ToDto).ToList(), page.TotalRecords, page.PageNumber,
            page.PageSize);
    }

    public async Task<PostOfficeDto> UpdateAsync(Guid id, UpdatePostOfficeRequest request, CancellationToken ct)
    {
        await _updateValidator.ValidateAndThrowAsync(request, ct);
        var office = await LoadAsync(id, ct);
        EnsureVersion(office, request.Version);
        office.Update(request.Name, Address.Create(request.Address, request.City, request.State, request.Country),
            request.ContactNumber, UtcNow);
        await _unitOfWork.SaveChangesAsync(ct);
        _logger.LogInformation("Post office updated {PostOfficeId}", office.Id);
        return ToDto(office);
    }

    public async Task<PostOfficeDto> SetActiveAsync(Guid id, bool active, CancellationToken ct)
    {
        var office = await LoadAsync(id, ct);
        if (active) office.Activate(UtcNow); else office.Deactivate(UtcNow);
        await _unitOfWork.SaveChangesAsync(ct);
        _logger.LogInformation("Post office active state changed {PostOfficeId} to {IsActive}", office.Id, active);
        return ToDto(office);
    }

    /// <summary>
    /// Delete saga spanning two services:
    /// 1. mark pending deletion and commit — from now on Shipments' synchronous check rejects new shipments;
    /// 2. ask Shipments for active dependencies;
    /// 3a. none → delete;  3b. some, or Shipments unreachable → compensate (cancel) and report.
    /// </summary>
    public async Task DeleteAsync(Guid id, CancellationToken ct)
    {
        var office = await LoadAsync(id, ct);
        office.BeginDeletion(UtcNow);
        await _unitOfWork.SaveChangesAsync(ct);

        ShipmentDependencies dependencies;
        try
        {
            dependencies = await _dependencies.GetAsync(office.ZipCode, ct);
        }
        catch (Exception)
        {
            await CompensateAsync(office);
            throw;
        }

        if (dependencies.ActiveShipments > 0)
        {
            await CompensateAsync(office);
            throw new ConflictException("postoffice.has-active-shipments",
                $"Post office {office.ZipCode} has {dependencies.ActiveShipments} active shipment(s). " +
                "Deactivate it instead, or wait until they are delivered.");
        }

        office.ConfirmDeletion(dependencies.ActiveShipments, UtcNow);
        _offices.Remove(office);
        await _unitOfWork.SaveChangesAsync(ct);
        _logger.LogInformation("Post office deleted {PostOfficeId}", office.Id);
    }

    private async Task CompensateAsync(PostOffice office)
    {
        office.CancelDeletion(UtcNow);
        // Compensation must run even if the caller's request was cancelled.
        await _unitOfWork.SaveChangesAsync(CancellationToken.None);
    }

    private async Task<PostOffice> LoadAsync(Guid id, CancellationToken ct) =>
        await _offices.GetByIdAsync(id, ct) ?? throw new NotFoundException("Post office", id);

    private static void EnsureVersion(PostOffice office, int expected)
    {
        if (office.Version != expected)
            throw new ConflictException("concurrency",
                $"Post office {office.ZipCode} was changed by someone else. Reload and retry.");
    }

    public static PostOfficeDto ToDto(PostOffice o) => new(o.Id, o.ZipCode.Value, o.Name, o.Address.Line, o.Address.City,
        o.Address.State, o.Address.Country, o.ContactNumber, o.IsActive, o.CanAcceptShipments, o.CreatedAtUtc,
        o.UpdatedAtUtc, o.Version);
}

public static class DependencyInjection
{
    public static IServiceCollection AddPostOfficesApplication(this IServiceCollection services)
    {
        services.AddValidatorsFromAssemblyContaining<CreatePostOfficeRequestValidator>();
        services.AddScoped<PostOfficeService>();
        return services;
    }
}
