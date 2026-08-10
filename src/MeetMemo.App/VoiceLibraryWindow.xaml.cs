using System.IO;
using System.Windows;
using System.Windows.Controls;
using MeetMemo.Asr;
using MeetMemo.Audio;

namespace MeetMemo.App;

/// <summary>
/// Память на голоса: кого приложение узнаёт, как их зовут и кем работают.
///
/// Здесь же исправляется то, что не назвали вовремя: поговорили с человеком, имени
/// не знали — вписали позже. И здесь же голос забывается совсем: отпечаток голоса
/// это биометрия, и человек должен видеть, что о нём помнят, и уметь это стереть.
/// </summary>
public partial class VoiceLibraryWindow : Window
{
    private readonly VoicePrintStore _store;

    private readonly SamplePlayer _player = new();

    private sealed record Row(
        string Id, string Name, string? Role, int Confirmations, string Since,
        string? SampleFile)
    {
        public string HasSample => SampleFile is not null ? "есть" : "—";
    }

    public VoiceLibraryWindow(VoicePrintStore store)
    {
        InitializeComponent();
        _store = store;
        Reload();

        Closed += (_, _) => _player.Dispose();
    }

    private void Reload()
    {
        var rows = _store.All
            .OrderBy(p => p.Name)
            .Select(p => new Row(
                p.Id, p.Name, p.Role, p.Confirmations,
                p.CreatedUtc.ToLocalTime().ToString("dd.MM.yyyy"),
                p.SampleFile))
            .ToList();

        VoiceList.ItemsSource = rows;

        SubtitleText.Text = rows.Count == 0
            ? "Пока никого. Во время встречи нажмите «Назвать» под именем говорящего — "
              + "голос запомнится, и дальше имя будет подставляться само."
            : $"Приложение узнаёт по голосу: {rows.Count}. "
              + "Отпечатки хранятся только на этом компьютере.";
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (VoiceList.SelectedItem is not Row row)
        {
            NameBox.IsEnabled = RoleBox.IsEnabled = SaveButton.IsEnabled = ForgetButton.IsEnabled = false;
            PlayButton.IsEnabled = false;
            NameBox.Text = RoleBox.Text = string.Empty;
            return;
        }

        NameBox.Text = row.Name;
        RoleBox.Text = row.Role ?? string.Empty;
        NameBox.IsEnabled = RoleBox.IsEnabled = SaveButton.IsEnabled = ForgetButton.IsEnabled = true;

        // Слушать нечего у голосов, запомненных до появления образцов.
        PlayButton.IsEnabled = row.SampleFile is not null && File.Exists(row.SampleFile);
    }

    private void OnPlayClick(object sender, RoutedEventArgs e)
    {
        if (VoiceList.SelectedItem is not Row { SampleFile: { } file }) return;

        try
        {
            _player.Play(file);
        }
        catch (Exception)
        {
            SubtitleText.Text = "Не удалось воспроизвести — проверьте устройство вывода звука.";
        }
    }

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        if (VoiceList.SelectedItem is not Row row) return;

        var name = NameBox.Text.Trim();
        if (name.Length == 0) return;

        var role = RoleBox.Text.Trim();
        _store.Rename(row.Id, name, role.Length > 0 ? role : null);
        Reload();
    }

    private void OnForgetClick(object sender, RoutedEventArgs e)
    {
        if (VoiceList.SelectedItem is not Row row) return;

        var answer = MessageBox.Show(
            this,
            $"Забыть голос «{row.Name}»?\n\nОтпечаток удалится с компьютера, "
            + "и на следующих встречах этот человек снова будет неизвестным.",
            "MeetMemo",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (answer != MessageBoxResult.Yes) return;

        _store.Forget(row.Id);
        Reload();
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
