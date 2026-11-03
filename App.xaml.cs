using System.Windows;

namespace HB;

public partial class App : Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        SelectToCopy.Install();
        SmoothScroll.Install();

        // The slow components warm up behind the splash screen; then the user logs in.
        var splash = new SplashWindow();
        splash.Show();
        await WarmUp.RunAsync(splash.Report);
        splash.Close();
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
