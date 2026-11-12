using PostalService.Shipments.Domain;

namespace PostalService.Shipments.UnitTests.Domain;

public class ShipmentStatusRulesTests
{
    [Fact]
    public void Every_status_has_a_transition_entry() =>
        Enum.GetValues<ShipmentStatus>().Should().AllSatisfy(s =>
            FluentActions.Invoking(() => ShipmentStatusRules.NextStatuses(s, false)).Should().NotThrow());

    [Theory]
    [InlineData(ShipmentStatus.Delivered)]
    [InlineData(ShipmentStatus.Lost)]
    public void Terminal_statuses_have_no_next_status(ShipmentStatus status)
    {
        ShipmentStatusRules.IsTerminal(status).Should().BeTrue();
        ShipmentStatusRules.NextStatuses(status, isLocalDelivery: true).Should().BeEmpty();
    }

    [Fact]
    public void Every_non_terminal_status_can_be_marked_lost() =>
        Enum.GetValues<ShipmentStatus>().Where(s => !ShipmentStatusRules.IsTerminal(s))
            .Should().AllSatisfy(s => ShipmentStatusRules.IsAllowed(s, ShipmentStatus.Lost, false).Should().BeTrue());

    [Fact]
    public void Shortcut_to_out_for_delivery_exists_only_for_local_delivery()
    {
        ShipmentStatusRules.IsAllowed(ShipmentStatus.ProcessedAtOrigin, ShipmentStatus.OutForDelivery, true).Should().BeTrue();
        ShipmentStatusRules.IsAllowed(ShipmentStatus.ProcessedAtOrigin, ShipmentStatus.OutForDelivery, false).Should().BeFalse();
    }

    [Theory]
    [InlineData(ShipmentStatus.ProcessedAtOrigin, StatusLocation.Origin)]
    [InlineData(ShipmentStatus.InTransit, StatusLocation.Origin)]
    [InlineData(ShipmentStatus.ReceivedAtDestination, StatusLocation.Destination)]
    [InlineData(ShipmentStatus.Delivered, StatusLocation.Destination)]
    [InlineData(ShipmentStatus.Lost, StatusLocation.Anywhere)]
    public void Location_rules(ShipmentStatus status, StatusLocation expected) =>
        ShipmentStatusRules.RequiredLocation(status).Should().Be(expected);
}
