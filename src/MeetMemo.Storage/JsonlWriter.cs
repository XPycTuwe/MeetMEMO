using System.Text;
using System.Text.Json;

namespace MeetMemo.Storage;

/// <summary>
/// Потоковая запись JSONL с гарантией «не теряем больше N секунд» (ТЗ 11.3, 12.2).
/// Строка пишется целиком; сброс на диск идёт по таймеру, поэтому аварийное завершение
/// стоит не больше интервала flush. Оборванную последнюю строку читатель отбрасывает.
/// </summary>
public sealed class JsonlWriter : IAsyncDisposable
{
    private static readonly TimeSpan DefaultFlushInterval = TimeSpan.FromSeconds(5);

    private readonly FileStream _stream;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Timer _flushTimer;
    private readonly JsonSerializerOptions _options;
    private bool _dirty;
    private bool _disposed;

    public JsonlWriter(string path, TimeSpan? flushInterval = null, JsonSerializerOptions? options = null)
    {
        Path = path;
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        _options = options ?? JsonSetup.Compact;
        _stream = new FileStream(
            path,
            FileMode.Append,
            FileAccess.Write,
            FileShare.Read,
            bufferSize: 8192,
            FileOptions.SequentialScan);

        var interval = flushInterval ?? DefaultFlushInterval;
        _flushTimer = new Timer(_ => FlushIfDirty(), null, interval, interval);
    }

    public string Path { get; }

    public long Count { get; private set; }

    public async Task AppendAsync<T>(T record, CancellationToken ct = default)
    {
        var json = JsonSerializer.Serialize(record, _options);
        var bytes = Encoding.UTF8.GetBytes(json + "\n");

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_disposed) return;
            await _stream.WriteAsync(bytes, ct).ConfigureAwait(false);
            Count++;
            _dirty = true;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Принудительный сброс на диск — вызывается при паузе, остановке и по таймеру.</summary>
    public async Task FlushAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_disposed || !_dirty) return;
            await _stream.FlushAsync(ct).ConfigureAwait(false);
            _stream.Flush(flushToDisk: true);
            _dirty = false;
        }
        finally
        {
            _gate.Release();
        }
    }

    private void FlushIfDirty()
    {
        if (!_dirty || _disposed) return;
        try
        {
            FlushAsync().GetAwaiter().GetResult();
        }
        catch
        {
            // Таймерный сброс не должен ронять приложение; следующая попытка через интервал.
        }
    }

    public async ValueTask DisposeAsync()
    {
        // Повторное освобождение штатно: writer закрывается на финализации сессии,
        // а затем ещё раз при выгрузке хранилища.
        if (_disposed) return;

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed) return;
            _disposed = true;
            await _flushTimer.DisposeAsync().ConfigureAwait(false);
            await _stream.FlushAsync().ConfigureAwait(false);
            _stream.Flush(flushToDisk: true);
            await _stream.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
            _gate.Dispose();
        }
    }

    /// <summary>
    /// Толерантное чтение: последняя строка после аварии может быть обрезана — её отбрасываем,
    /// всё остальное возвращаем. Так восстановление даёт максимум уцелевших данных.
    /// </summary>
    public static IEnumerable<T> ReadAll<T>(string path, JsonSerializerOptions? options = null)
    {
        if (!File.Exists(path)) yield break;

        using var reader = new StreamReader(path, Encoding.UTF8);
        while (reader.ReadLine() is { } line)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;

            T? item;
            try
            {
                item = JsonSerializer.Deserialize<T>(line, options ?? JsonSetup.Compact);
            }
            catch (JsonException)
            {
                // Обрыв записи при kill — дальше по файлу читать нечего.
                yield break;
            }

            if (item is not null) yield return item;
        }
    }
}
