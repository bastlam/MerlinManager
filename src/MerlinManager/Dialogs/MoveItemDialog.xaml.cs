using MerlinManager.Models;
using System.Windows;

namespace MerlinManager.Dialogs;

public partial class MoveItemDialog : Window
{
    public FolderNode SelectedFolder => FolderComboBox.SelectedItem as FolderNode;

    public MoveItemDialog(IEnumerable<FolderNode> folders, FolderNode currentFolder)
    {
        InitializeComponent();
        var availableFolders = folders.Where(f => f.Id != currentFolder.Id).ToList();
        FolderComboBox.ItemsSource = availableFolders;
        FolderComboBox.SelectedIndex = availableFolders.Count > 0 ? 0 : -1;
    }

    private void MoveButton_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedFolder is null)
        {
            MessageBox.Show(this, "Choisir un dossier de destination", "Validation", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        DialogResult = true;
        Close();
    }
}
