using System.Runtime.InteropServices;
using GlassDesk.Core;

namespace GlassDesk.NativeBridge;

public readonly record struct NativeTypographyDescription(uint Version,uint V1Size,uint V2Size,uint Capabilities);
public readonly record struct NativeTextMetrics(float Advance,float WidthIncludingTrailingWhitespace,float Height,
    float Baseline,float LineHeight,uint LineCount);
public readonly record struct NativeInkBounds(float Left,float Top,float Right,float Bottom)
{
    public bool IsEmpty=>Right<=Left||Bottom<=Top;
}
public readonly record struct TypographyCacheStats(ulong Formats,ulong Layouts,ulong FormatHits,ulong LayoutHits,ulong LayoutsCreated);

/// <summary>Explicit managed/native batch negotiation. An old DLL cannot silently drop V2 typography.</summary>
public static class NativeTypographyAbi
{
    internal const uint InkBoundsCapability=32;

    public static IReadOnlyDictionary<string,int> ManagedLayout {get;}=new Dictionary<string,int>
    {
        ["GDOp.size"]=Marshal.SizeOf<NativeMethods.Op>(),["GDOpV2.size"]=Marshal.SizeOf<NativeMethods.OpV2>(),
        ["fontWeight"]=(int)Marshal.OffsetOf<NativeMethods.OpV2>(nameof(NativeMethods.OpV2.FontWeight)),
        ["trackingEm"]=(int)Marshal.OffsetOf<NativeMethods.OpV2>(nameof(NativeMethods.OpV2.TrackingEm)),
        ["textRole"]=(int)Marshal.OffsetOf<NativeMethods.OpV2>(nameof(NativeMethods.OpV2.TextRole)),
        ["lineHeightDip"]=(int)Marshal.OffsetOf<NativeMethods.OpV2>(nameof(NativeMethods.OpV2.LineHeightDip)),
        ["baselineDip"]=(int)Marshal.OffsetOf<NativeMethods.OpV2>(nameof(NativeMethods.OpV2.BaselineDip)),
        ["flags"]=(int)Marshal.OffsetOf<NativeMethods.OpV2>(nameof(NativeMethods.OpV2.Flags)),
        ["locale"]=(int)Marshal.OffsetOf<NativeMethods.OpV2>(nameof(NativeMethods.OpV2.Locale))
    };
    public static NativeTypographyDescription ReadAndValidate()
    {
        int[] expected=[64,96,64,68,72,76,80,84,88];
        string[] fields=["GDOp.size","GDOpV2.size","fontWeight","trackingEm","textRole","lineHeightDip","baselineDip","flags","locale"];
        if(!fields.Select(field=>ManagedLayout[field]).SequenceEqual(expected)||Marshal.SizeOf<NativeMethods.AbiInfo>()!=24||
            Marshal.SizeOf<NativeMethods.TextMetrics>()!=24||Marshal.SizeOf<NativeMethods.CacheStats>()!=40)
            throw new InvalidOperationException("GlassDesk managed typography ABI layout is incompatible.");
        if(Marshal.SizeOf<NativeMethods.InkBounds>()!=16)
            throw new InvalidOperationException("GlassDesk managed ink-bounds ABI layout is incompatible.");
        var info=new NativeMethods.AbiInfo{Size=24};
        try {Marshal.ThrowExceptionForHR(NativeMethods.GDGetNativeAbiInfo(ref info));}
        catch(EntryPointNotFoundException error)
        {throw new InvalidOperationException("GlassDesk.Native.dll requires typography ABI V2; use the DLL from this preview batch.",error);}
        if(info.Size!=24||info.Version!=2||info.V1Size!=64||info.V2Size!=96||(info.Capabilities&31)!=31)
            throw new InvalidOperationException("GlassDesk.Native.dll reports an incompatible typography ABI.");
        return new(info.Version,info.V1Size,info.V2Size,info.Capabilities);
    }
}

internal static partial class NativeMethods
{
    [StructLayout(LayoutKind.Sequential)] internal struct AbiInfo
    {public uint Size,Version,V1Size,V2Size,Capabilities,Reserved;}
    [StructLayout(LayoutKind.Sequential)] internal struct OpV2
    {
        public Op Legacy;
        public int FontWeight;
        public float TrackingEm;
        public uint TextRole;
        public float LineHeightDip,BaselineDip;
        public uint Flags;
        public nint Locale;
        public OpV2(SurfaceOp op,nint text,nint image,nint locale)
        {
            Legacy=new((int)op.Kind,op.X,op.Y,op.Width,op.Height,op.Radius,op.FontSize,op.Color,
                op.Bold?1:0,op.Center?1:0,text,image,(uint)(op.Image?.Length??0));
            FontWeight=op.FontWeight;TrackingEm=op.TrackingEm;TextRole=(uint)op.TextRole;
            LineHeightDip=op.LineHeightDip;BaselineDip=op.BaselineDip;Flags=(uint)op.TextFlags;Locale=locale;
        }
    }
    [StructLayout(LayoutKind.Sequential)] internal readonly record struct TextMetrics(float Advance,float TrailingWidth,float Height,
        float Baseline,float LineHeight,uint LineCount);
    [StructLayout(LayoutKind.Sequential)] internal struct InkBounds
    {public float Left,Top,Right,Bottom;}
    [StructLayout(LayoutKind.Sequential)] internal readonly record struct CacheStats(ulong Formats,ulong Layouts,ulong FormatHits,ulong LayoutHits,ulong LayoutsCreated);
    [DllImport(Library,ExactSpelling=true,CallingConvention=CallingConvention.StdCall)]internal static extern int GDGetNativeAbiInfo(ref AbiInfo info);
    [DllImport(Library,ExactSpelling=true,CallingConvention=CallingConvention.StdCall,CharSet=CharSet.Unicode)]internal static extern int GDConfigureTypographyV2(nint context,uint profile,uint revision,string? qaFontPath);
    [DllImport(Library,ExactSpelling=true,CallingConvention=CallingConvention.StdCall)]internal static extern int GDMeasureTextV2(nint context,in OpV2 op,uint stride,out TextMetrics metrics);
    [DllImport(Library,ExactSpelling=true,CallingConvention=CallingConvention.StdCall)]internal static extern int GDMeasureTextInkBoundsV2(nint context,in OpV2 op,uint stride,out InkBounds bounds);
    [DllImport(Library,ExactSpelling=true,CallingConvention=CallingConvention.StdCall)]internal static extern unsafe int GDRenderSurfaceV2(nint context,float width,float height,OpV2* operations,uint count,uint stride,out nint surface);
    [DllImport(Library,ExactSpelling=true,CallingConvention=CallingConvention.StdCall)]internal static extern int GDGetTypographyCacheStats(nint context,out CacheStats stats);
}
