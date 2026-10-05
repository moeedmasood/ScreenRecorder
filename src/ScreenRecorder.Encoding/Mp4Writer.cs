using Vortice.MediaFoundation;

namespace ScreenRecorder.Encoding;

/// <summary>H.264/AAC MP4 writer on the Media Foundation sink writer. Thread-safe.</summary>
public sealed class Mp4Writer : IDisposable
{
    private readonly object _gate = new();
    private IMFSinkWriter? _writer;
    private int _videoStream, _audioStream;
    private bool _finished;
    private readonly int _width, _height;

    public Mp4Writer(string path, int width, int height, int fps, int videoBitrate, int audioBitrate,
        int sampleRate, int channels, bool withAudio)
    {
        if (width % 16 != 0 || height % 2 != 0 || width < 16 || height < 2)
            throw new ArgumentException($"Unsupported frame size {width}x{height}: width must be a multiple of 16 and height even.");
        _width = width;
        _height = height;
        MediaFactory.MFStartup().CheckError();

        using var attrs = MediaFactory.MFCreateAttributes(4);
        attrs.Set(SinkWriterAttributeKeys.ReadwriteEnableHardwareTransforms, 1u);
        attrs.Set(SinkWriterAttributeKeys.DisableThrottling, 1u);
        // Fragmented MP4: the file stays playable if the app or PC dies mid-recording.
        attrs.Set(TranscodeAttributeKeys.TranscodeContainertype, TranscodeContainerTypeGuids.Fmpeg4);
        _writer = MediaFactory.MFCreateSinkWriterFromURL(path, null, attrs);

        using (var vOut = MediaFactory.MFCreateMediaType())
        {
            vOut.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
            vOut.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.H264);
            vOut.Set(MediaTypeAttributeKeys.AvgBitrate, (uint)videoBitrate);
            vOut.Set(MediaTypeAttributeKeys.InterlaceMode, (uint)VideoInterlaceMode.Progressive);
            vOut.Set(MediaTypeAttributeKeys.FrameSize, Pack(_width, _height));
            vOut.Set(MediaTypeAttributeKeys.FrameRate, Pack(fps, 1));
            vOut.Set(MediaTypeAttributeKeys.PixelAspectRatio, Pack(1, 1));
            _videoStream = _writer.AddStream(vOut);
        }
        using (var vIn = MediaFactory.MFCreateMediaType())
        {
            vIn.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
            vIn.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.Argb32);
            vIn.Set(MediaTypeAttributeKeys.InterlaceMode, (uint)VideoInterlaceMode.Progressive);
            vIn.Set(MediaTypeAttributeKeys.FrameSize, Pack(_width, _height));
            vIn.Set(MediaTypeAttributeKeys.FrameRate, Pack(fps, 1));
            vIn.Set(MediaTypeAttributeKeys.PixelAspectRatio, Pack(1, 1));
            vIn.Set(MediaTypeAttributeKeys.DefaultStride, (uint)(_width * 4));
            _writer.SetInputMediaType(_videoStream, vIn, null);
        }

        if (withAudio)
        {
            using (var aOut = MediaFactory.MFCreateMediaType())
            {
                aOut.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Audio);
                aOut.Set(MediaTypeAttributeKeys.Subtype, AudioFormatGuids.Aac);
                aOut.Set(MediaTypeAttributeKeys.AudioBitsPerSample, 16u);
                aOut.Set(MediaTypeAttributeKeys.AudioSamplesPerSecond, (uint)sampleRate);
                aOut.Set(MediaTypeAttributeKeys.AudioNumChannels, (uint)channels);
                aOut.Set(MediaTypeAttributeKeys.AudioAvgBytesPerSecond, (uint)(audioBitrate / 8));
                aOut.Set(MediaTypeAttributeKeys.AudioBlockAlignment, 1u);
                _audioStream = _writer.AddStream(aOut);
            }
            using var aIn = MediaFactory.MFCreateMediaType();
            aIn.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Audio);
            aIn.Set(MediaTypeAttributeKeys.Subtype, AudioFormatGuids.Pcm);
            aIn.Set(MediaTypeAttributeKeys.AudioBitsPerSample, 16u);
            aIn.Set(MediaTypeAttributeKeys.AudioSamplesPerSecond, (uint)sampleRate);
            aIn.Set(MediaTypeAttributeKeys.AudioNumChannels, (uint)channels);
            aIn.Set(MediaTypeAttributeKeys.AudioBlockAlignment, (uint)(2 * channels));
            aIn.Set(MediaTypeAttributeKeys.AudioAvgBytesPerSecond, (uint)(sampleRate * 2 * channels));
            _writer.SetInputMediaType(_audioStream, aIn, null);
        }
        _writer.BeginWriting();
    }

    public int Width => _width;
    public int Height => _height;

    private static ulong Pack(int hi, int lo) => ((ulong)(uint)hi << 32) | (uint)lo;

    /// <summary>Writes one top-down BGRA frame of exactly Width x Height pixels.</summary>
    public void WriteVideo(byte[] bgra, long ptsHns, long durationHns) =>
        WriteSample(_videoStream, bgra, _width * _height * 4, ptsHns, durationHns);

    public void WriteAudio(byte[] pcm16, int byteCount, long ptsHns, long durationHns) =>
        WriteSample(_audioStream, pcm16, byteCount, ptsHns, durationHns);

    private void WriteSample(int stream, byte[] data, int length, long pts, long duration)
    {
        using var buffer = MediaFactory.MFCreateMemoryBuffer(length);
        buffer.Lock(out IntPtr ptr, out int _, out int _);
        try { System.Runtime.InteropServices.Marshal.Copy(data, 0, ptr, length); }
        finally { buffer.Unlock(); }
        buffer.CurrentLength = length;
        using var sample = MediaFactory.MFCreateSample();
        sample.AddBuffer(buffer);
        sample.SampleTime = pts;
        sample.SampleDuration = duration;
        lock (_gate)
        {
            if (_finished || _writer is null) return;
            _writer.WriteSample(stream, sample);
        }
    }

    public void Finish()
    {
        lock (_gate)
        {
            if (_finished || _writer is null) return;
            _finished = true;
            _writer.Finalize();
        }
    }

    public void Dispose()
    {
        Finish();
        lock (_gate) { _writer?.Dispose(); _writer = null; }
        MediaFactory.MFShutdown();
    }
}



