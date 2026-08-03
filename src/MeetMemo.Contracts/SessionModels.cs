using System.Text.Json.Serialization;

namespace MeetMemo.Contracts;

/// <summary>Версия схем пакета встречи. Несовместимые изменения повышают major (ТЗ 12.2).</summary>
public static class SchemaVersions
{
    public const string Current = "1.0";
}

/// <summary>Состояние сессии (ТЗ приложение A.1).</summary>
public enum SessionStatus
{
    Recording,
    Paused,
    Finalizing,
    Completed,
    CompletedWithWarnings,
    Recovered,
    Failed
}

/// <summary>Режим захвата звука приложения (ТЗ 8.1).</summary>
public enum AudioMode
{
    /// <summary>Звук дерева процессов выбранного приложения (WASAPI process loopback).</summary>
    ApplicationProcessTree,

    /// <summary>Общий loopback устройства вывода — резервный режим.</summary>
    System,

    /// <summary>Только микрофон (очная встреча).</summary>
    MicrophoneOnly
}

/// <summary>Источник аудиоканала, попадает в каждый сегмент стенограммы.</summary>
public enum AudioChannel
{
    Microphone,
    Application,
    System
}

/// <summary>Корневой session.json (ТЗ приложение A).</summary>
public sealed record SessionManifest
{
    [JsonPropertyName("schema_version")]
    public string SchemaVersion { get; init; } = SchemaVersions.Current;

    [JsonPropertyName("session_id")]
    public required string SessionId { get; init; }

    [JsonPropertyName("title")]
    public required string Title { get; init; }

    [JsonPropertyName("start_utc")]
    public required DateTimeOffset StartUtc { get; init; }

    [JsonPropertyName("start_local")]
    public required DateTimeOffset StartLocal { get; init; }

    [JsonPropertyName("timezone")]
    public required string Timezone { get; init; }

    /// <summary>Значение монотонных часов на момент старта — база для всех offset_ms (ТЗ 11.1).</summary>
    [JsonPropertyName("monotonic_origin")]
    public long MonotonicOrigin { get; init; }

    [JsonPropertyName("status")]
    public SessionStatus Status { get; init; } = SessionStatus.Recording;

    [JsonPropertyName("duration_ms")]
    public long? DurationMs { get; init; }

    [JsonPropertyName("app_version")]
    public string? AppVersion { get; init; }

    [JsonPropertyName("target")]
    public TargetInfo? Target { get; init; }

    [JsonPropertyName("audio")]
    public AudioInfo Audio { get; init; } = new();

    [JsonPropertyName("screenshots")]
    public ScreenshotsInfo Screenshots { get; init; } = new();

    [JsonPropertyName("transcription")]
    public TranscriptionInfo Transcription { get; init; } = new();

    /// <summary>Незакрытые проблемы сессии — основание для статуса completed_with_warnings.</summary>
    [JsonPropertyName("warnings")]
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
}

/// <summary>Целевое приложение и окно (ТЗ 6.2): картинка привязана к окну, звук — к процессу.</summary>
public sealed record TargetInfo
{
    [JsonPropertyName("application")]
    public string? Application { get; init; }

    [JsonPropertyName("process_id_at_start")]
    public int? ProcessIdAtStart { get; init; }

    [JsonPropertyName("window_title_at_start")]
    public string? WindowTitleAtStart { get; init; }

    [JsonPropertyName("executable")]
    public string? Executable { get; init; }
}

public sealed record AudioInfo
{
    [JsonPropertyName("mode")]
    public AudioMode Mode { get; init; } = AudioMode.ApplicationProcessTree;

    [JsonPropertyName("microphone_device")]
    public string? MicrophoneDevice { get; init; }

    /// <summary>Сохранение аудиофайлов — отключаемая опция (ТЗ 8.2, решение заказчика).</summary>
    [JsonPropertyName("save_files")]
    public bool SaveFiles { get; init; } = true;

    [JsonPropertyName("sample_rate")]
    public int SampleRate { get; init; } = 48000;

    [JsonPropertyName("files")]
    public IReadOnlyList<string> Files { get; init; } = Array.Empty<string>();
}

public sealed record ScreenshotsInfo
{
    [JsonPropertyName("auto_enabled")]
    public bool AutoEnabled { get; init; } = true;

    [JsonPropertyName("index")]
    public string Index { get; init; } = "screenshots/index.json";

    [JsonPropertyName("count")]
    public int Count { get; init; }
}

public sealed record TranscriptionInfo
{
    [JsonPropertyName("engine")]
    public string Engine { get; init; } = "sherpa-onnx";

    [JsonPropertyName("model")]
    public string? Model { get; init; }

    [JsonPropertyName("device")]
    public string Device { get; init; } = "cpu";

    [JsonPropertyName("language")]
    public string Language { get; init; } = "ru";

    /// <summary>Живая стенограмма — ядро MVP (P0, решение заказчика).</summary>
    [JsonPropertyName("live")]
    public bool Live { get; init; } = true;

    [JsonPropertyName("final_pass_completed")]
    public bool FinalPassCompleted { get; init; }
}
