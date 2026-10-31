namespace GlassDesk.Core;

/// <summary>One visible transport row and its matching three-lens opacity.</summary>
public readonly record struct TransportLensHandoffFrame(
    WidgetForm Owner,
    float PeekOpacity,
    float DetailsOpacity,
    bool Active,
    bool Changed)
{
    const float VisibleOpacityThreshold = .001f;

    public bool HasVisibleRow(WidgetForm form) => form switch
    {
        WidgetForm.Peek => PeekOpacity > VisibleOpacityThreshold,
        WidgetForm.Details => DetailsOpacity > VisibleOpacityThreshold,
        _ => false
    };

    public static bool CanExposeTransportControls(
        WidgetForm form,
        bool systemGlassCandidate,
        bool ownedGlassEnabled,
        bool submissionPending,
        TransportLensHandoffFrame? submittedFrame)
    {
        if (!systemGlassCandidate || !ownedGlassEnabled) return true;
        return !submissionPending && submittedFrame is { } frame && frame.HasVisibleRow(form);
    }
}

/// <summary>
/// Interruptible, managed-only handoff between the Peek and Details transport rows.
/// It fades one complete row out before changing the lens owner, then fades the new row in.
/// </summary>
public sealed class TransportLensHandoff
{
    public const double FadeDurationMilliseconds = 90;
    const float OpacityEpsilon = .001f;
    const float SettleEpsilon = .0005f;

    bool _initialized;
    bool _hasTime;
    double _lastTime;
    float _ownerFactor = 1;

    public WidgetForm Owner { get; private set; } = WidgetForm.Peek;
    public float OwnerFactor => _ownerFactor;
    public bool Active => _ownerFactor < 1 - SettleEpsilon;

    /// <summary>
    /// Advances the handoff. Parent opacity is multiplied into the active row, and a row
    /// contributes nothing until all three scaled lens bounds fit inside the presented card.
    /// </summary>
    public TransportLensHandoffFrame Tick(
        double now,
        float peekParentOpacity,
        float detailsParentOpacity,
        bool peekFits,
        bool detailsFits,
        bool reducedMotion = false)
    {
        float peek = SanitizeOpacity(peekParentOpacity);
        float details = SanitizeOpacity(detailsParentOpacity);
        WidgetForm previousOwner = Owner;
        float previousFactor = _ownerFactor;

        double elapsed = 0;
        if (double.IsFinite(now))
        {
            if (!_hasTime)
            {
                _hasTime = true;
                _lastTime = now;
            }
            else if (now > _lastTime)
            {
                elapsed = now - _lastTime;
                if (!double.IsFinite(elapsed)) elapsed = FadeDurationMilliseconds;
                _lastTime = now;
            }
        }
        else
        {
            _hasTime = false;
        }

        WidgetForm desired = DesiredOwner(peek, details, Owner, peekFits, detailsFits);
        bool desiredFits = Fits(desired, peekFits, detailsFits);
        if (!_initialized)
        {
            _initialized = true;
            Owner = desiredFits || ParentOpacity(desired, peek, details) <= OpacityEpsilon
                ? desired
                : Other(desired);
            _ownerFactor = 1;
        }

        desired = DesiredOwner(peek, details, Owner, peekFits, detailsFits);
        desiredFits = Fits(desired, peekFits, detailsFits);
        float desiredParentOpacity = ParentOpacity(desired, peek, details);
        bool wantsSwitch = desired != Owner
            && desiredFits
            && desiredParentOpacity > OpacityEpsilon;

        if (reducedMotion || !double.IsFinite(now))
        {
            if (wantsSwitch) Owner = desired;
            _ownerFactor = 1;
        }
        else if (wantsSwitch)
        {
            bool currentRowHasNoVisibleOwner = ParentOpacity(Owner, peek, details) <= OpacityEpsilon
                || !Fits(Owner, peekFits, detailsFits);
            if (currentRowHasNoVisibleOwner)
            {
                // Nothing is visible to fade. Switch with the new row at zero opacity.
                Owner = desired;
                _ownerFactor = 0;
            }
            else
            {
                _ownerFactor = MoveToward(_ownerFactor, 0, elapsed / FadeDurationMilliseconds);
                if (_ownerFactor <= SettleEpsilon)
                {
                    Owner = desired;
                    _ownerFactor = 0;
                }
            }
        }
        else
        {
            _ownerFactor = MoveToward(_ownerFactor, 1, elapsed / FadeDurationMilliseconds);
        }

        if (_ownerFactor >= 1 - SettleEpsilon) _ownerFactor = 1;

        float peekEffective = Owner == WidgetForm.Peek && peekFits
            ? SanitizeOpacity(peek * _ownerFactor) : 0;
        float detailsEffective = Owner == WidgetForm.Details && detailsFits
            ? SanitizeOpacity(details * _ownerFactor) : 0;
        bool active = Active;
        bool changed = previousOwner != Owner || Math.Abs(previousFactor - _ownerFactor) > .000001f;
        return new(Owner, peekEffective, detailsEffective, active, changed);
    }

