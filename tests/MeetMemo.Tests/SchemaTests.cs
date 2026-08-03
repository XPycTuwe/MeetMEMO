using System.Text.Json;
using MeetMemo.Contracts;
using MeetMemo.Storage;
using Xunit;

namespace MeetMemo.Tests;

/// <summary>
/// Формат пакета — публичный контракт: его читают Claude Skill и любые сторонние инструменты.
/// Эти тесты фиксируют вид значений, чтобы он не поехал при рефакторинге.
/// </summary>
public class SchemaTests
{
    [Fact]
    public void Источник_сегмента_пишется_в_нижнем_регистре()
    {
        var segment = new TranscriptSegment
        {
            StartMs = 872400,
            EndMs = 879100,
            Source = AudioChannel.Application,
            Text = "необходимо пересчитать пропускную способность"
        };

        var json = JsonSerializer.Serialize(segment, JsonSetup.Compact);

        Assert.Contains("\"source\":\"application\"", json);
        Assert.DoesNotContain("\"Application\"", json);
    }

    [Fact]
    public void Статус_и_режим_звука_пишутся_через_подчёркивание()
    {
        var manifest = new SessionManifest
        {
            SessionId = "x",
            Title = "Встреча",
            StartUtc = DateTimeOffset.UtcNow,
            StartLocal = DateTimeOffset.Now,
            Timezone = "Asia/Yekaterinburg",
            Status = SessionStatus.CompletedWithWarnings,
            Audio = new AudioInfo { Mode = AudioMode.ApplicationProcessTree }
        };

        var json = JsonSerializer.Serialize(manifest, JsonSetup.Compact);

        Assert.Contains("\"status\":\"completed_with_warnings\"", json);
        Assert.Contains("\"mode\":\"application_process_tree\"", json);
    }

    [Fact]
    public void Тип_снимка_пишется_в_нижнем_регистре()
    {
        var entry = new ScreenshotEntry
        {
            File = "screenshots/app_00-14-35.png",
            OffsetMs = 875000,
            TimestampLocal = DateTimeOffset.Now,
            Type = ScreenshotKind.ApplicationAuto
        };

        var json = JsonSerializer.Serialize(entry, JsonSetup.Compact);

        Assert.Contains("\"type\":\"application_auto\"", json);
    }

    [Fact]
    public void Значения_читаются_обратно_без_потерь()
    {
        var original = new TranscriptSegment
        {
            StartMs = 1, EndMs = 2, Source = AudioChannel.Microphone, Text = "текст"
        };

        var json = JsonSerializer.Serialize(original, JsonSetup.Compact);
        var restored = JsonSerializer.Deserialize<TranscriptSegment>(json, JsonSetup.Compact);

        Assert.NotNull(restored);
        Assert.Equal(AudioChannel.Microphone, restored!.Source);
    }

    [Fact]
    public void Схема_имеет_версию()
    {
        var manifest = new SessionManifest
        {
            SessionId = "x",
            Title = "t",
            StartUtc = DateTimeOffset.UtcNow,
            StartLocal = DateTimeOffset.Now,
            Timezone = "UTC"
        };

        var json = JsonSerializer.Serialize(manifest, JsonSetup.Compact);
        Assert.Contains("\"schema_version\":\"1.0\"", json);
    }
}
