using System.Numerics;

namespace WindFlow.Core;

/// <summary>Time mean of the positive/negative boundary flux, in m³/s. These are exchange rates,
/// not outdoor fresh-air delivery. Interior supply/return patches are added by Analysis.</summary>
public sealed class RoomFlux
{
    public Room Room = new();
    public double Inflow, Outflow, InternalExchange;
    /// <summary>Part of <see cref="Inflow"/> crossing faces of exterior openings, devices, source patches or the domain edge: air
    /// entering this room straight from outside. Transfer air through an interior door is not in it.</summary>
    public double OutdoorInflow;
}

/// <summary>Time mean of the per-step link flux across an open opening's throat plane, m3/s, along +Normal.
/// Pos/Neg are the per-step positive and negative parts of the net (Pos - Neg = Net = the conserved flow).</summary>
public sealed class OpeningLinkFlux
{
    public Opening Opening = new();
    public double Pos, Neg;
    public double Net => Pos - Neg;
}

/// <summary>Throat-plane link flux per open opening, f*_q(A) - f*_qbar(D) for every non-wall destination, in the same (A, D, q) form as the room faces
/// so the same kernel sums them. Every opening (P12-06 for the oblique ones) is cut by the plane through its centre: every link from a footprint cell on
/// the near side (normal coordinate e &lt;= 0) to a cell beyond it (e &gt; 0), so a rotated window's flow is a conserved flux like the axis-aligned ones
/// instead of u.n summed over a phase-dependent one-cell slab (0.63-1.50 of the area at 45 deg). 6a: the plane is the opening's centre, not the throat
/// layer next to it: in a wall thinner than a cell (cut links, no Solid jambs) a layer beside the wall lets air into the hole round the footprint.</summary>
internal sealed class OpeningFluxPlan
{
    public Opening[] Openings = [];
    public int[] LinkStart = [0], LinkA = [], LinkD = [], LinkQ = [];

    public static OpeningFluxPlan Build(VoxelGrid g, BuildingModel model)
    {
        var ops = new List<Opening>(); var lk = new List<(int F,int A,int D,int Q)>();
        foreach (var o in model.Openings)
        {
            if (!o.IsOpen) continue;
            var n = o.Normal; n.Z = 0;
            if (n.LengthSquared() < 1e-8f) continue;
            AddPlane(g, o, Vector3.Normalize(n), ops, lk);
        }
        var start = new int[ops.Count + 1];
        foreach (var l in lk) start[l.F + 1]++;
        for (int k = 0; k < ops.Count; k++) start[k + 1] += start[k];
        return new() { Openings = ops.ToArray(), LinkStart = start, LinkA = lk.Select(l => l.A).ToArray(), LinkD = lk.Select(l => l.D).ToArray(), LinkQ = lk.Select(l => l.Q).ToArray() };
    }

