using System.IO;
using NAudio.Wave;
using Windows.Media.SpeechSynthesis;

namespace HB.Speech;

public sealed record SpeakerVoice(VoiceInformation Info)
{
    public string Label => $"{Info.DisplayName.Replace("Microsoft ", "")} ({Info.Language})";

    public static IReadOnlyList<SpeakerVoice> All { get; } =
        SpeechSynthesizer.AllVoices.OrderBy(v => v.Language).ThenBy(v => v.DisplayName)
            .Select(v => new SpeakerVoice(v)).ToList();
}

/// <summary>Reads text out loud with the voices installed in Windows.</summary>
public sealed class TextSpeaker : IDisposable
{
    private readonly SpeechSynthesizer _synthesizer = new();
    private WaveOutEvent? _output;

    /// <summary>Raised on the UI thread when playback ends or is stopped.</summary>
    public event Action? Finished;

    public bool IsSpeaking => _output != null;

    public async Task SpeakAsync(string text, SpeakerVoice? voice)
    {
        Stop();
        if (voice != null) _synthesizer.Voice = voice.Info;

        var audio = new MemoryStream();
        using (var synthesized = await _synthesizer.SynthesizeTextToStreamAsync(text))
            await synthesized.AsStreamForRead().CopyToAsync(audio);
        audio.Position = 0;

        var reader = new WaveFileReader(audio);
        var output = new WaveOutEvent();
        output.Init(reader);
        output.PlaybackStopped += (_, _) =>
        {
            output.Dispose();
            reader.Dispose();
            if (_output != output) return; // superseded by a newer playback
            _output = null;
            Finished?.Invoke();
        };
        _output = output;
        output.Play();
    }

    public void Stop() => _output?.Stop();

    public void Dispose()
    {
        _output?.Dispose();
        _output = null;
        _synthesizer.Dispose();
    }
}
