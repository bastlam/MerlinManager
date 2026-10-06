# MerlinManager

Application Windows de gestion de playlists pour l'enceinte **Merlin**. Elle permet d'organiser hors-ligne une bibliothèque de dossiers et de sons, puis de la transférer sur l'enceinte en réécrivant le fichier `playlist.bin` de l'appareil.

## Fonctionnalités

- **Bibliothèque hiérarchique** : création de dossiers, sous-dossiers et sons, avec images associées.
- **Ajout de contenu audio** :
  - import de fichiers audio locaux (conversion en MP3 128 kbps via NAudio / ffmpeg) ;
  - téléchargement depuis une URL YouTube (titre et miniature récupérés automatiquement via YoutubeExplode).
- **Édition** : modification des titres et images, déplacement des éléments dans l'arborescence, suppression.
- **Transfert vers l'enceinte** : sélection du fichier `playlist.bin` présent sur l'enceinte connectée, fusion avec la playlist existante et réécriture au format binaire propriétaire Merlin (avec boîte de dialogue de progression).
- **Lecture de playlist** : import et analyse d'un fichier `.bin` Merlin existant.
- **Stockage configurable** : les données (arborescence `tree.json`, fichiers MP3 et images JPG) sont stockées par défaut dans `%AppData%\MerlinManager\data`, dossier modifiable via le menu *Fichier → Dossier de stockage...* (persisté en registre sous `HKCU\Software\MerlinManager`).

## Pile technique

| Composant | Détail |
|---|---|
| Framework | .NET 8 (`net8.0-windows`), WPF |
| Audio | [NAudio](https://github.com/naudio/NAudio) 2.2.1, `ffmpeg.exe` embarqué |
| YouTube | [YoutubeExplode.Converter](https://github.com/Tyrrrz/YoutubeExplode) 6.6.2 |
| Installateur | WiX Toolset (projet `.wixproj`, sortie MSI) |

## Prérequis

- Windows 10/11
- [.NET SDK 8.0](https://dotnet.microsoft.com/download) ou supérieur pour compiler
- (Installateur) L'outil WiX est installé automatiquement par le script de build (`dotnet tool install --global wix`)

## Compilation et exécution

```powershell
dotnet build src/MerlinManager/MerlinManager.csproj
dotnet run --project src/MerlinManager/MerlinManager.csproj
```

## Génération de l'installateur MSI

Le script [build-installer.bat](src/MerlinManager.Installer/build-installer.bat) publie l'application en mode autonome (`win-x64`, self-contained) puis compile le projet WiX :

```powershell
src\MerlinManager.Installer\build-installer.bat
```

Le MSI est produit à la racine de `src` : `src\MerlinManager.msi`.

## Structure du dépôt

```
src/
├── MerlinManager.sln                # Solution Visual Studio
├── MerlinManager/                   # Application WPF
│   ├── MainWindow.xaml(.cs)         # Fenêtre principale et arborescence
│   ├── Dialogs/                     # Boîtes de dialogue (ajout, déplacement, transfert)
│   ├── Models/                      # MerlinItem, LibraryData, YoutubeInfos
│   └── Services/                    # Format binaire Merlin, stockage, audio, images
└── MerlinManager.Installer/         # Projet WiX (Product.wxs) + script de build
```

## Format binaire Merlin

Le fichier `playlist.bin` est une séquence d'enregistrements à champs fixes : identifiants, ordre, nombre d'enfants, type (le type de base s'obtient par `Type % 32` : dossier, son, favoris, récents…), puis UUID (64 octets) et titre (66 octets) avec préfixe de longueur et remplissage. La lecture et l'écriture sont implémentées dans [MerlinPlaylistService.cs](src/MerlinManager/Services/MerlinPlaylistService.cs).

## Licence

Voir [LICENSE](LICENSE).
