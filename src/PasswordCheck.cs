using System.Security.Cryptography;
using System.Text;

namespace SboxServerConsole;

// The one password comparison for HTTP and RCON. Both sides are hashed first, so the
// comparison takes the same time whatever the supplied password's length or content.
public static class PasswordCheck
{
    public static bool Matches(string? supplied, string expected)
    {
        if (string.IsNullOrEmpty(expected)) return false;
        Span<byte> a = stackalloc byte[32];
        Span<byte> b = stackalloc byte[32];
        SHA256.HashData(Encoding.UTF8.GetBytes(supplied ?? ""), a);
        SHA256.HashData(Encoding.UTF8.GetBytes(expected), b);
        return CryptographicOperations.FixedTimeEquals(a, b);
    }
}
