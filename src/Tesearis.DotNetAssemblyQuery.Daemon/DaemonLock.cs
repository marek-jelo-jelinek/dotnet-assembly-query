namespace Tesearis.DotNetAssemblyQuery;

/// <summary>
/// The single-instance-per-signature mutex. Backed by an exclusive-create lock file rather than
/// named-pipe bind-failure semantics, which aren't a documented cross-platform contract. Holding
/// the returned <see cref="FileStream"/> open for the daemon's whole lifetime is what makes a
/// second, concurrently-spawned daemon for the same DLL set fail to acquire and exit quietly
/// instead of racing to also bind the pipe.
/// </summary>
internal static class DaemonLock
{
    public static FileStream? TryAcquire(string signature)
    {
        Directory.CreateDirectory(DaemonRegistry.RegistryDirectory);
        var path = LockPath(signature);
        try
        {
            return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException)
        {
            // Another process currently holds this signature's lock - this is the expected
            // "someone else won" outcome, not an error.
            return null;
        }
    }

    /// <summary>Releases the lock and removes the lock file. Call once, on graceful shutdown.</summary>
    public static void Release(FileStream lockHandle, string signature)
    {
        lockHandle.Dispose();
        DeleteLockFile(signature);
    }

    /// <summary>Best-effort deletion of a signature's lock file.</summary>
    public static void DeleteLockFile(string signature)
    {
        try
        {
            File.Delete(LockPath(signature));
        }
        catch
        {
            // Best-effort cleanup.
        }
    }

    private static string LockPath(string signature)
    {
        return Path.Combine(DaemonRegistry.RegistryDirectory, signature + ".lock");
    }
}
