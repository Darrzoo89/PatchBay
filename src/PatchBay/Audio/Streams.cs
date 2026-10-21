using NAudio.Wave;

namespace PatchBay.Audio;

public enum ChannelMode
{
    /// <summary>Left goes to left, right to right (mono is copied to both).</summary>
    Normal,
    /// <summary>Only the left / first channel, sent to every output channel. Good for a guitar in input 1.</summary>
    LeftOnly,
    /// <summary>Only the right / second channel, sent to every output channel.</summary>
    RightOnly,
    /// <summary>All channels mixed down to mono, sent to every output channel.</summary>
    MonoMix,
}

/// <summary>Thread-safe ring buffer of float samples. Drops the oldest audio when full.</summary>
internal sealed class FloatRing
{
    private readonly float[] _buf;
    private readonly object _gate = new();
    private int _read, _count;

    public FloatRing(int capacity) => _buf = new float[capacity];

    public int Count { get { lock (_gate) return _count; } }

    public void Write(float[] src, int n)
    {
        lock (_gate)
        {
            int srcOff = 0;
            if (n > _buf.Length) { srcOff = n - _buf.Length; n = _buf.Length; }
            int overflow = _count + n - _buf.Length;
            if (overflow > 0) DiscardLocked(overflow);

            int write = (_read + _count) % _buf.Length;
            int first = Math.Min(n, _buf.Length - write);
            Array.Copy(src, srcOff, _buf, write, first);
            if (n > first) Array.Copy(src, srcOff + first, _buf, 0, n - first);
            _count += n;
        }
    }

    public int Read(float[] dst, int off, int n)
    {
        lock (_gate)
        {
            n = Math.Min(n, _count);
            int first = Math.Min(n, _buf.Length - _read);
            Array.Copy(_buf, _read, dst, off, first);
            if (n > first) Array.Copy(_buf, 0, dst, off + first, n - first);
            _read = (_read + n) % _buf.Length;
            _count -= n;
            return n;
        }
    }

    public void Discard(int n)
    {
        lock (_gate) DiscardLocked(n);
    }

    private void DiscardLocked(int n)
    {
        n = Math.Min(n, _count);
        _read = (_read + n) % _buf.Length;
        _count -= n;
    }
}

/// <summary>
/// One patch cable: receives audio from a capture device (Push, on the capture thread) and
/// hands it to the output device's mixer (Read, on the render thread). Keeps the buffer near a
/// target size so latency stays low even when the two devices' clocks drift apart.
/// </summary>
internal sealed class CableStream : ISampleProvider
{
    private readonly FloatRing _ring;
    private readonly int _srcChannels, _dstChannels;
    private readonly int _target, _max;
    private float[] _map = Array.Empty<float>();
    private bool _primed;
    private float _gain;

    public Guid Id { get; }
    public WaveFormat WaveFormat { get; }
    public volatile ChannelMode Mode;
    public volatile float Volume = 1f;
    public volatile bool Muted;
    public int Underruns;
    public int Skips;

    public CableStream(Guid id, int srcRate, int srcChannels, int dstChannels, int latencyMs)
    {
        Id = id;
        _srcChannels = srcChannels;
        _dstChannels = dstChannels;
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(srcRate, dstChannels);
        int Frames(int ms) => Math.Max(1, srcRate * ms / 1000);
        _target = Frames(latencyMs) * dstChannels;
        _max = _target + Frames(Math.Max(30, latencyMs * 2)) * dstChannels;
        _ring = new FloatRing(Frames(1000) * dstChannels);
    }

    /// <summary>Called from the capture thread with interleaved float frames in the source's channel layout.</summary>
    public void Push(float[] src, int frames)
    {
        int needed = frames * _dstChannels;
        if (_map.Length < needed) _map = new float[needed];
        int sc = _srcChannels, dc = _dstChannels;
        var mode = Mode;

        for (int f = 0; f < frames; f++)
        {
            int si = f * sc, di = f * dc;
            switch (mode)
            {
                case ChannelMode.LeftOnly:
                {
                    float v = src[si];
                    for (int c = 0; c < dc; c++) _map[di + c] = v;
                    break;
                }
                case ChannelMode.RightOnly:
                {
                    float v = src[si + (sc > 1 ? 1 : 0)];
                    for (int c = 0; c < dc; c++) _map[di + c] = v;
                    break;
                }
                case ChannelMode.MonoMix:
                {
                    float sum = 0;
                    for (int c = 0; c < sc; c++) sum += src[si + c];
                    float v = sum / sc;
                    for (int c = 0; c < dc; c++) _map[di + c] = v;
                    break;
                }
                default:
                    if (dc == 1 && sc > 1)
                    {
                        float sum = 0;
                        for (int c = 0; c < sc; c++) sum += src[si + c];
                        _map[di] = sum / sc;
                    }
                    else
                    {
                        for (int c = 0; c < dc; c++) _map[di + c] = src[si + (c % sc)];
                    }
                    break;
            }
        }
        _ring.Write(_map, needed);
    }

    public int Read(float[] buffer, int offset, int count)
    {
        int avail = _ring.Count;
        if (!_primed)
        {
            if (avail < _target)
            {
                Array.Clear(buffer, offset, count);
                return count;
            }
            _primed = true;
        }

        // Too much buffered (clock drift or a hiccup) -> skip ahead to keep latency low.
        if (avail > _max)
        {
            int drop = avail - _target;
            drop -= drop % _dstChannels;
            _ring.Discard(drop);
            Skips++;
        }

        int got = _ring.Read(buffer, offset, count);
        if (got < count)
        {
            Array.Clear(buffer, offset + got, count - got);
            _primed = false; // underrun: build the cushion back up
            Underruns++;
        }

        float target = Muted ? 0f : Volume;
        float g = _gain;
        if (Math.Abs(target - g) < 1e-4f)
        {
            if (Math.Abs(g - 1f) > 1e-4f)
                for (int i = 0; i < got; i++) buffer[offset + i] *= g;
        }
        else
        {
            // Short ramp so volume/mute changes don't click.
            float step = (target - g) / Math.Max(1, got);
            for (int i = 0; i < got; i++) { buffer[offset + i] *= g; g += step; }
        }
        _gain = target;
        return count; // always full so the mixer keeps this input
    }
}
