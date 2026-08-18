using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using MeetMemo.Contracts;
using MeetMemo.Core;
using MeetMemo.Export;
using MeetMemo.Storage;

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

    /// <summary>
    /// Открывает карточку для встречи, записанной раньше. Собрать архив можно было
    /// только сразу после остановки; если окно закрыли, оставалось паковать вручную.
    /// Сводку восстанавливаем по файлам самой папки — session.json описывает встречу
    /// полностью, а реплики и снимки пересчитываются по месту.
    /// </summary>
    public static CompletionWindow? ForFolder(string folderPath, AppSettings settings)
    {
        var folder = new MeetingFolder(folderPath);
        if (!File.Exists(folder.SessionJson)) return null;

        SessionManifest? manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<SessionManifest>(
                File.ReadAllText(folder.SessionJson), JsonSetup.Compact);
        }
        catch (Exception)
        {
            return null;
        }

        if (manifest is null) return null;

        var result = new SessionResult
        {
            SessionId = manifest.SessionId,
            FolderPath = folderPath,
            Status = manifest.Status,
            Duration = TimeSpan.FromMilliseconds(manifest.DurationMs ?? 0),
            SegmentCount = CountLines(folder.TranscriptJsonl),
            ScreenshotCount = Directory.Exists(folder.ScreenshotsDir)
                ? Directory.GetFiles(folder.ScreenshotsDir, "*.png").Length
                : 0
        };

        return new CompletionWindow(result, settings);
    }

    private static int CountLines(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadLines(path).Count() : 0;
        }
        catch (IOException)
        {
            return 0;
        }
    }

    /// <summary>
    /// Показывает, что пакет ещё дорабатывается: идёт разбор голосов и склейка дублей
    /// снимков. Кнопка архива при этом не блокируется — бывает, что мемо нужно прямо
    /// сейчас, — но человек видит, чего лишится, если поторопится.
    /// </summary>
    public void ShowProcessing(string what)
    {
        ProcessingPanel.Visibility = Visibility.Visible;
        ProcessingText.Text = what;
        ZipButton.Content = "Собрать ZIP сейчас";
    }

    /// <summary>Обработка закончена: состав пакета пересчитываем — он изменился.</summary>
    public void ProcessingFinished(string summary)
    {
        ProcessingBar.IsIndeterminate = false;
        ProcessingBar.Value = 100;
        ProcessingText.Text = summary;
        ProcessingHint.Text = "Пакет готов полностью.";
        ZipButton.Content = "Собрать ZIP";

        RefreshFileList();
    }

    private void RefreshFileList()
    {
        try
        {
            var plan = ExportPlanBuilder.Build(_result.FolderPath, _settings.IncludeAudioInExport);
            FilesList.ItemsSource = plan.Items
                .Where(i => i.Included)
                .Select(i => $"{i.RelativePath}  ({ExportPlan.FormatSize(i.SizeBytes)})")
                .ToList();
        }
        catch (Exception)
        {
            // Список — справочный: если пересчитать не вышло, окно должно остаться рабочим.
        }
    }

    private static string FormatDuration(TimeSpan ts) =>
        ts.Hours > 0 ? $"{ts.Hours} ч {ts.Minutes} мин" : $"{ts.Minutes} мин {ts.Seconds} с";

    /// <summary>
    /// Карточка показывается немодально, поэтому IsCancel сам окно не закрывает —
    /// нужен явный обработчик, иначе работает только системный крестик.
    /// </summary>
    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    /// <summary>
    /// Встречу переименовали или удалили. Список встреч в параметрах живёт своей жизнью
    /// и об этом не узнает: без события там осталась бы строка удалённой папки.
    /// </summary>
    public event Action? MeetingChanged;

    private void OnRenameClick(object sender, RoutedEventArgs e)
    {
        var dialog = new RenameWindow(MeetingActions.ReadTitle(_result.FolderPath)) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.Result is null) return;

        if (!MeetingActions.Rename(this, _result.FolderPath, dialog.Result)) return;

        HeaderText.Text = dialog.Result;
        MeetingChanged?.Invoke();
    }

    private void OnDeleteClick(object sender, RoutedEventArgs e)
    {
        if (!MeetingActions.Delete(this, _result.FolderPath)) return;

        MeetingChanged?.Invoke();
        Close();
    }

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
