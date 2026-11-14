using MerlinManager.Models;
using System.Collections.ObjectModel;
using System.IO;
using System.Text;

namespace MerlinManager.Services
{
    public enum PlaylistVersion
    {
        /// <summary>Ancien format : "playlist.bin" en clair, enregistrements de 144 octets, UUID en texte.</summary>
        V2Plain,
        /// <summary>Nouveau format (app 1.30.0) : "pl_enc.bin" chiffré AES-256-GCM, enregistrements de 96 octets, UUID binaire.</summary>
        V3Encrypted
    }

    public static class MerlinPlaylistService
    {
        // --- Format v3 ---
        private const int V3RecordSize = 96;
        private const int V3TitleOffset = 32;
        private const int V3TitleMaxBytes = V3RecordSize - V3TitleOffset; // 64
        private const int V3GuidOffset = 16;
        private const ushort V3TypeFolder = 0x02;
        private const ushort V3TypeSound = 0x24; // valeur majoritaire observée pour les sons locaux

        #region Détection de version

        public static PlaylistVersion DetectVersion(string filepath)
        {
            var name = Path.GetFileName(filepath).ToLowerInvariant();
            if (name.Contains("enc"))
            {
                return PlaylistVersion.V3Encrypted;
            }
            if (name is "playlist.bin" or "pl.bin")
            {
                return PlaylistVersion.V2Plain;
            }

            var length = new FileInfo(filepath).Length;
            // v3 chiffré = IV(12) + N*96 + tag(16)
            if (length > 28 && (length - 28) % V3RecordSize == 0 && length % 144 != 0)
            {
                return PlaylistVersion.V3Encrypted;
            }
            return PlaylistVersion.V2Plain;
        }

        #endregion

        #region API publique (dispatch v2 / v3)

        /// <summary>
        /// Extrait l'arborescence des dossiers d'une playlist Merlin (v2 ou v3).
        /// Pour une playlist v3 chiffrée, <paramref name="stationMac"/> (MAC WiFi STA du boîtier) est requis.
        /// </summary>
        public static FolderNode ExtractFoldersTree(string filepath, string stationMac = null)
        {
            if (DetectVersion(filepath) == PlaylistVersion.V3Encrypted)
            {
                var key = RequireKey(stationMac);
                var items = ReadPlaylistV3(filepath, key);
                return BuildFoldersTree(items, Path.GetDirectoryName(filepath));
            }
            else
            {
                var items = ReadPlaylistFromBin(filepath);
                return BuildFoldersTree(items, Path.GetDirectoryName(filepath));
            }
        }

        public static Task<bool> ConvertToMerlinPlaylistAsync(LibraryData libraryData, string playlistPath, string stationMac, Action<TransfertStatus> transfertProgress = null)
        {
            if (DetectVersion(playlistPath) == PlaylistVersion.V3Encrypted)
            {
                return ConvertToMerlinPlaylistV3Async(libraryData, playlistPath, stationMac, transfertProgress);
            }
            return ConvertToMerlinPlaylistV2Async(libraryData, playlistPath, transfertProgress);
        }

        private static byte[] RequireKey(string stationMac)
        {
            if (string.IsNullOrWhiteSpace(stationMac))
            {
                throw new InvalidOperationException("L'adresse MAC du boîtier Merlin est requise pour les playlists chiffrées (format v3).");
            }
            return MerlinCryptoService.KeyFromStationMac(stationMac);
        }

        #endregion

        #region Lecture / écriture v3

        public static List<MerlinItem> ReadPlaylistV3(string filepath, byte[] key)
        {
            var plain = MerlinCryptoService.Decrypt(File.ReadAllBytes(filepath), key);
            return ParsePlaylistV3(plain);
        }

