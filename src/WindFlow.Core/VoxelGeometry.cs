using System.Collections.Concurrent;
using System.Numerics;

namespace WindFlow.Core;

public static partial class Voxelizer
{
    // 6a: closed, consistently oriented components are material at the cell centres they contain (the LBM mask); every surface,
    // open sheets included, also cuts the links that cross it (ComputeWallLinks). Independent shells union; oppositely oriented
    // nested shells define cavities. Separate Mesh objects always union.
    readonly record struct Triangle(Vector3 A, Vector3 B, Vector3 C, int Material, bool Furniture = false)
    {
        public Vector3 Min => Vector3.Min(A, Vector3.Min(B, C));
        public Vector3 Max => Vector3.Max(A, Vector3.Max(B, C));
    }

    static List<Triangle> GeometryTriangles(List<Mesh> meshes)
    {
        var result = new List<Triangle>();
        int nextMaterial = 0;
        for (int m = 0; m < meshes.Count; m++)
        {
            var mesh = meshes[m]; int nt = mesh.Indices.Length / 3;
            var ids = new Dictionary<Vector3, int>();
            var welded = mesh.Vertices.Select(v => { if (!ids.TryGetValue(v, out int id)) ids[v] = id = ids.Count; return id; }).ToArray();
            var edges = new Dictionary<(int, int), List<(int Tri, int Sign)>>();
            for (int t = 0; t < nt; t++)
            for (int e = 0; e < 3; e++)
            {
                int a = welded[mesh.Indices[3*t+e]], b = welded[mesh.Indices[3*t+(e+1)%3]];
                var key = (Math.Min(a,b), Math.Max(a,b));
                if (!edges.TryGetValue(key, out var list)) edges[key] = list = new();
                list.Add((t, a < b ? 1 : -1));
            }
            var adjacent = Enumerable.Range(0, nt).Select(_ => new List<int>()).ToArray();
            var bad = new bool[nt];
            foreach (var (edge, list) in edges)
            {
                // 6a addendum: closed = every undirected edge is used as often A->B as B->A (multiplicity counts), so abutting or
                // overlapping consistently oriented boxes merged into one mesh (non-manifold unions) are closed volumes; their shared
                // internal faces cancel in the signed winding number. Flipped normals or a hole unbalance an edge: an open sheet.
                bool invalid = edge.Item1 == edge.Item2 || list.Sum(x => x.Sign) != 0;
                foreach (var v in list)
                {
                    bad[v.Tri] |= invalid;
                    foreach (var other in list) if (other.Tri != v.Tri) adjacent[v.Tri].Add(other.Tri);
                }
            }
            var seen = new bool[nt]; var materials = Enumerable.Repeat(-1,nt).ToArray();
            var components = new List<int[]>();
            for (int t = 0; t < nt; t++)
            {
                if (seen[t]) continue;
                var component = new List<int>(); var todo = new Stack<int>(); todo.Push(t); seen[t] = true;
                bool valid = true;
                while (todo.TryPop(out int k))
                {
                    component.Add(k); valid &= !bad[k];
                    foreach (int j in adjacent[k]) if (!seen[j]) { seen[j] = true; todo.Push(j); }
                }
                if (valid) components.Add(component.ToArray());
            }
            Triangle Tri(int t) => new(mesh.Vertices[mesh.Indices[3*t]],mesh.Vertices[mesh.Indices[3*t+1]],mesh.Vertices[mesh.Indices[3*t+2]],0);
            var shells = components.Select(c =>
            {
                var ts=c.Select(Tri).ToArray();
                Vector3 lo=ts.Select(t=>t.Min).Aggregate(Vector3.Min),hi=ts.Select(t=>t.Max).Aggregate(Vector3.Max);
                // Translation before the volume sum avoids cancellation for models far from the origin.
                double volume=ts.Sum(t=>(double)Vector3.Dot(t.A-lo,Vector3.Cross(t.B-lo,t.C-lo)));
                return (Cells:c,Triangles:ts,Lo:lo,Hi:hi,Volume:Math.Abs(volume),Sign:Math.Sign(volume));
            }).OrderByDescending(c=>c.Volume).ToArray();
            var groups=new int[shells.Length];
            for(int c=0;c<shells.Length;c++)
            {
                groups[c]=nextMaterial++;
                // Independent shells union, even when their winding is reversed. An oppositely oriented
                // shell wholly inside another is its cavity and contributes to that shell's winding.
                for(int parent=c-1;parent>=0;parent--)
                {
                    var child=shells[c];var outer=shells[parent];
                    if(child.Lo.X<outer.Lo.X||child.Lo.Y<outer.Lo.Y||child.Lo.Z<outer.Lo.Z||
                       child.Hi.X>outer.Hi.X||child.Hi.Y>outer.Hi.Y||child.Hi.Z>outer.Hi.Z) continue;
                    var p=child.Triangles[0].A;int winding=0;
                    foreach(var t in outer.Triangles) if(XCrossing(t,p.Y,p.Z,out double x,out int sign)&&x>p.X) winding+=sign;
                    if(winding==0) continue;
                    if(child.Sign!=outer.Sign) groups[c]=groups[parent];
                    break;
                }
                foreach(int t in shells[c].Cells) materials[t]=groups[c];
            }
            for (int t = 0; t < nt; t++)
                result.Add(new(mesh.Vertices[mesh.Indices[3*t]], mesh.Vertices[mesh.Indices[3*t+1]],
                    mesh.Vertices[mesh.Indices[3*t+2]], materials[t], mesh.Furniture));
        }
        return result;
    }

