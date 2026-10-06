using MerlinManager.Models;
using NAudio.Wave;
using System.IO;
using System.Net.Http;
using YoutubeExplode;
using YoutubeExplode.Common;
using YoutubeExplode.Converter;
using YoutubeExplode.Videos.Streams;

namespace MerlinManager.Services;

public static class AudioService
{
    private const int Mp3Bitrate = 128000;

    public static async Task<YoutubeInfos> GetYoutubeInfosAsync(string youtubeUrl, string id)
    {
        try
        {
            using var youtube = new YoutubeClient();
            var video = await youtube.Videos.GetAsync(youtubeUrl);

            var thumbnail = video.Thumbnails?.GetWithHighestResolution();
            string thumbnailPath = null;

            //Download thumbnail to local file if it exists
            if (thumbnail != null)
            {
                thumbnailPath = Path.Combine(LibraryStorageService.DataDirectory, "temp", $"{id}.jpg");
                //create temp directory if it doesn't exist
                Directory.CreateDirectory(Path.Combine(LibraryStorageService.DataDirectory, "temp"));

                using var client = new HttpClient();
                var thumbnailData = await client.GetByteArrayAsync(thumbnail.Url);
                await File.WriteAllBytesAsync(thumbnailPath, thumbnailData);
            }

            return new YoutubeInfos
            {
                Title = video.Title,
                ThumbnailPath = thumbnailPath
            };
        }
        catch { }
        return null;
    }

    public static void ClearTempFiles(string id)
    {
        var tempDir = Path.Combine(LibraryStorageService.DataDirectory, "temp");
        if (Directory.Exists(tempDir))
        {
            try
            {
                // Delete all files in the temp directory that match the pattern "{id}.*"
                var files = Directory.GetFiles(tempDir, $"{id}.*");
                foreach (var file in files)
                {
                    File.Delete(file);
                }
            }
            catch (IOException)
            {
                // Ignore exceptions during cleanup
            }
        }
    }

    public static async Task<bool> ImportFromYoutubeAsync(string youtubeUrl, string id, Action<TransfertStatus> transfertStatusAction = null)
    {
        YoutubeTransfert progress = null;

        try
        {
            if (transfertStatusAction != null)
            {
                progress = new YoutubeTransfert(transfertStatusAction);
                await Task.Delay(250); // Allow UI to update before starting the download
            }

            using var youtube = new YoutubeClient();

            // Get stream manifest
            var streamManifest = await youtube.Videos.Streams.GetManifestAsync(youtubeUrl);

            // Select best audio stream (highest bitrate)
            var audioStreamInfo = streamManifest
                .GetAudioStreams()
                .Where(s => s is AudioOnlyStreamInfo) // Filter for MP4 audio streams
                .GetWithHighestBitrate();

            var destinationFileName = $"{id}.mp3";
            var tempAudioFileName = Path.Combine(LibraryStorageService.DataDirectory, "temp", destinationFileName);

            //create temp directory if it doesn't exist
            Directory.CreateDirectory(Path.Combine(LibraryStorageService.DataDirectory, "temp"));

            // Download and mux streams into a single file
            await youtube.Videos.DownloadAsync(
                [audioStreamInfo],
                new ConversionRequestBuilder(tempAudioFileName).Build(),
                progress
            );

            // Convert to MP3
            await ImportAudioAsync(tempAudioFileName, id);

            // Clean up temp file
            File.Delete(tempAudioFileName);

            progress?.Complete(true);

            return true;
        }
        catch (Exception ex)
        {
            progress?.Complete(false, ex.Message);
            return false;
        }
    }

    public static Task<bool> ImportAudioAsync(string sourcePath, string id)
    {
        return Task.Run(() =>
        {
            try
            {
                var destinationFileName = $"{id}.mp3";
                var destinationPath = Path.Combine(LibraryStorageService.DataDirectory, destinationFileName);

                using var reader = new MediaFoundationReader(sourcePath);
                MediaFoundationEncoder.EncodeToMp3(reader, destinationPath, Mp3Bitrate);
                return true;
            }
            catch
            {
                return false;
            }
        });
    }

    public static void DeleteAudioIfExists(string path)
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

public class YoutubeTransfert : IProgress<double>
{
    private Action<TransfertStatus> _transfertStatusAction;
    private TransfertStatus _transfertStatus;

    public YoutubeTransfert(Action<TransfertStatus> transfertStatusAction)
    {
        _transfertStatusAction = transfertStatusAction;
        _transfertStatus = new TransfertStatus
        {
            DialogTitle = "Téléchargement en cours...",
            AutoClose = true,
            CurrentAction = "",
            CurrentText = " %",
            Current = 0,
            Total = 100
        };

        Fire();
    }

    public void Complete(bool success, string errorMessage = null)
    {
        _transfertStatus.TransfertCompleted = true;
        _transfertStatus.TransfertSucceeded = success;
        _transfertStatus.TransfertError = errorMessage;
        Fire();
    }

    public void Report(double value)
    {
        _transfertStatus.Current = (int)(value * 100);
        Fire();
    }

    public void Fire()
    {
        _transfertStatusAction(_transfertStatus);
    }
}