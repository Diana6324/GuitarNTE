using Melanchall.DryWetMidi.Core;
using Melanchall.DryWetMidi.Interaction;

namespace GuitarNTE.Core;

public record MidiTrackInfo(int Index, string Name, int NoteCount, bool HasMelody);
public static class MidiImporter
{
    static MidiFile Read(byte[] bytes)
    {
        if (bytes.Length == 0) throw new FormatException(UiText.T("MIDI 文件为空。", "The MIDI file is empty."));
        if (bytes.Length > 32 * 1024 * 1024) throw new FormatException(UiText.T("MIDI 文件不能超过 32 MB。", "MIDI files cannot exceed 32 MB."));
        try { return MidiFile.Read(new MemoryStream(bytes), new ReadingSettings { TextEncoding = System.Text.Encoding.UTF8, EndOfTrackStoringPolicy = EndOfTrackStoringPolicy.Store, MissedEndOfTrackPolicy = MissedEndOfTrackPolicy.Abort }); }
        catch (Exception e) { throw new FormatException(UiText.T("无法读取 MIDI 文件：", "Cannot read MIDI file: ") + e.Message, e); }
    }
    public static List<MidiTrackInfo> Inspect(byte[] bytes) => Read(bytes).GetTrackChunks().Select((t, i) =>
    {
        var notes = t.GetNotes().ToList();
        return new MidiTrackInfo(i, t.Events.OfType<SequenceTrackNameEvent>().FirstOrDefault()?.Text ?? UiText.T($"音轨 {i + 1}", $"Track {i + 1}"), notes.Count, notes.Any(n => (int)n.Channel != 9));
    }).ToList();
    public static ScoreData Import(byte[] bytes, IReadOnlyCollection<int> selected, bool includePercussion = false)
    {
        var midi = Read(bytes);
        if (selected.Count == 0) throw new FormatException(UiText.T("请至少选择一个含音符的音轨。", "Select at least one track containing notes."));
        var tracks = midi.GetTrackChunks().ToList();
        if (selected.Any(i => i < 0 || i >= tracks.Count)) throw new FormatException(UiText.T("音轨选择已失效，请重新导入。", "The track selection is no longer valid. Import the file again."));
        var tempo = midi.GetTempoMap();
        var notes = new List<NoteEvent>();
        foreach (int index in selected.Distinct())
        foreach (var note in tracks[index].GetNotes())
        {
            if (!includePercussion && (int)note.Channel == 9) continue;
            double start = TimeConverter.ConvertTo<MetricTimeSpan>(note.Time, tempo).TotalMicroseconds / 1000000d;
            double end = TimeConverter.ConvertTo<MetricTimeSpan>(note.Time + note.Length, tempo).TotalMicroseconds / 1000000d;
            notes.Add(new((int)note.NoteNumber, start, Math.Max(0.001, end - start)));
            if (notes.Count > 200000) throw new FormatException(UiText.T("MIDI 音符超过 20 万，请减少所选音轨。", "More than 200,000 MIDI notes. Select fewer tracks."));
        }
        if (notes.Count == 0) throw new FormatException(UiText.T("所选音轨没有可演奏音符（默认排除打击乐通道）。", "Selected tracks contain no playable notes (percussion is excluded by default)."));
        double duration = notes.Max(n => n.Start + n.Duration);
        // Track end includes trailing rests on selected tracks; tempo events are global.
        foreach (int index in selected)
        {
            long endTicks = tracks[index].Events.Sum(e => e.DeltaTime);
            duration = Math.Max(duration, TimeConverter.ConvertTo<MetricTimeSpan>(endTicks, tempo).TotalMicroseconds / 1000000d);
        }
        if (duration > 86400) throw new FormatException(UiText.T("MIDI 最多支持 24 小时。", "MIDI duration cannot exceed 24 hours."));
        return new(notes.OrderBy(n => n.Start).ToList(), duration);
    }
}
