using System.Text.Json.Serialization;

namespace MeetMemo.Contracts;

/// <summary>
/// Событие временной шкалы — одна строка timeline.jsonl (ТЗ 11.2).
/// Единственный писатель — TimelineStore; порядок событий определяется монотонной шкалой.
/// </summary>
public sealed record TimelineEvent
{
    [JsonPropertyName("offset_ms")]
    public required long OffsetMs { get; init; }

    [JsonPropertyName("timestamp_local")]
    public required DateTimeOffset TimestampLocal { get; init; }

    [JsonPropertyName("type")]
    public required string Type { get; init; }

    [JsonPropertyName("severity")]
    public EventSeverity Severity { get; init; } = EventSeverity.Info;

    /// <summary>
    /// Полезная нагрузка события. Ключи и значения — только не-персональные технические данные:
    /// в timeline.jsonl не попадает текст стенограммы и содержимое снимков (ТЗ 15.1).
    /// </summary>
    [JsonPropertyName("data")]
    public IReadOnlyDictionary<string, string>? Data { get; init; }
}

public enum EventSeverity
{
    Info,
    Warning,
    Error
}

/// <summary>Канонические имена событий из ТЗ 11.2 — строками, чтобы схема оставалась открытой.</summary>
public static class EventTypes
{
    public const string SessionStarted = "session_started";
    public const string SessionStopped = "session_stopped";
    public const string SessionRecovered = "session_recovered";

    public const string RecordingPaused = "recording_paused";
    public const string RecordingResumed = "recording_resumed";

    public const string AudioSourceChanged = "audio_source_changed";
    public const string AudioSilence = "audio_silence";
    public const string AudioOverrun = "audio_overrun";
    public const string AudioLevel = "audio_level";

    public const string TargetWindowChanged = "target_window_changed";
    public const string TargetLost = "target_lost";
    public const string TargetRestored = "target_restored";

    public const string ScreenshotCreated = "screenshot_created";
    public const string ImportantMarkAdded = "important_mark_added";

    public const string TranscriptionSegmentCreated = "transcription_segment_created";
    public const string TranscriptionFinalized = "transcription_finalized";
    public const string BackendFallback = "backend_fallback";

    public const string DegradationApplied = "degradation_applied";
    public const string DiskSpaceLow = "disk_space_low";
    public const string ExportCreated = "export_created";
}
