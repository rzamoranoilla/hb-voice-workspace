using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Navigation;

namespace HB;

/// <summary>
/// The window shown while the slow parts of the app warm up at start-up, so the login window
/// opens on a program that is already ready.
/// </summary>
public partial class SplashWindow : Window
{
    public SplashWindow()
    {
        InitializeComponent();
        var version = Assembly.GetExecutingAssembly().GetName().Version;
        VersionText.Text = version == null ? "" : $"Version {version.Major}.{version.Minor}";
    }

    /// <summary>Shows what is being loaded and how far along the start-up is (0 to 1).</summary>
    public void Report(string status, double progress)
    {
        StatusText.Text = status;
        Progress.Value = Math.Clamp(progress, 0, 1);
    }

    private void Zamoilla_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        Zamoilla.OpenSite();
        e.Handled = true;
    }
}

/// <summary>Who makes the program, and the link to them shown in the start-up windows.</summary>
public static class Zamoilla
{
    public const string Site = "https://raul.zamoilla.cl";

    /// <summary>Opens the Zamoilla site in the default browser.</summary>
    public static void OpenSite()
    {
        try { Process.Start(new ProcessStartInfo(Site) { UseShellExecute = true }); }
        catch (System.ComponentModel.Win32Exception) { } // no browser: nothing to do
    }
}
