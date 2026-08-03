using MeetMemo.Contracts;

namespace MeetMemo.Core;

/// <summary>Параметры запуска сессии, собранные экраном подтверждения.</summary>
public sealed record SessionStartRequest
{
    public required string Title { get; init; }

    public required string MeetingsRoot { get; init; }

    public AudioMode AudioMode { get; init; } = AudioMode.ApplicationProcessTree;

    public string? MicrophoneDeviceId { get; init; }

    public string? MicrophoneDeviceName { get; init; }

    /// <summary>Сохранять ли аудиофайлы на диск. Захват идёт всегда — он нужен ASR (ТЗ 8.2).</summary>
    public bool SaveAudioFiles { get; init; } = true;

    public bool AutoScreenshotsEnabled { get; init; } = true;

    public TargetSelection? Target { get; init; }
}

/// <summary>
/// Выбранная цель: окно (для картинки) и процесс (для звука) хранятся раздельно — ТЗ 6.2.
/// </summary>
public sealed record TargetSelection
{
    public nint WindowHandle { get; init; }

    public int ProcessId { get; init; }

    public string? ApplicationName { get; init; }

    public string? WindowTitle { get; init; }

    public string? ExecutablePath { get; init; }
}

/// <summary>Состояния приложения из ТЗ 5.2.</summary>
public enum SessionState
{
    Idle,
    Starting,
    Recording,
    Paused,
    Finalizing,
    Completed,
    Failed
}

/// <summary>
/// Подсистема, участвующая в жизненном цикле сессии (аудио, захват окна, ASR, хранилище).
/// Каждый метод вызывается контроллером под таймаутом: зависшая подсистема не должна
/// блокировать остановку остальных (ТЗ 16, риск R-09).
/// </summary>
public interface ISessionParticipant
{
    /// <summary>Имя для журналов и диагностики.</summary>
    string Name { get; }

    /// <summary>Порядок остановки: больше — останавливается позже. Аудио и стенограмма — последние.</summary>
    int StopOrder => 0;

    Task StartAsync(SessionContext context, CancellationToken ct);

    Task PauseAsync(CancellationToken ct) => Task.CompletedTask;

    Task ResumeAsync(CancellationToken ct) => Task.CompletedTask;

    Task StopAsync(CancellationToken ct);
}

/// <summary>Всё, что нужно подсистеме во время сессии.</summary>
public sealed class SessionContext
{
    public required string SessionId { get; init; }

    public required string FolderPath { get; init; }

    public required ISessionClock Clock { get; init; }

    public required SessionStartRequest Request { get; init; }

    public required IEventSink Events { get; init; }
}

/// <summary>Приёмник событий временной шкалы. Реализация пишет timeline.jsonl.</summary>
public interface IEventSink
{
    void Publish(TimelineEvent evt);
}

/// <summary>Хелперы публикации, чтобы подсистемы не собирали TimelineEvent руками.</summary>
public static class EventSinkExtensions
{
    public static void Emit(
        this IEventSink sink,
        ISessionClock clock,
        string type,
        EventSeverity severity = EventSeverity.Info,
        IReadOnlyDictionary<string, string>? data = null)
    {
        var offset = clock.ElapsedMs;
        sink.Publish(new TimelineEvent
        {
            OffsetMs = offset,
            TimestampLocal = clock.ToLocal(offset),
            Type = type,
            Severity = severity,
            Data = data
        });
    }

    public static void Emit(
        this IEventSink sink,
        ISessionClock clock,
        string type,
        params (string Key, string Value)[] data)
        => sink.Emit(clock, type, EventSeverity.Info,
            data.Length == 0 ? null : data.ToDictionary(x => x.Key, x => x.Value));
}
