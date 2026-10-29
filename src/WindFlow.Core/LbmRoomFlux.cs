using ILGPU;
using ILGPU.Runtime;

namespace WindFlow.Core;

public static partial class LbmSolver
{
    // Two launches per averaging step for ALL faces (rooms then opening throats): a per-SEGMENT kernel (<= FluxSegLinks links each,
    // so a long link list such as an opening's throat plane is spread over many threads, not walked by one) and a per-FACE kernel
    // that adds a face's segment sums in order and runs the Kahan accumulation. A face of <= FluxSegLinks links has one segment, so
    // its sum is the same sequential float sum as before.
    const int FluxSegLinks=16;
    static Action<Index1D,ArrayView<int>,ArrayView<float>,ArrayView<float>,ArrayView<float>,int,int,int>? _roomFluxSegKernel;
    static Action<Index1D,ArrayView<int>,ArrayView<float>,ArrayView<float>,int>? _roomFluxFaceKernel;

    // Room-face mass flux = D3Q19 LINK flux across the room boundary (conserved), not a node-velocity mean.
    // Every lattice link A -> D from a room cell A to a non-room, non-wall cell D crosses the boundary once and
    // carries f*_q(A) - f*_qbar(D) (net outward). RoomFluxPlan owns each such link to ONE room face (see
    // RoomFluxPlan.Build); a face's flux is the sum over its links. On a planar section this is the textbook
    // sum_{c_a=s} f*(A) - sum_{c_a=-s} f*(B); owning diagonals per link (instead of per source-cell face) keeps
    // the sum exact at jamb and corner cells, where the face-by-face formula counts a diagonal twice or never.
    // f* are the POST-COLLISION (pre-stream) populations of this step: the accumulator runs after the
    // collide/stream kernel and before the cur/nxt swap, so f* sits in nxt for every cell the step collided
    // and in cur for cells it did not collide (Fixed, domain-edge ring, open-z rows), whose populations are
    // the ones neighbours stream from. Links whose destination is a wall (Solid, closed z) bounce back and are
    // not links here. An open domain boundary face (Outside < 0) has no D cell and no conserved crossing: it keeps the
    // old reading, the signed node velocity u_a = m_a/rho of the cell (Map() below picks the cell), so only in-domain
    // faces are link flux.
    // meta = [faces+1 link starts][3 ints per link: A, D, q | 32 = no D leg, | 64 = no A leg (links past a domain edge, D = A: a leg whose destination is a wall bounces back); qf >= 128 = open-boundary face, one entry holding 128 + 2*axis + (sign>0): flux = sign * m_axis / rho of cell A; A and D encode i >= 0 = collided (read nxt), ~i = read cur.
    internal static void LinkDir(int k,out int dx,out int dy,out int dz)
    {
        dx=0;dy=0;dz=0;
        switch(k)
        {
            case 1: dx=1; break; case 2: dx=-1; break; case 3: dy=1; break; case 4: dy=-1; break;
            case 5: dz=1; break; case 6: dz=-1; break;
            case 7: dx=1;dy=1; break; case 8: dx=-1;dy=-1; break; case 9: dx=1;dy=-1; break; case 10: dx=-1;dy=1; break;
            case 11: dx=1;dz=1; break; case 12: dx=-1;dz=-1; break; case 13: dx=1;dz=-1; break; case 14: dx=-1;dz=1; break;
            case 15: dy=1;dz=1; break; case 16: dy=-1;dz=-1; break; case 17: dy=1;dz=-1; break; default: dy=-1;dz=1; break;
        }
    }

    static bool LinkWall(byte[] cell,GridDims d,int x,int y,int z)
    {
        if(z<0) return d.OpenZMin==0; if(z>=d.Nz) return d.OpenZMax==0;
        if(x<0||x>=d.Nx||y<0||y>=d.Ny) return false;
        return cell[x+d.Nx*(y+d.Ny*z)]==VoxelGrid.Solid;
    }

