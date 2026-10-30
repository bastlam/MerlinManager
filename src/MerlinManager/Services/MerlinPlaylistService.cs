using MerlinManager.Models;
using System.Collections.ObjectModel;
using System.IO;
using System.Text;

namespace MerlinManager.Services
{
    public static class MerlinPlaylistService
    {
        /// <summary>
        /// Lit une playlist binaire Merlin depuis un fichier .bin
        /// </summary>
        public static List<MerlinItem> ReadPlaylistFromBin(string filepath)
        {
            using var stream = File.OpenRead(filepath);
            return ReadMerlinPlaylist(stream);
        }

        /// <summary>
        /// Lit le format binaire Merlin depuis un stream
        /// </summary>
        private static List<MerlinItem> ReadMerlinPlaylist(Stream stream)
        {
            var items = new List<MerlinItem>();
            using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);

            try
            {
                while (stream.Position < stream.Length)
                {
                    var item = new MerlinItem();

                    // ID (2 bytes)
                    item.Id = reader.ReadUInt16();

                    // Parent ID (2 bytes)
                    item.ParentId = reader.ReadUInt16();

                    // Order (2 bytes)
                    item.Order = reader.ReadUInt16();

                    // Number of children (2 bytes)
                    item.NbChildren = reader.ReadUInt16();

                    // Favorite order (2 bytes)
                    item.FavOrder = reader.ReadUInt16();

                    // Type (2 bytes)
                    item.Type = reader.ReadUInt16();

                    // Limit time (4 bytes)
                    item.LimitTime = reader.ReadUInt32();

                    // Add time (4 bytes)
                    item.AddTime = reader.ReadUInt32();

                    // UUID (64 bytes: 1 byte length + string + padding)
                    byte uuidLength = reader.ReadByte();
                    byte[] uuidBytes = reader.ReadBytes(uuidLength);
                    item.Uuid = Encoding.UTF8.GetString(uuidBytes);
                    reader.ReadBytes(64 - uuidLength); // Skip padding

                    // Title (66 bytes: 1 byte length + string + padding)
                    byte titleLength = reader.ReadByte();
                    byte[] titleBytes = reader.ReadBytes(titleLength);
                    item.Title = Encoding.UTF8.GetString(titleBytes);
                    reader.ReadBytes(66 - titleLength); // Skip padding

                    items.Add(item);
                }
            }
            catch (EndOfStreamException)
            {
                // Fin normale du fichier
            }

            //var json = System.Text.Json.JsonSerializer.Serialize(items, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
            //File.WriteAllText("playlist_read.json", json);

            return items;
        }

        /// <summary>
        /// Écrit une playlist au format binaire Merlin
        /// </summary>
        public static void WritePlaylistToBin(string filepath, List<MerlinItem> items)
        {
            using var stream = File.Create(filepath);
            WriteMerlinPlaylist(stream, items);
        }

        /// <summary>
        /// Écrit le format binaire Merlin dans un stream
        /// </summary>
        private static void WriteMerlinPlaylist(Stream stream, List<MerlinItem> items)
        {
            using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);

            foreach (var item in items)
            {
                // ID (2 bytes)
                writer.Write((ushort)item.Id);

                // Parent ID (2 bytes)
                writer.Write((ushort)item.ParentId);

                // Order (2 bytes)
                writer.Write((ushort)item.Order);

                // Number of children (2 bytes)
                writer.Write((ushort)item.NbChildren);

                // Favorite order (2 bytes)
                writer.Write((ushort)item.FavOrder);

                // Type (2 bytes)
                writer.Write((ushort)item.Type);

                // Limit time (4 bytes)
                writer.Write(item.LimitTime);

                // Add time (4 bytes)
                writer.Write(item.AddTime);

                // UUID (64 bytes: 1 byte length + string + padding)
                byte[] uuidBytes = Encoding.UTF8.GetBytes(item.Uuid);
                writer.Write((byte)uuidBytes.Length);
                writer.Write(uuidBytes);
                writer.Write(new byte[64 - uuidBytes.Length]); // Padding

                // Title (66 bytes: 1 byte length + string + padding)
                byte[] titleBytes = Encoding.UTF8.GetBytes(item.Title);
                writer.Write((byte)titleBytes.Length);
                writer.Write(titleBytes);
                writer.Write(new byte[66 - titleBytes.Length]); // Padding
            }

