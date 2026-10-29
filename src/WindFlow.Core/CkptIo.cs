using System.Runtime.InteropServices;

namespace WindFlow.Core;

/// <summary>Ultra checkpoints: chunked array I/O over a BinaryWriter/BinaryReader on a stream, so no array is ever copied whole into a byte[].</summary>
public static class CkptIo
{
    /// <summary>Elements per GPU read-back / file chunk (64 MB of floats).</summary>
    public const int Chunk = 1 << 24;

    public static void Write(BinaryWriter w, float[] a) { w.Write(a.LongLength); WriteSpan<float>(w, a); }
    public static void Write(BinaryWriter w, int[] a) { w.Write(a.LongLength); WriteSpan<int>(w, a); }
    public static void Write(BinaryWriter w, byte[] a) { w.Write(a.LongLength); w.Write(a); }
    public static void Write(BinaryWriter w, bool[] a) { w.Write(a.LongLength); foreach (bool b in a) w.Write(b); }
    public static void Write(BinaryWriter w, double[] a) { w.Write(a.LongLength); WriteSpan<double>(w, a); }

    public static void WriteSpan<T>(BinaryWriter w, ReadOnlySpan<T> s) where T : unmanaged
    {
        var bytes = MemoryMarshal.AsBytes(s);
        for (int o = 0; o < bytes.Length; o += 1 << 26) w.Write(bytes.Slice(o, Math.Min(1 << 26, bytes.Length - o)));
    }

    public static float[] ReadFloats(BinaryReader r) { var a = new float[r.ReadInt64()]; ReadSpan<float>(r, a); return a; }
    public static int[] ReadInts(BinaryReader r) { var a = new int[r.ReadInt64()]; ReadSpan<int>(r, a); return a; }
    public static byte[] ReadBytes(BinaryReader r) { var a = new byte[r.ReadInt64()]; ReadSpan<byte>(r, a); return a; }
    public static double[] ReadDoubles(BinaryReader r) { var a = new double[r.ReadInt64()]; ReadSpan<double>(r, a); return a; }
    public static bool[] ReadBools(BinaryReader r) { var a = new bool[r.ReadInt64()]; for (int i = 0; i < a.Length; i++) a[i] = r.ReadBoolean(); return a; }

    public static void ReadSpan<T>(BinaryReader r, Span<T> s) where T : unmanaged
    {
        var bytes = MemoryMarshal.AsBytes(s);
        for (int o = 0; o < bytes.Length;)
        {
            int n = r.Read(bytes.Slice(o, Math.Min(1 << 26, bytes.Length - o)));
            if (n <= 0) throw new EndOfStreamException("checkpoint file is truncated");
            o += n;
        }
    }

    public static void WriteString(BinaryWriter w, string s) => w.Write(s ?? "");
}
