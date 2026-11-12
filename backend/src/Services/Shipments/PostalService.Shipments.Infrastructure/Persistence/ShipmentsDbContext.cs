using Microsoft.EntityFrameworkCore;
using PostalService.BuildingBlocks.Application;
using PostalService.BuildingBlocks.Domain;
using PostalService.BuildingBlocks.Infrastructure.Messaging;
using PostalService.BuildingBlocks.Infrastructure.Persistence;
using PostalService.Shipments.Application;
using PostalService.Shipments.Domain;

namespace PostalService.Shipments.Infrastructure.Persistence;

/// <summary>shipments.db — owned exclusively by the Shipments service.</summary>
public sealed class ShipmentsDbContext : DbContext, IUnitOfWork
{
    private readonly IIntegrationEventMapper _eventMapper;
    private readonly TimeProvider _clock;

    public ShipmentsDbContext(DbContextOptions<ShipmentsDbContext> options, IIntegrationEventMapper eventMapper,
        TimeProvider clock) : base(options)
    {
        _eventMapper = eventMapper;
        _clock = clock;
    }

    public DbSet<Shipment> Shipments => Set<Shipment>();
    public DbSet<ShipmentStatusHistory> StatusHistory => Set<ShipmentStatusHistory>();
    public DbSet<PostOfficeReference> PostOfficeReferences => Set<PostOfficeReference>();
    public DbSet<IdempotencyEntry> IdempotencyKeys => Set<IdempotencyEntry>();

    public override async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        OutboxWriter.CaptureDomainEvents(this, _eventMapper, _clock.GetUtcNow().UtcDateTime);
        return await base.SaveChangesAsync(cancellationToken);
    }

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder) =>
        configurationBuilder.UseUtcDateTimes();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var zip = new Microsoft.EntityFrameworkCore.Storage.ValueConversion.ValueConverter<ZipCode, string>(
            v => v.Value, v => ZipCode.Create(v));

        modelBuilder.Entity<Shipment>(b =>
        {
            b.ToTable("Shipments");
            b.HasKey(x => x.Id);
            b.Ignore(x => x.DomainEvents);
            b.Ignore(x => x.IsLocalDelivery);
            b.Ignore(x => x.IsActive);
            b.Ignore(x => x.CanBeEdited);

            b.Property(x => x.Number).HasColumnName("ShipmentNumber").HasMaxLength(30).IsRequired()
                .HasConversion(v => v.Value, v => ShipmentNumber.Create(v));
            b.HasIndex(x => x.Number).IsUnique();

            b.Property(x => x.Type).HasConversion<string>().HasMaxLength(20);
            b.Property(x => x.Status).HasConversion<string>().HasMaxLength(30);
            b.Property(x => x.WeightCategory).HasConversion<string>().HasMaxLength(20);

            // Grams as INTEGER: exact, and sortable (SQLite cannot ORDER BY decimal).
            b.Property(x => x.Weight).HasColumnName("WeightGrams")
                .HasConversion(v => v.Grams, v => Weight.FromGrams(v));

            b.Property(x => x.OriginZip).HasConversion(zip).HasMaxLength(10).IsRequired();
            b.Property(x => x.DestinationZip).HasConversion(zip).HasMaxLength(10).IsRequired();
            b.Property(x => x.CurrentZip).HasConversion(zip).HasMaxLength(10).IsRequired();

            b.Property(x => x.Version).IsConcurrencyToken();

            b.HasMany(x => x.History).WithOne().HasForeignKey(h => h.ShipmentId).OnDelete(DeleteBehavior.Cascade);
            b.Navigation(x => x.History).UsePropertyAccessMode(PropertyAccessMode.Field);

            // Indexes backing the list filters and sorts.
            b.HasIndex(x => x.Status);
            b.HasIndex(x => x.WeightCategory);
            b.HasIndex(x => x.OriginZip);
            b.HasIndex(x => x.DestinationZip);
            b.HasIndex(x => new { x.CurrentZip, x.Status });
            b.HasIndex(x => x.CreatedAtUtc);
        });

        modelBuilder.Entity<ShipmentStatusHistory>(b =>
        {
            b.ToTable("ShipmentStatusHistory");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedOnAdd();
            b.Property(x => x.Status).HasConversion<string>().HasMaxLength(30);
            b.Property(x => x.PreviousStatus).HasConversion<string>().HasMaxLength(30);
            b.Property(x => x.PostOfficeZip).HasConversion(zip).HasMaxLength(10).IsRequired();
            b.Property(x => x.PerformedBy).HasMaxLength(100).IsRequired();
            b.Property(x => x.Remarks).HasMaxLength(500);
            b.HasIndex(x => new { x.ShipmentId, x.OccurredAtUtc });
        });

        modelBuilder.Entity<PostOfficeReference>(b =>
        {
            b.ToTable("PostOfficeReferences");
            b.HasKey(x => x.ZipCode);
            b.Property(x => x.ZipCode).HasMaxLength(10);
            b.Property(x => x.Name).HasMaxLength(150);
            b.Property(x => x.City).HasMaxLength(100);
        });

        modelBuilder.Entity<IdempotencyEntry>(b =>
        {
            b.ToTable("IdempotencyKeys");
            b.HasKey(x => new { x.Key, x.UserId });
            b.Property(x => x.Key).HasMaxLength(100);
            b.Property(x => x.RequestHash).HasMaxLength(64);
        });

        modelBuilder.ApplyOutboxAndInbox();
    }
}

public sealed class IdempotencyEntry
{
    public string Key { get; set; } = string.Empty;
    public Guid UserId { get; set; }
    public string RequestHash { get; set; } = string.Empty;
    public Guid ShipmentId { get; set; }
    public DateTime CreatedAtUtc { get; set; }
}
