using MeetMemo.Capture;

namespace MeetMemo.App;

/// <summary>Строка списка окон в параметрах: что за приложение и ведём ли мы его.</summary>
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

    /// <summary>Пользователь поменял отметку — окно параметров сохраняет её в настройки.</summary>
    public event Action<WindowRow>? TrackedChanged;

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
}
