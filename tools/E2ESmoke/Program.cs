using System.Diagnostics;
using MeetMemo.Asr;
using MeetMemo.Audio;
using MeetMemo.Contracts;
using MeetMemo.Core;
using MeetMemo.Export;
using MeetMemo.Storage;

// Сквозная проверка контура без интерфейса: сессия → захват звука → живая стенограмма →
// пакет встречи → ZIP. Аудио берётся из системного loopback, поэтому достаточно проиграть
// на компьютере любой русскоязычный файл, чтобы увидеть весь путь целиком.
//
//   E2ESmoke [секунд-записи]

Console.OutputEncoding = System.Text.Encoding.UTF8;

var seconds = args.Length > 0 && int.TryParse(args[0], out var s) ? s : 25;
var root = Path.Combine(Path.GetTempPath(), "meetmemo-e2e", DateTime.Now.ToString("HHmmss"));
Directory.CreateDirectory(root);

Console.WriteLine($"Папка встреч: {root}");
Console.WriteLine($"Длительность записи: {seconds} с");
Console.WriteLine();

var models = new ModelManager();
if (models.GetMissing().Count > 0)
{
    Console.WriteLine("Модели распознавания не установлены — прогон невозможен.");
    return 2;
}

var store = new MeetingSessionStore();
var audio = new AudioEngine();
var degradation = new DegradationPolicy();
var asr = new AsrEngine(store, audio);

var recognized = 0;
asr.SegmentRecognized += seg =>
{
    recognized++;
    Console.WriteLine($"  [{seg.StartMs / 1000.0:F1}s {seg.Channel}] {seg.Text}");
};

await using var controller = new SessionController(
    new ISessionParticipant[] { asr, audio }, store, degradation);

var request = new SessionStartRequest
{
    Title = "Сквозная проверка",
    MeetingsRoot = root,
    // Системный loopback: пишем всё, что звучит на компьютере.
    AudioMode = AudioMode.System,
    SaveAudioFiles = true,
    AutoScreenshotsEnabled = false
};

Console.WriteLine("Старт сессии...");
var started = await controller.SendAsync(new SessionCommand.Start(request));
if (!started.Accepted)
{
    Console.WriteLine($"Не удалось начать: {started.Message}");
    return 1;
}

Console.WriteLine($"Запись идёт. Режим звука: {audio.CurrentMode}");
Console.WriteLine("Распознанные реплики:");

var sw = Stopwatch.StartNew();
while (sw.Elapsed.TotalSeconds < seconds)
{
    await Task.Delay(1000);
    if ((int)sw.Elapsed.TotalSeconds % 5 == 0)
    {
        Console.WriteLine($"  … {sw.Elapsed.TotalSeconds:F0} с, "
            + $"уровень микрофона {audio.MicrophonePeak:F3}, приложения {audio.ApplicationPeak:F3}");
    }
}

Console.WriteLine();
Console.WriteLine("Остановка...");
var stopped = await controller.SendAsync(new SessionCommand.Stop());
if (!stopped.Accepted)
{
    Console.WriteLine($"Ошибка остановки: {stopped.Message}");
    return 1;
}

var folder = Directory.GetDirectories(root).Single();
Console.WriteLine();
Console.WriteLine("=== Результат ===");
Console.WriteLine($"Папка: {folder}");

var checks = new List<(string Name, bool Ok, string Detail)>();

void Check(string name, string path)
{
    var exists = File.Exists(path);
    var size = exists ? new FileInfo(path).Length : 0;
    checks.Add((name, exists && size > 0, exists ? $"{size} байт" : "нет файла"));
}

var meeting = new MeetingFolder(folder);
Check("session.json", meeting.SessionJson);
Check("timeline.jsonl", meeting.TimelineJsonl);
Check("transcript.jsonl", meeting.TranscriptJsonl);
Check("transcript.md", meeting.TranscriptMd);
Check("glossary.md", meeting.GlossaryMd);
Check("audio/microphone.wav", meeting.MicrophoneAudio());
Check("audio/application.wav", meeting.ApplicationAudio());

foreach (var (name, ok, detail) in checks)
    Console.WriteLine($"  {(ok ? "OK  " : "НЕТ ")} {name,-26} {detail}");

var segments = JsonlWriter.ReadAll<TranscriptSegment>(meeting.TranscriptJsonl).ToList();
var events = JsonlWriter.ReadAll<TimelineEvent>(meeting.TimelineJsonl).ToList();

Console.WriteLine();
Console.WriteLine($"Сегментов стенограммы: {segments.Count}");
Console.WriteLine($"Событий шкалы: {events.Count} ({string.Join(", ", events.Select(e => e.Type).Distinct())})");

// Сборка ZIP — последний шаг пути к Claude.
var plan = ExportPlanBuilder.Build(folder);
var zipPath = Path.Combine(root, ZipPackager.SuggestArchiveName(folder));
var zip = await new ZipPackager().CreateAsync(plan, zipPath);
Console.WriteLine($"ZIP: {zip.Name}, {ExportPlan.FormatSize(zip.Length)}, файлов {plan.IncludedCount}");

await store.DisposeAsync();
audio.Dispose();
asr.Dispose();

var allOk = checks.Where(c => !c.Name.StartsWith("audio/")).All(c => c.Ok) && zip.Exists;
Console.WriteLine();
Console.WriteLine(allOk ? "СКВОЗНОЙ ПРОГОН УСПЕШЕН" : "ЕСТЬ ПРОБЛЕМЫ");
return allOk ? 0 : 1;
