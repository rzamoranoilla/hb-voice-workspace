using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using Whisper.net;
using Whisper.net.Ggml;

namespace HB.Speech;

/// <summary>
/// Captures the microphone (or plays back an audio file) and transcribes it with Whisper in real time.
/// Audio is cut into utterances at pauses: the utterance in progress is re-transcribed
/// continuously for the live preview and committed once the speaker pauses.
/// </summary>
public sealed class SpeechTranscriber : IDisposable
{
    private const int SampleRate = 16000;
    private const int PauseToCommitMs = 700;
    private const int MaxUtteranceSeconds = 25;

    private static readonly Regex NonSpeechTags = new(@"\[[^\]]*\]|\([^)]*\)", RegexOptions.Compiled);
    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.Compiled);

    private readonly object _gate = new();
    private readonly List<float> _utterance = new();
    private int _speechEnd;            // index in _utterance just past the last frame that contained speech
    private float _noiseLevel = 0.003f;

    private readonly StringBuilder _committed = new();
    private WhisperFactory? _factory;
    private string? _factoryModel;
    private WhisperProcessor? _processor;
    private WaveInEvent? _microphone;
    private WaveOutEvent? _player;
    private IDisposable? _fileHandle; // the open file behind the playback
    private CancellationTokenSource? _cancellation;
    private Task? _worker;

    /// <summary>Raised on a background thread with the full text of the current dictation so far.</summary>
    public event Action<string>? TextChanged;

    /// <summary>Raised on a background thread with progress messages (model download, loading).</summary>
    public event Action<string>? StatusChanged;

    /// <summary>Raised when the audio file being played reaches its end (or playback fails).</summary>
    public event Action<Exception?>? FileEnded;

    public bool IsRecording => _microphone != null || _player != null;
    public bool IsPlayingFile => _player != null;

    /// <summary>Length and current position of the audio file being played.</summary>
    public TimeSpan FileDuration { get; private set; }
    public TimeSpan FilePosition { get; private set; }

    public async Task StartAsync(string languageCode, string modelId)
    {
        if (IsRecording) return;
        await PrepareAsync(languageCode, modelId);

        var microphone = new WaveInEvent
        {
            WaveFormat = new WaveFormat(SampleRate, 16, 1),
            BufferMilliseconds = 100,
        };
        microphone.DataAvailable += OnAudio;
        microphone.StartRecording(); // throws when there is no usable microphone
        _microphone = microphone;

        _cancellation = new CancellationTokenSource();
        _worker = Task.Run(() => TranscribeLoopAsync(_cancellation.Token));
    }

    /// <summary>
    /// Plays an audio file (anything Windows can play, plus Ogg Opus) through the speakers and
    /// transcribes it as it plays, exactly like a dictation. The file is decoded as it plays.
    /// </summary>
    public async Task StartFileAsync(string path, string languageCode, string modelId)
    {
        if (IsRecording) return;
        await PrepareAsync(languageCode, modelId);

        StatusChanged?.Invoke("Opening the audio file…");
        var (audio, duration, handle) = await Task.Run(() => OpenAudio(path));
        FileDuration = duration;
        FilePosition = TimeSpan.Zero;

        var player = new WaveOutEvent { DesiredLatency = 300 };
        try
        {
            player.Init(new FileSource(audio, this).ToWaveProvider());
        }
        catch
        {
            player.Dispose();
            handle.Dispose();
            throw;
        }
        player.PlaybackStopped += (sender, e) =>
        {
            if (ReferenceEquals(sender, _player)) FileEnded?.Invoke(e.Exception);
        };
        _player = player;
        _fileHandle = handle;
        _cancellation = new CancellationTokenSource();
        _worker = Task.Run(() => TranscribeLoopAsync(_cancellation.Token));
        player.Play();
    }

    private async Task PrepareAsync(string languageCode, string modelId)
    {
        await EnsureModelAsync(modelId);
        _processor?.Dispose();
        _processor = _factory!.CreateBuilder()
            .WithLanguage(languageCode)
            .WithThreads(Math.Clamp(Environment.ProcessorCount / 2, 2, 8))
            .Build();

        _committed.Clear();
        lock (_gate)
        {
            _utterance.Clear();
            _speechEnd = 0;
        }
    }

    /// <summary>Stops the microphone or the playback and returns the complete text so far.</summary>
    public async Task<string> StopAsync()
    {
        if (!IsRecording) return "";

        if (_microphone != null)
        {
            var stopped = new TaskCompletionSource();
            _microphone.RecordingStopped += (_, _) => stopped.TrySetResult();
            _microphone.StopRecording();
            await Task.WhenAny(stopped.Task, Task.Delay(2000));
            _microphone.Dispose();
            _microphone = null;
        }
        if (_player != null)
        {
            var player = _player;
            _player = null; // so that the stop does not count as the file ending
            player.Stop();
            player.Dispose();
            _fileHandle?.Dispose();
            _fileHandle = null;
        }

        _cancellation!.Cancel();
        try { await _worker!; } catch (OperationCanceledException) { }
        _cancellation.Dispose();
        _cancellation = null;

        // Whatever was said after the last committed pause.
        float[] rest;
        bool hasSpeech;
        lock (_gate)
        {
            rest = _utterance.ToArray();
            hasSpeech = _speechEnd > 0;
            _utterance.Clear();
            _speechEnd = 0;
        }
        if (hasSpeech) Commit(await TranscribeAsync(rest, CancellationToken.None));

        return _committed.ToString().Trim();
    }

    private void OnAudio(object? sender, WaveInEventArgs e)
    {
        int count = e.BytesRecorded / 2;
        if (count == 0) return;

        var samples = new float[count];
        for (int i = 0; i < count; i++)
            samples[i] = BitConverter.ToInt16(e.Buffer, i * 2) / 32768f;
        Feed(samples);
    }

    /// <summary>Adds a block of 16 kHz samples (about 100 ms) to the utterance in progress.</summary>
    private void Feed(ReadOnlySpan<float> samples)
    {
        if (samples.Length == 0) return;
        double energy = 0;
        foreach (var sample in samples) energy += sample * sample;
        float level = (float)Math.Sqrt(energy / samples.Length);

        lock (_gate)
        {
            // Voice detection relative to the background noise, which is tracked during quiet frames.
            float threshold = Math.Clamp(_noiseLevel * 3f, 0.006f, 0.05f);
            _utterance.AddRange(samples);
            if (level > threshold) _speechEnd = _utterance.Count;
            else _noiseLevel = _noiseLevel * 0.95f + level * 0.05f;
        }
    }

    /// <summary>
    /// Pulls 16 kHz samples from the decoder as the sound card needs them and, on the way,
    /// feeds them to the transcriber. Playback therefore paces the decoding and the recognition.
    /// </summary>
    private sealed class FileSource(ISampleProvider audio, SpeechTranscriber owner) : ISampleProvider
    {
        private const int BlockSize = SampleRate / 10; // feed in 100 ms blocks like the microphone
        private long _position;

        public WaveFormat WaveFormat => audio.WaveFormat;

        public int Read(float[] buffer, int offset, int count)
        {
            int read = audio.Read(buffer, offset, count);
            for (int done = 0; done < read; done += BlockSize)
                owner.Feed(new ReadOnlySpan<float>(buffer, offset + done, Math.Min(BlockSize, read - done)));
            _position += read;
            owner.FilePosition = TimeSpan.FromSeconds(_position / (double)SampleRate);
            return read;
        }
    }

    private async Task TranscribeLoopAsync(CancellationToken cancellation)
    {
        int previewedLength = 0;
        while (!cancellation.IsCancellationRequested)
        {
            await Task.Delay(300, cancellation);

            float[] audio;
            int silenceMs;
            lock (_gate)
            {
                if (_speechEnd == 0)
                {
                    // Nothing but silence so far: keep only a short lead-in.
                    int keep = SampleRate * 3 / 10;
                    if (_utterance.Count > keep) _utterance.RemoveRange(0, _utterance.Count - keep);
                    previewedLength = 0;
                    continue;
                }
                audio = _utterance.ToArray();
                silenceMs = (audio.Length - _speechEnd) * 1000 / SampleRate;
            }

            bool commit = silenceMs >= PauseToCommitMs || audio.Length > SampleRate * MaxUtteranceSeconds;
            if (!commit && audio.Length == previewedLength) continue;

            var text = await TranscribeAsync(audio, cancellation);
            if (commit)
            {
                Commit(text);
                lock (_gate)
                {
                    _utterance.RemoveRange(0, audio.Length);
                    _speechEnd = Math.Max(0, _speechEnd - audio.Length);
                }
                previewedLength = 0;
                TextChanged?.Invoke(_committed.ToString());
            }
            else
            {
                previewedLength = audio.Length;
                TextChanged?.Invoke(Join(_committed.ToString(), text));
            }
        }
    }

    private void Commit(string text)
    {
        var joined = Join(_committed.ToString(), text);
        _committed.Clear().Append(joined);
    }

    private static string Join(string first, string second) =>
        first.Length == 0 ? second : second.Length == 0 ? first : first + " " + second;

    private async Task<string> TranscribeAsync(float[] audio, CancellationToken cancellation)
    {
        // Whisper rejects clips shorter than a second.
        int minimum = SampleRate * 5 / 4;
        if (audio.Length < minimum) Array.Resize(ref audio, minimum);

        var text = new StringBuilder();
        await foreach (var segment in _processor!.ProcessAsync(audio, cancellation))
            text.Append(segment.Text);
        return Clean(text.ToString());
    }

    /// <summary>Drops annotations such as [BLANK_AUDIO] or (music) and tidies the spacing.</summary>
    private static string Clean(string text) => Whitespace.Replace(NonSpeechTags.Replace(text, " "), " ").Trim();

    /// <summary>Opens a file as a 16 kHz mono stream for Whisper, with its duration and the handle to dispose after.</summary>
    private static (ISampleProvider Audio, TimeSpan Duration, IDisposable Handle) OpenAudio(string path)
    {
        // Windows cannot decode Ogg Opus (WhatsApp voice notes, for instance); our own reader can.
        if (IsOgg(path))
        {
            var ogg = new OggOpusReader(path);
            return (ToWhisperFormat(ogg), ogg.Duration, ogg);
        }
        var reader = new MediaFoundationReader(path);
        try
        {
            return (ToWhisperFormat(reader.ToSampleProvider()), reader.TotalTime, reader);
        }
        catch
        {
            reader.Dispose();
            throw;
        }
    }

    private static bool IsOgg(string path)
    {
        using var stream = File.OpenRead(path);
        Span<byte> magic = stackalloc byte[4];
        return stream.Read(magic) == 4 && magic.SequenceEqual("OggS"u8);
    }

    private static ISampleProvider ToWhisperFormat(ISampleProvider provider)
    {
        if (provider.WaveFormat.Channels == 2) provider = provider.ToMono();
        else if (provider.WaveFormat.Channels > 2)
            throw new InvalidOperationException("Files with more than two audio channels are not supported.");
        if (provider.WaveFormat.SampleRate != SampleRate)
            provider = new WdlResamplingSampleProvider(provider, SampleRate);
        return provider;
    }

    private async Task EnsureModelAsync(string modelId)
    {
        if (_factory != null && _factoryModel == modelId) return;

        var folder = Directory.CreateDirectory(Path.Combine(AppPaths.DataFolder, "models")).FullName;
        var path = Path.Combine(folder, $"ggml-{modelId}.bin");
        if (!File.Exists(path))
        {
            var type = modelId == "small" ? GgmlType.Small : GgmlType.Base;
            var partial = path + ".download";
            using (var source = await WhisperGgmlDownloader.Default.GetGgmlModelAsync(type))
            using (var target = File.Create(partial))
            {
                var buffer = new byte[1 << 20];
                long total = 0, reported = 0;
                int read;
                while ((read = await source.ReadAsync(buffer)) > 0)
                {
                    await target.WriteAsync(buffer.AsMemory(0, read));
                    total += read;
                    if (total - reported >= 4 << 20)
                    {
                        reported = total;
                        StatusChanged?.Invoke($"Downloading speech model (one time)… {total >> 20} MB");
                    }
                }
            }
            File.Move(partial, path, overwrite: true);
        }

        StatusChanged?.Invoke("Loading speech model…");
        var factory = await Task.Run(() => WhisperFactory.FromPath(path));
        _processor?.Dispose();
        _processor = null;
        _factory?.Dispose();
        _factory = factory;
        _factoryModel = modelId;
    }

    public void Dispose()
    {
        _cancellation?.Cancel();
        _microphone?.Dispose();
        _microphone = null;
        _player?.Dispose();
        _player = null;
        _fileHandle?.Dispose();
        _fileHandle = null;
        _processor?.Dispose();
        _factory?.Dispose();
    }
}
