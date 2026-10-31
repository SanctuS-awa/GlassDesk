using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Windows.Graphics.DirectX;
using Windows.UI.Composition;
using WinRT;

internal static class Program
{
    private const string ProbeLibrary = "SystemGlassInkInteropProbe.dll";
    private static int _checks;

    private static int Main(string[] args)
    {
        if (args.Length != 1)
        {
            Console.Error.WriteLine("Pass the absolute path to SystemGlassInkInteropProbe.dll.");
            return 2;
        }

        string probePath = Path.GetFullPath(args[0]);
        NativeLibrary.SetDllImportResolver(typeof(Program).Assembly,
            (name, _, _) => name == ProbeLibrary ? NativeLibrary.Load(probePath) : IntPtr.Zero);

        nint input = 0;
        nint output = 0;
        try
        {
            input = CreateManagedInput();
        Check(input != 0, "CsWinRT FromManaged returns an inspectable for CompositionDrawingSurface");

            int hr = Native.CloneSurface((nint)0x1357, input,
                1.25f, -2.5f, 128.5f, 48.25f, out output);
            Check(hr >= 0 && output != 0,
                "GDCreateSystemGlassInk-shaped call marshals context, inspectable, floats, and out pointer");

            Marshal.Release(input);
            input = 0;
            CollectProjections();

            Check(Native.LiveSurfaceCount() == 1,
                "detached native output reference keeps the stub alive after input and projection release");
            hr = Native.VerifySurface(output, out int matched);
            Check(hr >= 0 && matched == 1,
                "detached output retains both surface interfaces after input release");

            nint ownedOutput = output;
            output = 0;
            VerifyProjectedOutput(ownedOutput);
            CollectProjections();
            Check(Native.LiveSurfaceCount() == 0,
                "releasing the projected output drops its final owned native reference");

            Console.WriteLine($"{_checks} CPU-only CsWinRT/native inspectable checks passed.");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
        finally
        {
            if (output != 0) Marshal.Release(output);
            if (input != 0) Marshal.Release(input);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static nint CreateManagedInput()
    {
        int hr = Native.CreateSurface(out nint created);
        if (hr < 0 || created == 0)
            Marshal.ThrowExceptionForHR(hr < 0 ? hr : unchecked((int)0x80004005));

        try
        {
            CompositionDrawingSurface projected = MarshalInterface<CompositionDrawingSurface>.FromAbi(created);
            Check(projected.Size.Width == 64.0f && projected.Size.Height == 32.0f &&
                projected.AlphaMode == (DirectXAlphaMode)0 && projected.PixelFormat == (DirectXPixelFormat)0,
                "CsWinRT projects the stub as CompositionDrawingSurface and calls its default interface");
            return MarshalInspectable<object>.FromManaged(projected);
        }
        finally
        {
            Marshal.Release(created);
        }
    }

    private static void CollectProjections()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void VerifyProjectedOutput(nint ownedOutput)
    {
        nint remainingOutput = ownedOutput;
        nint roundTrip = 0;
        try
        {
            CompositionDrawingSurface projected = MarshalInterface<CompositionDrawingSurface>.FromAbi(remainingOutput);
            Marshal.Release(remainingOutput);
            remainingOutput = 0;

            roundTrip = MarshalInspectable<object>.FromManaged(projected);
            int hr = Native.VerifySurface(roundTrip, out int matched);
            Check(hr >= 0 && matched == 1,
                "CsWinRT FromAbi keeps the native out object usable after releasing the raw result pointer");
        }
        finally
        {
            if (roundTrip != 0) Marshal.Release(roundTrip);
            if (remainingOutput != 0) Marshal.Release(remainingOutput);
        }
    }

    private static void Check(bool condition, string description)
    {
        if (!condition) throw new InvalidOperationException("FAIL " + description);
        ++_checks;
        Console.WriteLine("PASS " + description);
    }

    private static class Native
    {
        [DllImport(ProbeLibrary, EntryPoint = "InkInteropCreateSurface", ExactSpelling = true, CallingConvention = CallingConvention.StdCall)]
        internal static extern int CreateSurface(out nint result);

        [DllImport(ProbeLibrary, EntryPoint = "InkInteropCloneSurface", ExactSpelling = true, CallingConvention = CallingConvention.StdCall)]
        internal static extern int CloneSurface(nint context, nint input,
            float x, float y, float width, float height, out nint result);

        [DllImport(ProbeLibrary, EntryPoint = "InkInteropVerifySurface", ExactSpelling = true, CallingConvention = CallingConvention.StdCall)]
        internal static extern int VerifySurface(nint input, out int matched);

        [DllImport(ProbeLibrary, EntryPoint = "InkInteropLiveSurfaceCount", ExactSpelling = true, CallingConvention = CallingConvention.StdCall)]
        internal static extern int LiveSurfaceCount();
    }
}
