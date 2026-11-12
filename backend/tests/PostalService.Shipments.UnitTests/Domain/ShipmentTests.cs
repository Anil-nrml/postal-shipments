using PostalService.BuildingBlocks.Domain;
using PostalService.Shipments.Domain;
using PostalService.Tests;

namespace PostalService.Shipments.UnitTests.Domain;

public class ShipmentTests
{
    private static readonly ZipCode Origin = ZipCode.Create("500001");
    private static readonly ZipCode Destination = ZipCode.Create("560001");
    private static readonly ZipCode Elsewhere = ZipCode.Create("110001");
    private static readonly Guid User = Guid.NewGuid();
    private readonly TestClock _clock = new();

    private Shipment NewShipment(ZipCode? destination = null, decimal kg = 2.5m) =>
        Shipment.Create(ShipmentNumber.Generate(_clock.UtcNow), ShipmentType.Package, Weight.FromKilograms(kg), Origin,
            destination ?? Destination, User, "clerk", _clock.UtcNow);

    private void Move(Shipment s, ShipmentStatus status, ZipCode at) => s.ChangeStatus(status, at, User, "clerk", null, _clock.UtcNow);

    [Fact]
    public void Create_starts_at_origin_with_one_history_entry_and_an_event()
    {
        var s = NewShipment();

        s.Status.Should().Be(ShipmentStatus.ReceivedAtOrigin);
        s.CurrentZip.Should().Be(Origin);
        s.WeightCategory.Should().Be(WeightCategory.Between1And5Kg);
        s.Version.Should().Be(1);
        s.History.Should().ContainSingle().Which.Status.Should().Be(ShipmentStatus.ReceivedAtOrigin);
        s.DomainEvents.Should().ContainSingle().Which.Should().BeOfType<ShipmentCreated>();
    }

    [Fact]
    public void Full_inter_city_lifecycle_is_accepted_and_audited()
    {
        var s = NewShipment();
        Move(s, ShipmentStatus.ProcessedAtOrigin, Origin);
        Move(s, ShipmentStatus.InTransit, Origin);
        Move(s, ShipmentStatus.ReceivedAtDestination, Destination);
        Move(s, ShipmentStatus.ProcessedAtDestination, Destination);
        Move(s, ShipmentStatus.OutForDelivery, Destination);
        Move(s, ShipmentStatus.Delivered, Destination);

        s.Status.Should().Be(ShipmentStatus.Delivered);
        s.CurrentZip.Should().Be(Destination);
        s.IsActive.Should().BeFalse();
        s.History.Select(h => h.Status).Should().HaveCount(7);
        s.History.Last().PreviousStatus.Should().Be(ShipmentStatus.OutForDelivery);
        s.Version.Should().Be(7);
    }

    [Fact]
    public void Local_delivery_may_skip_transit()
    {
        var s = NewShipment(destination: Origin);
        Move(s, ShipmentStatus.ProcessedAtOrigin, Origin);
        Move(s, ShipmentStatus.OutForDelivery, Origin);
        Move(s, ShipmentStatus.Delivered, Origin);
        s.Status.Should().Be(ShipmentStatus.Delivered);
    }

    [Fact]
    public void Non_local_delivery_cannot_skip_transit()
    {
        var s = NewShipment();
        Move(s, ShipmentStatus.ProcessedAtOrigin, Origin);
        var act = () => Move(s, ShipmentStatus.OutForDelivery, Destination);
        act.Should().Throw<DomainException>().Which.Code.Should().Be("status.transition-invalid");
    }

    [Theory]
    [InlineData(ShipmentStatus.InTransit)]
    [InlineData(ShipmentStatus.Delivered)]
    [InlineData(ShipmentStatus.ReceivedAtDestination)]
    public void Invalid_transitions_are_rejected(ShipmentStatus target)
    {
        var s = NewShipment();
        var act = () => Move(s, target, target == ShipmentStatus.InTransit ? Origin : Destination);
        act.Should().Throw<DomainException>().Which.Code.Should().Be("status.transition-invalid");
        s.Status.Should().Be(ShipmentStatus.ReceivedAtOrigin);
        s.History.Should().HaveCount(1);
    }

