using FluentValidation;
using Moq;
using PostalService.BuildingBlocks.Application;
using PostalService.BuildingBlocks.Domain;
using PostalService.PostOffices.Application;
using PostalService.PostOffices.Domain;
using PostalService.Tests;

namespace PostalService.PostOffices.UnitTests;

public class PostOfficeTests
{
    private readonly TestClock _clock = new();

    private PostOffice NewOffice() => PostOffice.Create(ZipCode.Create("500001"), "Hyderabad GPO",
        Address.Create("Abids", "Hyderabad", "Telangana", "India"), "+91 40 2346 3000", _clock.UtcNow);

    [Fact]
    public void New_office_is_active_and_raises_created()
    {
        var office = NewOffice();
        office.CanAcceptShipments.Should().BeTrue();
        office.DomainEvents.Should().ContainSingle().Which.Should().BeOfType<PostOfficeChanged>()
            .Which.Kind.Should().Be(PostOfficeChangeKind.Created);
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("12")]
    [InlineData("+91 40 2346 3000 ext 5")]
    public void Rejects_invalid_contact_numbers(string contact) =>
        FluentActions.Invoking(() => PostOffice.Create(ZipCode.Create("500001"), "x",
                Address.Create("a", "b", "c", "d"), contact, _clock.UtcNow))
            .Should().Throw<DomainException>();

    [Fact]
    public void Deactivated_office_cannot_accept_shipments_and_publishes_the_change()
    {
        var office = NewOffice();
        office.ClearDomainEvents();
        office.Deactivate(_clock.UtcNow);

        office.CanAcceptShipments.Should().BeFalse();
        office.DomainEvents.OfType<PostOfficeChanged>().Single().CanAcceptShipments.Should().BeFalse();
    }

    [Fact]
    public void Pending_deletion_blocks_shipments_and_edits()
    {
        var office = NewOffice();
        office.BeginDeletion(_clock.UtcNow);
        office.CanAcceptShipments.Should().BeFalse();
        FluentActions.Invoking(() => office.Update("x", office.Address, "+91 1234567", _clock.UtcNow))
            .Should().Throw<DomainException>().Which.Code.Should().Be("postoffice.pending-deletion");

        office.CancelDeletion(_clock.UtcNow);
        office.CanAcceptShipments.Should().BeTrue();
    }

    [Fact]
    public void Deletion_with_active_shipments_is_refused()
    {
        var office = NewOffice();
        office.BeginDeletion(_clock.UtcNow);
        FluentActions.Invoking(() => office.ConfirmDeletion(3, _clock.UtcNow))
            .Should().Throw<DomainException>().Which.Code.Should().Be("postoffice.has-active-shipments");
    }
}

public class PostOfficeServiceTests
{
    private readonly Mock<IPostOfficeRepository> _repo = new();
    private readonly Mock<IShipmentDependencyChecker> _deps = new();
    private readonly Mock<IUnitOfWork> _uow = new();
    private readonly TestClock _clock = new();

    private PostOfficeService Sut() => new(_repo.Object, _deps.Object, _uow.Object, _clock,
        new CreatePostOfficeRequestValidator(), new UpdatePostOfficeRequestValidator(), new PostOfficeSearchCriteriaValidator());

    private PostOffice Existing()
    {
        var office = PostOffice.Create(ZipCode.Create("500001"), "GPO", Address.Create("a", "b", "c", "d"), "+91 1234567",
            _clock.UtcNow);
        _repo.Setup(r => r.GetByIdAsync(office.Id, It.IsAny<CancellationToken>())).ReturnsAsync(office);
        return office;
    }

    private static CreatePostOfficeRequest Create(string zip = "500081") =>
        new(zip, "Madhapur SO", "Hitech City Road", "Hyderabad", "Telangana", "India", "+91 40 2311 1100");

    [Fact]
    public async Task Create_rejects_duplicate_zip()
    {
        _repo.Setup(r => r.ZipExistsAsync(ZipCode.Create("500081"), It.IsAny<CancellationToken>())).ReturnsAsync(true);
        var act = () => Sut().CreateAsync(Create(), CancellationToken.None);
        (await act.Should().ThrowAsync<ConflictException>()).Which.Code.Should().Be("postoffice.zip-exists");
    }

    [Fact]
    public async Task Create_validates_input()
    {
        var act = () => Sut().CreateAsync(Create(zip: "!!"), CancellationToken.None);
        await act.Should().ThrowAsync<ValidationException>();
    }

    [Fact]
    public async Task Create_normalises_zip_and_saves()
    {
        var dto = await Sut().CreateAsync(Create(zip: "ab123"), CancellationToken.None);
        dto.ZipCode.Should().Be("AB123");
        dto.IsActive.Should().BeTrue();
        _uow.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Delete_with_active_shipments_compensates_and_reports_conflict()
    {
        var office = Existing();
        _deps.Setup(d => d.GetAsync(office.ZipCode, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ShipmentDependencies("500001", ActiveShipments: 2, TotalShipments: 5));

        var act = () => Sut().DeleteAsync(office.Id, CancellationToken.None);

        (await act.Should().ThrowAsync<ConflictException>()).Which.Code.Should().Be("postoffice.has-active-shipments");
        office.IsPendingDeletion.Should().BeFalse("the saga must compensate");
        office.CanAcceptShipments.Should().BeTrue();
        _repo.Verify(r => r.Remove(It.IsAny<PostOffice>()), Times.Never);
    }

    [Fact]
    public async Task Delete_when_shipments_service_is_down_compensates_and_fails_closed()
    {
        var office = Existing();
        _deps.Setup(d => d.GetAsync(office.ZipCode, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ServiceUnavailableException("down"));

        var act = () => Sut().DeleteAsync(office.Id, CancellationToken.None);

        await act.Should().ThrowAsync<ServiceUnavailableException>();
        office.IsPendingDeletion.Should().BeFalse();
        _repo.Verify(r => r.Remove(It.IsAny<PostOffice>()), Times.Never);
    }

    [Fact]
    public async Task Delete_without_active_shipments_removes_the_office()
    {
        var office = Existing();
        _deps.Setup(d => d.GetAsync(office.ZipCode, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ShipmentDependencies("500001", 0, 12));

        await Sut().DeleteAsync(office.Id, CancellationToken.None);

        _repo.Verify(r => r.Remove(office), Times.Once);
        office.DomainEvents.OfType<PostOfficeChanged>().Last().Kind.Should().Be(PostOfficeChangeKind.Deleted);
    }

    [Fact]
    public async Task Update_with_stale_version_is_a_conflict()
    {
        var office = Existing();
        var act = () => Sut().UpdateAsync(office.Id,
            new UpdatePostOfficeRequest("New name", "a", "b", "c", "d", "+91 1234567", Version: 99), CancellationToken.None);
        (await act.Should().ThrowAsync<ConflictException>()).Which.Code.Should().Be("concurrency");
    }
}
