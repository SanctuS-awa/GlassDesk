using System.Diagnostics;
using System.Numerics;
using GlassDesk.Core;
using Windows.UI.Composition;

namespace GlassDesk.App;

/// <summary>Samples the shared spring into compositor keyframes. Retargeting keeps
/// the evaluated position and velocity without per-frame UI-thread callbacks.</summary>
internal sealed class CompositionSpring(Compositor compositor) : IDisposable
{
    sealed record Track(Vector3 From, Vector3 To, Vector3 Velocity, double Started, double Duration, SpringTuning Tuning);
    readonly Dictionary<(Visual Visual, string Property), Track> _tracks = new();
    public SpringTuning Tuning { get; set; } = SpringTuning.Default;
    public bool Active => _tracks.Values.Any(track => Now < track.Started + track.Duration);
    static double Now => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;

    static (Vector3 Position, Vector3 Velocity) Sample(Track track, double seconds)
    {
        var x = SpringPhysics.Sample(track.From.X, track.To.X, track.Velocity.X, seconds, track.Duration, track.Tuning);
        var y = SpringPhysics.Sample(track.From.Y, track.To.Y, track.Velocity.Y, seconds, track.Duration, track.Tuning);
        var z = SpringPhysics.Sample(track.From.Z, track.To.Z, track.Velocity.Z, seconds, track.Duration, track.Tuning);
        return (new((float)x.Position, (float)y.Position, (float)z.Position),
            new((float)x.Velocity, (float)y.Velocity, (float)z.Velocity));
    }

    public void Scale(Visual visual, Vector3 target, bool immediate, int duration = 260)
        => Animate(visual, "Scale", visual.Scale, target, immediate, duration, false);
    public void PulseScale(Visual visual, Vector3 target, bool immediate)
        => Animate(visual, "Scale", visual.Scale, target, immediate, 280, false, impulse: new(-4.5f, -4.5f, 0));
    public void Move(Visual visual, Vector3 target, bool immediate, int duration = 300, int delay = 0)
        => Animate(visual, "Offset", visual.Offset, target, immediate, duration, false, delay);
    public void Scalar(Visual visual, string property, float target, bool immediate, int duration = 200)
    {
        float initial = property switch { "Opacity" => visual.Opacity, "Offset.Y" => visual.Offset.Y,
            "RotationAngleInDegrees" => visual.RotationAngleInDegrees, _ => throw new ArgumentException(nameof(property)) };
        Animate(visual, property, new(initial, 0, 0), new(target, 0, 0), immediate, duration, true);
    }
    void Animate(Visual visual, string property, Vector3 initial, Vector3 target, bool immediate,
        int durationMs, bool scalar, int delayMs = 0, Vector3 impulse = default)
    {
        var key = (visual, property); double now = Now;
        if (!immediate && impulse == Vector3.Zero && _tracks.TryGetValue(key, out var same) && same.To == target) return;
        Vector3 velocity = Vector3.Zero;
        if (_tracks.TryGetValue(key, out var previous))
            (initial, velocity) = Sample(previous, Math.Max(0, now - previous.Started));
        if (immediate)
        {
            _tracks.Remove(key); visual.StopAnimation(property);
            if (property == "Scale") visual.Scale = target;
            else if (property == "Offset") visual.Offset = target;
            else if (property == "Opacity") visual.Opacity = target.X;
            else if (property == "Offset.Y") visual.Offset = new(visual.Offset.X, target.X, visual.Offset.Z);
            else visual.RotationAngleInDegrees = target.X;
            return;
        }
        velocity += impulse;
        // An interrupted element resumes immediately; stagger only a new track.
        int delay = _tracks.ContainsKey(key) ? 0 : delayMs;
        var track = new Track(initial, target, velocity, now + delay / 1000d, Math.Max(1, durationMs) / 1000d, Tuning);
        _tracks[key] = track;
        using var linear = compositor.CreateLinearEasingFunction();
        if (scalar)
        {
            using var animation = compositor.CreateScalarKeyFrameAnimation();
            for (int i = 0; i <= 32; i++)
            {
                float value = Sample(track, track.Duration * i / 32).Position.X;
                if (property == "Opacity") value = Math.Clamp(value, 0, 1);
                animation.InsertKeyFrame(i / 32f, value, linear);
            }
            animation.Duration = TimeSpan.FromMilliseconds(durationMs); animation.DelayTime = TimeSpan.FromMilliseconds(delay);
            animation.DelayBehavior = AnimationDelayBehavior.SetInitialValueBeforeDelay;
            visual.StartAnimation(property, animation);
        }
        else
        {
            using var animation = compositor.CreateVector3KeyFrameAnimation();
            for (int i = 0; i <= 32; i++) animation.InsertKeyFrame(i / 32f, Sample(track, track.Duration * i / 32).Position, linear);
            animation.Duration = TimeSpan.FromMilliseconds(durationMs); animation.DelayTime = TimeSpan.FromMilliseconds(delay);
            animation.DelayBehavior = AnimationDelayBehavior.SetInitialValueBeforeDelay;
            visual.StartAnimation(property, animation);
        }
    }
    public void Dispose()
    {
        foreach (var key in _tracks.Keys) key.Visual.StopAnimation(key.Property);
        _tracks.Clear();
    }
}
