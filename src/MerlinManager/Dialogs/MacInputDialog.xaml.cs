using MerlinManager.Services;
using System.Windows;

namespace MerlinManager.Dialogs;

public partial class MacInputDialog : Window
{
    public string Mac => MacTextBox.Text?.Trim();

    public MacInputDialog(string currentMac = null)
    {
        InitializeComponent();
        MacTextBox.Text = currentMac ?? string.Empty;
        Loaded += (_, _) => { MacTextBox.Focus(); MacTextBox.SelectAll(); };
    }

    private void OkButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            // Valide le format (lève une exception si invalide)
            MerlinCryptoService.ParseMac(Mac);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Validation", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        DialogResult = true;
        Close();
    }
}
