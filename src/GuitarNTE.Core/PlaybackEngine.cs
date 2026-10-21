using System.Diagnostics;

namespace GuitarNTE.Core;

public interface IKeyboardOutput
{
    bool IsTargetForeground { get; }
    void KeyDown(IReadOnlyList<int> pitches);
    void KeyUp(IReadOnlyList<int> pitches);
    void ReleaseAll();
}
public interface IPlaybackClock { double Now { get; } }
public sealed class MonotonicClock : IPlaybackClock
{
    readonly Stopwatch watch = Stopwatch.StartNew();
    public double Now => watch.Elapsed.TotalSeconds;
}
public enum PlaybackState { Idle, Countdown, Playing, Paused, Completed, Faulted }
public record PlaybackStatus(PlaybackState State, double Position, double Duration, double Countdown, int Loop, int Loops, int[] ActivePitches, string Message);

public sealed class PlaybackEngine : IDisposable
{
    readonly object gate = new();
    readonly IKeyboardOutput output;
    readonly IPlaybackClock clock;
    readonly Thread? worker;
    readonly AutoResetEvent wake = new(false);
    readonly Dictionary<int, double> releases = [];
    volatile bool disposed;
    CompiledScore? score;
    PlaybackState state;
    int index, loop, loops;
    double position, anchor, deadline, countdown, pressSeconds, lastPublish;
    bool preview;
    bool sustain;
    string message = "准备就绪";
    public event Action<PlaybackStatus>? Changed;
    public PlaybackEngine(IKeyboardOutput output, IPlaybackClock? clock = null, bool runThread = true)
    {
        this.output = output; this.clock = clock ?? new MonotonicClock();
        if (runThread)
        {
            worker = new Thread(() =>
            {
                while (!disposed)
                {
                    Tick();
                    if (Status.State is PlaybackState.Playing or PlaybackState.Countdown) Thread.Sleep(2);
                    else wake.WaitOne();
                }
            }) { IsBackground = true, Name = "GuitarNTE.Playback" };
            worker.Start();
        }
    }
    public PlaybackStatus Status { get { lock (gate) return Snapshot(); } }
    PlaybackStatus Snapshot() => new(state, position, score?.Duration ?? 0, Math.Max(0, deadline - clock.Now), loop, loops, releases.Keys.ToArray(), message);
    void Publish() { lastPublish = clock.Now; Changed?.Invoke(Snapshot()); }
    void Release()
    {
        try { if (!preview) output.ReleaseAll(); }
        finally { releases.Clear(); }
    }
    void Fault(Exception e)
    {
        state = PlaybackState.Faulted; message = "演奏停止：" + e.Message;
        try { Release(); } catch (Exception releaseError) { message += "；释放按键失败：" + releaseError.Message; }
        Publish();
    }
    public void Start(CompiledScore song, double countdownSeconds, int pressMilliseconds, int repeat, bool visualPreview, bool sustainNotes = false)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (song.Notes.Count == 0 || song.Duration <= 0) throw new FormatException("曲谱没有可演奏音符。");
            if (!double.IsFinite(countdownSeconds) || countdownSeconds is < 0 or > 30 || pressMilliseconds is < 10 or > 500 || repeat is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(countdownSeconds));
            Release(); score = song; countdown = countdownSeconds; pressSeconds = pressMilliseconds / 1000d; loops = repeat; preview = visualPreview; sustain = sustainNotes;
            index = 0; loop = 1; position = 0; state = PlaybackState.Countdown; deadline = clock.Now + countdown;
            message = preview ? "可视预演倒计时（不发送按键）" : "倒计时：请切回选定游戏窗口"; wake.Set(); Publish();
        }
    }
    public void Pause(string reason = "已暂停；F8 继续")
    {
        lock (gate)
        {
            if (state is not (PlaybackState.Playing or PlaybackState.Countdown)) return;
            if (state == PlaybackState.Playing) position = Math.Min(score!.Duration, Math.Max(0, clock.Now - anchor));
            // Never resume after the next unplayed note, even after a delayed scheduler tick.
            if (score != null && index < score.Notes.Count) position = Math.Min(position, score.Notes[index].Start);
            state = PlaybackState.Paused; message = reason;
            try { Release(); Publish(); } catch (Exception e) { Fault(e); }
        }
    }
    public void Resume()
    {
        lock (gate)
        {
            if (state != PlaybackState.Paused) return;
            state = PlaybackState.Countdown; deadline = clock.Now + countdown; message = "恢复倒计时：请切回游戏"; wake.Set(); Publish();
        }
    }
    public void Stop()
    {
        lock (gate)
        {
            state = PlaybackState.Idle; index = 0; position = 0; loop = 1; deadline = 0; message = "已停止，回到曲首";
            try { Release(); Publish(); } catch (Exception e) { Fault(e); }
        }
    }
    public void Tick()
    {
        lock (gate)
        {
            if (disposed) return;
            try
            {
                double now = clock.Now;
                if (state == PlaybackState.Countdown && now >= deadline)
                {
                    if (!preview && !output.IsTargetForeground) { Pause("所选游戏未在前台，已暂停；切回后按继续热键"); return; }
                    anchor = now - position; state = PlaybackState.Playing; message = preview ? "正在可视预演（不发送按键）" : "正在演奏";
                }
                if (state == PlaybackState.Playing)
                {
                    if (!preview && !output.IsTargetForeground) { Pause("游戏失去焦点，已暂停并释放按键"); return; }
                    position = Math.Max(0, now - anchor);
                    int[] dueReleases = releases.Where(x => x.Value <= now).Select(x => x.Key).ToArray();
                    if (dueReleases.Length > 0)
                    {
                        if (!preview) output.KeyUp(dueReleases);
                        foreach (int p in dueReleases) releases.Remove(p);
                    }
                    if (index < score!.Notes.Count && score.Notes[index].Start <= position)
                    {
                        if (position - score.Notes[index].Start > 0.25) { Pause("调度延迟超过 250 毫秒，已暂停，避免集中补发音符"); return; }
                        double time = score.Notes[index].Start;
                        var events = new List<NoteEvent>();
                        while (index < score.Notes.Count && Math.Abs(score.Notes[index].Start - time) < 0.000001) { events.Add(score.Notes[index]); index++; }
                        var group = events.Select(n => n.Pitch).Distinct().ToArray();
                        int[] repeated = group.Where(releases.ContainsKey).ToArray();
                        if (repeated.Length > 0 && !preview) output.KeyUp(repeated);
                        if (!preview) output.KeyDown(group);
                        foreach (int pitch in group)
                        {
                            double hold = sustain ? events.Where(n => n.Pitch == pitch).Max(n => n.Duration) : pressSeconds;
                            // A repeated note must have a key-up edge before its next onset.
                            for (int j = index; j < score.Notes.Count && score.Notes[j].Start < time + hold + 0.004; j++)
                                if (score.Notes[j].Pitch == pitch) { hold = Math.Max(0.001, score.Notes[j].Start - time - 0.004); break; }
                            releases[pitch] = sustain ? Math.Max(now + 0.001, anchor + time + hold) : now + hold;
                        }
                        Publish();
                    }
                    if (position >= score.Duration && index >= score.Notes.Count && releases.Count == 0)
                    {
                        Release();
                        if (loop < loops) { loop++; index = 0; position = 0; anchor = now; }
                        else { position = score.Duration; state = PlaybackState.Completed; message = "演奏完成"; Publish(); }
                    }
                }
                if (state is PlaybackState.Playing or PlaybackState.Countdown && now - lastPublish >= 0.033) Publish();
            }
            catch (Exception e) { Fault(e); }
        }
    }
    public void Dispose()
    {
        lock (gate) { if (disposed) return; disposed = true; wake.Set(); try { Release(); } catch { /* UI already receives failures while running; disposal is best effort. */ } }
        worker?.Join(1000);
        if (worker == null || !worker.IsAlive) wake.Dispose();
    }
}
