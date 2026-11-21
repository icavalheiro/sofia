using System.Diagnostics;
using Avalonia.Platform;
using CommunityToolkit.Mvvm.ComponentModel;
using Sofia.App.Theme;

namespace Sofia.App.Services;

/// <summary>The six synthesized UI sounds, in the web's declaration order.</summary>
public enum SoundKey
{
    Click,
    Select,
    Send,
    Done,
    Permission,
    Error,
}

/// <summary>
/// Plays a pre-rendered WAV at a gain. Implemented by the head (SoundFlow on desktop, WebAudio/HTML audio in the browser
/// head, spec 044) and never by <c>Sofia.App</c>, which must stay free of platform audio packages.
/// </summary>
public interface ISoundPlayer
{
    bool IsAvailable { get; }

    /// <summary>Starts playback without blocking; implementations must never throw into the UI.</summary>
    void Play(ReadOnlyMemory<byte> wav, double gain);
}

/// <summary>Silent player used before a real one is installed and whenever audio is unavailable.</summary>
public sealed class NullSoundPlayer : ISoundPlayer
{
    public bool IsAvailable => false;

    public void Play(ReadOnlyMemory<byte> wav, double gain)
    {
    }
}

/// <summary>
/// The web's <c>useSound.ts</c> behaviour on top of pre-rendered WAVs: the same six triggers, the same
/// <c>(volume/100)^1.6*0.9</c> master curve, a 45 ms per-sound retrigger guard and at most six simultaneous voices.
/// The WAVs are rendered at the default volume (60), so playback applies the ratio of the current curve to that default.
/// </summary>
public sealed class SoundService : ObservableObject
{
    /// <summary>Master gain baked into the WAV assets: (60/100)^1.6 * .9 ≈ 0.39745.</summary>
    public static readonly double DefaultVolumeGain = Math.Pow(DefaultVolume / 100.0, 1.6) * 0.9;

    public const double DefaultVolume = 60;

    private static readonly TimeSpan RetriggerGuard = TimeSpan.FromMilliseconds(45);
    private const int MaxVoices = 6;

    private readonly Dictionary<SoundKey, byte[]> _cache = [];
    private readonly Dictionary<SoundKey, double> _lastPlayed = [];
    private readonly Queue<double> _activeVoices = new();
    private ISoundPlayer _player = new NullSoundPlayer();
    private bool _enabled = true;
    private double _volume = DefaultVolume;

    public static SoundService Current { get; } = new();

    /// <summary>Sound effects switch (Settings → General, default on).</summary>
    public bool Enabled
    {
        get => _enabled;
        set => SetProperty(ref _enabled, value);
    }

    /// <summary>0-100; default 60.</summary>
    public double Volume
    {
        get => _volume;
        set
        {
            var clamped = Math.Clamp(value, 0, 100);
            if (SetProperty(ref _volume, clamped)) OnPropertyChanged(nameof(Gain));
        }
    }

    public bool IsAvailable => _player.IsAvailable;

    /// <summary>Gain applied to playback: the web curve divided by the curve already baked into the assets.</summary>
    public double Gain => VolumeToGain(Volume) / DefaultVolumeGain;

    /// <summary>Installs the head's player (desktop: SoundFlow). Called once at startup.</summary>
    public void Use(ISoundPlayer player) => _player = player ?? new NullSoundPlayer();

    /// <summary>Same curve as <c>volumeToGain</c> in useSound.ts.</summary>
    public static double VolumeToGain(double volume)
    {
        var clamped = Math.Clamp(double.IsFinite(volume) ? volume : 0, 0, 100);
        return Math.Pow(clamped / 100.0, 1.6) * 0.9;
    }

    public void Play(SoundKey key)
    {
        if (!Enabled || !_player.IsAvailable) return;

        var now = Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency * 1000;
        if (_lastPlayed.TryGetValue(key, out var last) && now - last < RetriggerGuard.TotalMilliseconds) return;
        PruneVoices(now);
        if (_activeVoices.Count >= MaxVoices) return;

        var wav = Load(key);
        if (wav is null) return;

        _lastPlayed[key] = now;
        _activeVoices.Enqueue(now + DurationMs(key));
        _player.Play(wav, Gain);
    }

    /// <summary>Cycles all six sounds 420 ms apart (Settings → General → preview).</summary>
    public async Task PreviewAsync(CancellationToken ct = default)
    {
        foreach (var key in Enum.GetValues<SoundKey>())
        {
            ct.ThrowIfCancellationRequested();
            _lastPlayed.Remove(key);
            Play(key);
            await Task.Delay(420, ct);
        }
    }

    /// <summary>Asset path of a sound, as embedded in Sofia.App.</summary>
    public static string AssetPath(SoundKey key) =>
        $"avares://Sofia.App/Assets/Sounds/{key.ToString().ToLowerInvariant()}.wav";

    private static byte[]? Load(SoundKey key)
    {
        try
        {
            using var stream = AssetLoader.Open(new Uri(AssetPath(key)));
            using var memory = new MemoryStream();
            stream.CopyTo(memory);
            return memory.ToArray();
        }
        catch
        {
            return null;
        }
    }

    private static double DurationMs(SoundKey key) => key switch
    {
        SoundKey.Click => 100,
        SoundKey.Select => 220,
        SoundKey.Send => 280,
        SoundKey.Done => 550,
        SoundKey.Error => 580,
        SoundKey.Permission => 600,
        _ => 600,
    };

    private void PruneVoices(double now)
    {
        while (_activeVoices.Count > 0 && _activeVoices.Peek() <= now) _activeVoices.Dequeue();
    }
}
