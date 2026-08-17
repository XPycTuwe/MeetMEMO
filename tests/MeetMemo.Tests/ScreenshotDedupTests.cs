using System.Drawing;
using System.Text.Json;
using MeetMemo.Capture;
using MeetMemo.Contracts;
using MeetMemo.Storage;
using Xunit;

namespace MeetMemo.Tests;

/// <summary>
/// Склейка одинаковых снимков. Проверяем не только «дубли ушли», но и то, что моменты
/// повторов сохранились и что ручные снимки не тронуты.
/// </summary>
public sealed class ScreenshotDedupTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"mm-dedup-{Guid.NewGuid():N}");

    private string MakeShot(string name, Color fill, long offsetMs, bool manual = false)
    {
        var folder = new MeetingFolder(_root);
        Directory.CreateDirectory(folder.ScreenshotsDir);

        using (var bitmap = new Bitmap(64, 64))
        {
            using var g = Graphics.FromImage(bitmap);
            g.Clear(fill);
            // Немного «шума», как курсор поверх слайда: кадры одного слайда не совпадают побайтно.
            g.FillRectangle(Brushes.Gray, (int)(offsetMs % 20), 2, 3, 3);
            bitmap.Save(Path.Combine(folder.ScreenshotsDir, name), System.Drawing.Imaging.ImageFormat.Png);
        }

        _entries.Add(new ScreenshotEntry
        {
            File = $"screenshots/{name}",
            OffsetMs = offsetMs,
            TimestampLocal = DateTimeOffset.Now,
            Type = manual ? ScreenshotKind.ApplicationManual : ScreenshotKind.ApplicationAuto,
            Manual = manual,
            Width = 64,
            Height = 64
        });

        return name;
    }

    private readonly List<ScreenshotEntry> _entries = new();

    private void WriteIndex()
    {
        var folder = new MeetingFolder(_root);
        File.WriteAllText(folder.ScreenshotIndex, JsonSerializer.Serialize(
            new ScreenshotIndex { Items = _entries }, JsonSetup.Pretty));
    }

    [Fact]
    public void Повторы_слайда_схлопываются_в_один_файл_с_отметками_времени()
    {
        MakeShot("a.png", Color.White, 1_000);
        MakeShot("b.png", Color.White, 5_000);     // тот же слайд
        MakeShot("c.png", Color.DarkBlue, 9_000);  // другой слайд
        MakeShot("d.png", Color.White, 30_000);    // снова первый
        WriteIndex();

        var result = ScreenshotDeduplicator.Deduplicate(_root);

        Assert.Equal(2, result.Removed);
        Assert.True(result.FreedBytes > 0);

        var folder = new MeetingFolder(_root);
        var updated = JsonSerializer.Deserialize<ScreenshotIndex>(
            File.ReadAllText(folder.ScreenshotIndex), JsonSetup.Compact)!.Items;

        Assert.Equal(2, updated.Count);

        var slide = updated.Single(e => e.File.EndsWith("a.png"));
        Assert.Equal(new long[] { 5_000, 30_000 }, slide.AlsoShownAtMs!);

        // Файлы дублей действительно убраны с диска.
        Assert.False(File.Exists(Path.Combine(folder.ScreenshotsDir, "b.png")));
        Assert.True(File.Exists(Path.Combine(folder.ScreenshotsDir, "a.png")));
    }

    [Fact]
    public void Ручной_снимок_не_склеивается_даже_если_кадр_тот_же()
    {
        MakeShot("auto.png", Color.White, 1_000);
        MakeShot("manual.png", Color.White, 4_000, manual: true);
        WriteIndex();

        var result = ScreenshotDeduplicator.Deduplicate(_root);

        // Нажатие кнопки — осознанное действие: этот кадр важен сам по себе.
        Assert.Equal(0, result.Removed);
        Assert.True(File.Exists(Path.Combine(new MeetingFolder(_root).ScreenshotsDir, "manual.png")));
    }

    [Fact]
    public void Разные_слайды_остаются_каждый_своим_файлом()
    {
        MakeShot("one.png", Color.White, 1_000);
        MakeShot("two.png", Color.DarkRed, 4_000);
        MakeShot("three.png", Color.DarkGreen, 8_000);
        WriteIndex();

        var result = ScreenshotDeduplicator.Deduplicate(_root);

        Assert.Equal(0, result.Removed);
        Assert.Equal(3, result.Kept);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
    }
}
