using System.Diagnostics;
using System.IO;
using HB.Documents;
using HB.Speech;
using HB.Terminal;

namespace HB;

/// <summary>
/// Warms up, behind the splash screen, the components that take time the first time they are
/// used: the browser engine of the terminals and previews, the Windows voices, the audio stack.
/// Each step is best effort — a failure here surfaces later, where it can be explained.
/// </summary>
public static class WarmUp
{
    private static readonly TimeSpan MinimumDuration = TimeSpan.FromSeconds(1.6);

    public static async Task RunAsync(Action<string, double> report)
    {
        var started = Stopwatch.StartNew();
        var steps = new (string Status, Func<Task> Run)[]
        {
            ("Preparing the data folder…", () => Task.Run(PrepareDataFolder)),
            ("Starting the browser engine (WebView2)…", async () => await WebViewEnvironment.GetAsync()),
            ("Loading the Windows voices…", () => Task.Run(() => SpeakerVoice.All.Count)),
            ("Preparing the audio engine…", () => Task.Run(NAudio.MediaFoundation.MediaFoundationApi.Startup)),
            ("Checking the speech recognition models…", () => Task.Run(() =>
                Directory.CreateDirectory(Path.Combine(AppPaths.DataFolder, "models")).EnumerateFiles("ggml-*.bin").Count())),
        };

        for (int i = 0; i < steps.Length; i++)
        {
            report(steps[i].Status, (double)i / steps.Length);
            try { await steps[i].Run(); }
            catch (Exception) { } // reported where the component is used
        }
        report("Ready", 1);

        var remaining = MinimumDuration - started.Elapsed;
        if (remaining > TimeSpan.Zero) await Task.Delay(remaining);
    }

    private static void PrepareDataFolder()
    {
        _ = AppPaths.DataFolder;
        _ = UserStore.HasUsers;
        // PDF copies shown by the previous run.
        foreach (var file in Directory.EnumerateFiles(PreviewView.PdfFolder, "*.pdf"))
        {
            try { File.Delete(file); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }
}
