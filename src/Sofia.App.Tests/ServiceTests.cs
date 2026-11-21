using System.Text.Json;
using Avalonia.Headless.XUnit;
using Avalonia.Platform;
using Sofia.App.Services;
using Sofia.Contracts.Domain;
using Sofia.Contracts.Events;

namespace Sofia.App.Tests;

/// <summary>Non-visual services: local state, the store's delta coalescing and the sound pipeline.</summary>
public class ServiceTests
{
    [Fact]
    public void Local_state_round_trips_and_recovers_from_corruption()
    {
        var directory = Path.Combine(Path.GetTempPath(), "sofia-app-state-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "state.json");

        var state = new LocalState(path);
        state.Set(LocalState.DeliveryKey, "steer");
        state.SetFlag(LocalState.RightPanelKey, "s_1", true);
        state.SetFlag(LocalState.RightPanelKey, "s_2", false);
        state.SaveNow();

        var reloaded = new LocalState(path);
        Assert.Equal("steer", reloaded.Get<string>(LocalState.DeliveryKey));
        Assert.True(reloaded.GetFlag(LocalState.RightPanelKey, "s_1"));
        Assert.False(reloaded.GetFlag(LocalState.RightPanelKey, "s_2"));
        Assert.False(reloaded.RecoveredFromCorruption);

        File.WriteAllText(path, "{ not json");
        var recovered = new LocalState(path);
        Assert.True(recovered.RecoveredFromCorruption);
        Assert.Empty(recovered.Values);
        Assert.Single(Directory.GetFiles(directory, "state.corrupt-*.json"));

        Directory.Delete(directory, recursive: true);
    }

    [AvaloniaFact]
    public void Store_coalesces_message_deltas_per_message()
    {
        var store = new HarnessStore();
        var flushed = new List<(string Id, string Text)>();
        store.DeltaFlushed += (id, text) => flushed.Add((id, text));

        foreach (var chunk in new[] { "Hel", "lo", " there" })
        {
            store.Apply(Delta("msg_1", chunk));
        }
        Assert.Single(store.PendingDeltas);
        Assert.Equal("Hello there", store.PendingDeltas["msg_1"]);

        store.FlushDeltas();
        Assert.Empty(store.PendingDeltas);
        Assert.Equal([("msg_1", "Hello there")], flushed);

        // A completed message supersedes the pending text.
        store.Apply(Delta("msg_2", "partial"));
        store.Apply(new SofiaEventDto(
            EventTypes.MessageUpdated,
            "s_1",
            "ws_1",
            JsonSerializer.SerializeToElement(new { id = "msg_2" })));
        Assert.Empty(store.PendingDeltas);
    }

    [AvaloniaFact]
    public void Store_tracks_session_status_and_workspace_deletion()
    {
        var store = new HarnessStore();
        store.Apply(new SofiaEventDto(
            EventTypes.WorkspaceUpdated,
            null,
            "ws_1",
            JsonSerializer.SerializeToElement(new
            {
                id = "ws_1",
                name = "sofia",
                path = "/repo",
                conversations = Array.Empty<object>(),
            })));
        Assert.Single(store.Workspaces);

        store.Apply(new SofiaEventDto(
            EventTypes.SessionStatus,
            "s_1",
            "ws_1",
            JsonSerializer.SerializeToElement(new { status = "working" })));
        Assert.Equal("working", store.Statuses["s_1"]);

        store.Apply(new SofiaEventDto(
            EventTypes.WorkspaceDeleted,
            null,
            "ws_1",
            JsonSerializer.SerializeToElement(new { id = "ws_1" })));
        Assert.Empty(store.Workspaces);
    }

    [AvaloniaFact]
    public void Sound_volume_curve_matches_useSound()
    {
        Assert.Equal(0, SoundService.VolumeToGain(0));
        Assert.Equal(0.9, SoundService.VolumeToGain(100), 4);
        Assert.Equal(0.39745, SoundService.VolumeToGain(60), 4);
        Assert.Equal(SoundService.VolumeToGain(60), SoundService.DefaultVolumeGain, 6);

        var sound = SoundService.Current;
        sound.Volume = 60;
        Assert.Equal(1.0, sound.Gain, 0.01);
        sound.Volume = 100;
        Assert.Equal(0.9 / SoundService.DefaultVolumeGain, sound.Gain, 0.01);
        sound.Volume = 60;
    }

    [AvaloniaFact]
    public void Six_sound_assets_are_embedded()
    {
        foreach (var key in Enum.GetValues<SoundKey>())
        {
            using var stream = AssetLoader.Open(new Uri(SoundService.AssetPath(key)));
            Assert.True(stream.Length > 10_000, $"{key}: {stream.Length} bytes");
        }
        Assert.Equal(6, Enum.GetValues<SoundKey>().Length);
    }

    [AvaloniaFact]
    public void Sound_service_plays_once_per_key_and_respects_the_retrigger_guard()
    {
        var player = new FakeSoundPlayer();
        var sound = SoundService.Current;
        sound.Use(player);
        sound.Enabled = true;
        sound.Volume = 60;

        foreach (var key in Enum.GetValues<SoundKey>())
        {
            sound.Play(key);
            sound.Play(key); // same instant -> swallowed by the 45ms guard
        }

        Assert.Equal(6, player.Calls);
        Assert.Equal(1.0, player.LastGain, 2);
        Assert.True(player.LastLength > 10_000);

        sound.Enabled = false;
        sound.Play(SoundKey.Click);
        Assert.Equal(6, player.Calls);

        sound.Enabled = true;
    }

    private static SofiaEventDto Delta(string messageId, string text) => new(
        EventTypes.MessageDelta,
        "s_1",
        "ws_1",
        JsonSerializer.SerializeToElement(new { messageId, field = "text", delta = text }));

    private sealed class FakeSoundPlayer : ISoundPlayer
    {
        public int Calls { get; private set; }

        public double LastGain { get; private set; }

        public int LastLength { get; private set; }

        public bool IsAvailable => true;

        public void Play(ReadOnlyMemory<byte> wav, double gain)
        {
            Calls++;
            LastGain = gain;
            LastLength = wav.Length;
        }
    }
}
