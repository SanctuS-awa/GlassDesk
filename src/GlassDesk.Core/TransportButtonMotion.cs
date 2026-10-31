namespace GlassDesk.Core;

/// <summary>Small interruptible spring for a transport button's visual scale.</summary>
public sealed class TransportButtonMotion
{
    public const float RestScale = 1f;
    public const float HoverScale = 1.04f;
    public const float PressScale = .94f;

    const double AngularFrequency = 36;
    const double DampingRatio = .68;
    const double PositionEpsilon = .00025;
    const double VelocityEpsilon = .003;
    const double PointerReleaseSuppressionMs = 180;
    const double ClickImpulseCooldownMs = 90;
    const double ClickImpulseVelocity = 1.8;
    const double MaximumClickVelocity = 2.4;
    double _velocity;
    double _lastTime;
    double _lastPointerReleaseAt = double.NegativeInfinity;
    double _lastClickImpulseAt = double.NegativeInfinity;
    bool _hasTime;
    bool _pointerPressed;
    float _target = RestScale;

    public float Scale { get; private set; } = RestScale;
    public bool Active { get; private set; }

    /// <summary>Retargets from the currently presented scale and inherited velocity.</summary>
    public float Tick(double now, bool hovered, bool pressed, bool reducedMotion = false)
    {
        Advance(now);
        ObservePointerState(now, pressed);
        if (reducedMotion)
        {
            SnapToRest(now);
            return Scale;
        }

        float target = pressed ? PressScale : hovered ? HoverScale : RestScale;
        if (Math.Abs(target - _target) > .000001f) _target = target;
        UpdateActive();
        return Scale;
    }

    /// <summary>Injects a short downward impulse; the spring supplies the elastic return.</summary>
    public void TriggerClick(double now, bool reducedMotion = false)
    {
        if (!double.IsFinite(now)) return;
        Advance(now);
        if (reducedMotion)
        {
            SnapToRest(now);
            return;
        }

        if (_pointerPressed || now - _lastPointerReleaseAt is >= 0 and < PointerReleaseSuppressionMs
            || now - _lastClickImpulseAt is >= 0 and < ClickImpulseCooldownMs) return;
        _lastClickImpulseAt = now;
        _velocity = Math.Clamp(_velocity - ClickImpulseVelocity, -MaximumClickVelocity, MaximumClickVelocity);
        Active = true;
    }

    void ObservePointerState(double now, bool pressed)
    {
        if (!double.IsFinite(now)) return;
        if (pressed && !_pointerPressed) _pointerPressed = true;
        else if (!pressed && _pointerPressed)
        {
            _pointerPressed = false;
            _lastPointerReleaseAt = now;
        }
    }

    void Advance(double now)
    {
        if (!double.IsFinite(now)) return;
        if (!_hasTime)
        {
            _lastTime = now;
            _hasTime = true;
            return;
        }
        if (now <= _lastTime) return;

        double seconds = (now - _lastTime) / 1000;
        _lastTime = now;
        if (seconds >= 1)
        {
            Scale = _target;
            _velocity = 0;
            Active = false;
            return;
        }

        double decayRate = DampingRatio * AngularFrequency;
        double dampedFrequency = AngularFrequency * Math.Sqrt(1 - DampingRatio * DampingRatio);
        double displacement = Scale - _target;
        double sineCoefficient = (_velocity + decayRate * displacement) / dampedFrequency;
        double angle = dampedFrequency * seconds;
        double cosine = Math.Cos(angle), sine = Math.Sin(angle);
        double envelope = Math.Exp(-decayRate * seconds);
        double wave = displacement * cosine + sineCoefficient * sine;
        double derivative = -decayRate * wave
            - displacement * dampedFrequency * sine
            + sineCoefficient * dampedFrequency * cosine;
        Scale = (float)(_target + envelope * wave);
        _velocity = envelope * derivative;
        UpdateActive();
    }

    void UpdateActive()
    {
        if (Math.Abs(Scale - _target) <= PositionEpsilon && Math.Abs(_velocity) <= VelocityEpsilon)
        {
            Scale = _target;
            _velocity = 0;
            Active = false;
        }
        else Active = true;
    }

    void SnapToRest(double now)
    {
        _target = RestScale;
        Scale = RestScale;
        _velocity = 0;
        _lastTime = now;
        _hasTime = double.IsFinite(now);
        Active = false;
    }
}
