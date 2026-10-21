using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace HB.Documents;

public sealed record LatexResult(bool Success, string? PdfPath, string Errors);

/// <summary>Compiles a .tex file to PDF with the TeX distribution installed on the machine.</summary>
public static class LatexCompiler
{
    /// <summary>Auxiliary files go here, next to the main file, to keep the folder clean.</summary>
    public const string BuildFolderName = ".latex-build";

    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(3);
    private static readonly Regex ErrorLine = new(@"^(!|l\.\d+ |error:|.+:\d+: )", RegexOptions.Compiled);

    public static async Task<LatexResult> CompileAsync(string mainFile)
    {
        var folder = Path.GetDirectoryName(mainFile)!;
        var name = Path.GetFileNameWithoutExtension(mainFile);
        var buildFolder = Path.Combine(folder, BuildFolderName);
        var builtPdf = Path.Combine(buildFolder, name + ".pdf");

        // The compiler runs in the document's folder with relative names, which keeps its messages short.
        var source = Path.GetFileName(mainFile);
        string output;
        int exitCode;
        try
        {
            Directory.CreateDirectory(buildFolder);
            if (FindOnPath("tectonic.exe") is { } tectonic)
            {
                (exitCode, output) = await RunAsync(tectonic, $"--outdir \"{BuildFolderName}\" \"{source}\"", folder);
            }
            else if (FindOnPath("latexmk.exe") is { } latexmk && FindOnPath("perl.exe") != null)
            {
                (exitCode, output) = await RunAsync(latexmk,
                    $"-pdf -interaction=nonstopmode -halt-on-error -file-line-error -outdir=\"{BuildFolderName}\" \"{source}\"",
                    folder);
            }
            else if (FindOnPath("pdflatex.exe") is { } pdflatex)
            {
                // MiKTeX would otherwise open a dialog to ask before installing a missing package.
                var installer = pdflatex.Contains("miktex", StringComparison.OrdinalIgnoreCase) ? "-enable-installer " : "";
                var arguments = $"{installer}-interaction=nonstopmode -halt-on-error -file-line-error " +
                                $"-output-directory=\"{BuildFolderName}\" \"{source}\"";
                (exitCode, output) = await RunAsync(pdflatex, arguments, folder);
                // A second pass resolves the table of contents and cross references.
                if (exitCode == 0 && output.Contains("Rerun to get", StringComparison.OrdinalIgnoreCase))
                    (exitCode, output) = await RunAsync(pdflatex, arguments, folder);
            }
            else
            {
                return new LatexResult(false, null,
                    "No LaTeX compiler was found (pdflatex, latexmk or tectonic). Install MiKTeX or TeX Live and restart the app.");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            return new LatexResult(false, null, "The compiler could not run: " + ex.Message);
        }

        if (exitCode != 0 || !File.Exists(builtPdf))
            return new LatexResult(false, null, SummarizeErrors(output));

        // The finished document also goes next to its source, where one expects to find it.
        try { File.Copy(builtPdf, Path.Combine(folder, name + ".pdf"), overwrite: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } // open in another program
        return new LatexResult(true, builtPdf, "");
    }

    private static async Task<(int ExitCode, string Output)> RunAsync(string program, string arguments, string folder)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(program, arguments)
            {
                WorkingDirectory = folder,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            },
        };
        process.StartInfo.Environment["max_print_line"] = "1000"; // do not wrap messages at 79 columns
        process.Start();
        process.StandardInput.Close(); // TeX must never wait for an answer
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();

        using var timeout = new CancellationTokenSource(Timeout);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            return (-1, "error: the compilation took more than 3 minutes and was stopped.");
        }
        return (process.ExitCode, await standardOutput + "\n" + await standardError);
    }

    /// <summary>Keeps the lines of the compiler output that say what went wrong.</summary>
    private static string SummarizeErrors(string output)
    {
        var lines = output.Split('\n').Select(l => l.TrimEnd()).Where(l => l.Length > 0).ToList();
        var errors = new StringBuilder();
        for (int i = 0; i < lines.Count; i++)
        {
            if (!ErrorLine.IsMatch(lines[i])) continue;
            errors.AppendLine(lines[i]);
            // After "! Undefined control sequence." the next line shows where.
            if (lines[i].StartsWith('!') && i + 1 < lines.Count && !ErrorLine.IsMatch(lines[i + 1]))
                errors.AppendLine(lines[i + 1]);
        }
        return errors.Length > 0 ? errors.ToString().TrimEnd() : string.Join(Environment.NewLine, lines.TakeLast(12));
    }

    private static string? FindOnPath(string fileName)
    {
        foreach (var folder in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
        {
            if (string.IsNullOrWhiteSpace(folder)) continue;
            try
            {
                var candidate = Path.Combine(folder.Trim(), fileName);
                if (File.Exists(candidate)) return candidate;
            }
            catch (ArgumentException) { }
        }
        return null;
    }
}
