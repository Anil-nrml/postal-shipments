using System.Security.Cryptography;
using System.Text.RegularExpressions;
using PostalService.BuildingBlocks.Domain;

namespace PostalService.Shipments.Domain;

/// <summary>Weight in kilograms, kept to gram precision (3 decimals).</summary>
public sealed record Weight
{
    public const decimal MaxKilograms = 1000m;

    private Weight(decimal kilograms) => Kilograms = kilograms;

    public decimal Kilograms { get; }

    public long Grams => (long)(Kilograms * 1000m);

    public WeightCategory Category => CategoryFor(Kilograms);

    public static Weight FromKilograms(decimal kilograms)
    {
        if (kilograms <= 0)
            throw new DomainException("weight.not-positive", "Weight must be greater than 0 kg.");
        if (kilograms > MaxKilograms)
            throw new DomainException("weight.too-heavy", $"Weight must not exceed {MaxKilograms} kg.");

        var rounded = Math.Round(kilograms, 3, MidpointRounding.AwayFromZero);
        if (rounded <= 0)
            throw new DomainException("weight.not-positive", "Weight must be at least 1 gram.");
        return new Weight(rounded);
    }

    public static Weight FromGrams(long grams) => new(grams / 1000m);

    /// <summary>Boundaries from the requirements: &lt;1, [1,5], &gt;5.</summary>
    public static WeightCategory CategoryFor(decimal kilograms) => kilograms switch
    {
        < 1m => WeightCategory.LessThan1Kg,
        <= 5m => WeightCategory.Between1And5Kg,
        _ => WeightCategory.Above5Kg,
    };
}

/// <summary>Human-facing unique shipment number, e.g. <c>SHP-20261110-7K3QX9MZ</c>.</summary>
public sealed partial record ShipmentNumber
{
    private const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789"; // no 0/O/1/I to avoid misreads

    private ShipmentNumber(string value) => Value = value;

    public string Value { get; }

    public static ShipmentNumber Create(string? value)
    {
        var normalized = (value ?? string.Empty).Trim().ToUpperInvariant();
        if (!Pattern().IsMatch(normalized))
            throw new DomainException("shipment-number.invalid", $"'{value}' is not a valid shipment number.");
        return new ShipmentNumber(normalized);
    }

    public static ShipmentNumber Generate(DateTime utcNow)
    {
        Span<char> suffix = stackalloc char[8];
        for (var i = 0; i < suffix.Length; i++)
            suffix[i] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
        return new ShipmentNumber($"SHP-{utcNow:yyyyMMdd}-{new string(suffix)}");
    }

    public override string ToString() => Value;

    [GeneratedRegex("^SHP-[0-9]{8}-[A-Z0-9]{8}$")]
    private static partial Regex Pattern();
}
