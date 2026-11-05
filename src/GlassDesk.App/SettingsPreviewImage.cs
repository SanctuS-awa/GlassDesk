using System.Runtime.InteropServices;
using System.Text;
using System.Numerics;
using Windows.Graphics.Imaging;
using Windows.Storage;

namespace GlassDesk.App;

/// <summary>Only a user-selected local file; no display input or network.</summary>
internal sealed record SettingsPreviewImage(byte[] Pixels,int Width,int Height)
{
    static readonly double[] Linear=Enumerable.Range(0,256).Select(i=>{
        double s=i/255d;return s<=.04045?s/12.92:Math.Pow((s+.055)/1.055,2.4);
    }).ToArray();

    /// <summary>Same cover/shift mapping as the GPU, over the already decoded
    /// local image. No screen access or GPU readback.</summary>
    public double Luminance(Vector2 point,Vector2 stage,float shift)
    {
        float scale=Math.Max(stage.X/Width,stage.Y/Height);
        int x=Math.Clamp((int)((point.X+shift-stage.X*.5f)/scale+Width*.5f),0,Width-1);
        int y=Math.Clamp((int)((point.Y-stage.Y*.5f)/scale+Height*.5f),0,Height-1);
        int offset=(y*Width+x)*4;double alpha=Pixels[offset+3]/255d;
        int r=(int)Math.Round(Pixels[offset+2]*alpha+255*(1-alpha));
        int g=(int)Math.Round(Pixels[offset+1]*alpha+255*(1-alpha));
        int b=(int)Math.Round(Pixels[offset]*alpha+255*(1-alpha));
        return .2126*Linear[r]+.7152*Linear[g]+.0722*Linear[b];
    }

    public static string? PickFile(nint owner)
    {
        var name=new StringBuilder(32768);
        var dialog=new OpenFileName { Size=(uint)Marshal.SizeOf<OpenFileName>(),Owner=owner,
            Filter="图片 (*.jpg;*.jpeg;*.png;*.bmp)\0*.jpg;*.jpeg;*.png;*.bmp\0\0",FilterIndex=1,
            File=name,MaxFile=(uint)name.Capacity,Title="选择预览背景",Flags=0x80000|0x1000|0x800|0x8 };
        if(GetOpenFileNameW(ref dialog))return name.ToString();
        uint error=CommDlgExtendedError();
        if(error!=0)throw new InvalidOperationException($"文件选择器错误 0x{error:X8}");
        return null;
    }
    [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)]
    struct OpenFileName
    {
        public uint Size;public nint Owner,Instance;
        public string Filter;public nint CustomFilter;public uint MaxCustomFilter,FilterIndex;
        public StringBuilder File;public uint MaxFile;public nint FileTitle;public uint MaxFileTitle;
        public string? InitialDirectory;public string Title;public uint Flags;public ushort FileOffset,ExtensionOffset;
        public string? DefaultExtension;public nint CustomData,Hook,Template,Reserved;
        public uint ReservedValue,FlagsEx;
    }
    [DllImport("comdlg32.dll",CharSet=CharSet.Unicode,ExactSpelling=true)]
    [return:MarshalAs(UnmanagedType.Bool)]static extern bool GetOpenFileNameW(ref OpenFileName value);
    [DllImport("comdlg32.dll",ExactSpelling=true)]static extern uint CommDlgExtendedError();
    public static async Task<SettingsPreviewImage> Load(string path)
    {
        var file=await StorageFile.GetFileFromPathAsync(path);
        if((await file.GetBasicPropertiesAsync()).Size>40*1024*1024)throw new InvalidOperationException("请选择小于 40 MB 的图片。");
        using var stream=await file.OpenReadAsync();var decoder=await BitmapDecoder.CreateAsync(stream);
        if(decoder.PixelWidth==0||decoder.PixelHeight==0||(ulong)decoder.PixelWidth*decoder.PixelHeight>100_000_000)
            throw new InvalidOperationException("图片尺寸过大或无效。");
        double scale=Math.Min(1,1600d/Math.Max(decoder.OrientedPixelWidth,decoder.OrientedPixelHeight));
        var transform=new BitmapTransform { ScaledWidth=(uint)Math.Max(1,Math.Round(decoder.PixelWidth*scale)),ScaledHeight=(uint)Math.Max(1,Math.Round(decoder.PixelHeight*scale)),InterpolationMode=BitmapInterpolationMode.Fant };
        // Respect the selected photo orientation; the decoder returns oriented pixels.
        var pixels=(await decoder.GetPixelDataAsync(BitmapPixelFormat.Bgra8,BitmapAlphaMode.Straight,transform,ExifOrientationMode.RespectExifOrientation,ColorManagementMode.ColorManageToSRgb)).DetachPixelData();
        bool swapped=decoder.OrientedPixelWidth!=decoder.PixelWidth;
        return new(pixels,(int)(swapped?transform.ScaledHeight:transform.ScaledWidth),(int)(swapped?transform.ScaledWidth:transform.ScaledHeight));
    }
}
