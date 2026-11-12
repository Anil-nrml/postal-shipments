using FluentValidation;
using Moq;
using PostalService.BuildingBlocks.Application;
using PostalService.BuildingBlocks.Domain;
using PostalService.Shipments.Application;
using PostalService.Shipments.Domain;
using PostalService.Tests;

namespace PostalService.Shipments.UnitTests.Application;

public class ShipmentServiceTests
{
    private readonly Mock<IShipmentRepository> _repo = new();
    private readonly Mock<IShipmentQueries> _queries = new();
    private readonly Mock<IPostOfficeDirectory> _directory = new();
    private readonly Mock<IIdempotencyStore> _idempotency = new();
    private readonly Mock<IUnitOfWork> _uow = new();
    private readonly Mock<ICurrentUser> _user = new();
    private readonly TestClock _clock = new();
    private readonly List<Shipment> _added = new();

    public ShipmentServiceTests()
    {
        _user.SetupGet(u => u.UserId).Returns(Guid.Parse("11111111-1111-1111-1111-111111111111"));
        _user.SetupGet(u => u.UserName).Returns("clerk");
        _user.Setup(u => u.HasPermission(It.IsAny<string>())).Returns(false);
        _repo.Setup(r => r.Add(It.IsAny<Shipment>())).Callback<Shipment>(_added.Add);
        _repo.Setup(r => r.NumberExistsAsync(It.IsAny<ShipmentNumber>(), It.IsAny<CancellationToken>())).ReturnsAsync(false);
        _queries.Setup(q => q.GetPostOfficeSummariesAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<string, PostOfficeSummary>());
        GivenOffice("500001", active: true);
        GivenOffice("560001", active: true);
    }

    private ShipmentService Sut() => new(_repo.Object, _queries.Object, _directory.Object, _idempotency.Object, _uow.Object,
        _user.Object, _clock, new CreateShipmentRequestValidator(), new UpdateShipmentRequestValidator(),
        new ChangeStatusRequestValidator(), new CorrectStatusRequestValidator(), new ShipmentSearchCriteriaValidator());