        public static List<MerlinItem> ParsePlaylistV3(byte[] plain)
        {
            var items = new List<MerlinItem>();
            var count = plain.Length / V3RecordSize;
            for (var i = 0; i < count; i++)
            {
                var rec = new ReadOnlySpan<byte>(plain, i * V3RecordSize, V3RecordSize);
                var guid = MerlinCryptoService.MerlinBytesToGuid(rec.Slice(V3GuidOffset, 16));

                var titleSpan = rec.Slice(V3TitleOffset, V3TitleMaxBytes);
                var nul = titleSpan.IndexOf((byte)0);
                if (nul >= 0) titleSpan = titleSpan.Slice(0, nul);

                items.Add(new MerlinItem
                {
                    Id = BitConverter.ToUInt16(rec.Slice(0, 2)),
                    ParentId = BitConverter.ToUInt16(rec.Slice(2, 2)),
                    Order = BitConverter.ToUInt16(rec.Slice(4, 2)),
                    NbChildren = BitConverter.ToUInt16(rec.Slice(6, 2)),
                    Type = BitConverter.ToUInt16(rec.Slice(8, 2)),
                    // offset 10 (u16) et 12 (u32) : non réservés pour certains items d'origine
                    // (identifiant de pack/contenu). Conservés tels quels pour la réécriture.
                    FavOrder = BitConverter.ToUInt16(rec.Slice(10, 2)),
                    LimitTime = 0,
                    AddTime = BitConverter.ToUInt32(rec.Slice(12, 4)),
                    Uuid = guid == Guid.Empty ? string.Empty : guid.ToString(),
                    Title = Encoding.UTF8.GetString(titleSpan)
                });
            }
            return items;
        }

        public static byte[] SerializePlaylistV3(List<MerlinItem> items)
        {
            var buffer = new byte[items.Count * V3RecordSize];
            for (var i = 0; i < items.Count; i++)
            {
                var item = items[i];
                var o = i * V3RecordSize;
                WriteU16(buffer, o + 0, (ushort)item.Id);
                WriteU16(buffer, o + 2, (ushort)item.ParentId);
                WriteU16(buffer, o + 4, (ushort)item.Order);
                WriteU16(buffer, o + 6, (ushort)item.NbChildren);
                WriteU16(buffer, o + 8, (ushort)item.Type);
                WriteU16(buffer, o + 10, (ushort)item.FavOrder);
                WriteU32(buffer, o + 12, item.AddTime);

                var guid = string.IsNullOrEmpty(item.Uuid) ? Guid.Empty : Guid.Parse(item.Uuid);
                var guidBytes = MerlinCryptoService.GuidToMerlinBytes(guid);
                Buffer.BlockCopy(guidBytes, 0, buffer, o + V3GuidOffset, 16);

                var titleBytes = TruncateUtf8(item.Title ?? string.Empty, V3TitleMaxBytes);
                Buffer.BlockCopy(titleBytes, 0, buffer, o + V3TitleOffset, titleBytes.Length);
            }
            return buffer;
        }

        public static void WritePlaylistV3(string filepath, List<MerlinItem> items, byte[] key)
        {
            var plain = SerializePlaylistV3(items);
            File.WriteAllBytes(filepath, MerlinCryptoService.Encrypt(plain, key));
        }

        private static void WriteU16(byte[] buffer, int offset, ushort value)
        {
            buffer[offset] = (byte)(value & 0xFF);
            buffer[offset + 1] = (byte)(value >> 8);
        }

        private static void WriteU32(byte[] buffer, int offset, uint value)
        {
            buffer[offset] = (byte)(value & 0xFF);
            buffer[offset + 1] = (byte)((value >> 8) & 0xFF);
            buffer[offset + 2] = (byte)((value >> 16) & 0xFF);
            buffer[offset + 3] = (byte)((value >> 24) & 0xFF);
        }

        private static byte[] TruncateUtf8(string text, int maxBytes)
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            if (bytes.Length <= maxBytes) return bytes;
            // tronque sans couper un caractère multi-octets
            var count = maxBytes;
            while (count > 0 && (bytes[count] & 0xC0) == 0x80) count--;
            return bytes[..count];
        }

        #endregion

        #region Construction de l'arborescence (commune v2 / v3)

