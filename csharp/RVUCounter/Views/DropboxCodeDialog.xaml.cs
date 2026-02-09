using System.Windows;
using RVUCounter.Core;

namespace RVUCounter.Views;

public partial class DropboxCodeDialog : Window
{
    public string AuthorizationCode => CodeTextBox.Text;

    public DropboxCodeDialog()
    {
        InitializeComponent();
        ThemeManager.ApplyCurrentThemeTitleBar(this);
        CodeTextBox.Focus();
    }

    private void OK_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(CodeTextBox.Text))
        {
            MessageBox.Show("Please paste the authorization code.", "Missing Code",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
