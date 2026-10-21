using System.Buffers.Binary;
using System.IO;
using Concentus;
using NAudio.Wave;

namespace HB.Speech;

/// <summary>
/// Streams an Ogg Opus file (WhatsApp voice notes, .opus files…) as 48 kHz mono samples.
/// Parses the Ogg pages itself and decodes the packets with Concentus, so nothing is buffered
/// beyond one page. Chained files (several logical streams one after the other, as produced by
/// recorders with pause/resume) are played through as one.
/// </summary>
public sealed class OggOpusReader : ISampleProvider, IDisposable
{
    public const int OpusRate = 48000;
    private const int MaxFrame = OpusRate * 120 / 1000; // longest Opus packet: 120 ms
    private const int HeaderSize = 23; // after "OggS": version, flags, granule (8), serial (4), sequence (4), CRC (4), segments

    private readonly Stream _stream;
    private readonly IOpusDecoder _decoder = OpusCodecFactory.CreateDecoder(OpusRate, 1);
    private readonly Queue<byte[]> _packets = new();
    private readonly MemoryStream _partial = new(); // packet continued on the next page
    private readonly short[] _pcm = new short[MaxFrame];
    private int _pcmCount, _pcmOffset;
    private uint? _serial;  // logical stream being played; null until its first page
    private int _skip;      // samples still to drop (pre-skip from the stream header)
    private bool _ended;

    public OggOpusReader(string path)
    {
        _stream = File.OpenRead(path);
        Duration = ScanDuration();

        // The first packet of the file must be an Opus identification header.
        while (_packets.Count == 0)
            if (!ReadPage()) throw new InvalidOperationException("The file is not an Ogg Opus recording.");
        if (!IsOpusHead(_packets.Peek()))
            throw new InvalidOperationException("The file is not an Ogg Opus recording (Ogg Vorbis is not supported).");
    }

    public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(OpusRate, 1);
    public TimeSpan Duration { get; }
    public long Length => _stream.Length;
    public long Position => _stream.Position;

    public int Read(float[] buffer, int offset, int count)
    {
        int written = 0;
        while (written < count)
        {
            if (_pcmOffset == _pcmCount && !DecodeNextPacket()) break;
            int take = Math.Min(count - written, _pcmCount - _pcmOffset);
            for (int i = 0; i < take; i++) buffer[offset + written + i] = _pcm[_pcmOffset + i] / 32768f;
            _pcmOffset += take;
            written += take;
        }
        return written;
    }

    private bool DecodeNextPacket()
    {
        while (true)
        {
            while (_frames.Count == 0)
            {
                while (_packets.Count == 0)
                    if (!ReadPage()) return false;
                var packet = _packets.Dequeue();
                if (packet.Length == 0) continue;
                if (IsOpusHead(packet))
                {
                    // A new logical stream (chained file): fresh decoder state and pre-skip.
                    _decoder.ResetState();
                    _skip = PreSkipOf(packet);
                    continue;
                }
                if (packet.AsSpan().StartsWith("OpusTags"u8)) continue;
                SplitFrames(packet);
            }

            DecodeFrame(_frames.Dequeue());
            _pcmOffset = 0;
            if (_skip > 0)
            {
                int drop = Math.Min(_skip, _pcmCount);
                _pcmOffset = drop;
                _skip -= drop;
            }
            if (_pcmOffset < _pcmCount) return true;
        }
    }

    private readonly Queue<(byte Toc, byte[] Packet, int Offset, int Length)> _frames = new();

