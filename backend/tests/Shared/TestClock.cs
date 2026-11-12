namespace PostalService.Tests;

/// <summary>Deterministic clock for tests.</summary>
public sealed class TestClock : TimeProvider
{
    private DateTimeOffset _now;

    public TestClock(DateTime? utcNow = null) =>
        _now = new DateTimeOffset(utcNow ?? new DateTime(2026, 11, 10, 9, 0, 0, DateTimeKind.Utc));

    public override DateTimeOffset GetUtcNow() => _now;

    public DateTime UtcNow => _now.UtcDateTime;

    public void Advance(TimeSpan by) => _now = _now.Add(by);
}
