using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace Tesearis.DotNetAssemblyQuery;

/// <summary>
/// Best-effort Windows-only process spawning that requests <c>CREATE_BREAKAWAY_FROM_JOB</c>, so a
/// daemon spawned from inside an agent harness that wraps its children in a job object with
/// <c>JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE</c> has a chance to survive that harness exiting. This
/// only works if the parent's own job (if any) was itself created with
/// <c>JOB_OBJECT_LIMIT_BREAKAWAY_OK</c> - something this tool doesn't control - so callers must
/// treat a failure here as "fall back to a normal detached spawn", not a hard error.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class WindowsProcessLauncher
{
    private const uint CREATE_BREAKAWAY_FROM_JOB = 0x01000000;
    private const uint CREATE_NEW_PROCESS_GROUP = 0x00000200;
    private const uint CREATE_NO_WINDOW = 0x08000000;
    private const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;
    private const uint STARTF_USESTDHANDLES = 0x00000100;
    private const uint GENERIC_READ = 0x80000000;
    private const uint GENERIC_WRITE = 0x40000000;
    private const uint FILE_SHARE_READ = 0x00000001;
    private const uint FILE_SHARE_WRITE = 0x00000002;
    private const uint OPEN_EXISTING = 3;
    private const int INVALID_HANDLE_VALUE = -1;

    /// <summary>Spawns <paramref name="fileName"/> detached, breakaway-if-possible, with stdio redirected to NUL.</summary>
    public static bool TryStartDetached(string fileName, IReadOnlyList<string> args)
    {
        var nulRead = IntPtr.Zero;
        var nulWrite = IntPtr.Zero;
        try
        {
            nulRead = OpenNul(GENERIC_READ);
            nulWrite = OpenNul(GENERIC_WRITE);
            if (nulRead == IntPtr.Zero || nulWrite == IntPtr.Zero) return false;

            var startupInfo = new STARTUPINFO
            {
                cb = Marshal.SizeOf<STARTUPINFO>(),
                dwFlags = STARTF_USESTDHANDLES,
                hStdInput = nulRead,
                hStdOutput = nulWrite,
                hStdError = nulWrite,
            };

            var commandLine = new StringBuilder(BuildCommandLine(fileName, args));
            const uint creationFlags = CREATE_BREAKAWAY_FROM_JOB | CREATE_NEW_PROCESS_GROUP | CREATE_NO_WINDOW | CREATE_UNICODE_ENVIRONMENT;

            var created = CreateProcessW(
                null,
                commandLine,
                IntPtr.Zero,
                IntPtr.Zero,
                bInheritHandles: true,
                creationFlags,
                IntPtr.Zero,
                null,
                ref startupInfo,
                out var processInfo);

            if (!created) return false;

            CloseHandle(processInfo.hProcess);
            CloseHandle(processInfo.hThread);
            return true;
        }
        catch
        {
            return false;
        }
        finally
        {
            if (nulRead != IntPtr.Zero)
            {
                CloseHandle(nulRead);
            }

            if (nulWrite != IntPtr.Zero)
            {
                CloseHandle(nulWrite);
            }
        }
    }

    private static IntPtr OpenNul(uint access)
    {
        var securityAttributes = new SECURITY_ATTRIBUTES
        {
            nLength = Marshal.SizeOf<SECURITY_ATTRIBUTES>(),
            bInheritHandle = 1,
        };

        var handle = CreateFileW("NUL", access, FILE_SHARE_READ | FILE_SHARE_WRITE, ref securityAttributes, OPEN_EXISTING, 0, IntPtr.Zero);
        return handle.ToInt64() == INVALID_HANDLE_VALUE ? IntPtr.Zero : handle;
    }

    // Standard Microsoft C runtime argv quoting rules (matches CommandLineToArgvW's expectations).
    private static string BuildCommandLine(string fileName, IReadOnlyList<string> args)
    {
        var sb = new StringBuilder();
        AppendArgument(sb, fileName);
        foreach (var arg in args)
        {
            sb.Append(' ');
            AppendArgument(sb, arg);
        }

        return sb.ToString();
    }

    private static void AppendArgument(StringBuilder sb, string argument)
    {
        if (argument.Length > 0 && argument.IndexOfAny([' ', '\t', '\n', '\v', '"']) < 0)
        {
            sb.Append(argument);
            return;
        }

        sb.Append('"');
        for (var i = 0; i < argument.Length; i++)
        {
            var backslashes = 0;
            while (i < argument.Length && argument[i] == '\\')
            {
                backslashes++;
                i++;
            }

            if (i == argument.Length)
            {
                sb.Append('\\', backslashes * 2);
                break;
            }

            if (argument[i] == '"')
            {
                sb.Append('\\', backslashes * 2 + 1);
                sb.Append('"');
            }
            else
            {
                sb.Append('\\', backslashes);
                sb.Append(argument[i]);
            }
        }

        sb.Append('"');
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFO
    {
        public int cb;
        public string? lpReserved;
        public string? lpDesktop;
        public string? lpTitle;
        public uint dwX;
        public uint dwY;
        public uint dwXSize;
        public uint dwYSize;
        public uint dwXCountChars;
        public uint dwYCountChars;
        public uint dwFillAttribute;
        public uint dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput;
        public IntPtr hStdOutput;
        public IntPtr hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public uint dwProcessId;
        public uint dwThreadId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SECURITY_ATTRIBUTES
    {
        public int nLength;
        public IntPtr lpSecurityDescriptor;
        public int bInheritHandle;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcessW(
        string? lpApplicationName,
        StringBuilder lpCommandLine,
        IntPtr lpProcessAttributes,
        IntPtr lpThreadAttributes,
        bool bInheritHandles,
        uint dwCreationFlags,
        IntPtr lpEnvironment,
        string? lpCurrentDirectory,
        ref STARTUPINFO lpStartupInfo,
        out PROCESS_INFORMATION lpProcessInformation);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateFileW(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        ref SECURITY_ATTRIBUTES lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);
}
