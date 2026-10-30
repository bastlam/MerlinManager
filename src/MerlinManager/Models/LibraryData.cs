using MerlinManager.Services;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using System.Windows.Media;

namespace MerlinManager.Models;

public sealed class LibraryData
{
    public const string CustomItemIdPrefix = "999999";
    public FolderNode RootFolder { get; set; } = new();

    public LibraryData()
    {
        RootFolder = CreateRoot;
        RootFolder.InitializeRuntimeState();
    }

    public static FolderNode CreateRoot => new()
    {
        Name = "Root"
    };
}

public sealed class FolderNode : INotifyPropertyChanged
{
    private bool _runtimeInitialized;

    private string _id = string.Empty;
    public string Id
    {
        get => _id;
        set
        {
            if (_id == value)
            {
                return;
            }

            _id = value;
            OnPropertyChanged();
        }
    }
    private string _name = string.Empty;
    public string Name
    {
        get => _name;
        set
        {
            if (_name == value)
            {
                return;
            }

            _name = value;
            OnPropertyChanged();
        }
    }

    private ImageSource _pictureSource;
    
    [JsonIgnore]
    public ImageSource PictureSource
    {
        get => _pictureSource;
        set
        {
            if (Equals(_pictureSource, value))
            {
                return;
            }

            _pictureSource = value;
            OnPropertyChanged();
        }
    }
    public ObservableCollection<FolderNode> Subfolders { get; set; } = new();
    public ObservableCollection<ItemNode> Items { get; set; } = new();

    [JsonIgnore]
    public ObservableCollection<object> Children { get; } = new();

    [JsonIgnore]
    public string WorkingDirectory { get; set; } = LibraryStorageService.DataDirectory;

    [JsonIgnore]
    public string PicturePath { get => string.IsNullOrWhiteSpace(Id) ? null : LibraryStorageService.GetPictureFilePath(Id,WorkingDirectory); }

    public event PropertyChangedEventHandler PropertyChanged;

    public void InitializeRuntimeState()
    {
        Subfolders ??= new ObservableCollection<FolderNode>();
        Items ??= new ObservableCollection<ItemNode>();

        if (!_runtimeInitialized)
        {
            Subfolders.CollectionChanged += OnFolderCollectionsChanged;
            Items.CollectionChanged += OnFolderCollectionsChanged;
            _runtimeInitialized = true;
        }

        foreach (var subfolder in Subfolders)
        {
            subfolder.InitializeRuntimeState();
        }

        SyncChildren();
    }

    public void SyncChildren()
    {
        Children.Clear();

        foreach (var subfolder in Subfolders)
        {
            Children.Add(subfolder);
        }

        foreach (var item in Items)
        {
            Children.Add(item);
        }
    }

    private void OnFolderCollectionsChanged(object sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems is not null)
        {
            foreach (var added in e.NewItems)
            {
                if (added is FolderNode folder)
                {
                    folder.InitializeRuntimeState();
                }
            }
        }

        SyncChildren();
    }

    private void OnPropertyChanged([CallerMemberName]string propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

public sealed class ItemNode :INotifyPropertyChanged
{
    private string _id = string.Empty;
    public string Id
    {
        get => _id;
        set
        {
            if (_id == value)
            {
                return;
            }

            _id = value;
            OnPropertyChanged();
        }
    }
    private string _name = string.Empty;
    public string Name
    {
        get => _name;
        set
        {
            if (_name == value)
            {
                return;
            }

            _name = value;
            OnPropertyChanged();
        }
    }

    [JsonIgnore]
    public string WorkingDirectory { get; set; } = LibraryStorageService.DataDirectory;

    [JsonIgnore]
    public string PicturePath { get => string.IsNullOrWhiteSpace(Id) ? null : LibraryStorageService.GetPictureFilePath(Id, WorkingDirectory); }

    [JsonIgnore]
    public string AudioPath { get => string.IsNullOrWhiteSpace(Id) ? null : LibraryStorageService.GetAudioFilePath(Id, WorkingDirectory); }
    private ImageSource _pictureSource;

    [JsonIgnore]
    public ImageSource PictureSource
    {
        get => _pictureSource;
        set
        {
            if (Equals(_pictureSource, value))
            {
                return;
            }

            _pictureSource = value;
            OnPropertyChanged();
        }
    }

    public event PropertyChangedEventHandler PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
