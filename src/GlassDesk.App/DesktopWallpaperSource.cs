using System.Runtime.InteropServices;
using GlassDesk.Core;
using Windows.Graphics.Imaging;
using Windows.Storage;

namespace GlassDesk.App;

/// <summary>Read-only static wallpaper FILE input. Never samples a screen/window.</summary>
internal sealed record DesktopWallpaperSource(byte[] Pixels,int Width,int Height,
    DesktopWallpaperMapping Mapping,uint BackgroundArgb,string Stamp)
{
    public static async Task<DesktopWallpaperSource> LoadAsync(ScreenRect monitor)
    {
        var info=ReadMetadata(monitor);
        string stamp=MakeStamp(info,monitor);
        var budget=new DesktopWallpaperMappingBudget();
        if(!budget.AllowsSourceFileBytes(new FileInfo(info.Path).Length))
            throw new InvalidOperationException("壁纸超过 40 MiB 输入上限。");
        var file=await StorageFile.GetFileFromPathAsync(info.Path);
        using var stream=await file.OpenReadAsync();
        if(stream.Size>long.MaxValue||!budget.AllowsSourceFileBytes((long)stream.Size))
            throw new InvalidOperationException("打开的壁纸流超过输入上限。");
        var decoder=await BitmapDecoder.CreateAsync(stream);
        if(decoder.FrameCount!=1||!budget.Allows(new((int)decoder.OrientedPixelWidth,(int)decoder.OrientedPixelHeight)))
            throw new InvalidOperationException("实验仅支持单帧静态壁纸，最大 16 MP、长边 4096 像素。");
        if(!DesktopWallpaperMapping.TryCreate(new(monitor.X,monitor.Y,monitor.Width,monitor.Height),
            new((int)decoder.OrientedPixelWidth,(int)decoder.OrientedPixelHeight),info.Mode,out var mapping,budget))
            throw new InvalidOperationException("不支持当前壁纸坐标映射。");
        double ratio=Math.Min(1,1600d/Math.Max(decoder.OrientedPixelWidth,decoder.OrientedPixelHeight));
        var transform=new BitmapTransform { ScaledWidth=(uint)Math.Max(1,Math.Round(decoder.PixelWidth*ratio)),
            ScaledHeight=(uint)Math.Max(1,Math.Round(decoder.PixelHeight*ratio)),InterpolationMode=BitmapInterpolationMode.Fant };
        byte[] pixels=(await decoder.GetPixelDataAsync(BitmapPixelFormat.Bgra8,BitmapAlphaMode.Straight,transform,
            ExifOrientationMode.RespectExifOrientation,ColorManagementMode.ColorManageToSRgb)).DetachPixelData();
        bool swapped=decoder.OrientedPixelWidth!=decoder.PixelWidth;
        if(stamp!=MakeStamp(ReadMetadata(monitor),monitor))throw new IOException("壁纸在解码时已改变，将在下一次刷新重新读取。");
        int width=(int)(swapped?transform.ScaledHeight:transform.ScaledWidth),height=(int)(swapped?transform.ScaledWidth:transform.ScaledHeight);
        if(pixels.LongLength!=(long)width*height*4)throw new InvalidOperationException("壁纸像素布局无效。");
        return new(pixels,width,height,mapping!,info.BackgroundArgb,stamp);
    }
    public static string ReadStamp(ScreenRect monitor)=>MakeStamp(ReadMetadata(monitor),monitor);
    static string MakeStamp((string Path,DesktopWallpaperPositionMode Mode,uint BackgroundArgb) info,ScreenRect monitor)
    {
        var file=new FileInfo(info.Path);
        return $"{info.Path}|{file.Length}|{file.LastWriteTimeUtc.Ticks}|{info.Mode}|{info.BackgroundArgb}|{monitor}";
    }

    public double Luminance(double screenX,double screenY)
    {
        uint background=BackgroundArgb;
        double r=(background>>16)&255,g=(background>>8)&255,b=background&255;
        if(Mapping.TryMapScreenPoint(screenX,screenY,out var point)&&point.IsOnMonitor&&point.IsCoveredByImage){
            int x=Math.Clamp((int)(point.PixelX*Width/Mapping.Image.Width),0,Width-1);
            int y=Math.Clamp((int)(point.PixelY*Height/Mapping.Image.Height),0,Height-1);
            int offset=(y*Width+x)*4;double a=Pixels[offset+3]/255d;
            r=Pixels[offset+2]*a+r*(1-a);g=Pixels[offset+1]*a+g*(1-a);b=Pixels[offset]*a+b*(1-a);
        }
        static double Linear(double c){c/=255;return c<=.04045?c/12.92:Math.Pow((c+.055)/1.055,2.4);}
        return .2126*Linear(r)+.7152*Linear(g)+.0722*Linear(b);
    }
    static (string Path,DesktopWallpaperPositionMode Mode,uint BackgroundArgb) ReadMetadata(ScreenRect monitor)
    {
        object instance=Activator.CreateInstance(Type.GetTypeFromCLSID(new("C2CF3110-460E-4FC1-B9D0-8A1C0C9CC4BD"),throwOnError:true)!)!;
        try{
            var wallpaper=(IDesktopWallpaper)instance;
            Marshal.ThrowExceptionForHR(wallpaper.GetStatus(out uint status));
            if((status&2)!=0)throw new InvalidOperationException("实验尚不支持壁纸幻灯片。");
            Marshal.ThrowExceptionForHR(wallpaper.GetPosition(out int position));
            var mode=position switch {0=>DesktopWallpaperPositionMode.Center,2=>DesktopWallpaperPositionMode.Stretch,
                3=>DesktopWallpaperPositionMode.Fit,4=>DesktopWallpaperPositionMode.Fill,
                _=>throw new InvalidOperationException("实验暂不支持平铺或跨屏壁纸。")};
            Marshal.ThrowExceptionForHR(wallpaper.GetBackgroundColor(out uint background));
            Marshal.ThrowExceptionForHR(wallpaper.GetMonitorDevicePathCount(out uint count));
            if(count>64)throw new InvalidOperationException("显示器数量无效。");
            for(uint i=0;i<count;i++){
                Marshal.ThrowExceptionForHR(wallpaper.GetMonitorDevicePathAt(i,out nint idPointer));
                string id;try{id=Marshal.PtrToStringUni(idPointer)??"";}finally{Marshal.FreeCoTaskMem(idPointer);}
                Marshal.ThrowExceptionForHR(wallpaper.GetMonitorRECT(id,out var bounds));
                if(bounds.Left!=monitor.X||bounds.Top!=monitor.Y||bounds.Right-bounds.Left!=monitor.Width||bounds.Bottom-bounds.Top!=monitor.Height)continue;
                int result=wallpaper.GetWallpaper(id,out nint pathPointer);
                string path;try{path=Marshal.PtrToStringUni(pathPointer)??"";}finally{Marshal.FreeCoTaskMem(pathPointer);}
                if(result!=0||string.IsNullOrWhiteSpace(path)||!File.Exists(path))throw new InvalidOperationException("此显示器没有可读取的静态壁纸文件。");
                // COLORREF is BGR; the shader consumes an ordinary ARGB colour.
                uint argb=0xff000000u|((background&255)<<16)|(background&0xff00)|((background>>16)&255);
                return (path,mode,argb);
            }
            throw new InvalidOperationException("壁纸显示器映射未匹配。");
        }finally{if(Marshal.IsComObject(instance))Marshal.FinalReleaseComObject(instance);}
    }
    [StructLayout(LayoutKind.Sequential)]struct Rect {public int Left,Top,Right,Bottom;}
    [ComImport,Guid("B92B56A9-8B55-4E14-9A89-0199BBB6F93B"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IDesktopWallpaper
    {
        [PreserveSig]int SetWallpaper([MarshalAs(UnmanagedType.LPWStr)]string id,[MarshalAs(UnmanagedType.LPWStr)]string path);
        [PreserveSig]int GetWallpaper([MarshalAs(UnmanagedType.LPWStr)]string id,out nint path);
        [PreserveSig]int GetMonitorDevicePathAt(uint index,out nint id);
        [PreserveSig]int GetMonitorDevicePathCount(out uint count);
        [PreserveSig]int GetMonitorRECT([MarshalAs(UnmanagedType.LPWStr)]string id,out Rect rect);
        [PreserveSig]int SetBackgroundColor(uint color);
        [PreserveSig]int GetBackgroundColor(out uint color);
        [PreserveSig]int SetPosition(int position);
        [PreserveSig]int GetPosition(out int position);
        [PreserveSig]int SetSlideshow(nint items);
        [PreserveSig]int GetSlideshow(out nint items);
        [PreserveSig]int SetSlideshowOptions(uint options,uint tick);
        [PreserveSig]int GetSlideshowOptions(out uint options,out uint tick);
        [PreserveSig]int AdvanceSlideshow([MarshalAs(UnmanagedType.LPWStr)]string id,int direction);
        [PreserveSig]int GetStatus(out uint state);
    }
}