            writer.Flush();
        }

        public static FolderNode ExtractFoldersTree(string filepath)
        {
            var workingDirectory = Path.GetDirectoryName(filepath);
            var items = ReadPlaylistFromBin(filepath);
            var root = new FolderNode { Id = "", Name = "Root", Subfolders = new ObservableCollection<FolderNode>(), Items = new ObservableCollection<ItemNode>() };
            var folderDict = new Dictionary<int, FolderNode> { { 1, root } };
            foreach (var item in items)
            {
                // On ignore le root
                if (item.IsRoot) continue;

                // On ignore les dossiers spéciaux (favoris, récentes, etc.)
                if (item.IsFavoriteFolder) continue;
                if (item.IsRecentFolder) continue;

                // On ignore les sons
                if (item.IsSound) continue;

                if (item.IsDirectory) // Dossier
                {
                    // On ignore les dossiers personnalisés de MerlinManager (ceux dont l'UUID commence par "MM-")
                    if (item.Uuid.StartsWith(LibraryData.CustomItemIdPrefix)) continue;

                    var folder = new FolderNode
                    {
                        Id = item.Uuid,
                        Name = item.Title,
                        Subfolders = new ObservableCollection<FolderNode>(),
                        Items = new ObservableCollection<ItemNode>(),
                        WorkingDirectory = workingDirectory
                    };
                    folderDict[item.Id] = folder;
                    if (folderDict.TryGetValue(item.ParentId, out var parentFolder))
                    {
                        parentFolder.Subfolders.Add(folder);
                    }
                }
            }
            return root;
        }

        /// <summary>
        /// Converts the specified library data into a Merlin-compatible playlist file asynchronously.
        /// </summary>
        /// <remarks>Existing custom items in the playlist and corresponding files in the target directory
        /// are replaced during conversion. The method performs file I/O and may take time depending on the size of the
        /// library and playlist.</remarks>
        /// <param name="libraryData">The library data to be converted and added to the playlist. Must not be null.</param>
        /// <param name="playlistPath">The full file path of the target playlist to update or create. Cannot be null or empty.</param>
        /// <param name="statusCallback">Delegate for transfert progress</param>
        /// <returns>A task that represents the asynchronous operation. The task result is true if the conversion succeeds;
        /// otherwise, false.</returns>
        public static Task<bool> ConvertToMerlinPlaylistAsync(LibraryData libraryData, string playlistPath, Action<TransfertStatus> transfertProgress = null)
        {
            return Task.Run(() =>
            {
                TransfertStatus transfertStatus = new TransfertStatus()
                {
                    DialogTitle = "Transfert playlist",
                    AutoClose = false
                };

                try
                {
                    transfertStatus.CurrentAction = "Lecture de la playlist existante...";
                    transfertProgress?.Invoke(transfertStatus);

                    //On récupère le dossier de travail à partir du chemin de la playlist
                    var workingDirectory = Path.GetDirectoryName(playlistPath);

                    // On lit la playlist existante pour récupérer les éléments déjà présents
                    var items = ReadPlaylistFromBin(playlistPath);

                    //var jsonExisting = System.Text.Json.JsonSerializer.Serialize(items, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
                    //File.WriteAllText("playlist_existing.json", jsonExisting);


                    // On retire tous les éléments dont l'uuid commence par CustomItemIdPrefix (ce sont les éléments personnalisés de MerlinManager)
                    items = items.Where(i => !i.Uuid.StartsWith(LibraryData.CustomItemIdPrefix)).ToList();
                    // On récupère le plus grand ID utilisé dans la playlist pour éviter les conflits avec les éléments personnalisés
                    var maxId = items.Max(i => i.Id);


                    transfertStatus.CurrentAction = "Suppression des fichiers personnalisés...";
                    transfertProgress?.Invoke(transfertStatus);
                    //On supprime tous les fichiers commencant par CustomItemIdPrefix dans le dossier de travail
                    var existingFiles = Directory.GetFiles(workingDirectory, LibraryData.CustomItemIdPrefix + "*.*");
                    existingFiles.ToList().ForEach(File.Delete);

                    transfertStatus.CurrentAction = "Création de la nouvelle playlist...";
                    transfertProgress?.Invoke(transfertStatus);

                    // On ajoute les éléments de la bibliothèque à la playlist
                    AddNode(libraryData.RootFolder, 1, ref maxId, items);

                    // On recalcule le nombre d'enfants pour chaque dossier
                    //// Récupération des items de type dossier
                    var folderItems = items.Where(i => i.IsDirectory).ToList();
                    //// Pour chaque dossier, on compte le nombre d'enfants directs (sons + dossiers) et on met à jour le champ NbChildren
                    folderItems.ForEach(folder =>
                    {
                        var childCount = items.Count(i => i.ParentId == folder.Id);
                        folder.NbChildren = (ushort)childCount;
                    });

                    //var jsonWithLibrary = System.Text.Json.JsonSerializer.Serialize(items, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
                    //File.WriteAllText("playlist_with_library.json", jsonWithLibrary);

                    //On trie les éléments par dossiers parents puis leurs éléments et on renumérotes les éléments
                    var rootMerlinItem = items.FirstOrDefault(i => i.IsRoot);
                    var sortedMerlinItems = new List<MerlinItem>{ rootMerlinItem};

                    var newId = 1;
                    SortMerlinItems(sortedMerlinItems, items, 1, 1, ref newId);

                    //var jsonSorted = System.Text.Json.JsonSerializer.Serialize(sortedMerlinItems, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
                    //File.WriteAllText("playlist_sorted.json", jsonSorted);


                    transfertStatus.CurrentAction = "Ecriture de la nouvelle playlist...";
                    transfertProgress?.Invoke(transfertStatus);

                    // On écrit la playlist modifiée dans le fichier
                    WritePlaylistToBin(playlistPath, sortedMerlinItems);

                    transfertStatus.CurrentAction = "Préparation de la copie des fichiers...";
                    transfertProgress?.Invoke(transfertStatus);

                    // On copie les fichiers de la bibliothèque dans le dossier de travail
                    var libraryFiles = Directory.GetFiles(LibraryStorageService.DataDirectory, LibraryData.CustomItemIdPrefix + "*.*", SearchOption.TopDirectoryOnly);

                    transfertStatus.CurrentAction = "Copie des fichiers...";
                    transfertStatus.Total = libraryFiles.Length;
                    transfertProgress?.Invoke(transfertStatus);

                    libraryFiles.ToList().ForEach(lf =>
                    {
                        transfertStatus.CurrentText = Path.GetFileName(lf);
                        transfertStatus.Current++;
                        transfertProgress.Invoke(transfertStatus);

                        var destPath = Path.Combine(workingDirectory, Path.GetFileName(lf));
                        File.Copy(lf, destPath, overwrite: true);

                    });

                    transfertStatus.CurrentAction = "Transfert terminé.";
                    transfertStatus.TransfertCompleted = true;
                    transfertStatus.TransfertSucceeded = true;

                    transfertProgress?.Invoke(transfertStatus);
                }
                catch (Exception ex)
                {
                    transfertStatus.CurrentAction = "Erreur de transfert.";
                    transfertStatus.TransfertCompleted = true;
                    transfertStatus.TransfertSucceeded = false;
                    transfertStatus.TransfertError = ex.Message;

                    transfertProgress?.Invoke(transfertStatus);

                    return false;
                }

                return true;
            });
        }

        private static void AddNode(FolderNode node, int parentId, ref int maxId, List<MerlinItem> items)
        {
            foreach (var folder in node.Subfolders)
            {
                // On vérifier que le dossier n'est pas déjà présent dans la playlist (en se basant sur l'UUID), pour éviter les doublons
                var existingFolder = items.FirstOrDefault(i => i.Uuid == folder.Id);

                var id = existingFolder != null ? existingFolder.Id : ++maxId;

                if (existingFolder == null)
                {
                    items.Add(new MerlinItem
                    {
                        Id = id,
                        ParentId = parentId,
                        Order = 0,
                        NbChildren = folder.Subfolders.Count + folder.Items.Count,
                        FavOrder = 0,
                        Type = 2, // Dossier
                        LimitTime = 0,
                        AddTime = 0,
                        Uuid = folder.Id,
                        Title = folder.Name
                    });
                }

                AddNode(folder, id, ref maxId, items);
            }
            foreach (var item in node.Items)
            {
                var id = ++maxId;
                items.Add(new MerlinItem
                {
                    Id = id,
                    ParentId = parentId,
                    Order = 0,
                    NbChildren = 0,
                    FavOrder = 0,
                    Type = 4, // Son
                    LimitTime = 0,
                    AddTime = (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    Uuid = item.Id,
                    Title = item.Name
                });
            }
        }

        private static void SortMerlinItems(List<MerlinItem> sortedItems, List<MerlinItem> items,int oldId, int parentId, ref int newId)
        {
            var children = items.Where(i => i.ParentId == oldId).OrderBy(i => i.Type).ThenBy(i => i.Id).ToList();
            for(int i = 0; i < children.Count; i++)
            {
                var currentId = children[i].Id;
                var child = children[i].Clone();
                child.Order = (ushort)i;
                child.Id = ++newId;
                child.ParentId = parentId;
                sortedItems.Add(child);
                if (child.IsDirectory)
                {
                    SortMerlinItems(sortedItems, items, currentId, child.Id, ref newId);
                }
            }
        }
    }

    public class TransfertStatus
    {
        public string DialogTitle { get; set; }
        public bool AutoClose { get; set; }
        public string CurrentAction { get; set; } = string.Empty;
        public int Total { get; set; }
        public int Current { get; set; }
        public string CurrentText { get; set; } = string.Empty;
        public bool TransfertCompleted { get; set; }
        public bool TransfertSucceeded { get; set; }
        public string TransfertError { get; set; }
    }
}

