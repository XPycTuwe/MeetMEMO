using MeetMemo.Contracts;
using MeetMemo.Storage;
using Xunit;

namespace MeetMemo.Tests;

/// <summary>Временная папка, убирающаяся за собой.</summary>
public sealed class TempDir : IDisposable
{
    public TempDir()
    {
        Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "meetmemo-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public void Dispose()
    {
        try { if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true); }
        catch (IOException) { }
    }
}

public class JsonlWriterTests
{
    [Fact]
    public async Task Записывает_и_читает_строки()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "transcript.jsonl");

        await using (var writer = new JsonlWriter(path))
        {
            await writer.AppendAsync(new TranscriptSegment
            {
                StartMs = 0, EndMs = 1000, Source = AudioChannel.Microphone, Text = "первая"
            });
            await writer.AppendAsync(new TranscriptSegment
            {
                StartMs = 1000, EndMs = 2000, Source = AudioChannel.Application, Text = "вторая"
            });
        }

        var read = JsonlWriter.ReadAll<TranscriptSegment>(path).ToList();

        Assert.Equal(2, read.Count);
        Assert.Equal("первая", read[0].Text);
        Assert.Equal(AudioChannel.Application, read[1].Source);
    }

    [Fact]
    public async Task Оборванная_последняя_строка_не_ломает_чтение()
    {
        // Ровно то, что остаётся после аварийного завершения: файл обрывается посреди строки.
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "transcript.jsonl");

        await using (var writer = new JsonlWriter(path))
        {
            await writer.AppendAsync(new TranscriptSegment
            {
                StartMs = 0, EndMs = 500, Source = AudioChannel.Microphone, Text = "целая"
            });
            await writer.FlushAsync();
        }

        await File.AppendAllTextAsync(path, "{\"start_ms\":500,\"end_ms\":900,\"sou");

        var read = JsonlWriter.ReadAll<TranscriptSegment>(path).ToList();

        Assert.Single(read);
        Assert.Equal("целая", read[0].Text);
    }

    [Fact]
    public async Task Кириллица_пишется_читаемой()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "t.jsonl");

        await using (var writer = new JsonlWriter(path))
        {
            await writer.AppendAsync(new TranscriptSegment
            {
                StartMs = 0, EndMs = 1, Source = AudioChannel.Microphone,
                Text = "пропускная способность"
            });
        }

        var raw = await File.ReadAllTextAsync(path);

        // Пакет должен читаться человеком и сторонними инструментами без декодирования.
        Assert.Contains("пропускная способность", raw);
        Assert.DoesNotContain("\\u", raw);
    }
}

public class AtomicJsonStoreTests
{
    [Fact]
    public async Task Перезапись_не_оставляет_временных_файлов()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "session.json");

        var manifest = new SessionManifest
        {
            SessionId = "test",
            Title = "Совещание",
            StartUtc = DateTimeOffset.UtcNow,
            StartLocal = DateTimeOffset.Now,
            Timezone = "Europe/Moscow"
        };

        await AtomicJsonStore.WriteAsync(path, manifest);
        await AtomicJsonStore.WriteAsync(path, manifest with { Title = "Обновлённое" });

        var read = await AtomicJsonStore.ReadAsync<SessionManifest>(path);

        Assert.NotNull(read);
        Assert.Equal("Обновлённое", read!.Title);
        Assert.False(File.Exists(path + ".tmp"));
    }

    [Fact]
    public async Task Битый_файл_читается_как_null_без_исключения()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "broken.json");
        await File.WriteAllTextAsync(path, "{ это не json");

        var read = await AtomicJsonStore.ReadAsync<SessionManifest>(path);

        Assert.Null(read);
    }
}

public class MeetingFolderTests
{
    [Theory]
    [InlineData("Совещание ППД", "Совещание_ППД")]
    [InlineData("Отчёт: 1/2 квартал", "Отчёт_1_2_квартал")]
    [InlineData("  пробелы  ", "пробелы")]
    [InlineData("", "")]
    public void Нормализует_название_в_имя_папки(string input, string expected)
    {
        Assert.Equal(expected, MeetingFolderFactory.Sanitize(input));
    }

    [Fact]
    public void Зарезервированные_имена_Windows_получают_суффикс()
    {
        // Папку с именем CON создать нельзя — это устройство, а не файл.
        Assert.Equal("CON_meeting", MeetingFolderFactory.Sanitize("CON"));
    }

    [Fact]
    public void Повторный_запуск_не_перезаписывает_папку()
    {
        using var dir = new TempDir();
        var start = new DateTimeOffset(2026, 7, 31, 14, 30, 0, TimeSpan.Zero);

        var first = MeetingFolderFactory.Create(dir.Path, start, "Встреча");
        Directory.CreateDirectory(first.Root);

        var second = MeetingFolderFactory.Create(dir.Path, start, "Встреча");

        Assert.NotEqual(first.Root, second.Root);
        Assert.EndsWith("-2", second.Root);
    }
}
