using System.Numerics;

namespace GlassDesk.Core;

/// <summary>A settings-only, interruptible capsule/peek experiment. No player, HWND or capture.</summary>
public sealed class LiquidTransitionPrototype
{
    public const float Width = 320, Height = 176;
    // Internal art direction, not a claimed Apple value or a user preference.
    // Refract adds a small continuous size/velocity response to this baseline.
    public const float MaterialMagnification = .08f;
    public const float MaterialRefraction = 1f;
    public double Progress { get; private set; }
    public double Velocity { get; private set; }
    public double Target { get; private set; }
    public bool Active { get; private set; }
    public float Tension { get; private set; } = .75f;
    public float Bounce { get; private set; } = .55f;
    public int ResponseMs { get; private set; } = 420;
    double _position, _speed, _started;

    public void Configure(float tension, float bounce, int responseMs, double now)
    {
        Sample(now);
        Tension = float.IsFinite(tension) ? Math.Clamp(tension, 0, 1.5f) : .75f;
        Bounce = float.IsFinite(bounce) ? Math.Clamp(bounce, 0, 1) : .55f;
        ResponseMs = Math.Clamp(responseMs, 220, 800);
        _position = Progress; _speed = Velocity; _started = now;
    }

    public void SetTarget(bool expanded, double now, bool reduced = false)
    {
        Sample(now);
        double next = expanded ? 1 : 0;
        if (reduced) { Scrub(next); return; }
        if (Target == next && Active) return;
        _position = Progress; _speed = Velocity; _started = now; Target = next;
        Active = Math.Abs(Progress - Target) > .0005 || Math.Abs(Velocity) > .005;
    }

    public void Scrub(double progress)
    {
        Progress = Target = _position = double.IsFinite(progress) ? Math.Clamp(progress, 0, 1) : 0;
        Velocity = _speed = 0; Active = false;
    }

    public void Sample(double now)
    {
        if (!Active) return;
        double t = Math.Max(0, now - _started) / 1000;
        // The familiar mass=1, stiffness=100 spring, with response scaling and
        // an adjustable damping ratio. Retargeting retains position AND speed.
        double omega = 10 * 500d / ResponseMs, damping = .95 - .35 * Bounce;
        double decay = damping * omega, frequency = omega * Math.Sqrt(1 - damping * damping);
        double a = _position - Target, b = (_speed + decay * a) / frequency;
        double c = Math.Cos(frequency * t), s = Math.Sin(frequency * t), e = Math.Exp(-decay * t);
        Progress = Target + e * (a * c + b * s);
        Velocity = e * ((-a * frequency * s + b * frequency * c) - decay * (a * c + b * s));
        if ((Math.Abs(Progress - Target) < .0005 && Math.Abs(Velocity) < .005) || t > ResponseMs / 250d)
        { Progress = Target; Velocity = 0; Active = false; }
    }

    // Shared presentation values for crisp foreground pieces. These paths
    // explain the expansion without stretching text or the complete card.
    public Vector2 HeaderOffset(bool companion)
    {
        float p=Math.Clamp((float)Progress,0,1),arc=MathF.Sin(MathF.PI*p);
        float response=Math.Clamp((float)Velocity/9,-.18f,.18f)*Tension;
        return companion ? new(-14*arc,3*arc+response*2) : new(10*arc,7*arc-response*2);
    }
    public Vector2 CompanionSize => Vector2.Lerp(new(32,32),new(58,28),HeaderProgress);

    public float ContentProgress => Phase((float)Progress, .58f, .94f);
    public float TransportProgress => Phase((float)Progress,.74f,.98f);
    public float HeaderProgress => Phase((float)Progress, .04f, .88f);

    public Vector2 Extent
    {
        get
        {
            float p=Math.Clamp((float)Progress,0,1),growth=Phase(p,.08f,1);
            float strain=Math.Clamp((float)Velocity/7,-.6f,.6f)*Tension;
            float rebound=Math.Clamp((float)Progress-p,-.06f,.08f)*Tension;
            return new(float.Lerp(200,320,Phase(p,0,.95f))*(1-.035f*strain-rebound*.3f),
                float.Lerp(52,176,growth)*(1+.045f*strain+rebound*.55f));
        }
    }

