using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace MeetMemo.Asr;

/// <summary>Прогресс загрузки для мастера первого запуска.</summary>
public sealed record ModelDownloadProgress(
    string ModelName, string FileName, long BytesReceived, long TotalBytes)
{
    public double Fraction => TotalBytes > 0 ? (double)BytesReceived / TotalBytes : 0;
}

/// <summary>
/// Загрузка моделей распознавания. Это единственное место, где приложение ходит в сеть
/// в основном контуре, и делает это только по явному действию пользователя (ТЗ 15.1, AC-20).
/// Файлы качаются по одному, докачиваются во временный файл и переименовываются —
/// прерванная загрузка не оставляет битую модель, которую движок примет за рабочую.
/// </summary>
public sealed class ModelManager
{
    private readonly string _modelsRoot;
    private readonly ILogger _log;

    public ModelManager(string? modelsRoot = null, ILogger? log = null)
    {
        _modelsRoot = modelsRoot ?? AsrModelCatalog.DefaultModelsRoot;
        _log = log ?? NullLogger.Instance;
    }

    public string ModelsRoot => _modelsRoot;

    public string GetModelDirectory(AsrModelDescriptor model) =>
        Path.Combine(_modelsRoot, model.FolderName);

    /// <summary>Модель считается установленной, когда на месте все её файлы и они не пустые.</summary>
    public bool IsInstalled(AsrModelDescriptor model)
    {
        var dir = GetModelDirectory(model);
        return model.Files.All(f =>
        {
            var path = Path.Combine(dir, f.Name);
            return File.Exists(path) && new FileInfo(path).Length > 0;
        });
    }

    public IReadOnlyList<AsrModelDescriptor> GetMissing() =>
        AsrModelCatalog.Required.Where(m => !IsInstalled(m)).ToList();

    public long GetMissingBytes() => GetMissing().Sum(m => m.ApproxSizeBytes);

    public async Task DownloadAsync(
        AsrModelDescriptor model,
        IProgress<ModelDownloadProgress>? progress = null,
        CancellationToken ct = default)
    {
        var dir = GetModelDirectory(model);
        Directory.CreateDirectory(dir);

        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };

        foreach (var file in model.Files)
        {
            var target = Path.Combine(dir, file.Name);
            if (File.Exists(target) && new FileInfo(target).Length > 0) continue;

            var temp = target + ".part";
            _log.LogInformation("Скачиваю {File} для {Model}", file.Name, model.DisplayName);

            using var response = await http
                .GetAsync(file.Url, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            var total = response.Content.Headers.ContentLength ?? file.ApproxSizeBytes;

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
                        model.DisplayName, file.Name, received, total));
                }
            }

            // Переименование делает файл видимым как готовый только целиком.
            File.Move(temp, target, overwrite: true);
            _log.LogInformation("{File} загружен", file.Name);
        }
    }

    /// <summary>Скачивает всё недостающее для работы живой стенограммы.</summary>
    public async Task DownloadMissingAsync(
        IProgress<ModelDownloadProgress>? progress = null, CancellationToken ct = default)
    {
        foreach (var model in GetMissing())
            await DownloadAsync(model, progress, ct).ConfigureAwait(false);
    }
}