    // Half-open projected triangles partition a planar face without double-counting its diagonal.
    static bool XCrossing(Triangle t, double y, double z, out double x, out int sign)
    {
        var a = t.A; var b = t.B; var c = t.C;
        double area = ((double)b.Y-a.Y)*(c.Z-a.Z)-((double)b.Z-a.Z)*(c.Y-a.Y);
        x = 0; sign = Math.Sign(area);
        if (Math.Abs(area) < 1e-20) return false;
        if (area < 0) (b,c) = (c,b);
        static double Edge(Vector3 p, Vector3 q, double y, double z) => ((double)q.Y-p.Y)*(z-p.Z)-((double)q.Z-p.Z)*(y-p.Y);
        static bool Inside(double e, Vector3 p, Vector3 q) => e > 0 || (e == 0 && (q.Z > p.Z || (q.Z == p.Z && q.Y < p.Y)));
        double e0 = Edge(a,b,y,z), e1 = Edge(b,c,y,z), e2 = Edge(c,a,y,z);
        if (!Inside(e0,a,b) || !Inside(e1,b,c) || !Inside(e2,c,a)) return false;
        x = (e1*a.X + e2*b.X + e0*c.X) / Math.Abs(area);
        return true;
    }

    static void FillObstacleVolumes(VoxelGrid g, List<Mesh> meshes)
    {
        foreach (var group in GeometryTriangles(meshes).Where(t => t.Material >= 0).GroupBy(t => t.Material))
        {
            var rows = new Dictionary<int, List<(double X, int Sign)>>();
            foreach (var t in group)
            {
                int y0 = Math.Clamp((int)MathF.Floor((t.Min.Y-g.Origin.Y)/g.Dx),0,g.Ny-1);
                int y1 = Math.Clamp((int)MathF.Floor((t.Max.Y-g.Origin.Y)/g.Dx),0,g.Ny-1);
                int z0 = Math.Clamp((int)MathF.Floor((t.Min.Z-g.Origin.Z)/g.Dx),0,g.Nz-1);
                int z1 = Math.Clamp((int)MathF.Floor((t.Max.Z-g.Origin.Z)/g.Dx),0,g.Nz-1);
                for (int z=z0; z<=z1; z++) for (int y=y0; y<=y1; y++)
                {
                    var p = g.CellCenter(0,y,z);
                    if (!XCrossing(t,p.Y,p.Z,out double x,out int s)) continue;
                    int row = y+g.Ny*z;
                    if (!rows.TryGetValue(row,out var list)) rows[row] = list = new();
                    list.Add((x,s));
                }
            }
            foreach (var (row, events) in rows)
            {
                events.Sort((a,b)=>a.X.CompareTo(b.X));
                int k=0, winding=0;
                for (int x=0; x<g.Nx; x++)
                {
                    double cx = g.Origin.X+(x+0.5)*g.Dx;
                    while(k<events.Count && events[k].X<=cx) winding += events[k++].Sign;
                    if(winding!=0) g.Cell[x+g.Nx*row]=VoxelGrid.Solid;
                }
            }
        }
    }

