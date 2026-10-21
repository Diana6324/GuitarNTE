using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using GuitarNTE.Core;

namespace GuitarNTE.App;
public record WindowChoice(nint Handle, uint ProcessId, string Title, string ProcessName = "")
{
    public override string ToString() => $"{(Title.Length > 70 ? Title[..70] + "…" : Title)}  ·  {ProcessName}  ·  PID {ProcessId}";
}
internal static class Native
{
    internal delegate bool EnumWindowProc(nint hwnd, nint param);
    [DllImport("user32.dll")] internal static extern bool EnumWindows(EnumWindowProc callback, nint param);
    [DllImport("user32.dll")] internal static extern bool IsWindowVisible(nint hwnd);
    [DllImport("user32.dll")] internal static extern nint GetForegroundWindow();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern int GetWindowText(nint hwnd, System.Text.StringBuilder text, int count);
    [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(nint hwnd, out uint pid);
    [DllImport("user32.dll", SetLastError = true)] internal static extern bool RegisterHotKey(nint hwnd, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] internal static extern bool UnregisterHotKey(nint hwnd, int id);
    [DllImport("user32.dll", SetLastError = true)] internal static extern uint SendInput(uint count, INPUT[] input, int size);
    [DllImport("user32.dll")] internal static extern short GetAsyncKeyState(int key);
    [StructLayout(LayoutKind.Sequential)] internal struct INPUT { public uint type; public INPUTUNION data; }
    [StructLayout(LayoutKind.Explicit)] internal struct INPUTUNION { [FieldOffset(0)] public KEYBDINPUT keyboard; [FieldOffset(0)] public MOUSEINPUT mouse; }
    [StructLayout(LayoutKind.Sequential)] internal struct KEYBDINPUT { public ushort vk, scan; public uint flags, time; public nuint extra; }
    [StructLayout(LayoutKind.Sequential)] internal struct MOUSEINPUT { public int x, y; public uint data, flags, time; public nuint extra; }
    internal static List<WindowChoice> Windows()
    {
        var result = new List<WindowChoice>();
        EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd)) return true;
            var title = new System.Text.StringBuilder(512); GetWindowText(hwnd, title, title.Capacity);
            GetWindowThreadProcessId(hwnd, out uint pid);
            if (title.Length > 0 && pid != Environment.ProcessId)
            {
                string processName = "";
                try { using var process = Process.GetProcessById((int)pid); processName = process.ProcessName; } catch { }
                result.Add(new(hwnd, pid, title.ToString(), processName));
            }
            return true;
        }, 0);
        return result.OrderBy(x => x.Title).ToList();
    }
    internal static WindowChoice? SelectGameWindow(IEnumerable<WindowChoice> windows)
    {
        var exact = windows.Where(w => new[] { "异环", "NTE", "Neverness to Everness" }.Contains(w.Title.Trim(), StringComparer.OrdinalIgnoreCase)).ToList();
        return exact.FirstOrDefault(w => w.ProcessName.Equals("HTGame", StringComparison.OrdinalIgnoreCase)) ?? exact.FirstOrDefault();
    }
}
public sealed class WindowsKeyboardOutput : IKeyboardOutput
{
    readonly KeyboardSession session = new(Send);
    WindowChoice? target;
    uint accessCheckedProcess;
    public WindowChoice? Target { get => target; set { target = value; accessCheckedProcess = 0; } }
    public Dictionary<int, string> KeyMap { get; set; } = KeyboardLayout.DefaultMap();
    public InstrumentMode Instrument { get; set; }
    public bool IsTargetForeground
    {
        get
        {
            if (Target == null || Native.GetForegroundWindow() != Target.Handle) return false;
            Native.GetWindowThreadProcessId(Target.Handle, out uint pid);
            return pid == Target.ProcessId;
        }
    }
    static void Send(IReadOnlyList<KeyStroke> strokes)
    {
        if (strokes.Count == 0) return;
        var events = strokes.Select(stroke =>
        {
            var k = stroke.Key;
            return new Native.INPUT { type = 1, data = new Native.INPUTUNION { keyboard = new Native.KEYBDINPUT { scan = k.ScanCode, flags = 8u | (stroke.Up ? 2u : 0u) | (k.Extended ? 1u : 0u) } } };
        }).ToArray();
        uint sent = Native.SendInput((uint)events.Length, events, Marshal.SizeOf<Native.INPUT>());
        if (sent != events.Length) throw new Win32Exception(Marshal.GetLastWin32Error(), "键盘输入未完整发送，请检查游戏与工具权限是否一致。");
    }
    public void KeyDown(IReadOnlyList<int> pitches)
    {
        if (!IsTargetForeground) throw new InvalidOperationException("所选游戏窗口不在前台。");
        if (accessCheckedProcess != Target!.ProcessId)
        {
            InputAccess.Check(Target.ProcessId).RequireCompatible();
            accessCheckedProcess = Target.ProcessId;
        }
        if (new[] { 0x10, 0x11, 0x12, 0x5B, 0x5C }.Any(vk => Native.GetAsyncKeyState(vk) < 0)) throw new InvalidOperationException("请松开 Shift、Ctrl、Alt 和 Win 键后再演奏。");
        session.KeyDown(pitches, KeyMap, Instrument);
    }
    public void KeyUp(IReadOnlyList<int> pitches) => session.KeyUp(pitches);
    public void ReleaseAll() => session.ReleaseAll();
}
internal sealed class Hotkeys : IDisposable
{
    readonly HwndSource source;
    string toggle = "F8", stop = "F9";
    bool registered;
    internal event Action<bool>? Pressed;
    internal Hotkeys(nint hwnd) { source = HwndSource.FromHwnd(hwnd)!; source.AddHook(Hook); }
    internal void Register(string newToggle, string newStop)
    {
        var oldToggle = toggle; var oldStop = stop; bool hadOld = registered;
        Clear();
        if (!TryRegister(newToggle, newStop))
        {
            bool restored = hadOld && TryRegister(oldToggle, oldStop);
            throw new InvalidOperationException("全局热键已被占用，请更换。" + (restored ? "已保留原热键。" : "当前热键不可用，请修改并应用。"));
        }
    }
    bool TryRegister(string a, string b)
    {
        if (!Native.RegisterHotKey(source.Handle, 1, 0x4000, KeyboardLayout.Keys[a].VirtualKey)) return false;
        if (!Native.RegisterHotKey(source.Handle, 2, 0x4000, KeyboardLayout.Keys[b].VirtualKey)) { Native.UnregisterHotKey(source.Handle, 1); return false; }
        toggle = a; stop = b; registered = true; return true;
    }
    nint Hook(nint hwnd, int msg, nint wparam, nint lparam, ref bool handled)
    {
        if (msg == 0x312) { Pressed?.Invoke(wparam == 2); handled = true; }
        return 0;
    }
    void Clear() { Native.UnregisterHotKey(source.Handle, 1); Native.UnregisterHotKey(source.Handle, 2); registered = false; }
    public void Dispose() { Clear(); source.RemoveHook(Hook); }
}
