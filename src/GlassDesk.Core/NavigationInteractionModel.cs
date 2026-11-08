namespace GlassDesk.Core;

/// <summary>Deadline based navigation interaction. All times are monotonic milliseconds.</summary>
public sealed class NavigationInteractionModel
{
    public const double HoverDelay = 100, ExpandDuration = 220, ItemDuration = 160,
        EmphasisDuration = 180, CloseDelay = 300;
    double? _hoverAt, _closeAt;
    double _outerStarted, _outerFrom, _outerTarget, _itemStarted, _emphasisStarted;
    readonly double[] _items = new double[5], _itemFrom = new double[5], _itemTargets = new double[5];
    double _emphasisFrom, _emphasisTarget;
    double _outerVelocity, _outerInitialVelocity, _centerFrom, _centerTarget, _centerStarted, _centerVelocity, _centerInitialVelocity;
    bool _inside, _reducedMotion, _keepExpanded;
    public NavigationState State { get; private set; } = new(0, NavigationOuterState.Collapsed, null, "home", false);
    public bool IsActive { get; private set; }
    public double Expansion { get; private set; }
    public double EmphasisIndex { get; private set; }
    public double CenterIndex { get; private set; }
    public IReadOnlyList<double> ItemEmphasis => _items;
    public bool NeedsTick
    {
        get
        {
            if (!IsActive) return false;
            if (_hoverAt.HasValue || _closeAt.HasValue || Math.Abs(Expansion - _outerTarget) > .00001 ||
                Math.Abs(EmphasisIndex - _emphasisTarget) > .00001 || Math.Abs(CenterIndex - _centerTarget) > .00001) return true;
            for (int index = 0; index < 5; index++) if (Math.Abs(_items[index] - _itemTargets[index]) > .00001) return true;
            return false;
        }
    }
    public event Action<long>? IntroConsumed;

    public void BeginOpen(long sequence, double now, bool reducedMotion, bool companionEnabled, bool keepExpanded = false)
    {
        IsActive = true; _inside = false; _reducedMotion = reducedMotion; _keepExpanded = keepExpanded;
        _hoverAt = _closeAt = null; Expansion = _outerFrom = _outerTarget = 0;
        EmphasisIndex = _emphasisFrom = _emphasisTarget = 0;
        CenterIndex = _centerFrom = _centerTarget = _centerVelocity = _outerVelocity = _centerInitialVelocity = _outerInitialVelocity = 0;
        _centerStarted = now;
        Array.Clear(_items); Array.Clear(_itemFrom); Array.Clear(_itemTargets);
        _outerStarted = _itemStarted = _emphasisStarted = now;
        State = new(sequence, NavigationOuterState.Collapsed, null, "home", !companionEnabled);
        if (_keepExpanded) OpenImmediately(now);
    }

    public void Close()
    {
        IsActive = false; _inside = false; _hoverAt = _closeAt = null;
        Expansion = _outerFrom = _outerTarget = 0;
        State = State with { OuterState = NavigationOuterState.Collapsed, HoveredItemId = null, CaptureOwner = PointerCaptureOwner.None };
    }

    public void SetReducedMotion(bool reducedMotion, double now)
    {
        Tick(now); _reducedMotion = reducedMotion;
        if (reducedMotion) Tick(now);
    }

    public void SetPointerInside(bool inside, double now)
    {
        if (!IsActive) return;
        Tick(now);
        if (_inside == inside) return;
        _inside = inside;
        if (inside)
        {
            _closeAt = null;
            if (State.OuterState == NavigationOuterState.ClosePending)
                State = State with { OuterState = Expansion >= 1 ? NavigationOuterState.Open : NavigationOuterState.Opening };
            else if (State.OuterState == NavigationOuterState.Closing) StartOuter(1, now);
            else if (State.OuterState == NavigationOuterState.Collapsed)
            {
                _hoverAt = now + HoverDelay;
                State = State with { OuterState = NavigationOuterState.HoverPending };
            }
        }
        else
        {
            _hoverAt = null; SetHoveredItem(null, now);
            if (_keepExpanded) { _closeAt = null; return; }
            if (Expansion > 0 || _outerTarget > 0)
            {
                _closeAt = now + CloseDelay;
                State = State with { OuterState = NavigationOuterState.ClosePending };
            }
            else State = State with { OuterState = NavigationOuterState.Collapsed };
        }
    }

    public void OpenImmediately(double now)
    {
        if (!IsActive) return;
        Tick(now); _hoverAt = _closeAt = null; StartOuter(1, now); ConsumeIntro(); Tick(now);
    }

