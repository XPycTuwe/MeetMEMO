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

/// <summary>
/// Окно выбора источника (ТЗ 6.1). Здесь пользователь подтверждает всё, что влияет
/// на приватность: какое окно снимается, какой звук пишется и сохраняются ли файлы.
/// </summary>
public partial class SourcePickerWindow : Window
{
    private readonly AppSettings _settings;
    private List<WindowRow> _allRows = new();
    private bool _titleEditedByUser;
    private bool _suppressTitleEdit;

    public SourcePickerWindow(AppSettings settings)
    {
        InitializeComponent();
        _settings = settings;

        TitleBox.Text = $"Встреча {DateTime.Now:dd.MM.yyyy HH:mm}";
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

    /// <summary>Заполненный запрос на старт — читается вызывающим после ShowDialog.</summary>
    public SessionStartRequest? Result { get; private set; }

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

    private void OnTitleChanged(object sender, TextChangedEventArgs e)
    {
        if (!_suppressTitleEdit) _titleEditedByUser = true;
    }

    private void OnRefreshClick(object sender, RoutedEventArgs e) => LoadWindows();

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (WindowList.SelectedItem is not WindowRow row) return;

        // Название следует за выбором окна, пока пользователь не отредактировал его сам:
        // иначе при смене выбора в названии остаётся заголовок предыдущего окна.
        if (!_titleEditedByUser)
        {
            _suppressTitleEdit = true;
            TitleBox.Text = row.Title.Length > 60 ? row.Title[..60] : row.Title;
            _suppressTitleEdit = false;
        }

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

    private void OnStartClick(object sender, RoutedEventArgs e)
    {
        var mode = AudioModeBox.SelectedIndex switch
        {
            1 => AudioMode.System,
            2 => AudioMode.MicrophoneOnly,
            _ => AudioMode.ApplicationProcessTree
        };

        TargetSelection? target = null;
        if (WindowList.SelectedItem is WindowRow row)
        {
            target = new TargetSelection
            {
                WindowHandle = row.Candidate.Handle,
                ProcessId = row.Candidate.ProcessId,
                ApplicationName = row.Candidate.ProcessName,
                WindowTitle = row.Candidate.Title,
                ExecutablePath = row.Candidate.ExecutablePath
            };
        }
        else if (mode != AudioMode.MicrophoneOnly)
        {
            ShowWarning("Выберите окно встречи или переключитесь на режим «Только микрофон».");
            return;
        }

        var device = MicrophoneBox.SelectedItem as AudioDeviceInfo;

        Result = new SessionStartRequest
        {
            Title = string.IsNullOrWhiteSpace(TitleBox.Text) ? "Встреча" : TitleBox.Text.Trim(),
            MeetingsRoot = _settings.MeetingsRoot,
            AudioMode = mode,
            MicrophoneDeviceId = device?.Id,
            MicrophoneDeviceName = device?.Name,
            SaveAudioFiles = SaveAudioBox.IsChecked == true,
            AutoScreenshotsEnabled = AutoShotsBox.IsChecked == true,
            Target = target
        };

        DialogResult = true;
        Close();
    }
}
