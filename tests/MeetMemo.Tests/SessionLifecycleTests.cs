using MeetMemo.Contracts;
using MeetMemo.Core;
using MeetMemo.Storage;
using Xunit;

namespace MeetMemo.Tests;

/// <summary>Подсистема-заглушка: фиксирует порядок вызовов жизненного цикла.</summary>
internal sealed class FakeParticipant : ISessionParticipant
{
    private readonly List<string> _log;

    public FakeParticipant(string name, List<string> log, int stopOrder = 0)
    {
        Name = name;
        _log = log;
        StopOrder = stopOrder;
    }

    public string Name { get; }
    public int StopOrder { get; }
    public bool ThrowOnStart { get; init; }
    public TimeSpan StopDelay { get; init; }

    public Task StartAsync(SessionContext context, CancellationToken ct)
    {
        if (ThrowOnStart) throw new InvalidOperationException($"{Name} не стартовал");
        _log.Add($"start:{Name}");
        return Task.CompletedTask;
    }

    public Task PauseAsync(CancellationToken ct)
    {
        _log.Add($"pause:{Name}");
        return Task.CompletedTask;
    }

    public Task ResumeAsync(CancellationToken ct)
    {
        _log.Add($"resume:{Name}");
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken ct)
    {
        if (StopDelay > TimeSpan.Zero) await Task.Delay(StopDelay, ct);
        _log.Add($"stop:{Name}");
    }
}

public class SessionLifecycleTests
{
    private static SessionStartRequest Request(string root) => new()
    {
        Title = "Тестовая встреча",
        MeetingsRoot = root,
        AudioMode = AudioMode.MicrophoneOnly,
        SaveAudioFiles = false,
        AutoScreenshotsEnabled = false
    };

    [Fact]
    public async Task Полный_цикл_создаёт_валидный_пакет()
    {
        using var dir = new TempDir();
        var log = new List<string>();
        var store = new MeetingSessionStore();

        await using var controller = new SessionController(
            new ISessionParticipant[] { new FakeParticipant("audio", log) },
            store,
            new DegradationPolicy());

        var started = await controller.SendAsync(new SessionCommand.Start(Request(dir.Path)));
        Assert.True(started.Accepted);
        Assert.Equal(SessionState.Recording, controller.State);

        var stopped = await controller.SendAsync(new SessionCommand.Stop());
        Assert.True(stopped.Accepted);
        Assert.Equal(SessionState.Completed, controller.State);

        var folder = Directory.GetDirectories(dir.Path).Single();
        Assert.True(File.Exists(Path.Combine(folder, "session.json")));
        Assert.True(File.Exists(Path.Combine(folder, "timeline.jsonl")));
        Assert.True(File.Exists(Path.Combine(folder, "transcript.md")));
        Assert.True(File.Exists(Path.Combine(folder, "glossary.md")));

        // Метка активной сессии должна быть снята: иначе следующий запуск сочтёт встречу брошенной.
        Assert.False(File.Exists(Path.Combine(folder, "session.lock")));

        var manifest = await AtomicJsonStore.ReadAsync<SessionManifest>(
            Path.Combine(folder, "session.json"), JsonSetup.Pretty);
        Assert.NotNull(manifest);
        Assert.Equal(SessionStatus.Completed, manifest!.Status);

        await store.DisposeAsync();
    }

    [Fact]
    public async Task Пауза_и_продолжение_фиксируются_событиями()
    {
        using var dir = new TempDir();
        var log = new List<string>();
        var store = new MeetingSessionStore();

        await using var controller = new SessionController(
            new ISessionParticipant[] { new FakeParticipant("audio", log) },
            store,
            new DegradationPolicy());

        await controller.SendAsync(new SessionCommand.Start(Request(dir.Path)));
        await controller.SendAsync(new SessionCommand.Pause());
        Assert.Equal(SessionState.Paused, controller.State);

        await controller.SendAsync(new SessionCommand.Resume());
        Assert.Equal(SessionState.Recording, controller.State);

        await controller.SendAsync(new SessionCommand.Stop());

        var folder = Directory.GetDirectories(dir.Path).Single();
        var events = JsonlWriter
            .ReadAll<TimelineEvent>(Path.Combine(folder, "timeline.jsonl"))
            .Select(e => e.Type)
            .ToList();

        Assert.Contains(EventTypes.SessionStarted, events);
        Assert.Contains(EventTypes.RecordingPaused, events);
        Assert.Contains(EventTypes.RecordingResumed, events);
        Assert.Contains(EventTypes.SessionStopped, events);

        await store.DisposeAsync();
    }

    [Fact]
    public async Task Недопустимые_переходы_отклоняются()
    {
        using var dir = new TempDir();
        var store = new MeetingSessionStore();

        await using var controller = new SessionController(
            new ISessionParticipant[] { new FakeParticipant("audio", new List<string>()) },
            store,
            new DegradationPolicy());

        // Пауза до старта и стоп без записи не должны ничего ломать.
        Assert.False((await controller.SendAsync(new SessionCommand.Pause())).Accepted);
        Assert.False((await controller.SendAsync(new SessionCommand.Stop())).Accepted);

        await controller.SendAsync(new SessionCommand.Start(Request(dir.Path)));

        // Повторный старт при активной записи тоже отклоняется.
        Assert.False((await controller.SendAsync(new SessionCommand.Start(Request(dir.Path)))).Accepted);

        await controller.SendAsync(new SessionCommand.Stop());
        await store.DisposeAsync();
    }

