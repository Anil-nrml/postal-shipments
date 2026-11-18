using System.Diagnostics;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
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
using PostalService.PostOffices.Application;
using PostalService.PostOffices.Domain;

namespace PostalService.PostOffices.Infrastructure;

/// <summary>postoffices.db — owned exclusively by the PostOffices service.</summary>
public sealed class PostOfficesDbContext : DbContext, IUnitOfWork
{
    private readonly IIntegrationEventMapper _mapper;
    private readonly TimeProvider _clock;

    public PostOfficesDbContext(DbContextOptions<PostOfficesDbContext> options, IIntegrationEventMapper mapper,
        TimeProvider clock) : base(options)
    {
        _mapper = mapper;
        _clock = clock;
    }

    public DbSet<PostOffice> PostOffices => Set<PostOffice>();

    public override async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        OutboxWriter.CaptureDomainEvents(this, _mapper, _clock.GetUtcNow().UtcDateTime);
        return await base.SaveChangesAsync(cancellationToken);
    }

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder) =>
        configurationBuilder.UseUtcDateTimes();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<PostOffice>(b =>
        {
            b.ToTable("PostOffices");
            b.HasKey(x => x.Id);
            b.Ignore(x => x.DomainEvents);
            b.Ignore(x => x.CanAcceptShipments);
            b.Property(x => x.ZipCode).HasMaxLength(10).IsRequired()
                .HasConversion(new ValueConverter<ZipCode, string>(v => v.Value, v => ZipCode.Create(v)));
            b.HasIndex(x => x.ZipCode).IsUnique();
            b.Property(x => x.Name).HasMaxLength(150).IsRequired();
            b.HasIndex(x => x.Name);
            b.Property(x => x.ContactNumber).HasMaxLength(20).IsRequired();
            b.Property(x => x.Version).IsConcurrencyToken();
            b.HasIndex(x => x.IsActive);
            b.OwnsOne(x => x.Address, a =>
            {
                a.Property(p => p.Line).HasColumnName("AddressLine").HasMaxLength(200).IsRequired();
                a.Property(p => p.City).HasColumnName("City").HasMaxLength(100).IsRequired();
                a.Property(p => p.State).HasColumnName("State").HasMaxLength(100).IsRequired();
                a.Property(p => p.Country).HasColumnName("Country").HasMaxLength(100).IsRequired();
            });
            b.Navigation(x => x.Address).IsRequired();
        });

        modelBuilder.ApplyOutboxAndInbox();
    }
}

public sealed class PostOfficeRepository : IPostOfficeRepository
{
    private readonly PostOfficesDbContext _db;

    public PostOfficeRepository(PostOfficesDbContext db) => _db = db;

    public Task<PostOffice?> GetByIdAsync(Guid id, CancellationToken ct) =>
        _db.PostOffices.FirstOrDefaultAsync(p => p.Id == id, ct);

    public Task<PostOffice?> GetByZipAsync(ZipCode zip, CancellationToken ct) =>
        _db.PostOffices.FirstOrDefaultAsync(p => p.ZipCode == zip, ct);

    public Task<bool> ZipExistsAsync(ZipCode zip, CancellationToken ct) =>
        _db.PostOffices.AnyAsync(p => p.ZipCode == zip, ct);