    /// <summary>P12-06: the links of an opening across the plane through its centre. <paramref name="n"/> is the unit horizontal normal with its true sign
    /// (flux is along it); the footprint is the carve's own (cell-centre test in the canonical, x-positive frame, as <see cref="Analysis.ThroatCells"/>).</summary>
    static void AddPlane(VoxelGrid g, Opening o, Vector3 n, List<Opening> ops, List<(int F, int A, int D, int Q)> lk)
    {
        var nc = n; if (nc.X < 0 || (nc.X == 0 && nc.Y < 0)) nc = -nc;
        var t = new Vector3(-nc.Y, nc.X, 0);
        float dx = g.Dx;
        float halfW = MathF.Max(o.Width, dx) * 0.5f, halfH = MathF.Max(o.Height, dx) * 0.5f;
        var reach = new Vector3(halfW + 2 * dx, halfW + 2 * dx, halfH + 2 * dx);
        var lo = o.Center - reach - g.Origin; var hi = o.Center + reach - g.Origin;
        int x0 = Math.Max(0, (int)MathF.Floor(lo.X / dx)), x1 = Math.Min(g.Nx - 1, (int)MathF.Floor(hi.X / dx));
        int y0 = Math.Max(0, (int)MathF.Floor(lo.Y / dx)), y1 = Math.Min(g.Ny - 1, (int)MathF.Floor(hi.Y / dx));
        int z0 = Math.Max(0, (int)MathF.Floor(lo.Z / dx)), z1 = Math.Min(g.Nz - 1, (int)MathF.Floor(hi.Z / dx));
        int f = ops.Count, before = lk.Count;
        for (int z = z0; z <= z1; z++)
        for (int y = y0; y <= y1; y++)
        for (int x = x0; x <= x1; x++)
        {
            int i = g.Index(x, y, z);
            if (!Voxelizer.LbmFluid(g, i)) continue;
            var c = g.CellCenter(x, y, z) - o.Center;
            float e = Vector3.Dot(c, n);
            // The near side of the cut: far enough behind that a diagonal link (up to 1.42 Dx along the normal) can still cross it.
            if (!(e <= 0f && e > -1.5f * dx)) continue;
            if (!Voxelizer.InClosed(Vector3.Dot(c, t), halfW, dx) || !Voxelizer.InClosed(c.Z, halfH, dx)) continue;   // RP follow-up 4: the carve's rule
            for (int q = 1; q < 19; q++)
            {
                LbmSolver.LinkDir(q, out int cx, out int cy, out int cz);
                int nx = x + cx, ny = y + cy, nz = z + cz;
                if (nx < 0 || nx >= g.Nx || ny < 0 || ny >= g.Ny || nz < 0 || nz >= g.Nz) continue;
                // Across the cut: D on the far side by the same test that put A on the near one, so a centre on the plane is on one side only.
                if (!(Vector3.Dot(g.CellCenter(nx, ny, nz) - o.Center, n) > 0f)) continue;
                int j = g.Index(nx, ny, nz);
                if (Wall(g, j, q)) continue;
                lk.Add((f, i, j, q));
            }
        }
        if (lk.Count > before) ops.Add(o);
    }

    /// <summary>6a: the link along which D pulls q (from the throat cell) is a wall for the LBM: D not LBM-fluid, or the link cut (VoxelGrid.WallLinks).</summary>
    static bool Wall(VoxelGrid g, int d, int q) => !Voxelizer.LbmFluid(g, d) || g.WallLinks.Length == 19 * g.Count && g.WallLinks[q * g.Count + d] != 0;

    /// <summary>sums = [2k] Sum max(inward,0), [2k+1] Sum max(outward,0), the kernel's convention; outward = along +Normal.</summary>
    public List<OpeningLinkFlux> Reduce(double[] sums, double scale) =>
        Openings.Select((o, k) => new OpeningLinkFlux { Opening = o, Neg = sums[2 * k] * scale, Pos = sums[2 * k + 1] * scale }).ToList();
}

/// <param name="CountPlain">False for a plain-air face of a detected room whose outside cell is in no other room: that is the room's own one-cell
/// band (the outline is the eroded core), so its flux is recirculation inside the room, not exchange with anything (RV-09).</param>
internal readonly record struct RoomFace(int Room, int Inside, int Outside, int Axis, int Sign, bool Connection, bool Exterior, bool CountPlain = true);

internal sealed class RoomFluxPlan
{
    public Room[] Rooms = [];
    public RoomFace[] Faces = [];
    // Link flux: face k owns links LinkStart[k]..LinkStart[k+1]-1. A link is (LinkA room cell, LinkD cell it streams
    // to, lattice direction LinkQ of A's population); its net outward mass is f*_q(A) - f*_qbar(D). For an open
    // domain boundary face (Outside < 0) D = A. See LbmSolver.RoomFluxKernel.
    public int[] LinkStart = [0], LinkA = [], LinkD = [], LinkQ = [];

