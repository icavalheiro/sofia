using Avalonia.Threading;
using SoundFlow.Backends.MiniAudio;
using SoundFlow.Components;
using SoundFlow.Providers;
using SoundFlow.Structs;
using Sofia.App.Services;

namespace Sofia.App.Desktop;

/// <summary>
/// Desktop sound output: SoundFlow (miniaudio) playing the pre-rendered WAVs. The audio device is opened lazily on the
/// first sound and every failure degrades to silence - a machine without an audio device must never break the UI.
/// </summary>
public sealed class SoundFlowSoundPlayer : ISoundPlayer, IDisposable
{
    private static readonly AudioFormat Format = AudioFormat.Cd;

    private readonly object _gate = new();
    private MiniAudioEngine? _engine;
    private SoundFlow.Abstracts.Devices.AudioPlaybackDevice? _device;
    private bool _failed;

    public bool IsAvailable
    {
        get
        {
            EnsureDevice();
            return _device is not null;
        }
    }

    public void Play(ReadOnlyMemory<byte> wav, double gain)
    {
        EnsureDevice();
        if (_device is null || _engine is null) return;
        try
        {
            var provider = new AssetDataProvider(_engine, wav.ToArray());
            var player = new SoundPlayer(_engine, Format, provider)
            {
                Volume = (float)Math.Clamp(gain, 0, 4),
            };
            _device.MasterMixer.AddComponent(player);
            player.Play();

            // The mixer cannot drop a component while the audio graph processes it: remove it after the tail.
            _ = Task.Delay(900).ContinueWith(_ => Dispatcher.UIThread.Post(() =>
            {
                try
                {
                    _device.MasterMixer.RemoveComponent(player);
                    player.Dispose();
                    provider.Dispose();
                }
                catch
                {
                    // Device closed or already disposed: nothing to clean up.
                }
            }));
        }
        catch
        {
            // No device / driver error: the harness keeps running silently.
        }
    }

    private void EnsureDevice()
    {
        lock (_gate)
        {
            if (_engine is not null || _failed) return;
            try
            {
                _engine = new MiniAudioEngine();
                _device = _engine.InitializePlaybackDevice(null, Format);
                _device.Start();
            }
            catch
            {
                _failed = true;
                _device = null;
                try
                {
                    _engine?.Dispose();
                }
                catch
                {
                    // Already disposed.
                }
                _engine = null;
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            try
            {
                _device?.Dispose();
                _engine?.Dispose();
            }
            catch
            {
                // Shutdown best effort.
            }
            _device = null;
            _engine = null;
        }
    }
}

