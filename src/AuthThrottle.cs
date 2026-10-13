using System.Net;

namespace SboxServerConsole;

// Slows down password guessing against the HTTP API and the RCON port.
//
// The password is the only credential the agent has, and both listeners are often
// bound to a public address. After MaxFailures wrong passwords from one address
// inside Window, that address is refused for Lockout. Loopback is never locked out,
// so a local panel or proxy keeps working whatever happens on the public side.
public sealed class AuthThrottle
{
    public const int MaxFailures = 10;
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(1);
    public static readonly TimeSpan Lockout = TimeSpan.FromMinutes(5);
    const int MaxTrackedAddresses = 4096;

    sealed class State
    {
        public int Failures;
        public DateTime WindowStart;
        public DateTime LockedUntil;
    }

    readonly Dictionary<string, State> _byAddress = new(StringComparer.Ordinal);
    readonly object _lock = new();
    readonly Func<DateTime> _now;

    public AuthThrottle(Func<DateTime>? now = null) => _now = now ?? (() => DateTime.UtcNow);

    static string? Key(IPAddress? address)
    {
        if (address is null) return null;
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address)) return null;
        return address.ToString();
    }

    public bool IsLockedOut(IPAddress? address)
    {
        var key = Key(address);
        if (key is null) return false;
        lock (_lock)
        {
            return _byAddress.TryGetValue(key, out var s) && s.LockedUntil > _now();
        }
    }

    // Returns true when this failure starts a lockout.
    public bool RecordFailure(IPAddress? address)
    {
        var key = Key(address);
        if (key is null) return false;
        var now = _now();
        lock (_lock)
        {
            if (!_byAddress.TryGetValue(key, out var s))
            {
                if (_byAddress.Count >= MaxTrackedAddresses) Prune(now);
                s = new State { WindowStart = now };
                _byAddress[key] = s;
            }
            if (now - s.WindowStart > Window) { s.WindowStart = now; s.Failures = 0; }
            s.Failures++;
            if (s.Failures >= MaxFailures && s.LockedUntil <= now)
            {
                s.LockedUntil = now + Lockout;
                s.Failures = 0;
                return true;
            }
            return false;
        }
    }

    public void RecordSuccess(IPAddress? address)
    {
        var key = Key(address);
        if (key is null) return;
        lock (_lock) _byAddress.Remove(key);
    }

    void Prune(DateTime now)
    {
        var stale = _byAddress.Where(kv => kv.Value.LockedUntil <= now && now - kv.Value.WindowStart > Window)
            .Select(kv => kv.Key).ToList();
        foreach (var k in stale) _byAddress.Remove(k);
        // Still full of live entries: drop everything rather than grow without bound.
        if (_byAddress.Count >= MaxTrackedAddresses) _byAddress.Clear();
    }
}
