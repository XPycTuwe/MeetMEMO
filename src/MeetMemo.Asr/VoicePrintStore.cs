using System.Text.Json;
using System.Text.Json.Serialization;
using MeetMemo.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SherpaOnnx;

namespace MeetMemo.Asr;

/// <summary>Запомненный голос: кто это и как звучит.</summary>
public sealed record VoicePrint
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("name")]
    public required string Name { get; init; }

    /// <summary>Должность или роль — подсказка для мемо: «кто это в компании».</summary>
    [JsonPropertyName("role")]
    public string? Role { get; init; }

    /// <summary>Отпечаток голоса: 512 чисел, по которым узнаётся тембр.</summary>
    [JsonPropertyName("embedding")]
    public required float[] Embedding { get; init; }

    [JsonPropertyName("created_utc")]
    public DateTimeOffset CreatedUtc { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>Сколько раз голос подтверждали — по этому видно, насколько отпечатку верить.</summary>
    [JsonPropertyName("confirmations")]
    public int Confirmations { get; init; } = 1;

    public string Display => string.IsNullOrWhiteSpace(Role) ? Name : $"{Name} — {Role}";
}

/// <summary>
/// Память на голоса: кто говорил на прошлых встречах и как их зовут.
///
/// Диаризация различает тембры внутри одной записи, но между встречами связи нет:
/// «Собеседник 1» сегодня и завтра — разные люди. Здесь отпечатки хранятся с именами,
/// поэтому знакомый голос узнаётся сразу, даже если в окне встречи имён взять неоткуда.
///
/// Про приватность. Отпечаток голоса — биометрия, и это голоса коллег. Поэтому база
/// пополняется только осознанным действием: человек сам назвал говорящего. Пассивно,
/// «на всякий случай», ничего не копится. Файл лежит рядом с настройками и целиком
/// в распоряжении пользователя — его можно посмотреть, поправить и удалить.
/// </summary>
public sealed class VoicePrintStore
{
    /// <summary>
    /// Насколько похожим должен быть голос, чтобы его признали знакомым. Косинусная мера:
    /// выше порог — реже узнаём, но и реже ошибаемся. 0,55 подобрано так, чтобы уверенно
    /// узнавать знакомых и не приписывать имя случайному похожему голосу.
    /// </summary>
    public const float DefaultThreshold = 0.55f;

    private readonly string _path;
    private readonly ILogger _log;
    private readonly object _gate = new();
    private List<VoicePrint> _prints = new();

    public VoicePrintStore(string? path = null, ILogger? log = null)
    {
        _path = path ?? DefaultPath;
        _log = log ?? NullLogger.Instance;
        Load();
    }

    /// <summary>Рядом с настройками приложения, а не в папке встречи: память общая для всех встреч.</summary>
    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MeetMemo", "voices.json");

    public string Path_ => _path;

    public IReadOnlyList<VoicePrint> All
    {
        get { lock (_gate) return _prints.ToList(); }
    }