    [Fact]
    public async Task Подсистемы_останавливаются_в_порядке_StopOrder()
    {
        using var dir = new TempDir();
        var log = new List<string>();
        var store = new MeetingSessionStore();

        await using var controller = new SessionController(
            new ISessionParticipant[]
            {
                new FakeParticipant("audio", log, stopOrder: 100),
                new FakeParticipant("screenshots", log, stopOrder: 50),
                new FakeParticipant("asr", log, stopOrder: 90)
            },
            store,
            new DegradationPolicy());

        await controller.SendAsync(new SessionCommand.Start(Request(dir.Path)));
        await controller.SendAsync(new SessionCommand.Stop());

        var stops = log.Where(l => l.StartsWith("stop:")).ToList();

        // Аудио останавливается последним: запись ценнее вспомогательных функций.
        Assert.Equal(new[] { "stop:screenshots", "stop:asr", "stop:audio" }, stops);

        await store.DisposeAsync();
    }

    [Fact]
    public async Task Отказ_некритичной_подсистемы_не_срывает_запись()
    {
        using var dir = new TempDir();
        var log = new List<string>();
        var store = new MeetingSessionStore();

        await using var controller = new SessionController(
            new ISessionParticipant[]
            {
                new FakeParticipant("asr", log) { ThrowOnStart = true },
                new FakeParticipant("audio", log, stopOrder: 100)
            },
            store,
            new DegradationPolicy());

        var started = await controller.SendAsync(new SessionCommand.Start(Request(dir.Path)));

        // Требование ТЗ: сбой распознавания не должен останавливать запись.
        Assert.True(started.Accepted);
        Assert.Equal(SessionState.Recording, controller.State);

        await controller.SendAsync(new SessionCommand.Stop());

        var folder = Directory.GetDirectories(dir.Path).Single();
        var manifest = await AtomicJsonStore.ReadAsync<SessionManifest>(
            Path.Combine(folder, "session.json"), JsonSetup.Pretty);

        Assert.Equal(SessionStatus.CompletedWithWarnings, manifest!.Status);
        Assert.NotEmpty(manifest.Warnings);

        await store.DisposeAsync();
    }
}

public class RecoveryTests
{
    [Fact]
    public async Task Незакрытая_сессия_находится_и_восстанавливается()
    {
        using var dir = new TempDir();

        // Имитируем аварию: папка, манифест со статусом recording, метка с несуществующим PID,
        // несколько уцелевших строк стенограммы — но без финализации.
        var folder = Path.Combine(dir.Path, "2026-07-31_1430_Авария");
        Directory.CreateDirectory(folder);

        var manifest = new SessionManifest
        {
            SessionId = "crashed-1",
            Title = "Авария",
            StartUtc = DateTimeOffset.UtcNow.AddMinutes(-30),
            StartLocal = DateTimeOffset.Now.AddMinutes(-30),
            Timezone = TimeZoneInfo.Local.Id,
            Status = SessionStatus.Recording
        };
        await AtomicJsonStore.WriteAsync(Path.Combine(folder, "session.json"), manifest, JsonSetup.Pretty);

        await using (var writer = new JsonlWriter(Path.Combine(folder, "transcript.jsonl")))
        {
            await writer.AppendAsync(new TranscriptSegment
            {
                StartMs = 0, EndMs = 3000, Source = AudioChannel.Microphone, Text = "речь до аварии"
            });
            await writer.FlushAsync();
        }

        await File.WriteAllTextAsync(
            Path.Combine(folder, "session.lock"), "{\"pid\":999999,\"session_id\":\"crashed-1\"}");

        var recovery = new RecoveryService();
        var found = recovery.Scan(dir.Path);

        Assert.Single(found);
        Assert.Equal(1, found[0].TranscriptLines);

        var recovered = await recovery.RecoverAsync(found[0].FolderPath);

        Assert.NotNull(recovered);
        Assert.Equal(SessionStatus.Recovered, recovered!.Status);
        // session_id сохраняется: восстановление не создаёт вторую сессию.
        Assert.Equal("crashed-1", recovered.SessionId);

        // Стенограмма пересобрана из уцелевших строк.
        var md = await File.ReadAllTextAsync(Path.Combine(folder, "transcript.md"));
        Assert.Contains("речь до аварии", md);

        Assert.False(File.Exists(Path.Combine(folder, "session.lock")));
        Assert.Empty(recovery.Scan(dir.Path));
    }

    [Fact]
    public void Завершённая_сессия_не_предлагается_к_восстановлению()
    {
        using var dir = new TempDir();
        var recovery = new RecoveryService();

        Assert.Empty(recovery.Scan(dir.Path));
    }
}
