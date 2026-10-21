using Melanchall.DryWetMidi.Core;
using Melanchall.DryWetMidi.Interaction;

namespace GuitarNTE.Core;

public record MidiTrackInfo(int Index, string Name, int NoteCount, bool HasMelody);
public static class MidiImporter
{
    static MidiFile Read(byte[] bytes)
    {
        if (bytes.Length == 0) throw new FormatException("MIDI 文件为空。");
        if (bytes.Length > 32 * 1024 * 1024) throw new FormatException("MIDI 文件不能超过 32 MB。");
        try { return MidiFile.Read(new MemoryStream(bytes), new ReadingSettings { TextEncoding = System.Text.Encoding.UTF8, EndOfTrackStoringPolicy = EndOfTrackStoringPolicy.Store, MissedEndOfTrackPolicy = MissedEndOfTrackPolicy.Abort }); }
        catch (Exception e) { throw new FormatException("无法读取 MIDI 文件：" + e.Message, e); }
    }
    public static List<MidiTrackInfo> Inspect(byte[] bytes) => Read(bytes).GetTrackChunks().Select((t, i) =>
    {
        var notes = t.GetNotes().ToList();
        return new MidiTrackInfo(i, t.Events.OfType<SequenceTrackNameEvent>().FirstOrDefault()?.Text ?? $"音轨 {i + 1}", notes.Count, notes.Any(n => (int)n.Channel != 9));
    }).ToList();
    public static ScoreData Import(byte[] bytes, IReadOnlyCollection<int> selected, bool includePercussion = false)
    {
        var midi = Read(bytes);
        if (selected.Count == 0) throw new FormatException("请至少选择一个含音符的音轨。");
        var tracks = midi.GetTrackChunks().ToList();
        if (selected.Any(i => i < 0 || i >= tracks.Count)) throw new FormatException("音轨选择已失效，请重新导入。");
        var tempo = midi.GetTempoMap();
        var notes = new List<NoteEvent>();
        foreach (int index in selected.Distinct())
        foreach (var note in tracks[index].GetNotes())
        {
            if (!includePercussion && (int)note.Channel == 9) continue;
            double start = TimeConverter.ConvertTo<MetricTimeSpan>(note.Time, tempo).TotalMicroseconds / 1000000d;
            double end = TimeConverter.ConvertTo<MetricTimeSpan>(note.Time + note.Length, tempo).TotalMicroseconds / 1000000d;
            notes.Add(new((int)note.NoteNumber, start, Math.Max(0.001, end - start)));
            if (notes.Count > 200000) throw new FormatException("MIDI 音符超过 20 万，请减少所选音轨。");
        }
        if (notes.Count == 0) throw new FormatException("所选音轨没有可演奏音符（默认排除打击乐通道）。");
        double duration = notes.Max(n => n.Start + n.Duration);
        // Track end includes trailing rests on selected tracks; tempo events are global.
        foreach (int index in selected)
        {
            long endTicks = tracks[index].Events.Sum(e => e.DeltaTime);
            duration = Math.Max(duration, TimeConverter.ConvertTo<MetricTimeSpan>(endTicks, tempo).TotalMicroseconds / 1000000d);
        }
        if (duration > 86400) throw new FormatException("MIDI 最多支持 24 小时。");
        return new(notes.OrderBy(n => n.Start).ToList(), duration);
    }
}
