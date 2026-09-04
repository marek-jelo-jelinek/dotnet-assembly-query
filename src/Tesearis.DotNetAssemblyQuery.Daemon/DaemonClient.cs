using System.IO.Pipes;

namespace Tesearis.DotNetAssemblyQuery;

/// <summary>The client half of the daemon protocol: connect-with-timeout, send a request, replay the response.</summary>
public static class DaemonClient
{
    private const int ConnectTimeoutMilliseconds = 200;

    /// <summary>
    /// Tries to serve <paramref name="request"/> via a warm daemon for its <see cref="DaemonRequest.DllPaths"/>.
    /// Returns false (never throwing) on any failure to connect or communicate - callers fall
    /// back to running the query in-process exactly as if no daemon mechanism existed.
    /// </summary>
    public static bool TryRun(DaemonRequest request, out int exitCode)
    {
        exitCode = 1;
        try
        {
            var signature = DllSetSignature.Compute(request.DllPaths);
            var pipeName = DllSetSignature.PipeName(signature);

            using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.None);
            client.Connect(ConnectTimeoutMilliseconds);

            PipeFraming.WriteInt32(client, DaemonProtocolVersion.Current);
            PipeFraming.WriteJson(client, request, DaemonJsonContext.Default.DaemonRequest);

            var response = PipeFraming.ReadJson(client, DaemonJsonContext.Default.DaemonResponse);

            if (response.Stdout.Length > 0)
            {
                Console.Out.Write(response.Stdout);
            }

            if (response.Stderr.Length > 0)
            {
                Console.Error.Write(response.Stderr);
            }

            exitCode = response.ExitCode;
            return true;
        }
        catch
        {
            // No daemon listening, a stale/incompatible one that self-terminated mid-handshake,
            // a sandbox that blocks named pipes, etc. - all equivalent to "no daemon available".
            return false;
        }
    }
}