    /// <summary>
    /// Decodes one frame as a single-frame packet. Frames are decoded one at a time because
    /// multi-frame packets with empty (DTX / silence) frames, which WhatsApp produces, make
    /// the decoder throw; an empty frame becomes concealment or plain silence instead.
    /// </summary>
    private void DecodeFrame((byte Toc, byte[] Packet, int Offset, int Length) frame)
    {
        int frameSamples = FrameSamples(frame.Toc);
        try
        {
            if (frame.Length == 0)
            {
                _pcmCount = _decoder.Decode(ReadOnlySpan<byte>.Empty, _pcm, frameSamples, false); // loss concealment
                return;
            }
            var single = new byte[frame.Length + 1];
            single[0] = (byte)(frame.Toc & 0xFC); // same configuration, code 0: one frame
            Buffer.BlockCopy(frame.Packet, frame.Offset, single, 1, frame.Length);
            _pcmCount = _decoder.Decode(single, _pcm, MaxFrame, false);
        }
        catch (OpusException)
        {
            Array.Clear(_pcm, 0, frameSamples);
            _pcmCount = frameSamples;
        }
    }

    /// <summary>Samples (at 48 kHz) in one frame for the packet's configuration (RFC 6716 §3.1).</summary>
    private static int FrameSamples(byte toc)
    {
        int config = toc >> 3;
        if (config < 12) return (config & 3) switch { 0 => 480, 1 => 960, 2 => 1920, _ => 2880 }; // SILK
        if (config < 16) return (config & 1) == 0 ? 480 : 960;                                      // Hybrid
        return (config & 3) switch { 0 => 120, 1 => 240, 2 => 480, _ => 960 };                       // CELT
    }

    /// <summary>Queues the frames of an Opus packet (RFC 6716 §3.2). Malformed packets are dropped.</summary>
    private void SplitFrames(byte[] p)
    {
        byte toc = p[0];
        int pos = 1, end = p.Length;
        var lengths = new List<int>(6);
        switch (toc & 3)
        {
            case 0:
                lengths.Add(end - pos);
                break;
            case 1:
                if (((end - pos) & 1) != 0) return;
                lengths.Add((end - pos) / 2);
                lengths.Add((end - pos) / 2);
                break;
            case 2:
                if (!TryReadFrameLength(p, ref pos, out int first) || first > end - pos) return;
                lengths.Add(first);
                lengths.Add(end - pos - first);
                break;
            default:
                if (pos >= end) return;
                int count = p[pos] & 0x3F;
                bool vbr = (p[pos] & 0x80) != 0, padded = (p[pos] & 0x40) != 0;
                pos++;
                if (count == 0) return;
                if (padded)
                {
                    int padding = 0, b;
                    do
                    {
                        if (pos >= end) return;
                        b = p[pos++];
                        padding += b == 255 ? 254 : b;
                    } while (b == 255);
                    end -= padding;
                    if (end < pos) return;
                }
                if (vbr)
                {
                    int total = 0;
                    for (int i = 0; i < count - 1; i++)
                    {
                        if (!TryReadFrameLength(p, ref pos, out int length)) return;
                        lengths.Add(length);
                        total += length;
                    }
                    if (end - pos - total < 0) return;
                    lengths.Add(end - pos - total);
                }
                else
                {
                    if ((end - pos) % count != 0) return;
                    for (int i = 0; i < count; i++) lengths.Add((end - pos) / count);
                }
                break;
        }
        foreach (var length in lengths)
        {
            _frames.Enqueue((toc, p, pos, length));
            pos += length;
        }
    }

    private static bool TryReadFrameLength(byte[] p, ref int pos, out int length)
    {
        length = 0;
        if (pos >= p.Length) return false;
        int b = p[pos++];
        if (b < 252)
        {
            length = b;
            return true;
        }
        if (pos >= p.Length) return false;
        length = b + 4 * p[pos++];
        return true;
    }

    private static bool IsOpusHead(byte[] packet) => packet.Length >= 19 && packet.AsSpan().StartsWith("OpusHead"u8);

    private static int PreSkipOf(byte[] opusHead) => BinaryPrimitives.ReadUInt16LittleEndian(opusHead.AsSpan(10, 2));

