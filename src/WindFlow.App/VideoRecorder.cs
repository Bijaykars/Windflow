using System.Collections.Concurrent;
using Vortice.MediaFoundation;

namespace WindFlow.App;

/// <summary>H.264 MP4 writer on the Media Foundation sink writer the OS provides: no FFmpeg, no bundled codec.
/// The render thread hands over finished frames with <see cref="Submit"/>; a worker thread does the encode so
/// the render loop never waits on it. A full queue drops the frame and counts it.</summary>
public sealed class VideoRecorder : IDisposable
{
    const int QueueDepth = 6;

    public int FramesWritten { get; private set; }
    public int FramesDropped { get; private set; }
    public string? Error { get; private set; }
    public bool Recording => worker != null;
    public int Width { get; private set; }
    public int Height { get; private set; }
    public int Fps { get; private set; } = 30;
    public string Path { get; private set; } = "";

    static bool started;
    // Recreated per recording: CompleteAdding() in Stop() is permanent, so a second export on the same
    // instance used to throw InvalidOperationException out of TryAdd -- on the render thread.
    BlockingCollection<byte[]> queue = new(QueueDepth);
    readonly ConcurrentBag<byte[]> spare = new();
    Thread? worker;
    int sourcePitch, sourceHeight;   // the readback geometry, which is NOT the cropped encode height

    static void EnsureMediaFoundation()
    {
        if (started) return;
        MediaFactory.MFStartup(false).CheckError();   // MF_VERSION, no socket support needed
        started = true;
    }

    public void Start(string path, int width, int height, int pitch, int fps)
    {
        Stop();
        Error = null;
        FramesWritten = 0;
        FramesDropped = 0;
        // H.264 wants even dimensions; crop rather than rescale (a resizer MFT would be the alternative).
        Width = width & ~1;
        Height = height & ~1;
        Fps = Math.Clamp(fps, 1, 120);
        Path = path;
        sourcePitch = pitch;
        sourceHeight = height;
        queue = new BlockingCollection<byte[]>(QueueDepth);
        while (spare.TryTake(out _)) { }          // buffers sized for the previous capture

        worker = new Thread(() => Encode(path)) { IsBackground = true, Name = "WindFlow encoder" };
        worker.Start();
    }

    /// <summary>Called on the render thread with a tightly packed RGBA readback. Never blocks.</summary>
    public void Submit(ReadOnlySpan<byte> rgba)
    {
        if (worker == null) return;
        // A resize during recording changes the readback pitch or height; the encoder still reads
        // sourcePitch x Height rows out of the frame it was given, so a frame of another shape would read
        // past the end of the buffer. (Height is the even, cropped ENCODE height, not the readback height.)
        if (rgba.Length != sourcePitch * sourceHeight) { FramesDropped++; return; }
        if (!spare.TryTake(out var buffer) || buffer.Length != rgba.Length) buffer = new byte[rgba.Length];
        rgba.CopyTo(buffer);
        var target = queue;                       // Stop() may swap or complete it under us
        try
        {
            if (!target.TryAdd(buffer)) FramesDropped++;
        }
        catch (Exception)
        {
            // Completed or disposed between the check and the add: a dropped frame, never an exception
            // escaping onto the render thread.
            FramesDropped++;
        }
    }

    public void Stop()
    {
        if (worker == null) return;
        try { queue.CompleteAdding(); } catch (ObjectDisposedException) { /* already gone */ }
        worker.Join(20000);
        worker = null;
    }