    /// <summary>Forgets the previous transition when experimental glass is disabled.</summary>
    public void Reset()
    {
        _initialized = false;
        _hasTime = false;
        _lastTime = 0;
        _ownerFactor = 1;
        Owner = WidgetForm.Peek;
    }

    static WidgetForm DesiredOwner(float peek, float details, WidgetForm tieOwner, bool peekFits, bool detailsFits)
    {
        if (details > peek) return WidgetForm.Details;
        if (peek > details) return WidgetForm.Peek;
        if (!Fits(tieOwner, peekFits, detailsFits)
            && Fits(Other(tieOwner), peekFits, detailsFits)
            && ParentOpacity(Other(tieOwner), peek, details) > OpacityEpsilon)
            return Other(tieOwner);
        return tieOwner;
    }

    static float ParentOpacity(WidgetForm form, float peek, float details)
        => form == WidgetForm.Details ? details : peek;

    static bool Fits(WidgetForm form, bool peekFits, bool detailsFits)
        => form == WidgetForm.Details ? detailsFits : peekFits;

    static WidgetForm Other(WidgetForm form)
        => form == WidgetForm.Details ? WidgetForm.Peek : WidgetForm.Details;

    static float SanitizeOpacity(float value)
        => float.IsFinite(value) ? Math.Clamp(value, 0, 1) : 0;

    static float MoveToward(float value, float target, double step)
    {
        if (!float.IsFinite(value)) value = target;
        value = Math.Clamp(value, 0, 1);
        target = Math.Clamp(target, 0, 1);
        if (double.IsPositiveInfinity(step)) return target;
        if (!double.IsFinite(step) || step <= 0) return value;
        if (step >= 1) return target;

        // `step` is elapsed / duration. Apply that fraction of the full opacity
        // range, rather than that fraction of the remaining distance, so a 90 ms
        // fade settles in roughly 90 ms at any regular timer cadence.
        float amount = (float)step;
        return target >= value
            ? Math.Min(target, value + amount)
            : Math.Max(target, value - amount);
    }
}

/// <summary>Validated square-lens containment using its actual animated scale.</summary>
public static class TransportLensGeometry
{
    public const float MaximumNativeLensDiameter = 64;

    public static bool IsFullyContained(
        float centerX,
        float centerY,
        float baseDiameter,
        float scale,
        float viewportWidth,
        float viewportHeight)
    {
        if (!float.IsFinite(centerX) || !float.IsFinite(centerY)
            || !float.IsFinite(baseDiameter) || !float.IsFinite(scale)
            || !float.IsFinite(viewportWidth) || !float.IsFinite(viewportHeight)
            || baseDiameter <= 0 || scale <= 0 || viewportWidth <= 0 || viewportHeight <= 0)
            return false;

        float diameter = baseDiameter * scale;
        if (!float.IsFinite(diameter) || diameter <= 0 || diameter > MaximumNativeLensDiameter)
            return false;
        float radius = diameter / 2;
        return centerX - radius >= 0 && centerY - radius >= 0
            && centerX + radius <= viewportWidth && centerY + radius <= viewportHeight;
    }
}