    /// <summary>
    /// Reads the next page of the current logical stream into the packet queue. A "beginning of
    /// stream" page switches to that stream (chained files); pages of any other stream are skipped.
    /// </summary>
    private bool ReadPage()
    {
        Span<byte> header = stackalloc byte[HeaderSize];
        while (!_ended)
        {
            if (!ReadPageHeader(header, out byte flags, out _, out uint serial, out byte[] lacing, out int size))
            {
                _ended = true;
                return false;
            }
            var body = new byte[size];
            if (_stream.ReadAtLeast(body, size, throwOnEndOfStream: false) < size)
            {
                _ended = true;
                return false;
            }

            if ((flags & 0x02) != 0) _serial = serial; // beginning of a (new) logical stream
            if (serial != _serial) continue;

            if ((flags & 0x01) == 0) _partial.SetLength(0); // not a continuation: drop any dangling partial packet
            int position = 0;
            foreach (var l in lacing)
            {
                _partial.Write(body, position, l);
                position += l;
                if (l < 255)
                {
                    _packets.Enqueue(_partial.ToArray());
                    _partial.SetLength(0);
                }
            }
            return true;
        }
        return false;
    }

    /// <summary>Finds the next page and reads its header and lacing table; the body is left in the stream.</summary>
    private bool ReadPageHeader(Span<byte> header, out byte flags, out long granule, out uint serial,
        out byte[] lacing, out int bodySize)
    {
        flags = 0;
        granule = 0;
        serial = 0;
        lacing = Array.Empty<byte>();
        bodySize = 0;
        if (!FindCapturePattern() || _stream.ReadAtLeast(header, HeaderSize, throwOnEndOfStream: false) < HeaderSize)
            return false;

        flags = header[1];
        granule = BinaryPrimitives.ReadInt64LittleEndian(header[2..10]);
        serial = BinaryPrimitives.ReadUInt32LittleEndian(header[10..14]);
        int segmentCount = header[22];
        lacing = new byte[segmentCount];
        if (_stream.ReadAtLeast(lacing, segmentCount, throwOnEndOfStream: false) < segmentCount) return false;
        foreach (var l in lacing) bodySize += l;
        return true;
    }

    private bool FindCapturePattern()
    {
        int matched = 0;
        while (matched < 4)
        {
            int b = _stream.ReadByte();
            if (b < 0) return false;
            if (b == "OggS"u8[matched]) matched++;
            else matched = b == 'O' ? 1 : 0;
        }
        return true;
    }

    /// <summary>
    /// Walks the page headers of the whole file (bodies are skipped, so this is quick) and adds up
    /// the length of every logical stream: its last granule position minus its pre-skip.
    /// </summary>
    private TimeSpan ScanDuration()
    {
        var streams = new Dictionary<uint, (long Granule, int PreSkip)>();
        Span<byte> header = stackalloc byte[HeaderSize];
        var head = new byte[19];
        _stream.Position = 0;
        try
        {
            while (ReadPageHeader(header, out byte flags, out long granule, out uint serial, out _, out int size))
            {
                if ((flags & 0x02) != 0)
                {
                    int preSkip = 0;
                    if (size >= head.Length && _stream.ReadAtLeast(head, head.Length, throwOnEndOfStream: false) == head.Length)
                    {
                        if (IsOpusHead(head)) preSkip = PreSkipOf(head);
                        size -= head.Length;
                    }
                    streams[serial] = (0, preSkip);
                }
                else if (granule > 0 && streams.TryGetValue(serial, out var known) && granule > known.Granule)
                {
                    streams[serial] = (granule, known.PreSkip);
                }
                _stream.Seek(size, SeekOrigin.Current);
            }
        }
        finally
        {
            _stream.Position = 0;
            _ended = false;
        }

        double samples = 0;
        foreach (var (granule, preSkip) in streams.Values) samples += Math.Max(0, granule - preSkip);
        return TimeSpan.FromSeconds(samples / OpusRate);
    }

    public void Dispose()
    {
        _stream.Dispose();
        _partial.Dispose();
    }
}
