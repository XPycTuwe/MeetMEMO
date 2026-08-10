using System.Windows;
using System.Windows.Controls;
using MeetMemo.Asr;
using MeetMemo.Audio;

namespace MeetMemo.App;

/// <summary>
/// «Кто это говорит?» — присвоение имени голосу.
///
/// Открывается прямо во время встречи, когда приложение ошиблось или услышало новый
/// голос. Отпечаток к этому моменту уже посчитан — сохраняется именно он, а не то,
/// что прозвучит, пока человек печатает.
/// </summary>
public partial class VoiceNameWindow : Window
{
    private readonly VoicePrintStore _store;
    private readonly float[] _embedding;
    private readonly float[]? _audio;
    private readonly SamplePlayer _player = new();

    public VoiceNameWindow(
        VoicePrintStore store, float[] embedding, float[]? audio = null, string? suggestedName = null)
    {
        InitializeComponent();

        _store = store;
        _embedding = embedding;
        _audio = audio;

        // Нечего играть — незачем и предлагать.
        if (_audio is null || _audio.Length == 0) PlayButton.Visibility = Visibility.Collapsed;

        Closed += (_, _) => _player.Dispose();

        NameBox.Text = suggestedName ?? string.Empty;

        var known = store.All;
        if (known.Count > 0)
        {
            KnownLabel.Visibility = Visibility.Visible;
            KnownBox.Visibility = Visibility.Visible;
            KnownBox.ItemsSource = known.Select(p => p.Display).ToList();
        }

        Loaded += (_, _) => { NameBox.Focus(); NameBox.SelectAll(); };
    }

    /// <summary>Выбор знакомого подставляет его имя и должность — чтобы не плодить двойников.</summary>
    private void OnKnownSelected(object sender, SelectionChangedEventArgs e)
    {
        if (KnownBox.SelectedIndex < 0) return;

        var print = _store.All[KnownBox.SelectedIndex];
        NameBox.Text = print.Name;
        RoleBox.Text = print.Role ?? string.Empty;
    }

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        var name = NameBox.Text.Trim();
        if (name.Length == 0)
        {
            HintText.Text = "Введите имя — под ним голос и запомнится.";
            NameBox.Focus();
            return;
        }

        var role = RoleBox.Text.Trim();

        // Вместе с отпечатком кладём и саму фразу: позже её можно будет переслушать
        // и проверить, того ли человека мы помним.
        _store.Remember(name, role.Length > 0 ? role : null, _embedding, _audio);
        Close();
    }

    private void OnPlayClick(object sender, RoutedEventArgs e)
    {
        if (_audio is null) return;

        try
        {
            _player.Play(_audio);
        }
        catch (Exception)
        {
            HintText.Text = "Не удалось воспроизвести — проверьте устройство вывода звука.";
        }
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => Close();
}
