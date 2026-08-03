using System.Runtime.InteropServices;
using MeetMemo.Audio.Interop;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace MeetMemo.Audio;

/// <summary>
/// Захват звука конкретного процесса и его дочерних процессов (WASAPI process loopback).
/// Это главная ставка продукта: без изоляции по дереву процессов в дорожку встречи попадал бы
/// весь звук системы. Ограничение платформы: изоляция идёт по процессу, а не по окну, поэтому
/// у многопроцессных браузеров в дорожку может попасть звук другой вкладки того же процесса.
/// </summary>
public sealed class ProcessLoopbackCapture : IDisposable
{
    private const int SampleRate = 48000;
    private const int Channels = 2;
    private const long BufferDurationHns = 2_000_000; // 200 мс

    private readonly int _processId;
    private readonly bool _includeTree;
    private readonly ILogger _log;

    private IAudioClient? _client;
    private IAudioCaptureClient? _captureClient;
    private IntPtr _eventHandle = IntPtr.Zero;
    private Thread? _pumpThread;
    private volatile bool _running;

    public ProcessLoopbackCapture(int processId, bool includeProcessTree = true, ILogger? log = null)
    {
        _processId = processId;
        _includeTree = includeProcessTree;
        _log = log ?? NullLogger.Instance;
    }

    /// <summary>Порция звука: float32, 48 кГц, 2 канала (interleaved).</summary>
    public event Action<float[]>? DataAvailable;

    /// <summary>Фатальная ошибка потока захвата — подписчик решает, переключаться ли на system loopback.</summary>
    public event Action<Exception>? Failed;

    public int Format_SampleRate => SampleRate;

    public int Format_Channels => Channels;

    public void Start()
    {
        if (_running) return;

        _client = ActivateProcessLoopbackClient();

        var format = WaveFormatEx.CreateIeeeFloat(SampleRate, Channels);
        var formatPtr = Marshal.AllocHGlobal(Marshal.SizeOf<WaveFormatEx>());
        try
        {
            Marshal.StructureToPtr(format, formatPtr, false);

            var hr = _client.Initialize(
                AudioClientFlags.Shared,
                AudioClientFlags.StreamFlagsLoopback | AudioClientFlags.StreamFlagsEventCallback,
                BufferDurationHns,
                0,
                formatPtr,
                IntPtr.Zero);

            if (hr != 0)
                throw new InvalidOperationException(
                    $"IAudioClient.Initialize для process loopback вернул 0x{hr:X8}");
        }
        finally
        {
            Marshal.FreeHGlobal(formatPtr);
        }

        _eventHandle = WasapiNative.CreateEventW(IntPtr.Zero, false, false, null);
        if (_eventHandle == IntPtr.Zero)
            throw new InvalidOperationException("Не удалось создать событие для захвата звука");

        Check(_client.SetEventHandle(_eventHandle), nameof(IAudioClient.SetEventHandle));

        Check(_client.GetService(typeof(IAudioCaptureClient).GUID, out var service), "GetService");
        _captureClient = (IAudioCaptureClient)service;

        Check(_client.Start(), nameof(IAudioClient.Start));

        _running = true;
        _pumpThread = new Thread(Pump)
        {
            IsBackground = true,
            Name = $"MeetMemo process loopback {_processId}",
            // Захват звука приоритетнее распознавания: пропуск аудио невосполним.
            Priority = ThreadPriority.AboveNormal
        };
        _pumpThread.Start();

        _log.LogInformation("Process loopback запущен для PID {Pid} (дерево процессов: {Tree})",
            _processId, _includeTree);
    }

