using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace PatchBay.Audio;

/// <summary>A capture device (or a speaker's "what's playing" loopback) that feeds one or more cables.</summary>
internal sealed class SourceNode : IDisposable
{
    private static readonly Guid FloatSubType = new("00000003-0000-0010-8000-00aa00389b71");

    private readonly JackInfo _jack;
    private WasapiCapture? _capture;
    private volatile CableStream[] _cables = Array.Empty<CableStream>();
    private float[] _floats = Array.Empty<float>();
    private bool _isFloat;
    private int _bytesPerSample;
    private volatile bool _disposed;

    public SourceNode(JackInfo jack, int captureMs) { _jack = jack; CaptureMs = Math.Clamp(captureMs, 3, 10); }
    private int CaptureMs { get; }

    public string JackId => _jack.Id;
    public int Rate { get; private set; }
    public int Channels { get; private set; }
    public int CableCount => _cables.Length;
    public event Action<SourceNode, Exception?>? Failed;

    public void Start()
    {
        var dev = _jack.Device ?? throw new InvalidOperationException("Device is unplugged.");
        WasapiCapture cap = _jack.IsLoopback ? new WasapiLoopbackCapture(dev) : new WasapiCapture(dev, true, CaptureMs);
        var fmt = cap.WaveFormat;
        Rate = fmt.SampleRate;
        Channels = fmt.Channels;
        _bytesPerSample = fmt.BitsPerSample / 8;
        _isFloat = fmt.Encoding == WaveFormatEncoding.IeeeFloat
                   || (fmt is WaveFormatExtensible ext && ext.SubFormat == FloatSubType);
        if (_isFloat && _bytesPerSample != 4) throw new NotSupportedException($"Unsupported format: {fmt}");
        if (!_isFloat && _bytesPerSample is not (2 or 3 or 4)) throw new NotSupportedException($"Unsupported format: {fmt}");

        cap.DataAvailable += OnData;
        cap.RecordingStopped += (_, e) =>
        {
            if (!_disposed) Failed?.Invoke(this, e.Exception ?? new Exception("Recording stopped."));
        };
        _capture = cap;
        cap.StartRecording();
    }

    public void Add(CableStream c)
    {
        lock (this) _cables = _cables.Append(c).ToArray();
    }

    public void Remove(Guid id)
    {
        lock (this) _cables = _cables.Where(c => c.Id != id).ToArray();
    }

    private void OnData(object? sender, WaveInEventArgs e)
    {
        var cables = _cables;
        if (cables.Length == 0 || e.BytesRecorded == 0) return;

        int n = e.BytesRecorded / _bytesPerSample;
        if (_floats.Length < n) _floats = new float[n];
        var b = e.Buffer;

        if (_isFloat)
        {
            Buffer.BlockCopy(b, 0, _floats, 0, n * 4);
        }
        else if (_bytesPerSample == 2)
        {
            for (int i = 0; i < n; i++) _floats[i] = BitConverter.ToInt16(b, i * 2) / 32768f;
        }
        else if (_bytesPerSample == 3)
        {
            for (int i = 0; i < n; i++)
            {
                int j = i * 3;
                int v = b[j] | (b[j + 1] << 8) | ((sbyte)b[j + 2] << 16);
                _floats[i] = v / 8388608f;
            }
        }
        else
        {
            for (int i = 0; i < n; i++) _floats[i] = BitConverter.ToInt32(b, i * 4) / 2147483648f;
        }

        int frames = n / Channels;
        foreach (var c in cables) c.Push(_floats, frames);
    }

    public void Dispose()
    {
        _disposed = true;
        try { _capture?.StopRecording(); } catch { }
        try { _capture?.Dispose(); } catch { }
        _capture = null;
    }
}

/// <summary>An output device (speakers, "CABLE Input"...) that mixes every cable plugged into it.</summary>
internal sealed class DestNode : IDisposable
{
    private readonly JackInfo _jack;
    private WasapiOut? _out;
    private MixingSampleProvider? _mixer;
    private readonly Dictionary<Guid, ISampleProvider> _inputs = new();
    private volatile bool _disposed;

    public DestNode(JackInfo jack) => _jack = jack;

    public string JackId => _jack.Id;
    public int Rate { get; private set; }
    public int Channels { get; private set; }
    public int CableCount => _inputs.Count;
    public event Action<DestNode, Exception?>? Failed;

    public void Start(int latencyMs)
    {
        var dev = _jack.Device ?? throw new InvalidOperationException("Device is unplugged.");
        var mix = dev.AudioClient.MixFormat;
        Rate = mix.SampleRate;
        Channels = mix.Channels;
        _mixer = new MixingSampleProvider(WaveFormat.CreateIeeeFloatWaveFormat(Rate, Channels)) { ReadFully = true };

        var output = new WasapiOut(dev, AudioClientShareMode.Shared, true, latencyMs);
        output.PlaybackStopped += (_, e) =>
        {
            if (!_disposed) Failed?.Invoke(this, e.Exception ?? new Exception("Playback stopped."));
        };
        output.Init(new SampleToWaveProvider(_mixer));
        _out = output;
        output.Play();
    }

