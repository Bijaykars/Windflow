using System.Numerics;
using System.Reflection;
using ILGPU.Runtime;
using WindFlow.Core;

namespace WindFlow.Sandbox;

/// <summary>Regression cases for the external audit. Fast geometry/counterexample checks plus real
/// CPU/GPU stepping of prescribed reversal and low gauge pressure; run with --audit.</summary>
static class AuditChecks
{
    static VoxelGrid Grid(int n=40) => new() { Nx=n,Ny=n,Nz=n,Dx=.125f,Cell=new byte[n*n*n] };
    static Mesh Box(Vector3 a,Vector3 b)
    {
        var v=new Vector3[8];for(int i=0;i<8;i++)v[i]=new((i&1)==0?a.X:b.X,(i&2)==0?a.Y:b.Y,(i&4)==0?a.Z:b.Z);
        return new() { Vertices=v,Indices=[0,2,1,1,2,3,4,5,6,5,7,6,0,1,4,1,5,4,2,6,3,3,6,7,0,4,2,2,4,6,1,3,5,3,7,5] };
    }
    static VoxelGrid Build(BuildingModel m)=>Voxelizer.Build(m,64000,domain:new(Vector3.Zero,new(5)),sealUnreachable:false);
    static FlowField Field(VoxelGrid g,float u)=>new() { Grid=g,Ux=Enumerable.Repeat(u,g.Count).ToArray(),Uy=new float[g.Count],Uz=new float[g.Count],P=new float[g.Count] };
    static Room Room(float lo,float hi)=>new() { Id="r",ZMin=lo,ZMax=hi,Loops={new Vector2[]{new(lo,lo),new(hi,lo),new(hi,hi),new(lo,hi)}} };