    private IAudioClient ActivateProcessLoopbackClient()
    {
        var activationParams = new AudioClientActivationParams
        {
            ActivationType = AudioClientActivationType.ProcessLoopback,
            ProcessLoopbackParams = new AudioClientProcessLoopbackParams
            {
                TargetProcessId = (uint)_processId,
                ProcessLoopbackMode = _includeTree
                    ? ProcessLoopbackMode.IncludeTargetProcessTree
                    : ProcessLoopbackMode.ExcludeTargetProcessTree
            }
        };

        var paramsSize = Marshal.SizeOf<AudioClientActivationParams>();
        var paramsPtr = Marshal.AllocHGlobal(paramsSize);
        // PROPVARIANT на x64: vt(2) + 3 резерва(6) + union с 8-байтовым выравниванием = 24 байта.
        var propVariant = Marshal.AllocHGlobal(24);

        try
        {
            Marshal.StructureToPtr(activationParams, paramsPtr, false);

            for (var i = 0; i < 24; i++) Marshal.WriteByte(propVariant, i, 0);
            Marshal.WriteInt16(propVariant, 0, (short)VarEnum.VT_BLOB);
            Marshal.WriteInt32(propVariant, 8, paramsSize);      // blob.cbSize
            Marshal.WriteIntPtr(propVariant, 16, paramsPtr);     // blob.pBlobData

            var handler = new ActivationHandler();
            WasapiNative.ActivateAudioInterfaceAsync(
                WasapiNative.VirtualAudioDeviceProcessLoopback,
                WasapiNative.IID_IAudioClient,
                propVariant,
                handler,
                out var operation);

            if (!handler.Wait(TimeSpan.FromSeconds(10)))
                throw new TimeoutException("Активация process loopback не завершилась за 10 секунд");

            operation.GetActivateResult(out var activateResult, out var activatedInterface);
            if (activateResult != 0)
                throw new InvalidOperationException(
                    $"Активация process loopback для PID {_processId} вернула 0x{activateResult:X8}");

            return (IAudioClient)activatedInterface;
        }
        finally
        {
            Marshal.FreeHGlobal(propVariant);
            Marshal.FreeHGlobal(paramsPtr);
        }
    }

    private void Pump()
    {
        try
        {
            while (_running)
            {
                var wait = WasapiNative.WaitForSingleObject(_eventHandle, 500);
                if (!_running) break;
                if (wait == WasapiNative.WAIT_TIMEOUT)
                {
                    // Тишина в приложении не является ошибкой: буферов просто нет.
                    continue;
                }

                DrainPackets();
            }
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Поток process loopback завершился с ошибкой");
            Failed?.Invoke(ex);
        }
    }

    private void DrainPackets()
    {
        if (_captureClient is null) return;

        while (_running)
        {
            var hr = _captureClient.GetNextPacketSize(out var packetFrames);
            if (hr != 0 || packetFrames == 0) return;

            hr = _captureClient.GetBuffer(
                out var dataPtr, out var framesRead, out var flags, out _, out _);
            if (hr != 0 || framesRead == 0) return;

            try
            {
                var sampleCount = (int)framesRead * Channels;
                var samples = new float[sampleCount];

                if ((flags & AudioClientFlags.BufferFlagsSilent) != 0)
                {
                    // Драйвер сообщил тишину и не заполнил буфер — отдаём нули, чтобы
                    // шкала времени не разъезжалась с реально прошедшим временем.
                    Array.Clear(samples);
                }
                else
                {
                    Marshal.Copy(dataPtr, samples, 0, sampleCount);
                }

                DataAvailable?.Invoke(samples);
            }
            finally
            {
                _captureClient.ReleaseBuffer(framesRead);
            }
        }
    }

    private static void Check(int hr, string what)
    {
        if (hr != 0) throw new InvalidOperationException($"{what} вернул 0x{hr:X8}");
    }

    public void Stop()
    {
        if (!_running) return;
        _running = false;

        if (_eventHandle != IntPtr.Zero) WasapiNative.SetEvent(_eventHandle);
        _pumpThread?.Join(TimeSpan.FromSeconds(3));
        _pumpThread = null;

        try { _client?.Stop(); } catch { /* устройство могло исчезнуть */ }
    }

    public void Dispose()
    {
        Stop();

        if (_captureClient is not null)
        {
            Marshal.ReleaseComObject(_captureClient);
            _captureClient = null;
        }

        if (_client is not null)
        {
            Marshal.ReleaseComObject(_client);
            _client = null;
        }

        if (_eventHandle != IntPtr.Zero)
        {
            WasapiNative.CloseHandle(_eventHandle);
            _eventHandle = IntPtr.Zero;
        }
    }

    /// <summary>
    /// ActivateAudioInterfaceAsync возвращает результат через COM-коллбэк, поэтому ждём его
    /// событием. Активация выполняется на MTA-потоке вызывающего.
    /// </summary>
    private sealed class ActivationHandler : IActivateAudioInterfaceCompletionHandler
    {
        private readonly ManualResetEventSlim _done = new(false);

        public void ActivateCompleted(IActivateAudioInterfaceAsyncOperation activateOperation)
            => _done.Set();

        public bool Wait(TimeSpan timeout) => _done.Wait(timeout);
    }
}
