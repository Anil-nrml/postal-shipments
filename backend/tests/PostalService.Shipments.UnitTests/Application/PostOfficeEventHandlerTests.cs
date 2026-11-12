using Moq;
using PostalService.BuildingBlocks.Application;
using PostalService.Shipments.Application;
using PostalService.Shipments.Domain;
using PostalService.Tests;

namespace PostalService.Shipments.UnitTests.Application;

public class PostOfficeEventHandlerTests
{
    private readonly Dictionary<string, PostOfficeReference> _store = new();
    private readonly HashSet<Guid> _inbox = new();
    private readonly Mock<IUnitOfWork> _uow = new();
    private readonly PostOfficeEventHandler _sut;

    public PostOfficeEventHandlerTests()
    {
        var refs = new Mock<IPostOfficeReferenceRepository>();
        refs.Setup(r => r.FindAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string zip, CancellationToken _) => _store.GetValueOrDefault(zip));
        refs.Setup(r => r.Add(It.IsAny<PostOfficeReference>())).Callback<PostOfficeReference>(r => _store[r.ZipCode] = r);

        var inbox = new Mock<IInbox>();
        inbox.Setup(i => i.HasProcessedAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid id, CancellationToken _) => _inbox.Contains(id));
        inbox.Setup(i => i.MarkProcessed(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<DateTime>()))
            .Callback<Guid, string, DateTime>((id, _, _) => _inbox.Add(id));

        _sut = new PostOfficeEventHandler(refs.Object, inbox.Object, _uow.Object, new TestClock());
    }

    private static PostOfficeChangedV1 Evt(string kind, string name, bool active, int version) =>
        new(Guid.NewGuid(), kind, "500001", name, "Hyderabad", active, version);

    [Fact]
    public async Task Duplicate_delivery_is_ignored()
    {
        var id = Guid.NewGuid();
        (await _sut.HandleAsync(id, Evt("Created", "GPO", true, 1), default)).Should().BeTrue();
        (await _sut.HandleAsync(id, Evt("Created", "GPO", true, 1), default)).Should().BeFalse();
        _uow.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Out_of_order_older_event_does_not_overwrite_newer_state()
    {
        await _sut.HandleAsync(Guid.NewGuid(), Evt("Deactivated", "GPO renamed", false, 3), default);
        await _sut.HandleAsync(Guid.NewGuid(), Evt("Updated", "GPO", true, 2), default);

        _store["500001"].Name.Should().Be("GPO renamed");
        _store["500001"].IsActive.Should().BeFalse();
    }

    [Fact]
    public async Task Deleted_event_marks_the_replica()
    {
        await _sut.HandleAsync(Guid.NewGuid(), Evt("Created", "GPO", true, 1), default);
        await _sut.HandleAsync(Guid.NewGuid(), Evt("Deleted", "GPO", false, 4), default);
        _store["500001"].IsDeleted.Should().BeTrue();
    }
}