    public static RoomFluxPlan Build(VoxelGrid g, BuildingModel model)
    {
        var faces = new List<RoomFace>();
        var conn = Analysis.ConnectionCells(g, model);
        var ext = Analysis.ConnectionCells(g, model, exteriorOnly: true);
        int[] dx=[-1,1,0,0,0,0],dy=[0,0,-1,1,0,0],dz=[0,0,0,0,-1,1];
        var lk = new List<(int Face,int A,int D,int Q)>();
        var masks = Analysis.RoomMasks(g, model, out _, lbm: true, conn);   // OF-1: outline plus band
        var owner = model.Rooms.Any(Analysis.IsDetectedRoom) ? Analysis.RoomOwner(g, model, lbm: true, masks) : [];
        // 6a: the faces and links are the LBM's own: a destination is a wall only when it is not LBM-fluid or the link to it is cut,
        // so every uncut link across a room's outline counts, also into a cell the conservative Cell mask calls Solid (VoxelGrid.LbmAir).
        bool hasLinks = g.WallLinks.Length == 19 * g.Count;
        bool Cut(int d, int q) => hasLinks && g.WallLinks[q * g.Count + d] != 0;   // d pulls q from d - c_q
        int[] faceQ = [2, 1, 4, 3, 6, 5];
        for(int r=0;r<model.Rooms.Count;r++)
        {
            var mask=masks[r];   // RY-01 (+ OF-1 band)
            bool detected = Analysis.IsDetectedRoom(model.Rooms[r]);
            var fid=new Dictionary<long,int>();
            for(int i=0;i<g.Count;i++)
            {
                if(!mask[i]) continue;
                int x=i%g.Nx,y=i/g.Nx%g.Ny,z=i/(g.Nx*g.Ny);
                for(int f=0;f<6;f++)
                {
                    int j=Analysis.FaceNeighbor(g,mask,x,y,z,dx[f],dy[f],dz[f],lbm:true);
                    if(j>=0 && Cut(j,faceQ[f])) j=-2;
                    if(j==-2) continue;
                    fid[(long)i*6+f]=faces.Count;
                    bool isConn = j<0||conn[i]||conn[j];
                    faces.Add(new(r,i,j,f/2,(f&1)==0?-1:1,isConn,j<0||ext[i]||ext[j], !(detected && !isConn && owner[j]==0)));
                }
            }
            // 0 wall (bounces back: Solid or closed Z), 1 room, 2 other air, 3 beyond an open domain boundary
            int State(int x,int y,int z)
            {
                if(z<0) return g.OpenZMin?3:0; if(z>=g.Nz) return g.OpenZMax?3:0;
                if(x<0||x>=g.Nx||y<0||y>=g.Ny) return 3;
                int j=g.Index(x,y,z);
                return !Voxelizer.LbmFluid(g,j)?0:mask[j]?1:2;
            }
            int Face(int x,int y,int z,int axis,int sign) => fid.TryGetValue((long)g.Index(x,y,z)*6+2*axis+(sign>0?1:0),out int k)?k:-1;
            int[] c=new int[3];
            for(int i=0;i<g.Count;i++)
            {
                if(!mask[i]) continue;
                int x=i%g.Nx,y=i/g.Nx%g.Ny,z=i/(g.Nx*g.Ny);
                int[] p=[x,y,z];
                for(int q=1;q<19;q++)
                {
                    LbmSolver.LinkDir(q,out c[0],out c[1],out c[2]);
                    int st=State(x+c[0],y+c[1],z+c[2]);
                    if((st==1||st==2) && Cut(g.Index(x+c[0],y+c[1],z+c[2]),q)) st=0;
                    int a=-1,b=-1,face=-1,d=i,flag=0;
                    for(int ax=0;ax<3;ax++) if(c[ax]!=0) { if(a<0) a=ax; else b=ax; }
                    // open boundary: does the link leave through an open domain face?
                    int open=-1;
                    for(int ax=2;ax>=0&&open<0;ax--)
                    {
                        int n=p[ax]+c[ax],len=ax==0?g.Nx:ax==1?g.Ny:g.Nz;
                        if(c[ax]!=0&&(n<0||n>=len)&&(ax<2||(n<0?g.OpenZMin:g.OpenZMax))) open=ax;
                    }
                    if(open>=0)
                    {
                        // No D cell: D = A, the two legs are A's own populations q (outward) and qbar (inward), each
                        // dropped on its own when its destination is a wall (it bounces back).
                        // Counted at every in-domain face of A the link leaves through (a convex corner of the room duplicates it, as
                        // the u-mean did; there is no D cell to give it one owner, and for a uniform stream the duplicates
                        // cancel inside the cross face). Open boundary faces themselves read the node velocity instead.
                        if(st==0) flag|=64;
                        if(State(x-c[0],y-c[1],z-c[2])==0 || Cut(i,q)) flag|=32;
                        if(flag!=96)
                            for(int ax=0;ax<3;ax++)
                            {
                                int fo=c[ax]!=0?Face(x,y,z,ax,c[ax]):-1;
                                if(fo>=0&&faces[fo].Outside>=0) lk.Add((fo,i,i,q|flag));
                            }
                    }
                    else if(st==2)
                    {
                        d=g.Index(x+c[0],y+c[1],z+c[2]);
                        if(b<0) face=Face(x,y,z,a,c[a]);
                        else
                        {
                            // Diagonal link. Planar rule (convex room corner, A has a face on axis a and/or b): counted at
                            // every such A-side face, as the textbook sum_{c_a=s} f(A) - sum_{c_a=-s} f(B) does, so a box
                            // room in a uniform stream reads exact per face. If A has neither face (concave corner at a
                            // jamb: the planar rule never sees the link) own it to ONE face of the room neighbour Q (offset
                            // along b) or P (along a) towards D, so the closed-surface sum stays conserved.
                            int[] pa=[x+c[0]*(a==0?1:0),y+c[1]*(a==1?1:0),z+c[2]*(a==2?1:0)];
                            int[] pb=[x+c[0]*(b==0?1:0),y+c[1]*(b==1?1:0),z+c[2]*(b==2?1:0)];
                            int sa=State(pa[0],pa[1],pa[2]),sb=State(pb[0],pb[1],pb[2]);
                            int fa=sa==2?Face(x,y,z,a,c[a]):-1, fb=sb==2?Face(x,y,z,b,c[b]):-1;
                            if(fa>=0&&fb>=0) lk.Add((fb,i,d,q));
                            if(fa>=0) face=fa; else if(fb>=0) face=fb;
                            else if(sb==1) face=Face(pb[0],pb[1],pb[2],a,c[a]);
                            else if(sa==1) face=Face(pa[0],pa[1],pa[2],b,c[b]);
                        }
                    }
                    if(face>=0) lk.Add((face,i,d,q|flag));
                }
            }
        }
        // open domain boundary faces: one momentum entry each (see LbmSolver.RoomFluxKernel)
        for(int k=0;k<faces.Count;k++)
            if(faces[k].Outside<0) lk.Add((k,faces[k].Inside,faces[k].Inside,128+2*faces[k].Axis+(faces[k].Sign>0?1:0)));
        lk.Sort((u,v)=>u.Face.CompareTo(v.Face));
        var start=new int[faces.Count+1];
        foreach(var l in lk) start[l.Face+1]++;
        for(int k=0;k<faces.Count;k++) start[k+1]+=start[k];
        return new()
        {
            Rooms=model.Rooms.ToArray(), Faces=faces.ToArray(), LinkStart=start,
            LinkA=lk.Select(l=>l.A).ToArray(), LinkD=lk.Select(l=>l.D).ToArray(), LinkQ=lk.Select(l=>l.Q).ToArray(),
        };
    }

