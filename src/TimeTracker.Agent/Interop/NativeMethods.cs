using System.Runtime.InteropServices;
using System.Text;

namespace TimeTracker.Agent.Interop;

internal static class NativeMethods
{
    [DllImport("user32.dll")]
    internal static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    internal static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    internal static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll")]
    internal static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);

    [StructLayout(LayoutKind.Sequential)]
    internal struct LASTINPUTINFO
    {
        public uint cbSize;
        public uint dwTime;
    }

    [DllImport("wtsapi32.dll", SetLastError = true)]
    private static extern bool WTSQuerySessionInformationW(
        IntPtr hServer, int sessionId, int wtsInfoClass, out IntPtr buffer, out int bytesReturned);

    [DllImport("wtsapi32.dll")]
    private static extern void WTSFreeMemory(IntPtr memory);

    private const int WtsCurrentSession = -1;
    private const int WtsSessionInfoEx = 25;
    private const int WtsSessionStateLock = 0;

    /// <summary>
    /// This session's Windows sign-in time and whether it is currently locked, or null if Windows
    /// won't say. Reads WTSINFOEX_LEVEL1_W by offset: SessionFlags at 16, LogonTime (FILETIME) at
    /// 168 - verified on Windows 11 against `query user` and the known lock state. The lock flag
    /// is reported inverted on Windows 7 / Server 2008 R2, which this Agent doesn't target.
    /// </summary>
    internal static (DateTimeOffset LogonUtc, bool IsLocked)? GetSessionInfo()
    {
        if (!WTSQuerySessionInformationW(IntPtr.Zero, WtsCurrentSession, WtsSessionInfoEx, out var buffer, out var bytes))
        {
            return null;
        }

        try
        {
            if (bytes < 176 || Marshal.ReadInt32(buffer, 0) != 1)
            {
                return null;
            }

            var flags = Marshal.ReadInt32(buffer, 16);
            var logonFileTime = Marshal.ReadInt64(buffer, 168);
            if (logonFileTime <= 0)
            {
                return null;
            }

            return (DateTimeOffset.FromFileTime(logonFileTime).ToUniversalTime(), flags == WtsSessionStateLock);
        }
        finally
        {
            WTSFreeMemory(buffer);
        }
    }

    internal static string GetWindowTitle(IntPtr hWnd)
    {
        var length = GetWindowTextLength(hWnd);
        if (length == 0)
        {
            return string.Empty;
        }

        var builder = new StringBuilder(length + 1);
        GetWindowText(hWnd, builder, builder.Capacity);
        return builder.ToString();
    }

    /// <summary>
    /// Milliseconds since the last keyboard/mouse input, computed against the
    /// wrapping 32-bit tick counter GetLastInputInfo uses.
    /// </summary>
    internal static uint GetIdleMilliseconds()
    {
        var lastInput = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
        if (!GetLastInputInfo(ref lastInput))
        {
            return 0;
        }

        return unchecked((uint)Environment.TickCount - lastInput.dwTime);
    }
}
