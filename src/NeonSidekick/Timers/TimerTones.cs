using NeonSidekick.Audio;

namespace NeonSidekick.Timers;

/// <summary>
/// The setting <c>Timer tone</c> (2026-10-18, the user's ask: with TTS off a timer going off was silent): the tone a countdown
/// rings with when it goes off and at each repeat, or <see cref="Silent"/>, the default and the behavior until then. The
/// <c>CameraShutterMode</c> shape: <see cref="Names"/> in menu order, a hint per name, <see cref="Normalize"/> the one place the
/// saved string becomes a name. <see cref="Render"/> draws each tone as 16-bit PCM: a sum of sines under an envelope, one fixed
/// level (no volume setting, the user's pick), every clip led by <see cref="LeadSeconds"/> of silence (an output waking from idle,
/// or a Bluetooth headset, can drop the first moment of sound, which would clip the chime's first note), every note faded in and
/// out over <see cref="FadeSeconds"/> so it never clicks, and the whole scaled to peak at <see cref="Peak"/> of full scale
/// (about −12 dBFS). No clip runs past <see cref="MaxSeconds"/>, the lead included. Pure.
/// </summary>
public static class TimerTones
{
    public const string Silent = "silent";
    public const string Chime = "chime";
    public const string Bell = "bell";
    public const string Beep = "beep";
    public const string Alarm = "alarm";

    public const string Default = Silent;

    /// <summary>The tones in menu order.</summary>
    public static readonly string[] Names = [Silent, Chime, Bell, Beep, Alarm];

    /// <summary>The peak every tone is scaled to, as a share of full scale (about −12 dBFS).</summary>
    public const double Peak = 0.25;

    /// <summary>The silence ahead of every clip.</summary>
    public const double LeadSeconds = 0.05;

    /// <summary>Each note's fade in and out.</summary>
    public const double FadeSeconds = 0.005;

    /// <summary>The longest clip, its lead included.</summary>
    public const double MaxSeconds = 1.35;

    // The table (2026-10-18): the notes and their decay. Chime: E6 then C6, each struck and dying away. Bell: A5 with its octave
    // and twelfth (amplitudes 1, 0.5, 0.25), one long decay. Beep: three 1 kHz beeps with gaps. Alarm: A5 and E5 alternating.
    private const double E6 = 1318.5, C6 = 1046.5, A5 = 880.0, E5 = 660.0, Kilohertz = 1000.0;
    private const double ChimeFirst = 0.35, ChimeSecond = 0.45, ChimeDecay = 0.18;
    private const double BellLength = 1.2, BellDecay = 0.35;
    private const double BeepLength = 0.12, BeepGap = 0.1;
    private const int BeepCount = 3;
    private const double AlarmLength = 1.2, AlarmStep = 0.15;

    /// <summary>The menu hint next to a tone. Pinned.</summary>
    public static string Describe(string name) => name switch
    {
        Silent => "no sound",
        Chime => "a soft two-note ding-dong",
        Bell => "one struck bell",
        Beep => "three short beeps",
        Alarm => "an insistent two-tone alarm",
        _ => "",
    };

    /// <summary>The saved value as one of <see cref="Names"/>, any case; an unknown or empty value is <see cref="Silent"/>, so a hand-edited profile never crashes or blares.</summary>
    public static string Normalize(string? name)
    {
        string trimmed = name?.Trim() ?? "";
        foreach (string known in Names)
        {
            if (string.Equals(known, trimmed, StringComparison.OrdinalIgnoreCase))
            {
                return known;
            }
        }

        return Silent;
    }

    /// <summary>
    /// The tone <paramref name="name"/> (normalized first) as 16-bit little-endian PCM in <paramref name="format"/>, every channel
    /// the same; empty for <see cref="Silent"/>. Whole frames, at most <see cref="MaxSeconds"/>.
    /// </summary>
    public static byte[] Render(string name, PcmFormat format)
    {
        if (format.BitsPerSample != 16 || format.Channels < 1 || format.SampleRate < 8000)
        {
            throw new ArgumentException("A timer tone renders 16-bit PCM at 8 kHz or more.", nameof(format));
        }

        int rate = format.SampleRate;
        float[]? samples = Normalize(name) switch
        {
            Chime => Chimed(rate),
            Bell => Belled(rate),
            Beep => Beeped(rate),
            Alarm => Alarmed(rate),
            _ => null,
        };
        if (samples is null)
        {
            return [];
        }

        float loudest = 0f;
        foreach (float s in samples)
        {
            loudest = Math.Max(loudest, Math.Abs(s));
        }

        double scale = loudest > 0f ? Peak / loudest : 0;
        var pcm = new byte[samples.Length * format.BlockAlign];
        int at = 0;
        foreach (float s in samples)
        {
            short value = (short)Math.Round(s * scale * short.MaxValue);
            for (int c = 0; c < format.Channels; c++)
            {
                pcm[at++] = (byte)value;
                pcm[at++] = (byte)(value >> 8);
            }
        }

        return pcm;
    }

    private static float[] Clip(int rate, double seconds) => new float[(int)Math.Round((LeadSeconds + seconds) * rate)];

    /// <summary>One note into <paramref name="clip"/> at <paramref name="start"/> seconds after the lead: the partials summed, faded in and out, under an exponential decay (none for 0).</summary>
    private static void Note(float[] clip, int rate, double start, double seconds, double decay, params (double Hertz, double Amplitude)[] partials)
    {
        int first = (int)Math.Round((LeadSeconds + start) * rate);
        int count = Math.Min((int)Math.Round(seconds * rate), clip.Length - first);
        int fade = Math.Max(1, (int)Math.Round(FadeSeconds * rate));
        for (int i = 0; i < count; i++)
        {
            double t = (double)i / rate;
            double envelope = Math.Min(1.0, Math.Min((double)i / fade, (double)(count - 1 - i) / fade));
            if (decay > 0)
            {
                envelope *= Math.Exp(-t / decay);
            }

            double sum = 0;
            foreach (var (hertz, amplitude) in partials)
            {
                sum += amplitude * Math.Sin(2 * Math.PI * hertz * t);
            }

            clip[first + i] += (float)(envelope * sum);
        }
    }

    private static float[] Chimed(int rate)
    {
        var clip = Clip(rate, ChimeFirst + ChimeSecond);
        Note(clip, rate, 0, ChimeFirst, ChimeDecay, (E6, 1));
        Note(clip, rate, ChimeFirst, ChimeSecond, ChimeDecay, (C6, 1));
        return clip;
    }

    private static float[] Belled(int rate)
    {
        var clip = Clip(rate, BellLength);
        Note(clip, rate, 0, BellLength, BellDecay, (A5, 1), (A5 * 2.0, 0.5), (A5 * 3.0, 0.25));
        return clip;
    }

    private static float[] Beeped(int rate)
    {
        var clip = Clip(rate, BeepCount * BeepLength + (BeepCount - 1) * BeepGap);
        for (int i = 0; i < BeepCount; i++)
        {
            Note(clip, rate, i * (BeepLength + BeepGap), BeepLength, 0, (Kilohertz, 1));
        }

        return clip;
    }

    private static float[] Alarmed(int rate)
    {
        var clip = Clip(rate, AlarmLength);
        int steps = (int)Math.Round(AlarmLength / AlarmStep);
        for (int i = 0; i < steps; i++)
        {
            Note(clip, rate, i * AlarmStep, AlarmStep, 0, (i % 2 == 0 ? A5 : E5, 1));
        }

        return clip;
    }
}
