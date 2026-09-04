using System.Security.Cryptography;
using System.Text;

namespace Tesearis.DotNetAssemblyQuery;

/// <summary>
/// Derives a stable identity for a resolved DLL set, used to key a daemon instance's registry
/// entry, lock file, and named pipe. Deliberately identity-only (path set + user, no
/// mtime/size) - two invocations against the same DLLs always reach the same daemon regardless
/// of whether the files have changed since; per-request freshness is <see cref="DaemonHost"/>'s
/// job, not this one's.
/// </summary>
internal static class DllSetSignature
{
    // Bumping this literal invalidates every previously-computed signature for free, without
    // needing to touch anything else - useful if the protocol/format ever changes shape.
    private const string SignatureFormatTag = "daq-dllset-v1";

    public static string Compute(IReadOnlyList<string> dllPaths)
    {
        var normalized = new List<string>(dllPaths.Count);
        foreach (var path in dllPaths)
        {
            var full = Path.GetFullPath(path);
            normalized.Add(OperatingSystem.IsWindows() ? full.ToLowerInvariant() : full);
        }

        normalized.Sort(StringComparer.Ordinal);

        var userIdentity = Environment.UserName;
        var input = SignatureFormatTag + "\n" + userIdentity + "\n" + string.Join("\n", normalized);

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(hash)[..32].ToLowerInvariant();
    }

    public static string PipeName(string signature) => $"daq-{signature}";
}
