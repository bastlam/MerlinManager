using MerlinManager.Dialogs;
using MerlinManager.Models;
using MerlinManager.Services;
using MerlinManager.Services.Speaker;
using MerlinManager.Utils;
using Microsoft.Win32;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace MerlinManager;

public partial class MainWindow : Window
{
    private readonly LibraryStorageService _storageService = new();
    private TransfertDialog _transfertDialog;
    private string _merlinUsername;

    public System.Collections.ObjectModel.ObservableCollection<FolderNode> Folders { get; } = new();

    public MainWindow()
    {
        InitializeComponent();
        DataContext = this;
        LoadData();
        RefreshSelectionDetails(null);

        var version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
        VersionText.Text = $"v{version.Major}.{version.Minor}.{version.Build}.{version.Revision}";
    }

    private void LoadData()
    {
        var data = _storageService.Load();
        InitializeFoldersView(data.RootFolder.Subfolders);
    }

    private void InitializeFoldersView(ICollection<FolderNode> folders)
    {
        Folders.Clear();

        foreach (var folder in folders)
        {
            InitializeFolderHierarchy(folder);
            Folders.Add(folder);
        }
    }

    private void InitializeFolderHierarchy(FolderNode folder)
    {
        folder.InitializeRuntimeState();
        folder.PictureSource = LoadImageSource(folder.Id);

        foreach (var item in folder.Items)
        {
            item.PictureSource = LoadImageSource(item.Id);
        }

        foreach (var subfolder in folder.Subfolders)
        {
            InitializeFolderHierarchy(subfolder);
        }

        folder.SyncChildren();
    }

    private void SaveData()
    {
        _storageService.Save(Folders);
    }



    #region Menu Handlers
    private async void InitializeFoldersMenuItem_Click(object sender, RoutedEventArgs e)
    {
        //Sélection d'un fichier "playlist.bin" sur le disque
        var dialog = new OpenFileDialog
        {
            Title = "Choisissez le fichier de playlist",
            Filter = "Playlist files|*.bin|All files|*.*"
        };

        if (dialog.ShowDialog(this) == true)
        {
            await ImportPlaylistFileAsync(dialog.FileName);
        }
    }

    private async Task ImportPlaylistFileAsync(string playlistPath)
    {
        //Récupération des données de la playlist sélectionnée
        var playlistRootNode = MerlinPlaylistService.ExtractFoldersTree(playlistPath);

        //On merge les dossiers extraits avec ceux déjà présents dans l'application
        var rootFolder = LibraryData.CreateRoot;
        rootFolder.Subfolders = new System.Collections.ObjectModel.ObservableCollection<FolderNode>(Folders);
        rootFolder = await rootFolder.MergeAsync(playlistRootNode);

        InitializeFoldersView(rootFolder.Subfolders);

        SaveData();
    }

