using System.Globalization;
using System.Security.Cryptography;
using GuitarNTE.Core;

namespace GuitarNTE.App;

internal static class SongDuplicates
{
    internal static string? MidiKey(SongProject song) => song.MidiBase64 == null || song.Mode != Notation.Midi
        ? null : Convert.ToHexString(SHA256.HashData(Convert.FromBase64String(song.MidiBase64)));

    // Playback variants and annotations are intentional versions, even when their source MIDI is identical.
    internal static string? CleanupKey(SongProject song)
    {
        string? midi = MidiKey(song);
        return midi == null ? null : string.Join("|", midi, song.Speed.ToString("R", CultureInfo.InvariantCulture),
            song.Transpose, string.Join(",", song.SelectedTracks.Distinct().Order()), song.Text);
    }
}
