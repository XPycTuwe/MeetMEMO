using MeetMemo.Asr;
using MeetMemo.Audio;
using MeetMemo.Contracts;
using MeetMemo.Export;
using MeetMemo.Storage;
using Xunit;

namespace MeetMemo.Tests;

public class ResamplerTests
{
    [Fact]
    public void Понижение_частоты_даёт_пропорциональную_длину()
    {
        var input = new float[48000]; // ровно секунда при 48 кГц
        var output = AsrFeeder.Resample(input, 48000, 16000);

        Assert.Equal(16000, output.Length);
    }

    [Fact]
    public void Совпадающая_частота_возвращает_исходный_массив()
    {
        var input = new float[100];
        Assert.Same(input, AsrFeeder.Resample(input, 16000, 16000));
    }

    [Fact]
    public void Синусоида_сохраняет_амплитуду_после_ресемплинга()
    {
        // Проверяем, что ресемплер не глушит и не разгоняет сигнал: модель ждёт
        // тот же уровень, что был на входе.
        var input = new float[48000];
        for (var i = 0; i < input.Length; i++)
            input[i] = 0.5f * MathF.Sin(2 * MathF.PI * 440 * i / 48000f);

        var output = AsrFeeder.Resample(input, 48000, 16000);
        var peak = output.Max(MathF.Abs);

        Assert.InRange(peak, 0.45f, 0.55f);
    }
}

public class AudioBufferTests
{
    [Fact]
    public void Стерео_сводится_в_моно_усреднением()
    {
        var buffer = new AudioBuffer
        {
            Channel = AudioChannel.Application,
            OffsetMs = 0,
            Samples = [1.0f, 0.0f, 0.5f, 0.5f],
            SampleRate = 48000,
            Channels = 2
        };

        var mono = buffer.ToMono();

        Assert.Equal(2, mono.Length);
        Assert.Equal(0.5f, mono[0], 3);
        Assert.Equal(0.5f, mono[1], 3);
    }

    [Fact]
    public void Пик_находится_по_модулю()
    {
        var buffer = new AudioBuffer
        {
            Channel = AudioChannel.Microphone,
            OffsetMs = 0,
            Samples = [0.1f, -0.8f, 0.3f],
            SampleRate = 16000,
            Channels = 1
        };

        Assert.Equal(0.8f, buffer.Peak, 3);
    }
}

public class WaveFileTapTests
{
    [Fact]
    public void Заголовок_WAV_дописывается_при_закрытии()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "microphone.wav");

        using (var tap = new WaveFileTap(path, AudioChannel.Microphone, 16000, 1))
        {
            tap.OnBuffer(new AudioBuffer
            {
                Channel = AudioChannel.Microphone,
                OffsetMs = 0,
                Samples = new float[16000],
                SampleRate = 16000,
                Channels = 1
            });
        }

        var bytes = File.ReadAllBytes(path);

        Assert.True(bytes.Length > 44);
        Assert.Equal("RIFF", System.Text.Encoding.ASCII.GetString(bytes, 0, 4));
        Assert.Equal("WAVE", System.Text.Encoding.ASCII.GetString(bytes, 8, 4));

        // Размер данных в заголовке должен соответствовать реально записанному объёму.
        var dataSize = BitConverter.ToUInt32(bytes, 40);
        Assert.Equal((uint)(bytes.Length - 44), dataSize);
    }

    [Fact]
    public void Файл_после_аварии_чинится_по_метке_длины()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "microphone.wav");

        // Пишем данные и «забываем» закрыть файл — так выглядит аварийное завершение.
        using (var fs = new FileStream(path, FileMode.Create))
        {
            fs.Write(new byte[44]);              // пустой заголовок
            fs.Write(new byte[16000 * 2]);       // секунда 16-битного моно
        }
        File.WriteAllText(path + ".len",
            "{\"data_bytes\":32000,\"sample_rate\":16000,\"channels\":1}");

        Assert.True(WaveFileTap.TryRepair(path));

        var bytes = File.ReadAllBytes(path);
        Assert.Equal("RIFF", System.Text.Encoding.ASCII.GetString(bytes, 0, 4));
        Assert.Equal(32000u, BitConverter.ToUInt32(bytes, 40));
        Assert.False(File.Exists(path + ".len"));
    }
}

public class ExportTests
{
    private static string CreatePackage(string root)
    {
        var folder = Path.Combine(root, "2026-07-31_1430_Встреча");
        Directory.CreateDirectory(folder);
        Directory.CreateDirectory(Path.Combine(folder, "screenshots"));
        Directory.CreateDirectory(Path.Combine(folder, "audio"));
        Directory.CreateDirectory(Path.Combine(folder, "diagnostics"));

        File.WriteAllText(Path.Combine(folder, "session.json"), "{\"schema_version\":\"1.0\"}");
        File.WriteAllText(Path.Combine(folder, "transcript.jsonl"), "{\"text\":\"тест\"}\n");
        File.WriteAllText(Path.Combine(folder, "transcript.md"), "# Встреча");
        File.WriteAllText(Path.Combine(folder, "screenshots", "app_00-01-00.png"), "png");
        File.WriteAllText(Path.Combine(folder, "audio", "microphone.wav"), new string('a', 5000));
        File.WriteAllText(Path.Combine(folder, "diagnostics", "app.log"), "log");

        return folder;
    }

