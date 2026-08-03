using MeetMemo.Audio;

// Проверка микрофонов: заводится ли устройство и идёт ли с него сигнал.
// Нужна потому, что часть USB-микрофонов отвергает «микшерный» формат WASAPI,
// и без лестницы форматов голос владельца компьютера в запись не попадает.

Console.OutputEncoding = System.Text.Encoding.UTF8;

var devices = DeviceCapture.ListMicrophones();
Console.WriteLine($"Найдено устройств записи: {devices.Count}");
foreach (var d in devices)
    Console.WriteLine($"  {(d.IsDefault ? "* " : "  ")}{d.Name}");

if (devices.Count == 0) return 1;

Console.WriteLine();
var failures = 0;

foreach (var device in devices)
{
    Console.Write($"{device.Name,-45} ");
    using var capture = new DeviceCapture();

    var buffers = 0;
    var peak = 0f;
    capture.DataAvailable += samples =>
    {
        buffers++;
        foreach (var s in samples)
        {
            var abs = Math.Abs(s);
            if (abs > peak) peak = abs;
        }
    };

    try
    {
        capture.StartMicrophone(device.Id);
        await Task.Delay(2500);
        capture.Stop();

        var status = buffers > 0 ? "OK" : "запустился, но данных нет";
        Console.WriteLine($"{status}  ({capture.SampleRate} Гц, {capture.Channels} кан., "
            + $"порций {buffers}, пик {peak:F3})");

        if (buffers == 0) failures++;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"ОШИБКА: {ex.Message}");
        failures++;
    }
}

Console.WriteLine();
Console.WriteLine(failures == 0
    ? "Все устройства записи работают"
    : $"Проблемных устройств: {failures}");

// Главная проверка: приложение должно найти рабочий микрофон даже если
// устройство по умолчанию неисправно.
Console.WriteLine();
Console.Write("Выбор рабочего микрофона приложением: ");
using (var auto = new DeviceCapture())
{
    var buffers = 0;
    auto.DataAvailable += _ => buffers++;
    try
    {
        auto.StartBestMicrophone(null);
        await Task.Delay(1500);
        auto.Stop();
        Console.WriteLine($"«{auto.ActiveDeviceName}», порций {buffers}"
            + (auto.SubstitutedFrom is { } from ? $" (заменён с «{from}»)" : string.Empty));
        if (buffers == 0) return 1;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"ОШИБКА: {ex.Message}");
        return 1;
    }
}

return 0;
