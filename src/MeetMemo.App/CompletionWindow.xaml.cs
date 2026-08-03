using System.Diagnostics;
using System.IO;
using System.Windows;
using MeetMemo.Contracts;
using MeetMemo.Core;
using MeetMemo.Export;

namespace MeetMemo.App;

/// <summary>
/// Карточка завершения (ТЗ 5.1): что записано, где лежит и что уйдёт в архив.
/// Состав пакета показывается до создания ZIP — пользователь должен видеть,
/// что именно покинет его компьютер (AC-21).
/// </summary>
public partial class CompletionWindow : Window
{
    private readonly SessionResult _result;
    private readonly AppSettings _settings;

    public CompletionWindow(SessionResult result, AppSettings settings)
    {
        InitializeComponent();
        _result = result;
        _settings = settings;

        HeaderText.Text = result.Status switch
        {
            SessionStatus.CompletedWithWarnings => "Встреча записана с предупреждениями",
            SessionStatus.Recovered => "Встреча восстановлена",
            SessionStatus.Failed => "Запись завершилась с ошибкой",
            _ => "Встреча записана"
        };

        SummaryText.Text =
            $"Длительность: {FormatDuration(result.Duration)}   •   "
            + $"Реплик: {result.SegmentCount}   •   Снимков: {result.ScreenshotCount}";

        PathText.Text = result.FolderPath;

        var plan = ExportPlanBuilder.Build(result.FolderPath, settings.IncludeAudioInExport);
        FilesList.ItemsSource = plan.Items
            .Where(i => i.Included)
            .Select(i => $"{i.RelativePath}  ({ExportPlan.FormatSize(i.SizeBytes)})")
            .ToList();

        ZipButton.Content = $"Собрать ZIP ({ExportPlan.FormatSize(plan.TotalBytes)})";

        if (result.Warnings.Count > 0)
        {
            WarningsText.Text = "Предупреждения:\n• " + string.Join("\n• ", result.Warnings);
            WarningsText.Visibility = Visibility.Visible;
        }
    }

    private static string FormatDuration(TimeSpan ts) =>
        ts.Hours > 0 ? $"{ts.Hours} ч {ts.Minutes} мин" : $"{ts.Minutes} мин {ts.Seconds} с";

    /// <summary>
    /// Карточка показывается немодально, поэтому IsCancel сам окно не закрывает —
    /// нужен явный обработчик, иначе работает только системный крестик.
    /// </summary>
    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    private void OnOpenFolderClick(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_result.FolderPath}\"")
            {
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Не удалось открыть папку: {ex.Message}", "MeetMemo",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void OnCreateZipClick(object sender, RoutedEventArgs e)
    {
        ZipButton.IsEnabled = false;
        try
        {
            var plan = ExportPlanBuilder.Build(_result.FolderPath, _settings.IncludeAudioInExport);
            var archivePath = Path.Combine(
                Path.GetDirectoryName(_result.FolderPath)!,
                ZipPackager.SuggestArchiveName(_result.FolderPath));

            var packager = new ZipPackager();
            var file = await packager.CreateAsync(plan, archivePath);

            var answer = MessageBox.Show(
                this,
                $"Архив собран:\n{file.FullName}\n\nРазмер: {ExportPlan.FormatSize(file.Length)}\n\n"
                + "Загрузите его в Claude и попросите подготовить мемо.\n\nОткрыть папку с архивом?",
                "Архив готов",
                MessageBoxButton.YesNo,
                MessageBoxImage.Information);

            if (answer == MessageBoxResult.Yes)
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{file.FullName}\"")
                {
                    UseShellExecute = true
                });
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Не удалось собрать архив: {ex.Message}", "MeetMemo",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            ZipButton.IsEnabled = true;
        }
    }
}