    public int Count
    {
        get { lock (_gate) return _prints.Count; }
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_path)) return;

            var loaded = JsonSerializer.Deserialize<List<VoicePrint>>(
                File.ReadAllText(_path), JsonSetup.Compact);

            lock (_gate) _prints = loaded ?? new List<VoicePrint>();
        }
        catch (Exception ex)
        {
            // Битый файл не должен мешать встрече: начинаем с пустой памяти,
            // но и не затираем — вдруг человек захочет достать оттуда данные.
            _log.LogWarning(ex, "Не удалось прочитать память голосов");
        }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_path)!);

            List<VoicePrint> snapshot;
            lock (_gate) snapshot = _prints.ToList();

            // Через временный файл: прерванная запись не должна оставить обрубок
            // вместо всей памяти о голосах.
            var temp = _path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(snapshot, JsonSetup.Pretty));
            File.Move(temp, _path, overwrite: true);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Не удалось сохранить память голосов");
        }
    }

    /// <summary>
    /// Ищет знакомый голос. Возвращает самый похожий отпечаток и меру сходства,
    /// либо null, если ничего похожего нет.
    /// </summary>
    public (VoicePrint Print, float Similarity)? Recognize(
        float[] embedding, float threshold = DefaultThreshold)
    {
        List<VoicePrint> snapshot;
        lock (_gate) snapshot = _prints;

        VoicePrint? best = null;
        var bestScore = float.MinValue;

        foreach (var print in snapshot)
        {
            var score = CosineSimilarity(embedding, print.Embedding);
            if (score <= bestScore) continue;

            bestScore = score;
            best = print;
        }

        return best is not null && bestScore >= threshold ? (best, bestScore) : null;
    }

    /// <summary>
    /// Запоминает голос под именем. Если такой человек уже известен, отпечаток
    /// уточняется усреднением: с каждым подтверждением узнавание становится увереннее,
    /// а разовая случайная запись не перебивает накопленное.
    /// </summary>
    public VoicePrint Remember(string name, string? role, float[] embedding)
    {
        // Пустой отпечаток запомнить нельзя: он совпадёт с чем угодно и начнёт
        // приписывать это имя посторонним голосам.
        ArgumentNullException.ThrowIfNull(embedding);
        if (embedding.Length == 0)
            throw new ArgumentException("Отпечаток голоса пуст", nameof(embedding));

        lock (_gate)
        {
            var existing = _prints.FirstOrDefault(p =>
                string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));

            if (existing is not null)
            {
                var merged = Blend(existing.Embedding, embedding, existing.Confirmations);
                var updated = existing with
                {
                    Role = string.IsNullOrWhiteSpace(role) ? existing.Role : role,
                    Embedding = merged,
                    Confirmations = existing.Confirmations + 1
                };

                _prints[_prints.IndexOf(existing)] = updated;
                Save();
                return updated;
            }

            var print = new VoicePrint
            {
                Id = Guid.NewGuid().ToString("N")[..8],
                Name = name,
                Role = role,
                Embedding = embedding
            };

            _prints.Add(print);
            Save();
            return print;
        }
    }

    /// <summary>Правка имени и должности у уже запомненного голоса.</summary>
    public bool Rename(string id, string name, string? role)
    {
        lock (_gate)
        {
            var index = _prints.FindIndex(p => p.Id == id);
            if (index < 0) return false;

            _prints[index] = _prints[index] with { Name = name, Role = role };
        }

        Save();
        return true;
    }

    public bool Forget(string id)
    {
        bool removed;
        lock (_gate) removed = _prints.RemoveAll(p => p.Id == id) > 0;

        if (removed) Save();
        return removed;
    }

    /// <summary>
    /// Усреднение отпечатков с весом накопленных подтверждений: новый голос уточняет
    /// старый, а не заменяет его. Иначе одна неудачная фраза стёрла бы всё, что накопилось.
    /// </summary>
    private static float[] Blend(float[] known, float[] fresh, int weight)
    {
        var result = new float[known.Length];
        for (var i = 0; i < known.Length; i++)
            result[i] = (known[i] * weight + fresh[i]) / (weight + 1);

        return Normalize(result);
    }

    /// <summary>Мера похожести двух голосов: 1 — тот же голос, 0 — ничего общего.</summary>
    public static float CosineSimilarity(float[] a, float[] b)
    {
        if (a.Length != b.Length) return 0;

        double dot = 0, na = 0, nb = 0;
        for (var i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            na += a[i] * a[i];
            nb += b[i] * b[i];
        }

        var denominator = Math.Sqrt(na) * Math.Sqrt(nb);
        return denominator > 0 ? (float)(dot / denominator) : 0;
    }

    private static float[] Normalize(float[] vector)
    {
        double norm = 0;
        foreach (var value in vector) norm += value * value;
        norm = Math.Sqrt(norm);

        if (norm <= 0) return vector;

        var result = new float[vector.Length];
        for (var i = 0; i < vector.Length; i++) result[i] = (float)(vector[i] / norm);
        return result;
    }
}

/// <summary>
/// Считает отпечатки голоса из отсчётов речи. Держит модель загруженной: на фразу
/// уходит 25–90 мс, поэтому узнавание успевает идти прямо во время разговора —
/// распознавание самой фразы занимает больше.
/// </summary>
public sealed class VoiceEmbedder : IDisposable
{
    private readonly SpeakerEmbeddingExtractor? _extractor;

    public VoiceEmbedder(string? modelsRoot = null, ILogger? log = null)
    {
        var manager = new ModelManager(modelsRoot);
        if (!manager.IsInstalled(AsrModelCatalog.SpeakerEmbedding)) return;

        try
        {
            var config = new SpeakerEmbeddingExtractorConfig
            {
                Model = Path.Combine(
                    manager.GetModelDirectory(AsrModelCatalog.SpeakerEmbedding),
                    AsrModelCatalog.SpeakerEmbedding.ModelFile),
                NumThreads = 1,
                Provider = "cpu"
            };

            _extractor = new SpeakerEmbeddingExtractor(config);
        }
        catch (Exception ex)
        {
            (log ?? NullLogger.Instance).LogWarning(ex, "Модель отпечатков голоса не загрузилась");
        }
    }

    public bool Ready => _extractor is not null;

    /// <summary>
    /// Отпечаток фразы. На слишком коротком куске тембр не успевает проявиться —
    /// такие фразы пропускаем, иначе узнавание начнёт путать людей.
    /// </summary>
    public float[]? Compute(float[] samples, int sampleRate = 16000)
    {
        if (_extractor is null) return null;
        if (samples.Length < sampleRate) return null;   // короче секунды

        try
        {
            using var stream = _extractor.CreateStream();
            stream.AcceptWaveform(sampleRate, samples);
            stream.InputFinished();
            return _extractor.Compute(stream);
        }
        catch (Exception)
        {
            return null;
        }
    }

    public void Dispose() => _extractor?.Dispose();
}