    [Fact]
    public void Аудио_и_диагностика_по_умолчанию_вне_архива()
    {
        using var dir = new TempDir();
        var folder = CreatePackage(dir.Path);

        var plan = ExportPlanBuilder.Build(folder);

        Assert.False(plan.ContainsAudio);
        Assert.All(
            plan.Items.Where(i => i.Category == ExportCategory.Diagnostics),
            i => Assert.False(i.Included));
        Assert.Contains(plan.Items, i => i.Included && i.RelativePath == "transcript.jsonl");
    }

    [Fact]
    public void Аудио_включается_явным_выбором()
    {
        using var dir = new TempDir();
        var folder = CreatePackage(dir.Path);

        var plan = ExportPlanBuilder.Build(folder, includeAudio: true);

        Assert.True(plan.ContainsAudio);
    }

    [Fact]
    public async Task Архив_распаковывается_стандартными_средствами()
    {
        using var dir = new TempDir();
        var folder = CreatePackage(dir.Path);

        var plan = ExportPlanBuilder.Build(folder);
        var archivePath = Path.Combine(dir.Path, "package.zip");

        var packager = new ZipPackager();
        var file = await packager.CreateAsync(plan, archivePath);

        Assert.True(file.Exists);
        Assert.False(File.Exists(archivePath + ".part"));

        using var archive = System.IO.Compression.ZipFile.OpenRead(archivePath);
        var names = archive.Entries.Select(e => e.FullName).ToList();

        Assert.Contains("2026-07-31_1430_Встреча/transcript.jsonl", names);
        Assert.Contains("2026-07-31_1430_Встреча/session.json", names);
        // Записка для того, кто откроет архив в Claude.
        Assert.Contains(names, n => n.Contains("КАК_ИСПОЛЬЗОВАТЬ"));
        // Аудио по умолчанию не выгружается.
        Assert.DoesNotContain(names, n => n.Contains("microphone.wav"));
    }

    [Fact]
    public async Task Правила_обработки_вложены_в_архив()
    {
        using var dir = new TempDir();
        var folder = CreatePackage(dir.Path);

        var plan = ExportPlanBuilder.Build(folder);
        var archivePath = Path.Combine(dir.Path, "package.zip");
        await new ZipPackager().CreateAsync(plan, archivePath);

        using var archive = System.IO.Compression.ZipFile.OpenRead(archivePath);
        var rules = archive.GetEntry(SkillTemplate.FileName);
        Assert.NotNull(rules);

        using var reader = new StreamReader(rules!.Open(), System.Text.Encoding.UTF8);
        var text = await reader.ReadToEndAsync();

        // Ключевые запреты должны доехать до получателя целиком, а не остаться ссылкой на скилл.
        Assert.Contains("не определён", text);
        Assert.Contains("Приложение А", text);
        Assert.Contains("Приложение Б", text);
        // YAML-заголовок скилла внутри архива только мешает читателю.
        Assert.DoesNotContain("name: meeting-memo", text);
    }
}

public class TranscriptRendererTests
{
    [Fact]
    public async Task Markdown_содержит_таймкоды_и_предупреждение()
    {
        using var dir = new TempDir();
        var folder = new MeetingFolder(dir.Path);
        folder.EnsureCreated(withAudio: false);

        await using (var writer = new JsonlWriter(folder.TranscriptJsonl))
        {
            await writer.AppendAsync(new TranscriptSegment
            {
                StartMs = 65_000, EndMs = 70_000,
                Source = AudioChannel.Application, Text = "нужно пересчитать давление"
            });
        }

        var manifest = new SessionManifest
        {
            SessionId = "x",
            Title = "Совещание",
            StartUtc = DateTimeOffset.UtcNow,
            StartLocal = DateTimeOffset.Now,
            Timezone = "Europe/Moscow",
            DurationMs = 120_000
        };

        var count = TranscriptRenderer.Render(folder, manifest);
        var md = await File.ReadAllTextAsync(folder.TranscriptMd);

        Assert.Equal(1, count);
        Assert.Contains("01:05", md);
        Assert.Contains("нужно пересчитать давление", md);
        // Читатель обязан понимать, что это не дословный протокол.
        Assert.Contains("автоматическим распознаванием", md);
    }

    [Theory]
    [InlineData(0, "00:00")]
    [InlineData(65_000, "01:05")]
    [InlineData(3_725_000, "01:02:05")]
    public void Таймкод_форматируется_по_длительности(long ms, string expected)
    {
        Assert.Equal(expected, TranscriptRenderer.FormatTimecode(ms));
    }
}