    public void Add(CableStream c)
    {
        ISampleProvider input = c;
        if (c.WaveFormat.SampleRate != Rate)
            input = new WdlResamplingSampleProvider(c, Rate);
        _inputs[c.Id] = input;
        _mixer!.AddMixerInput(input);
    }

    public void Remove(Guid id)
    {
        if (_inputs.Remove(id, out var input)) _mixer?.RemoveMixerInput(input);
    }

    public void Dispose()
    {
        _disposed = true;
        try { _out?.Stop(); } catch { }
        try { _out?.Dispose(); } catch { }
        _out = null;
    }
}

/// <summary>
/// Runs the cables. Each source device is captured once and each output device is opened once,
/// no matter how many cables share them. All public methods must be called on the UI thread.
/// </summary>
public sealed class AudioEngine : IDisposable
{
    private readonly Dictionary<string, SourceNode> _sources = new();
    private readonly Dictionary<string, DestNode> _dests = new();
    private readonly Dictionary<Guid, (CableStream Stream, SourceNode Src, DestNode Dst)> _cables = new();
    private readonly SynchronizationContext? _ui = SynchronizationContext.Current;

    public int LatencyMs { get; set; } = 3;

    /// <summary>Raised on the UI thread when a running cable dies (device unplugged, taken exclusively, ...).</summary>
    public event Action<Guid, string>? CableFailed;

    public bool IsActive(Guid id) => _cables.ContainsKey(id);

    public void Connect(Guid id, JackInfo from, JackInfo to, float volume, bool muted, ChannelMode mode)
    {
        if (_cables.ContainsKey(id)) return;
        if (from.Kind != JackKind.Out || to.Kind != JackKind.In) throw new ArgumentException("Cables go from an OUT jack to an IN jack.");

        bool newSrc = false, newDst = false;
        if (!_dests.TryGetValue(to.Id, out var dst))
        {
            dst = new DestNode(to);
            dst.Failed += (n, ex) => Post(() => NodeFailed(n, ex));
            try { dst.Start(LatencyMs); }
            catch { dst.Dispose(); throw; }
            _dests[to.Id] = dst;
            newDst = true;
        }

        if (!_sources.TryGetValue(from.Id, out var src))
        {
            src = new SourceNode(from, LatencyMs);
            src.Failed += (n, ex) => Post(() => NodeFailed(n, ex));
            try { src.Start(); }
            catch
            {
                src.Dispose();
                if (newDst) { dst.Dispose(); _dests.Remove(to.Id); }
                throw;
            }
            _sources[from.Id] = src;
            newSrc = true;
        }

        try
        {
            var stream = new CableStream(id, src.Rate, src.Channels, dst.Channels, LatencyMs)
            {
                Volume = volume, Muted = muted, Mode = mode,
            };
            dst.Add(stream);
            src.Add(stream);
            _cables[id] = (stream, src, dst);
        }
        catch
        {
            if (newSrc) { src.Dispose(); _sources.Remove(from.Id); }
            if (newDst) { dst.Dispose(); _dests.Remove(to.Id); }
            throw;
        }
    }

    public void Update(Guid id, float volume, bool muted, ChannelMode mode)
    {
        if (!_cables.TryGetValue(id, out var c)) return;
        c.Stream.Volume = volume;
        c.Stream.Muted = muted;
        c.Stream.Mode = mode;
    }

    /// <summary>How many times a cable ran dry (underruns) or had to skip ahead (skips).</summary>
    public (int Underruns, int Skips) Stats(Guid id) =>
        _cables.TryGetValue(id, out var c) ? (c.Stream.Underruns, c.Stream.Skips) : (0, 0);

    public void Disconnect(Guid id)
    {
        if (!_cables.Remove(id, out var c)) return;
        c.Src.Remove(id);
        c.Dst.Remove(id);
        if (c.Src.CableCount == 0) { c.Src.Dispose(); _sources.Remove(c.Src.JackId); }
        if (c.Dst.CableCount == 0) { c.Dst.Dispose(); _dests.Remove(c.Dst.JackId); }
    }

    public void DisconnectAll()
    {
        foreach (var id in _cables.Keys.ToList()) Disconnect(id);
    }

    private void NodeFailed(object node, Exception? ex)
    {
        var dead = _cables.Where(kv => ReferenceEquals(kv.Value.Src, node) || ReferenceEquals(kv.Value.Dst, node))
                          .Select(kv => kv.Key).ToList();
        string msg = ex?.Message ?? "Device stopped.";
        foreach (var id in dead)
        {
            Disconnect(id);
            CableFailed?.Invoke(id, msg);
        }
    }

    private void Post(Action a)
    {
        if (_ui != null) _ui.Post(_ => a(), null); else a();
    }

    public void Dispose() => DisconnectAll();
}
