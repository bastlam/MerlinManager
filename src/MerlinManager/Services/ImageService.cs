using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace MerlinManager.Services;

public static class ImageService
{
    private const int TargetSize = 128;

    public static Task ImportImageAsync(string sourcePath, string id, string dataDirectory = null)
    {
        return Task.Run(() =>
        {
            if(dataDirectory == null)
            {
                dataDirectory = LibraryStorageService.DataDirectory;
            }

            var destinationFileName = $"{id}.jpg";
            var destinationPath = Path.Combine(dataDirectory, destinationFileName);

            var source = new BitmapImage();
            source.BeginInit();
            source.CacheOption = BitmapCacheOption.OnLoad;
            source.UriSource = new Uri(sourcePath, UriKind.Absolute);
            source.EndInit();
            source.Freeze();

            var scale = Math.Max((double)TargetSize / source.PixelWidth, (double)TargetSize / source.PixelHeight);
            var scaledWidth = source.PixelWidth * scale;
            var scaledHeight = source.PixelHeight * scale;

            var offsetX = (TargetSize - scaledWidth) / 2;
            var offsetY = (TargetSize - scaledHeight) / 2;

            var visual = new DrawingVisual();
            using (var context = visual.RenderOpen())
            {
                context.DrawImage(source, new Rect(offsetX, offsetY, scaledWidth, scaledHeight));
            }

            var target = new RenderTargetBitmap(TargetSize, TargetSize, 96, 96, PixelFormats.Pbgra32);
            target.Render(visual);
            target.Freeze();

            var encoder = new JpegBitmapEncoder
            {
                QualityLevel = 90
            };
            encoder.Frames.Add(BitmapFrame.Create(target));

            using var stream = File.Create(destinationPath);
            encoder.Save(stream);
        });
    }

    public static void DeleteImageIfExists(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        if (!File.Exists(path))
        {
            return;
        }

        const int maxAttempts = 3;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                File.Delete(path);
                return;
            }
            catch (IOException) when (attempt < maxAttempts)
            {
                Thread.Sleep(100 * attempt);
            }
        }
    }
}