    sealed class SurfaceTree
    {
        readonly Triangle[] tris;
        readonly Node root;
        sealed class Node
        {
            public Vector3 Min, Max;
            public int Start, Count;
            public Node? Left, Right;
        }
        public SurfaceTree(List<Triangle> triangles) { tris=triangles.ToArray(); root=Build(0,tris.Length); }
        Node Build(int start,int count)
        {
            var n = new Node { Start=start, Count=count, Min=new(float.PositiveInfinity), Max=new(float.NegativeInfinity) };
            for(int i=start;i<start+count;i++) { n.Min=Vector3.Min(n.Min,tris[i].Min); n.Max=Vector3.Max(n.Max,tris[i].Max); }
            if(count<=8) return n;
            var d=n.Max-n.Min; int axis=d.X>=d.Y && d.X>=d.Z?0:d.Y>=d.Z?1:2;
            float Coord(Triangle t) => axis==0?t.Min.X+t.Max.X:axis==1?t.Min.Y+t.Max.Y:t.Min.Z+t.Max.Z;
            Array.Sort(tris,start,count,Comparer<Triangle>.Create((a,b)=>Coord(a).CompareTo(Coord(b))));
            n.Left=Build(start,count/2); n.Right=Build(start+count/2,count-count/2); return n;
        }
        static bool Intersects(Node n,Vector3 a,Vector3 b) =>
            Vector3.Min(a,b).X<=n.Max.X && Vector3.Max(a,b).X>=n.Min.X &&
            Vector3.Min(a,b).Y<=n.Max.Y && Vector3.Max(a,b).Y>=n.Min.Y &&
            Vector3.Min(a,b).Z<=n.Max.Z && Vector3.Max(a,b).Z>=n.Min.Z;

        /// <summary>Fraction along a->b of the nearest crossing that <paramref name="removed"/> does not reject, or +inf. 6a: watertight
        /// (Woop, Benthin and Wald 2013, in double): the sheared vertex coordinates depend only on the vertex and the link, so the edge
        /// function of an edge two triangles share is the exact negative in the other triangle, and a zero edge function (the link passes
        /// through the edge or a vertex) belongs to one triangle only, by a top-left rule on the edge as oriented after the sign flip.
        /// A link through a seam therefore hits exactly one triangle; a missed hit would be a leak.</summary>
        public float Hit(Vector3 a,Vector3 b,Func<Vector3,bool> removed) => Hit(a,b,removed,null,out _);

        /// <summary>RP-05: <see cref="Hit(Vector3,Vector3,Func{Vector3,bool})"/> over the triangles <paramref name="accept"/> admits (null = all), and the material of the one hit.</summary>
        public float Hit(Vector3 a,Vector3 b,Func<Vector3,bool> removed,Func<Triangle,bool>? accept,out int material)
        {
            float best=float.PositiveInfinity; var dir=b-a; int bestMaterial=-1;
            static double C(Vector3 v,int k) => k==0?v.X:k==1?v.Y:v.Z;
            int kz=MathF.Abs(dir.X)>=MathF.Abs(dir.Y)?(MathF.Abs(dir.X)>=MathF.Abs(dir.Z)?0:2):(MathF.Abs(dir.Y)>=MathF.Abs(dir.Z)?1:2);
            int kx=(kz+1)%3,ky=(kx+1)%3; if(C(dir,kz)<0) (kx,ky)=(ky,kx);
            double sx=C(dir,kx)/C(dir,kz),sy=C(dir,ky)/C(dir,kz),sz=1.0/C(dir,kz);
            double ox=C(a,kx),oy=C(a,ky),oz=C(a,kz);
            void Shear(Vector3 v,out double px,out double py,out double pz)
            {
                double vx=C(v,kx)-ox,vy=C(v,ky)-oy,vz=C(v,kz)-oz;
                px=vx-sx*vz; py=vy-sy*vz; pz=sz*vz;
            }
            // e = 0 on the edge p->q (as oriented after the flip): owned when the edge points "up", or exactly sideways to +x.
            static bool Owns(double e,double px,double py,double qx,double qy) => e>0 || e==0 && (qy-py>0 || qy-py==0 && qx-px>0);
            void Visit(Node n)
            {
                if(!Intersects(n,a,b)) return;
                if(n.Left!=null) { Visit(n.Left); Visit(n.Right!); return; }
                for(int i=n.Start;i<n.Start+n.Count;i++)
                {
                    var t=tris[i];
                    if(accept!=null && !accept(t)) continue;
                    Shear(t.A,out double ax,out double ay,out double az); Shear(t.B,out double bx,out double by,out double bz); Shear(t.C,out double cx,out double cy,out double cz);
                    double u=cx*by-cy*bx, v=ax*cy-ay*cx, w=bx*ay-by*ax, det=u+v+w;
                    if(det==0) continue;   // the link lies in the triangle's plane (or the triangle is degenerate seen along it)
                    bool inside = det>0
                        ? Owns(u,bx,by,cx,cy) && Owns(v,cx,cy,ax,ay) && Owns(w,ax,ay,bx,by)
                        : Owns(-u,cx,cy,bx,by) && Owns(-v,ax,ay,cx,cy) && Owns(-w,bx,by,ax,ay);
                    if(!inside) continue;
                    double tt=(u*az+v*bz+w*cz)/det;
                    if(tt<0 || tt>1) continue;
                    float f=(float)tt;
                    if(f>=best || removed(a+f*dir)) continue;
                    best=f; bestMaterial=t.Material;
                }
            }
            Visit(root); material=bestMaterial; return best;
        }

