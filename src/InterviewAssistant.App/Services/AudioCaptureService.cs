using InterviewAssistant.Core.Audio;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using NAudio.Wave;

namespace InterviewAssistant.App.Services;

public sealed record PlaybackDevice(string? Id, string Name)
{
    public override string ToString() => Name;
}

/// <summary>
/// WASAPI loopback capture of a playback (render) device — hears exactly what Chrome/Meet plays to the headset,
/// with no browser extension or tab sharing. Converts to 24 kHz mono PCM16 in 100 ms frames.
/// Robustness: injects silence when the device delivers no packets (WASAPI loopback is silent-by-absence, but the
/// transcription VAD needs continuous time), follows the Windows default device when "System default" is chosen,
/// and restarts automatically after device loss (e.g. Bluetooth headset reconnect).
/// </summary>
public sealed class AudioCaptureService : IDisposable, IMMNotificationClient
{
    private readonly object _lock = new();
    private MMDeviceEnumerator? _enumerator;
    private WasapiLoopbackCapture? _capture;
    private PcmConverter? _converter;
    private AudioFramer _framer = new();
    private System.Threading.Timer? _silenceTimer;
    private long _lastDataTicks;
    private string? _requestedDeviceId;
    private bool _running;
    private int _restartAttempts;

    public event Action<byte[], double>? FrameReady;   // pcm16 frame (100 ms), level dBFS
    public event Action<string>? StatusChanged;
    public string CurrentDeviceName { get; private set; } = "";
    public bool IsRunning => _running;

    public static IReadOnlyList<PlaybackDevice> ListDevices()
    {
        var list = new List<PlaybackDevice> { new(null, "System default (follows Windows)") };
        try
        {
            using var en = new MMDeviceEnumerator();
            foreach (var d in en.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            {
                list.Add(new PlaybackDevice(d.ID, d.FriendlyName));
                d.Dispose();
            }
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            AppLog.Error("Device enumeration failed", ex);
        }
        return list;
    }

    public void Start(string? deviceId)
    {
        lock (_lock)
        {
            _requestedDeviceId = deviceId;
            _running = true;
            _restartAttempts = 0;
            if (_enumerator == null)
            {
                _enumerator = new MMDeviceEnumerator();
                _enumerator.RegisterEndpointNotificationCallback(this);
            }
            StartCaptureLocked();
            _silenceTimer ??= new System.Threading.Timer(_ => InjectSilenceIfStalled(), null, 100, 100);
        }
    }

    public void Stop()
    {
        lock (_lock)
        {
            _running = false;
            StopCaptureLocked();
        }
    }

    private void StartCaptureLocked()
    {
        StopCaptureLocked();
        try
        {
            var device = _requestedDeviceId == null
                ? _enumerator!.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia)
                : _enumerator!.GetDevice(_requestedDeviceId);
            CurrentDeviceName = device.FriendlyName;
            _capture = new WasapiLoopbackCapture(device);
            _converter = new PcmConverter(ToSourceFormat(_capture.WaveFormat));
            _framer = new AudioFramer();
            _capture.DataAvailable += OnData;
            _capture.RecordingStopped += OnStopped;
            Interlocked.Exchange(ref _lastDataTicks, Environment.TickCount64);
            _capture.StartRecording();
            AppLog.Info($"Loopback capture started: {CurrentDeviceName} ({_capture.WaveFormat})");
            StatusChanged?.Invoke("ACTIVE");
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or ArgumentException or InvalidOperationException or NotSupportedException)
        {
            AppLog.Error("Could not start loopback capture", ex);
            StatusChanged?.Invoke("DEVICE ERROR");
            ScheduleRestart();
        }
    }

    private void StopCaptureLocked()
    {
        if (_capture == null) return;
        _capture.DataAvailable -= OnData;
        _capture.RecordingStopped -= OnStopped;
        try { _capture.StopRecording(); } catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException) { /* device already gone */ }
        _capture.Dispose();
        _capture = null;
    }

    internal static SourceFormat ToSourceFormat(WaveFormat wf)
    {
        bool isFloat = wf.Encoding == WaveFormatEncoding.IeeeFloat ||
                       (wf is WaveFormatExtensible ext && ext.SubFormat == NAudio.Dmo.AudioMediaSubtypes.MEDIASUBTYPE_IEEE_FLOAT);
        var enc = isFloat ? SampleEncoding.Float32 : wf.BitsPerSample switch { 16 => SampleEncoding.Pcm16, 24 => SampleEncoding.Pcm24, 32 => SampleEncoding.Pcm32, _ => throw new NotSupportedException($"Unsupported bit depth {wf.BitsPerSample}") };
        return new SourceFormat(wf.SampleRate, wf.Channels, enc);
    }

    private void OnData(object? sender, WaveInEventArgs e)
    {
        if (e.BytesRecorded == 0) return;
        Interlocked.Exchange(ref _lastDataTicks, Environment.TickCount64);
        PcmConverter? conv; AudioFramer framer;
        lock (_lock) { conv = _converter; framer = _framer; }
        if (conv == null) return;
        var pcm = conv.Convert(e.Buffer.AsSpan(0, e.BytesRecorded));
        if (pcm.Length == 0) return;
        foreach (var frame in framer.Push(pcm)) FrameReady?.Invoke(frame, PcmConverter.LevelDb(frame));
    }

    private void InjectSilenceIfStalled()
    {
        if (!_running || _capture == null) return;
        // WASAPI loopback delivers no packets while nothing is playing. Keep the stream's timeline continuous
        // so server-side VAD can detect end-of-speech even if playback stops abruptly.
        if (Environment.TickCount64 - Interlocked.Read(ref _lastDataTicks) < 200) return;
        Interlocked.Exchange(ref _lastDataTicks, Environment.TickCount64 - 100);
        FrameReady?.Invoke(new byte[PcmConverter.TargetRate * 2 / 10], -100);
    }

    private void OnStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception != null) AppLog.Warn("Capture stopped: " + e.Exception.Message);
        if (_running) { StatusChanged?.Invoke("RECONNECTING DEVICE"); ScheduleRestart(); }
    }

    private void ScheduleRestart()
    {
        if (!_running) return;
        var delay = Math.Min(10_000, 1000 * (1 << Math.Min(_restartAttempts++, 3)));
        _ = Task.Delay(delay).ContinueWith(_ => { lock (_lock) { if (_running) StartCaptureLocked(); } }, TaskScheduler.Default);
    }

    // IMMNotificationClient: follow default-device changes and recover from unplug/replug.
    public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId)
    {
        if (flow != DataFlow.Render || role != Role.Multimedia || !_running || _requestedDeviceId != null) return;
        AppLog.Info("Default playback device changed; switching capture");
        _ = Task.Run(() => { lock (_lock) { if (_running) StartCaptureLocked(); } });
    }
    public void OnDeviceStateChanged(string deviceId, DeviceState newState)
    {
        if (!_running) return;
        if (newState == DeviceState.Active && (deviceId == _requestedDeviceId || _capture == null))
            _ = Task.Run(() => { lock (_lock) { if (_running) { _restartAttempts = 0; StartCaptureLocked(); } } });
    }
    public void OnDeviceAdded(string pwstrDeviceId) { }
    public void OnDeviceRemoved(string deviceId) { }
    public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key) { }

    public void Dispose()
    {
        Stop();
        _silenceTimer?.Dispose();
        if (_enumerator != null)
        {
            try { _enumerator.UnregisterEndpointNotificationCallback(this); } catch (System.Runtime.InteropServices.COMException) { }
            _enumerator.Dispose();
        }
    }
}
