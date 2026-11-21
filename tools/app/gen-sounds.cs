// Pre-renders the six Sofia UI sounds to WAV with the parameters of src/Sofia.UI/src/composables/useSound.ts
// (sine fundamental + 3rd partial at .12, 18 ms attack, exponential decay to .0001, lowpass 3.8 kHz Q .5, compressor
// threshold -12 dB / knee 8 / ratio 12 / attack 2 ms / release 120 ms). Master gain is baked in at the default volume
// (60 -> (60/100)^1.6*0.9 = .4029); SoundService scales the ratio for other volumes. Local helper (spec 039).
//
// Run from the repository root: dotnet run tools/app/gen-sounds.cs

using System.Text;

var outDir = Path.Combine(Directory.GetCurrentDirectory(), "src", "Sofia.App", "Assets", "Sounds");
Directory.CreateDirectory(outDir);
var defaultVolumeGain = Math.Pow(60 / 100.0, 1.6) * 0.9;

var sounds = new (string Name, double Seconds, Action<double[]> Build)[]
{
    ("click", 0.10, m => Synth.AddKnock(m, 0.045, 0.022)),
    ("select", 0.22, m =>
    {
        Synth.AddNote(m, 440, 0, 0.16, 0.13);
        Synth.AddKnock(m, 0.03, 0.02);
    }),
    ("send", 0.28, m =>
    {
        Synth.AddNote(m, 329.63, 0, 0.18, 0.11);
        Synth.AddNote(m, 440, 0.055, 0.18, 0.11);
    }),
    ("done", 0.55, m =>
    {
        Synth.AddNote(m, 440, 0, 0.42, 0.09);
        Synth.AddNote(m, 523.25, 0.04, 0.42, 0.11);
    }),
    ("error", 0.58, m =>
    {
        Synth.AddNote(m, 392, 0, 0.26, 0.12);
        Synth.AddNote(m, 329.63, 0.09, 0.26, 0.12);
        Synth.AddNote(m, 220, 0.18, 0.26, 0.12, -8);
    }),
    ("permission", 0.60, m =>
    {
        Synth.AddNote(m, 440, 0, 0.5, 0.10);
        Synth.AddNote(m, 659.25, 0, 0.5, 0.07, 6);
    }),
};

foreach (var (name, seconds, build) in sounds)
{
    var buffer = Synth.Buffer(seconds);
    build(buffer);
    var wav = Synth.Render(buffer, defaultVolumeGain);
    File.WriteAllBytes(Path.Combine(outDir, name + ".wav"), wav);
    Console.WriteLine($"{name}.wav {wav.Length / 1024.0:F1} kB ({seconds * 1000:F0} ms)");
}

/// <summary>Offline marimba synth: note voices, noise knock, lowpass, master gain and a peak compressor.</summary>
internal static class Synth
{
    public const int SampleRate = 44100;
    public const double Silence = 0.0001;

    public static double NoteFreq(double hertz, double cents = 0) => hertz * Math.Pow(2, cents / 1200.0);

    public static void AddNote(double[] mono, double freq, double at, double dur, double peak, double cents = 0)
    {
        var start = (int)(at * SampleRate);
        var length = (int)((dur + 0.05) * SampleRate);
        var f = NoteFreq(freq, cents);
        for (var i = 0; i < length && start + i < mono.Length; i++)
        {
            var t = i / (double)SampleRate;
            double gain;
            if (t < 0.018) gain = peak * (t / 0.018);
            else if (t < dur) gain = peak * Math.Pow(Silence / peak, (t - 0.018) / (dur - 0.018));
            else gain = t < dur + 0.02 ? Math.Max(0, Silence * (1 - (t - dur) / 0.02)) : 0;

            var fundamental = Math.Sin(2 * Math.PI * f * t);
            var third = Math.Sin(2 * Math.PI * f * 3 * t) * 0.12;
            mono[start + i] += (fundamental + third) * gain;
        }
    }

    public static void AddKnock(double[] mono, double peak, double dur, double freq = 1500, double q = 0.9)
    {
        var rng = new Random(20261120);
        var size = Math.Max(1, (int)(SampleRate * dur));
        var noise = new double[size];
        for (var i = 0; i < size; i++) noise[i] = (rng.NextDouble() * 2 - 1) * (1 - (double)i / size);
        noise = Biquad(noise, Bandpass(freq, q));
        for (var i = 0; i < size && i < mono.Length; i++)
            mono[i] += noise[i] * peak * (1 - (double)i / size);
    }

