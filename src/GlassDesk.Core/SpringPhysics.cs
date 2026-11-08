namespace GlassDesk.Core;

/// <summary>Dimensionless stiffness and damping for a finite, interruptible spring.</summary>
public readonly record struct SpringTuning(double Stiffness = 64, double DampingRatio = .76)
{
    public static SpringTuning Default => new(64, .76);
}

public static class SpringPhysics
{
    /// <summary>Analytic damped spring plus a C1 residual correction at the deadline.
    /// Both position and velocity survive retargeting; completion reaches exact rest.</summary>
    public static (double Position, double Velocity) Sample(double from, double to, double velocity,
        double seconds, double duration, SpringTuning tuning)
    {
        if (duration <= 0 || seconds >= duration) return (to, 0);
        seconds = Math.Max(0, seconds);
        double stiffness = double.IsFinite(tuning.Stiffness) ? Math.Clamp(tuning.Stiffness, 16, 144) : 64;
        double damping = double.IsFinite(tuning.DampingRatio) ? Math.Clamp(tuning.DampingRatio, .5, .99) : .76;
        double omega = Math.Sqrt(stiffness) / duration;
        double decay = damping * omega, frequency = omega * Math.Sqrt(1 - damping * damping);
        double offset = from - to, sineCoefficient = (velocity + decay * offset) / frequency;
        (double Position, double Velocity) Raw(double t)
        {
            double cosine = Math.Cos(frequency * t), sine = Math.Sin(frequency * t);
            double wave = offset * cosine + sineCoefficient * sine, envelope = Math.Exp(-decay * t);
            return (envelope * wave, envelope * (-decay * wave
                - offset * frequency * sine + sineCoefficient * frequency * cosine));
        }
        var value = Raw(seconds); var end = Raw(duration);
        double u = seconds / duration;
        return (to + value.Position - end.Position * u*u*(3-2*u) - duration * end.Velocity * u*u*(u-1),
            value.Velocity - end.Position * 6*u*(1-u)/duration - end.Velocity * u*(3*u-2));
    }
}

/// <summary>A scalar channel that keeps its velocity across target changes.</summary>
public sealed class SpringValue(double initial)
{
    double _from = initial, _to = initial, _velocity, _started, _duration;
    SpringTuning _tuning = SpringTuning.Default;
    public bool Active(double now) => now < _started + _duration && _duration > 0;
    public (double Position, double Velocity) Sample(double now)
        => now < _started ? (_from, 0) : SpringPhysics.Sample(_from, _to, _velocity,
            (now - _started) / 1000, _duration / 1000, _tuning);
    public void Jump(double value) { _from = _to = value; _velocity = _duration = 0; _started = 0; }
    public void Retarget(double value, double now, int duration, bool immediate = false, int delay = 0)
    {
        if (immediate) { Jump(value); return; }
        if (value == _to) return;
        var current = Sample(now);
        _from = current.Position; _velocity = current.Velocity; _to = value;
        // A visible moving element must never pause at a stagger boundary.
        _started = now + (Math.Abs(_velocity) < .0001 ? delay : 0); _duration = duration;
        _tuning = SpringTuning.Default;
    }
}
