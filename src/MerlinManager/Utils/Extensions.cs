using MerlinManager.Models;
using MerlinManager.Services;

namespace MerlinManager.Utils
{
    public static class Extensions
    {
        public static async Task<FolderNode> MergeAsync(this FolderNode node, FolderNode other)
        {
            if(node.Name != other.Name)
            {
                return node;
            }

            // On recherche dans l'autre noeud si on trouve un sous-noeud avec le même nom que celui du noeud actuel
            foreach (FolderNode child in node.Subfolders)
            {
                var otherChild = other.Subfolders.FirstOrDefault(c => c.Name == child.Name);
                if(otherChild != null) await child.MergeAsync(otherChild);
            }

            // On ajoute les sous-noeuds de l'autre noeud qui n'ont pas de correspondance dans le noeud actuel
            var otherSubfoldersWithoutMatch = other.Subfolders.Where(c => !node.Subfolders.Any(n => n.Name == c.Name));
            foreach(var otherFolder in otherSubfoldersWithoutMatch)
            {
                await otherFolder.ImportImagesAsync(node.WorkingDirectory);
                node.Subfolders.Add(otherFolder);
            }

            return node;
        }

        public static async Task ImportImagesAsync(this FolderNode node, string workingDirectory)
        {
            // On copie l'image du noeud dans le dossier de destination
            await ImageService.ImportImageAsync(node.PicturePath, node.Id, workingDirectory);

            // On fait de même pour les sous-noeuds 
            foreach(var subfolder in node.Subfolders)
            {
                await subfolder.ImportImagesAsync(workingDirectory);
            }
        }
    }
}
