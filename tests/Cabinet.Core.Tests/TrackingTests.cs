namespace Cabinet.Core.Tests;

public class SegmentBuilderTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 20, 9, 0, 0, TimeSpan.FromHours(1));

    private static ActivitySample S(int seconds, string app, string title) => new(T0.AddSeconds(seconds), app, title);

    [Fact]
    public void SameWindow_ExtendsSegment()
    {
        var b = new SegmentBuilder(TimeSpan.FromSeconds(10));
        b.Add(S(0, "Word", "A"));
        b.Add(S(2, "Word", "A"));
        b.Add(S(4, "Word", "A"));
        var seg = Assert.Single(b.All);
        Assert.Equal(TimeSpan.FromSeconds(4), seg.Duration);
    }

    [Fact]
    public void WindowChange_GivesIntervalToPreviousWindow()
    {
        var b = new SegmentBuilder(TimeSpan.FromSeconds(10));
        b.Add(S(0, "Word", "A"));
        b.Add(S(2, "Outlook", "B"));
        b.Add(S(4, "Outlook", "B"));
        Assert.Equal(2, b.All.Count);
        Assert.Equal(TimeSpan.FromSeconds(2), b.All[0].Duration);
        Assert.Equal(TimeSpan.FromSeconds(2), b.All[1].Duration);
    }

    [Fact]
    public void Gap_StartsNewSegmentWithoutCountingTheGap()
    {
        var b = new SegmentBuilder(TimeSpan.FromSeconds(10));
        b.Add(S(0, "Word", "A"));
        b.Add(S(2, "Word", "A"));
        b.Add(S(60, "Word", "A"));
        b.Add(S(62, "Word", "A"));
        Assert.Equal(2, b.All.Count);
        Assert.Equal(TimeSpan.FromSeconds(4), b.All.Aggregate(TimeSpan.Zero, (t, s) => t + s.Duration));
    }

    [Fact]
    public void Close_StopsExtension()
    {
        var b = new SegmentBuilder(TimeSpan.FromSeconds(10));
        b.Add(S(0, "Word", "A"));
        b.Add(S(2, "Word", "A"));
        b.Close();
        b.Add(S(4, "Word", "A"));
        Assert.Equal(2, b.All.Count);
        Assert.Equal(TimeSpan.FromSeconds(2), b.All[0].Duration);
    }

    [Fact]
    public void AssignCurrent_SetsDossierOnCurrentSegment()
    {
        var b = new SegmentBuilder(TimeSpan.FromSeconds(10));
        var id = Guid.NewGuid();
        b.Add(S(0, "Word", "A"));
        b.AssignCurrent(id);
        Assert.Equal(id, b.All[0].AssignedDossierId);
    }
}

public class TrackerTests
{
    private sealed class FakeProbe : IActivityProbe
    {
        public (string App, string Title)? Window { get; set; } = ("Microsoft Word", "Conclusions MARTIN.docx");
        public TimeSpan? Idle { get; set; } = TimeSpan.Zero;
        public ActiveWindow? ReadActiveWindow() => Window is { } w ? new ActiveWindow(w.App, w.Title) : null;
        public TimeSpan? IdleTime() => Idle;
    }

    private static readonly DateTimeOffset Start = new(2026, 11, 20, 9, 0, 0, TimeSpan.FromHours(1));

    private static (Tracker Tracker, FakeProbe Probe, Func<DateTimeOffset> Advance) Create(TrackerOptions? options = null)
    {
        var now = Start;
        var probe = new FakeProbe();
        var tracker = new Tracker(probe, () => now, options ?? new TrackerOptions());
        return (tracker, probe, () => now = now.AddSeconds(2));
    }

    [Fact]
    public void Ticks_RecordActivity()
    {
        var (tracker, _, advance) = Create();
        for (var i = 0; i < 31; i++) { tracker.Tick(); advance(); }
        Assert.Equal(TimeSpan.FromMinutes(1), tracker.Segments.Aggregate(TimeSpan.Zero, (t, s) => t + s.Duration));
    }

    [Fact]
    public void Paused_RecordsNothing()
    {
        var (tracker, _, advance) = Create();
        tracker.Paused = true;
        for (var i = 0; i < 10; i++) { tracker.Tick(); advance(); }
        Assert.Empty(tracker.Segments);
    }

    [Fact]
    public void Idle_StopsRecordingAndRemovesTheInactivePeriod()
    {
        // 10 minutes d'activité, puis on constate une absence de 6 minutes : seules les 4 premières minutes comptent.
        var (tracker, probe, advance) = Create(new TrackerOptions { IdleThreshold = TimeSpan.FromMinutes(5) });
        for (var i = 0; i < 301; i++) { tracker.Tick(); advance(); }
        probe.Idle = TimeSpan.FromMinutes(6);
        for (var i = 0; i < 10; i++) { tracker.Tick(); advance(); }
        Assert.Equal(TimeSpan.FromMinutes(4) + TimeSpan.FromSeconds(2), tracker.Segments.Aggregate(TimeSpan.Zero, (t, s) => t + s.Duration));
    }

    [Fact]
    public void ExcludedWindow_IsNeverRecorded()
    {
        var (tracker, probe, advance) = Create(new TrackerOptions { Exclusions = new[] { "banque" } });
        probe.Window = ("Safari", "Ma banque");
        for (var i = 0; i < 5; i++) { tracker.Tick(); advance(); }
        Assert.Empty(tracker.Segments);
    }

    [Fact]
    public void ProbeFailure_RecordsNothing()
    {
        var (tracker, probe, advance) = Create();
        probe.Window = null;
        for (var i = 0; i < 5; i++) { tracker.Tick(); advance(); }
        Assert.Empty(tracker.Segments);
    }

    [Fact]
    public void Current_ReflectsActiveWindow()
    {
        var (tracker, _, advance) = Create();
        tracker.Tick(); advance(); tracker.Tick();
        Assert.Equal("Conclusions MARTIN.docx", tracker.Current?.Title);
    }
}
