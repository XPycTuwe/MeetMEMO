using MeetMemo.Contracts;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace MeetMemo.Audio;

/// <summary>Описание устройства ввода для экрана подтверждения старта.</summary>
public sealed record AudioDeviceInfo(string Id, string Name, bool IsDefault);

/// <summary>
/// Захват микрофона и общего системного loopback через NAudio. Оба режима есть в библиотеке
/// готовыми, поэтому здесь только приведение к единому формату float32 и события.
/// </summary>
public sealed class DeviceCapture : IDisposable
{
    private readonly ILogger _log;
    private WasapiCapture? _capture;

    public DeviceCapture(ILogger? log = null) => _log = log ?? NullLogger.Instance;

    public event Action<float[]>? DataAvailable;

    public event Action<Exception>? Failed;

    public int SampleRate { get; private set; } = 48000;

    public int Channels { get; private set; } = 2;

    public bool IsRunning { get; private set; }

    public static IReadOnlyList<AudioDeviceInfo> ListMicrophones()
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            var defaultId = TryGetDefaultId(enumerator, DataFlow.Capture);

            return enumerator
                .EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active)
                .Select(d => new AudioDeviceInfo(d.ID, d.FriendlyName, d.ID == defaultId))
                .ToList();
        }
        catch (Exception)
        {
            return Array.Empty<AudioDeviceInfo>();
        }
    }

    private static string? TryGetDefaultId(MMDeviceEnumerator enumerator, DataFlow flow)
    {
        try
        {
            return enumerator.GetDefaultAudioEndpoint(flow, Role.Communications).ID;
        }
        catch (Exception)
        {
            // Устройства по умолчанию может не быть — это не ошибка (ТЗ 17.2).
            return null;
        }
    }

    /// <summary>Имя устройства, с которого реально идёт запись.</summary>
    public string? ActiveDeviceName { get; private set; }

    /// <summary>
    /// Устройство, которое выбрал пользователь, оказалось неработоспособным и было
    /// заменено на другое. Вызывающий обязан сообщить об этом в журнал сессии.
    /// </summary>
    public string? SubstitutedFrom { get; private set; }

    /// <summary>Запуск микрофона. deviceId=null — устройство по умолчанию.</summary>
    public void StartMicrophone(string? deviceId)
    {
        using var enumerator = new MMDeviceEnumerator();
        MMDevice device;

        if (deviceId is not null)
        {
            device = enumerator.GetDevice(deviceId);
        }
        else
        {
            device = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);
        }

        StartMicrophoneWithFormatLadder(device);
        ActiveDeviceName = device.FriendlyName;
    }

    /// <summary>
    /// Запуск микрофона с переходом на другое устройство, если выбранное не работает.
    ///
    /// Реальный случай: устройство по умолчанию числится активным, но аудиодвижок его
    /// не открывает (микрофон физически отключён или занят драйвером). Раньше встреча
    /// в такой ситуации оставалась совсем без голоса владельца компьютера, хотя рядом
    /// были рабочие устройства — теперь берём первое из них.
    /// </summary>
    public void StartBestMicrophone(string? preferredDeviceId)
    {
        var tried = new List<string>();
        string? preferredName = null;

        if (preferredDeviceId is not null || TryGetDefaultDeviceId() is not null)
        {
            var firstId = preferredDeviceId ?? TryGetDefaultDeviceId();
            try
            {
                StartMicrophone(firstId);
                return;
            }
            catch (Exception ex)
            {
                preferredName = SafeDeviceName(firstId);
                tried.Add(preferredName ?? firstId ?? "по умолчанию");
                _log.LogWarning("Микрофон «{Device}» не запустился: {Message}", preferredName, ex.Message);
            }
        }

        foreach (var candidate in ListMicrophones())
        {
            if (candidate.Id == preferredDeviceId) continue;

            try
            {
                StartMicrophone(candidate.Id);
                SubstitutedFrom = preferredName;
                _log.LogInformation("Микрофон заменён на «{Device}»", candidate.Name);
                return;
            }
            catch (Exception ex)
            {
                tried.Add(candidate.Name);
                _log.LogWarning("Микрофон «{Device}» не запустился: {Message}", candidate.Name, ex.Message);
            }
        }

        throw new InvalidOperationException(
            tried.Count == 0
                ? "Не найдено ни одного устройства записи"
                : $"Ни одно устройство записи не запустилось: {string.Join(", ", tried)}");
    }

    private static string? TryGetDefaultDeviceId()
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            return enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications).ID;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string? SafeDeviceName(string? deviceId)
    {
        if (deviceId is null) return null;
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            return enumerator.GetDevice(deviceId).FriendlyName;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Некоторые микрофоны (в том числе студийные USB вроде Razer Seiren) отвергают формат,
    /// который WASAPI отдаёт как «микшерный», и возвращают AUDCLNT_E_UNSUPPORTED_FORMAT.
    /// Поэтому пробуем формат устройства, а затем спускаемся по списку обычных: потерять
    /// голос владельца компьютера из-за одной неудачной попытки недопустимо.
    /// </summary>
    private void StartMicrophoneWithFormatLadder(MMDevice device)
    {
        var candidates = new List<WaveFormat?> { null }; // null — формат устройства по умолчанию

        try
        {
            var mix = device.AudioClient.MixFormat;
            candidates.Add(WaveFormat.CreateIeeeFloatWaveFormat(mix.SampleRate, mix.Channels));
            candidates.Add(new WaveFormat(mix.SampleRate, 16, mix.Channels));
            candidates.Add(new WaveFormat(mix.SampleRate, 16, 1));
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Не удалось прочитать микшерный формат устройства");
        }

        candidates.Add(WaveFormat.CreateIeeeFloatWaveFormat(48000, 1));
        candidates.Add(new WaveFormat(48000, 16, 1));
        candidates.Add(new WaveFormat(44100, 16, 1));

        Exception? lastError = null;

        foreach (var format in candidates)
        {
            WasapiCapture? capture = null;
            try
            {
                capture = new WasapiCapture(device) { ShareMode = AudioClientShareMode.Shared };
                if (format is not null) capture.WaveFormat = format;

                StartInternal(capture, $"микрофон «{device.FriendlyName}»");
                return;
            }
            catch (Exception ex)
            {
                lastError = ex;
                _log.LogWarning("Формат {Format} не принят устройством: {Message}",
                    format?.ToString() ?? "по умолчанию", ex.Message);

                try { capture?.Dispose(); } catch { }
                _capture = null;
            }
        }

        throw new InvalidOperationException(
            $"Устройство «{device.FriendlyName}» не приняло ни один из проверенных форматов записи",
            lastError);
    }

    /// <summary>Резервный режим: общий loopback устройства вывода (ТЗ 8.1).</summary>
    public void StartSystemLoopback()
    {
        var capture = new WasapiLoopbackCapture();
        StartInternal(capture, "системный звук");
    }

    private void StartInternal(WasapiCapture capture, string what)
    {
        _capture = capture;
        SampleRate = capture.WaveFormat.SampleRate;
        Channels = capture.WaveFormat.Channels;

        capture.DataAvailable += OnData;
        capture.RecordingStopped += (_, e) =>
        {
            IsRunning = false;
            if (e.Exception is not null)
            {
                _log.LogError(e.Exception, "Захват ({What}) остановлен ошибкой", what);
                Failed?.Invoke(e.Exception);
            }
        };

        capture.StartRecording();
        IsRunning = true;
        _log.LogInformation("Запущен захват: {What}, {Rate} Гц, {Channels} кан.",
            what, SampleRate, Channels);
    }

    private void OnData(object? sender, WaveInEventArgs e)
    {
        if (e.BytesRecorded == 0) return;

        var format = _capture?.WaveFormat;
        if (format is null) return;

        var samples = ConvertToFloat(e.Buffer, e.BytesRecorded, format);
        if (samples.Length > 0) DataAvailable?.Invoke(samples);
    }

    /// <summary>
    /// Приведение к float32. WASAPI в shared mode обычно отдаёт float32, но при захвате
    /// с некоторых устройств (в том числе Bluetooth-гарнитур) формат оказывается 16-битным.
    /// </summary>
    private static float[] ConvertToFloat(byte[] buffer, int bytesRecorded, WaveFormat format)
    {
        if (format.Encoding == WaveFormatEncoding.IeeeFloat && format.BitsPerSample == 32)
        {
            var count = bytesRecorded / 4;
            var samples = new float[count];
            Buffer.BlockCopy(buffer, 0, samples, 0, bytesRecorded);
            return samples;
        }

        if (format.BitsPerSample == 16)
        {
            var count = bytesRecorded / 2;
            var samples = new float[count];
            for (var i = 0; i < count; i++)
                samples[i] = BitConverter.ToInt16(buffer, i * 2) / 32768f;
            return samples;
        }

        if (format.BitsPerSample == 32 && format.Encoding == WaveFormatEncoding.Pcm)
        {
            var count = bytesRecorded / 4;
            var samples = new float[count];
            for (var i = 0; i < count; i++)
                samples[i] = BitConverter.ToInt32(buffer, i * 4) / 2147483648f;
            return samples;
        }

        return Array.Empty<float>();
    }

    public void Stop()
    {
        if (_capture is null) return;
        try { _capture.StopRecording(); } catch { /* устройство могло исчезнуть */ }
        IsRunning = false;
    }

    public void Dispose()
    {
        Stop();
        _capture?.Dispose();
        _capture = null;
    }
}
