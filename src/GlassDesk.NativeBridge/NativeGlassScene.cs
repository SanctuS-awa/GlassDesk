using GlassDesk.Core;
using System.Runtime.InteropServices;

namespace GlassDesk.NativeBridge;

/// <summary>A local Layer 2 or Layer 3 glass shape, expressed in panel DIPs.</summary>
public readonly record struct GlassShape(
    uint Id,
    uint ParentId,
    uint Layer,
    uint GroupId,
    float X,
    float Y,
    float Width,
    float Height,
    float Radius,
    float Materialize,
    float Press,
    bool Enabled = true);

internal static class GlassSceneValidation
{
    internal const int MaximumShapes = 32;

    internal static void Validate(ReadOnlySpan<GlassShape> shapes)
    {
        if (shapes.Length > MaximumShapes)
            throw new ArgumentOutOfRangeException(nameof(shapes), "A glass frame supports at most 32 shapes.");
        Span<uint> identifiers = stackalloc uint[MaximumShapes];
        for (int index = 0; index < shapes.Length; index++)
        {
            GlassShape shape = shapes[index];
            if (shape.Id == 0 || (shape.Layer != 2u && shape.Layer != 3u) ||
                !float.IsFinite(shape.X) || !float.IsFinite(shape.Y) ||
                !float.IsFinite(shape.Width) || !float.IsFinite(shape.Height) ||
                !float.IsFinite(shape.Radius) || !float.IsFinite(shape.Materialize) ||
                !float.IsFinite(shape.Press) || shape.Width <= 0 || shape.Height <= 0 ||
                shape.Radius < 0 || shape.Materialize is < 0 or > 1 || shape.Press is < 0 or > 1 ||
                MathF.Abs(shape.X) > 4096 || MathF.Abs(shape.Y) > 4096 ||
                shape.Width > 4096 || shape.Height > 4096 || shape.Radius > 4096)
                throw new ArgumentException($"Glass shape at index {index} is invalid.", nameof(shapes));
            for (int prior = 0; prior < index; prior++)
                if (identifiers[prior] == shape.Id)
                    throw new ArgumentException($"Glass shape id {shape.Id} is duplicated.", nameof(shapes));
            identifiers[index] = shape.Id;
        }
    }
}

internal static class GlassSceneAbi
{
    internal const uint Version = 1;
    internal const uint ShapeStride = 48;

    [StructLayout(LayoutKind.Sequential)]
    internal struct ShapeV1
    {
        public uint Id, ParentId, Layer, GroupId;
        public float X, Y, Width, Height, Radius, Materialize, Press;
        public uint Enabled;

        public static ShapeV1 From(GlassShape value) => new()
        {
            Id = value.Id,
            ParentId = value.ParentId,
            Layer = value.Layer,
            GroupId = value.GroupId,
            X = value.X,
            Y = value.Y,
            Width = value.Width,
            Height = value.Height,
            Radius = value.Radius,
            Materialize = value.Materialize,
            Press = value.Press,
            Enabled = value.Enabled ? 1u : 0u
        };
    }
}

public sealed partial class NativeWidgetHost
{
    readonly record struct PendingGlassScene(ulong Sequence, GlassShape[] Shapes);
    PendingGlassScene? pendingGlassScene;

    /// <summary>
    /// Copies the shape snapshot. It is submitted only by the matching
    /// positioned-frame overload, so native rendering cannot pair it with an
    /// older panel geometry frame.
    /// </summary>
    public void SetGlassScene(ulong sequence, ReadOnlySpan<GlassShape> shapes)
    {
        CheckThread();
        GlassSceneValidation.Validate(shapes);
        pendingGlassScene = new PendingGlassScene(sequence, shapes.ToArray());
    }

    /// <summary>Commits the pending shape snapshot and frame geometry together.</summary>
    public unsafe void ApplyContinuousPositionedFrame(HostFrame frame, float screenX, float screenY, ulong sequence)
    {
        CheckThread();
        PendingGlassScene pending = pendingGlassScene is { } value && value.Sequence == sequence
            ? value
            : new PendingGlassScene(sequence, Array.Empty<GlassShape>());
        if (pendingGlassScene is { } unmatched && unmatched.Sequence != sequence)
            throw new InvalidOperationException("Glass shape sequence must match the positioned frame sequence.");

        GlassShape[] source = pending.Shapes;
        var nativeShapes = new GlassSceneAbi.ShapeV1[source.Length];
        for (int index = 0; index < source.Length; index++)
            nativeShapes[index] = GlassSceneAbi.ShapeV1.From(source[index]);

        var nativeFrame = new NativeMethods.Frame(frame.Width, frame.Height, frame.Radius,
            frame.Blur, frame.TintOpacity, frame.Solid ? 1 : 0);
        int result;
        if (nativeShapes.Length == 0)
        {
            result = NativeMethods.GDApplySceneFrameV1(native, in nativeFrame, frame.SurfaceTension,
                screenX, screenY, sequence, null, 0, GlassSceneAbi.ShapeStride,
                GlassSceneAbi.ShapeStride, GlassSceneAbi.Version);
        }
        else
        {
            fixed (GlassSceneAbi.ShapeV1* pointer = nativeShapes)
                result = NativeMethods.GDApplySceneFrameV1(native, in nativeFrame, frame.SurfaceTension,
                    screenX, screenY, sequence, pointer, checked((uint)nativeShapes.Length),
                    GlassSceneAbi.ShapeStride, GlassSceneAbi.ShapeStride, GlassSceneAbi.Version);
        }
        Marshal.ThrowExceptionForHR(result);
        if (pendingGlassScene is { } applied && applied.Sequence == sequence)
            pendingGlassScene = null;
        RefreshContext();
    }
}

internal static partial class NativeMethods
{
    [DllImport("GlassDesk.Native.dll", ExactSpelling = true, CallingConvention = CallingConvention.StdCall)]
    internal static extern unsafe int GDApplySceneFrameV1(nint context, in Frame frame, float tension,
        float screenX, float screenY, ulong sequence, GlassSceneAbi.ShapeV1* shapes,
        uint count, uint stride, uint structSize, uint version);
}
