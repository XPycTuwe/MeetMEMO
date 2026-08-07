using System.IO;
using System.Text.Json.Serialization;
using MeetMemo.Asr;
using MeetMemo.Contracts;
using MeetMemo.Storage;

namespace MeetMemo.App;

/// <summary>
/// Настройки приложения (ТЗ 18.2). Хранятся в профиле пользователя, пишутся атомарно —
/// повреждённый файл настроек не должен мешать приложению запуститься.
/// </summary>
public sealed record AppSettings
{
    [JsonPropertyName("meetings_root")]
    public string MeetingsRoot { get; init; } = DefaultMeetingsRoot;

    [JsonPropertyName("models_root")]
    public string ModelsRoot { get; init; } = AsrModelCatalog.DefaultModelsRoot;

    [JsonPropertyName("microphone_device_id")]
    public string? MicrophoneDeviceId { get; init; }

    [JsonPropertyName("audio_mode")]
    public AudioMode AudioMode { get; init; } = AudioMode.ApplicationProcessTree;

    /// <summary>Сохранять аудиофайлы. Захват идёт всегда — он нужен распознаванию (ТЗ 8.2).</summary>
    [JsonPropertyName("save_audio_files")]
    public bool SaveAudioFiles { get; init; } = true;

    [JsonPropertyName("auto_screenshots")]
    public bool AutoScreenshots { get; init; } = true;

    /// <summary>
    /// Показывать субтитры распознавания внизу экрана во время записи. Читать их не нужно —
    /// они подтверждают, что стенограмма пишется. Кому мешает, тот выключает.
    /// </summary>
    [JsonPropertyName("show_subtitles")]
    public bool ShowSubtitles { get; init; } = true;

    [JsonPropertyName("auto_screenshot_interval_seconds")]
    public int AutoScreenshotIntervalSeconds { get; init; } = 15;

    /// <summary>
    /// Сколько цветов оставлять в снимке. Полноцветный снимок 4K весит 3–6 МБ, а на слайдах
    /// и в интерфейсах реальных оттенков десятки: 32 цвета уменьшают файл почти в десять раз,
    /// не размывая текст. Поднимите до 64–128, если в кадре бывают фотографии и плавные
    /// градиенты; 0 отключает уменьшение палитры совсем.
    /// </summary>
    [JsonPropertyName("screenshot_colors")]
    public int ScreenshotColors { get; init; } = 64;

    [JsonPropertyName("auto_screenshot_threshold")]
    public int AutoScreenshotThreshold { get; init; } = 10;

    [JsonPropertyName("live_model_id")]
    public string LiveModelId { get; init; } = AsrModelCatalog.GigaAmV2CtcId;

    [JsonPropertyName("include_audio_in_export")]
    public bool IncludeAudioInExport { get; init; }

    [JsonPropertyName("start_sound")]
    public bool StartSound { get; init; } = true;

    [JsonPropertyName("legal_notice_accepted")]
    public bool LegalNoticeAccepted { get; init; }

    /// <summary>
    /// Приложения, с которыми работает MeetMemo (имена процессов, без расширения).
    /// В окнах этих приложений появляется панель управления записью, а в списке выбора
    /// они отмечены галочкой. Пустой список означает «ещё ничего не отмечено».
    /// </summary>
    [JsonPropertyName("tracked_apps")]
    public IReadOnlyList<string> TrackedApps { get; init; } = Array.Empty<string>();

    /// <summary>Показывать ли кнопки управления в заголовках окон отмеченных приложений.</summary>
    [JsonPropertyName("show_title_bar_controls")]
    public bool ShowTitleBarControls { get; init; } = true;

    /// <summary>
    /// Насколько левее правого края окна ставить значок в заголовке. Увеличьте, если
    /// значок налезает на собственные кнопки приложения (аватар, меню); уменьшите,
    /// если он стоит слишком далеко от края.
    /// </summary>
    [JsonPropertyName("title_bar_offset")]
    public double TitleBarOffset { get; init; } = 250;

    /// <summary>
    /// Отступ значка для конкретных приложений: имя процесса → расстояние до правого края.
    /// У каждого приложения слева от системных кнопок своё — аватар профиля, меню, вкладки, —
    /// поэтому одно общее значение не подходит никому. Заполняется перетаскиванием значка.
    /// </summary>
    [JsonPropertyName("title_bar_offsets")]
    public Dictionary<string, double> TitleBarOffsets { get; init; } = new();

    /// <summary>Отступ для приложения: его собственный, а если такого нет — общий.</summary>
    public double OffsetFor(string? applicationName) =>
        applicationName is not null && TitleBarOffsets.TryGetValue(applicationName, out var own)
            ? own
            : TitleBarOffset;

    public AppSettings WithOffset(string applicationName, double offset)
    {
        var map = new Dictionary<string, double>(TitleBarOffsets) { [applicationName] = offset };
        return this with { TitleBarOffsets = map };
    }

    /// <summary>Куда пользователь перетащил плавающую панель записи всей системы.</summary>
    [JsonPropertyName("system_overlay_x")]
    public double? SystemOverlayX { get; init; }

    [JsonPropertyName("system_overlay_y")]
    public double? SystemOverlayY { get; init; }

    public bool IsTracked(string? processName) =>
        processName is not null
        && TrackedApps.Any(a => string.Equals(a, processName, StringComparison.OrdinalIgnoreCase));

    public AppSettings WithTracked(string processName, bool tracked)
    {
        var set = TrackedApps.ToList();
        set.RemoveAll(a => string.Equals(a, processName, StringComparison.OrdinalIgnoreCase));
        if (tracked) set.Add(processName);
        return this with { TrackedApps = set };
    }

    /// <summary>
    /// По умолчанию папка встреч лежит вне облачных синхронизируемых каталогов:
    /// клиенты синхронизации периодически держат файлы открытыми, что мешает
    /// потоковой записи и атомарной подмене.
    /// </summary>
    public static string DefaultMeetingsRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "MeetMemo", "Meetings");

    public static string SettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MeetMemo", "settings.json");

    public static async Task<AppSettings> LoadAsync(CancellationToken ct = default)
    {
        try
        {
            var loaded = await AtomicJsonStore
                .ReadAsync<AppSettings>(SettingsPath, JsonSetup.Pretty, ct)
                .ConfigureAwait(false);
            return loaded ?? new AppSettings();
        }
        catch (Exception)
        {
            // Битые настройки не должны блокировать запуск — берём значения по умолчанию.
            return new AppSettings();
        }
    }

    public Task SaveAsync(CancellationToken ct = default) =>
        AtomicJsonStore.WriteAsync(SettingsPath, this, JsonSetup.Pretty, ct);
}
