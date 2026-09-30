namespace AxeV2.Access;

/// <summary>
/// Server time for the access timer, independent of the Windows clock.
/// <para>
/// The last signed server time is combined with a monotonic tick counter
/// (<c>GetTickCount64</c>, which keeps counting through sleep and is unaffected by changes
/// to the system time or time zone). Changing the Windows clock therefore neither adds nor
/// removes access time. The clock is re-synced with the server periodically.
/// </para>
/// </summary>
public sealed class TrustedClock
{
    private readonly Func<long> _monotonicMs;
    private long _serverMs;
    private long _syncedAtTick;

    public TrustedClock(Func<long>? monotonicMs = null)
    {
        _monotonicMs = monotonicMs ?? (() => Environment.TickCount64);
    }

    public bool IsSynced { get; private set; }

    /// <summary>Records a verified server time (Unix ms).</summary>
    public void Sync(long serverNowMs)
    {
        _serverMs = serverNowMs;
        _syncedAtTick = _monotonicMs();
        IsSynced = true;
    }

    /// <summary>Current server time estimate (Unix ms).</summary>
    public long NowMs => _serverMs + Math.Max(0, _monotonicMs() - _syncedAtTick);

    /// <summary>Time left until <paramref name="expiresAtMs"/>; zero once passed.</summary>
    public TimeSpan Remaining(long expiresAtMs) =>
        IsSynced ? TimeSpan.FromMilliseconds(Math.Max(0, expiresAtMs - NowMs)) : TimeSpan.Zero;

    /// <summary>h:mm:ss, rounded up so "0:00:00" only shows when access has ended.</summary>
    public static string Format(TimeSpan remaining)
    {
        var seconds = (long)Math.Ceiling(Math.Max(0, remaining.TotalSeconds));
        return $"{seconds / 3600}:{seconds / 60 % 60:00}:{seconds % 60:00}";
    }
}
