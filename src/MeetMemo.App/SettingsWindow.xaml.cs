using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using MeetMemo.Asr;
using MeetMemo.Audio;
using MeetMemo.Capture;
using MeetMemo.Contracts;

namespace MeetMemo.App;

/// <summary>
/// Единственное окно настроек: приложения и запись, знакомые голоса, модели, встречи.
///
/// Раньше это были четыре отдельных пункта в меню значка, и человек не понимал, где что
/// искать: список встреч в одном месте, память голосов в другом, модели в третьем.
/// Теперь одно окно с вкладками, а в меню осталось только самое частое.
/// </summary>
public partial class SettingsWindow : Window
{
    private readonly VoicePrintStore _voices;
    private readonly SamplePlayer _player = new();

    private AppSettings _settings;
    private List<WindowRow> _allWindows = new();

    /// <summary>Настройки изменены и сохранены — приложение перечитывает их у себя.</summary>
    public event Action<AppSettings>? SettingsSaved;

    /// <summary>Пользователь попросил открыть карточку встречи.</summary>
    public event Action<string>? MeetingRequested;

    /// <summary>Пользователь попросил докачать модели.</summary>
    public event Action? ModelsDownloadRequested;

    public SettingsWindow(AppSettings settings, VoicePrintStore voices)
    {
        InitializeComponent();

        _settings = settings;
        _voices = voices;

        LoadRecordingTab();
        LoadVoices();
        LoadModels();
        LoadMeetings();

        Closed += (_, _) => _player.Dispose();
    }

    /// <summary>Отметка «вести» поменялась — сохраняем сразу, не дожидаясь кнопки.</summary>
    public event Action<string, bool>? TrackedAppsChanged;

    // ======================= Приложения и запись =======================

    private void LoadRecordingTab()
    {
        SaveAudioBox.IsChecked = _settings.SaveAudioFiles;
        AutoShotsBox.IsChecked = _settings.AutoScreenshots;
        SubtitlesBox.IsChecked = _settings.ShowSubtitles;
        ConfirmShotsBox.IsChecked = _settings.ConfirmAutoScreenshots;

        AudioModeBox.Items.Add("Звук выбранного приложения");
        AudioModeBox.Items.Add("Общий звук системы");
        AudioModeBox.Items.Add("Только микрофон (очная встреча)");
        AudioModeBox.SelectedIndex = _settings.AudioMode switch
        {
            AudioMode.System => 1,
            AudioMode.MicrophoneOnly => 2,
            _ => 0
        };

        var devices = DeviceCapture.ListMicrophones().ToList();
        MicrophoneBox.ItemsSource = devices;
        MicrophoneBox.SelectedItem = devices.FirstOrDefault(d => d.Id == _settings.MicrophoneDeviceId)
            ?? devices.FirstOrDefault(d => d.IsDefault)
            ?? devices.FirstOrDefault();

        if (devices.Count == 0) ShowWarning("Микрофон не найден. Запись пойдёт без вашего голоса.");

        LoadWindows();
    }

    private void LoadWindows()
    {
        var candidates = WindowEnumerator.Enumerate();

        _allWindows = candidates
            .Select(c =>
            {
                var row = new WindowRow
                {
                    Candidate = c,
                    IsTracked = _settings.IsTracked(c.ProcessName)
                };
                row.TrackedChanged += OnTrackedChanged;
                return row;
            })
            .ToList();

        ApplyWindowFilter();

        if (candidates.Count == 0)
        {
            ShowWarning(WindowEnumerator.LastSeenWindowCount == 0
                ? "Не удалось получить список окон. Попробуйте «Обновить список»."
                : $"Подходящих окон не найдено (система показала {WindowEnumerator.LastSeenWindowCount}). "
                  + "Откройте окно встречи и обновите список.");
        }
    }