    // Each face owns its four slots: inward/outward sums and their Kahan compensations. No atomics
    // or room-wide float reductions are needed. The host reduces the face means in double precision.
    // meta (GPU, merged rooms + openings) = [faces+1 first-segment indices][segs+1 first-link indices][3 ints per link, see above].
    static void RoomFluxSegKernel(Index1D index,ArrayView<int> meta,ArrayView<float> cur,ArrayView<float> nxt,
        ArrayView<float> seg,int count,int faces,int segs)
    {
        int s=index, lb=faces+segs+2;
        float fout=0;
        for(int l=meta[faces+1+s];l<meta[faces+2+s];l++)
        {
            int ea=meta[lb+3*l],ed=meta[lb+3*l+1],qf=meta[lb+3*l+2];
            if(qf>=128)
            {
                // open domain boundary face: the cell's own signed momentum flux u_a = m_a/rho (what the old u-mean read)
                float rho=0,m=0;int ca=(qf-128)>>1,ia=ea>=0?ea:~ea;
                for(int p=0;p<Q;p++)
                {
                    LinkDir(p,out int cx,out int cy,out int cz);
                    float fp=ea>=0?nxt[p*count+ia]:cur[p*count+ia];
                    rho+=fp;if(p>0) m+=(ca==0?cx:ca==1?cy:cz)*fp;
                }
                fout+=((qf&1)==1?1f:-1f)*m/rho;
                continue;
            }
            int q=qf&31,qb=(q&1)==1?q+1:q-1;
            if((qf&64)==0) fout+=(ea>=0?nxt[q*count+ea]:cur[q*count+~ea]);
            if((qf&32)==0) fout-=(ed>=0?nxt[qb*count+ed]:cur[qb*count+~ed]);
        }
        seg[s]=fout;
    }

    static void RoomFluxFaceKernel(Index1D index,ArrayView<int> meta,ArrayView<float> seg,ArrayView<float> sums,int faces)
    {
        int k=index;
        float fout=0;
        for(int s=meta[k];s<meta[k+1];s++) fout+=seg[s];
        float inward=-fout;
        float qi=MathF.Max(inward,0),qo=MathF.Max(-inward,0);
        float yy=qi-sums[2*faces+k],t=sums[k]+yy;
        sums[2*faces+k]=(t-sums[k])-yy; sums[k]=t;
        yy=qo-sums[3*faces+k];t=sums[faces+k]+yy;
        sums[3*faces+k]=(t-sums[faces+k])-yy;sums[faces+k]=t;
    }

    public sealed partial class LbmSession
    {
        RoomFluxPlan _fluxPlan=new();
        int[] _fluxMeta=[];
        float[] _instant=[];   // unused now (room flux reads populations); RunStep skips it while empty
        double[] _fluxSums=[];
        MemoryBuffer1D<int,Stride1D.Dense>? _dFluxMeta;
        MemoryBuffer1D<float,Stride1D.Dense>? _dFluxSums;   // 4 x _fluxFaces: in, out, Kahan comp in, comp out; room faces then opening faces
        MemoryBuffer1D<float,Stride1D.Dense>? _dFluxSeg;    // per-segment link sums of the current step
        int _fluxFaces,_fluxSegs;
        OpeningFluxPlan _openPlan=new();
        int[] _opMeta=[];
        double[] _opSums=[];
        int SumPlanes => (_thermal?6:5) + (_ageWanted?1:0);   // 6k: + the eddy-viscosity plane of the online age

        void SetupRoomFlux()
        {
            _fluxPlan = _model.Rooms.Count==0 ? new() : RoomFluxPlan.Build(_source,_model);
            int f=_fluxPlan.Faces.Length;
            _fluxSums=new double[2*f];
            var ring=new Dictionary<int,int>();
            for(int k=0;k<_bl.Index.Length-_bl.FixedCount;k++) ring[_bl.Index[k]]=_bl.NeighborSlot[k];
            int Map(int i)
            {
                if(ring.TryGetValue(i,out int ni)) i=ni;
                int x=i%_grid.Nx,y=i/_grid.Nx%_grid.Ny,z=i/(_grid.Nx*_grid.Ny);
                // Fixed, coarse-driven and open-z boundary nodes are not collided by this session (flux reads cur).
                return _grid.Cell[i]!=VoxelGrid.Air || x==0||x==_grid.Nx-1||y==0||y==_grid.Ny-1||z==0&&_grid.OpenZMin||z==_grid.Nz-1&&_grid.OpenZMax ? ~i : i;
            }
            int nl=_fluxPlan.LinkQ.Length;
            _fluxMeta=new int[f+1+3*nl];
            Array.Copy(_fluxPlan.LinkStart,_fluxMeta,f+1);
            for(int l=0;l<nl;l++)
            {
                _fluxMeta[f+1+3*l]=Map(_fluxPlan.LinkA[l]);
                _fluxMeta[f+2+3*l]=Map(_fluxPlan.LinkD[l]);
                _fluxMeta[f+3+3*l]=_fluxPlan.LinkQ[l];
            }
            // Opening throat planes: same link flux, one pseudo face per open opening (own buffers, same kernel).
            _openPlan = OpeningFluxPlan.Build(_source,_model);
            int no=_openPlan.Openings.Length; nl=_openPlan.LinkQ.Length;
            _opSums=new double[2*no]; _opMeta=new int[no+1+3*nl];
            Array.Copy(_openPlan.LinkStart,_opMeta,no+1);
            for(int l=0;l<nl;l++)
            {
                _opMeta[no+1+3*l]=Map(_openPlan.LinkA[l]);
                _opMeta[no+2+3*l]=Map(_openPlan.LinkD[l]);
                _opMeta[no+3+3*l]=_openPlan.LinkQ[l];
            }
        }

