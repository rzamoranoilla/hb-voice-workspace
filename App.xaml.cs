using System.IO;
using System.Windows;
using HB.Documents;

namespace HB;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        SelectToCopy.Install();

        // PDF copies shown by the previous run.
        foreach (var file in Directory.EnumerateFiles(PreviewView.PdfFolder, "*.pdf"))
        {
            try { File.Delete(file); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        ShowLogin();
    }

    /// <summary>Asks who is using the app, then opens the main window with that user's settings.</summary>
    private void ShowLogin()
    {
        var login = new LoginWindow();
        if (login.ShowDialog() != true)
        {
            Shutdown();
            return;
        }

        var main = new MainWindow(login.Session!);
        main.Closed += (_, _) =>
        {
            if (main.LogoutRequested) ShowLogin();
            else Shutdown();
        };
        main.Show();
    }
}
