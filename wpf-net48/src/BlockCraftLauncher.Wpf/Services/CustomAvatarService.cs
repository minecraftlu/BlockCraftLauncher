using System;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace BlockCraftLauncher.Wpf.Services
{
    /// <summary>
    /// Local launcher-only avatar. It is never uploaded to Microsoft/Mojang or
    /// used as a Minecraft skin. The PNG lives beside the persisted settings,
    /// not inside the EXE or a user-configured Minecraft/download directory.
    /// </summary>
    internal static class CustomAvatarService
    {
        internal const int OutputSize = 256;
        const long MaximumInputBytes = 12L * 1024 * 1024;
        const int MaximumDimension = 8192;

        internal static string AvatarPath(string settingsFile)
        {
            if (string.IsNullOrWhiteSpace(settingsFile))
                throw new ArgumentException("启动器设置文件路径不能为空。", nameof(settingsFile));
            return Path.Combine(Path.GetDirectoryName(Path.GetFullPath(settingsFile)), "custom-avatar.png");
        }

        internal static ImageSource LoadOrDefault(string settingsFile)
        {
            string path = AvatarPath(settingsFile);
            if (File.Exists(path))
            {
                try
                {
                    using (var file = new FileStream(path, FileMode.Open, FileAccess.Read,
                        FileShare.ReadWrite | FileShare.Delete))
                    {
                        var decoder = BitmapDecoder.Create(file,
                            BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.OnLoad);
                        var frame = decoder.Frames[0];
                        if (frame.PixelWidth > 0 && frame.PixelHeight > 0 &&
                            frame.PixelWidth <= MaximumDimension && frame.PixelHeight <= MaximumDimension)
                        {
                            frame.Freeze(); // Do not retain a file lock during updates.
                            return frame;
                        }
                    }
                }
                catch (Exception error) when (error is IOException ||
                    error is UnauthorizedAccessException || error is NotSupportedException ||
                    error is ArgumentException || error is FileFormatException ||
                    error is System.Runtime.InteropServices.COMException)
                {
                    // A damaged/locked avatar must never prevent launcher startup.
                }
            }
            var fallback = new BitmapImage(
                new Uri("pack://application:,,,/Assets/blockcraft_icon.png", UriKind.Absolute));
            fallback.Freeze();
            return fallback;
        }

        internal static void SaveImage(string settingsFile, string inputFile)
        {
            if (string.IsNullOrWhiteSpace(inputFile) || !File.Exists(inputFile))
                throw new FileNotFoundException("找不到所选头像图片。", inputFile);
            var size = new FileInfo(inputFile).Length;
            if (size <= 0 || size > MaximumInputBytes)
                throw new InvalidOperationException("头像图片必须小于 12 MB。");

            BitmapSource source;
            using (var stream = new FileStream(inputFile, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var decoder = BitmapDecoder.Create(stream,
                    BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.OnLoad);
                if (!(decoder is PngBitmapDecoder) && !(decoder is JpegBitmapDecoder))
                    throw new InvalidOperationException("只支持 PNG 或 JPG/JPEG 格式的头像。");
                source = decoder.Frames[0];
            }
            int width = source.PixelWidth, height = source.PixelHeight;
            if (width <= 0 || height <= 0 || width > MaximumDimension || height > MaximumDimension)
                throw new InvalidOperationException("图片尺寸必须在 1～8192 像素之间。");

            // Center square crop, scaled to a stable 256×256 PNG to keep
            // the avatar crisp and avoid storing personal source paths.
            int side = Math.Min(width, height);
            var cropped = new CroppedBitmap(source,
                new Int32Rect((width - side) / 2, (height - side) / 2, side, side));
            var normalized = new TransformedBitmap(cropped,
                new ScaleTransform((double)OutputSize / side, (double)OutputSize / side));
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(normalized));

            string destination = AvatarPath(settingsFile);
            Directory.CreateDirectory(Path.GetDirectoryName(destination));
            string temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write,
                    FileShare.None))
                    encoder.Save(output);
                if (File.Exists(destination))
                    File.Replace(temporary, destination, null, true);
                else
                    File.Move(temporary, destination);
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
        }

        internal static void Reset(string settingsFile)
        {
            string path = AvatarPath(settingsFile);
            if (File.Exists(path)) File.Delete(path);
        }

        /// <summary>Network-free regression for cropping, persistence and reset.</summary>
        internal static void RunOfflineSelfTest()
        {
            string folder = Path.Combine(Path.GetTempPath(), "BlockCraftAvatarTest-" +
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            try
            {
                string settingsFile = Path.Combine(folder, "settings.json");
                string sourceFile = Path.Combine(folder, "source.png");
                var bitmap = new WriteableBitmap(8, 4, 96, 96, PixelFormats.Bgra32, null);
                var pixels = new byte[8 * 4 * 4];
                for (int y = 0; y < 4; ++y)
                    for (int x = 0; x < 8; ++x)
                    {
                        int p = (y * 8 + x) * 4;
                        pixels[p] = x >= 6 ? (byte)255 : (byte)0; // blue strip
                        pixels[p + 1] = x >= 2 && x < 6 ? (byte)255 : (byte)0; // green center
                        pixels[p + 2] = x < 2 ? (byte)255 : (byte)0; // red strip
                        pixels[p + 3] = 255;
                    }
                bitmap.WritePixels(new Int32Rect(0, 0, 8, 4), pixels, 8 * 4, 0);
                var png = new PngBitmapEncoder();
                png.Frames.Add(BitmapFrame.Create(bitmap));
                using (var output = File.Create(sourceFile)) png.Save(output);
                SaveImage(settingsFile, sourceFile);
                var saved = (BitmapSource)LoadOrDefault(settingsFile);
                if (saved.PixelWidth != OutputSize || saved.PixelHeight != OutputSize)
                    throw new InvalidOperationException("Avatar export size regression");
                var sample = new byte[4];
                var sampleSource = new FormatConvertedBitmap(saved, PixelFormats.Bgra32, null, 0);
                sampleSource.CopyPixels(new Int32Rect(128, 128, 1, 1), sample, 4, 0);
                if (sample[1] < 220 || sample[0] > 35 || sample[2] > 35)
                    throw new InvalidOperationException("Avatar center crop regression");
                string invalid = Path.Combine(folder, "invalid.png");
                File.WriteAllText(invalid, "not an image");
                try
                {
                    SaveImage(settingsFile, invalid);
                    throw new InvalidOperationException("Invalid avatar was accepted");
                }
                catch (Exception ex) when (ex is FileFormatException ||
                    ex is NotSupportedException || ex is System.Runtime.InteropServices.COMException)
                {
                    // Invalid replacement must leave the previously saved image intact.
                }
                if (!File.Exists(AvatarPath(settingsFile)))
                    throw new InvalidOperationException("Invalid input removed the existing avatar");
                Reset(settingsFile);
                if (File.Exists(AvatarPath(settingsFile)))
                    throw new InvalidOperationException("Avatar reset failed");
            }
            finally
            {
                try { Directory.Delete(folder, true); } catch (IOException) { }
            }
        }
    }
}
