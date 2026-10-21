using System.IO;
using GuitarNTE.Core;

namespace GuitarNTE.App;

internal sealed record MidiImportProgress(int Completed, int Total, string FileName);
internal sealed record MidiImportFailure(string Path, string Reason);
internal sealed record MidiFolderImportResult(List<SongProject> Songs, List<MidiImportFailure> Failures, int FileCount, int DuplicateCount = 0);

internal static class MidiLibraryImport
{
    internal static SongProject ReadSong(string path)
    {
        if (new FileInfo(path).Length > 32 * 1024 * 1024) throw new FormatException(UiText.T("MIDI 文件不能超过 32 MB。", "MIDI files cannot exceed 32 MB."));
        byte[] bytes = File.ReadAllBytes(path);
        var tracks = MidiImporter.Inspect(bytes);
        if (!tracks.Any(t => t.NoteCount > 0)) throw new FormatException(UiText.T("MIDI 没有音符。", "The MIDI file contains no notes."));
        string title = Path.GetFileNameWithoutExtension(path);
        if (string.IsNullOrWhiteSpace(title)) title = UiText.T("未命名 MIDI", "Untitled MIDI");
        return new SongProject
        {
            Title = title.Length > 100 ? title[..100] : title,
            Mode = Notation.Midi, MidiBase64 = Convert.ToBase64String(bytes),
            SelectedTracks = tracks.Where(t => t.HasMelody).Select(t => t.Index).ToList(), Text = ""
        };
    }

    // Read sources only; persist each independent library copy before reporting success.
    internal static MidiFolderImportResult ImportFolder(string folder, Action<SongProject> save, IProgress<MidiImportProgress>? progress = null, IEnumerable<SongProject>? existingSongs = null)
    {
        var result = new MidiFolderImportResult([], [], 0);
        var known = new HashSet<string>((existingSongs ?? []).Select(SongDuplicates.MidiKey).OfType<string>(), StringComparer.Ordinal);
        var files = new List<string>();
        var pending = new Stack<string>(); pending.Push(folder);
        while (pending.TryPop(out var directory))
        {
            try
            {
                files.AddRange(Directory.GetFiles(directory).Where(path =>
                    Path.GetExtension(path).Equals(".mid", StringComparison.OrdinalIgnoreCase) ||
                    Path.GetExtension(path).Equals(".midi", StringComparison.OrdinalIgnoreCase)));
                foreach (string child in Directory.GetDirectories(directory))
                {
                    // Do not follow junctions or symbolic links outside the selected tree or into cycles.
                    if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) == 0) pending.Push(child);
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                result.Failures.Add(new(Path.GetRelativePath(folder, directory), e.Message));
            }
        }
        files = files.OrderBy(path => path.Count(c => c == Path.DirectorySeparatorChar))
            .ThenBy(path => path, StringComparer.OrdinalIgnoreCase).ToList();
        result = result with { FileCount = files.Count };
        for (int i = 0; i < files.Count; i++)
        {
            string relativePath = Path.GetRelativePath(folder, files[i]);
            try
            {
                var song = ReadSong(files[i]); string key = SongDuplicates.MidiKey(song)!;
                if (known.Contains(key)) result = result with { DuplicateCount = result.DuplicateCount + 1 };
                else { save(song); result.Songs.Add(song); known.Add(key); }
            }
            catch (Exception e)
            {
                result.Failures.Add(new(relativePath, e.Message));
            }
            progress?.Report(new(i + 1, files.Count, relativePath));
        }
        return result;
    }
}