        /// <summary>Is <paramref name="p"/> inside a closed volume (RP-05: of material <paramref name="onlyMaterial"/> alone when it is not -1)?</summary>
        public bool Contains(Vector3 p,int onlyMaterial=-1)
        {
            var winding=new Dictionary<int,int>();
            void Visit(Node n)
            {
                if(n.Max.X<p.X || p.Y<n.Min.Y || p.Y>n.Max.Y || p.Z<n.Min.Z || p.Z>n.Max.Z) return;
                if(n.Left!=null) { Visit(n.Left); Visit(n.Right!); return; }
                for(int i=n.Start;i<n.Start+n.Count;i++)
                {
                    var t=tris[i];
                    if(t.Material>=0 && (onlyMaterial<0 || t.Material==onlyMaterial) && XCrossing(t,p.Y,p.Z,out double x,out int sign) && x>p.X)
                        winding[t.Material]=winding.GetValueOrDefault(t.Material)+sign;
                }
            }
            Visit(root); return winding.Values.Any(x=>x!=0);
        }
    }

    readonly record struct CutBox(Vector3 Center,Vector3 N,Vector3 T,float W,float H,float D)
    {
        public Vector3 Local(Vector3 p) { p-=Center; return new(Vector3.Dot(p,N),Vector3.Dot(p,T),p.Z); }
        public bool Contains(Vector3 p) { var v=Local(p); return MathF.Abs(v.X)<=D && MathF.Abs(v.Y)<=W && MathF.Abs(v.Z)<=H; }
        public float Exit(Vector3 a,Vector3 b)
        {
            var p=Local(a); var v=Local(b)-p; float enter=0,exit=1;
            bool Clip(float x,float d,float half)
            {
                if(MathF.Abs(d)<1e-12f) return MathF.Abs(x)<=half;
                float lo=(-half-x)/d,hi=(half-x)/d; if(lo>hi)(lo,hi)=(hi,lo);
                enter=MathF.Max(enter,lo); exit=MathF.Min(exit,hi); return enter<=exit;
            }
            return Clip(p.X,v.X,D)&&Clip(p.Y,v.Y,W)&&Clip(p.Z,v.Z,H)&&exit>=0&&exit<=1 ? exit : float.PositiveInfinity;
        }
    }

    static readonly int[] LinkX=[0,1,-1,0,0,0,0,1,-1,1,-1,1,-1,1,-1,0,0,0,0];
    static readonly int[] LinkY=[0,0,0,1,-1,0,0,1,-1,-1,1,0,0,0,0,1,-1,1,-1];
    static readonly int[] LinkZ=[0,0,0,0,0,1,-1,0,0,0,0,1,-1,-1,1,1,-1,-1,1];
    static readonly int[] LinkOpp=[0,2,1,4,3,6,5,8,7,10,9,12,11,14,13,16,15,18,17];