    void Encode(string path)
    {
        IMFSinkWriter? writer = null;
        try
        {
            EnsureMediaFoundation();
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path))!);

            long frameSize = ((long)Width << 32) | (uint)Height;
            long frameRate = ((long)Fps << 32) | 1u;
            long aspect = (1L << 32) | 1u;
            // Bitrate from pixel rate: roughly 0.1 bit per pixel per frame, generous for synthetic content.
            uint bitrate = (uint)Math.Clamp((long)Width * Height * Fps / 10, 2_000_000, 40_000_000);

            writer = MediaFactory.MFCreateSinkWriterFromURL(path, null, null);

            var output = MediaFactory.MFCreateMediaType();
            output.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
            output.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.H264);
            output.Set(MediaTypeAttributeKeys.AvgBitrate, bitrate);
            output.Set(MediaTypeAttributeKeys.InterlaceMode, 2u);      // progressive
            output.Set(MediaTypeAttributeKeys.FrameSize, (ulong)frameSize);
            output.Set(MediaTypeAttributeKeys.FrameRate, (ulong)frameRate);
            output.Set(MediaTypeAttributeKeys.PixelAspectRatio, (ulong)aspect);
            int stream = writer.AddStream(output);

            var input = MediaFactory.MFCreateMediaType();
            input.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
            input.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.Rgb32);
            input.Set(MediaTypeAttributeKeys.InterlaceMode, 2u);
            input.Set(MediaTypeAttributeKeys.FrameSize, (ulong)frameSize);
            input.Set(MediaTypeAttributeKeys.FrameRate, (ulong)frameRate);
            input.Set(MediaTypeAttributeKeys.PixelAspectRatio, (ulong)aspect);
            writer.SetInputMediaType(stream, input, null);
            writer.BeginWriting();

            long duration = 10_000_000L / Fps;
            int rowBytes = Width * 4;
            foreach (var frame in queue.GetConsumingEnumerable())
            {
                var buffer = MediaFactory.MFCreateMemoryBuffer(rowBytes * Height);
                buffer.Lock(out var destination, out _, out _);
                unsafe
                {
                    byte* dst = (byte*)destination;
                    fixed (byte* src = frame)
                    {
                        // RGBA to BGRA, bottom-up: MF reads RGB32 with a positive stride as bottom-up.
                        for (int y = 0; y < Height; y++)
                        {
                            byte* s = src + (long)(Height - 1 - y) * sourcePitch;
                            byte* d = dst + (long)y * rowBytes;
                            for (int x = 0; x < Width; x++, s += 4, d += 4)
                            {
                                d[0] = s[2];
                                d[1] = s[1];
                                d[2] = s[0];
                                d[3] = 255;
                            }
                        }
                    }
                }
                buffer.Unlock();
                buffer.CurrentLength = rowBytes * Height;

                var sample = MediaFactory.MFCreateSample();
                sample.AddBuffer(buffer);
                sample.SampleTime = FramesWritten * duration;
                sample.SampleDuration = duration;
                writer.WriteSample(stream, sample);
                FramesWritten++;

                sample.Dispose();
                buffer.Dispose();
                spare.Add(frame);
            }

            writer.Finalize();
        }
        catch (Exception e)
        {
            Log.Error($"encoder thread failed after {FramesWritten} frames ({path})", e);
            Error = e.Message.Length > 200 ? e.Message[..200] : e.Message;
        }
        finally
        {
            writer?.Dispose();
        }
    }

    public void Dispose() => Stop();

    /// <summary>Decodes one frame at <paramref name="atSeconds"/> and writes it as a PNG: proof the file really
    /// is a playable video and not just bytes on disk. Returns the decoded size, last timestamp and count.</summary>
    public static (int Width, int Height, double LastSeconds, int Frames) ExtractFrame(
        string videoPath, double atSeconds, string pngPath)
    {
        EnsureMediaFoundation();
        // The reader must be allowed to insert a converter, or asking for RGB32 out of H.264 is rejected.
        var readerAttributes = MediaFactory.MFCreateAttributes(1);
        readerAttributes.Set(SourceReaderAttributeKeys.EnableVideoProcessing, 1u);
        using var reader = MediaFactory.MFCreateSourceReaderFromURL(videoPath, readerAttributes);
        var wanted = MediaFactory.MFCreateMediaType();
        wanted.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
        wanted.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.Rgb32);
        reader.SetCurrentMediaType(0, wanted);

        var actual = reader.GetCurrentMediaType(0);
        ulong size = actual.GetUInt64(MediaTypeAttributeKeys.FrameSize);
        int width = (int)(size >> 32), height = (int)(size & 0xFFFFFFFF);

        long target = (long)(atSeconds * 10_000_000);
        long last = 0;
        int frames = 0;
        bool saved = false;
        while (true)
        {
            var sample = reader.ReadSample(0, SourceReaderControlFlag.None, out _, out var flags, out long time);
            if (sample == null || (flags & SourceReaderFlag.EndOfStream) != 0) break;
            last = time;
            frames++;
            if (!saved && time >= target)
            {
                using var contiguous = sample.ConvertToContiguousBuffer();
                contiguous.Lock(out var pointer, out _, out _);
                var rgba = new byte[width * height * 4];
                unsafe
                {
                    byte* src = (byte*)pointer;
                    for (int y = 0; y < height; y++)
                    {
                        // The video processor hands RGB32 back top-down, unlike the bottom-up buffer the
                        // sink writer wanted going in, so no flip here.
                        byte* s = src + (long)y * width * 4;
                        for (int x = 0; x < width; x++)
                        {
                            int d = (y * width + x) * 4;
                            rgba[d] = s[x * 4 + 2];
                            rgba[d + 1] = s[x * 4 + 1];
                            rgba[d + 2] = s[x * 4];
                            rgba[d + 3] = 255;
                        }
                    }
                }
                contiguous.Unlock();
                Bench.WritePng(pngPath, width, height, rgba, width * 4);
                saved = true;
            }
            sample.Dispose();
        }
        return (width, height, last / 10_000_000.0, frames);
    }
}