    public List<RoomFlux> Reduce(double[] sums, double scale)
    {
        var result=Rooms.Select(r=>new RoomFlux { Room=r }).ToList();
        for(int k=0;k<Faces.Length;k++)
        {
            var face=Faces[k]; var r=result[face.Room];
            if(face.Connection) { r.Inflow+=sums[2*k]*scale; r.Outflow+=sums[2*k+1]*scale; if(face.Exterior) r.OutdoorInflow+=sums[2*k]*scale; }
            else if(face.CountPlain) r.InternalExchange+=sums[2*k]*scale;
        }
        return result;
    }
}

public static partial class Analysis
{
    /// <summary>A detected (voxel-derived) room: its loop is the eroded core, so it has a one-cell band of its own around it.</summary>
    internal static bool IsDetectedRoom(Room room) => room.VoxelDerived || room.Id.StartsWith("det-", StringComparison.Ordinal);

    /// <summary>RC-01: elevation + height of the storey the room sits on (<see cref="BuildingModel.StoreyOf"/>); null when it has none or no height.</summary>
    internal static float? StoreyTop(BuildingModel model, Room room) => model.StoreyOf(room) is { Height: > 0 } s ? s.Elevation + s.Height : null;

    /// <summary>Per cell, 1 + the index of the first room whose mask holds it, 0 for a cell in no room.</summary>
    internal static int[] RoomOwner(VoxelGrid g, BuildingModel model, bool lbm = false, bool[][]? masks = null)
    {
        var owner = new int[g.Count];
        for (int r = 0; r < model.Rooms.Count; r++)
        {
            var mask = masks != null ? masks[r] : RoomMask(g, model.Rooms[r], out _, lbm, StoreyTop(model, model.Rooms[r]));
            for (int i = 0; i < mask.Length; i++) if (mask[i] && owner[i] == 0) owner[i] = r + 1;
        }
        return owner;
    }

