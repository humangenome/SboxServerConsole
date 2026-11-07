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
    // The table makes room at MaxTrackedAddresses (see MakeRoom). It grows past that only while
    // every address in it is locked out, and never past HardCeiling.
    const int MaxTrackedAddresses = 4096;
    const int HardCeiling = 65536;
    const int RoomBatch = MaxTrackedAddresses / 16;

    sealed class State
    {
        public int Failures;
        public DateTime WindowStart;
        public DateTime LockedUntil;
        public DateTime LastFailure;
    }

    readonly Dictionary<string, State> _byAddress = new(StringComparer.Ordinal);
    readonly object _lock = new();
    readonly Func<DateTime> _now;
    int _lookAgainAt = MaxTrackedAddresses;
    DateTime _lookAgainAtTime = DateTime.MaxValue;
    bool _allLockedNoted;
    string? _notice;

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
                if (_byAddress.Count >= MaxTrackedAddresses && (_byAddress.Count >= _lookAgainAt || now >= _lookAgainAtTime))
                    MakeRoom(now);
                s = new State { WindowStart = now };
                _byAddress[key] = s;
            }
            s.LastFailure = now;
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

    // The one line to log when the table first fills with lockouts, with no address in it; null
    // otherwise. A listener calls this after RecordFailure and logs what it gets.
    public string? TakeNotice()
    {
        lock (_lock)
        {
            var notice = _notice;
            _notice = null;
            return notice;
        }
    }

    // Makes room in a full table for a new address. First go the addresses that are neither locked
    // out nor inside a live window, then the longest-quiet addresses that are not locked out, a batch
    // at a time. A running lockout is never dropped to make room: when every address left is locked
    // out, the table keeps them all and grows (one log line, no addresses), up to HardCeiling. Only
    // at the ceiling does the lockout nearest to running out make way. Runs once per batch of new
    // addresses or when the next lockout runs out, not on every wrong password.
    void MakeRoom(DateTime now)
    {
        var quiet = new List<string>();
        var open = new List<KeyValuePair<string, DateTime>>();
        string? soonest = null;
        var soonestUntil = DateTime.MaxValue;
        foreach (var (key, s) in _byAddress)
        {
            if (s.LockedUntil > now)
            {
                if (s.LockedUntil < soonestUntil)
                {
                    soonestUntil = s.LockedUntil;
                    soonest = key;
                }
            }
            else if (now - s.WindowStart > Window) quiet.Add(key);
            else open.Add(new KeyValuePair<string, DateTime>(key, s.LastFailure));
        }
        foreach (var key in quiet) _byAddress.Remove(key);

        int excess = _byAddress.Count - (MaxTrackedAddresses - RoomBatch);
        if (excess > 0 && open.Count > 0)
        {
            open.Sort((a, b) => a.Value.CompareTo(b.Value));
            for (int i = 0; i < excess && i < open.Count; i++) _byAddress.Remove(open[i].Key);
        }

        if (_byAddress.Count < MaxTrackedAddresses)
        {
            _lookAgainAt = MaxTrackedAddresses;
            _lookAgainAtTime = DateTime.MaxValue;
            _allLockedNoted = false;
            return;
        }

        // Every address left is locked out.
        if (!_allLockedNoted)
        {
            _allLockedNoted = true;
            _notice = $"auth table full: all {_byAddress.Count} tracked addresses are locked out; " +
                      $"every lockout is kept (up to {HardCeiling})";
        }
        if (_byAddress.Count >= HardCeiling && soonest is not null)
        {
            _byAddress.Remove(soonest);
            _lookAgainAt = HardCeiling;
        }
        else
        {
            _lookAgainAt = Math.Min(HardCeiling, _byAddress.Count + RoomBatch);
        }
        _lookAgainAtTime = soonestUntil;
    }
}
