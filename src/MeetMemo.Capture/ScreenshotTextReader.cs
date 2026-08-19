using System.IO;
using System.Runtime.Versioning;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;

namespace MeetMemo.Capture;

/// <summary>
/// Чтение текста со снимков окна встречи встроенным в Windows распознаванием.
///
/// Зачем: TrueConf на Qt почти не отдаёт дерево доступности — из него читаются одни
/// «Свернуть» и «Закрыть», а список участников и бейдж говорящего снаружи невидимы.
/// При этом на снимках они есть: TrueConf сам подписывает, кто сейчас в эфире, и держит
/// панель участников с именами. Снимки идут каждые 15–20 секунд — этого хватает,
/// чтобы привязывать имена к отрезкам речи и перепроверять ими отпечатки голосов.
///
/// Всё локально: OCR — часть самой Windows, никакая картинка никуда не уходит.
/// </summary>
[SupportedOSPlatform("windows10.0.19041.0")]
public sealed class ScreenshotTextReader
{
    private readonly OcrEngine? _engine;

    public ScreenshotTextReader()
    {
        // Русский движок ставится вместе с русским языком интерфейса; если его нет,
        // берём системный по умолчанию — латиница и цифры всё равно прочитаются.
        _engine = OcrEngine.TryCreateFromLanguage(new Language("ru"))
                  ?? OcrEngine.TryCreateFromUserProfileLanguages();
    }

    /// <summary>Распознавание доступно на этой машине.</summary>
    public bool Ready => _engine is not null;

    /// <summary>
    /// Читает весь текст со снимка построчно, в порядке сверху вниз. Строки — уже
    /// склеенные слова: списки имён приходят по одному имени на строку.
    /// </summary>
    public async Task<IReadOnlyList<string>> ReadLinesAsync(string imagePath, CancellationToken ct = default)
    {
        if (_engine is null || !File.Exists(imagePath)) return Array.Empty<string>();

        try
        {
            using var stream = File.OpenRead(imagePath);
            using var ras = stream.AsRandomAccessStream();

            var decoder = await BitmapDecoder.CreateAsync(ras);
            using var bitmap = await decoder.GetSoftwareBitmapAsync();

            ct.ThrowIfCancellationRequested();

            var result = await _engine.RecognizeAsync(bitmap);

            return result.Lines
                .Select(l => l.Text.Trim())
                .Where(t => t.Length > 1)
                .ToList();
        }
        catch (Exception)
        {
            // Снимок мог быть повреждён или занят — для контекста это не потеря.
            return Array.Empty<string>();
        }
    }
}
