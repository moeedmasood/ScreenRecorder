namespace ScreenRecorder.Core;

/// <summary>Mixes interleaved float samples with per-input gain and hard clipping.</summary>
public static class AudioMath
{
    public static void MixInto(float[] destination, float[] source, int count, float gain)
    {
        for (int i = 0; i < count; i++) destination[i] += source[i] * gain;
    }

    public static void Clip(float[] buffer, int count)
    {
        for (int i = 0; i < count; i++)
            buffer[i] = Math.Clamp(buffer[i], -1f, 1f);
    }

    public static void ToPcm16(float[] source, int count, byte[] destination)
    {
        for (int i = 0; i < count; i++)
        {
            short s = (short)Math.Round(Math.Clamp(source[i], -1f, 1f) * short.MaxValue);
            destination[i * 2] = (byte)(s & 0xFF);
            destination[i * 2 + 1] = (byte)((s >> 8) & 0xFF);
        }
    }

    /// <summary>Number of stereo sample frames due after <paramref name="elapsedHns"/>.</summary>
    public static long FramesDue(long elapsedHns, int sampleRate) => elapsedHns * sampleRate / 10_000_000;
}
