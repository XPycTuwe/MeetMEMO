using System.Text.Json.Serialization;

namespace MeetMemo.Contracts;

/// <summary>
/// Одна строка transcript.jsonl (ТЗ 12.3). Дозаписывается по ходу встречи, flush не реже 5 с.
/// </summary>
public sealed record TranscriptSegment
{
    [JsonPropertyName("start_ms")]
    public required long StartMs { get; init; }

    [JsonPropertyName("end_ms")]
    public required long EndMs { get; init; }

    [JsonPropertyName("source")]
    public required AudioChannel Source { get; init; }

    [JsonPropertyName("text")]
    public required string Text { get; init; }

    [JsonPropertyName("language")]
    public string Language { get; init; } = "ru";

    /// <summary>false — черновой live-результат, может быть уточнён финальным проходом.</summary>
    [JsonPropertyName("final")]
    public bool Final { get; init; }

    [JsonPropertyName("confidence")]
    public double? Confidence { get; init; }

    /// <summary>Движок, выдавший сегмент: sherpa-onnx (live) или whisper.net (финальный проход).</summary>
    [JsonPropertyName("engine")]
    public string? Engine { get; init; }

    /// <summary>
    /// Кто говорит: «spk1», «spk2»… — голоса, различённые диаризацией в звуке приложения.
    /// Это метки тембров, а не имена: имя можно узнать только из самой речи. У сегментов
    /// микрофона всегда null — этот канал целиком принадлежит владельцу компьютера.
    /// </summary>
    [JsonPropertyName("speaker")]
    public string? Speaker { get; init; }
}

/// <summary>Тип снимка (ТЗ 9.1).</summary>
public enum ScreenshotKind
{
    ApplicationManual,
    DesktopManual,
    Important,
    ApplicationAuto
}

/// <summary>Запись в screenshots/index.json (ТЗ приложение B).</summary>
public sealed record ScreenshotEntry
{
    [JsonPropertyName("file")]
    public required string File { get; init; }

    [JsonPropertyName("offset_ms")]
    public required long OffsetMs { get; init; }

    [JsonPropertyName("timestamp_local")]
    public required DateTimeOffset TimestampLocal { get; init; }

    [JsonPropertyName("type")]
    public required ScreenshotKind Type { get; init; }

    [JsonPropertyName("manual")]
    public bool Manual { get; init; }

    [JsonPropertyName("important")]
    public bool Important { get; init; }

    [JsonPropertyName("trigger")]
    public string? Trigger { get; init; }

    [JsonPropertyName("application")]
    public string? Application { get; init; }

    [JsonPropertyName("window_title")]
    public string? WindowTitle { get; init; }

    [JsonPropertyName("monitor")]
    public string? Monitor { get; init; }

    [JsonPropertyName("width")]
    public int Width { get; init; }

    [JsonPropertyName("height")]
    public int Height { get; init; }
}

/// <summary>Файл screenshots/index.json целиком.</summary>
public sealed record ScreenshotIndex
{
    [JsonPropertyName("schema_version")]
    public string SchemaVersion { get; init; } = SchemaVersions.Current;

    [JsonPropertyName("items")]
    public IReadOnlyList<ScreenshotEntry> Items { get; init; } = Array.Empty<ScreenshotEntry>();
}

/// <summary>Структурированное поручение в output/actions.json (ТЗ 13.2). Заполняет Claude Skill.</summary>
public sealed record ActionItem
{
    [JsonPropertyName("description")]
    public required string Description { get; init; }

    [JsonPropertyName("owner")]
    public string Owner { get; init; } = "не определён";

    [JsonPropertyName("due")]
    public string Due { get; init; } = "не определён";

    [JsonPropertyName("status")]
    public string Status { get; init; } = "открыто";

    [JsonPropertyName("timecode_ms")]
    public long? TimecodeMs { get; init; }

    [JsonPropertyName("confidence")]
    public string Confidence { get; init; } = "средняя";

    [JsonPropertyName("screenshot")]
    public string? Screenshot { get; init; }
}
