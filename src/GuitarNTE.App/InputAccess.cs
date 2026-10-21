using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace GuitarNTE.App;

internal sealed record IntegrityInfo(int? Level, int Error = 0)
{
    internal string Label => Level switch
    {
        null => "无法读取",
        >= 0x4000 => "系统权限",
        >= 0x3000 => "管理员权限",
        >= 0x2000 => "普通权限",
        _ => "低权限"
    };
}
internal sealed record InputAccessReport(IntegrityInfo Sender, IntegrityInfo Target)
{
    internal bool Blocked => Sender.Level.HasValue && Target.Level.HasValue && Sender.Level.Value < Target.Level.Value;
    internal bool Known => Sender.Level.HasValue && Target.Level.HasValue;
    internal bool CanRestartAsAdmin => Blocked && Sender.Level < 0x3000 && Target.Level <= 0x3000;
    internal string Message => Blocked
        ? $"游戏：{Target.Label} · 工具：{Sender.Label}。Windows 会拦截按键，请点击「管理员重启」并确认系统弹窗。"
        : Known ? $"游戏：{Target.Label} · 工具：{Sender.Label}。输入权限匹配。"
        : $"游戏：{Target.Label} · 工具：{Sender.Label}。权限检查不完整（错误 {Target.Error}/{Sender.Error}）。";
    internal void RequireCompatible()
    {
        if (Blocked) throw new InvalidOperationException(Message);
    }
}
internal static class InputAccess
{
    [DllImport("kernel32.dll", SetLastError = true)] static extern SafeProcessHandle OpenProcess(uint access, bool inherit, int processId);
    [DllImport("advapi32.dll", SetLastError = true)] static extern bool OpenProcessToken(SafeProcessHandle process, uint access, out SafeAccessTokenHandle token);
    [DllImport("advapi32.dll", SetLastError = true)] static extern bool GetTokenInformation(SafeAccessTokenHandle token, int informationClass, nint buffer, int bufferLength, out int required);
    [DllImport("advapi32.dll")] static extern nint GetSidSubAuthorityCount(nint sid);
    [DllImport("advapi32.dll")] static extern nint GetSidSubAuthority(nint sid, uint index);

    internal static IntegrityInfo Inspect(int processId)
    {
        using var process = OpenProcess(0x1000, false, processId); // QUERY_LIMITED_INFORMATION, read-only
        if (process.IsInvalid) return new(null, Marshal.GetLastWin32Error());
        if (!OpenProcessToken(process, 0x0008, out var token)) return new(null, Marshal.GetLastWin32Error());
        using (token)
        {
            GetTokenInformation(token, 25, 0, 0, out int length); // TokenIntegrityLevel
            if (length <= 0) return new(null, Marshal.GetLastWin32Error());
            nint buffer = Marshal.AllocHGlobal(length);
            try
            {
                if (!GetTokenInformation(token, 25, buffer, length, out _)) return new(null, Marshal.GetLastWin32Error());
                nint sid = Marshal.ReadIntPtr(buffer);
                byte count = Marshal.ReadByte(GetSidSubAuthorityCount(sid));
                return count == 0 ? new(null, 87) : new(Marshal.ReadInt32(GetSidSubAuthority(sid, (uint)(count - 1))));
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
    }
    internal static InputAccessReport Check(uint targetProcessId) => new(Inspect(Environment.ProcessId), Inspect((int)targetProcessId));
}

internal static class ElevatedRestart
{
    internal static ProcessStartInfo CreateStartInfo(string exe, string dataRoot, string? songId, int parentId)
    {
        var info = new ProcessStartInfo(exe) { UseShellExecute = true, Verb = "runas", WorkingDirectory = AppContext.BaseDirectory };
        info.ArgumentList.Add("--data-directory"); info.ArgumentList.Add(Path.GetFullPath(dataRoot));
        info.ArgumentList.Add("--wait-for-exit"); info.ArgumentList.Add(parentId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (songId != null) { info.ArgumentList.Add("--select-song"); info.ArgumentList.Add(songId); }
        return info;
    }
    internal static bool Start(string dataRoot, string? songId)
    {
        string exe = Environment.ProcessPath ?? throw new InvalidOperationException("无法确定程序路径。");
        try
        {
            using var child = Process.Start(CreateStartInfo(exe, dataRoot, songId, Environment.ProcessId));
            if (child == null) throw new InvalidOperationException("管理员进程未能启动，当前窗口已保留。");
            return true;
        }
        catch (Win32Exception e) when (e.NativeErrorCode == 1223) { return false; } // User cancelled UAC.
    }
}