    public static double[] Biquad(double[] input, (double B0, double B1, double B2, double A0, double A1, double A2) c)
    {
        var output = new double[input.Length];
        double x1 = 0, x2 = 0, y1 = 0, y2 = 0;
        for (var i = 0; i < input.Length; i++)
        {
            var x0 = input[i];
            var y0 = (c.B0 * x0 + c.B1 * x1 + c.B2 * x2 - c.A1 * y1 - c.A2 * y2) / c.A0;
            x2 = x1; x1 = x0; y2 = y1; y1 = y0;
            output[i] = y0;
        }
        return output;
    }

    public static (double, double, double, double, double, double) Lowpass(double freq, double q)
    {
        var w0 = 2 * Math.PI * freq / SampleRate;
        var cos = Math.Cos(w0);
        var alpha = Math.Sin(w0) / (2 * q);
        return ((1 - cos) / 2, 1 - cos, (1 - cos) / 2, 1 + alpha, -2 * cos, 1 - alpha);
    }

    public static (double, double, double, double, double, double) Bandpass(double freq, double q)
    {
        var w0 = 2 * Math.PI * freq / SampleRate;
        var cos = Math.Cos(w0);
        var alpha = Math.Sin(w0) / (2 * q);
        return (alpha, 0, -alpha, 1 + alpha, -2 * cos, 1 - alpha);
    }

    /// <summary>Feed-forward peak compressor approximating DynamicsCompressorNode(threshold -12, knee 8, ratio 12).</summary>
    public static double[] Compress(double[] input)
    {
        const double thresholdDb = -12, kneeDb = 8, ratio = 12, attackMs = 2, releaseMs = 120;
        var attack = Math.Exp(-1.0 / (SampleRate * attackMs / 1000.0));
        var release = Math.Exp(-1.0 / (SampleRate * releaseMs / 1000.0));
        var output = new double[input.Length];
        var envelope = 0.0;
        for (var i = 0; i < input.Length; i++)
        {
            var level = Math.Abs(input[i]);
            envelope = level > envelope ? attack * envelope + (1 - attack) * level : release * envelope + (1 - release) * level;
            var levelDb = envelope <= 1e-9 ? -120 : 20 * Math.Log10(envelope);
            var over = levelDb - thresholdDb;
            double reducedDb;
            if (over <= -kneeDb / 2) reducedDb = 0;
            else if (over >= kneeDb / 2) reducedDb = over - over / ratio;
            else
            {
                var x = over + kneeDb / 2;
                reducedDb = (1 / ratio - 1) * x * x / (2 * kneeDb) + over / ratio;
            }
            output[i] = input[i] * Math.Pow(10, -reducedDb / 20);
        }
        return output;
    }

    /// <summary>Stereo 16-bit PCM WAV with lowpass + master gain + compressor applied (normalised only if it clips).</summary>
    public static byte[] Render(double[] mono, double masterGain)
    {
        var filtered = Biquad(mono, Lowpass(3800, 0.5));
        for (var i = 0; i < filtered.Length; i++) filtered[i] *= masterGain;
        var compressed = Compress(filtered);
        var peak = compressed.Max(Math.Abs);
        if (peak > 0.999) for (var i = 0; i < compressed.Length; i++) compressed[i] /= peak;

        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.ASCII, true);
        var dataBytes = compressed.Length * 2 * 2;
        writer.Write(Encoding.ASCII.GetBytes("RIFF"));
        writer.Write(36 + dataBytes);
        writer.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)2);
        writer.Write(SampleRate);
        writer.Write(SampleRate * 2 * 2);
        writer.Write((short)(2 * 2));
        writer.Write((short)16);
        writer.Write(Encoding.ASCII.GetBytes("data"));
        writer.Write(dataBytes);
        foreach (var sample in compressed)
        {
            var value = (short)Math.Round(Math.Clamp(sample, -1, 1) * short.MaxValue);
            writer.Write(value);
            writer.Write(value);
        }
        writer.Flush();
        return stream.ToArray();
    }

    public static double[] Buffer(double seconds) => new double[(int)(seconds * SampleRate)];
}