    public void SetHoveredItem(string? id, double now)
    {
        if (!IsActive) return;
        id = PreviewNavigationItems.All.Any(item => item.Id == id) ? id : null;
        if (State.HoveredItemId == id) return;
        SampleItems(now);
        State = State with { HoveredItemId = id };
        _itemStarted = now; Array.Copy(_items, _itemFrom, 5);
        int active = Index(id ?? State.SelectedPreviewItemId);
        for (int index = 0; index < 5; index++)
            _itemTargets[index] = id is null ? 0 : Math.Max(0, 1 - Math.Abs(index - active) * .36);
        StartEmphasis(active, now);
        SampleItems(now);
    }

    public bool SelectPreview(string id, double now)
    {
        if (!IsActive || !PreviewNavigationItems.All.Any(item => item.Id == id)) return false;
        State = State with { SelectedPreviewItemId = id };
        SampleCenter(now); _centerFrom = CenterIndex; _centerInitialVelocity = _centerVelocity;
        _centerTarget = Index(id); _centerTarget += Math.Round((CenterIndex - _centerTarget) / 5) * 5;
        _centerStarted = now; SampleCenter(now);
        if (State.HoveredItemId is null) StartEmphasis(Index(id), now);
        return true;
    }

    public void SetCaptureOwner(PointerCaptureOwner owner) => State = State with { CaptureOwner = owner };

    public void Tick(double now)
    {
        if (!IsActive) return;
        if (_hoverAt is { } hover && now + 1e-9 >= hover)
        {
            _hoverAt = null;
            if (_inside) { StartOuter(1, hover); ConsumeIntro(); }
        }
        if (_closeAt is { } close && now + 1e-9 >= close)
        {
            _closeAt = null;
            if (!_inside) StartOuter(0, close);
        }
        (Expansion, _outerVelocity) = Spring(_outerFrom, _outerTarget, _outerInitialVelocity, now - _outerStarted, ExpandDuration);
        if (Math.Abs(Expansion - _outerTarget) < .00001)
        {
            Expansion = _outerTarget;
            if (State.OuterState == NavigationOuterState.Opening) State = State with { OuterState = NavigationOuterState.Open };
            if (State.OuterState == NavigationOuterState.Closing) State = State with { OuterState = NavigationOuterState.Collapsed };
        }
        SampleItems(now);
        SampleCenter(now);
    }

    void ConsumeIntro()
    {
        if (State.IntroConsumed) return;
        State = State with { IntroConsumed = true };
        IntroConsumed?.Invoke(State.OpenSequence);
    }

    void StartOuter(double target, double now)
    {
        _outerFrom = Expansion; _outerInitialVelocity = _outerVelocity; _outerTarget = target; _outerStarted = now;
        State = State with { OuterState = target > 0 ? NavigationOuterState.Opening : NavigationOuterState.Closing };
    }

    void StartEmphasis(int target, double now)
    {
        SampleItems(now); _emphasisFrom = EmphasisIndex; _emphasisTarget = target; _emphasisStarted = now;
    }

    void SampleItems(double now)
    {
        EmphasisIndex = Interpolate(_emphasisFrom, _emphasisTarget, now - _emphasisStarted, EmphasisDuration);
        for (int index = 0; index < 5; index++) _items[index] = Interpolate(_itemFrom[index], _itemTargets[index], now - _itemStarted, ItemDuration);
    }

    double Interpolate(double from, double target, double elapsed, double duration)
    {
        double t = _reducedMotion ? 1 : Math.Clamp(elapsed / duration, 0, 1);
        double eased = 1 - Math.Pow(1 - t, 3);
        return from + (target - from) * eased;
    }
    void SampleCenter(double now) => (CenterIndex, _centerVelocity) = Spring(_centerFrom, _centerTarget, _centerInitialVelocity, now - _centerStarted, 280);
    (double Value, double Velocity) Spring(double from, double target, double velocity, double elapsed, double duration)
    {
        if (_reducedMotion || elapsed >= duration) return (target, 0);
        double t = Math.Max(0, elapsed) / 1000, decay = 29, frequency = 30;
        double a = from - target, b = (velocity + decay * a) / frequency;
        double c = Math.Cos(frequency * t), s = Math.Sin(frequency * t), e = Math.Exp(-decay * t);
        return (target + e * (a * c + b * s), e * ((b * frequency - a * decay) * c - (a * frequency + b * decay) * s));
    }
    static int Index(string id)
    {
        for (int index = 0; index < 5; index++) if (PreviewNavigationItems.All[index].Id == id) return index;
        return 0;
    }
}
