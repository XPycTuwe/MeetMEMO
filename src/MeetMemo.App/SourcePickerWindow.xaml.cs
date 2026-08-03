using System.Windows;
using System.Windows.Controls;
using MeetMemo.Audio;
using MeetMemo.Capture;
using MeetMemo.Contracts;
using MeetMemo.Core;

namespace MeetMemo.App;

/// <summary>Строка списка окон.</summary>
public sealed class WindowRow : System.ComponentModel.INotifyPropertyChanged
{
    private bool _isTracked;

    public required WindowCandidate Candidate { get; init; }

    /// <summary>Понятное название приложения, а не имя процесса вроде «browser» или «olk».</summary>
    public string ProcessName => Candidate.AppLabel;
    public string Title => Candidate.Title;
    public string SizeText => $"{Candidate.Width}×{Candidate.Height}";
    public string StateText => Candidate.IsMinimized ? "свёрнуто" : "открыто";

    /// <summary>
    /// Приложение отмечено как «ведём»: в заголовках его окон появляются кнопки записи.
    /// Отметка относится к приложению целиком, а не к конкретному окну.
    /// </summary>
    public bool IsTracked
    {
        get => _isTracked;
        set
        {
            if (_isTracked == value) return;
            _isTracked = value;
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(IsTracked)));
            TrackedChanged?.Invoke(this);
        }
    }

    /// <summary>Пользователь поменял отметку — окно выбора сохраняет её в настройки.</summary>
    public event Action<WindowRow>? TrackedChanged;

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>Параметры записи, подтверждённые пользователем в окне настройки.</summary>
public sealed record RecordingPreferences
{
    public required AudioMode AudioMode { get; init; }

    public string? MicrophoneDeviceId { get; init; }

    public required bool SaveAudioFiles { get; init; }

    public required bool AutoScreenshotsEnabled { get; init; }
}

/// <summary>
/// Окно настройки записи (ТЗ 6.1). Здесь пользователь подтверждает всё, что влияет
/// на приватность: какие приложения ведутся, какой звук пишется и сохраняются ли файлы.
///
/// Запись отсюда не начинается: её запускает кнопка в заголовке отмеченного окна — там
/// видно, какое именно окно пишется. Это окно только сохраняет параметры.
/// </summary>
public partial class SourcePickerWindow : Window
{
    private readonly AppSettings _settings;
    private List<WindowRow> _allRows = new();

    public SourcePickerWindow(AppSettings settings)
    {
        InitializeComponent();
        _settings = settings;

        SaveAudioBox.IsChecked = settings.SaveAudioFiles;
        AutoShotsBox.IsChecked = settings.AutoScreenshots;

        AudioModeBox.Items.Add("Звук выбранного приложения");
        AudioModeBox.Items.Add("Общий звук системы");
        AudioModeBox.Items.Add("Только микрофон (очная встреча)");
        AudioModeBox.SelectedIndex = settings.AudioMode switch
        {
            AudioMode.System => 1,
            AudioMode.MicrophoneOnly => 2,
            _ => 0
        };

        LoadMicrophones();
        LoadWindows();
    }

    /// <summary>Подтверждённые параметры — читаются вызывающим после ShowDialog.</summary>
    public RecordingPreferences? Result { get; private set; }

    private void LoadMicrophones()
    {
        var devices = DeviceCapture.ListMicrophones().ToList();
        MicrophoneBox.ItemsSource = devices;

        if (devices.Count == 0)
        {
            ShowWarning("Микрофон не найден. Запись пойдёт без вашего голоса.");
            return;
        }

        var saved = devices.FirstOrDefault(d => d.Id == _settings.MicrophoneDeviceId);
        MicrophoneBox.SelectedItem = saved ?? devices.FirstOrDefault(d => d.IsDefault) ?? devices[0];
    }