    public async Task<PagedResult<PostOffice>> SearchAsync(PostOfficeSearchCriteria c, CancellationToken ct)
    {
        var (pageNumber, pageSize) = Paging.Normalize(c.PageNumber, c.PageSize);
        IQueryable<PostOffice> query;
        if (!string.IsNullOrWhiteSpace(c.Search))
        {
            // ZipCode is value-converted, so match it in parameterised SQL; LIKE is case-insensitive for ASCII.
            var term = c.Search.Trim();
            var zipPrefix = term.ToUpperInvariant() + "%";
            var nameLike = "%" + term + "%";
            query = _db.PostOffices.FromSqlInterpolated(
                $"SELECT * FROM PostOffices WHERE ZipCode LIKE {zipPrefix} OR Name LIKE {nameLike} OR City LIKE {nameLike}");
        }
        else
        {
            query = _db.PostOffices;
        }

        query = query.AsNoTracking();
        if (c.IsActive.HasValue) query = query.Where(p => p.IsActive == c.IsActive.Value);

        var total = await query.CountAsync(ct);
        var items = await query.OrderBy(p => p.ZipCode).Skip((pageNumber - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        return new PagedResult<PostOffice>(items, total, pageNumber, pageSize);
    }

    public void Add(PostOffice office) => _db.PostOffices.Add(office);

    public void Remove(PostOffice office) => _db.PostOffices.Remove(office);
}

public sealed class HttpShipmentDependencyChecker : IShipmentDependencyChecker
{
    private readonly HttpClient _http;
    private readonly ILogger<HttpShipmentDependencyChecker> _logger;

    public HttpShipmentDependencyChecker(HttpClient http, ILogger<HttpShipmentDependencyChecker> logger)
    {
        _http = http;
        _logger = logger;
    }

    public async Task<ShipmentDependencies> GetAsync(ZipCode zip, CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();
        _logger.LogInformation("Dependency request started {Dependency} {Operation}", "Shipments", "GetPostOfficeDependencies");
        try
        {
            var result = await _http.GetFromJsonAsync<ShipmentDependencies>(
                             $"internal/postoffices/{Uri.EscapeDataString(zip.Value)}/dependencies", ct)
                         ?? throw new ServiceUnavailableException("Empty response from the Shipments service.");
            _logger.LogInformation(
                "Dependency request completed {Dependency} {Operation} in {DurationMs}ms",
                "Shipments", "GetPostOfficeDependencies", stopwatch.Elapsed.TotalMilliseconds);
            return result;
        }
        catch (Exception ex) when (ex is HttpRequestException or BrokenCircuitException or TimeoutRejectedException
                                       or TaskCanceledException && !ct.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Dependency request failed {Dependency} {Operation} in {DurationMs}ms",
                "Shipments", "GetPostOfficeDependencies", stopwatch.Elapsed.TotalMilliseconds);
            throw new ServiceUnavailableException(
                "Shipment dependencies cannot be verified right now, so the post office was not deleted.", ex);
        }
    }
}

public sealed class PostOfficeIntegrationEventMapper : IIntegrationEventMapper
{
    public (string EventType, object Payload, string? AggregateId)? Map(IDomainEvent domainEvent) => domainEvent switch
    {
        PostOfficeChanged e => ("postoffice.changed.v1", e, e.ZipCode),
        _ => null,
    };
}

public static class DependencyInjection
{
    public static IServiceCollection AddPostOfficesInfrastructure(this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddSqliteDbContext<PostOfficesDbContext>(
            configuration.GetConnectionString("PostOffices") ?? "Data Source=App_Data/postoffices.db");
        services.AddScoped<IUnitOfWork>(sp => sp.GetRequiredService<PostOfficesDbContext>());
        services.AddScoped<IPostOfficeRepository, PostOfficeRepository>();
        services.AddSingleton<IIntegrationEventMapper, PostOfficeIntegrationEventMapper>();

        var shipmentsUrl = configuration["Services:Shipments"] ?? "http://localhost:5103/";
        services.AddHttpClient<IShipmentDependencyChecker, HttpShipmentDependencyChecker>(c =>
                c.BaseAddress = new Uri(shipmentsUrl.EndsWith('/') ? shipmentsUrl : shipmentsUrl + "/"))
            .AddHttpMessageHandler<CorrelationIdDelegatingHandler>()
            .AddHttpMessageHandler<InternalApiKeyHandler>()
            .AddStandardResilienceHandler(o =>
            {
                o.AttemptTimeout.Timeout = TimeSpan.FromSeconds(2);
                o.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(6);
                o.Retry.MaxRetryAttempts = 2;
            });

        services.Configure<OutboxOptions>(configuration.GetSection(OutboxOptions.SectionName));
        services.AddHttpClient(HttpPushTransport.HttpClientName, c => c.Timeout = TimeSpan.FromSeconds(5))
            .AddHttpMessageHandler<CorrelationIdDelegatingHandler>()
            .AddHttpMessageHandler<InternalApiKeyHandler>();
        services.AddScoped<IIntegrationEventTransport, HttpPushTransport>();
        services.AddHostedService<OutboxDispatcher<PostOfficesDbContext>>();
        return services;
    }
}
