using System.Windows;

namespace MerlinManager.Dialogs;

public partial class MerlinLoginDialog : Window
{
    public string Username => UsernameTextBox.Text.Trim();
    public string Password => PasswordBox.Password;

    public MerlinLoginDialog(string defaultUsername = null)
    {
        InitializeComponent();
        UsernameTextBox.Text = defaultUsername ?? string.Empty;
        Loaded += (_, _) =>
        {
            if (string.IsNullOrEmpty(UsernameTextBox.Text))
            {
                UsernameTextBox.Focus();
            }
            else
            {
                PasswordBox.Focus();
            }
        };
    }

    private void LoginButton_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(Username) || string.IsNullOrEmpty(Password))
        {
            MessageBox.Show(this, "Saisir l'email et le mot de passe", "Validation", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        DialogResult = true;
        Close();
    }
}
