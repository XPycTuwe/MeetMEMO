using System.Windows;
using System.Windows.Controls;
using MeetMemo.Asr;

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

    public VoiceNameWindow(VoicePrintStore store, float[] embedding, string? suggestedName = null)
    {
        InitializeComponent();

        _store = store;
        _embedding = embedding;

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
        _store.Remember(name, role.Length > 0 ? role : null, _embedding);
        Close();
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => Close();
}
