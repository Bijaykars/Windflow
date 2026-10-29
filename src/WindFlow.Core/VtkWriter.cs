using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace WindFlow.Core;

/// <summary>Legacy VTK (STRUCTURED_POINTS, BINARY, big-endian) writer for ParaView. Each grid cell is
/// written as one point at its centre, so point-data filters (contour, streamline, slice) see continuous
/// fields without a cell/point dimension mismatch.</summary>
public static class VtkWriter
{
    public static void Write(SimulationResult r, string path)
    {
        var g = r.Field.Grid;
        var field = r.Field;
        int n = g.Count;
        var origin = g.CellCenter(0, 0, 0);

        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
        using var w = new BinaryWriter(fs);

        void Line(string s) => w.Write(Encoding.ASCII.GetBytes(s + "\n"));
        string Inv(float v) => v.ToString(CultureInfo.InvariantCulture);

        Line("# vtk DataFile Version 3.0");
        Line($"WindFlow {r.Model.Name}");
        Line("BINARY");
        Line("DATASET STRUCTURED_POINTS");
        Line($"DIMENSIONS {g.Nx} {g.Ny} {g.Nz}");
        Line($"ORIGIN {Inv(origin.X)} {Inv(origin.Y)} {Inv(origin.Z)}");
        Line($"SPACING {Inv(g.Dx)} {Inv(g.Dx)} {Inv(g.Dx)}");
        Line($"POINT_DATA {n}");

        void F(float v)
        {
            Span<byte> buf4 = stackalloc byte[4];
            BinaryPrimitives.WriteSingleBigEndian(buf4, v);
            w.Write(buf4);
        }

        Line("VECTORS velocity float");
        for (int i = 0; i < n; i++) { F(field.Ux[i]); F(field.Uy[i]); F(field.Uz[i]); }
        w.Write((byte)'\n');

        Line("SCALARS pressure float 1");
        Line("LOOKUP_TABLE default");
        for (int i = 0; i < n; i++) F(field.P[i]);
        w.Write((byte)'\n');

        Line("SCALARS kind unsigned_char 1");
        Line("LOOKUP_TABLE default");
        w.Write(g.Cell, 0, n);
        w.Write((byte)'\n');
    }
}