    private void LoadWindows()
    {
        var candidates = WindowEnumerator.Enumerate();

        _allRows = candidates
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

        ApplyFilter();

        if (WindowList.Items.Count > 0) WindowList.SelectedIndex = 0;

        // Пустой список — либо действительно нет подходящих окон, либо сбой перечисления.
        // Показываем, сколько окон вообще увидела система: это сразу отличает одно от другого.
        if (candidates.Count == 0)
        {
            ShowWarning(WindowEnumerator.LastSeenWindowCount == 0
                ? "Не удалось получить список окон. Попробуйте «Обновить список»; "
                  + "если не помогает, перезапустите MeetMemo."
                : $"Подходящих окон не найдено (система показала {WindowEnumerator.LastSeenWindowCount} окон, "
                  + "все они служебные или свёрнутые). Откройте окно встречи и нажмите «Обновить список».");
        }
    }

    /// <summary>Отметка «вести» действует на всё приложение, поэтому синхронизируем однофамильцев.</summary>
    private void OnTrackedChanged(WindowRow changed)
    {
        var name = changed.Candidate.ProcessName;
        if (name is null) return;

        foreach (var row in _allRows)
        {
            if (row != changed
                && string.Equals(row.Candidate.ProcessName, name, StringComparison.OrdinalIgnoreCase))
            {
                row.IsTracked = changed.IsTracked;
            }
        }

        TrackedAppsChanged?.Invoke(name, changed.IsTracked);
    }

    /// <summary>Приложение отмечено или снято с отметки: имя процесса и новое состояние.</summary>
    public event Action<string, bool>? TrackedAppsChanged;

    private void ApplyFilter()
    {
        var query = SearchBox.Text?.Trim();
        var rows = string.IsNullOrEmpty(query)
            ? _allRows
            : _allRows.Where(r =>
                r.Title.Contains(query, StringComparison.OrdinalIgnoreCase)
                || (r.Candidate.ProcessName?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false))
              .ToList();

        WindowList.ItemsSource = rows;
    }

    private void OnSearchChanged(object sender, TextChangedEventArgs e) => ApplyFilter();

    private void OnRefreshClick(object sender, RoutedEventArgs e) => LoadWindows();

    /// <summary>
    /// Двойным щелчком раньше начиналась запись. Теперь строка просто переключает отметку:
    /// случайный двойной клик по списку не должен запускать запись встречи.
    /// </summary>
    private void OnRowDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (WindowList.SelectedItem is WindowRow row) row.IsTracked = !row.IsTracked;
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (WindowList.SelectedItem is not WindowRow row) return;

        // У многопроцессных браузеров изоляция звука идёт по дереву процессов, а не по вкладке:
        // предупредить об этом честнее, чем показывать неожиданный результат в записи.
        var process = row.Candidate.ProcessName?.ToLowerInvariant() ?? string.Empty;
        if (process is "chrome" or "msedge" or "firefox" or "yandex" or "opera")
        {
            ShowWarning("Это браузер: в дорожку может попасть звук другой вкладки того же процесса. "
                      + "Если постороннего звука быть не должно, закройте лишние вкладки.");
        }
        else
        {
            WarningText.Visibility = Visibility.Collapsed;
        }
    }

    private void ShowWarning(string text)
    {
        WarningText.Text = text;
        WarningText.Visibility = Visibility.Visible;
    }

    /// <summary>
    /// Сохраняем параметры и закрываем окно. Запись не начинаем: её запускают кнопкой
    /// в заголовке отмеченного окна — так всегда видно, какое окно пишется.
    /// </summary>
    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        var mode = AudioModeBox.SelectedIndex switch
        {
            1 => AudioMode.System,
            2 => AudioMode.MicrophoneOnly,
            _ => AudioMode.ApplicationProcessTree
        };

        Result = new RecordingPreferences
        {
            AudioMode = mode,
            MicrophoneDeviceId = (MicrophoneBox.SelectedItem as AudioDeviceInfo)?.Id,
            SaveAudioFiles = SaveAudioBox.IsChecked == true,
            AutoScreenshotsEnabled = AutoShotsBox.IsChecked == true
        };

        DialogResult = true;
        Close();
    }
}
