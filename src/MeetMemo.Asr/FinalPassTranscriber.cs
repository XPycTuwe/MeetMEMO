using MeetMemo.Contracts;
using MeetMemo.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Whisper.net;

namespace MeetMemo.Asr;

/// <summary>Модель Whisper для финального прохода.</summary>
public sealed record WhisperModelDescriptor(string Id, string DisplayName, string FileName, string Url, long SizeBytes)
{
    /// <summary>
    /// large-v3-turbo в квантовании q5_0: компромисс размера и качества, даёт пунктуацию,
    /// заглавные буквы и корректно обрабатывает англоязычные термины в русской речи.
    /// </summary>
    public static readonly WhisperModelDescriptor LargeV3TurboQ5 = new(
        "whisper-large-v3-turbo-q5",
        "Whisper large-v3-turbo (q5_0)",
        "ggml-large-v3-turbo-q5_0.bin",
        "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-large-v3-turbo-q5_0.bin",
        574_000_000);

    /// <summary>Лёгкая модель для слабых машин: быстрее, но заметно хуже на русском.</summary>
    public static readonly WhisperModelDescriptor SmallQ5 = new(
        "whisper-small-q5",
        "Whisper small (q5_1)",
        "ggml-small-q5_1.bin",
        "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-small-q5_1.bin",
        190_000_000);
}

/// <summary>
/// Финальный проход по сохранённому аудио (P1 по ТЗ 10.3). Обязательное условие — включённое
/// сохранение аудиофайлов: пересчитывать нечего, если звук не писался на диск.
///
/// Зачем второй движок: живая модель GigaAM даёт лучший русский WER, но выдаёт текст без
/// пунктуации и заглавных. Whisper медленнее, зато возвращает читаемый текст — а для
/// офлайн-прохода скорость некритична.
/// </summary>
public sealed class FinalPassTranscriber
{
    private readonly string _modelsRoot;
    private readonly WhisperModelDescriptor _model;
    private readonly ILogger _log;

    public FinalPassTranscriber(
        string? modelsRoot = null,
        WhisperModelDescriptor? model = null,
        ILogger? log = null)
    {
        _modelsRoot = modelsRoot ?? AsrModelCatalog.DefaultModelsRoot;
        _model = model ?? WhisperModelDescriptor.LargeV3TurboQ5;
        _log = log ?? NullLogger.Instance;
    }

    public string ModelPath => Path.Combine(_modelsRoot, "whisper", _model.FileName);

    public bool IsModelInstalled => File.Exists(ModelPath) && new FileInfo(ModelPath).Length > 0;

    public WhisperModelDescriptor Model => _model;

    public async Task DownloadModelAsync(
        IProgress<ModelDownloadProgress>? progress = null, CancellationToken ct = default)
    {
        if (IsModelInstalled) return;

        Directory.CreateDirectory(Path.GetDirectoryName(ModelPath)!);
        var temp = ModelPath + ".part";

        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(60) };
        using var response = await http
            .GetAsync(_model.Url, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var total = response.Content.Headers.ContentLength ?? _model.SizeBytes;

        await using (var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
        await using (var dest = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 81920))
        {
            var buffer = new byte[81920];
            long received = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                await dest.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                received += read;
                progress?.Report(new ModelDownloadProgress(
                    _model.DisplayName, _model.FileName, received, total));
            }
        }

        File.Move(temp, ModelPath, overwrite: true);
        _log.LogInformation("Модель финального прохода загружена: {Model}", _model.DisplayName);
    }

    /// <summary>
    /// Пере-распознаёт папку встречи и заменяет живые сегменты уточнёнными.
    /// Исходный transcript.jsonl сохраняется рядом как .live.jsonl — черновик остаётся
    /// доступным, если финальный проход окажется хуже на конкретной записи.
    /// </summary>
    public async Task<int> RunAsync(
        string meetingFolderPath,
        IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        var folder = new MeetingFolder(meetingFolderPath);

        if (!IsModelInstalled)
            throw new InvalidOperationException(
                $"Модель финального прохода не установлена: {ModelPath}");

        var manifest = await AtomicJsonStore
            .ReadAsync<SessionManifest>(folder.SessionJson, JsonSetup.Pretty, ct)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException("session.json не читается");

        if (!manifest.Audio.SaveFiles)
            throw new InvalidOperationException(
                "Финальный проход требует сохранённого аудио, а в этой сессии оно не писалось");

        var sources = new List<(string Path, AudioChannel Channel)>();
        if (File.Exists(folder.MicrophoneAudio()))
            sources.Add((folder.MicrophoneAudio(), AudioChannel.Microphone));
        if (File.Exists(folder.ApplicationAudio()))
            sources.Add((folder.ApplicationAudio(), AudioChannel.Application));

        if (sources.Count == 0)
            throw new InvalidOperationException("В папке встречи нет аудиофайлов");

        using var factory = WhisperFactory.FromPath(ModelPath);
        var segments = new List<TranscriptSegment>();

        for (var i = 0; i < sources.Count; i++)
        {
            var (path, channel) = sources[i];
            ct.ThrowIfCancellationRequested();

            await using var processor = factory.CreateBuilder()
                .WithLanguage("ru")
                .Build();

            await using var fileStream = File.OpenRead(path);

            await foreach (var result in processor.ProcessAsync(fileStream, ct).ConfigureAwait(false))
            {
                var text = result.Text?.Trim();
                if (string.IsNullOrWhiteSpace(text)) continue;

                segments.Add(new TranscriptSegment
                {
                    StartMs = (long)result.Start.TotalMilliseconds,
                    EndMs = (long)result.End.TotalMilliseconds,
                    Source = channel,
                    Text = text,
                    Language = "ru",
                    Final = true,
                    Engine = "whisper.net/" + _model.Id
                });
            }

            progress?.Report((i + 1) / (double)sources.Count);
        }

        if (segments.Count == 0)
        {
            _log.LogWarning("Финальный проход не дал ни одного сегмента — черновик оставлен как есть");
            return 0;
        }

        // Черновик сохраняем: он может оказаться точнее на терминах, где Whisper слабее.
        var livePath = Path.Combine(meetingFolderPath, "transcript.live.jsonl");
        if (File.Exists(folder.TranscriptJsonl) && !File.Exists(livePath))
            File.Copy(folder.TranscriptJsonl, livePath);

        var temp = folder.TranscriptJsonl + ".new";
        await using (var writer = new JsonlWriter(temp))
        {
            foreach (var segment in segments.OrderBy(s => s.StartMs))
                await writer.AppendAsync(segment, ct).ConfigureAwait(false);
            await writer.FlushAsync(ct).ConfigureAwait(false);
        }

        File.Move(temp, folder.TranscriptJsonl, overwrite: true);

        manifest = manifest with
        {
            Transcription = manifest.Transcription with
            {
                FinalPassCompleted = true,
                Model = _model.DisplayName
            }
        };
        await AtomicJsonStore.WriteAsync(folder.SessionJson, manifest, JsonSetup.Pretty, ct)
            .ConfigureAwait(false);

        TranscriptRenderer.Render(folder, manifest);

        _log.LogInformation("Финальный проход завершён: {Count} сегментов", segments.Count);
        return segments.Count;
    }
}
