using Microsoft.Win32;
using Windows.ApplicationModel;
using Windows.Foundation;
using Windows.Storage;
using Windows.Storage.FileProperties;
using Windows.Storage.Streams;

namespace GlassDesk.App;

/// <summary>Read the installed app icon from the shell, with bundled artwork as an offline fallback.</summary>
internal static class HomeIconLoader
{
    public static async Task<byte[]?> ReadAsync(int index)
    {
        try
        {
            if (index is 0 or 1)
            {
                string? executable = await Task.Run(() => FindExecutable(index)).ConfigureAwait(false);
                if (executable is not null)
                {
                    try
                    {
                        var installedFile = await StorageFile.GetFileFromPathAsync(executable);
                        using var installedThumbnail = await installedFile.GetThumbnailAsync(ThumbnailMode.SingleItem, 128, ThumbnailOptions.UseCurrentScale);
                        if (installedThumbnail is not null)
                        {
                            var installedIcon = await ReadBytes(installedThumbnail);
                            if (installedIcon is not null) return installedIcon;
                        }
                    }
                    catch { /* Use the bundled offline artwork below. */ }
                }
                string asset = index == 0 ? "HomeMusic.png" : "HomeBrowser.png";
                string bundledPath = Path.Combine(AppContext.BaseDirectory, "Assets", asset);
                if (File.Exists(bundledPath)) return await File.ReadAllBytesAsync(bundledPath).ConfigureAwait(false);
            }
            string? aumid = index switch
            {
                2 => "Microsoft.WindowsTerminal_8wekyb3d8bbwe!App",
                3 => "Microsoft.Windows.Photos_8wekyb3d8bbwe!App",
                4 => "Microsoft.WindowsNotepad_8wekyb3d8bbwe!App",
                5 => "Microsoft.WindowsCalculator_8wekyb3d8bbwe!App",
                _ => null
            };
            if (aumid is not null)
            {
                try
                {
                    var app = AppInfo.GetFromAppUserModelId(aumid);
                    using var stream = await app.DisplayInfo.GetLogo(new Size(128, 128)).OpenReadAsync();
                    return await ReadBytes(stream);
                }
                catch { /* Classic/local fallback below. */ }
            }
            string? path = await Task.Run(() => FindExecutable(index));
            if (path is null) return null;
            var file = await StorageFile.GetFileFromPathAsync(path);
            using var thumbnail = await file.GetThumbnailAsync(ThumbnailMode.SingleItem, 128, ThumbnailOptions.UseCurrentScale);
            return thumbnail is null ? null : await ReadBytes(thumbnail);
        }
        catch { return null; } // Artwork must never block launch or replace a readable fallback.
    }
    static async Task<byte[]?> ReadBytes(IRandomAccessStream stream)
    {
        if (stream.Size is 0 or > 2097152) return null;
        using var input = stream.GetInputStreamAt(0);
        using var reader = new DataReader(input);
        uint size = (uint)stream.Size;
        if (await reader.LoadAsync(size) != size) return null;
        var bytes = new byte[(int)size]; reader.ReadBytes(bytes); return bytes;
    }
    static string? FindExecutable(int index)
    {
        string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        string x86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (index == 0)
        {
            foreach (var hive in new[] { Registry.CurrentUser, Registry.LocalMachine })
            {
                using var key = hive.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\App Paths\cloudmusic.exe");
                if (key?.GetValue(null) is string installed && File.Exists(installed)) return installed;
            }
        }
        string[] candidates = index switch
        {
            0 => [Path.Combine(pf, "NetEase", "CloudMusic", "cloudmusic.exe"),
                Path.Combine(x86, "NetEase", "CloudMusic", "cloudmusic.exe"), Path.Combine(local, "NetEase", "CloudMusic", "cloudmusic.exe")],
            1 => [Path.Combine(x86, "Microsoft", "Edge", "Application", "msedge.exe"),
                Path.Combine(pf, "Microsoft", "Edge", "Application", "msedge.exe")],
            2 => [Path.Combine(local, "Microsoft", "WindowsApps", "wt.exe")],
            4 => [Path.Combine(Environment.SystemDirectory, "notepad.exe")],
            5 => [Path.Combine(Environment.SystemDirectory, "calc.exe")],
            _ => []
        };
        return candidates.FirstOrDefault(File.Exists);
    }
}