    /// <summary>6a (P12-01): the wall links of the cell-centre mask (g.Cell at this point: Solid = centre inside a closed volume, a closed
    /// opening, the ground layer), against the final obstacle / opening geometry. For every fluid cell and every D3Q19 link, in pull order
    /// q*Count+cell, the segment from the cell centre to the neighbour centre it pulls from is tested against every surface triangle
    /// (obstacles plus closed-opening boxes; hits inside open-opening cut boxes are removed, a jamb is at the cut-box exit). A link that
    /// crosses a surface, or whose neighbour is Solid, is a wall link: q = the fraction to the nearest crossing, clamped to [1/254, 1],
    /// encoded 1 + round(254 q). This includes links between two fluid cells (a wall thinner than a cell: each side gets its own q).
    /// A link from the first air layer into the ground layer (<paramref name="groundLayer"/>) with no surface on it has the ground plane,
    /// the face between the two layers, at q = 1/2. 0 = a free link, or halfway bounce-back where a Solid neighbour has no surface
    /// (a closed box face, a sealed cell). <paramref name="through"/>: per open opening, the normal-coordinate range of the cells its
    /// carve-through cleared (<see cref="WallRunCells"/>); its cut box is stretched to the wall face behind them, so the carved hole has no
    /// cut link across it.</summary>
    internal static void ComputeWallLinks(VoxelGrid g,List<Mesh> meshes,List<Opening> openings,int? maxDop=null,
        Dictionary<Opening,(float Lo,float Hi)>? through=null,bool groundLayer=false)
    {
        var triangles=GeometryTriangles(meshes);
        int closedOpeningMaterial=triangles.Count==0?0:triangles.Max(t=>t.Material)+1;
        var cuts=new List<CutBox>(); var cutOpenings=new List<Opening>();
        foreach(var o in openings)
        {
            var n=new Vector3(o.Normal.X,o.Normal.Y,0); if(n.LengthSquared()<1e-8f) continue;
            n=Vector3.Normalize(n); if(n.X<0||(n.X==0&&n.Y<0)) n=-n;   // the carve's canonical side (ApplyOpening); the box itself is symmetric
            var t=new Vector3(-n.Y,n.X,0);
            // 6d (P13a-03): an open box is 1e-3 Dx wide each side in W and H, so a link that crosses the wall exactly ON the hole's edge (a hole
            // drawn on cell faces: every even-n window) is inside it at the lower and the upper jamb alike; float noise used to decide, and
            // it cut the lower-y / lower-z jambs only (cells beside the plate went Solid + LbmAir on one side of the hole).
            float edge=o.IsOpen?1e-3f*g.Dx:0f;
            var box=new CutBox(o.Center,n,t,MathF.Max(o.Width,g.Dx)/2+edge,MathF.Max(o.Height,g.Dx)/2+edge,
                o.IsOpen?o.Depth/2+0.6f*g.Dx:MathF.Max(o.Depth,g.Dx)/2);
            if(o.IsOpen) { cuts.Add(box); cutOpenings.Add(o); continue; }
            // RY-03: the closed box is the only seal now (no jamb cells), so it overlaps the wall by 0.5 Dx per side in W and H: a hole wider than the box leaves no crack
            box=box with { W=box.W+0.5f*g.Dx, H=box.H+0.5f*g.Dx };
            var v=new Vector3[8];
            for(int k=0;k<8;k++) v[k]=o.Center+n*((k&1)==0?-box.D:box.D)+t*((k&2)==0?-box.W:box.W)+Vector3.UnitZ*((k&4)==0?-box.H:box.H);
            int[] ix=[0,2,1,1,2,3,4,5,6,5,7,6,0,1,4,1,5,4,2,6,3,3,6,7,0,4,2,2,4,6,1,3,5,3,7,5];
            for(int k=0;k<ix.Length;k+=3) triangles.Add(new(v[ix[k]],v[ix[k+1]],v[ix[k+2]],closedOpeningMaterial+openings.IndexOf(o)));
        }
        // A nonempty lookup explicitly disables the old unsigned-distance inference.
        g.WallLinks=new byte[checked(g.Count*19)];
        var tree=triangles.Count==0?null:new SurfaceTree(triangles);
        // RP-05 / RP-06: the wall each open opening sits in, and its thickness along the normal (Opening.MeasuredDepth, 0 = none found: Depth is used).
        foreach(var o in openings) o.MeasuredDepth=0f;
        var own=new (int Mat,int Side,float Near,float Thick)[cuts.Count];
        for(int k=0;k<cuts.Count;k++)
        {
            own[k]=(-1,0,float.PositiveInfinity,0f);
            if(tree==null) continue;
            own[k]=OwnWall(tree,cuts[k],cutOpenings[k].Depth*0.5f+g.Dx,g.Dx);
            if(own[k].Mat>=0 && own[k].Thick>0f) cutOpenings[k].MeasuredDepth=own[k].Thick;
        }
        if(tree!=null && through!=null)
            for(int k=0;k<cuts.Count;k++)
            {
                var c=cuts[k]; float hi=c.D, lo=c.D;
                if(!through.TryGetValue(cutOpenings[k],out var run))
                {
                    // RY-04: no cleared cell (a wall thinner than a cell holds no cell centre): the first surface of the opening's own wall (OwnWall, RP-05: a closed,
                    // non-furniture volume covering the whole W x H, within the wall span Depth/2 + 1 Dx, the RX-05 span) and the face it leaves by (a thin wall's far side);
                    // never a second surface across air. RQ-02: one way only, toward that wall; the far side could only meet a second object across air and must stay intact.
                    var w=own[k]; float tiny=1e-3f*g.Dx;
                    if(w.Mat>=0)
                    {
                        float d1=w.Near+tiny, reach=d1; var past=c.Center+c.N*(w.Side*d1);
                        if(tree.Contains(past,w.Mat)) { float h2=tree.Hit(past,past+c.N*(w.Side*g.Dx),_=>false,t=>t.Material==w.Mat,out _); if(float.IsFinite(h2)) reach=d1+h2*g.Dx+tiny; }
                        if(w.Side>0) hi=MathF.Max(hi,reach); else lo=MathF.Max(lo,reach);
                    }
                    if(hi!=c.D || lo!=c.D) cuts[k]=c with { Center=c.Center+c.N*(0.5f*(hi-lo)), D=0.5f*(hi+lo) };
                    continue;
                }
                // From the deepest cleared centre on each side, along the normal: the first face met is the wall's own far face.
                float Reach(float e,int side)
                {
                    var p0=c.Center+c.N*e; float h=tree.Hit(p0,p0+c.N*(side*2f*g.Dx),_=>false);
                    return side*e+(float.IsFinite(h)?h*2f*g.Dx+1e-3f*g.Dx:0.5f*g.Dx);
                }
                if(run.Hi>0) hi=MathF.Max(hi,Reach(run.Hi,1));
                if(run.Lo<0) lo=MathF.Max(lo,Reach(run.Lo,-1));
                cuts[k]=c with { Center=c.Center+c.N*(0.5f*(hi-lo)), D=0.5f*(hi+lo) };
            }
        // Broad phase: a crossing lies in the closed box of one of the link's two cells, so only links touching a cell whose box (+eps)
        // overlaps a triangle can be cut; cells in and around an open cut box also take the jamb test.
        var near=new bool[g.Count];
        var po=new ParallelOptions { MaxDegreeOfParallelism=maxDop is >0 ? maxDop.Value : -1 };
        float eps=1e-3f*g.Dx;
        void Mark(Vector3 lo,Vector3 hi,Func<Vector3,bool>? test)
        {
            lo-=g.Origin; hi-=g.Origin;
            int x0=Math.Clamp((int)MathF.Floor((lo.X-eps)/g.Dx),0,g.Nx-1),x1=Math.Clamp((int)MathF.Floor((hi.X+eps)/g.Dx),0,g.Nx-1);
            int y0=Math.Clamp((int)MathF.Floor((lo.Y-eps)/g.Dx),0,g.Ny-1),y1=Math.Clamp((int)MathF.Floor((hi.Y+eps)/g.Dx),0,g.Ny-1);
            int z0=Math.Clamp((int)MathF.Floor((lo.Z-eps)/g.Dx),0,g.Nz-1),z1=Math.Clamp((int)MathF.Floor((hi.Z+eps)/g.Dx),0,g.Nz-1);
            for(int z=z0;z<=z1;z++) for(int y=y0;y<=y1;y++) for(int x=x0;x<=x1;x++)
                if(test==null||test(g.CellCenter(x,y,z))) near[g.Index(x,y,z)]=true;   // the same value from every writer: race-safe
        }
        var boxHalf=new Vector3(0.5f*g.Dx+eps);
        Parallel.ForEach(Partitioner.Create(0,triangles.Count),po,range=>
        {
            for(int k=range.Item1;k<range.Item2;k++)
            {
                var tr=triangles[k];
                Mark(tr.Min,tr.Max,c=>TriBoxOverlap(c,boxHalf,tr.A,tr.B,tr.C));
            }
        });
        foreach(var c in cuts)
        {
            var reach=Vector3.Abs(c.N)*c.D+Vector3.Abs(c.T)*c.W+Vector3.UnitZ*c.H+new Vector3(g.Dx);
            Mark(c.Center-reach,c.Center+reach,null);
        }
        bool Removed(Vector3 p)=>cuts.Any(c=>c.Contains(p));
        byte half=(byte)(1+127);   // q = 1/2 exactly
        Parallel.For(0,g.Count,po,i=>
        {
            if(g.Cell[i]==VoxelGrid.Solid) return;
            int x=i%g.Nx,y=i/g.Nx%g.Ny,z=i/(g.Nx*g.Ny); var a=g.CellCenter(x,y,z);
            for(int q=1;q<19;q++)
            {
                int sx=x-LinkX[q],sy=y-LinkY[q],sz=z-LinkZ[q];
                if(sx<0||sx>=g.Nx||sy<0||sy>=g.Ny||sz<0||sz>=g.Nz) continue;
                int s=g.Index(sx,sy,sz);
                float f=float.PositiveInfinity;
                if(tree!=null && (near[i]||near[s]))
                {
                    var b=g.CellCenter(sx,sy,sz); f=tree.Hit(a,b,Removed);
                    foreach(var cut in cuts)
                    {
                        float exit=cut.Exit(a,b);
                        if(exit<f && exit<1)
                        {
                            // 6a: material just past the exit (not at it): a link that only grazes the hole's edge where the jamb meets
                            // the wall face leaves into air, and must not be cut there.
                            var after=Vector3.Lerp(a,b,MathF.Min(1,exit+1e-4f));
                            if(!Removed(after) && tree.Contains(after)) f=exit;
                        }
                    }
                }
                if(float.IsFinite(f)) g.WallLinks[q*g.Count+i]=(byte)(1+(int)MathF.Round(254*Math.Clamp(f,1f/254,1f)));
                else if(groundLayer && sz==0 && z==1 && g.Cell[s]==VoxelGrid.Solid) g.WallLinks[q*g.Count+i]=half;
            }
        });
    }

