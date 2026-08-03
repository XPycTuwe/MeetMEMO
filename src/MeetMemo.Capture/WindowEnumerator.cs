using System.Diagnostics;
using MeetMemo.Capture.Interop;

namespace MeetMemo.Capture;

/// <summary>Окно-кандидат для выбора источника встречи.</summary>
public sealed record WindowCandidate
{
    public required nint Handle { get; init; }

    public required string Title { get; init; }

    public required int ProcessId { get; init; }

    public string? ProcessName { get; init; }

    public string? ExecutablePath { get; init; }

    /// <summary>
    /// Понятное название приложения из свойств исполняемого файла. Имя процесса часто
    /// ничего не говорит пользователю: у Яндекс Браузера это «browser», у Outlook — «olk».
    /// </summary>
    public string? FriendlyName { get; init; }

    /// <summary>Что показывать в колонке «Приложение».</summary>
    public string AppLabel => FriendlyName ?? ProcessName ?? "—";

    public bool IsMinimized { get; init; }

    public int Width { get; init; }

    public int Height { get; init; }

    /// <summary>Приложение опознано как средство видеосвязи — такие показываем первыми.</summary>
    public bool IsKnownMeetingApp { get; init; }

    public string DisplayName => string.IsNullOrWhiteSpace(Title)
        ? ProcessName ?? "Без названия"
        : Title;
}

/// <summary>
/// Перечисление пользовательских окон для окна выбора источника (ТЗ 6.1).
/// Показываем только то, что пользователь реально может выбрать: видимые окна верхнего
/// уровня с заголовком, не скрытые DWM и не служебные.
/// </summary>
public static class WindowEnumerator
{
    private static readonly string[] MeetingApps =
    [
        "teams", "ms-teams", "zoom", "telegram", "discord", "slack",
        "skype", "webex", "chrome", "msedge", "firefox", "yandex"
    ];

    public static IReadOnlyList<WindowCandidate> Enumerate()
    {
        var result = new List<WindowCandidate>();
        var shell = Win32.GetShellWindow();
        var self = Environment.ProcessId;

        // Делегат обязан жить в переменной и дожить до конца вызова: если передать его
        // напрямую, сборщик мусора вправе освободить его прямо во время перечисления —
        // тогда обход обрывается и список окон молча оказывается пустым.
        var seen = 0;

        var callback = new Win32.EnumWindowsProc((hWnd, _) =>
        {
            seen++;
            try
            {
                if (hWnd == shell) return true;
                if (!Win32.IsWindowVisible(hWnd)) return true;
                if (Win32.GetAncestor(hWnd, Win32.GA_ROOT) != hWnd) return true;
                if (Win32.IsCloaked(hWnd)) return true;

                var exStyle = Win32.GetWindowLongW(hWnd, Win32.GWL_EXSTYLE);
                // Служебные окна (панели инструментов, всплывающие подсказки) пользователю не нужны.
                if ((exStyle & Win32.WS_EX_TOOLWINDOW) != 0
                    && (exStyle & Win32.WS_EX_APPWINDOW) == 0) return true;

                var title = Win32.GetWindowTitle(hWnd);
                if (string.IsNullOrWhiteSpace(title)) return true;

                Win32.GetWindowThreadProcessId(hWnd, out var pid);
                if (pid == 0 || pid == self) return true;

                string? processName = null;
                string? exePath = null;
                try
                {
                    using var process = Process.GetProcessById((int)pid);
                    processName = process.ProcessName;
                    try { exePath = process.MainModule?.FileName; }
                    catch (Exception) { /* доступ к модулю может быть закрыт */ }
                }
                catch (ArgumentException)
                {
                    return true; // процесс уже завершился
                }

                // У свёрнутого окна система отдаёт размер 276x45 в координатах -32000,
                // и фильтр по размеру выбрасывал такие окна из списка. Берём размер,
                // который окно займёт после разворачивания.
                var rect = Win32.GetLogicalBounds(hWnd);
                if (rect.Width < 100 || rect.Height < 100) return true;

                result.Add(new WindowCandidate
                {
                    Handle = hWnd,
                    Title = title,
                    ProcessId = (int)pid,
                    ProcessName = processName,
                    ExecutablePath = exePath,
                    FriendlyName = GetFriendlyName(exePath),
                    IsMinimized = Win32.IsIconic(hWnd),
                    Width = rect.Width,
                    Height = rect.Height,
                    IsKnownMeetingApp = processName is not null
                        && MeetingApps.Any(a => processName.Contains(a, StringComparison.OrdinalIgnoreCase))
                });
            }
            catch (Exception)
            {
                // Одно проблемное окно не должно ломать весь список.
            }

            return true;
        });

        Win32.EnumWindows(callback, IntPtr.Zero);
        GC.KeepAlive(callback);
        LastSeenWindowCount = seen;

        return result
            .OrderByDescending(w => w.IsKnownMeetingApp)
            .ThenBy(w => w.ProcessName)
            .ThenBy(w => w.Title)
            .ToList();
    }

    /// <summary>Окно ещё существует и пригодно для захвата.</summary>
    public static bool IsAlive(nint handle) => handle != 0 && Win32.IsWindow(handle);

    private static readonly Dictionary<string, string?> FriendlyNameCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Название приложения из свойств exe. Результат кэшируется: чтение свойств файла
    /// заметно дороже остальных проверок, а список окон перестраивается часто.
    /// </summary>
    private static string? GetFriendlyName(string? exePath)
    {
        if (string.IsNullOrEmpty(exePath)) return null;

        lock (FriendlyNameCache)
        {
            if (FriendlyNameCache.TryGetValue(exePath, out var cached)) return cached;
        }

        string? name = null;
        try
        {
            var info = FileVersionInfo.GetVersionInfo(exePath);

            // FileDescription обычно и есть человеческое название («Yandex Browser»),
            // но у части приложений он пуст — тогда берём название продукта.
            name = FirstMeaningful(info.FileDescription, info.ProductName);
        }
        catch (Exception)
        {
            // Свойства могут быть недоступны (нет прав, файл на сетевом диске) —
            // тогда просто останется имя процесса.
        }

        lock (FriendlyNameCache)
        {
            FriendlyNameCache[exePath] = name;
        }

        return name;
    }

    private static string? FirstMeaningful(params string?[] candidates)
    {
        foreach (var candidate in candidates)
        {
            var trimmed = candidate?.Trim();
            if (!string.IsNullOrEmpty(trimmed)) return trimmed;
        }

        return null;
    }

    /// <summary>
    /// Сколько окон система вообще показала при последнем обходе. Если список пуст,
    /// это число отличает «нечего показывать» от сбоя перечисления.
    /// </summary>
    public static int LastSeenWindowCount { get; private set; }
}