        private static FolderNode BuildFoldersTree(List<MerlinItem> items, string workingDirectory)
        {
            var root = new FolderNode { Id = string.Empty, Name = "Root", Subfolders = new ObservableCollection<FolderNode>(), Items = new ObservableCollection<ItemNode>() };

            var rootItem = items.FirstOrDefault(i => i.IsRoot);
            var rootId = rootItem?.Id ?? 1; // v2 : 1, v3 : 0
            var folderDict = new Dictionary<int, FolderNode> { { rootId, root } };

            foreach (var item in items)
            {
                if (item.IsRoot) continue;
                if (item.IsFavoriteFolder) continue;
                if (item.IsRecentFolder) continue;
                if (item.IsSound) continue;

                if (item.IsDirectory)
                {
                    // Ignore les dossiers personnalisés de MerlinManager
                    if (IsCustomItem(item)) continue;

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

        /// <summary>Un item de playlist est-il un item créé par MerlinManager (v2 : UUID "MM-…" ; v3 : GUID marqué) ?</summary>
        private static bool IsCustomItem(MerlinItem item)
        {
            if (string.IsNullOrEmpty(item.Uuid)) return false;
            if (item.Uuid.StartsWith(LibraryData.CustomItemIdPrefix)) return true; // v2
            return Guid.TryParse(item.Uuid, out var g) && MerlinCryptoService.IsCustomGuid(g); // v3
        }

        #endregion

        #region Lecture / écriture v2 (format d'origine)

        /// <summary>Lit une playlist binaire Merlin v2 depuis un fichier .bin</summary>
        public static List<MerlinItem> ReadPlaylistFromBin(string filepath)
        {
            using var stream = File.OpenRead(filepath);
            return ReadMerlinPlaylist(stream);
        }

        private static List<MerlinItem> ReadMerlinPlaylist(Stream stream)
        {
            var items = new List<MerlinItem>();
            using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);

            try
            {
                while (stream.Position < stream.Length)
                {
                    var item = new MerlinItem();

                    item.Id = reader.ReadUInt16();
                    item.ParentId = reader.ReadUInt16();
                    item.Order = reader.ReadUInt16();
                    item.NbChildren = reader.ReadUInt16();
                    item.FavOrder = reader.ReadUInt16();
                    item.Type = reader.ReadUInt16();
                    item.LimitTime = reader.ReadUInt32();
                    item.AddTime = reader.ReadUInt32();

                    byte uuidLength = reader.ReadByte();
                    byte[] uuidBytes = reader.ReadBytes(uuidLength);
                    item.Uuid = Encoding.UTF8.GetString(uuidBytes);
                    reader.ReadBytes(64 - uuidLength);

                    byte titleLength = reader.ReadByte();
                    byte[] titleBytes = reader.ReadBytes(titleLength);
                    item.Title = Encoding.UTF8.GetString(titleBytes);
                    reader.ReadBytes(66 - titleLength);

                    items.Add(item);
                }
            }
            catch (EndOfStreamException)
            {
                // Fin normale du fichier
            }

            return items;
        }

        public static void WritePlaylistToBin(string filepath, List<MerlinItem> items)
        {
            using var stream = File.Create(filepath);
            WriteMerlinPlaylist(stream, items);
        }

        private static void WriteMerlinPlaylist(Stream stream, List<MerlinItem> items)
        {
            using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);

            foreach (var item in items)
            {
                writer.Write((ushort)item.Id);
                writer.Write((ushort)item.ParentId);
                writer.Write((ushort)item.Order);
                writer.Write((ushort)item.NbChildren);
                writer.Write((ushort)item.FavOrder);
                writer.Write((ushort)item.Type);
                writer.Write(item.LimitTime);
                writer.Write(item.AddTime);

                byte[] uuidBytes = Encoding.UTF8.GetBytes(item.Uuid);
                writer.Write((byte)uuidBytes.Length);
                writer.Write(uuidBytes);
                writer.Write(new byte[64 - uuidBytes.Length]);

                byte[] titleBytes = Encoding.UTF8.GetBytes(item.Title);
                writer.Write((byte)titleBytes.Length);
                writer.Write(titleBytes);
                writer.Write(new byte[66 - titleBytes.Length]);
            }

            writer.Flush();
        }

        #endregion

        #region Transfert v2

        private static Task<bool> ConvertToMerlinPlaylistV2Async(LibraryData libraryData, string playlistPath, Action<TransfertStatus> transfertProgress = null)
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

                    var workingDirectory = Path.GetDirectoryName(playlistPath);
                    var items = ReadPlaylistFromBin(playlistPath);

                    items = items.Where(i => !i.Uuid.StartsWith(LibraryData.CustomItemIdPrefix)).ToList();
                    var maxId = items.Max(i => i.Id);

                    transfertStatus.CurrentAction = "Suppression des fichiers personnalisés...";
                    transfertProgress?.Invoke(transfertStatus);
                    var existingFiles = Directory.GetFiles(workingDirectory, LibraryData.CustomItemIdPrefix + "*.*");
                    existingFiles.ToList().ForEach(File.Delete);

                    transfertStatus.CurrentAction = "Création de la nouvelle playlist...";
                    transfertProgress?.Invoke(transfertStatus);

                    AddNodeV2(libraryData.RootFolder, 1, ref maxId, items);

                    var folderItems = items.Where(i => i.IsDirectory).ToList();
                    folderItems.ForEach(folder =>
                    {
                        var childCount = items.Count(i => i.ParentId == folder.Id);
                        folder.NbChildren = (ushort)childCount;
                    });

                    var rootMerlinItem = items.FirstOrDefault(i => i.IsRoot);
                    var sortedMerlinItems = new List<MerlinItem> { rootMerlinItem };

                    var newId = 1;
                    SortMerlinItems(sortedMerlinItems, items, 1, 1, ref newId);

                    transfertStatus.CurrentAction = "Ecriture de la nouvelle playlist...";
                    transfertProgress?.Invoke(transfertStatus);

                    WritePlaylistToBin(playlistPath, sortedMerlinItems);

                    CopyLibraryFiles(workingDirectory, transfertStatus, transfertProgress, null);

                    transfertStatus.CurrentAction = "Transfert terminé.";
                    transfertStatus.TransfertCompleted = true;
                    transfertStatus.TransfertSucceeded = true;
                    transfertProgress?.Invoke(transfertStatus);
                }
                catch (Exception ex)
                {
                    FailTransfert(transfertStatus, transfertProgress, ex);
                    return false;
                }