    public static int Run()
    {
        int failures=0;
        void Check(string name,bool pass,string detail="")
        {
            Console.WriteLine($"[{(pass?"PASS":"FAIL")}] audit {name} {detail}");
            CheckLog.Record(pass?"PASS":"FAIL","audit "+name,detail);if(!pass)failures++;
        }
        var wall=Box(new(2,.125f,.125f),new(2.5f,4.875f,4.875f));
        var m=new BuildingModel { GroundZ=-10,Obstacles={wall},Openings={new() { Center=new(2.25f,2.5f,2.5f),Normal=Vector3.UnitX,Width=1,Height=1,Depth=.5f }} };
        var g=Build(m);
        Check("wall core stays material after carving",g.IsSolid(g.Index(17,8,8)));
        Check("thick wall throat area",Analysis.ThroatCells(g,m.Openings[0]).Count==64);
        Check("opening connects both sides only at passage",!g.IsSolid(g.Index(17,19,19)) && g.IsSolid(g.Index(18,8,8)));

        var inner=Box(new(1),new(4));for(int k=0;k<inner.Indices.Length;k+=3)(inner.Indices[k],inner.Indices[k+1])=(inner.Indices[k+1],inner.Indices[k]);
        var outer=Box(new(.25f),new(4.75f));
        var shell=new Mesh { Vertices=outer.Vertices.Concat(inner.Vertices).ToArray(),Indices=outer.Indices.Concat(inner.Indices.Select(i=>i+8)).ToArray() };
        g=Build(new() { GroundZ=-10,Obstacles={shell} });
        Check("nested cavity stays air",!g.IsSolid(g.Index(20,20,20))&&g.IsSolid(g.Index(5,20,20)));
        var sheet=new Mesh { Vertices=[new(1.15625f,0,0),new(1.15625f,5,0),new(1.15625f,0,5),new(1.15625f,5,5)],Indices=[0,1,2,1,3,2] };
        g=Build(new() { GroundZ=-10,Obstacles={sheet} });
        Check("open sheet leaves air on both sides",!g.IsSolid(g.Index(3,15,15))&&!g.IsSolid(g.Index(16,15,15)));
        // P12-01 (batch 6a, cell-centre mask + cut links): the sheet at x = 1.15625 cuts the 8|9 link, q 0.25 from cell 9 and 0.75 from cell 8; cell 10's link to 9 is free (was: q clamped to 1, 255).
        Check("thin-wall link is cut at its exact q from both sides",MathF.Abs((g.WallLinks[g.Count+g.Index(9,10,10)]-1)/254f-.25f)<.003f&&MathF.Abs((g.WallLinks[2*g.Count+g.Index(8,10,10)]-1)/254f-.75f)<.003f&&g.WallLinks[g.Count+g.Index(10,10,10)]==0);

        var jamb=new Opening { Center=new(1.25f,2.0625f,2),Normal=Vector3.UnitX,Width=1.375f,Height=.625f,Depth=.5f };
        g=Build(new() { GroundZ=-10,Obstacles={Box(new(1,.125f,.125f),new(1.5f,4.875f,4.875f))},Openings={jamb} });
        int encoded=g.WallLinks[9*g.Count+g.Index(9,21,15)];
        Check("carved jamb uses actual diagonal intersection",encoded==128,$"q={(encoded-1)/254f}");
        // RP follow-up 4 (rule 3): the throat counts what the carve opens; this opening's z edges (1.6875, 2.3125) lie on cell centres, so the carve is 11 across by 5 + 1 rows (was 55 by the half-open rule)
        Check("odd opening is 11 by 5 plus one row where its edges lie on cell centres (66)",Analysis.ThroatCells(g,jamb).Count==66);
        jamb.Normal=-Vector3.UnitX;var reversed=Build(new() { GroundZ=-10,Obstacles={Box(new(1,.125f,.125f),new(1.5f,4.875f,4.875f))},Openings={jamb} });
        Check("opening footprint independent of normal sign",g.Cell.SequenceEqual(reversed.Cell));

        g=Grid(8);m=new() { Rooms={Room(0,1)} };
        var room=Analysis.Analyze(m,Field(g,1),Vector2.UnitX,new()).Rooms.Single();
        Check("cropped-room boundary flux",Math.Abs(room.Inflow-1)<1e-6 && Math.Abs(room.Ach-3600)<.01);
        using(var s=LbmSolver.Start(g,m,Vector2.UnitX,new() { Backend=Backend.Cpu,WindProfileExponent=0 }))
        {
            var status=Simulation.Converge(s,m,Vector2.UnitX,new() { Backend=Backend.Cpu,WindProfileExponent=0 },null);
            var f=s.ToField();
            // BatchCount is the KEPT batches now (n minus the power-of-two start-up truncation), not every batch.
            Check("legacy loop (Validation-free): convergence retains field and completed count",f.AverageSamples>0 && f.Ux.Max()>.9f && status.Batches-ConvergenceMonitor.TruncationFor(status.Batches)==status.Status.Rooms[0].BatchCount);
            s.ResetAveraging();f=s.ToField();Check("empty mean has zero gauge pressure",f.P.All(v=>v==0)&&f.AverageSamples==0);
        }
        using(var s=LbmSolver.Start(g,m,Vector2.UnitX,new() { Backend=Backend.Cpu,WindProfileExponent=0 },Field(g,1)))
        {
            s.ResetAveraging(startNow:true);s.Step(3);
            Check("warm-start batch can accumulate immediately",s.ToField().AverageSamples==3);
        }
        int patch=g.Index(2,3,3);g.Cell[patch]=VoxelGrid.Fixed;g.FixedCells.Add(new(patch,new(.064f,0,0),0));
        m=new() { Sources={new() { Id="s",Kind=SourceKind.Supply,Direction=Vector3.UnitX,Width=.125f,Height=.125f,FlowRate=.001f }} };
        using(var s=new LbmSolver.LbmSession(g,m,Vector2.Zero,new() { Backend=Backend.Cpu },uMaxOverride:1))
        {
            var next=Grid(8);next.Cell[patch]=VoxelGrid.Fixed;next.FixedCells.Add(new(patch,new(.128f,0,0),0));
            var model=BuildingModel.FromJson(m.ToJson());model.Sources[0].FlowRate=.002f;
            s.UpdateCells(next,model);
            Check("live edit replaces supply target and nested model",Math.Abs(s.ToField().Supplies[0].SetFlowRate-.002f)<1e-7 && ReferenceEquals(s.NestModel,model));
        }

        g=Grid(16);g.Origin=new(0,0,10);m=new() { GroundZ=0 };
        using(var s=LbmSolver.Start(g,m,Vector2.UnitX,new() { Backend=Backend.Cpu,WindProfileExponent=.25f }))
        {
            var bl=typeof(LbmSolver.LbmSession).GetField("_bl",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(s)!;
            var ids=(int[])bl.GetType().GetField("Index")!.GetValue(bl)!;var vel=(float[])bl.GetType().GetField("Vel")!.GetValue(bl)!;
            int k=Array.IndexOf(ids,g.Index(0,5,8));
            Check("floating wind uses physical ground",Math.Abs(vel[k]/s.VelocityScale-MathF.Pow(11.0625f/10,.25f))<1e-6);
        }

        foreach (var backend in LbmSolver.GpuAvailable ? new[] { Backend.Cpu,Backend.Gpu } : new[] { Backend.Cpu })
        {
            var openGrid=Grid(8);openGrid.OpenZMin=openGrid.OpenZMax=true;
            var openModel=new BuildingModel { Rooms={Room(0,1)} };
            using var vertical=new LbmSolver.LbmSession(openGrid,openModel,Vector2.Zero,new() { Backend=backend },uMaxOverride:1);
            Seed(vertical,openGrid,1,Vector3.UnitZ);vertical.ResetAveraging(startNow:true);vertical.Step(1);
            var vf=vertical.ToField();
            Check($"open Z faces exchange ({backend})",Math.Abs(vf.Uz[openGrid.Index(3,3,0)]-1)<1e-5 && Math.Abs(vf.RoomFluxes[0].Inflow-1)<1e-4);
        }

        var scalarGrid=Grid(8);var scalarModel=new BuildingModel { Rooms={Room(0,1)} };
        var scalar=ScalarSolver.Solve(scalarModel,Field(scalarGrid,1),new(),ageOnly:true);
        Check("cropped-domain inflow supplies fresh scalar air",scalar.Age[scalarGrid.Index(0,3,3)]==0 &&
            Math.Abs(scalar.Age[scalarGrid.Index(4,3,3)]-.5f)<.01f,$"age at x=0.5625: {scalar.Age[scalarGrid.Index(4,3,3)]}");

        var cpu=Reversal(Backend.Cpu);
        Check("CPU retains reversing exchange",Math.Abs(cpu.Q-1)<1e-4&&Math.Abs(cpu.Mean)<1e-5,$"Q={cpu.Q} meanU={cpu.Mean}");
        var pressureCpu=Pressure(Backend.Cpu);
        Check("CPU low gauge pressure",pressureCpu>.005&&pressureCpu<.008,$"P={pressureCpu}");
        if(LbmSolver.GpuAvailable)
        {
            var gpu=Reversal(Backend.Gpu);var pressureGpu=Pressure(Backend.Gpu);
            Check("GPU retains reversing exchange",Math.Abs(gpu.Q-1)<1e-4&&Math.Abs(gpu.Mean)<1e-5,$"Q={gpu.Q} meanU={gpu.Mean}");
            Check("long-window GPU gauge pressure matches CPU",Math.Abs(pressureGpu-pressureCpu)<.01*Math.Abs(pressureCpu),$"CPU={pressureCpu} GPU={pressureGpu}");
        }
        var a=Field(Grid(8),1);a.AverageSamples=1;a.RoomFluxes.Add(new() { Room=Room(0,1),Inflow=1,Outflow=1 });
        var b=Field(a.Grid,-1);b.AverageSamples=3;b.RoomFluxes.Add(new() { Room=a.RoomFluxes[0].Room,Inflow=2,Outflow=2 });
        FlowFieldMean.Add(a,b);
        Check("unequal batches weight field and exchange together",a.AverageSamples==4&&a.Ux[0]==-.5f&&a.RoomFluxes[0].Inflow==1.75);
        return failures;
    }

    static (double Q,float Mean) Reversal(Backend backend)
    {
        var g=Grid(16);var m=new BuildingModel { Rooms={Room(.5f,1.5f)},Openings={new() { Center=new(1),Normal=Vector3.UnitX,Width=3,Height=3,Depth=3 }} };
        using var s=new LbmSolver.LbmSession(g,m,Vector2.Zero,new() { Backend=backend,OpeningModel=OpeningModel.Resolved },uMaxOverride:1);
        s.ResetAveraging(startNow:true);
        for(int k=0;k<20;k++) { Seed(s,g,1,new(k%2==0?1:-1,0,0));s.Step(1); }
        var f=s.ToField();return(f.RoomFluxes.Single().Inflow,f.Ux[g.Index(8,8,8)]);
    }

    static float Pressure(Backend backend)
    {
        var g=Grid(6);
        for(int z=0;z<6;z++)for(int y=0;y<6;y++)for(int x=0;x<6;x++) if(x==0||x==5||y==0||y==5||z==0||z==5) g.Cell[g.Index(x,y,z)]=VoxelGrid.Solid;
        using var s=new LbmSolver.LbmSession(g,new(),Vector2.Zero,new() { Backend=backend },uMaxOverride:1);
        Seed(s,g,1.0001f,Vector3.Zero);s.ResetAveraging(startNow:true);s.Step(20000);
        return s.ToField().P[g.Index(3,3,3)];
    }

    static void Seed(LbmSolver.LbmSession s,VoxelGrid g,float rho,Vector3 velocity)
    {
        int[] cx=[0,1,-1,0,0,0,0,1,-1,1,-1,1,-1,1,-1,0,0,0,0];
        int[] cy=[0,0,0,1,-1,0,0,1,-1,-1,1,0,0,0,0,1,-1,1,-1];
        int[] cz=[0,0,0,0,0,1,-1,0,0,0,0,1,-1,-1,1,1,-1,-1,1];
        velocity*=s.VelocityScale;var f=new float[g.Count*19];
        for(int i=0;i<g.Count;i++)for(int q=0;q<19;q++)
        {
            float w=q==0?1f/3:q<=6?1f/18:1f/36;float cu=cx[q]*velocity.X+cy[q]*velocity.Y+cz[q]*velocity.Z;
            f[s.NestGpu?q*g.Count+i:19*i+q]=w*rho*(1+3*cu+4.5f*cu*cu-1.5f*velocity.LengthSquared());
        }
        if(s.NestGpu) {s.NestDCur!.View.CopyFromCPU(f);s.NestDNxt!.View.CopyFromCPU(f);}
        else {f.CopyTo(s.NestCur,0);f.CopyTo(s.NestNxt,0);}
    }
}
