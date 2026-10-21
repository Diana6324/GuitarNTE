using System.IO;
using System.Text.Json;
using GuitarNTE.Core;

namespace GuitarNTE.App;
internal sealed class LibraryStore
{
    internal string Root { get; } = Environment.GetEnvironmentVariable("GUITAR_NTE_DATA") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GuitarNTE");
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    internal List<string> Warnings { get; } = [];
    internal LibraryStore() { Directory.CreateDirectory(Path.Combine(Root, "Songs")); }
    internal AppSettings LoadSettings()
    {
        string path = Path.Combine(Root, "settings.json");
        if (!File.Exists(path)) return new();
        try
        {
            var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), Json) ?? throw new FormatException("设置为空");
            if (settings.Version != 1) throw new FormatException("设置版本不受支持");
            KeyboardLayout.Validate(settings); return settings;
        }
        catch (Exception e) { Warnings.Add("设置读取失败，已使用默认值；原文件已保留：" + e.Message); return new(); }
    }
    internal List<SongProject> LoadSongs()
    {
        var songs = new List<SongProject>();
        foreach (string path in Directory.EnumerateFiles(Path.Combine(Root, "Songs"), "*.json"))
        {
            try
            {
                var song = JsonSerializer.Deserialize<SongProject>(File.ReadAllText(path), Json) ?? throw new FormatException("曲谱为空");
                if (song.Version != 1 || !Guid.TryParseExact(song.Id, "N", out _) || Path.GetFileNameWithoutExtension(path) != song.Id) throw new FormatException("曲谱版本或编号无效");
                if (!Enum.IsDefined(song.Mode) || string.IsNullOrWhiteSpace(song.Title) || song.Title.Length > 100 || song.Text == null || song.SelectedTracks == null) throw new FormatException("曲谱内容无效");
                _ = Pitches.Parse(song.Tonic);
                if (!double.IsFinite(song.Bpm) || song.Bpm is < 20 or > 400 || !double.IsFinite(song.Speed) || song.Speed is < 0.25 or > 4 || song.Transpose is < -48 or > 48) throw new FormatException("曲谱参数无效");
                if (song.Mode == Notation.Midi && song.MidiBase64 == null) throw new FormatException("MIDI 数据缺失");
                if (song.MidiBase64 != null) _ = MidiImporter.Inspect(Convert.FromBase64String(song.MidiBase64));
                songs.Add(song);
            }
            catch (Exception e) { Warnings.Add($"无法加载 {Path.GetFileName(path)}，原文件已保留：{e.Message}"); }
        }
        return songs.OrderBy(x => x.Title).ToList();
    }
    static void AtomicWrite(string path, object value)
    {
        string temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(value, Json));
        File.Move(temp, path, true);
    }
    internal void SaveSettings(AppSettings settings) => AtomicWrite(Path.Combine(Root, "settings.json"), settings);
    string SongPath(string id)
    {
        if (!Guid.TryParseExact(id, "N", out _)) throw new FormatException("曲谱编号无效。");
        return Path.Combine(Root, "Songs", id + ".json");
    }
    internal void SaveSong(SongProject song) => AtomicWrite(SongPath(song.Id), song);
    internal int CleanDuplicateSongs(List<SongProject> songs, string? preferredId = null)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        string backup = Path.Combine(Root, "DuplicateBackups", DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..8]);
        int removed = 0;
        foreach (var song in songs.OrderByDescending(s => s.Id == preferredId)
            .ThenByDescending(s => File.GetLastWriteTimeUtc(SongPath(s.Id))).ToArray())
        {
            string? key = SongDuplicates.CleanupKey(song);
            if (key == null || seen.Add(key)) continue;
            try
            {
                Directory.CreateDirectory(backup);
                File.Move(SongPath(song.Id), Path.Combine(backup, song.Id + ".json"));
                songs.Remove(song); removed++;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            { Warnings.Add($"无法备份重复曲谱「{song.Title}」，已保留：{e.Message}"); }
        }
        return removed;
    }
    internal void DeleteSong(SongProject song) => File.Delete(SongPath(song.Id));
    internal static SongProject Copy(SongProject song) => JsonSerializer.Deserialize<SongProject>(JsonSerializer.Serialize(song, Json), Json)!;
}