                return true;
            });
        }

        private static void AddNodeV2(FolderNode node, int parentId, ref int maxId, List<MerlinItem> items)
        {
            foreach (var folder in node.Subfolders)
            {
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
                        Type = 2,
                        LimitTime = 0,
                        AddTime = (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                        Uuid = folder.Id,
                        Title = folder.Name
                    });
                }

                AddNodeV2(folder, id, ref maxId, items);
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
                    Type = 4,
                    LimitTime = 0,
                    AddTime = (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    Uuid = item.Id,
                    Title = item.Name
                });
            }
        }

        #endregion

        #region Transfert v3

        private static Task<bool> ConvertToMerlinPlaylistV3Async(LibraryData libraryData, string playlistPath, string stationMac, Action<TransfertStatus> transfertProgress = null)
        {
            return Task.Run(() =>
            {
                var transfertStatus = new TransfertStatus { DialogTitle = "Transfert playlist", AutoClose = false };

                try
                {
                    var key = RequireKey(stationMac);
                    var workingDirectory = Path.GetDirectoryName(playlistPath);

                    transfertStatus.CurrentAction = "Lecture de la playlist existante...";
                    transfertProgress?.Invoke(transfertStatus);

                    var items = ReadPlaylistV3(playlistPath, key);

                    // On retire les items déjà créés par MerlinManager (GUID marqué)
                    items = items.Where(i => !IsCustomItem(i)).ToList();

                    var rootItem = items.FirstOrDefault(i => i.IsRoot);
                    var rootId = rootItem?.Id ?? 0;
                    var maxId = items.Count > 0 ? items.Max(i => i.Id) : rootId;

                    transfertStatus.CurrentAction = "Suppression des fichiers personnalisés...";
                    transfertProgress?.Invoke(transfertStatus);
                    DeleteCustomCardFiles(workingDirectory);

                    transfertStatus.CurrentAction = "Création de la nouvelle playlist...";
                    transfertProgress?.Invoke(transfertStatus);

                    AddNodeV3(libraryData.RootFolder, rootId, ref maxId, items);

                    foreach (var folder in items.Where(i => i.IsDirectory))
                    {
                        folder.NbChildren = (ushort)items.Count(i => i.ParentId == folder.Id);
                    }

                    var sortedMerlinItems = rootItem != null ? new List<MerlinItem> { rootItem } : new List<MerlinItem>();
                    var newId = rootId;
                    SortMerlinItems(sortedMerlinItems, items, rootId, rootId, ref newId);

                    transfertStatus.CurrentAction = "Ecriture de la nouvelle playlist...";
                    transfertProgress?.Invoke(transfertStatus);

                    WritePlaylistV3(playlistPath, sortedMerlinItems, key);

                    CopyLibraryFiles(workingDirectory, transfertStatus, transfertProgress,
                        localGuid => MerlinCryptoService.ToMarkedCardGuid(localGuid).ToString());

                    transfertStatus.CurrentAction = "Transfert terminé.";
                    transfertStatus.TransfertCompleted = true;
                    transfertStatus.TransfertSucceeded = true;
                    transfertProgress?.Invoke(transfertStatus);
                }
                catch (Exception ex)
                {
                    FailTransfert(transfertStatus, transfertProgress, ex);
                    return false;
                }

                return true;
            });
        }

        private static void AddNodeV3(FolderNode node, int parentId, ref int maxId, List<MerlinItem> items)
        {
            foreach (var folder in node.Subfolders)
            {
                var cardUuid = CardUuidFor(folder.Id);
                var existingFolder = items.FirstOrDefault(i => i.Uuid == cardUuid || i.Uuid == folder.Id);
                var id = existingFolder != null ? existingFolder.Id : ++maxId;

                if (existingFolder == null)
                {
                    items.Add(new MerlinItem
                    {
                        Id = id,
                        ParentId = parentId,
                        Order = 0,
                        NbChildren = folder.Subfolders.Count + folder.Items.Count,
                        Type = V3TypeFolder,
                        Uuid = cardUuid,
                        Title = folder.Name
                    });
                }

                AddNodeV3(folder, id, ref maxId, items);
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
                    Type = V3TypeSound,
                    Uuid = CardUuidFor(item.Id),
                    Title = item.Name
                });
            }
        }

        /// <summary>
        /// Convertit un identifiant de bibliothèque en UUID à écrire sur la carte.
        /// Les items MerlinManager ("MM-&lt;guid&gt;") reçoivent un GUID marqué déterministe ;
        /// les dossiers d'origine (GUID canonique) sont conservés tels quels.
        /// </summary>
        private static string CardUuidFor(string libraryId)
        {
            if (string.IsNullOrEmpty(libraryId)) return string.Empty;

            if (libraryId.StartsWith(LibraryData.CustomItemIdPrefix))
            {
                var raw = libraryId.Substring(LibraryData.CustomItemIdPrefix.Length);
                if (Guid.TryParse(raw, out var g))
                {
                    return MerlinCryptoService.ToMarkedCardGuid(g).ToString();
                }
            }
            return libraryId; // déjà un GUID canonique (dossier d'origine)
        }

        private static void DeleteCustomCardFiles(string workingDirectory)
        {
            foreach (var file in Directory.GetFiles(workingDirectory))
            {
                var name = Path.GetFileNameWithoutExtension(file);
                if (Guid.TryParse(name, out var g) && MerlinCryptoService.IsCustomGuid(g))
                {
                    File.Delete(file);
                }
            }
        }

        #endregion

        #region Helpers communs de transfert

        /// <summary>
        /// Copie les fichiers locaux "MM-*" de la bibliothèque vers la carte.
        /// <paramref name="cardNameFromLocalGuid"/> : si fourni (v3), renomme chaque fichier
        /// "MM-&lt;guid&gt;.ext" en "&lt;guid-carte&gt;.ext" ; sinon (v2) conserve le nom "MM-*".
        /// </summary>
        private static void CopyLibraryFiles(string workingDirectory, TransfertStatus transfertStatus,
            Action<TransfertStatus> transfertProgress, Func<Guid, string> cardNameFromLocalGuid)
        {
            transfertStatus.CurrentAction = "Préparation de la copie des fichiers...";
            transfertProgress?.Invoke(transfertStatus);

            var libraryFiles = Directory.GetFiles(LibraryStorageService.DataDirectory,
                LibraryData.CustomItemIdPrefix + "*.*", SearchOption.TopDirectoryOnly);

            transfertStatus.CurrentAction = "Copie des fichiers...";
            transfertStatus.Total = libraryFiles.Length;
            transfertProgress?.Invoke(transfertStatus);

            foreach (var lf in libraryFiles)
            {
                transfertStatus.CurrentText = Path.GetFileName(lf);
                transfertStatus.Current++;
                transfertProgress?.Invoke(transfertStatus);

                string destName;
                if (cardNameFromLocalGuid != null)
                {
                    var raw = Path.GetFileNameWithoutExtension(lf).Substring(LibraryData.CustomItemIdPrefix.Length);
                    if (!Guid.TryParse(raw, out var localGuid))
                    {
                        continue; // nom inattendu, on ignore
                    }
                    destName = cardNameFromLocalGuid(localGuid) + Path.GetExtension(lf);
                }
                else
                {
                    destName = Path.GetFileName(lf);
                }

                File.Copy(lf, Path.Combine(workingDirectory, destName), overwrite: true);
            }
        }

        private static void FailTransfert(TransfertStatus transfertStatus, Action<TransfertStatus> transfertProgress, Exception ex)
        {
            transfertStatus.CurrentAction = "Erreur de transfert.";
            transfertStatus.TransfertCompleted = true;
            transfertStatus.TransfertSucceeded = false;
            transfertStatus.TransfertError = ex.Message;
            transfertProgress?.Invoke(transfertStatus);
        }

        private static void SortMerlinItems(List<MerlinItem> sortedItems, List<MerlinItem> items, int oldId, int parentId, ref int newId)
        {
            var children = items.Where(i => i.ParentId == oldId).OrderBy(i => i.Type).ThenBy(i => i.Id).ToList();
            for (int i = 0; i < children.Count; i++)
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

        #endregion
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
