using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace HB;

public partial class NewWorkspaceDialog : Window
{
    public NewWorkspaceDialog()
    {
        InitializeComponent();
        TemplateList.ItemsSource = WorkspaceTemplate.All;
        TemplateList.SelectedIndex = 0;
    }

    public WorkspaceTemplate SelectedTemplate => (WorkspaceTemplate)TemplateList.SelectedItem;
    public string FolderPath => Path.GetFullPath(FolderBox.Text.Trim());

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Choose the workspace folder" };
        if (dialog.ShowDialog(this) == true) FolderBox.Text = dialog.FolderName;
    }

    private void FolderBox_TextChanged(object sender, TextChangedEventArgs e) =>
        OpenButton.IsEnabled = Directory.Exists(FolderBox.Text.Trim());

    private void Open_Click(object sender, RoutedEventArgs e)
    {
        if (TemplateList.SelectedItem != null) DialogResult = true;
    }
}
