using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Windows.Automation;

namespace MeetMemo.Capture;

/// <summary>
/// Чтение текста из окна встречи через дерево доступности — то самое, что читают
/// программы экранного доступа.
///
/// Зачем: в самом окне звонка есть то, чего нет в звуке, — имена участников в списке,
/// сообщения чата, тема встречи. Диаризация различает голоса, но назвать их не может;
/// имя приходит отсюда.
///
/// Почему не разбор картинки и не вёрстка: дерево доступности — публичный контракт,
/// его держат стабильным ради незрячих пользователей. Вёрстка же меняется с каждым
/// обновлением приложения.
///
/// Подвох, ради которого всё это написано: Chromium (Chrome, Edge, Яндекс Браузер,
/// Electron-приложения) строит дерево лениво — пока никто не представился вспомогательной
/// технологией, снаружи видно полтора десятка пустых панелей. Сообщение WM_GETOBJECT
/// с OBJID_CLIENT — штатный сигнал «я такой клиент»; после него дерево появляется.
/// Замерено на живом окне: 14 элементов и ноль документов до сигнала, 502 элемента
/// и весь текст страницы после.
/// </summary>
[SupportedOSPlatform("windows")]
public static class WindowTextReader
{
    private const uint WM_GETOBJECT = 0x003D;
    private static readonly IntPtr ObjIdClient = new(unchecked((int)0xFFFFFFFC));

    /// <summary>Длиннее — уже не подпись в интерфейсе, а статья: такое в контекст не берём.</summary>
    private const int MaxFragmentLength = 300;

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessageTimeoutW(
        IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam,
        uint flags, uint timeoutMs, out IntPtr result);

    /// <summary>
    /// Просит окно включить дерево доступности. Вызов с таймаутом: подвисшее окно встречи
    /// не должно утянуть за собой поток захвата.
    /// </summary>
    public static void WakeAccessibility(nint window)
    {
        const uint SMTO_ABORTIFHUNG = 0x0002;
        SendMessageTimeoutW(window, WM_GETOBJECT, IntPtr.Zero, ObjIdClient,
            SMTO_ABORTIFHUNG, 500, out _);
    }

    /// <summary>
    /// Собирает видимые текстовые фрагменты окна: имена в списке участников, сообщения
    /// чата, заголовки. Порядок сохраняется — он несёт смысл (кто за кем в списке).
    /// Пустая выдача — обычное дело: у приложений не на Chromium дерево может быть скудным.
    /// </summary>
    public static IReadOnlyList<string> ReadVisibleText(nint window, int limit = 200)
    {
        try
        {
            WakeAccessibility(window);

            var element = AutomationElement.FromHandle(window);
            if (element is null) return Array.Empty<string>();

            var texts = element.FindAll(TreeScope.Descendants, Condition.TrueCondition);

            var result = new List<string>(Math.Min(limit, texts.Count));
            var seen = new HashSet<string>(StringComparer.Ordinal);

            foreach (AutomationElement item in texts)
            {
                if (result.Count >= limit) break;

                string name;
                try { name = item.Current.Name; }
                catch (ElementNotAvailableException) { continue; }

                if (string.IsNullOrWhiteSpace(name)) continue;

                name = name.Trim();
                if (name.Length < 2 || name.Length > MaxFragmentLength) continue;

                // Один и тот же текст приходит и от контейнера, и от вложенной надписи.
                if (!seen.Add(name)) continue;

                result.Add(name);
            }

            return result;
        }
        catch (Exception)
        {
            // Окно могло закрыться прямо во время обхода — для контекста это не потеря.
            return Array.Empty<string>();
        }
    }
}
