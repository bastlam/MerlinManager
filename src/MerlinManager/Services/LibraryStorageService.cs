using MerlinManager.Models;
using Microsoft.Win32;
using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;

namespace MerlinManager.Services;

public sealed class LibraryStorageService
{
    private const string RegistryRootPath = "Software\\MerlinManager";
    private const string RegistryValueName = "DataDirectory";
    private const string JsonFileName = "tree.json";
    private readonly JsonSerializerOptions _serializerOptions = new()
    {
        WriteIndented = true
    };

    public static string DataDirectory { get; private set; }

    public static string GetAudioFilePath(string id, string workingDirectory = null)
    {
        EnsureDataDirectoryInitialized();
        var audioFilePath = Path.Combine(workingDirectory ?? DataDirectory, $"{id}.mp3");
        if (File.Exists(audioFilePath))
        {
            return audioFilePath;
        }
        return null;
    }
    public static string GetPictureFilePath(string id, string workingDirectory = null)
    {
        EnsureDataDirectoryInitialized();
        var pictureFilePath = Path.Combine(workingDirectory ?? DataDirectory, $"{id}.jpg");
        if (File.Exists(pictureFilePath))
        {
            return pictureFilePath;
        }
        return null;
    }

    public LibraryStorageService()
    {
        DataDirectory = GetDataDirectory();
        Directory.CreateDirectory(DataDirectory);
    }

    public static string GetDefaultDataDirectory()
    {
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MerlinManager", "data");
    }

    public static void UpdateDataDirectory(string dataDirectory)
    {
        if (string.IsNullOrWhiteSpace(dataDirectory))
        {
            return;
        }

        var normalizedPath = Path.GetFullPath(dataDirectory);
        DataDirectory = normalizedPath;
        Directory.CreateDirectory(normalizedPath);
        WriteDataDirectoryToRegistry(normalizedPath);
    }

    public LibraryData Load()
    {
        var jsonPath = GetJsonPath();
        if (!File.Exists(jsonPath))
        {
            return new LibraryData();
        }

        var json = File.ReadAllText(jsonPath);
        var data = JsonSerializer.Deserialize<LibraryData>(json, _serializerOptions) ?? new LibraryData();

        return data;
    }

    public void Save(IEnumerable<FolderNode> folders)
    {
        var data = new LibraryData();
        data.RootFolder.Subfolders = new ObservableCollection<FolderNode>(folders);

        var json = JsonSerializer.Serialize(data, _serializerOptions);
        File.WriteAllText(GetJsonPath(), json);
    }

    private string GetJsonPath()
    {
        EnsureDataDirectoryInitialized();
        return Path.Combine(DataDirectory, JsonFileName);
    }

    private static void EnsureDataDirectoryInitialized()
    {
        if (!string.IsNullOrWhiteSpace(DataDirectory))
        {
            return;
        }

        DataDirectory = GetDataDirectory();
        Directory.CreateDirectory(DataDirectory);
    }

    private static string GetDataDirectory()
    {
        var fromRegistry = ReadDataDirectoryFromRegistry();
        if (!string.IsNullOrWhiteSpace(fromRegistry))
        {
            return fromRegistry;
        }

        var defaultPath = GetDefaultDataDirectory();
        WriteDataDirectoryToRegistry(defaultPath);
        return defaultPath;
    }

    private static string ReadDataDirectoryFromRegistry()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RegistryRootPath);
        return key?.GetValue(RegistryValueName) as string;
    }

    private static void WriteDataDirectoryToRegistry(string dataDirectory)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RegistryRootPath);
        key?.SetValue(RegistryValueName, dataDirectory, RegistryValueKind.String);
    }
}
