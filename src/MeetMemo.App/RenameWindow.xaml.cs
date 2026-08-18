using System.Windows;

namespace MeetMemo.App;

/// <summary>Ввод нового названия встречи.</summary>
public partial class RenameWindow : Window
{
    public RenameWindow(string currentTitle)
    {
        InitializeComponent();

        TitleBox.Text = currentTitle;

        // Выделяем текст целиком: чаще название переписывают, а не дополняют.
        Loaded += (_, _) => { TitleBox.Focus(); TitleBox.SelectAll(); };
    }

    /// <summary>Введённое название либо null, если передумали.</summary>
    public string? Result { get; private set; }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        var title = TitleBox.Text.Trim();
        if (title.Length == 0) { TitleBox.Focus(); return; }

        Result = title;
        DialogResult = true;
        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e) => Close();
}
