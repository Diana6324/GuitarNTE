using GuitarNTE.Core;
using System.Windows;
using System.IO;
using System.Security.Cryptography;
using System.Text;
namespace GuitarNTE.App;
public partial class App : Application
{
    Mutex? instance;
    internal bool SuppressStartupWindow { get; set; }
    internal static string? StartupSongId { get; private set; }
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (SuppressStartupWindow) return;
        string? Arg(string name)
        {
            int index = Array.IndexOf(e.Args, name);
            return index >= 0 && index + 1 < e.Args.Length ? e.Args[index + 1] : null;
        }
        if (Arg("--data-directory") is string explicitRoot) Environment.SetEnvironmentVariable("GUITAR_NTE_DATA", Path.GetFullPath(explicitRoot));
        StartupSongId = Arg("--select-song");
        if (int.TryParse(Arg("--wait-for-exit"), out int parentId) && parentId != Environment.ProcessId)
        {
            try { using var parent = System.Diagnostics.Process.GetProcessById(parentId); parent.WaitForExit(10000); }
            catch (ArgumentException) { } // Parent already exited after saving the draft.
        }
        string dataPath = Environment.GetEnvironmentVariable("GUITAR_NTE_DATA") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GuitarNTE");
        string dataId = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(dataPath).ToUpperInvariant())))[..20];
        instance = new Mutex(true, "Local\\GuitarNTE-" + dataId, out bool first);
        if (!first) { MessageBox.Show(UiText.T("演奏工具已在运行，请使用已有窗口。", "The app is already running. Use the existing window."), UiText.Title); Shutdown(); return; }
        try { MainWindow = new MainWindow(); MainWindow.Show(); }
        catch (Exception error)
        {
            MessageBox.Show(UiText.T("无法启动演奏工具：\n", "Cannot start the player:\n") + error.Message + UiText.T("\n请检查曲库目录访问权限。", "\nCheck the library folder permissions."), UiText.Title, MessageBoxButton.OK, MessageBoxImage.Error); Shutdown(1);
        }
    }
    protected override void OnExit(ExitEventArgs e) { instance?.Dispose(); base.OnExit(e); }
}
