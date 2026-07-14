using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage;

namespace SoundShell.App;

internal static class IconCache
{
    private static readonly ImageSource fallback = new BitmapImage(new Uri("ms-appx:///Assets/Square44x44Logo.png"));

    public static ImageSource Resolve(string executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath) || !File.Exists(executablePath))
            return fallback;

        try
        {
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(executablePath)))[..16];
            var directory = Path.Combine(ApplicationData.Current.LocalCacheFolder.Path, "icons");
            var path = Path.Combine(directory, $"{hash}.png");
            if (!File.Exists(path))
            {
                Directory.CreateDirectory(directory);
                using var icon = Icon.ExtractAssociatedIcon(executablePath);
                using var bitmap = icon?.ToBitmap();
                bitmap?.Save(path, ImageFormat.Png);
            }
            return File.Exists(path) ? new BitmapImage(new Uri(path)) : fallback;
        }
        catch
        {
            return fallback;
        }
    }
}
