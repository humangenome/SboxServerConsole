using System.Collections;
using System.Net;
using System.Reflection;
using Xunit;

namespace SboxServerConsole.Tests;

// The wrong-password table holds 4096 addresses before it makes room and 65536 at most. A flood of
// wrong passwords from new addresses must never lift a lockout that is running.
public class AuthThrottleTableTests
{
    const int TableLimit = 4096;
    const int TableCeiling = 65536;
    static readonly DateTime Start = new(2026, 11, 7, 12, 0, 0, DateTimeKind.Utc);

    static IPAddress Addr(int i) => new(new byte[] { 10, (byte)((i >> 16) & 255), (byte)((i >> 8) & 255), (byte)(i & 255) });

    static void LockOut(AuthThrottle th, IPAddress address)
    {
        for (int i = 0; i < AuthThrottle.MaxFailures; i++) th.RecordFailure(address);
    }

    static int Tracked(AuthThrottle th)
    {
        var field = typeof(AuthThrottle).GetField("_byAddress", BindingFlags.NonPublic | BindingFlags.Instance)!;
        return ((ICollection)field.GetValue(th)!).Count;
    }

    [Fact]
    public void AFloodOfNewAddressesNeverLiftsALockout()
    {
        var t = Start;
        var th = new AuthThrottle(() => t);
        var guesser = IPAddress.Parse("203.0.113.77");
        LockOut(th, guesser);
        Assert.True(th.IsLockedOut(guesser));

        t = t.AddSeconds(1);
        for (int i = 0; i < TableLimit * 3; i++) th.RecordFailure(Addr(i));

        Assert.True(th.IsLockedOut(guesser), "wrong passwords from many new addresses must not lift a lockout");
        Assert.True(Tracked(th) <= TableLimit, "addresses that are not locked out make room for new ones");
    }

    [Fact]
    public void TheLongestQuietAddressesThatAreNotLockedOutMakeRoomFirst()
    {
        var t = Start;
        var th = new AuthThrottle(() => t);
        var guesser = IPAddress.Parse("203.0.113.77");
        for (int i = 0; i < TableLimit - 1; i++) th.RecordFailure(Addr(i));
        t = t.AddSeconds(5);
        for (int i = 0; i < AuthThrottle.MaxFailures - 1; i++) th.RecordFailure(guesser);

        t = t.AddSeconds(1);
        th.RecordFailure(Addr(TableLimit));
        t = t.AddSeconds(1);
        Assert.True(th.RecordFailure(guesser), "the newest guesser keeps its count; the oldest rows go");
    }

    [Fact]
    public void ATableFullOfLockoutsKeepsThemUntilTheCeilingThenDropsTheOneNearestToRunningOut()
    {
        var t = Start;
        var th = new AuthThrottle(() => t);
        var first = IPAddress.Parse("203.0.113.77");
        LockOut(th, first);
        t = t.AddSeconds(10);
        for (int i = 0; i < TableCeiling - 1; i++) LockOut(th, Addr(i));

        t = t.AddSeconds(10);
        Assert.Equal(TableCeiling, Tracked(th));
        Assert.True(th.IsLockedOut(first));
        Assert.True(th.IsLockedOut(Addr(0)));

        var newcomer = IPAddress.Parse("198.51.100.9");
        LockOut(th, newcomer);
        Assert.Equal(TableCeiling, Tracked(th));
        Assert.True(th.IsLockedOut(newcomer));
        Assert.False(th.IsLockedOut(first), "at the ceiling the lockout nearest to running out makes way");
        Assert.True(th.IsLockedOut(Addr(0)));
        Assert.True(th.IsLockedOut(Addr(TableCeiling - 2)));
    }
}