    /// <summary>Отметка относится к приложению целиком — синхронизируем однофамильцев.</summary>
    private void OnTrackedChanged(WindowRow changed)
    {
        var name = changed.Candidate.ProcessName;
        if (name is null) return;

        foreach (var row in _allWindows)
        {
            if (row != changed
                && string.Equals(row.Candidate.ProcessName, name, StringComparison.OrdinalIgnoreCase))
            {
                row.IsTracked = changed.IsTracked;
            }
        }

        TrackedAppsChanged?.Invoke(name, changed.IsTracked);
    }

    private void ApplyWindowFilter()
    {
        var query = SearchBox.Text?.Trim();

        WindowList.ItemsSource = string.IsNullOrEmpty(query)
            ? _allWindows
            : _allWindows.Where(r =>
                r.Title.Contains(query, StringComparison.OrdinalIgnoreCase)
                || (r.Candidate.ProcessName?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false))
              .ToList();
    }

    private void OnSearchChanged(object sender, TextChangedEventArgs e) => ApplyWindowFilter();

    private void OnRefreshWindows(object sender, RoutedEventArgs e) => LoadWindows();

    /// <summary>Двойной щелчок переключает отметку: случайный клик не должен ничего запускать.</summary>
    private void OnWindowDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (WindowList.SelectedItem is WindowRow row) row.IsTracked = !row.IsTracked;
    }

    private void OnWindowSelected(object sender, SelectionChangedEventArgs e)
    {
        if (WindowList.SelectedItem is not WindowRow row) return;

        // У браузеров изоляция звука идёт по дереву процессов, а не по вкладке.
        var process = row.Candidate.ProcessName?.ToLowerInvariant() ?? string.Empty;
        if (process is "chrome" or "msedge" or "firefox" or "yandex" or "opera" or "browser")
            ShowWarning("Это браузер: в дорожку может попасть звук другой вкладки того же процесса.");
        else
            WarningText.Visibility = Visibility.Collapsed;
    }

    private void ShowWarning(string text)
    {
        WarningText.Text = text;
        WarningText.Visibility = Visibility.Visible;
    }

    private void OnSaveSettings(object sender, RoutedEventArgs e)
    {
        var mode = AudioModeBox.SelectedIndex switch
        {
            1 => AudioMode.System,
            2 => AudioMode.MicrophoneOnly,
            _ => AudioMode.ApplicationProcessTree
        };

        _settings = _settings with
        {
            AudioMode = mode,
            MicrophoneDeviceId = (MicrophoneBox.SelectedItem as AudioDeviceInfo)?.Id,
            SaveAudioFiles = SaveAudioBox.IsChecked == true,
            AutoScreenshots = AutoShotsBox.IsChecked == true,
            ShowSubtitles = SubtitlesBox.IsChecked == true,
            ConfirmAutoScreenshots = ConfirmShotsBox.IsChecked == true
        };

        SettingsSaved?.Invoke(_settings);
        ShowWarning("Параметры сохранены. Запись начинается кнопкой в заголовке отмеченного окна.");
    }

    // ============================ Голоса ============================

    private sealed record VoiceRow(
        string Id, string Name, string? Role, int Confirmations, string Since,
        string? SampleFile, string? PhotoFile)
    {
        public string HasPhoto => PhotoFile is not null ? "есть" : "—";
    }

    private string? _pendingPhoto;

    private void LoadVoices()
    {
        var rows = _voices.All
            .OrderBy(p => p.Name)
            .Select(p => new VoiceRow(
                p.Id, p.Name, p.Role, p.Confirmations,
                p.CreatedUtc.ToLocalTime().ToString("dd.MM.yyyy"),
                p.SampleFile, p.PhotoFile))
            .ToList();

        VoiceList.ItemsSource = rows;

        VoicesSubtitle.Text = rows.Count == 0
            ? "Пока никого. Во время встречи нажмите «Назвать» у реплики — голос запомнится, "
              + "и дальше имя будет подставляться само."
            : $"Приложение узнаёт по голосу: {rows.Count}. Отпечатки хранятся только на этом компьютере.";
    }

    private void OnVoiceSelected(object sender, SelectionChangedEventArgs e)
    {
        _pendingPhoto = null;

        if (VoiceList.SelectedItem is not VoiceRow row)
        {
            VoiceNameBox.IsEnabled = VoiceRoleBox.IsEnabled = false;
            SaveVoiceButton.IsEnabled = ForgetVoiceButton.IsEnabled = false;
            PlayVoiceButton.IsEnabled = PhotoButton.IsEnabled = false;
            VoiceNameBox.Text = VoiceRoleBox.Text = string.Empty;
            ShowVoicePhoto(null, string.Empty);
            return;
        }

        VoiceNameBox.Text = row.Name;
        VoiceRoleBox.Text = row.Role ?? string.Empty;

        VoiceNameBox.IsEnabled = VoiceRoleBox.IsEnabled = true;
        SaveVoiceButton.IsEnabled = ForgetVoiceButton.IsEnabled = PhotoButton.IsEnabled = true;
        PlayVoiceButton.IsEnabled = row.SampleFile is not null && File.Exists(row.SampleFile);

        ShowVoicePhoto(row.PhotoFile, row.Name);
    }

    /// <summary>Фото человека, а если его нет — кружок с инициалами.</summary>
    private void ShowVoicePhoto(string? path, string name)
    {
        if (path is not null && File.Exists(path))
        {
            try
            {
                var image = new BitmapImage();
                image.BeginInit();
                image.UriSource = new Uri(path);
                image.DecodePixelWidth = 160;
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.EndInit();
                image.Freeze();

                VoicePhotoBrush.ImageSource = image;
                VoiceInitials.Text = string.Empty;
                return;
            }
            catch (Exception)
            {
                // Файл мог быть удалён или испорчен — покажем инициалы.
            }
        }

        VoicePhotoBrush.ImageSource = null;
        VoiceInitials.Text = name.Length > 0 ? VoicePrint.MakeInitials(name) : string.Empty;
    }

    private void OnChoosePhoto(object sender, RoutedEventArgs e)
    {
        if (VoiceList.SelectedItem is not VoiceRow row) return;

        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = $"Фото: {row.Name}",
            Filter = "Изображения|*.jpg;*.jpeg;*.png;*.bmp|Все файлы|*.*"
        };

        if (dialog.ShowDialog(this) != true) return;

        _pendingPhoto = dialog.FileName;
        ShowVoicePhoto(_pendingPhoto, row.Name);
    }

    private void OnSaveVoice(object sender, RoutedEventArgs e)
    {
        if (VoiceList.SelectedItem is not VoiceRow row) return;

        var name = VoiceNameBox.Text.Trim();
        if (name.Length == 0) return;

        var role = VoiceRoleBox.Text.Trim();
        _voices.Rename(row.Id, name, role.Length > 0 ? role : null, _pendingPhoto);

        _pendingPhoto = null;
        LoadVoices();
    }

    private void OnPlayVoice(object sender, RoutedEventArgs e)
    {
        if (VoiceList.SelectedItem is not VoiceRow { SampleFile: { } file }) return;

        try { _player.Play(file); }
        catch (Exception) { VoicesSubtitle.Text = "Не удалось воспроизвести — проверьте устройство вывода."; }
    }

    private void OnForgetVoice(object sender, RoutedEventArgs e)
    {
        if (VoiceList.SelectedItem is not VoiceRow row) return;

        var answer = MessageBox.Show(
            this,
            $"Забыть голос «{row.Name}»?\n\nОтпечаток и запись речи удалятся с компьютера.",
            "MeetMemo", MessageBoxButton.YesNo, MessageBoxImage.Question);

        if (answer != MessageBoxResult.Yes) return;

        _voices.Forget(row.Id);
        LoadVoices();
    }

    // ============================ Модели ============================

    private sealed record ModelRow(string Name, string Purpose, string Size, string State);

    private void LoadModels()
    {
        var manager = new ModelManager(_settings.ModelsRoot);

        var purposes = new Dictionary<string, string>
        {
            ["gigaam-v2-ctc"] = "Распознавание русской речи",
            ["silero-vad"] = "Определение границ фраз",
            ["pyannote-segmentation-3-0"] = "Кто когда говорит",
            ["3dspeaker-eres2net-base"] = "Отпечатки голосов"
        };

        var rows = AsrModelCatalog.Required
            .Select(m => new ModelRow(
                m.DisplayName,
                purposes.GetValueOrDefault(m.Id, m.Notes ?? string.Empty),
                $"{m.ApproxSizeBytes / 1024 / 1024} МБ",
                manager.IsInstalled(m) ? "установлена" : "не скачана"))
            .ToList();

        ModelList.ItemsSource = rows;

        var missing = manager.GetMissing();
        ModelsSubtitle.Text = missing.Count == 0
            ? "Все модели на месте. Распознавание работает на этом компьютере, без интернета."
            : $"Не хватает моделей: {missing.Count}. Без них запись пойдёт, но стенограммы не будет.";

        DownloadModelsButton.IsEnabled = missing.Count > 0;
        ModelsPath.Text = manager.ModelsRoot;
    }

    private void OnDownloadModels(object sender, RoutedEventArgs e)
    {
        ModelsDownloadRequested?.Invoke();
        ModelsSubtitle.Text = "Загрузка идёт в фоне, ход виден по значку в трее.";
        DownloadModelsButton.IsEnabled = false;
    }

    // ============================ Встречи ============================

    private sealed record MeetingRow(string Path, string Name, string When, string Size);

    private void LoadMeetings()
    {
        try
        {
            if (!Directory.Exists(_settings.MeetingsRoot)) return;

            var rows = new DirectoryInfo(_settings.MeetingsRoot)
                .GetDirectories()
                .OrderByDescending(d => d.LastWriteTime)
                .Take(30)
                .Select(d => new MeetingRow(
                    d.FullName,
                    d.Name,
                    d.LastWriteTime.ToString("dd.MM.yyyy HH:mm"),
                    FormatSize(DirectorySize(d))))
                .ToList();

            MeetingList.ItemsSource = rows;
        }
        catch (Exception)
        {
            // Папка могла оказаться недоступной — вкладка просто останется пустой.
        }
    }

    private static long DirectorySize(DirectoryInfo dir)
    {
        try { return dir.EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length); }
        catch (Exception) { return 0; }
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        >= 1024L * 1024 * 1024 => $"{bytes / 1024.0 / 1024 / 1024:0.#} ГБ",
        >= 1024 * 1024 => $"{bytes / 1024.0 / 1024:0.#} МБ",
        >= 1024 => $"{bytes / 1024.0:0.#} КБ",
        _ => $"{bytes} Б"
    };

    private void OnMeetingSelected(object sender, SelectionChangedEventArgs e)
    {
        var has = MeetingList.SelectedItem is MeetingRow;
        OpenFolderButton.IsEnabled = OpenCardButton.IsEnabled = has;
    }

    private void OnOpenMeeting(object sender, RoutedEventArgs e)
    {
        if (MeetingList.SelectedItem is MeetingRow row) MeetingRequested?.Invoke(row.Path);
    }

    /// <summary>
    /// Отдельное имя для двойного щелчка: у одноимённых обработчиков разных событий
    /// WPF выбирает перегрузку в момент разбора разметки, и промах вылезает только в работе.
    /// </summary>
    private void OnMeetingDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e) =>
        OnOpenMeeting(sender, e);

    private void OnOpenMeetingFolder(object sender, RoutedEventArgs e)
    {
        if (MeetingList.SelectedItem is not MeetingRow row) return;

        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{row.Path}\"")
        {
            UseShellExecute = true
        });
    }
}