    /// <summary>OF-1 (DESIGN_OPENING_FIX option G): how many cells deep a room's band may reach into the air between its outline and its walls.</summary>
    internal const int BandDepth = 2;

    /// <summary>OF-1: every room's cells, the outline mask (<see cref="RoomMask"/>) plus its band. The sample's (and any drawn) outlines sit a few cm inside
    /// the walls; whether a column of cell centres falls in that gap is a grid lottery, and when it does the room's whole wall became "plain air" whose
    /// gross inflow counted as exchange (Bedroom 1 total +36 % between two grids 1 mm apart). The band is that gap: an LBM-fluid component of cells in no
    /// room's outline, bounded by connection cells (openings, devices, the domain shell) and cut links, that touches exactly ONE room and does not reach the
    /// domain shell (outdoor air) joins that room, out to <see cref="BandDepth"/> cells from it. A component touching two rooms (the stair void) is shared
    /// air and stays in none: the deterministic tie rule. Use these masks wherever room cells are used (flux plan, ACH, age and comfort means, the hold);
    /// the room VOLUME shown does not use them (see <c>AnalyzeRoom</c>).</summary>
    /// <param name="outline">Per room, the cells of its outline alone, as <see cref="RoomMask"/> counts them (the volume fallback).</param>
    public static bool[][] RoomMasks(VoxelGrid g, BuildingModel model, out int[] outline, bool lbm = false, bool[]? conn = null)
    {
        var rooms = model.Rooms;
        var masks = new bool[rooms.Count][];
        var owner = new int[g.Count];
        for (int r = 0; r < masks.Length; r++)
        {
            masks[r] = RoomMask(g, rooms[r], out _, lbm: true, storeyTop: StoreyTop(model, rooms[r]));
            for (int i = 0; i < owner.Length; i++) if (masks[r][i] && owner[i] == 0) owner[i] = r + 1;
        }
        outline = new int[masks.Length];
        for (int r = 0; r < masks.Length; r++)
            for (int i = 0; i < g.Count; i++) if (masks[r][i] && (lbm || g.Cell[i] != VoxelGrid.Solid)) outline[r]++;
        if (masks.Length > 0) MergeBands(g, masks, owner, conn ?? ConnectionCells(g, model));
        if (!lbm)
            foreach (var m in masks) for (int i = 0; i < m.Length; i++) if (m[i] && g.Cell[i] == VoxelGrid.Solid) m[i] = false;   // LbmAir layer: not Cell air
        return masks;
    }

