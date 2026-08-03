namespace MeetMemo.Asr;

/// <summary>Тип акустической модели — определяет, как её конфигурировать в sherpa-onnx.</summary>
public enum AsrModelKind
{
    /// <summary>NeMo CTC (GigaAM v2 — русский, MIT).</summary>
    NemoCtc,

    /// <summary>Transducer (RNNT): точнее, но тяжелее и сложнее в декодировании.</summary>
    Transducer
}

/// <summary>Один файл модели.</summary>
public sealed record ModelFile(string Name, string Url, long ApproxSizeBytes);

/// <summary>Описание готовой модели: что скачивать и как настраивать.</summary>
public sealed record AsrModelDescriptor
{
    public required string Id { get; init; }

    public required string DisplayName { get; init; }

    public required AsrModelKind Kind { get; init; }

    /// <summary>Имя папки модели внутри каталога моделей.</summary>
    public required string FolderName { get; init; }

    /// <summary>Файлы, которые нужно скачать. Качаем пофайлово — распаковка архивов не нужна.</summary>
    public required IReadOnlyList<ModelFile> Files { get; init; }

    /// <summary>Основной файл модели (для CTC) или энкодер (для transducer).</summary>
    public required string ModelFile { get; init; }

    public string TokensFile { get; init; } = "tokens.txt";

    public required string License { get; init; }

    public string? Notes { get; init; }

    public long ApproxSizeBytes => Files.Sum(f => f.ApproxSizeBytes);
}

/// <summary>
/// Каталог проверенных русских моделей.
///
/// Лицензионное правило, которое нельзя нарушать: GigaAM берём строго версии v2
/// (конвертация 2025-04-19, MIT). Конвертация v1 от 2024-10-24 распространяется по
/// некоммерческой лицензии и в открытый продукт попадать не должна.
/// </summary>
public static class AsrModelCatalog
{
    public const string GigaAmV2CtcId = "gigaam-v2-ctc";

    private const string GigaAmV2Repo =
        "https://huggingface.co/csukuangfj/sherpa-onnx-nemo-ctc-giga-am-v2-russian-2025-04-19/resolve/main";

    /// <summary>Основная модель живой стенограммы: лучший открытый русский WER, RTF ~0.33 на CPU.</summary>
    public static readonly AsrModelDescriptor GigaAmCtc = new()
    {
        Id = GigaAmV2CtcId,
        DisplayName = "GigaAM v2 CTC — русский",
        Kind = AsrModelKind.NemoCtc,
        FolderName = "sherpa-onnx-nemo-ctc-giga-am-v2-russian-2025-04-19",
        ModelFile = "model.int8.onnx",
        License = "MIT (SberDevices GigaAM v2)",
        Notes = "Живая стенограмма. Текст без пунктуации и заглавных — ограничение модели; "
              + "читаемую версию даёт финальный проход Whisper.",
        Files =
        [
            new ModelFile("model.int8.onnx", $"{GigaAmV2Repo}/model.int8.onnx", 236_000_000),
            new ModelFile("tokens.txt", $"{GigaAmV2Repo}/tokens.txt", 4_000)
        ]
    };

    /// <summary>Silero VAD — нарезка потока на фразы для живого распознавания.</summary>
    public static readonly AsrModelDescriptor SileroVad = new()
    {
        Id = "silero-vad",
        DisplayName = "Silero VAD v5",
        Kind = AsrModelKind.NemoCtc,
        FolderName = "silero-vad",
        ModelFile = "silero_vad.onnx",
        TokensFile = "silero_vad.onnx",
        License = "MIT",
        Notes = "Определение границ речи.",
        Files =
        [
            new ModelFile(
                "silero_vad.onnx",
                "https://github.com/k2-fsa/sherpa-onnx/releases/download/asr-models/silero_vad.onnx",
                640_000)
        ]
    };

    public static IReadOnlyList<AsrModelDescriptor> LiveModels => new[] { GigaAmCtc };

    /// <summary>Всё, что нужно скачать для работы живой стенограммы.</summary>
    public static IReadOnlyList<AsrModelDescriptor> Required => new[] { GigaAmCtc, SileroVad };

    public static AsrModelDescriptor? FindById(string id) =>
        Required.FirstOrDefault(m => m.Id == id);

    /// <summary>Каталог моделей по умолчанию — в профиле пользователя, не в Program Files.</summary>
    public static string DefaultModelsRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MeetMemo", "models");
}