    private async void SpeakerWifiTestMenuItem_Click(object sender, RoutedEventArgs e)
    {
        var ssid = WifiService.SavedSpeakerSsid;
        if (!string.IsNullOrEmpty(ssid))
        {
            var reuse = MessageBox.Show(this, $"Utiliser le Wi-Fi enregistré « {ssid} » ?\n\nNon : se reconnecter au compte Merlin pour récupérer à nouveau les identifiants Wi-Fi.",
                "Wi-Fi de l'enceinte", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (reuse == MessageBoxResult.Cancel)
            {
                return;
            }
            if (reuse == MessageBoxResult.No)
            {
                ssid = null;
            }
        }

        if (string.IsNullOrEmpty(ssid))
        {
            ssid = await FetchSpeakerWifiFromApiAsync();
            if (string.IsNullOrEmpty(ssid))
            {
                return;
            }
        }

        if (MessageBox.Show(this, "Mettez l'enceinte en mode Wifi / Transfert (« Connexion Wifi en cours » affiché sur l'écran), puis validez.\n\nLe PC sera déconnecté d'internet pendant l'opération.",
            "Connexion à l'enceinte", MessageBoxButton.OKCancel, MessageBoxImage.Information) != MessageBoxResult.OK)
        {
            return;
        }

        var downloadedPlaylistPath = Path.Combine(LibraryStorageService.DataDirectory, "speaker-playlist.bin");
        string summary;
        try
        {
            summary = await Task.Run(() => ReadSpeakerAsync(ssid, downloadedPlaylistPath));
        }
        catch (Exception ex)
        {
            TransfertProgress(new TransfertStatus { DialogTitle = "Connexion à l'enceinte", TransfertCompleted = true, TransfertError = ex.Message });
            return;
        }

        if (MessageBox.Show(this, $"{summary}\n\nImporter la playlist de l'enceinte dans la bibliothèque ?", "Enceinte Merlin",
            MessageBoxButton.YesNo, MessageBoxImage.Information) == MessageBoxResult.Yes)
        {
            await ImportPlaylistFileAsync(downloadedPlaylistPath);
        }
    }

    /// <summary>
    /// Demande les identifiants Merlin, récupère le SSID/mot de passe du point d'accès de l'enceinte via l'API
    /// et enregistre le profil Wi-Fi dans Windows. Renvoie le SSID, ou null si annulé.
    /// </summary>
    private async Task<string> FetchSpeakerWifiFromApiAsync()
    {
        var loginDialog = new MerlinLoginDialog(_merlinUsername) { Owner = this };
        if (loginDialog.ShowDialog() != true)
        {
            return null;
        }
        _merlinUsername = loginDialog.Username;

        try
        {
            using var api = new MerlinApiClient();
            await api.LoginAsync(loginDialog.Username, loginDialog.Password);
            var synchro = await api.CreateSynchroAsync();

            try
            {
                await api.CloseSynchroAsync(synchro.Id);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Clôture de la synchro {synchro.Id} impossible : {ex.Message}");
            }

            if (string.IsNullOrEmpty(synchro.Ssid) || string.IsNullOrEmpty(synchro.Password))
            {
                MessageBox.Show(this, "L'API n'a pas renvoyé d'identifiants Wi-Fi. L'enceinte est-elle bien associée à ce compte ?", "Compte Merlin", MessageBoxButton.OK, MessageBoxImage.Warning);
                return null;
            }

            // Playlist au format JSON telle que fournie par le serveur, conservée pour étudier le format attendu par l'enceinte
            if (!string.IsNullOrEmpty(synchro.PlaylistText))
            {
                await File.WriteAllTextAsync(Path.Combine(LibraryStorageService.DataDirectory, "api-playlist.json"), synchro.PlaylistText);
            }

            await WifiService.AddProfileAsync(synchro.Ssid, synchro.Password);
            WifiService.SavedSpeakerSsid = synchro.Ssid;
            return synchro.Ssid;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Compte Merlin", MessageBoxButton.OK, MessageBoxImage.Error);
            return null;
        }
    }

    /// <summary>
    /// Test en lecture seule : connexion au Wi-Fi de l'enceinte, lecture des informations et téléchargement de playlist.bin.
    /// </summary>
    private async Task<string> ReadSpeakerAsync(string ssid, string playlistDestinationPath)
    {
        const string dialogTitle = "Connexion à l'enceinte";
        void Report(string action) => TransfertProgress(new TransfertStatus { DialogTitle = dialogTitle, AutoClose = true, CurrentAction = action });

        Report("Connexion au Wi-Fi de l'enceinte...");
        var previousSsid = await WifiService.GetConnectedSsidAsync();
        string summary;
        try
        {
            await WifiService.ConnectToSpeakerAsync(ssid);

            using var speaker = new MerlinSpeakerClient();
            Report("Connexion à l'enceinte...");
            await speaker.ConnectAsync();
            await speaker.PingAsync();

            Report("Lecture des informations...");
            var firmware = await speaker.GetFirmwareVersionAsync();
            var available = await speaker.GetAvailableSpaceAsync();
            var total = await speaker.GetTotalSizeAsync();

            Report("Téléchargement de playlist.bin...");
            long playlistSize;
            await using (var file = File.Create(playlistDestinationPath))
            {
                await speaker.GetFileAsync("playlist.bin", file);
                playlistSize = file.Length;
            }

            summary = $"Firmware : {firmware}\nEspace libre : {available}\nTaille totale : {total}\nplaylist.bin : {playlistSize} octets";
        }
        finally
        {
            Report("Reconnexion au Wi-Fi précédent...");
            await WifiService.ReconnectAsync(previousSsid);
        }

        TransfertProgress(new TransfertStatus { DialogTitle = dialogTitle, AutoClose = true, TransfertCompleted = true, TransfertSucceeded = true });
        return summary;
    }

    private void QuitMenuItem_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void SelectStorageFolderMenuItem_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            DefaultDirectory = LibraryStorageService.DataDirectory,
            Title = "Sélectionnez le dossier de stockage des données"
        };

