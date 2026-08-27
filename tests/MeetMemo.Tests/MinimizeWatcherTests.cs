using MeetMemo.Capture;
using Xunit;

namespace MeetMemo.Tests;

/// <summary>
/// Подсказка про свёрнутое окно встречи: когда её показывать, а когда промолчать.
/// </summary>
public sealed class MinimizeWatcherTests
{
    private static MinimizeWatcher Watcher() => new(TimeSpan.FromMinutes(5));

    [Fact]
    public void Первое_сворачивание_попадает_в_подсказку()
    {
        Assert.True(Watcher().Update(minimized: true, nowMs: 1000));
    }

    [Fact]
    public void Пока_окно_свёрнуто_повторов_нет()
    {
        var watcher = Watcher();
        watcher.Update(true, 1000);

        Assert.Null(watcher.Update(true, 4000));
        Assert.Null(watcher.Update(true, 400_000));
    }

    [Fact]
    public void Возвращение_окна_подтверждается()
    {
        var watcher = Watcher();
        watcher.Update(true, 1000);

        Assert.False(watcher.Update(false, 9000));
    }

    [Fact]
    public void Частые_сворачивания_придерживаются()
    {
        var watcher = Watcher();

        Assert.True(watcher.Update(true, 0));
        Assert.False(watcher.Update(false, 10_000));

        // Второй раз в пределах паузы — молчим.
        Assert.Null(watcher.Update(true, 20_000));
    }

    /// <summary>
    /// Главная ловушка: если про сворачивание промолчали, то и про возвращение
    /// говорить не о чем — иначе выйдет ответ на вопрос, которого не задавали.
    /// </summary>
    [Fact]
    public void Промолчали_о_пропаже_молчим_и_о_возвращении()
    {
        var watcher = Watcher();

        watcher.Update(true, 0);
        watcher.Update(false, 10_000);
        watcher.Update(true, 20_000);   // придержано

        Assert.Null(watcher.Update(false, 30_000));
    }

    [Fact]
    public void После_паузы_подсказка_возвращается()
    {
        var watcher = Watcher();

        watcher.Update(true, 0);
        watcher.Update(false, 10_000);

        Assert.True(watcher.Update(true, 5 * 60_000));
    }

    [Fact]
    public void Новая_встреча_начинается_с_чистого_листа()
    {
        var watcher = Watcher();
        watcher.Update(true, 0);
        watcher.Reset();

        Assert.True(watcher.Update(true, 1000));
    }

    [Fact]
    public void Развёрнутое_окно_само_по_себе_ничего_не_сообщает()
    {
        Assert.Null(Watcher().Update(minimized: false, nowMs: 5000));
    }
}
