using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace HB.Speech;

/// <summary>Reads and appends transcription entries in a workspace's markdown file.</summary>
public static class TranscriptStore
{
    private const string TimestampFormat = "yyyy-MM-dd HH:mm:ss";

    private static readonly Regex EntryHeader =
        new(@"^## (\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2})(?: · (.+))?$", RegexOptions.Compiled);

    public static void Append(Workspace workspace, TranscriptEntry entry)
    {
        var builder = new StringBuilder();
        if (!File.Exists(workspace.MarkdownPath))
            builder.Append("# ").Append(workspace.Name).Append(" — Transcriptions\n\n");

        builder.Append("## ").Append(entry.Timestamp.ToString(TimestampFormat, CultureInfo.InvariantCulture))
            .Append(" · ").Append(entry.Language).Append("\n\n")
            .Append(entry.Text).Append("\n\n");

        File.AppendAllText(workspace.MarkdownPath, builder.ToString(), new UTF8Encoding(false));
    }

    public static List<TranscriptEntry> Load(Workspace workspace)
    {
        var entries = new List<TranscriptEntry>();
        if (!File.Exists(workspace.MarkdownPath)) return entries;

        DateTime timestamp = default;
        string language = "";
        StringBuilder? body = null;

        void Flush()
        {
            var text = body?.ToString().Trim();
            if (!string.IsNullOrEmpty(text)) entries.Add(new TranscriptEntry(timestamp, language, text));
        }

        foreach (var line in File.ReadLines(workspace.MarkdownPath))
        {
            var match = EntryHeader.Match(line);
            if (match.Success && DateTime.TryParseExact(match.Groups[1].Value, TimestampFormat,
                    CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
            {
                Flush();
                timestamp = parsed;
                language = match.Groups[2].Value;
                body = new StringBuilder();
            }
            else
            {
                body?.AppendLine(line);
            }
        }
        Flush();
        return entries;
    }
}
