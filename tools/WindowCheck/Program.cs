using System.Diagnostics;
using MeetMemo.Capture;
using MeetMemo.Capture.Interop;

// Диагностика списка окон: показывает, сколько окон отсеивает каждый фильтр.
// Пустой список в окне выбора источника — симптом, а не причина; здесь видно причину.

Console.OutputEncoding = System.Text.Encoding.UTF8;

var result = WindowEnumerator.Enumerate();
Console.WriteLine($"WindowEnumerator вернул: {result.Count} окон");
foreach (var w in result.Take(20))
    Console.WriteLine($"  {w.AppLabel,-26} ({w.ProcessName})  {w.Title}");

Console.WriteLine();
Console.WriteLine("=== разбор по фильтрам ===");

int total = 0, notVisible = 0, notRoot = 0, cloaked = 0, toolWindow = 0,
    noTitle = 0, badPid = 0, noProcess = 0, tooSmall = 0, passed = 0, threw = 0;

var shell = Win32.GetShellWindow();
var self = Environment.ProcessId;

Win32.EnumWindows((hWnd, _) =>
{
    total++;
    try
    {
        if (hWnd == shell) return true;
        if (!Win32.IsWindowVisible(hWnd)) { notVisible++; return true; }
        if (Win32.GetAncestor(hWnd, Win32.GA_ROOT) != hWnd) { notRoot++; return true; }
        if (Win32.IsCloaked(hWnd)) { cloaked++; return true; }

        var exStyle = Win32.GetWindowLongW(hWnd, Win32.GWL_EXSTYLE);
        if ((exStyle & Win32.WS_EX_TOOLWINDOW) != 0 && (exStyle & Win32.WS_EX_APPWINDOW) == 0)
        { toolWindow++; return true; }

        var title = Win32.GetWindowTitle(hWnd);
        if (string.IsNullOrWhiteSpace(title)) { noTitle++; return true; }

        Win32.GetWindowThreadProcessId(hWnd, out var pid);
        if (pid == 0 || pid == self) { badPid++; return true; }

        try
        {
            using var p = Process.GetProcessById((int)pid);
            var name = p.ProcessName;
        }
        catch (Exception ex)
        {
            noProcess++;
            Console.WriteLine($"  [процесс {pid}] {ex.GetType().Name}: {ex.Message}  ← {title}");
            return true;
        }

        Win32.GetWindowRect(hWnd, out var rect);
        if (rect.Width < 100 || rect.Height < 100) { tooSmall++; return true; }

        passed++;
    }
    catch (Exception ex)
    {
        threw++;
        Console.WriteLine($"  ИСКЛЮЧЕНИЕ: {ex.GetType().Name}: {ex.Message}");
    }

    return true;
}, IntPtr.Zero);

Console.WriteLine($"  всего окон обошли:        {total}");
Console.WriteLine($"  невидимые:                {notVisible}");
Console.WriteLine($"  не корневые:              {notRoot}");
Console.WriteLine($"  скрытые DWM (cloaked):    {cloaked}");
Console.WriteLine($"  служебные (toolwindow):   {toolWindow}");
Console.WriteLine($"  без заголовка:            {noTitle}");
Console.WriteLine($"  свой процесс/нет PID:     {badPid}");
Console.WriteLine($"  процесс недоступен:       {noProcess}");
Console.WriteLine($"  слишком маленькие:        {tooSmall}");
Console.WriteLine($"  ИСКЛЮЧЕНИЙ:               {threw}");
Console.WriteLine($"  ПРОШЛИ ФИЛЬТРЫ:           {passed}");

return result.Count > 0 ? 0 : 1;
