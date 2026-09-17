namespace SqlFlow.Delivery.Engine.Protocols.Etp;

/// <summary>
/// ETP timestamps: microseconds since the Unix epoch, UTC (osdu/specs/reservoir-ddms/INTEGRATION.md section 2.4).
/// Every <c>currentDateTime</c>, <c>storeLastWrite</c>, <c>storeCreated</c> and <c>lastChanged</c> is one of these.
/// </summary>
public static class EtpTime
{
    private const long TicksPerMicrosecond = TimeSpan.TicksPerMillisecond / 1000;

    public static long Microseconds(DateTimeOffset moment)
        => (moment.UtcDateTime.Ticks - DateTime.UnixEpoch.Ticks) / TicksPerMicrosecond;

    public static DateTimeOffset At(long microseconds)
        => new(DateTime.UnixEpoch.AddTicks(microseconds * TicksPerMicrosecond), TimeSpan.Zero);
}