    static void MergeBands(VoxelGrid g, bool[][] masks, int[] owner, bool[] conn)
    {
        bool links = g.WallLinks.Length == 19 * g.Count;
        int[] dxs = [1, -1, 0, 0, 0, 0], dys = [0, 0, 1, -1, 0, 0], dzs = [0, 0, 0, 0, 1, -1];   // q = k + 1 pulls from the cell before it; k ^ 1 is the reverse
        var seen = new bool[g.Count]; var depth = new byte[g.Count];
        var stack = new Stack<int>(); var comp = new List<int>(); var edge = new List<int>(); var touch = new HashSet<int>();
        // j reached from i along direction k: an LBM-fluid cell over an uncut link (either way), else -1
        int Step(int i, int x, int y, int z, int k)
        {
            int nx = x + dxs[k], ny = y + dys[k], nz = z + dzs[k];
            if (nx < 0 || ny < 0 || nz < 0 || nx >= g.Nx || ny >= g.Ny || nz >= g.Nz) return -1;
            int j = g.Index(nx, ny, nz);
            if (!Voxelizer.LbmFluid(g, j)) return -1;
            if (links && (g.WallLinks[(k + 1) * g.Count + j] != 0 || g.WallLinks[((k ^ 1) + 1) * g.Count + i] != 0)) return -1;
            return j;
        }
        for (int s = 0; s < g.Count; s++)
        {
            if (seen[s] || owner[s] != 0 || conn[s] || !Voxelizer.LbmFluid(g, s)) continue;
            comp.Clear(); edge.Clear(); touch.Clear(); bool outdoor = false;
            seen[s] = true; stack.Push(s);
            while (stack.Count > 0)
            {
                int i = stack.Pop(); comp.Add(i);
                int x = i % g.Nx, y = i / g.Nx % g.Ny, z = i / (g.Nx * g.Ny); bool adj = false;
                for (int k = 0; k < 6; k++)
                {
                    int j = Step(i, x, y, z, k); if (j < 0) continue;
                    int jx = j % g.Nx, jy = j / g.Nx % g.Ny, jz = j / (g.Nx * g.Ny);
                    if (jx == 0 || jy == 0 || jz == 0 || jx == g.Nx - 1 || jy == g.Ny - 1 || jz == g.Nz - 1) outdoor = true;   // the domain shell is outside air
                    if (owner[j] != 0) { touch.Add(owner[j] - 1); adj = true; continue; }
                    if (conn[j] || seen[j]) continue;
                    seen[j] = true; stack.Push(j);
                }
                if (adj) edge.Add(i);
            }
            if (outdoor || touch.Count != 1) continue;   // outdoor air is never a room's; shared air (two rooms) is no room's
            // depth: cells from the room, through the component (6-neighbours)
            var q = new Queue<int>();
            foreach (int i in edge) { depth[i] = 1; q.Enqueue(i); }
            while (q.Count > 0)
            {
                int i = q.Dequeue(); if (depth[i] >= BandDepth) continue;
                int x = i % g.Nx, y = i / g.Nx % g.Ny, z = i / (g.Nx * g.Ny);
                for (int k = 0; k < 6; k++)
                {
                    int j = Step(i, x, y, z, k);
                    if (j < 0 || owner[j] != 0 || conn[j] || depth[j] != 0) continue;
                    depth[j] = (byte)(depth[i] + 1); q.Enqueue(j);
                }
            }
            var mask = masks[touch.First()];
            foreach (int i in comp) if (depth[i] != 0) mask[i] = true;
        }
    }

