using MeetMemo.Contracts;
using Xunit;

namespace MeetMemo.Tests;

/// <summary>
/// Условие перехода на общий системный звук, когда дорожка приложения молчит.
///
/// Изоляция по дереву процессов удаётся не со всяким приложением: Teams из Microsoft Store
/// играет мимо своего дерева, захват формально успешен, а в дорожке тишина. Здесь
/// проверяется само правило — отличить «на встрече пауза» от «мы слушаем не то».
/// </summary>
public sealed class DeadChannelTests
{
    private const double DeadAfterMs = 30_000;

    /// <summary>Повторяет решение движка: переключаться или ещё подождать.</summary>
    private static bool ShouldSwitch(bool everHadSound, AudioMode mode, long offsetMs, bool handled) =>
        !everHadSound
        && !handled
        && mode == AudioMode.ApplicationProcessTree
        && offsetMs >= DeadAfterMs;

    [Fact]
    public void Молчащая_с_начала_дорожка_переключается_через_полминуты()
    {
        Assert.True(ShouldSwitch(everHadSound: false, AudioMode.ApplicationProcessTree,
            offsetMs: 30_000, handled: false));
    }

    [Fact]
    public void Пауза_на_встрече_не_повод_переключаться()
    {
        // Звук был — значит слушаем правильный процесс, а сейчас просто молчат.
        Assert.False(ShouldSwitch(everHadSound: true, AudioMode.ApplicationProcessTree,
            offsetMs: 120_000, handled: false));
    }

    [Fact]
    public void Первые_секунды_ждём_не_переключаясь()
    {
        // В начале встречи тишина обычна: люди подключаются.
        Assert.False(ShouldSwitch(everHadSound: false, AudioMode.ApplicationProcessTree,
            offsetMs: 12_000, handled: false));
    }

    [Fact]
    public void Переключаемся_только_один_раз()
    {
        Assert.False(ShouldSwitch(everHadSound: false, AudioMode.ApplicationProcessTree,
            offsetMs: 90_000, handled: true));
    }

    [Fact]
    public void В_режиме_общего_звука_переключать_некуда()
    {
        Assert.False(ShouldSwitch(everHadSound: false, AudioMode.System,
            offsetMs: 60_000, handled: false));

        // И в режиме одного микрофона дорожки приложения нет вовсе.
        Assert.False(ShouldSwitch(everHadSound: false, AudioMode.MicrophoneOnly,
            offsetMs: 60_000, handled: false));
    }
}