        if (dialog.ShowDialog(this) != true || string.IsNullOrWhiteSpace(dialog.FolderName))
        {
            return;
        }

        var selectedPath = Path.GetFullPath(dialog.FolderName);
        if (string.Equals(selectedPath, LibraryStorageService.DataDirectory, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        LibraryStorageService.UpdateDataDirectory(selectedPath);
        LoadData();
        RefreshSelectionDetails(null);
        LibraryTreeView.Items.Refresh();
    }
    #endregion

    #region Button Handlers
    private async void AddFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new AddEntryDialog("Ajouter un dossier", GenerateDefaultId())
        {
            Owner = this
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        if (!ValidateId(dialog.EntryId))
        {
            return;
        }

        if (IdExists(dialog.EntryId))
        {
            MessageBox.Show(this, "Cet ID existe déjà.", "Validation", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            if (dialog.HasNewImageSelection && dialog.SelectedImagePath is not null)
            {
                await ImageService.ImportImageAsync(dialog.SelectedImagePath, dialog.EntryId);
            }

            var folder = new FolderNode
            {
                Id = dialog.EntryId,
                Name = dialog.EntryName,
                PictureSource = LoadImageSource(dialog.EntryId)
            };

            folder.InitializeRuntimeState();
            Folders.Add(folder);
            SaveData();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Impossible d'ajouter le dossier: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void AddItem_Click(object sender, RoutedEventArgs e)
    {
        var targetFolder = GetSelectedFolderOrParent();
        if (targetFolder is null)
        {
            MessageBox.Show(this, "Choisir d'abord un dossier ou un élément.", "Ajouter", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dialog = new AddEntryDialog("Ajouter", GenerateDefaultId(), allowAudioSelection: true)
        {
            Owner = this
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        if (!ValidateId(dialog.EntryId))
        {
            return;
        }

        if (IdExists(dialog.EntryId))
        {
            MessageBox.Show(this, "Cet ID existe déjà.", "Validation", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            if (dialog.HasNewImageSelection && dialog.SelectedImagePath is not null)
            {
                await ImageService.ImportImageAsync(dialog.SelectedImagePath, dialog.EntryId);
            }

            var item = new ItemNode
            {
                Id = dialog.EntryId,
                Name = dialog.EntryName,
                PictureSource = LoadImageSource(dialog.EntryId)
            };

            if (dialog.HasNewAudioSelection)
            {
                var selectedAudioPath = dialog.SelectedAudioPath;
                var selectedYoutubeUrl = dialog.SelectedYoutubeUrl;
                var entryId = dialog.EntryId;
                var importSuccess = false;

                _ = Task.Run(async () =>
                {

                    if (selectedAudioPath != null)
                    {
                        importSuccess = await AudioService.ImportAudioAsync(selectedAudioPath, entryId);
                    }
                    else if (selectedYoutubeUrl != null)
                    {
                        importSuccess = await AudioService.ImportFromYoutubeAsync(selectedYoutubeUrl, entryId, (transfertStatus) => TransfertProgress(transfertStatus));
                        AudioService.ClearTempFiles(entryId);
                    }
                    else
                    {
                        importSuccess = true;
                    }

                    if (importSuccess)
                    {
                        Dispatcher.Invoke(() =>
                        {
                            targetFolder.Items.Add(item);
                            SaveData();
                        });
                    }

                });
            }
            else
            {
                targetFolder.Items.Add(item);
                SaveData();
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Impossible d'ajouter l'élément: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void AddSubfolder_Click(object sender, RoutedEventArgs e)
    {
        var parentFolder = GetSelectedFolderOrParent();
        if (parentFolder is null)
        {
            MessageBox.Show(this, "Choisir d'abord un dossier parent ou un élément.", "Sous-dossier", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dialog = new AddEntryDialog("Sous-dossier", GenerateDefaultId())
        {
            Owner = this
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        if (!ValidateId(dialog.EntryId))
        {
            return;
        }

        if (IdExists(dialog.EntryId))
        {
            MessageBox.Show(this, "Cet ID existe déjà.", "Validation", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            if (dialog.HasNewImageSelection && dialog.SelectedImagePath is not null)
            {
                await ImageService.ImportImageAsync(dialog.SelectedImagePath, dialog.EntryId);
            }

            var subfolder = new FolderNode
            {
                Id = dialog.EntryId,
                Name = dialog.EntryName,
                PictureSource = LoadImageSource(dialog.EntryId)
            };

            subfolder.InitializeRuntimeState();
            parentFolder.Subfolders.Add(subfolder);
            SaveData();
            LibraryTreeView.Items.Refresh();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Impossible d'ajouter le sous-dossier: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void MoveItem_Click(object sender, RoutedEventArgs e)
    {
        if (LibraryTreeView.SelectedItem is not ItemNode item)
        {
            MessageBox.Show(this, "Choisir l'élément à déplacer.", "Déplacement", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var sourceFolder = FindParentFolder(item);
        if (sourceFolder is null)
        {
            MessageBox.Show(this, "Dossier source introuvable.", "Déplacement", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (EnumerateAllFolders().Count() < 2)
        {
            MessageBox.Show(this, "Il faut au moins 2 dossiers pour déplacer un élément.", "Déplacement", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dialog = new MoveItemDialog(EnumerateAllFolders(), sourceFolder)
        {
            Owner = this
        };

        if (dialog.ShowDialog() != true || dialog.SelectedFolder is null)
        {
            return;
        }

        sourceFolder.Items.Remove(item);
        dialog.SelectedFolder.Items.Add(item);
        SaveData();
    }

    private async void EditSelected_Click(object sender, RoutedEventArgs e)
    {
        switch (LibraryTreeView.SelectedItem)
        {
            case FolderNode folder:
                await EditFolderAsync(folder);
                break;
            case ItemNode item:
                await EditItemAsync(item);
                break;
            default:
                MessageBox.Show(this, "Choisir un dossier ou élément à modifier.", "Modifier", MessageBoxButton.OK, MessageBoxImage.Information);
                break;
        }
    }

    private void Send_Click(object sender, RoutedEventArgs e)
    {
        //Sélection d'un fichier "playlist.bin" sur le disque
        var dialog = new OpenFileDialog
        {
            Title = "Select playlist file",
            Filter = "Playlist files|*.bin|All files|*.*"
        };

        if (dialog.ShowDialog(this) == true)
        {
            //Confirmation avant d'envoyer la playlist

            if (MessageBox.Show(this, "Ecrire la playlist ? Cela écrasera la playlist personnalisée actuellement présente.", "Confirmer l'écriture", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            {
                return;
            }

            var selectedPlaylistPath = dialog.FileName;

            var libraryData = new LibraryData();
            libraryData.RootFolder.Subfolders = new System.Collections.ObjectModel.ObservableCollection<FolderNode>(Folders);


            Task.Run(() => MerlinPlaylistService.ConvertToMerlinPlaylistAsync(libraryData, selectedPlaylistPath, (transfertStatus) => TransfertProgress(transfertStatus)));
        }
    }

    private void TransfertProgress(TransfertStatus status)
    {
        Dispatcher.Invoke(() =>
        {
            if (_transfertDialog is null || !_transfertDialog.IsLoaded)
            {
                _transfertDialog = new TransfertDialog(status.DialogTitle, status.AutoClose)
                {
                    Owner = this
                };
                IsEnabled = false;
                _transfertDialog.Closed += (_, _) =>
                {
                    _transfertDialog = null;
                    IsEnabled = true;
                };
                _transfertDialog.Show();
            }

            _transfertDialog.UpdateStatus(status);
        });
    }

    #endregion

    #region Editing Methods
    private async Task EditFolderAsync(FolderNode folder)
    {
        var dialog = new AddEntryDialog(
            title: "Modifier dossier",
            defaultId: folder.Id,
            defaultName: folder.Name)
        {
            Owner = this
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        var newId = dialog.EntryId;
        var currentId = folder.Id;

        if (!ValidateId(newId))
        {
            return;
        }

        if (!string.Equals(newId, currentId, StringComparison.OrdinalIgnoreCase) && IdExists(newId))
        {
            MessageBox.Show(this, "Cet ID existe déjà.", "Validation", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            folder.Id = newId;
            folder.Name = dialog.EntryName;
            await ApplyImageChangesAsync(folder.Id, folder, dialog);
            SaveData();
            RefreshSelectionDetails(folder);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Impossible de modifier le dossier: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async Task EditItemAsync(ItemNode item)
    {
        var dialog = new AddEntryDialog(
            title: "Modifier élément",
            defaultId: item.Id,
            defaultName: item.Name,
            existingImagePath: item.PicturePath,
            allowAudioSelection: false)
        {
            Owner = this
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        var newId = dialog.EntryId;
        var currentId = item.Id;

        if (!ValidateId(newId))
        {
            return;
        }

        if (!string.Equals(newId, currentId, StringComparison.OrdinalIgnoreCase) && IdExists(newId))
        {
            MessageBox.Show(this, "Cet ID existe déjà.", "Validation", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            item.Id = newId;
            item.Name = dialog.EntryName;
            await ApplyImageChangesAsync(item.Id, item, dialog);

            SaveData();
            RefreshSelectionDetails(item);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Impossible de modifier l'élément: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async Task ApplyImageChangesAsync(string id, FolderNode folder, AddEntryDialog dialog)
    {
        if (dialog.RemoveImageRequested)
        {
            ImageService.DeleteImageIfExists(folder.PicturePath);
            folder.PictureSource = null;
            return;
        }

        if (!dialog.HasNewImageSelection || dialog.SelectedImagePath is null)
        {
            return;
        }


        await ImageService.ImportImageAsync(dialog.SelectedImagePath, id);

        folder.PictureSource = LoadImageSource(folder.Id);
    }

    private async Task ApplyImageChangesAsync(string id, ItemNode item, AddEntryDialog dialog)
    {
        if (dialog.RemoveImageRequested)
        {
            ImageService.DeleteImageIfExists(item.PicturePath);
            item.PictureSource = null;
            return;
        }

        if (!dialog.HasNewImageSelection || dialog.SelectedImagePath is null)
        {
            return;
        }

        await ImageService.ImportImageAsync(dialog.SelectedImagePath, id);

        item.PictureSource = LoadImageSource(item.Id);
    }

    private void DeleteSelected_Click(object sender, RoutedEventArgs e)
    {
        switch (LibraryTreeView.SelectedItem)
        {
            case FolderNode folder:
                DeleteFolder(folder);
                break;
            case ItemNode item:
                DeleteItem(item);
                break;
            default:
                MessageBox.Show(this, "Choisir un dossier ou élément à supprimer.", "Supprimer", MessageBoxButton.OK, MessageBoxImage.Information);
                break;
        }
    }

    private void DeleteFolder(FolderNode folder)
    {
        var result = MessageBox.Show(
            this,
            $"Supprimer le dossier '{folder.Name}' et tous ses éléments ?",
            "Supprimer",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (result != MessageBoxResult.Yes)
        {
            return;
        }

        DeleteFolderImagesRecursive(folder);
        RemoveFolderFromTree(folder);
        SaveData();
        RefreshSelectionDetails(null);
        LibraryTreeView.Items.Refresh();
    }

    private void DeleteFolderImagesRecursive(FolderNode folder)
    {
        foreach (var item in folder.Items)
        {
            ImageService.DeleteImageIfExists(item.PicturePath);
            AudioService.DeleteAudioIfExists(item.AudioPath);
        }

        foreach (var subfolder in folder.Subfolders)
        {
            DeleteFolderImagesRecursive(subfolder);
        }

        ImageService.DeleteImageIfExists(folder.PicturePath);
    }

    private void RemoveFolderFromTree(FolderNode folder)
    {
        var parentFolder = FindParentFolder(folder);
        if (parentFolder is null)
        {
            Folders.Remove(folder);
            return;
        }

        parentFolder.Subfolders.Remove(folder);
    }

    private void DeleteItem(ItemNode item)
    {
        var parentFolder = FindParentFolder(item);
        if (parentFolder is null)
        {
            MessageBox.Show(this, "Impossible de trouver le dossier parent.", "Supprimer", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var result = MessageBox.Show(
            this,
            $"Supprimer l'élément '{item.Name}'?",
            "Supprimer",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (result != MessageBoxResult.Yes)
        {
            return;
        }

        ImageService.DeleteImageIfExists(item.PicturePath);
        AudioService.DeleteAudioIfExists(item.AudioPath);
        parentFolder.Items.Remove(item);
        SaveData();
        RefreshSelectionDetails(null);
    }
    #endregion

    #region Misc Handlers
    private void LibraryTreeView_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        RefreshSelectionDetails(e.NewValue);
    }

    private void RefreshSelectionDetails(object selected)
    {
        string selectedId = null;

        switch (selected)
        {
            case FolderNode folder:
                SelectionTypeText.Text = "Type: 📂";
                SelectionNameText.Text = $"Nom: {folder.Name}";
                SelectionIdText.Text = $"ID: {folder.Id}";
                SelectionImage.Source = folder.PictureSource;
                selectedId = folder.Id;
                break;
            case ItemNode item:
                SelectionTypeText.Text = "Type: 🎵";
                SelectionNameText.Text = $"Nom: {item.Name}";
                SelectionIdText.Text = $"ID: {item.Id}";
                selectedId = item.Id;
                SelectionImage.Source = item.PictureSource;
                break;
            default:
                SelectionTypeText.Text = "Type: -";
                SelectionNameText.Text = "Nom: -";
                SelectionIdText.Text = "ID: -";
                selectedId = null;
                SelectionImage.Source = null;
                break;
        }

        //Si selectedId est null ou ne commence pas par "MM-", on désactive les boutons de modification/suppression et déplacement.
        bool isReadOnlySelection = String.IsNullOrEmpty(selectedId) || !selectedId.StartsWith("MM-", StringComparison.OrdinalIgnoreCase);

        btnDelete.IsEnabled = !isReadOnlySelection;
        btnEdit.IsEnabled = !isReadOnlySelection;
        btnMove.IsEnabled = !isReadOnlySelection;
    }

    private ImageSource LoadImageSource(string id)
    {
        var imagePath = LibraryStorageService.GetPictureFilePath(id);

        if (string.IsNullOrWhiteSpace(imagePath) || !File.Exists(imagePath))
        {
            return null;
        }

        using var fs = new FileStream(imagePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var ms = new MemoryStream();
        fs.CopyTo(ms);
        ms.Position = 0;

        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = ms;
        image.EndInit();
        image.Freeze();
        return image;
    }

    private FolderNode GetSelectedFolderOrParent()
    {
        return LibraryTreeView.SelectedItem switch
        {
            FolderNode folder => folder,
            ItemNode item => FindParentFolder(item),
            _ => null
        };
    }

    private FolderNode FindParentFolder(ItemNode item)
    {
        foreach (var folder in Folders)
        {
            var parent = FindParentFolderRecursive(folder, item);
            if (parent is not null)
            {
                return parent;
            }
        }

        return null;
    }

    private FolderNode FindParentFolder(FolderNode targetFolder)
    {
        foreach (var root in Folders)
        {
            var parent = FindParentOfFolderRecursive(root, targetFolder);
            if (parent is not null)
            {
                return parent;
            }
        }

        return null;
    }

    private FolderNode FindParentFolderRecursive(FolderNode current, ItemNode item)
    {
        if (current.Items.Contains(item))
        {
            return current;
        }

        foreach (var child in current.Subfolders)
        {
            var parent = FindParentFolderRecursive(child, item);
            if (parent is not null)
            {
                return parent;
            }
        }

        return null;
    }

    private FolderNode FindParentOfFolderRecursive(FolderNode current, FolderNode target)
    {
        if (current.Subfolders.Contains(target))
        {
            return current;
        }

        foreach (var child in current.Subfolders)
        {
            var parent = FindParentOfFolderRecursive(child, target);
            if (parent is not null)
            {
                return parent;
            }
        }

        return null;
    }

    private bool IdExists(string id)
    {
        return EnumerateAllFolders().Any(folder => folder.Id.Equals(id, StringComparison.OrdinalIgnoreCase))
               || EnumerateAllItems().Any(item => item.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
    }

    private IEnumerable<FolderNode> EnumerateAllFolders()
    {
        foreach (var root in Folders)
        {
            foreach (var folder in EnumerateFoldersRecursive(root))
            {
                yield return folder;
            }
        }
    }

    private IEnumerable<FolderNode> EnumerateFoldersRecursive(FolderNode folder)
    {
        yield return folder;

        foreach (var subfolder in folder.Subfolders)
        {
            foreach (var nested in EnumerateFoldersRecursive(subfolder))
            {
                yield return nested;
            }
        }
    }

    private IEnumerable<ItemNode> EnumerateAllItems()
    {
        foreach (var folder in EnumerateAllFolders())
        {
            foreach (var item in folder.Items)
            {
                yield return item;
            }
        }
    }

    private bool ValidateId(string id)
    {
        if (id.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            MessageBox.Show(this, "ID contients des caractères invalides.", "Validation", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        return true;
    }

    private string GenerateDefaultId() => LibraryData.CustomItemIdPrefix + Guid.NewGuid().ToString();

    #endregion

}