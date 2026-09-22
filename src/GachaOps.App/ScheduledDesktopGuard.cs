using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using GachaOps.Core.Services;

namespace GachaOps.App;

internal static class ScheduledDesktopGuard
{
    public static string? Check()
    {
        var session = Process.GetCurrentProcess().SessionId;
        if (!WTSQuerySessionInformation(IntPtr.Zero, session, 8, out var buffer, out var bytes))
            return "无法确认登录会话";
        bool active;
        try { active = bytes >= 4 && Marshal.ReadInt32(buffer) == 0; }
        finally { WTSFreeMemory(buffer); }
        var desktop = OpenInputDesktop(0, false, 1);
        if (desktop == IntPtr.Zero) return "桌面已锁定或状态无法确认";
        try
        {
            var name = new StringBuilder(256);
            if (!GetUserObjectInformation(desktop, 2, name, name.Capacity * 2, out _))
                return "无法确认输入桌面";
            var foreground = GetForegroundWindow();
            if (foreground == IntPtr.Zero) return "无法确认前台窗口";
            var shell = GetShellWindow();
            GetWindowThreadProcessId(foreground, out var foregroundPid);
            GetWindowThreadProcessId(shell, out var shellPid);
            var className = new StringBuilder(256);
            GetClassName(foreground, className, className.Capacity);
            return ScheduledLaunch.ForegroundBlock(active, true, name.ToString(),
                foregroundPid == Environment.ProcessId, shell != IntPtr.Zero && foreground == shell,
                shellPid != 0 && foregroundPid == shellPid, className.ToString());
        }
        finally { CloseDesktop(desktop); }
    }

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern IntPtr GetShellWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr window, StringBuilder name, int count);
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr OpenInputDesktop(uint flags, bool inherit, uint access);
    [DllImport("user32.dll")] private static extern bool CloseDesktop(IntPtr desktop);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetUserObjectInformation(IntPtr handle, int index, StringBuilder value, int length, out int needed);
    [DllImport("wtsapi32.dll", CharSet = CharSet.Unicode)] private static extern bool WTSQuerySessionInformation(IntPtr server, int session, int information, out IntPtr buffer, out int bytes);
    [DllImport("wtsapi32.dll")] private static extern void WTSFreeMemory(IntPtr buffer);
}
