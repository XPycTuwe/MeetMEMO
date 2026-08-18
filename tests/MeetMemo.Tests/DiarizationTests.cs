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

    // ===== Короткие реплики: что поглощать, а что беречь =====

    [Fact]
    public void Пауза_посреди_речи_не_создаёт_нового_собеседника()
    {
        // Человек говорил, запнулся, продолжил — кластеризация завела на паузу
        // отдельный голос, хотя говорил всё время один.
        var voices = new List<VoiceSegment>
        {
            new(0, 20_000, Speaker: 0),
            new(20_000, 21_500, Speaker: 7),
            new(21_500, 40_000, Speaker: 0)
        };

        var result = SpeakerDiarizer.AbsorbShortPieces(voices);

        Assert.Equal(1, result.Select(v => v.Speaker).Distinct().Count());
        Assert.All(result, v => Assert.Equal(0, v.Speaker));
    }

    [Fact]
    public void Короткое_да_на_вопрос_остаётся_отдельной_репликой()
    {
        // «Паша, возьмёшься сделать?» — «Да». Единственная реплика человека за встречу
        // и самая важная: это согласие на поручение.
        var voices = new List<VoiceSegment>
        {
            new(0, 8_000, Speaker: 0),          // вопрос задаёт первый
            new(8_000, 9_000, Speaker: 3),      // Паша отвечает «да»
            new(9_000, 30_000, Speaker: 1)      // дальше говорит уже третий
        };

        var result = SpeakerDiarizer.AbsorbShortPieces(voices);

        // Соседи разные — реплику не присваиваем никому другому.
        Assert.Contains(result, v => v.Speaker == 3 && v.EndMs - v.StartMs == 1_000);
        Assert.Equal(3, result.Select(v => v.Speaker).Distinct().Count());
    }

    [Fact]
    public void Длинная_реплика_между_чужими_не_поглощается()
    {
        var voices = new List<VoiceSegment>
        {
            new(0, 10_000, Speaker: 0),
            new(10_000, 25_000, Speaker: 5),   // пятнадцать секунд — точно свой голос
            new(25_000, 40_000, Speaker: 0)
        };

        var result = SpeakerDiarizer.AbsorbShortPieces(voices);

        Assert.Contains(result, v => v.Speaker == 5);
    }

    [Fact]
    public void Короткая_реплика_в_самом_конце_никуда_не_девается()
    {
        // У последнего сегмента нет соседа справа — поглощать не с чем.
        var voices = new List<VoiceSegment>
        {
            new(0, 30_000, Speaker: 0),
            new(30_000, 31_000, Speaker: 2)
        };

        var result = SpeakerDiarizer.AbsorbShortPieces(voices);

        Assert.Equal(2, result.Count);
        Assert.Contains(result, v => v.Speaker == 2);
    }
}
