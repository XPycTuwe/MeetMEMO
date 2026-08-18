using MeetMemo.Asr;
using Xunit;

namespace MeetMemo.Tests;

/// <summary>
/// Инициалы для карточки говорящего: когда фото нет, в кружке рисуются буквы,
/// и они должны получаться из любого написания имени.
/// </summary>
public sealed class SpeakerCardTests
{
    [Theory]
    [InlineData("Елена Петрова", "ЕП")]
    [InlineData("елена петрова смирнова", "ЕП")]   // отчество и третье слово не берём
    [InlineData("Андрей", "А")]                    // одно слово — одна буква
    [InlineData("  Пётр   Иванов  ", "ПИ")]        // лишние пробелы не мешают
    public void Инициалы_берутся_из_первых_двух_слов(string name, string expected) =>
        Assert.Equal(expected, VoicePrint.MakeInitials(name));

    [Fact]
    public void Пустое_имя_не_роняет_карточку() =>
        Assert.Equal("?", VoicePrint.MakeInitials("   "));
}