    private void GivenOffice(string zip, bool active) =>
        _directory.Setup(d => d.FindAsync(ZipCode.Create(zip), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PostOfficeInfo(zip, $"Office {zip}", "City", active, 1));

    private static CreateShipmentRequest Request(string origin = "500001", string destination = "560001", decimal kg = 1.5m) =>
        new(ShipmentType.Package, kg, origin, destination);

    [Fact]
    public async Task Create_persists_a_new_shipment_once()
    {
        var (dto, created) = await Sut().CreateAsync(Request(), idempotencyKey: null, CancellationToken.None);

        created.Should().BeTrue();
        dto.Status.Should().Be(ShipmentStatus.ReceivedAtOrigin);
        dto.WeightCategory.Should().Be(WeightCategory.Between1And5Kg);
        dto.AllowedNextStatuses.Should().BeEquivalentTo(new[] { ShipmentStatus.ProcessedAtOrigin, ShipmentStatus.Lost });
        _added.Should().ContainSingle();
        _uow.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Create_supports_local_delivery_and_checks_the_office_once()
    {
        var (dto, _) = await Sut().CreateAsync(Request(destination: "500001"), null, CancellationToken.None);
        dto.IsLocalDelivery.Should().BeTrue();
        _directory.Verify(d => d.FindAsync(It.IsAny<ZipCode>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Create_rejects_an_inactive_post_office()
    {
        GivenOffice("560001", active: false);
        var act = () => Sut().CreateAsync(Request(), null, CancellationToken.None);
        (await act.Should().ThrowAsync<DomainException>()).Which.Code.Should().Be("postoffice.inactive");
        _uow.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Create_rejects_an_unknown_zip()
    {
        var act = () => Sut().CreateAsync(Request(destination: "999999"), null, CancellationToken.None);
        (await act.Should().ThrowAsync<DomainException>()).Which.Code.Should().Be("postoffice.unknown");
    }

    [Fact]
    public async Task Create_fails_closed_when_post_offices_service_is_down()
    {
        _directory.Setup(d => d.FindAsync(It.IsAny<ZipCode>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ServiceUnavailableException("down"));
        var act = () => Sut().CreateAsync(Request(), null, CancellationToken.None);
        await act.Should().ThrowAsync<ServiceUnavailableException>();
        _added.Should().BeEmpty();
    }

    [Fact]
    public async Task Create_validates_input()
    {
        var act = () => Sut().CreateAsync(Request(kg: 0, origin: "!"), null, CancellationToken.None);
        var ex = (await act.Should().ThrowAsync<ValidationException>()).Which;
        ex.Errors.Select(e => e.PropertyName).Should().Contain(new[] { "WeightKg", "OriginZip" });
    }

    [Fact]
    public async Task Create_with_a_repeated_idempotency_key_returns_the_original_shipment()
    {
        var sut = Sut();
        string? hash = null;
        _idempotency.Setup(i => i.Add(It.IsAny<IdempotencyRecord>())).Callback<IdempotencyRecord>(r => hash = r.RequestHash);
        var (first, _) = await sut.CreateAsync(Request(), "key-1", CancellationToken.None);

        _idempotency.Setup(i => i.FindAsync("key-1", It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new IdempotencyRecord("key-1", Guid.Empty, hash!, first.Id, _clock.UtcNow));
        _repo.Setup(r => r.GetByIdAsync(first.Id, false, It.IsAny<CancellationToken>())).ReturnsAsync(_added[0]);

        var (second, created) = await sut.CreateAsync(Request(), "key-1", CancellationToken.None);

        created.Should().BeFalse();
        second.Id.Should().Be(first.Id);
        _added.Should().HaveCount(1, "a retried request must never create a duplicate shipment");
    }

    [Fact]
    public async Task Reusing_an_idempotency_key_with_a_different_body_is_a_conflict()
    {
        _idempotency.Setup(i => i.FindAsync("key-1", It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new IdempotencyRecord("key-1", Guid.Empty, "other-hash", Guid.NewGuid(), _clock.UtcNow));
        var act = () => Sut().CreateAsync(Request(), "key-1", CancellationToken.None);
        (await act.Should().ThrowAsync<ConflictException>()).Which.Code.Should().Be("idempotency.key-reused");
    }

    [Fact]
    public async Task Status_change_with_a_stale_version_is_a_conflict()
    {
        var shipment = Shipment.Create(ShipmentNumber.Generate(_clock.UtcNow), ShipmentType.Letter, Weight.FromKilograms(0.1m),
            ZipCode.Create("500001"), ZipCode.Create("560001"), Guid.NewGuid(), "x", _clock.UtcNow);
        _repo.Setup(r => r.GetByIdAsync(shipment.Id, false, It.IsAny<CancellationToken>())).ReturnsAsync(shipment);

        var act = () => Sut().ChangeStatusAsync(shipment.Id,
            new ChangeStatusRequest(ShipmentStatus.ProcessedAtOrigin, "500001", null, Version: shipment.Version + 5),
            CancellationToken.None);

        (await act.Should().ThrowAsync<ConflictException>()).Which.Code.Should().Be("concurrency");
        shipment.Status.Should().Be(ShipmentStatus.ReceivedAtOrigin);
    }

    [Fact]
    public async Task Status_change_records_the_acting_user()
    {
        var shipment = Shipment.Create(ShipmentNumber.Generate(_clock.UtcNow), ShipmentType.Letter, Weight.FromKilograms(0.1m),
            ZipCode.Create("500001"), ZipCode.Create("560001"), Guid.NewGuid(), "x", _clock.UtcNow);
        _repo.Setup(r => r.GetByIdAsync(shipment.Id, false, It.IsAny<CancellationToken>())).ReturnsAsync(shipment);

        var dto = await Sut().ChangeStatusAsync(shipment.Id,
            new ChangeStatusRequest(ShipmentStatus.ProcessedAtOrigin, "500001", "sorted", shipment.Version), CancellationToken.None);

        dto.Status.Should().Be(ShipmentStatus.ProcessedAtOrigin);
        shipment.History.Last().PerformedBy.Should().Be("clerk");
        shipment.History.Last().Remarks.Should().Be("sorted");
        shipment.History.Last().IsCorrection.Should().BeFalse();
    }

    [Fact]
    public async Task Correction_requires_the_correct_permission()
    {
        var act = () => Sut().CorrectStatusAsync(Guid.NewGuid(),
            new CorrectStatusRequest(ShipmentStatus.OutForDelivery, "560001", "wrongly scanned as delivered", 3),
            CancellationToken.None);
        await act.Should().ThrowAsync<UnauthorizedAccessException>();
    }

    [Fact]
    public async Task Missing_shipment_is_not_found()
    {
        var act = () => Sut().GetAsync(Guid.NewGuid(), CancellationToken.None);
        await act.Should().ThrowAsync<NotFoundException>();
    }

    [Theory]
    [InlineData("weight", "desc", true)]
    [InlineData("bogus", null, false)]
    [InlineData(null, "sideways", false)]
    public async Task Search_validates_sort_parameters(string? sortBy, string? direction, bool valid)
    {
        _queries.Setup(q => q.SearchAsync(It.IsAny<ShipmentSearchCriteria>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PagedResult<ShipmentDto>(Array.Empty<ShipmentDto>(), 0, 1, 20));
        var act = () => Sut().SearchAsync(new ShipmentSearchCriteria { SortBy = sortBy, SortDirection = direction },
            CancellationToken.None);
        if (valid) await act.Should().NotThrowAsync();
        else await act.Should().ThrowAsync<ValidationException>();
    }
}

public class PagingTests
{
    [Theory]
    [InlineData(null, null, 1, 20)]
    [InlineData(0, 0, 1, 20)]
    [InlineData(3, 500, 3, 100)]
    public void Normalize(int? number, int? size, int expectedNumber, int expectedSize) =>
        Paging.Normalize(number, size).Should().Be((expectedNumber, expectedSize));

    [Fact]
    public void Metadata_is_computed()
    {
        var page = new PagedResult<int>(new[] { 1, 2 }, TotalRecords: 45, PageNumber: 2, PageSize: 20);
        page.TotalPages.Should().Be(3);
        page.HasNextPage.Should().BeTrue();
        page.HasPreviousPage.Should().BeTrue();
    }
}