    /// <summary>RP-05 / RP-06: the wall an open opening sits in. From the cut box centre along the normal, on both sides, the nearest closed volume that is not
    /// furniture (Mesh.Furniture) and whose cross-section covers the opening's whole W x H, within the wall span <paramref name="span"/> (or holding the centre, up to
    /// <see cref="CarveThroughCap"/> m: a wall thicker than the drawn Depth); a volume that does not cover it (a pilaster, a jamb) is passed over. None covers: the nearest
    /// closed non-furniture volume. A tie takes +n. Returns its material, the side, the distance to its first face, and its thickness along the normal at the centre
    /// (the exit faces when the centre is inside it, else near face to far face; 0 when the far face is not found within the cap).</summary>
    static (int Mat,int Side,float Near,float Thick) OwnWall(SurfaceTree tree,CutBox c,float span,float dx)
    {
        float tiny=1e-3f*dx, hw=c.W-2*tiny, hh=c.H-2*tiny, reachMax=MathF.Max(span,0.6f);
        bool Covers(int m,float e)
        {
            for(int k=0;k<4;k++)
                if(!tree.Contains(c.Center+c.N*e+c.T*((k&1)==0?-hw:hw)+Vector3.UnitZ*((k&2)==0?-hh:hh),m)) return false;
            return true;
        }
        (int Mat,int Side,float Near,bool Covers) best=(-1,0,float.PositiveInfinity,false);
        for(int side=1;side>=-1;side-=2)
        {
            var rejected=new List<int>();
            for(int it=0;it<6;it++)
            {
                float h=tree.Hit(c.Center,c.Center+c.N*(side*reachMax),_=>false,t=>t.Material>=0 && !t.Furniture && !rejected.Contains(t.Material),out int m);
                if(!float.IsFinite(h)) break;
                float d=h*reachMax;
                if(d>span && !tree.Contains(c.Center,m)) { rejected.Add(m); continue; }   // beyond the wall span, and not the wall the centre is in
                bool covers=Covers(m,side*(d+tiny)) || Covers(m,side*(d-tiny));          // inside the volume past an entry face, or just before an exit face
                if(covers ? !best.Covers || d<best.Near : !best.Covers && d<best.Near) best=(m,side,d,covers);
                if(covers) break;
                rejected.Add(m);
            }
        }
        if(best.Mat<0) return (-1,0,float.PositiveInfinity,0f);
        const float cap=CarveThroughCap;
        float thick=0f; int mat=best.Mat;
        if(tree.Contains(c.Center,mat))
        {
            float hp=tree.Hit(c.Center,c.Center+c.N*cap,_=>false,t=>t.Material==mat,out _), hm=tree.Hit(c.Center,c.Center-c.N*cap,_=>false,t=>t.Material==mat,out _);
            if(float.IsFinite(hp) && float.IsFinite(hm)) thick=(hp+hm)*cap;
        }
        else
        {
            var past=c.Center+c.N*(best.Side*(best.Near+tiny));
            float h2=tree.Hit(past,past+c.N*(best.Side*cap),_=>false,t=>t.Material==mat,out _);
            if(float.IsFinite(h2)) thick=h2*cap+tiny;
        }
        return (best.Mat,best.Side,best.Near,thick);
    }

