using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.Versioning;
using MeetMemo.Contracts;
using MeetMemo.Core;
using MeetMemo.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace MeetMemo.Capture;

/// <summary>
/// Хранилище снимков: PNG в папке встречи плюс index.json с метаданными (ТЗ 9.3).
/// Индекс переписывается атомарно после каждого снимка — потребитель пакета всегда
/// видит согласованный файл, даже если приложение аварийно завершится.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ScreenshotStore
{
    private readonly MeetingFolder _folder;
    private readonly ISessionClock _clock;
    private readonly ILogger _log;
    private readonly List<ScreenshotEntry> _entries = new();
    private readonly SemaphoreSlim _gate = new(1, 1);

    public ScreenshotStore(MeetingFolder folder, ISessionClock clock, ILogger? log = null)
    {
        _folder = folder;
        _clock = clock;
        _log = log ?? NullLogger.Instance;
    }

    public int Count
    {
        get { lock (_entries) return _entries.Count; }
    }

    public async Task<ScreenshotEntry?> SaveAsync(
        Bitmap bitmap,
        ScreenshotKind kind,
        string trigger,
        string? application = null,
        string? windowTitle = null,
        string? monitor = null,
        CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(_folder.ScreenshotsDir);

            var offset = _clock.ElapsedMs;
            var prefix = kind == ScreenshotKind.DesktopManual ? "desktop" : "app";
            var stamp = TimeSpan.FromMilliseconds(offset).ToString(@"hh\-mm\-ss");
            var fileName = $"{prefix}_{stamp}.png";
            var path = Path.Combine(_folder.ScreenshotsDir, fileName);

            // Одинаковые имена возможны при нескольких снимках в одну секунду.
            var suffix = 2;
            while (File.Exists(path))
            {
                fileName = $"{prefix}_{stamp}-{suffix}.png";
                path = Path.Combine(_folder.ScreenshotsDir, fileName);
                suffix++;
            }

            bitmap.Save(path, ImageFormat.Png);

            var entry = new ScreenshotEntry
            {
                File = $"screenshots/{fileName}",
                OffsetMs = offset,
                TimestampLocal = _clock.ToLocal(offset),
                Type = kind,
                Manual = kind != ScreenshotKind.ApplicationAuto,
                Important = kind == ScreenshotKind.Important,
                Trigger = trigger,
                Application = application,
                WindowTitle = windowTitle,
                Monitor = monitor,
                Width = bitmap.Width,
                Height = bitmap.Height
            };

            lock (_entries) _entries.Add(entry);
            await WriteIndexAsync(ct).ConfigureAwait(false);

            _log.LogInformation("Снимок сохранён: {File} ({Kind})", fileName, kind);
            return entry;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Не удалось сохранить снимок");
            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task WriteIndexAsync(CancellationToken ct)
    {
        ScreenshotIndex index;
        lock (_entries)
        {
            index = new ScreenshotIndex { Items = _entries.ToArray() };
        }

        await AtomicJsonStore.WriteAsync(_folder.ScreenshotIndex, index, JsonSetup.Pretty, ct)
            .ConfigureAwait(false);
    }

    public async Task FlushAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try { await WriteIndexAsync(ct).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }
}
