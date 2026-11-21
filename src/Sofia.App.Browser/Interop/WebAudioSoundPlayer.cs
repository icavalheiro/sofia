using Sofia.App.Services;

namespace Sofia.App.Browser;

/// <summary>
/// Browser sound output: the six pre-rendered WAVs played through WebAudio (<c>AudioContext</c> +
/// <c>GainNode</c>). The gain is the one <see cref="SoundService"/> computed with the web's
/// <c>(volume/100)^1.6*0.9</c> curve, so the browser and the desktop make the same noise at the same volume.
/// Playback never throws into the UI: a browser without audio, or a blocked autoplay policy, is simply silent.
/// </summary>
internal sealed class WebAudioSoundPlayer : ISoundPlayer
{
    public bool IsAvailable => true;

    public void Play(ReadOnlyMemory<byte> wav, double gain)
    {
        try
        {
            BrowserInterop.PlayWav(Convert.ToBase64String(wav.Span), gain);
        }
        catch
        {
            // Silence is preferable to a broken click handler.
        }
    }
}