    /// <param name="storeyTop">RC-01: top of the storey the room's base sits in (<see cref="StoreyTop"/>); the pass-2 flood stays below it (or ZMax if lower). null = ZMax.</param>
    /// <param name="lbm">RY-01: the room-face flux plan's mask is in LBM terms: an LBM-fluid cell (Air or <see cref="VoxelGrid.LbmAir"/>) whose centre is inside the loops
    /// belongs to the room, so its own wall-side LbmAir layer is inside the outline; otherwise the conservative Cell mask (the age solver's).</param>
    internal static bool[] RoomMask(VoxelGrid g, Room room, out int count, bool lbm = false, float? storeyTop = null)
    {
        var mask=new bool[g.Count]; count=0;
        // Two passes. IMP-C (bRW2 Duplex upstairs), pass 1 (seeds): each column stops at its ceiling, the first Solid cell above the floor air. The storey top is not the room's top: the air above a lower slab (roof
        // space, the next storey's void) is at the 24 h age cap and would sit in the room's fresh-air mean. Solid below the first air is the floor (or a standing cabinet), not a ceiling.
        // The ceiling is a Cell-Solid cell even when LBM treats it as fluid (a slab thinner than a cell): that cell is the room's last in LBM terms, nothing above it is.
        // RA-07, pass 2 (flood): that clip also cut the air above any table, desk, beam or loft bed out of the room. From every seed that is not Cell-Solid, a 6-connected fill takes in the fluid cells
        // inside the loops and the z range that are reached through open air, so air above furniture joins the room by its open sides. A Cell-Solid seed (a thin slab) is added when reached but not
        // expanded through, so nothing above a thin ceiling is reached; a roof void sealed by a slab has no fluid path inside the loops and stays out.
        // RC-01/RC-03: the fill never goes above the room's storey top (the storey's elevation + height, or ZMax if lower; ZMax when the caller has no model). It was the median of the columns' tops from pass 1, which did nothing
        // once furniture or a platform covered over half the floor (the median column then ended under it: desks on 58 % of the plan lost 41 % of the air), and let the fill into the upper storey when a stair void covered over half of
        // the plan. A stair void through the upper slab keeps its full column (pass 1 as before); the upper storey's air beside it is above the storey top. Slabs still stop the fill: solid, or Cell-Solid seeds are not expanded through.
        var col=new sbyte[g.Nx*g.Ny]; Array.Fill(col,(sbyte)-1);   // per column: -1 not tested yet, else 0/1: the column's centre against the loops
        bool Inside(int x,int y) { int k=y*g.Nx+x; if(col[k]<0) { var c=g.CellCenter(x,y,0); col[k]=(sbyte)(InsideLoops(room.Loops,new Vector2(c.X,c.Y)) ? 1 : 0); } return col[k]==1; }
        float cap=storeyTop is float st ? MathF.Min(st,room.ZMax) : room.ZMax;
        for(int y=0;y<g.Ny;y++) for(int x=0;x<g.Nx;x++)
        {
            bool air=false;
            for(int z=0;z<g.Nz;z++)
            {
                float cz=g.Origin.Z+(z+0.5f)*g.Dx;
                if(cz<room.ZMin) continue;
                if(cz>=room.ZMax) break;
                int i=g.Index(x,y,z);
                bool solid=g.Cell[i]==VoxelGrid.Solid, fluid=lbm ? Voxelizer.LbmFluid(g,i) : !solid;
                if(!fluid) { if(air) break; continue; }   // the floor below the air, then the ceiling above it
                if(!Inside(x,y)) break;
                mask[i]=true;count++;
                if(solid) { if(air) break; }   // a floor-top or ceiling cell that LBM counts as fluid
                else air=true;
            }
        }
        var q=new Queue<int>();
        for(int i=0;i<g.Count;i++) if(mask[i] && g.Cell[i]!=VoxelGrid.Solid) q.Enqueue(i);
        while(q.Count>0)
        {
            int i=q.Dequeue(), x=i%g.Nx, y=i/g.Nx%g.Ny, z=i/(g.Nx*g.Ny);
            for(int k=0;k<6;k++)
            {
                int nx=x+(k==0?-1:k==1?1:0), ny=y+(k==2?-1:k==3?1:0), nz=z+(k==4?-1:k==5?1:0);
                if(nx<0||nx>=g.Nx||ny<0||ny>=g.Ny||nz<0||nz>=g.Nz) continue;
                float cz=g.Origin.Z+(nz+0.5f)*g.Dx;
                if(cz<room.ZMin||cz>=cap) continue;
                int j=g.Index(nx,ny,nz);
                if(mask[j]||!(lbm ? Voxelizer.LbmFluid(g,j) : g.Cell[j]!=VoxelGrid.Solid)||!Inside(nx,ny)) continue;
                mask[j]=true;count++;
                if(g.Cell[j]!=VoxelGrid.Solid) q.Enqueue(j);
            }
        }
        return mask;
    }

    // -2 blocked/interior; -1 open domain boundary; otherwise neighboring air-cell index.
    /// <param name="lbm">6a: blocked only by a cell that is not LBM-fluid (the room-face link flux); otherwise by Cell's Solid.</param>
    internal static int FaceNeighbor(VoxelGrid g,bool[] mask,int x,int y,int z,int dx,int dy,int dz,bool lbm=false)
    {
        int nx=x+dx,ny=y+dy,nz=z+dz;
        if(nz<0 && !g.OpenZMin || nz>=g.Nz && !g.OpenZMax) return -2;
        if(nx<0||nx>=g.Nx||ny<0||ny>=g.Ny||nz<0||nz>=g.Nz) return -1;
        int j=g.Index(nx,ny,nz);
        return (lbm ? !Voxelizer.LbmFluid(g,j) : g.Cell[j]==VoxelGrid.Solid)||mask[j] ? -2 : j;
    }
}
