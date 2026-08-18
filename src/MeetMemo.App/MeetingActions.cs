using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using MeetMemo.Export;
using MeetMemo.Storage;

namespace MeetMemo.App;

/// <summary>
/// Действия над записанной встречей: переименовать, собрать архив, удалить.
///
/// Живут отдельно, потому что вызываются из двух мест сразу — из списка встреч
/// в параметрах и из карточки самой встречи. Раздвоение таких операций рано или поздно
/// приводит к тому, что в одном месте что-то чинят, а в другом забывают.
/// </summary>
public static class MeetingActions
{
    /// <summary>
    /// Переименовывает встречу: и папку, и название внутри session.json. Одной папки мало —
    /// в мемо попадает именно название из манифеста, и оно бы осталось прежним.
    /// Возвращает новый путь либо null, если переименовать не удалось.
    /// </summary>
    public static string? Rename(Window owner, string folderPath, string newTitle)
    {
        var title = newTitle.Trim();
        if (title.Length == 0) return null;

        try
        {
            UpdateManifestTitle(folderPath, title);

            var parent = Path.GetDirectoryName(folderPath)!;
            var target = Path.Combine(parent, Sanitize(title));

            // Такое же имя — переименовывать нечего, но название в манифесте уже обновлено.
            if (string.Equals(target, folderPath, StringComparison.OrdinalIgnoreCase))
                return folderPath;

            // Занятое имя не перетираем: там чужая встреча.
            if (Directory.Exists(target))
            {
                MessageBox.Show(owner,
                    $"Папка «{Path.GetFileName(target)}» уже есть. Выберите другое название.",
                    "MeetMemo", MessageBoxButton.OK, MessageBoxImage.Warning);
                return null;
            }

            Directory.Move(folderPath, target);
            return target;
        }
        catch (Exception ex)
        {
            MessageBox.Show(owner, $"Не удалось переименовать: {ex.Message}",
                "MeetMemo", MessageBoxButton.OK, MessageBoxImage.Error);
            return null;
        }
    }

    /// <summary>
    /// Удаляет папку встречи вместе со всем содержимым. Спрашивает подтверждение и
    /// показывает, что именно исчезнет: это запись разговора, восстановить её неоткуда.
    /// </summary>
    public static bool Delete(Window owner, string folderPath)
    {
        var name = Path.GetFileName(folderPath);
        var size = FormatSize(DirectorySize(folderPath));

        var answer = MessageBox.Show(
            owner,
            $"Удалить встречу «{name}»?\n\nИсчезнут стенограмма, снимки и запись звука ({size}).\n"
            + "Восстановить их будет неоткуда.",
            "MeetMemo", MessageBoxButton.YesNo, MessageBoxImage.Warning);

        if (answer != MessageBoxResult.Yes) return false;

        try
        {
            Directory.Delete(folderPath, recursive: true);
            return true;
        }
        catch (Exception ex)
        {
            MessageBox.Show(owner, $"Не удалось удалить: {ex.Message}",
                "MeetMemo", MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
    }

    /// <summary>Собирает ZIP рядом с папкой встречи и показывает результат.</summary>
    public static async Task<FileInfo?> BuildZipAsync(
        Window owner, string folderPath, bool includeAudio)
    {
        try
        {
            var plan = ExportPlanBuilder.Build(folderPath, includeAudio);
            var archivePath = Path.Combine(
                Path.GetDirectoryName(folderPath)!,
                ZipPackager.SuggestArchiveName(folderPath));

            var file = await new ZipPackager().CreateAsync(plan, archivePath);

            var answer = MessageBox.Show(
                owner,
                $"Архив собран:\n{file.FullName}\n\nРазмер: {ExportPlan.FormatSize(file.Length)}\n\n"
                + "Открыть папку с архивом?",
                "MeetMemo", MessageBoxButton.YesNo, MessageBoxImage.Information);

            if (answer == MessageBoxResult.Yes)
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                    "explorer.exe", $"/select,\"{file.FullName}\"") { UseShellExecute = true });
            }

            return file;
        }
        catch (Exception ex)
        {
            MessageBox.Show(owner, $"Не удалось собрать архив: {ex.Message}",
                "MeetMemo", MessageBoxButton.OK, MessageBoxImage.Error);
            return null;
        }
    }

    /// <summary>Название встречи из манифеста — его и показываем при переименовании.</summary>
    public static string ReadTitle(string folderPath)
    {
        try
        {
            var folder = new MeetingFolder(folderPath);
            if (!File.Exists(folder.SessionJson)) return Path.GetFileName(folderPath);

            var node = JsonNode.Parse(File.ReadAllText(folder.SessionJson));
            return node?["title"]?.GetValue<string>() ?? Path.GetFileName(folderPath);
        }
        catch (Exception)
        {
            return Path.GetFileName(folderPath);
        }
    }

    /// <summary>
    /// Правим только поле title, а не пересобираем манифест целиком: в нём есть поля,
    /// которых наша модель может не знать, и терять их при переименовании нельзя.
    /// </summary>
    private static void UpdateManifestTitle(string folderPath, string title)
    {
        var folder = new MeetingFolder(folderPath);
        if (!File.Exists(folder.SessionJson)) return;

        var node = JsonNode.Parse(File.ReadAllText(folder.SessionJson));
        if (node is null) return;

        node["title"] = title;

        File.WriteAllText(folder.SessionJson,
            node.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>Убирает из названия то, что Windows не пустит в имя папки.</summary>
    private static string Sanitize(string title)
    {
        var clean = new string(title
            .Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)
            .ToArray())
            .Trim();

        // Имя не должно кончаться точкой или пробелом — проводник такие папки не открывает.
        clean = clean.TrimEnd('.', ' ');

        return clean.Length > 0 ? clean : "Встреча";
    }

    private static long DirectorySize(string path)
    {
        try
        {
            return new DirectoryInfo(path)
                .EnumerateFiles("*", SearchOption.AllDirectories)
                .Sum(f => f.Length);
        }
        catch (Exception)
        {
            return 0;
        }
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        >= 1024L * 1024 * 1024 => $"{bytes / 1024.0 / 1024 / 1024:0.#} ГБ",
        >= 1024 * 1024 => $"{bytes / 1024.0 / 1024:0.#} МБ",
        >= 1024 => $"{bytes / 1024.0:0.#} КБ",
        _ => $"{bytes} Б"
    };
}
