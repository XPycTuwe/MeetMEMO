using System.Text;

namespace MeetMemo.Storage;

/// <summary>
/// Раскладка папки встречи (ТЗ 12.1). Единственное место, где зашиты имена файлов —
/// экспорт, восстановление и Skill опираются на него.
/// </summary>
public sealed class MeetingFolder
{
    public MeetingFolder(string root)
    {
        Root = root;
    }

    public string Root { get; }

    public string SessionJson => Path.Combine(Root, "session.json");
    public string TimelineJsonl => Path.Combine(Root, "timeline.jsonl");
    public string TranscriptJsonl => Path.Combine(Root, "transcript.jsonl");
    public string TranscriptMd => Path.Combine(Root, "transcript.md");
    public string GlossaryMd => Path.Combine(Root, "glossary.md");
    public string LockFile => Path.Combine(Root, "session.lock");

    public string AudioDir => Path.Combine(Root, "audio");
    public string MicrophoneAudio(string ext = "wav") => Path.Combine(AudioDir, $"microphone.{ext}");
    public string ApplicationAudio(string ext = "wav") => Path.Combine(AudioDir, $"application.{ext}");

    public string ScreenshotsDir => Path.Combine(Root, "screenshots");
    public string ScreenshotIndex => Path.Combine(ScreenshotsDir, "index.json");

    public string DiagnosticsDir => Path.Combine(Root, "diagnostics");
    public string AppLog => Path.Combine(DiagnosticsDir, "app.log");
    public string DiagnosticsJsonl => Path.Combine(DiagnosticsDir, "diagnostics.jsonl");

    /// <summary>Папка для результатов Skill: наполняется пользователем вручную из Claude (ТЗ 12.2).</summary>
    public string OutputDir => Path.Combine(Root, "output");

    public void EnsureCreated(bool withAudio)
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(ScreenshotsDir);
        Directory.CreateDirectory(DiagnosticsDir);
        Directory.CreateDirectory(OutputDir);
        if (withAudio) Directory.CreateDirectory(AudioDir);
    }
}

/// <summary>Создание папки встречи с безопасным именем (ТЗ 12.2).</summary>
public static class MeetingFolderFactory
{
    private static readonly string[] ReservedNames =
    [
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    ];

    private const int MaxTitleLength = 60;

    public static MeetingFolder Create(string meetingsRoot, DateTimeOffset startLocal, string title)
    {
        var safeTitle = Sanitize(title);
        var stamp = startLocal.ToString("yyyy-MM-dd_HHmm");
        var baseName = string.IsNullOrEmpty(safeTitle) ? stamp : $"{stamp}_{safeTitle}";

        var path = Path.Combine(meetingsRoot, baseName);
        var suffix = 2;
        while (Directory.Exists(path))
        {
            path = Path.Combine(meetingsRoot, $"{baseName}-{suffix}");
            suffix++;
        }

        return new MeetingFolder(path);
    }

    /// <summary>Приводит пользовательское название к безопасному для Windows имени папки.</summary>
    public static string Sanitize(string title)
    {
        if (string.IsNullOrWhiteSpace(title)) return string.Empty;

        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(title.Length);
        var lastWasSeparator = false;

        foreach (var ch in title.Trim())
        {
            if (invalid.Contains(ch) || ch is '\\' or '/' or ':')
            {
                if (!lastWasSeparator) { sb.Append('_'); lastWasSeparator = true; }
                continue;
            }

            if (char.IsWhiteSpace(ch))
            {
                if (!lastWasSeparator) { sb.Append('_'); lastWasSeparator = true; }
                continue;
            }

            sb.Append(ch);
            lastWasSeparator = false;
        }

        var result = sb.ToString().Trim('_', '.', ' ');
        if (result.Length > MaxTitleLength) result = result[..MaxTitleLength].TrimEnd('_', '.', ' ');

        // Зарезервированные имена Windows нельзя использовать даже с расширением.
        if (ReservedNames.Contains(result, StringComparer.OrdinalIgnoreCase))
            result += "_meeting";

        return result;
    }
}