        void AllocateRoomFluxGpu()
        {
            _dFluxMeta?.Dispose();_dFluxSums?.Dispose();_dFluxSeg?.Dispose();_dFluxMeta=null;_dFluxSums=null;_dFluxSeg=null;
            int fr=_fluxPlan.Faces.Length,no=_openPlan.Openings.Length,nF=fr+no;
            _fluxFaces=nF;_fluxSegs=0;
            if(nF==0) return;
            lock(GpuLiveLock)
            {
                _roomFluxSegKernel ??= _acc!.LoadAutoGroupedStreamKernel<Index1D,ArrayView<int>,ArrayView<float>,ArrayView<float>,ArrayView<float>,int,int,int>(RoomFluxSegKernel);
                _roomFluxFaceKernel ??= _acc!.LoadAutoGroupedStreamKernel<Index1D,ArrayView<int>,ArrayView<float>,ArrayView<float>,int>(RoomFluxFaceKernel);
            }
            // Merge room faces and opening pseudo faces into one launch set and cut every face's links into segments.
            int nlR=_fluxPlan.LinkQ.Length,nlO=_openPlan.LinkQ.Length;
            var linkStart=new int[nF+1];
            for(int k=0;k<=fr;k++) linkStart[k]=_fluxMeta[k];
            for(int k=1;k<=no;k++) linkStart[fr+k]=nlR+_opMeta[k];
            var faceSeg=new int[nF+1]; var segStart=new List<int>();
            for(int k=0;k<nF;k++)
            {
                faceSeg[k]=segStart.Count;
                for(int l=linkStart[k];l<linkStart[k+1];l+=FluxSegLinks) segStart.Add(l);
            }
            faceSeg[nF]=segStart.Count; segStart.Add(linkStart[nF]);
            int nS=_fluxSegs=segStart.Count-1;
            var meta=new int[nF+1+nS+1+3*(nlR+nlO)];
            Array.Copy(faceSeg,meta,nF+1);
            segStart.CopyTo(meta,nF+1);
            Array.Copy(_fluxMeta,fr+1,meta,nF+nS+2,3*nlR);
            Array.Copy(_opMeta,no+1,meta,nF+nS+2+3*nlR,3*nlO);
            _dFluxMeta=_acc!.Allocate1D(meta);
            _dFluxSeg=_acc!.Allocate1D<float>(Math.Max(nS,1));
            _dFluxSums=_acc!.Allocate1D<float>(4*nF);
            _dFluxSums.View.MemSetToZero();
        }

        void AccumulateRoomFluxCpu()
        {
            AccumulateLinkFluxCpu(_fluxMeta,_fluxSums,_fluxPlan.Faces.Length);
            AccumulateLinkFluxCpu(_opMeta,_opSums,_openPlan.Openings.Length);
        }

        void AccumulateLinkFluxCpu(int[] meta,double[] sums,int n)
        {
            if(n==0) return;
            int count=_grid.Count,lb=n+1;
            float[] cur=_cur,nxt=_nxt;
            Parallel.For(0,n,k=>
            {
                double fout=0;
                for(int l=meta[k];l<meta[k+1];l++)
                {
                    int ea=meta[lb+3*l],ed=meta[lb+3*l+1],qf=meta[lb+3*l+2];
                    if(qf>=128)
                    {
                        double rho=0,m=0;int ca=(qf-128)>>1,ia=ea>=0?ea:~ea;var fa=ea>=0?nxt:cur;
                        for(int p=0;p<Q;p++)
                        {
                            LinkDir(p,out int cx,out int cy,out int cz);
                            rho+=fa[ia*Q+p];if(p>0) m+=(ca==0?cx:ca==1?cy:cz)*(double)fa[ia*Q+p];
                        }
                        fout+=((qf&1)==1?1.0:-1.0)*m/rho;
                        continue;
                    }
                    int q=qf&31,qb=(q&1)==1?q+1:q-1;
                    if((qf&64)==0) fout+=ea>=0?nxt[ea*Q+q]:cur[~ea*Q+q];
                    if((qf&32)==0) fout-=ed>=0?nxt[ed*Q+qb]:cur[~ed*Q+qb];
                }
                double inward=-fout;
                sums[2*k]+=Math.Max(inward,0);sums[2*k+1]+=Math.Max(-inward,0);
            });
        }

