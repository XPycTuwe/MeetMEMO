using MeetMemo.Storage;

namespace MeetMemo.Export;

/// <summary>Файл-кандидат на попадание в ZIP-архив.</summary>
public sealed record ExportItem
{
    public required string RelativePath { get; init; }

    public required string FullPath { get; init; }

    public required long SizeBytes { get; init; }

    public required ExportCategory Category { get; init; }

    /// <summary>Включён ли файл в архив. Пользователь может снять галочку (ТЗ 13.1).</summary>
    public bool Included { get; set; } = true;
}

public enum ExportCategory
{
    Transcript,
    Metadata,
    Screenshots,
    Audio,
    Diagnostics,
    Output
}

/// <summary>
/// Состав будущего архива. Показывается пользователю до создания ZIP: он должен видеть,
/// что именно покинет его компьютер, и иметь возможность исключить лишнее (ТЗ 13.1, AC-21).
/// </summary>
public sealed class ExportPlan
{
    public required string MeetingFolder { get; init; }

    public required IReadOnlyList<ExportItem> Items { get; init; }

    public long TotalBytes => Items.Where(i => i.Included).Sum(i => i.SizeBytes);

    public int IncludedCount => Items.Count(i => i.Included);

    /// <summary>Есть ли в составе исходное аудио — повод предупредить отдельно.</summary>
    public bool ContainsAudio => Items.Any(i => i.Included && i.Category == ExportCategory.Audio);

    /// <summary>Есть ли в пакете слова на разбор — упоминать их в README только тогда.</summary>
    /// <summary>Человек оставил заметки — упоминать их в README только тогда.</summary>
    public bool ContainsNotes =>
        Items.Any(i => i.Included && i.RelativePath == "notes.md");

    public bool ContainsGlossaryCandidates =>
        Items.Any(i => i.Included && i.RelativePath == "glossary-candidates.md");

    public static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} Б",
        < 1024 * 1024 => $"{bytes / 1024.0:F1} КБ",
        < 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024):F1} МБ",
        _ => $"{bytes / (1024.0 * 1024 * 1024):F2} ГБ"
    };
}

/// <summary>Сборка состава архива из папки встречи.</summary>
public static class ExportPlanBuilder
{
    /// <summary>
    /// По умолчанию аудио в архив не включается: файлы тяжёлые, а для мемо достаточно
    /// стенограммы и снимков. Диагностика тоже отключена — она нужна для разбора сбоев,
    /// а не для анализа встречи.
    /// </summary>
    public static ExportPlan Build(string meetingFolderPath, bool includeAudio = false)
    {
        var folder = new MeetingFolder(meetingFolderPath);
        var items = new List<ExportItem>();

        void AddFile(string path, ExportCategory category, bool included = true)
        {
            if (!File.Exists(path)) return;
            var info = new FileInfo(path);
            items.Add(new ExportItem
            {
                RelativePath = Path.GetRelativePath(meetingFolderPath, path).Replace('\\', '/'),
                FullPath = path,
                SizeBytes = info.Length,
                Category = category,
                Included = included
            });
        }

        void AddDirectory(string dir, ExportCategory category, bool included = true)
        {
            if (!Directory.Exists(dir)) return;
            foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                AddFile(file, category, included);
        }

        AddFile(folder.SessionJson, ExportCategory.Metadata);
        AddFile(folder.TimelineJsonl, ExportCategory.Metadata);
        AddFile(folder.TranscriptJsonl, ExportCategory.Transcript);
        AddFile(folder.TranscriptMd, ExportCategory.Transcript);
        AddFile(folder.GlossaryMd, ExportCategory.Transcript);
        AddFile(Path.Combine(meetingFolderPath, "glossary-candidates.md"), ExportCategory.Transcript);
        AddFile(folder.NotesMd, ExportCategory.Metadata);
        AddFile(folder.ContextJsonl, ExportCategory.Metadata);
        AddFile(Path.Combine(meetingFolderPath, "transcript.live.jsonl"), ExportCategory.Transcript, false);

        AddDirectory(folder.ScreenshotsDir, ExportCategory.Screenshots);
        AddDirectory(folder.AudioDir, ExportCategory.Audio, includeAudio);
        AddDirectory(folder.DiagnosticsDir, ExportCategory.Diagnostics, false);
        AddDirectory(folder.OutputDir, ExportCategory.Output);

        return new ExportPlan
        {
            MeetingFolder = meetingFolderPath,
            Items = items
        };
    }
}
