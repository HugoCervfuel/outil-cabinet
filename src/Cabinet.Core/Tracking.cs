namespace Cabinet.Core;

/// <summary>Observe le poste : fenêtre au premier plan et durée d'inactivité. Implémentée pour Windows et macOS.</summary>
public interface IActivityProbe
{
    /// <summary>La fenêtre au premier plan, ou null si elle ne peut pas être lue.</summary>
    ActiveWindow? ReadActiveWindow();

    /// <summary>Temps écoulé depuis la dernière action au clavier ou à la souris, ou null si inconnu.</summary>
    TimeSpan? IdleTime();
}

/// <summary>Assemble les relevés successifs en périodes continues par fenêtre.</summary>
public sealed class SegmentBuilder(TimeSpan maxGap)
{
    private readonly List<Segment> _segments = new();
    private bool _open;

    public IReadOnlyList<Segment> All => _segments;

    public Segment? Current => _open ? _segments[^1] : null;

    public void Add(ActivitySample sample)
    {
        if (_open)
        {
            var current = _segments[^1];
            var gap = sample.At - current.End;
            if (gap >= TimeSpan.Zero && gap <= maxGap)
            {
                // Le temps écoulé depuis le dernier relevé revient à la fenêtre qui était au premier plan.
                current = current with { End = sample.At };
                _segments[^1] = current;
                if (current.App == sample.App && current.Title == sample.Title)
                {
                    return;
                }
            }
        }

        _segments.Add(new Segment(sample.At, sample.At, sample.App, sample.Title, null));
        _open = true;
    }

    public void Close() => _open = false;

    /// <summary>Ferme la période en cours au plus tard à <paramref name="at"/> (retire une absence constatée après coup).</summary>
    public void CloseAt(DateTimeOffset at)
    {
        if (_open)
        {
            var current = _segments[^1];
            var end = at < current.Start ? current.Start : at < current.End ? at : current.End;
            _segments[^1] = current with { End = end };
        }

        _open = false;
    }

    public void AssignCurrent(Guid? dossierId)
    {
        if (_open)
        {
            _segments[^1] = _segments[^1] with { AssignedDossierId = dossierId };
        }
    }

    public void Reset(IEnumerable<Segment> segments)
    {
        _segments.Clear();
        _segments.AddRange(segments);
        _open = false;
    }
}

public sealed record TrackerOptions
{
    public TimeSpan Interval { get; init; } = TimeSpan.FromSeconds(2);

    public TimeSpan MaxGap { get; init; } = TimeSpan.FromSeconds(10);

    public TimeSpan IdleThreshold { get; init; } = TimeSpan.FromMinutes(5);

    public IReadOnlyList<string> Exclusions { get; init; } = Array.Empty<string>();
}

/// <summary>Le suivi d'activité : un relevé à chaque appel de <see cref="Tick"/>.</summary>
public sealed class Tracker(IActivityProbe probe, Func<DateTimeOffset> clock, TrackerOptions options)
{
    private readonly SegmentBuilder _builder = new(options.MaxGap);
    private bool _paused;

    public TrackerOptions Options { get; set; } = options;

    public bool Paused
    {
        get => _paused;
        set
        {
            _paused = value;
            if (value)
            {
                _builder.Close();
            }
        }
    }

    public IReadOnlyList<Segment> Segments => _builder.All;

    public Segment? Current => _builder.Current;

    public void Tick()
    {
        if (_paused)
        {
            _builder.Close();
            return;
        }

        ActiveWindow? window;
        TimeSpan? idle;
        try
        {
            idle = probe.IdleTime();
            window = probe.ReadActiveWindow();
        }
        catch (Exception)
        {
            _builder.Close();
            return;
        }

        var now = clock();
        if (idle is { } inactive && inactive >= Options.IdleThreshold)
        {
            _builder.CloseAt(now - inactive);
            return;
        }

        if (window is null || ExclusionFilter.IsExcluded(window.App, window.Title, Options.Exclusions))
        {
            _builder.Close();
            return;
        }

        _builder.Add(new ActivitySample(now, window.App, window.Title));
    }

    public void AssignCurrent(Guid? dossierId) => _builder.AssignCurrent(dossierId);

    /// <summary>Reprend les périodes déjà enregistrées (redémarrage) ou repart de zéro (nouvelle journée).</summary>
    public void Reset(IEnumerable<Segment> segments) => _builder.Reset(segments);
}
