using System.Text.RegularExpressions;

namespace PostalService.BuildingBlocks.Domain;

/// <summary>
/// Post office identifier. Alphanumeric, 3–10 characters, stored upper-case
/// (covers Indian 6-digit PINs as well as alphanumeric postcodes).
/// </summary>
public sealed partial record ZipCode
{
    private ZipCode(string value) => Value = value;

    public string Value { get; }

    public static ZipCode Create(string? value)
    {
        var normalized = (value ?? string.Empty).Trim().ToUpperInvariant();
        if (!Pattern().IsMatch(normalized))
            throw new DomainException("zip.invalid", $"'{value}' is not a valid ZIP code (3-10 letters or digits).");
        return new ZipCode(normalized);
    }

    public static bool TryCreate(string? value, out ZipCode? zip)
    {
        try
        {
            zip = Create(value);
            return true;
        }
        catch (DomainException)
        {
            zip = null;
            return false;
        }
    }

    public override string ToString() => Value;

    [GeneratedRegex("^[A-Z0-9]{3,10}$")]
    private static partial Regex Pattern();
}
