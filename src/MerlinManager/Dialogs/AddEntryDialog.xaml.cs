using MerlinManager.Services;
using Microsoft.Win32;
using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;

namespace MerlinManager.Dialogs;

public partial class AddEntryDialog : Window
{
    private readonly bool _hasInitialImage;
    private string _selectedImagePath;
    private string _selectedAudioPath;
    private string _selectedYoutubeUrl;
    private BitmapImage _previewBitmap;

    public string EntryName => NameTextBox.Text.Trim();
    public string EntryId => IdTextBox.Text.Trim();
    public string SelectedImagePath => _selectedImagePath;
    public string SelectedAudioPath => _selectedAudioPath;
    public bool RemoveImageRequested { get; private set; }
    public bool HasNewImageSelection => !string.IsNullOrWhiteSpace(_selectedImagePath);
    public bool HasNewAudioSelection => !string.IsNullOrWhiteSpace(_selectedAudioPath) || !string.IsNullOrWhiteSpace(_selectedYoutubeUrl);
    public string SelectedYoutubeUrl => _selectedYoutubeUrl;

    public AddEntryDialog(
        string title,
        string defaultId,
        string defaultName = "",
        string existingImagePath = null,
        bool allowAudioSelection = false)
    {
        InitializeComponent();
        Title = title;
        IdTextBox.Text = defaultId;
        NameTextBox.Text = defaultName;

        AudioSection.Visibility = allowAudioSelection ? Visibility.Visible : Visibility.Collapsed;

        _hasInitialImage = !string.IsNullOrWhiteSpace(existingImagePath);

        SetPreviewImage(existingImagePath);
    }

    ~AddEntryDialog()
    {
        _previewBitmap?.StreamSource?.Dispose();
    }

    private void RadioAudioSource_Checked(object sender, RoutedEventArgs e)
    {
        if (AudioFileRow == null) return;
        bool isLocal = RadioLocalFile.IsChecked == true;
        AudioFileRow.Visibility = isLocal ? Visibility.Visible : Visibility.Collapsed;
        AudioYoutubeRow.Visibility = isLocal ? Visibility.Collapsed : Visibility.Visible;
    }

    private async void LoadYoutubeInfosButton_Click(object sender, RoutedEventArgs e)
    {
        LoadingOverlay.Visibility = Visibility.Visible;

        var youtubeInfos = await AudioService.GetYoutubeInfosAsync(YoutubeUrlTextBox.Text.Trim(), EntryId);

        LoadingOverlay.Visibility = Visibility.Collapsed;

        if (youtubeInfos == null) return;

        NameTextBox.Text = youtubeInfos.Title;

        if (!string.IsNullOrWhiteSpace(youtubeInfos.ThumbnailPath) && File.Exists(youtubeInfos.ThumbnailPath))
        {
            _selectedImagePath = youtubeInfos.ThumbnailPath;
            RemoveImageRequested = false;
            SetPreviewImage(_selectedImagePath);
        }
    }

    private void ClearYoutubeButton_Click(object sender, RoutedEventArgs e)
    {
        YoutubeUrlTextBox.Text = string.Empty;
        _selectedYoutubeUrl = null;
    }

    private void BrowseButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Select image",
            Filter = "Image files|*.jpg;*.jpeg;*.png;*.bmp;*.gif;*.tif;*.tiff|All files|*.*"
        };

        if (dialog.ShowDialog(this) == true)
        {
            _selectedImagePath = dialog.FileName;
            RemoveImageRequested = false;
            SetPreviewImage(_selectedImagePath);
        }
    }

    private void ClearImageButton_Click(object sender, RoutedEventArgs e)
    {
        _selectedImagePath = null;
        RemoveImageRequested = _hasInitialImage;
        SetPreviewImage(null);
    }

    private void BrowseAudioButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Select audio",
            Filter = "Audio files|*.mp3;*.wav;*.wma;*.aac;*.m4a;*.flac;*.ogg|All files|*.*"
        };

        if (dialog.ShowDialog(this) == true)
        {
            _selectedAudioPath = dialog.FileName;
            SetAudioPathDisplay(_selectedAudioPath);
        }
    }

    private void ClearAudioButton_Click(object sender, RoutedEventArgs e)
    {
        _selectedAudioPath = null;
        SetAudioPathDisplay(null);
    }

    private void OkButton_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(EntryName))
        {
            MessageBox.Show(this, "Le titre est obligatoire.", "Validation", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (string.IsNullOrWhiteSpace(EntryId))
        {
            MessageBox.Show(this, "ID est obligatoire.", "Validation", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (AudioSection.Visibility == Visibility.Visible && RadioYoutube.IsChecked == true)
        {
            _selectedYoutubeUrl = YoutubeUrlTextBox.Text.Trim();
            _selectedAudioPath = null;
        }
        else
        {
            _selectedYoutubeUrl = null;
        }

        DialogResult = true;
        Close();
    }

    private void SetAudioPathDisplay(string audioPath)
    {
        if (string.IsNullOrWhiteSpace(NameTextBox.Text))
        {
            NameTextBox.Text = string.IsNullOrWhiteSpace(audioPath) ? "" : Path.GetFileNameWithoutExtension(audioPath);
        }
        AudioPathTextBox.Text = string.IsNullOrWhiteSpace(audioPath) ? "Aucun audio" : audioPath;
    }

    private void SetPreviewImage(string imagePath)
    {
        if (string.IsNullOrWhiteSpace(imagePath) || !File.Exists(imagePath))
        {
            PreviewImage.Source = null;
            NoImageText.Visibility = Visibility.Visible;
            return;
        }

        var absolutePath = Path.GetFullPath(imagePath);

        PreviewImage.Source = null;

        _previewBitmap = new BitmapImage();
        _previewBitmap.BeginInit();
        _previewBitmap.CacheOption = BitmapCacheOption.OnLoad;
        _previewBitmap.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
        _previewBitmap.UriSource = new Uri(absolutePath, UriKind.Absolute);
        _previewBitmap.EndInit();
        _previewBitmap.Freeze();

        PreviewImage.Source = _previewBitmap;
        NoImageText.Visibility = Visibility.Collapsed;
    }
}
