using MeetMemo.Asr;
using MeetMemo.Contracts;
using Xunit;
using static MeetMemo.Asr.SpeakerDiarizer;

namespace MeetMemo.Tests;

/// <summary>
/// Назначение голосов сегментам стенограммы. Сама диаризация — модельная и в юнитах
/// не проверяется; здесь логика сопоставления, где легко перепутать границы.
/// </summary>
public sealed class DiarizationTests
{
    private static TranscriptSegment Seg(long startMs, long endMs) => new()
    {
        StartMs = startMs,
        EndMs = endMs,
        Source = AudioChannel.Application,
        Text = "т"
    };

    [Fact]
    public void Побеждает_голос_с_наибольшим_перекрытием()
    {
        var voices = new List<VoiceSegment>
        {
            new(0, 4_000, Speaker: 0),
            new(4_000, 10_000, Speaker: 1)
        };

        // Сегмент 3–9 с: одну секунду накрывает первый голос, пять — второй.
        Assert.Equal("spk2", AssignSpeaker(Seg(3_000, 9_000), voices));
        Assert.Equal("spk1", AssignSpeaker(Seg(500, 3_500), voices));
    }

    [Fact]
    public void Перекрытие_одного_голоса_из_кусков_складывается()
    {
        // Голос 0 говорит дважды по краям, голос 1 — сплошным куском посередине.
        var voices = new List<VoiceSegment>
        {
            new(0, 3_000, Speaker: 0),
            new(3_000, 7_000, Speaker: 1),
            new(7_000, 10_000, Speaker: 0)
        };

        // Куски голоса 0 вместе (3 c + 3 c) перевешивают середину (4 c).
        Assert.Equal("spk1", AssignSpeaker(Seg(0, 10_000), voices));
    }

    [Fact]
    public void Без_перекрытия_метки_нет()
    {
        var voices = new List<VoiceSegment> { new(0, 1_000, Speaker: 0) };

        Assert.Null(AssignSpeaker(Seg(5_000, 8_000), voices));
    }

    [Fact]
    public void Ничтожное_перекрытие_не_считается_совпадением()
    {
        // Голос зацепил сегмент на 100 мс из десяти секунд — это шум на границе.
        var voices = new List<VoiceSegment> { new(0, 1_100, Speaker: 0) };

        Assert.Null(AssignSpeaker(Seg(1_000, 11_000), voices));
    }

    [Fact]
    public void Сегмент_нулевой_длины_остаётся_без_метки_и_без_деления_на_ноль()
    {
        var voices = new List<VoiceSegment> { new(0, 2_000, Speaker: 2) };

        // Нулевая длительность — вырожденный сегмент: перекрытия нет, значит и метки нет.
        Assert.Null(AssignSpeaker(Seg(1_000, 1_000), voices));
    }

    [Fact]
    public void Поле_speaker_сериализуется_в_snake_case_и_не_ломает_схему()
    {
        var json = System.Text.Json.JsonSerializer.Serialize(
            Seg(0, 1_000) with { Speaker = "spk2" },
            MeetMemo.Storage.JsonSetup.Compact);

        Assert.Contains("\"speaker\":\"spk2\"", json);
        Assert.Contains("\"source\":\"application\"", json);
    }
}
