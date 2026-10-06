using MerlinManager.Services;
using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Input;

namespace MerlinManager.Dialogs;

public partial class TransfertDialog : Window
{
    private bool _canClose;
    private bool _autoClose;

    public TransfertDialog(string title, bool autoClose)
    {
        InitializeComponent();
        Closing += OnClosing;
        DialogTitle.Text = title;
        _autoClose = autoClose;
        CloseButton.Visibility = autoClose ? Visibility.Collapsed : Visibility.Visible;
    }

    public void UpdateStatus(TransfertStatus status)
    {
        ActionText.Text = status.CurrentAction;
        ItemText.ClearValue(TextBlock.ForegroundProperty);

        if (status.Total > 0)
        {
            TransferProgressBar.IsIndeterminate = false;
            TransferProgressBar.Maximum = status.Total;
            TransferProgressBar.Value = Math.Min(status.Current, status.Total);
            ItemText.Text = $"{status.Current}/{status.Total} - {status.CurrentText}";
        }
        else
        {
            TransferProgressBar.IsIndeterminate = true;
            ItemText.Text = string.Empty;
        }

        if (!status.TransfertCompleted)
        {
            _canClose = false;
            CloseButton.IsEnabled = false;
            return;
        }

        TransferProgressBar.IsIndeterminate = false;
        TransferProgressBar.Value = TransferProgressBar.Maximum;

        if (!status.TransfertSucceeded && !string.IsNullOrWhiteSpace(status.TransfertError))
        {
            ItemText.Text = $"Erreur: {status.TransfertError}";
            ItemText.Foreground = Brushes.Firebrick;
            CloseButton.Visibility = Visibility.Visible;
        }
        else if (status.TransfertSucceeded)
        {
            ItemText.Text = "Transfert terminé avec succès.";
            ItemText.ClearValue(TextBlock.ForegroundProperty);
            if (_autoClose)
            {
                _canClose = true;
                Close();
            }
        }

        _canClose = true;
        CloseButton.IsEnabled = true;
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void DialogSurface_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void OnClosing(object sender, CancelEventArgs e)
    {
        if (!_canClose)
        {
            e.Cancel = true;
        }
    }
}
