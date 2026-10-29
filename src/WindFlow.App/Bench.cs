using System.IO.Compression;
using WindFlow.App.Viewport;

namespace WindFlow.App;

/// <summary>--bench output: a one-page frame-time report plus a PNG of the last back buffer, both under out/.</summary>
public static class Bench
{
    public static string Write(BenchResult result, string modelName, int triangles, double seconds,
        string solver, string particles, string[]? liveLines = null)
    {
        string dir = Path.Combine(Options.RepoRoot(), "out");
        Directory.CreateDirectory(dir);
        string stem = Options.Live ? "app-live" : Options.View == "still" ? "app-bench-still" : "app-bench";
        string txtPath = Path.Combine(dir, stem + ".txt");
        string pngPath = Path.Combine(dir, Options.Live ? "app-live-before.png" : stem + ".png");
        string pngPath2 = Path.Combine(dir, "app-live-after.png");
        string shots = Options.Live ? pngPath + Environment.NewLine + "            after         " + pngPath2 : pngPath;
        string liveBlock = "";
        if (liveLines != null)
            foreach (var line in liveLines) liveBlock += line + Environment.NewLine + "            ";

        var sorted = (float[])result.FrameMs.Clone();
        Array.Sort(sorted);
        double total = 0;
        foreach (float f in sorted) total += f;
        int n = sorted.Length;
        double avgMs = n > 0 ? total / n : 0;
        int worst = Math.Max(1, n / 100);
        double worstSum = 0;
        for (int i = n - worst; i < n; i++) if (i >= 0) worstSum += sorted[i];
        double low1Ms = worst > 0 && n > 0 ? worstSum / worst : 0;

        var text = $"""
            WindFlow.App bench
            date          {DateTime.Now:yyyy-MM-dd HH:mm:ss}
            adapter       {result.Adapter}
            resolution    {result.Width} x {result.Height}
            model         {modelName} ({triangles:N0} triangles)
            solver        {(string.IsNullOrEmpty(solver) ? "not run" : solver)}
            particles     {particles}
            view          {Options.View}{(Options.FakeLive ? "  ·  live field republished at 10 Hz (--fakelive)" : "")}
            {liveBlock}duration      {seconds:0.#} s, {n:N0} frames measured (first 0.5 s discarded, vsync off)
            avg fps       {(avgMs > 0 ? 1000 / avgMs : 0):0.0}
            1% low fps    {(low1Ms > 0 ? 1000 / low1Ms : 0):0.0}
            avg frame ms  {avgMs:0.00}
            max frame ms  {(n > 0 ? sorted[n - 1] : 0):0.00}
            screenshot    {shots}

            """;
        File.WriteAllText(txtPath, text);
        if (result.Pixels.Length > 0) WritePng(pngPath, result.Width, result.Height, result.Pixels, result.RowPitch);
        if (Options.Live && result.Pixels2.Length > 0)
            WritePng(pngPath2, result.Width, result.Height, result.Pixels2, result.RowPitch);
        return text;
    }

    /// <summary>The renderer never produced a frame: record why rather than leaving the run hanging.</summary>
    public static void WriteFailure(string adapter, string status)
    {
        string dir = Path.Combine(Options.RepoRoot(), "out");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "app-bench.txt"),
            $"WindFlow.App bench FAILED\ndate          {DateTime.Now:yyyy-MM-dd HH:mm:ss}\n" +
            $"adapter       {(string.IsNullOrEmpty(adapter) ? "(none created)" : adapter)}\n" +
            $"status        {(string.IsNullOrEmpty(status) ? "(renderer produced no frames)" : status)}\n");
    }

    /// <summary>Minimal RGBA PNG writer: the back buffer is already sRGB-encoded, so the bytes go straight out.
    /// ponytail: no image library for one screenshot; swap for Avalonia's Bitmap.Save if the app ever needs
    /// thumbnails or a real image pipeline.</summary>
    public static void WritePng(string path, int width, int height, byte[] pixels, int rowPitch)
    {
        var raw = new byte[height * (width * 4 + 1)];
        for (int y = 0, o = 0; y < height; y++)
        {
            raw[o++] = 0;                                    // filter: none
            int src = y * rowPitch;
            for (int x = 0; x < width; x++, src += 4, o += 4)
            {
                raw[o] = pixels[src];
                raw[o + 1] = pixels[src + 1];
                raw[o + 2] = pixels[src + 2];
                raw[o + 3] = 255;
            }
        }

        using var deflated = new MemoryStream();
        using (var zlib = new ZLibStream(deflated, CompressionLevel.Optimal, leaveOpen: true)) zlib.Write(raw);

        using var file = File.Create(path);
        file.Write([0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A]);
        var ihdr = new byte[13];
        BeInt(ihdr, 0, width);
        BeInt(ihdr, 4, height);
        ihdr[8] = 8;        // bit depth
        ihdr[9] = 6;        // colour type: RGBA
        Chunk(file, "IHDR", ihdr);
        Chunk(file, "IDAT", deflated.ToArray());
        Chunk(file, "IEND", []);
    }

    static void BeInt(byte[] buffer, int offset, int value)
    {
        buffer[offset] = (byte)(value >> 24);
        buffer[offset + 1] = (byte)(value >> 16);
        buffer[offset + 2] = (byte)(value >> 8);
        buffer[offset + 3] = (byte)value;
    }

    static void Chunk(Stream stream, string type, byte[] data)
    {
        var length = new byte[4];
        BeInt(length, 0, data.Length);
        stream.Write(length);
        var payload = new byte[4 + data.Length];
        for (int i = 0; i < 4; i++) payload[i] = (byte)type[i];
        data.CopyTo(payload, 4);
        stream.Write(payload);
        var crc = new byte[4];
        BeInt(crc, 0, unchecked((int)Crc32(payload)));
        stream.Write(crc);
    }

    static readonly uint[] CrcTable = BuildCrcTable();

    static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint c = i;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            table[i] = c;
        }
        return table;
    }

    static uint Crc32(byte[] data)
    {
        uint c = 0xFFFFFFFFu;
        foreach (byte b in data) c = CrcTable[(c ^ b) & 0xFF] ^ (c >> 8);
        return c ^ 0xFFFFFFFFu;
    }
}
