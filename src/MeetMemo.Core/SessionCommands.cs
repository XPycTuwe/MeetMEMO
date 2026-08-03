namespace MeetMemo.Core;

/// <summary>
/// Команда контроллеру. Все источники (трей, плавающая панель, горячие клавиши) кладут команды
/// в один канал, поэтому гонок между ними не возникает по построению (ТЗ 7.3).
/// </summary>
public abstract record SessionCommand
{
    /// <summary>Ожидание результата: источник команды узнаёт, приняли её или отклонили.</summary>
    public TaskCompletionSource<CommandResult> Completion { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public sealed record Start(SessionStartRequest Request) : SessionCommand;

    public sealed record Pause : SessionCommand;

    public sealed record Resume : SessionCommand;

    public sealed record Stop : SessionCommand;

    /// <summary>Снимок целевого окна.</summary>
    public sealed record CaptureWindow(bool Important = false) : SessionCommand;

    /// <summary>Снимок рабочего стола — только вручную (ТЗ 9.1).</summary>
    public sealed record CaptureDesktop(string? MonitorId = null) : SessionCommand;

    /// <summary>Маркер «Важно» со связанным снимком.</summary>
    public sealed record MarkImportant : SessionCommand;

    /// <summary>Переключение источника звука без остановки сессии (ТЗ 8.2, AC-05).</summary>
    public sealed record SwitchAudioSource(Contracts.AudioMode Mode) : SessionCommand;
}

public sealed record CommandResult(bool Accepted, string? Message = null)
{
    public static CommandResult Ok() => new(true);

    public static CommandResult Ok(string message) => new(true, message);

    public static CommandResult Rejected(string message) => new(false, message);
}