        void AccumulateRoomFluxGpu()
        {
            int n=_fluxFaces,ns=_fluxSegs;
            if(n==0) return;
            if(ns>0) _roomFluxSegKernel!(ns,_dFluxMeta!.View,_dCur!.View,_dNxt!.View,_dFluxSeg!.View,_grid.Count,n,ns);
            _roomFluxFaceKernel!(n,_dFluxMeta!.View,_dFluxSeg!.View,_dFluxSums!.View,n);
        }

        /// <summary>Sandbox measurement: link flux (m3/s, +x) across the face x|x+1 for each x in <paramref name="xs"/>, from the
        /// post-collision populations the last Step left in cur: sum f_i(x), c_x=+1, minus sum f_i(x+1), c_x=-1, over the
        /// interior y/z rows, excluding populations whose destination is a wall (they bounce back). Same formula as the
        /// room faces. CPU = cur as is; GPU downloads cur.</summary>
        internal double[] PlaneLinkFluxX(int[] xs)
        {
            int count=_grid.Count; var d=new GridDims{Nx=_grid.Nx,Ny=_grid.Ny,Nz=_grid.Nz,Count=count,OpenZMin=_grid.OpenZMin?1:0,OpenZMax=_grid.OpenZMax?1:0};
            float[]? host=_useGpu ? _dCur!.View.GetAsArray1D() : null; var cpu=_cur; byte[] cell=_grid.Cell;
            float F(int i,int q)=>host!=null?host[q*count+i]:cpu[i*Q+q];
            var res=new double[xs.Length];
            for(int p=0;p<xs.Length;p++)
            {
                double sum=0;
                for(int z=1;z<d.Nz-1;z++) for(int y=1;y<d.Ny-1;y++)
                for(int side=0;side<2;side++)
                {
                    int x=xs[p]+side,sg=side==0?1:-1,i=x+d.Nx*(y+d.Ny*z);
                    if(cell[i]==VoxelGrid.Solid) continue;
                    for(int q=1;q<Q;q++)
                    {
                        LinkDir(q,out int cx,out int cy,out int cz);
                        if(cx!=sg || LinkWall(cell,d,x+cx,y+cy,z+cz)) continue;
                        sum+=side==0?F(i,q):-F(i,q);
                    }
                }
                res[p]=sum*_velScale*_dx*_dx;
            }
            return res;
        }

        // ---- Sandbox hooks for the flux-conservation suite (--fluxcons); read-only, nothing in the solve uses them ----

        /// <summary>The flux plan of this session (rooms, faces, per-face links).</summary>
        internal RoomFluxPlan FluxPlan => _fluxPlan;

        /// <summary>Reader (cell, q) -> population over cur, the post-collision f* the last Step left behind (GPU downloads it).</summary>
        internal Func<int,int,float> PopulationReader()
        {
            int count=_grid.Count;
            if(_useGpu){var host=_dCur!.View.GetAsArray1D();return (i,q)=>host[q*count+i];}
            var cpu=_cur;return (i,q)=>cpu[i*Q+q];
        }

        /// <summary>Raw per-face sums since the last ResetAveraging, lattice mass units (population sums, no unit scale, no averaging):
        /// the inward and outward parts of each face's per-step net flux, Sum_steps max(+-net,0). Face k of <see cref="FluxPlan"/>;
        /// Out - In summed over a room's faces is that room's accumulated per-step net outward flux.</summary>
        internal (double[] In,double[] Out) RoomFluxRaw()
        {
            int n=_fluxPlan.Faces.Length; var inn=new double[n]; var outt=new double[n];
            if(_useGpu && _dFluxSums!=null)
            {
                var s=_dFluxSums.View.GetAsArray1D();
                for(int k=0;k<n;k++){inn[k]=s[k];outt[k]=s[_fluxFaces+k];}
            }
            else for(int k=0;k<n;k++){inn[k]=_fluxSums[2*k];outt[k]=_fluxSums[2*k+1];}
            return (inn,outt);
        }

        void ReadRoomFlux(FlowField field)
        {
            field.AverageSamples=_avgSamples;
            if(_avgSamples==0) return;
            if(_useGpu && _dFluxSums!=null)
            {
                var sums=_dFluxSums.View.GetAsArray1D();int n=_fluxPlan.Faces.Length,nF=_fluxFaces;
                for(int k=0;k<n;k++) {_fluxSums[2*k]=sums[k];_fluxSums[2*k+1]=sums[nF+k];}
                for(int k=0;k<_openPlan.Openings.Length;k++) {_opSums[2*k]=sums[n+k];_opSums[2*k+1]=sums[nF+n+k];}
            }
            field.RoomFluxes=_fluxPlan.Reduce(_fluxSums,(double)_velScale*_dx*_dx/_avgSamples);
            field.OpeningFluxes=_openPlan.Reduce(_opSums,(double)_velScale*_dx*_dx/_avgSamples);
        }
    }
}