    /// <summary>6a: the conservative <see cref="VoxelGrid.Cell"/> mask every non-LBM consumer reads, from the cell-centre mask and its cut
    /// links (<see cref="ComputeWallLinks"/>). A cut link must be cut from both ends (the populations of a link cannot stream one way only),
    /// so an end the other end saw cut but that found no crossing itself takes q = 1 - q(other). Then of every cut link between two fluid
    /// cells, the end whose centre is nearer the crossing (the smaller q; a tie goes to the lower index) becomes Solid and is listed in
    /// <see cref="VoxelGrid.LbmAir"/>: the LBM still streams it, but no uncut link joins two cells that are air in Cell, so any D3Q19 path
    /// in Cell is leak-tight for walls of any thickness. A cell an open opening carved (<paramref name="carved"/>) stays air in both
    /// masks: a link from the hole that clips the wall at the hole's edge marks its other end instead.</summary>
    internal static void ConservativeMask(VoxelGrid g,bool[]? carved=null)
    {
        int n=g.Count; var links=g.WallLinks;
        if(links.Length!=19*n) return;
        void Pairs(Action<int,int,int> pair) => Parallel.For(0,n,i=>
        {
            if(g.Cell[i]==VoxelGrid.Solid) return;
            int x=i%g.Nx,y=i/g.Nx%g.Ny,z=i/(g.Nx*g.Ny);
            for(int q=1;q<19;q++)
            {
                int sx=x-LinkX[q],sy=y-LinkY[q],sz=z-LinkZ[q];
                if(sx<0||sx>=g.Nx||sy<0||sy>=g.Ny||sz<0||sz>=g.Nz) continue;
                int s=g.Index(sx,sy,sz);
                if(links[q*n+i]!=0 && g.Cell[s]!=VoxelGrid.Solid) pair(i,s,q);
            }
        });
        // (i, q) is written only from (s, opp) and the other way round, so the writes never collide.
        Pairs((i,s,q) => { if(links[LinkOpp[q]*n+s]==0) links[LinkOpp[q]*n+s]=(byte)(1+(int)MathF.Round(254*Math.Clamp(1f-(links[q*n+i]-1)/254f,1f/254,1f))); });
        var mark=new bool[n];
        Pairs((i,s,q) =>
        {
            int ci=links[q*n+i], cs=links[LinkOpp[q]*n+s];
            int end=ci<cs?i:cs<ci?s:Math.Min(i,s), other=end==i?s:i;
            if(carved!=null && carved[end] && !carved[other]) end=other;
            mark[end]=true;
        });
        bool any=false;
        for(int i=0;i<n;i++) if(mark[i]) { g.Cell[i]=VoxelGrid.Solid; any=true; }
        g.LbmAir=any?mark:[];
    }

    /// <summary>6a: is cell <paramref name="i"/> fluid for the LBM (not Solid, or Solid only in the conservative mask)?</summary>
    internal static bool LbmFluid(VoxelGrid g,int i) => g.Cell[i]!=VoxelGrid.Solid || g.LbmAir.Length==g.Count && g.LbmAir[i];

    /// <summary>6a: the cell the LBM-fluid cell (x, y, z) exchanges populations with along c_k: in the domain, LBM-fluid, link not cut; else -1.
    /// The connectivity the unreachable-air seal and the sealed-room balance walk.</summary>
    internal static int Linked(VoxelGrid g,int x,int y,int z,int k)
    {
        int nx=x+LinkX[k],ny=y+LinkY[k],nz=z+LinkZ[k];
        if(nx<0||nx>=g.Nx||ny<0||ny>=g.Ny||nz<0||nz>=g.Nz) return -1;
        int j=g.Index(nx,ny,nz);
        if(!LbmFluid(g,j)) return -1;
        // x pulls direction opp(k) from x - c_opp(k) = j: that is the link.
        return g.WallLinks.Length==19*g.Count && g.WallLinks[LinkOpp[k]*g.Count+g.Index(x,y,z)]!=0 ? -1 : j;
    }
}