    [Fact]
    public void Status_must_be_recorded_at_the_right_post_office()
    {
        var s = NewShipment();
        var act = () => Move(s, ShipmentStatus.ProcessedAtOrigin, Elsewhere);
        act.Should().Throw<DomainException>().Which.Code.Should().Be("status.wrong-location");
    }

    [Fact]
    public void Failed_attempt_can_be_retried_and_lost_can_happen_anywhere()
    {
        var s = NewShipment(destination: Origin);
        Move(s, ShipmentStatus.ProcessedAtOrigin, Origin);
        Move(s, ShipmentStatus.OutForDelivery, Origin);
        Move(s, ShipmentStatus.FailedDeliveryAttempt, Origin);
        Move(s, ShipmentStatus.OutForDelivery, Origin);
        Move(s, ShipmentStatus.Lost, Elsewhere);
        s.Status.Should().Be(ShipmentStatus.Lost);
        s.CurrentZip.Should().Be(Elsewhere);
    }

    [Fact]
    public void Delivered_is_terminal_without_the_correction_workflow()
    {
        var s = NewShipment(destination: Origin);
        Move(s, ShipmentStatus.ProcessedAtOrigin, Origin);
        Move(s, ShipmentStatus.OutForDelivery, Origin);
        Move(s, ShipmentStatus.Delivered, Origin);

        var act = () => Move(s, ShipmentStatus.OutForDelivery, Origin);
        act.Should().Throw<DomainException>().Which.Code.Should().Be("status.terminal");
    }

    [Fact]
    public void Correction_reverts_delivered_with_a_reason_and_is_flagged()
    {
        var s = NewShipment(destination: Origin);
        Move(s, ShipmentStatus.ProcessedAtOrigin, Origin);
        Move(s, ShipmentStatus.OutForDelivery, Origin);
        Move(s, ShipmentStatus.Delivered, Origin);

        s.CorrectStatus(ShipmentStatus.OutForDelivery, Origin, User, "admin", "Scanned wrong parcel as delivered", _clock.UtcNow);

        s.Status.Should().Be(ShipmentStatus.OutForDelivery);
        s.History.Last().IsCorrection.Should().BeTrue();
        s.History.Last().Remarks.Should().Be("Scanned wrong parcel as delivered");
    }

    [Fact]
    public void Correction_requires_a_meaningful_reason()
    {
        var s = NewShipment();
        var act = () => s.CorrectStatus(ShipmentStatus.Lost, Origin, User, "admin", "oops", _clock.UtcNow);
        act.Should().Throw<DomainException>().Which.Code.Should().Be("correction.reason-required");
    }

    [Fact]
    public void Details_are_editable_only_before_processing()
    {
        var s = NewShipment();
        s.UpdateDetails(ShipmentType.Letter, Weight.FromKilograms(0.2m), Elsewhere, _clock.UtcNow);
        s.Type.Should().Be(ShipmentType.Letter);
        s.WeightCategory.Should().Be(WeightCategory.LessThan1Kg);
        s.DestinationZip.Should().Be(Elsewhere);

        Move(s, ShipmentStatus.ProcessedAtOrigin, Origin);
        var act = () => s.UpdateDetails(ShipmentType.Package, Weight.FromKilograms(9), Destination, _clock.UtcNow);
        act.Should().Throw<DomainException>().Which.Code.Should().Be("shipment.not-editable");
        FluentActions.Invoking(() => s.MarkDeleted(_clock.UtcNow)).Should().Throw<DomainException>();
    }

    [Fact]
    public void Every_change_increments_the_concurrency_version()
    {
        var s = NewShipment();
        var before = s.Version;
        Move(s, ShipmentStatus.ProcessedAtOrigin, Origin);
        s.Version.Should().Be(before + 1);
    }
}
