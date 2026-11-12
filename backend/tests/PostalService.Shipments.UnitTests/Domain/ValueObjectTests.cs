using PostalService.BuildingBlocks.Domain;
using PostalService.Shipments.Domain;

namespace PostalService.Shipments.UnitTests.Domain;

public class WeightTests
{
    [Theory]
    [InlineData(0.001, WeightCategory.LessThan1Kg)]
    [InlineData(0.999, WeightCategory.LessThan1Kg)]
    [InlineData(1.000, WeightCategory.Between1And5Kg)] // boundary: 1 is "between"
    [InlineData(3.5, WeightCategory.Between1And5Kg)]
    [InlineData(5.000, WeightCategory.Between1And5Kg)] // boundary: 5 is "between"
    [InlineData(5.001, WeightCategory.Above5Kg)]
    [InlineData(1000, WeightCategory.Above5Kg)]
    public void Category_follows_the_documented_boundaries(double kg, WeightCategory expected) =>
        Weight.FromKilograms((decimal)kg).Category.Should().Be(expected);

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(1000.001)]
    [InlineData(0.0004)] // rounds to 0 g
    public void Rejects_out_of_range_weights(double kg)
    {
        var act = () => Weight.FromKilograms((decimal)kg);
        act.Should().Throw<DomainException>();
    }

    [Fact]
    public void Rounds_to_gram_precision_and_round_trips_through_grams()
    {
        var weight = Weight.FromKilograms(1.23456m);
        weight.Kilograms.Should().Be(1.235m);
        Weight.FromGrams(weight.Grams).Should().Be(weight);
    }
}

public class ShipmentNumberTests
{
    [Fact]
    public void Generated_numbers_are_valid_and_dated()
    {
        var number = ShipmentNumber.Generate(new DateTime(2026, 11, 10, 0, 0, 0, DateTimeKind.Utc));
        number.Value.Should().MatchRegex("^SHP-20261110-[A-Z0-9]{8}$");
        ShipmentNumber.Create(number.Value.ToLowerInvariant()).Should().Be(number);
    }

    [Fact]
    public void Generated_numbers_are_practically_unique()
    {
        var now = DateTime.UtcNow;
        Enumerable.Range(0, 5000).Select(_ => ShipmentNumber.Generate(now).Value).Distinct().Should().HaveCount(5000);
    }

    [Theory]
    [InlineData("")]
    [InlineData("SHP-123")]
    [InlineData("XYZ-20261110-ABCDEFGH")]
    public void Rejects_malformed_numbers(string value) =>
        FluentActions.Invoking(() => ShipmentNumber.Create(value)).Should().Throw<DomainException>();
}

public class ZipCodeTests
{
    [Theory]
    [InlineData("500001", "500001")]
    [InlineData(" sw1a1aa ", "SW1A1AA")]
    public void Normalises_valid_codes(string input, string expected) => ZipCode.Create(input).Value.Should().Be(expected);

    [Theory]
    [InlineData("")]
    [InlineData("12")]
    [InlineData("12345678901")]
    [InlineData("500 001")]
    [InlineData("50-001")]
    public void Rejects_invalid_codes(string input) =>
        FluentActions.Invoking(() => ZipCode.Create(input)).Should().Throw<DomainException>().Which.Code.Should().Be("zip.invalid");
}
