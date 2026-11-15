namespace Sentinel.Core.Model;

public sealed record Enrollment(
    string SensorId,
    string HostId,
    string? HostName,
    DateTimeOffset EnrolledAt,
    int ConfigGeneration,
    long ClockOffsetMs,
    long ClockUncertaintyMs)
{
    public bool ClockUncertain => ClockOffsetMs != 0 || ClockUncertaintyMs > 0;
}