    public float Distance(Vector2 point)
    {
        float p=Math.Clamp((float)Progress,0,1),growth=Phase(p,.08f,1);
        var size=Extent;
        float y=Math.Clamp(point.Y/size.Y,0,1),breath=MathF.Sin(MathF.PI*p)*Tension;
        // A single continuous body. The lower half unfolds before the shoulder;
        // an internal second lobe must not create a T-shaped shelf.
        float widthScale=1+breath*(.075f*MathF.Sin(MathF.PI*y)+.055f*(2*y-1));
        var warped=new Vector2(160+(point.X-160)/widthScale,point.Y);
        return Rounded(warped,new(160,size.Y/2),size,float.Lerp(26,24,growth));
    }

    /// <summary>Inverse map shared with PreviewGlass.hlsl. Stable interior scale
    /// plus a convex bezel with a short continuous boundary transition.</summary>
    public Vector2 Refract(Vector2 point,float magnification,float refraction)
    {
        var n=new Vector2(Distance(point+new Vector2(.4f,0))-Distance(point-new Vector2(.4f,0)),
            Distance(point+new Vector2(0,.4f))-Distance(point-new Vector2(0,.4f)));
        n/=Math.Max(n.Length(),.0001f);
        float p=Math.Clamp((float)Progress,0,1),energy=Math.Clamp((float)Math.Abs(Velocity)/8,0,1);
        float zoom=1+Math.Clamp(magnification,0,.35f)*(1+.12f*p+.12f*energy);
        float depth=Math.Max(0,-Distance(point)),band=float.Lerp(18,30,Phase(p,.08f,1));
        var centre=new Vector2(160,Extent.Y/2);
        float u=1-Math.Clamp(depth/band,0,1),height=band*.62f;
        float slope=height/band*u/MathF.Sqrt(Math.Max(1-u*u,.003f));
        var normal=Vector3.Normalize(new Vector3(n*slope,1));
        const float eta=1/1.45f;
        float coefficient=eta*normal.Z-MathF.Sqrt(1-eta*eta*(1-normal.Z*normal.Z));
        var ray=eta*new Vector3(0,0,-1)+coefficient*normal;
        float cap=MathF.Sqrt(Math.Clamp(1-u*u,0,1));
        var edge=new Vector2(ray.X,ray.Y)/Math.Max(-ray.Z,.1f)*(band*.62f+height*cap)
            *Math.Clamp(refraction,0,1.5f)*(1+.12f*energy)*Phase(depth,0,1.5f);
        return point-(point-centre)*(1-1/zoom)*Phase(depth,band*.6f,band+6)+edge;
    }

    public Vector2[] Contour(int samples = 72)
    {
        samples = Math.Clamp(samples, 24, 144);
        float top = Root(-16, 26, top: true), bottom = Root(26, 208, top: false);
        var points = new Vector2[(samples + 1) * 2];
        for (int i = 0; i <= samples; i++)
        {
            float t = (1 - MathF.Cos(MathF.PI * i / samples)) / 2;
            float y = float.Lerp(top, bottom, t);
            // Search in distance-from-centre coordinates, including a zero-width
            // cross section at round caps. All generated points stay ordered.
            float lo = 0, hi = 196;
            for (int n = 0; n < 16; n++)
            { float mid = (lo + hi) / 2; if (Distance(new(160 + mid, y)) <= .001f) lo = mid; else hi = mid; }
            points[i] = new(160 - lo, y);
            points[points.Length - 1 - i] = new(160 + lo, y);
        }
        return points;
    }

    float Root(float lo, float hi, bool top)
    {
        for (int i = 0; i < 18; i++)
        {
            float mid = (lo + hi) / 2; bool inside = Distance(new(160, mid)) <= 0;
            if (inside == top) hi = mid; else lo = mid;
        }
        return (lo + hi) / 2;
    }

    public static Vector2 ClampPlacement(Vector2 value, Vector2 size, bool expanded)
    {
        var min = expanded ? new Vector2(8, 6) : new Vector2(68, 5);
        var max = expanded ? new Vector2(312, 170) - size : new Vector2(252, 47) - size;
        return Vector2.Clamp(value, min, Vector2.Max(min, max));
    }
    public static float Phase(float value, float from, float to)
    { float t = Math.Clamp((value - from) / (to - from), 0, 1); return t * t * (3 - 2 * t); }
    static float Rounded(Vector2 p, Vector2 center, Vector2 size, float radius)
    {
        radius = Math.Min(radius, Math.Min(size.X, size.Y) / 2);
        Vector2 q = Vector2.Abs(p - center) - size / 2 + new Vector2(radius);
        return Vector2.Max(q, Vector2.Zero).Length() + Math.Min(Math.Max(q.X, q.Y), 0) - radius;
    }
}
