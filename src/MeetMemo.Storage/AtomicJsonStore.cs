using System.Text;
using System.Text.Json;

namespace MeetMemo.Storage;

/// <summary>
/// Атомарная запись JSON: временный файл + переименование (ТЗ 12.2). Половинчатого session.json
/// не бывает даже при выдёргивании питания. Ретраи с паузой — потому что антивирус и клиенты
/// облачной синхронизации периодически держат файл открытым (риск R-06).
/// </summary>
public static class AtomicJsonStore
{
    private const int MaxAttempts = 5;

    public static async Task WriteAsync<T>(
        string path, T value, JsonSerializerOptions? options = null, CancellationToken ct = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var json = JsonSerializer.Serialize(value, options ?? JsonSetup.Pretty);
        var bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(json);
        var tmp = path + ".tmp";

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await using (var fs = new FileStream(
                    tmp, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
                {
                    await fs.WriteAsync(bytes, ct).ConfigureAwait(false);
                    await fs.FlushAsync(ct).ConfigureAwait(false);
                }

                if (File.Exists(path)) File.Replace(tmp, path, destinationBackupFileName: null);
                else File.Move(tmp, path);

                return;
            }
            catch (IOException) when (attempt < MaxAttempts)
            {
                await Task.Delay(50 * attempt, ct).ConfigureAwait(false);
            }
            catch (UnauthorizedAccessException) when (attempt < MaxAttempts)
            {
                await Task.Delay(50 * attempt, ct).ConfigureAwait(false);
            }
        }
    }

    public static async Task<T?> ReadAsync<T>(
        string path, JsonSerializerOptions? options = null, CancellationToken ct = default)
    {
        if (!File.Exists(path)) return default;

        try
        {
            await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            return await JsonSerializer
                .DeserializeAsync<T>(fs, options ?? JsonSetup.Pretty, ct)
                .ConfigureAwait(false);
        }
        catch (JsonException)
        {
            return default;
        }
    }
}
