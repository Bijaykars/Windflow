using System.Numerics;
using System.Runtime.InteropServices;
using Vortice.Direct3D;
using Vortice.Direct3D12;
using Vortice.DXGI;

namespace WindFlow.App.Viewport;

/// <summary>Speed: moving particles coloured by speed. Still: a static amber cloud of the air that is barely
/// moving (the dead air), with the particles hidden or at a 10 % ghost.</summary>
public enum ParticleView { Speed, Still }

/// <summary>GPU particles: a compute shader advects them through the solver's velocity volume and each one
/// keeps a world-space ring buffer of its last <see cref="TrailLength"/> positions, drawn as fading line
/// segments in the same MSAA pass as the building. Owned and driven entirely by the render thread; the only
/// cross-thread entry points are <see cref="SetField"/>, <see cref="RequestCount"/> and the public toggles,
/// all of which publish through Volatile/Interlocked.</summary>
public sealed class ParticleSystem
{
    public const int TrailLength = 16;
    const int ThreadGroup = 256;
    const int ParticleStride = 32;          // float3 pos, age, stall, pool, life, pad
    const int TrailStride = 16;             // float3 position + speed
    const float SimStep = 1f / 60f;         // fixed sim rate: the look must not depend on the frame rate
    const int StagingRing = 3;              // one staging buffer per frame in flight

    // Heap: 0 fieldA SRV, 1 fieldB SRV, 2 spawn SRV, 3 occupancy SRV, 4 particles UAV, 5 trails UAV,
    //       6 particles SRV, 7 trails SRV.
    const int HeapSize = 9, UavTableOffset = 4, GraphicsTableOffset = 6, ScalarSlot = 8;

    public int Count { get; private set; }
    public float SpeedMax { get; private set; } = 1;
    public bool Ready => fieldB != null && particles != null && spawnCount > 0;
    public float TimeScale = 16f;
    public bool Enabled = true;
    public ParticleView View = ParticleView.Speed;
    /// <summary>Still view: air slower than this (m/s) is cloud. The rooms' still % uses the same number.</summary>
    public float StillThreshold = 0.10f;
    /// <summary>Still view: draw the moving air over the cloud as cool cyan lanes (on by default).</summary>
    public bool StillParticles = true;
    /// <summary>Per BuildingModel.Rooms index: the share of the room's texels below
    /// <see cref="StillThreshold"/> in the newest field (NaN for a room with no texels). Counted in the
    /// same pass, over the same texels, that builds the cloud, so the panel and the picture agree.</summary>
    public float[] RoomStill { get; private set; } = [];
    public int RoomStillVersion { get; private set; }
    /// <summary>Sprites in the current cloud, for the shot sidecar.</summary>
    public int CloudCount => cloudCount;
    /// <summary>Trail segments actually drawn. An integrated GPU is fill-bound, so it gets half the trail:
    /// the ring buffer is still 16 long, the draw just stops earlier.</summary>
    public int DrawnTrail = TrailLength;

    /// <summary>Live-field statistics for the status strip and the bench report.</summary>
    public int FieldUpdates { get; private set; }
    public int FieldUploadsSkipped { get; private set; }

    public string Diagnostic =>
        $"ready={Ready} count={Count} spawn={spawnCount}({spawnRooms}/{spawnInflow}) field={fieldDims} " +
        $"occ={occDims} updates={FieldUpdates} skipped={FieldUploadsSkipped} " +
        $"kill={boxMin}..{boxMax} domain={domainMin}+{domainSize}";

    ID3D12Device? device;
    ID3D12DescriptorHeap? heap;
    ID3D12RootSignature? computeRoot, drawRoot, sliceRoot, cloudRoot;
    ID3D12PipelineState? psoInit, psoStep, psoTrail, psoSlice, psoSliceArrow, psoCloud;
    ID3D12Resource? particles, trails, spawn, fieldA, fieldB, occupancy, scalars, spawnUpload, occUpload, scalarUpload;
    readonly ID3D12Resource?[] staging = new ID3D12Resource?[StagingRing];
    readonly ulong[] stagingFence = new ulong[StagingRing];
    ulong stagingBytes;
    int stagingNext;

    uint descriptorSize;
    int spawnCount, spawnRooms, spawnInflow, head, frame;
    float accumulator;
    bool needsInit;
    string fieldDims = "none", occDims = "none";

    FieldVolume? pendingField;
    int pendingCount;

    // Blend state: FieldA holds the previous published field, FieldB the newest, and Blend walks 0 -> 1 over
    // the measured publish interval so a 5-15 Hz live solver never pops.
    double blendStart, blendDuration = 0.1, lastPublish;
    float blend = 1;

    Vector3 domainMin, domainSize, boxMin, boxMax, houseMin, houseMax, occSize;
    float texelWorld = 1, occTexel = 1;

    [StructLayout(LayoutKind.Sequential)]
    struct SimConstants
    {
        public Vector3 DomainMin; public float TexelWorld;
        public Vector3 DomainSize; public float Dt;
        public Vector3 BoxMin; public float RealDt;
        public Vector3 BoxMax; public float MinSpeed;
        public Vector3 OccSize; public float OccTexel;
        public uint Count, Head, TrailLen, SpawnCount;
        public uint SpawnRooms, SpawnInflow, Frame; public float Blend;
        public float MaxLife; public Vector3 Pad;
        public const int Dwords = 32;
    }

    /// <summary>Horizontal and vertical cut planes through the live field, coloured by speed.</summary>
    public bool SliceHorizontal, SliceVertical;
    public float SliceZ, SliceOther;
    public int SliceAxis = 1;               // 1 = x plane, 2 = y plane for the vertical cut
    /// <summary>0 speed, 1 pressure, 2 temperature. 1 and 2 need a scalar volume from the analysis pass.</summary>
    public int SliceField;
    public bool HasScalars { get; private set; }
    public Vector4 ClipMin, ClipMax;
    /// <summary>Room isolate: trail points outside this box are not drawn (w > 0.5 enables). The section box
    /// deliberately leaves trails alone; only an isolated room cuts them.</summary>
    public Vector4 TrailClipMin, TrailClipMax;
    public float ScalarScale { get; private set; } = 1;
    /// <summary>The cut reads as a solid sheet, as in a CFD figure; walls still clip it.</summary>
    public float SliceAlphaScale = 0.92f;
    /// <summary>Grid spacing of the in-plane velocity arrows on a cut, metres.</summary>
    public const float SliceArrowSpacing = 0.5f;
    public Vector3 FocusMin => houseMin;
    public Vector3 FocusMax => houseMax;

    [StructLayout(LayoutKind.Sequential)]
    struct SliceConstants
    {
        public Matrix4x4 ViewProj;
        public Vector3 Min; public float Alpha;
        public Vector3 Max; public float Scale;
        public Vector3 Domain; public float Pos;
        public Vector3 Origin; public float Axis;
        public Vector3 OccSize; public float Mode;
        public Vector3 Arrow; public float ScalarScale;      // arrows: x spacing, y columns, z pass
        public Vector4 ClipMin, ClipMax;
        public const int Dwords = 48;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct DrawConstants
    {
        public Matrix4x4 ViewProj;
        public uint Head, TrailLen; public float SpeedMax, Brightness;
        public float OutdoorDim, BaseAlpha, StillAlpha, StillCut;
        public Vector3 HouseMin; public float RingLen;
        public Vector3 HouseMax; public float Theme;      // 0 dark, 1 light
        public Vector4 IsoMin, IsoMax;         // room isolate box, w > 0.5 enables
        public const int Dwords = 40;
    }

    // ---------------------------------------------------------------- setup

    public void Initialize(ID3D12Device dev, Format renderTarget, Format depth, int sampleCount)
    {
        device = dev;
        descriptorSize = dev.GetDescriptorHandleIncrementSize(DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView);
        heap = dev.CreateDescriptorHeap(new DescriptorHeapDescription(
            DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView, HeapSize,
            DescriptorHeapFlags.ShaderVisible));

        var computeTable = new RootParameter1(new RootDescriptorTable1(
            new DescriptorRange1(DescriptorRangeType.ShaderResourceView, 4, 0, 0, 0),
            new DescriptorRange1(DescriptorRangeType.UnorderedAccessView, 2, 0, 0, UavTableOffset)), ShaderVisibility.All);
        computeRoot = dev.CreateRootSignature(new RootSignatureDescription1
        {
            Flags = RootSignatureFlags.None,
            Parameters = [new RootParameter1(new RootConstants(0, 0, SimConstants.Dwords), ShaderVisibility.All), computeTable],
            StaticSamplers =
            [
                new StaticSamplerDescription(0, Filter.MinMagMipLinear, TextureAddressMode.Clamp, TextureAddressMode.Clamp,
                    TextureAddressMode.Clamp, 0f, 1, ComparisonFunction.Never, StaticBorderColor.TransparentBlack,
                    0f, float.MaxValue, ShaderVisibility.All, 0),
                new StaticSamplerDescription(1, Filter.MinMagMipPoint, TextureAddressMode.Clamp, TextureAddressMode.Clamp,
                    TextureAddressMode.Clamp, 0f, 1, ComparisonFunction.Never, StaticBorderColor.TransparentBlack,
                    0f, float.MaxValue, ShaderVisibility.All, 0),
            ],
        });

        var drawTable = new RootParameter1(new RootDescriptorTable1(
            new DescriptorRange1(DescriptorRangeType.ShaderResourceView, 2, 0, 0, 0)), ShaderVisibility.Vertex);
        drawRoot = dev.CreateRootSignature(new RootSignatureDescription1
        {
            Flags = RootSignatureFlags.None,
            Parameters = [new RootParameter1(new RootConstants(0, 0, DrawConstants.Dwords), ShaderVisibility.Vertex), drawTable],
        });

        // The slice reads two descriptors that are not adjacent in the heap, so its table has two ranges.
        var sliceTable = new RootParameter1(new RootDescriptorTable1(
            new DescriptorRange1(DescriptorRangeType.ShaderResourceView, 1, 0, 0, 1),
            new DescriptorRange1(DescriptorRangeType.ShaderResourceView, 1, 1, 0, 3),
            new DescriptorRange1(DescriptorRangeType.ShaderResourceView, 1, 2, 0, ScalarSlot)), ShaderVisibility.All);
        sliceRoot = dev.CreateRootSignature(new RootSignatureDescription1
        {
            Flags = RootSignatureFlags.None,
            Parameters = [new RootParameter1(new RootConstants(0, 0, SliceConstants.Dwords), ShaderVisibility.All), sliceTable],
            StaticSamplers =
            [
                new StaticSamplerDescription(0, Filter.MinMagMipLinear, TextureAddressMode.Clamp, TextureAddressMode.Clamp,
                    TextureAddressMode.Clamp, 0f, 1, ComparisonFunction.Never, StaticBorderColor.TransparentBlack,
                    0f, float.MaxValue, ShaderVisibility.All, 0),
                new StaticSamplerDescription(1, Filter.MinMagMipPoint, TextureAddressMode.Clamp, TextureAddressMode.Clamp,
                    TextureAddressMode.Clamp, 0f, 1, ComparisonFunction.Never, StaticBorderColor.TransparentBlack,
                    0f, float.MaxValue, ShaderVisibility.All, 0),
            ],
        });

        psoInit = dev.CreateComputePipelineState(new ComputePipelineStateDescription
        {
            RootSignature = computeRoot,
            ComputeShader = Renderer.CompileShader(Shaders.ParticleSource, "CSInit", Shaders.Profile5Cs),
        });
        psoStep = dev.CreateComputePipelineState(new ComputePipelineStateDescription
        {
            RootSignature = computeRoot,
            ComputeShader = Renderer.CompileShader(Shaders.ParticleSource, "CSStep", Shaders.Profile5Cs),
        });

        var raster = RasterizerDescription.CullNone;
        raster.MultisampleEnable = sampleCount > 1;
        raster.AntialiasedLineEnable = false;
        psoTrail = dev.CreateGraphicsPipelineState(new GraphicsPipelineStateDescription
        {
            RootSignature = drawRoot,
            VertexShader = Renderer.CompileShader(Shaders.ParticleSource, "VSTrail", Shaders.Profile5Vs),
            PixelShader = Renderer.CompileShader(Shaders.ParticleSource, "PSTrail", Shaders.Profile5Ps),
            InputLayout = new InputLayoutDescription([]),
            RasterizerState = raster,
            BlendState = new BlendDescription(Blend.One, Blend.InverseSourceAlpha),
            DepthStencilState = new DepthStencilDescription(true, DepthWriteMask.Zero, ComparisonFunction.LessEqual),
            SampleMask = uint.MaxValue,
            PrimitiveTopologyType = PrimitiveTopologyType.Line,
            RenderTargetFormats = [renderTarget],
            DepthStencilFormat = depth,
            SampleDescription = new SampleDescription((uint)sampleCount, 0),
        });

        psoSlice = dev.CreateGraphicsPipelineState(new GraphicsPipelineStateDescription
        {
            RootSignature = sliceRoot,
            VertexShader = Renderer.CompileShader(Shaders.ParticleSource, "VSSlice", Shaders.Profile5Vs),
            PixelShader = Renderer.CompileShader(Shaders.ParticleSource, "PSSlice", Shaders.Profile5Ps),
            InputLayout = new InputLayoutDescription([]),
            RasterizerState = RasterizerDescription.CullNone,
            BlendState = new BlendDescription(Blend.One, Blend.InverseSourceAlpha),
            DepthStencilState = new DepthStencilDescription(true, DepthWriteMask.Zero, ComparisonFunction.LessEqual),
            SampleMask = uint.MaxValue,
            PrimitiveTopologyType = PrimitiveTopologyType.Triangle,
            RenderTargetFormats = [renderTarget],
            DepthStencilFormat = depth,
            SampleDescription = new SampleDescription((uint)sampleCount, 0),
        });

        // The arrows on a cut: same root signature and blend as the plane, generated from the instance id.
        psoSliceArrow = dev.CreateGraphicsPipelineState(new GraphicsPipelineStateDescription
        {
            RootSignature = sliceRoot,
            VertexShader = Renderer.CompileShader(Shaders.ParticleSource, "VSSliceArrow", Shaders.Profile5Vs),
            PixelShader = Renderer.CompileShader(Shaders.ParticleSource, "PSTrail", Shaders.Profile5Ps),
            InputLayout = new InputLayoutDescription([]),
            RasterizerState = RasterizerDescription.CullNone,
            BlendState = new BlendDescription(Blend.One, Blend.InverseSourceAlpha),
            DepthStencilState = new DepthStencilDescription(true, DepthWriteMask.Zero, ComparisonFunction.LessEqual),
            SampleMask = uint.MaxValue,
            PrimitiveTopologyType = PrimitiveTopologyType.Triangle,
            RenderTargetFormats = [renderTarget],
            DepthStencilFormat = depth,
            SampleDescription = new SampleDescription((uint)sampleCount, 0),
        });

        // The cloud reads its instances straight from an upload-heap buffer through a root SRV: no descriptor
        // to rewrite when the buffer rotates, and nothing to copy to a default heap for a few thousand sprites.
        cloudRoot = dev.CreateRootSignature(new RootSignatureDescription1
        {
            Flags = RootSignatureFlags.None,
            Parameters =
            [
                new RootParameter1(new RootConstants(0, 0, CloudConstants.Dwords), ShaderVisibility.All),
                new RootParameter1(RootParameterType.ShaderResourceView, new RootDescriptor1(0, 0), ShaderVisibility.Vertex),
            ],
        });
        psoCloud = dev.CreateGraphicsPipelineState(new GraphicsPipelineStateDescription
        {
            RootSignature = cloudRoot,
            VertexShader = Renderer.CompileShader(Shaders.ParticleSource, "VSCloud", Shaders.Profile5Vs),
            PixelShader = Renderer.CompileShader(Shaders.ParticleSource, "PSCloud", Shaders.Profile5Ps),
            InputLayout = new InputLayoutDescription([]),
            RasterizerState = RasterizerDescription.CullNone,
            BlendState = new BlendDescription(Blend.One, Blend.InverseSourceAlpha),
            DepthStencilState = new DepthStencilDescription(true, DepthWriteMask.Zero, ComparisonFunction.LessEqual),
            SampleMask = uint.MaxValue,
            PrimitiveTopologyType = PrimitiveTopologyType.Triangle,
            RenderTargetFormats = [renderTarget],
            DepthStencilFormat = depth,
            SampleDescription = new SampleDescription((uint)sampleCount, 0),
        });
    }

    [StructLayout(LayoutKind.Sequential)]
    struct CloudConstants
    {
        public Matrix4x4 ViewProj;
        public Vector3 Right; public float Radius;
        public Vector3 Up; public float Alpha;
        public Vector4 ClipMin, ClipMax;
        public const int Dwords = 32;
    }

    // ---------------------------------------------------------------- still-air cloud

    const int CloudRing = 3;
    ID3D12Resource? cloudBuffer;             // CloudRing slots of cloudCapacity float4s, upload heap
    readonly ulong[] cloudFence = new ulong[CloudRing];
    int cloudCapacity, cloudSlot, cloudCount;
    FieldVolume? cloudSource;                // the newest uploaded field, the only thing the cloud reads
    bool cloudDirty;
    float cloudThreshold = float.NaN;
    Vector4[] cloudScratch = [];
    int[] stillCount = [], roomCount = [];

    /// <summary>Counts every room's still texels and, in the Still view, rebuilds the sprite list. Runs on the
    /// render thread only when a field has arrived (at most the solver publish rate, ~10 Hz) or the threshold
    /// moved, and only walks the indoor texels: a few thousand on the sample house.</summary>
    void RebuildCloud(ulong pendingSignal, ulong completed)
    {
        var v = cloudSource;
        if (v == null) return;
        bool wantSprites = View == ParticleView.Still;
        float threshold = MathF.Max(StillThreshold, 1e-3f);
        if (!cloudDirty && threshold == cloudThreshold && (!wantSprites || cloudBuilt)) return;

        int rooms = 0;
        foreach (short r in v.IndoorRoom) rooms = Math.Max(rooms, r + 1);
        if (stillCount.Length < rooms) { stillCount = new int[rooms]; roomCount = new int[rooms]; }
        Array.Clear(stillCount);
        Array.Clear(roomCount);
        if (wantSprites && cloudScratch.Length < v.IndoorTexels.Length) cloudScratch = new Vector4[v.IndoorTexels.Length];

        int n = 0;
        var texels = v.Texels;
        for (int i = 0; i < v.IndoorTexels.Length; i++)
        {
            int t = v.IndoorTexels[i], o = t * 4;
            if ((float)texels[o + 3] < FieldVolume.StillMinAir) continue;
            int room = v.IndoorRoom[i];
            roomCount[room]++;
            float speed = new Vector3((float)texels[o], (float)texels[o + 1], (float)texels[o + 2]).Length();
            if (speed >= threshold) continue;
            stillCount[room]++;
            if (!wantSprites) continue;
            int x = t % v.Nx, y = t / v.Nx % v.Ny, z = t / (v.Nx * v.Ny);
            cloudScratch[n++] = new Vector4(v.Origin + new Vector3(x + 0.5f, y + 0.5f, z + 0.5f) * v.Dx,
                1f - speed / threshold);
        }

        var fractions = new float[rooms];
        for (int r = 0; r < rooms; r++) fractions[r] = roomCount[r] > 0 ? stillCount[r] / (float)roomCount[r] : float.NaN;
        RoomStill = fractions;
        RoomStillVersion++;
        cloudDirty = false;
        cloudThreshold = threshold;
        if (!wantSprites) { cloudBuilt = false; return; }

        // Grow the ring for a bigger model; the old buffer may still be feeding a frame in flight.
        int capacity = Math.Max(1024, v.IndoorTexels.Length);
        if (cloudBuffer == null || capacity > cloudCapacity)
        {
            Retire(cloudBuffer, pendingSignal);
            cloudCapacity = capacity;
            cloudBuffer = device!.CreateCommittedResource(HeapProperties.UploadHeapProperties, HeapFlags.None,
                ResourceDescription.Buffer((ulong)(CloudRing * cloudCapacity * 16), ResourceFlags.None, 0),
                ResourceStates.GenericRead, null);
            Array.Clear(cloudFence);
        }
        int slot = (cloudSlot + 1) % CloudRing;
        if (cloudFence[slot] > completed) { cloudDirty = true; return; }     // all slots in flight: next frame
        var dst = cloudBuffer.Map<Vector4>(0, CloudRing * cloudCapacity);
        cloudScratch.AsSpan(0, n).CopyTo(dst.Slice(slot * cloudCapacity, n));
        cloudBuffer.Unmap(0, null);
        cloudSlot = slot;
        cloudCount = n;
        cloudBuilt = true;
    }

    bool cloudBuilt;

    /// <summary>The still-air cloud: soft depth-tested sprites, drawn after the building and before any
    /// particles. <paramref name="right"/>/<paramref name="up"/> are the camera axes in world space.</summary>
    public void DrawCloud(ID3D12GraphicsCommandList cmd, Matrix4x4 viewProj, Vector3 right, Vector3 up, ulong pendingSignal)
    {
        if (View != ParticleView.Still || !cloudBuilt || cloudCount == 0 || cloudBuffer == null) return;
        var c = new CloudConstants
        {
            ViewProj = viewProj,
            Right = right,
            Radius = texelWorld * 0.85f,
            Up = up,
            Alpha = 0.19f,
            ClipMin = ClipMin,
            ClipMax = ClipMax,
        };
        cmd.SetGraphicsRootSignature(cloudRoot);
        cmd.SetPipelineState(psoCloud!);
        cmd.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        unsafe { cmd.SetGraphicsRoot32BitConstants(0, CloudConstants.Dwords, (IntPtr)(&c), 0); }
        cmd.SetGraphicsRootShaderResourceView(1, cloudBuffer.GPUVirtualAddress + (ulong)(cloudSlot * cloudCapacity * 16));
        cmd.DrawInstanced(6, (uint)cloudCount, 0, 0);
        cloudFence[cloudSlot] = pendingSignal;
    }

    /// <summary>Cut planes, drawn before the particles so the trails read on top of them.</summary>
    public void DrawSlices(ID3D12GraphicsCommandList cmd, Matrix4x4 viewProj, ulong pendingSignal)
    {
        if (!Ready || (!SliceHorizontal && !SliceVertical)) return;
        // The field textures are waiting to be swapped for a new domain: drawing now would keep lastUseSignal
        // moving and the swap would never happen (see UploadField). One or two frames without the cut.
        if (reshapeWait || countWait) return;
        // This frame reads the field, occupancy and scalar descriptors even when the particle sim did not run
        // (the Still view hides them), so it counts as a use for the retirement rule.
        lastUseSignal = pendingSignal;
        cmd.SetDescriptorHeaps(heap!);
        cmd.SetGraphicsRootSignature(sliceRoot);
        cmd.SetGraphicsRootDescriptorTable(1, heap!.GetGPUDescriptorHandleForHeapStart());
        cmd.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        if (SliceHorizontal) DrawOnePlane(cmd, viewProj, 0, SliceZ);
        if (SliceVertical) DrawOnePlane(cmd, viewProj, SliceAxis, SliceOther);
    }

    void DrawOnePlane(ID3D12GraphicsCommandList cmd, Matrix4x4 viewProj, int axis, float position)
    {
        var c = new SliceConstants
        {
            ViewProj = viewProj,
            // The cut spans the building, not the focus box: a plan cut is an architectural drawing, and a
            // sheet stretching over the garden just hides the model and saturates at the outdoor wind speed.
            // Clipped to the field's own domain: with a simulation box the sheet would otherwise run past the
            // volume and paint the clamp-to-edge texels over the rest of the house.
            Min = Vector3.Max(houseMin - new Vector3(0.15f), domainMin),
            Alpha = SliceAlphaScale,
            Max = Vector3.Min(houseMax + new Vector3(0.15f), domainMin + domainSize),
            Scale = SpeedMax,
            Domain = domainSize,
            Pos = position,
            Origin = domainMin,
            Axis = axis,
            OccSize = occSize,
            Mode = HasScalars ? SliceField : 0,
            ScalarScale = ScalarScale,
            ClipMin = ClipMin,
            ClipMax = ClipMax,
        };
        cmd.SetPipelineState(psoSlice!);
        unsafe { cmd.SetGraphicsRoot32BitConstants(0, SliceConstants.Dwords, (IntPtr)(&c), 0); }
        cmd.DrawInstanced(6, 1, 0, 0);

        // The vector plot on the sheet: one arrow per grid cell of the two in-plane axes, outline then core.
        var span = c.Max - c.Min;
        var (ea, eb) = axis switch { 0 => (span.X, span.Y), 1 => (span.Y, span.Z), _ => (span.X, span.Z) };
        int columns = Math.Max(1, (int)MathF.Ceiling(ea / SliceArrowSpacing));
        int rows = Math.Max(1, (int)MathF.Ceiling(eb / SliceArrowSpacing));
        cmd.SetPipelineState(psoSliceArrow!);
        for (int pass = 0; pass < 2; pass++)
        {
            c.Arrow = new Vector3(SliceArrowSpacing, columns, pass);
            unsafe { cmd.SetGraphicsRoot32BitConstants(0, SliceConstants.Dwords, (IntPtr)(&c), 0); }
            cmd.DrawInstanced(9, (uint)(columns * rows), 0, 0);
        }
    }

    /// <summary>Publish a field. Safe from any thread and safe to call at 5-15 Hz: the render thread picks up
    /// the newest one, older unconsumed fields are simply dropped, and particles are NOT respawned.</summary>
    public void SetField(FieldVolume volume) => Volatile.Write(ref pendingField, volume);

    public void RequestCount(int count) => Volatile.Write(ref pendingCount, Math.Max(1024, count));

    // ---------------------------------------------------------------- per frame

    /// <summary>Uploads anything pending and runs the fixed-rate advection steps. Must be called with an open
    /// command list, before <see cref="Draw"/>. <paramref name="pendingSignal"/> is the fence value this frame
    /// will signal and <paramref name="completed"/> the fence's completed value, which is how staging buffers
    /// are recycled without stalling.</summary>
    public void Update(ID3D12GraphicsCommandList cmd, float realDt, double now, ulong pendingSignal, ulong completed)
    {
        if (device == null) return;
        CollectRetired(completed);

        // Rebuilding the particle buffers also rewrites shader-visible descriptors, so it has to wait until
        // every frame that could still reference the old ones has finished on the GPU (a frame or two).
        int wanted = Volatile.Read(ref pendingCount);
        countWait = false;
        if (wanted > 0 && (wanted == Count || completed >= lastUseSignal))
        {
            Interlocked.CompareExchange(ref pendingCount, 0, wanted);
            if (wanted != Count) CreateBuffers(wanted, pendingSignal);
        }
        // GPU-bound (a locked result at the display refresh), the previous frame is always still in flight, so
        // lastUseSignal stays ahead of the fence for ever and a new count never lands. Same cure as reshapeWait:
        // skip this frame's sim, trails and slice so the fence catches up; the swap happens a frame later.
        else if (wanted > 0) countWait = true;

        var field = Interlocked.Exchange(ref pendingField, null);
        if (field != null) UploadField(cmd, field, now, pendingSignal, completed);
        RebuildCloud(pendingSignal, completed);
        if (reseedPending && !spawnStale && spawnCount > 0) { reseedPending = false; needsInit = true; }
        // Waiting to swap the field textures: skip the sim so lastUseSignal stops moving and the fence can
        // catch up. A few frames of frozen particles, once, when the domain changes.
        if (reshapeWait || countWait) return;
        if (Interlocked.Exchange(ref pendingScalars, null) is { } scalarData) UploadScalars(cmd, scalarData, pendingSignal);
        if (!Ready || !Enabled) return;
        if (View == ParticleView.Still && !StillParticles) return;     // hidden: no reason to advect them either

        blend = blendDuration <= 0 ? 1f : (float)Math.Clamp((now - blendStart) / blendDuration, 0, 1);

        cmd.SetDescriptorHeaps(heap!);
        cmd.SetComputeRootSignature(computeRoot);
        cmd.SetComputeRootDescriptorTable(1, heap!.GetGPUDescriptorHandleForHeapStart());

        if (needsInit)
        {
            needsInit = false;
            accumulator = 0;
            Dispatch(cmd, psoInit!, SimStep);
        }

        lastUseSignal = pendingSignal;      // this frame reads the current buffers and descriptors
        if (Frozen) accumulator = 0;        // live run paused: particles stay drawn where they are, no advect/age/respawn
        else accumulator += Math.Clamp(realDt, 0, 0.25f);
        if (accumulator > SimStep * 6) accumulator = SimStep * 2;    // came back from a stall, do not catch up
        int steps = 0;
        while (accumulator >= SimStep && steps < 2)
        {
            accumulator -= SimStep;
            steps++;
            head = (head + 1) % TrailLength;
            frame++;
            Dispatch(cmd, psoStep!, SimStep);
        }
    }

    void Dispatch(ID3D12GraphicsCommandList cmd, ID3D12PipelineState pso, float step)
    {
        var c = new SimConstants
        {
            DomainMin = domainMin,
            TexelWorld = texelWorld,
            DomainSize = domainSize,
            Dt = step * TimeScale,
            BoxMin = boxMin,
            RealDt = step,
            BoxMax = boxMax,
            MinSpeed = 0.01f,
            OccSize = occSize,
            OccTexel = occTexel,
            Count = (uint)Count,
            Head = (uint)head,
            TrailLen = TrailLength,
            SpawnCount = (uint)spawnCount,
            SpawnRooms = (uint)spawnRooms,
            SpawnInflow = (uint)spawnInflow,
            Frame = (uint)frame,
            Blend = blend,
            MaxLife = 7f,
        };
        cmd.SetPipelineState(pso);
        unsafe { cmd.SetComputeRoot32BitConstants(0, SimConstants.Dwords, (IntPtr)(&c), 0); }
        cmd.Dispatch((uint)((Count + ThreadGroup - 1) / ThreadGroup), 1, 1);
        cmd.ResourceBarrierUnorderedAccessView(particles!);
        cmd.ResourceBarrierUnorderedAccessView(trails!);
    }

    public void Draw(ID3D12GraphicsCommandList cmd, Matrix4x4 viewProj, float brightness, float outdoorDim,
        float baseAlpha, float theme, int viewportWidth)
    {
        if (!Ready || !Enabled || countWait) return;
        bool still = View == ParticleView.Still;
        if (still && !StillParticles) return;

        cmd.ResourceBarrierTransition(particles!, ResourceStates.UnorderedAccess, ResourceStates.NonPixelShaderResource);
        cmd.ResourceBarrierTransition(trails!, ResourceStates.UnorderedAccess, ResourceStates.NonPixelShaderResource);

        // Still view: a shorter trail, one cool colour, and nothing slower than the cloud threshold, so a lane
        // never sits on top of an amber pocket and the two colours cannot mix into mud.
        int drawn = Math.Clamp(still ? Math.Min(DrawnTrail, 10) : DrawnTrail, 4, TrailLength);
        var c = new DrawConstants
        {
            ViewProj = viewProj,
            Head = (uint)head,
            TrailLen = (uint)drawn,
            SpeedMax = SpeedMax,
            Brightness = brightness,
            OutdoorDim = outdoorDim,
            BaseAlpha = baseAlpha,
            StillAlpha = still ? 0.32f : 1f,
            StillCut = still ? MathF.Max(StillThreshold, 1e-3f) : 0f,
            HouseMin = houseMin,
            RingLen = TrailLength,
            HouseMax = houseMax,
            Theme = theme,
            IsoMin = TrailClipMin,
            IsoMax = TrailClipMax,
        };
        cmd.SetDescriptorHeaps(heap!);
        cmd.SetGraphicsRootSignature(drawRoot);
        cmd.SetGraphicsRootDescriptorTable(1, heap!.GetGPUDescriptorHandleForHeapStart() + (int)(GraphicsTableOffset * descriptorSize));
        cmd.SetPipelineState(psoTrail!);
        cmd.IASetPrimitiveTopology(PrimitiveTopology.LineList);
        unsafe { cmd.SetGraphicsRoot32BitConstants(0, DrawConstants.Dwords, (IntPtr)(&c), 0); }
        cmd.DrawInstanced((uint)(Count * (drawn - 1) * 2), 1, 0, 0);

        cmd.ResourceBarrierTransition(particles!, ResourceStates.NonPixelShaderResource, ResourceStates.UnorderedAccess);
        cmd.ResourceBarrierTransition(trails!, ResourceStates.NonPixelShaderResource, ResourceStates.UnorderedAccess);
    }

    // ---------------------------------------------------------------- resources

    /// <summary>Retirement list: a resource the GPU may still be reading is kept until the fence passes the
    /// signal of the last frame that used it. Disposing it inline is a use-after-free two frames deep.</summary>
    readonly List<(ID3D12Resource Resource, ulong Fence)> retired = [];
    ulong lastUseSignal;
    /// <summary>Set by the window while the live run is Paused: the sim step is skipped, the particles are still drawn.</summary>
    public volatile bool Frozen;

    void Retire(ID3D12Resource? resource, ulong fence)
    {
        if (resource != null) retired.Add((resource, fence));
    }

    void CollectRetired(ulong completed)
    {
        for (int i = retired.Count - 1; i >= 0; i--)
        {
            if (retired[i].Fence > completed) continue;
            retired[i].Resource.Dispose();
            retired.RemoveAt(i);
        }
    }

    void CreateBuffers(int count, ulong pendingSignal)
    {
        Retire(particles, pendingSignal);
        Retire(trails, pendingSignal);
        particles = null;
        trails = null;
        Count = count;
        particles = device!.CreateCommittedResource(HeapProperties.DefaultHeapProperties, HeapFlags.None,
            ResourceDescription.Buffer((ulong)count * ParticleStride, ResourceFlags.AllowUnorderedAccess, 0),
            ResourceStates.UnorderedAccess, null);
        trails = device.CreateCommittedResource(HeapProperties.DefaultHeapProperties, HeapFlags.None,
            ResourceDescription.Buffer((ulong)count * TrailLength * TrailStride, ResourceFlags.AllowUnorderedAccess, 0),
            ResourceStates.UnorderedAccess, null);

        var start = heap!.GetCPUDescriptorHandleForHeapStart();
        device.CreateUnorderedAccessView(particles, null, BufferUav(count, ParticleStride), start + (int)(4 * descriptorSize));
        device.CreateUnorderedAccessView(trails, null, BufferUav(count * TrailLength, TrailStride), start + (int)(5 * descriptorSize));
        device.CreateShaderResourceView(particles, BufferSrv(count, ParticleStride), start + (int)(6 * descriptorSize));
        device.CreateShaderResourceView(trails, BufferSrv(count * TrailLength, TrailStride), start + (int)(7 * descriptorSize));
        needsInit = true;
    }

    void UploadField(ID3D12GraphicsCommandList cmd, FieldVolume v, double now, ulong pendingSignal, ulong completed)
    {
        if (particles == null) CreateBuffers(Count > 0 ? Count : 250_000, pendingSignal);

        var desc = ResourceDescription.Texture3D(Format.R16G16B16A16_Float, (uint)v.Nx, (uint)v.Ny, (ushort)v.Nz,
            1, ResourceFlags.None, TextureLayout.Unknown, 0);
        var layouts = new PlacedSubresourceFootPrint[1];
        device!.GetCopyableFootprints(desc, 0, 1, 0, layouts, new uint[1], new ulong[1], out ulong totalBytes);

        // A simulation box changes the domain, so the velocity and occupancy volumes change SHAPE, not just
        // content: copying into the old textures is an E_INVALIDARG that kills the render thread. Retire them
        // and start again -- but only once the GPU is done with the descriptors they are bound to.
        bool reshaped = fieldA != null && (v.Nx != fieldNx || v.Ny != fieldNy || v.Nz != fieldNz);
        if (reshaped && completed < lastUseSignal)
        {
            // Keep the field and try again next frame -- and make the caller STOP DISPATCHING while we wait.
            // lastUseSignal is bumped by every frame that runs the sim, so without that pause it outruns the
            // fence for ever, the reshape never happens, and a simulation box silently keeps advecting
            // particles through the previous whole-building field (measured: 1596 skips, box never uploaded).
            FieldUploadsSkipped++;
            reshapeWait = true;
            Volatile.Write(ref pendingField, v);
            return;
        }
        reshapeWait = false;

        var upload = TakeStaging(totalBytes, completed);
        if (upload == null) { FieldUploadsSkipped++; Volatile.Write(ref pendingField, v); return; }

        if (reshaped)
        {
            Retire(fieldA, pendingSignal);
            Retire(fieldB, pendingSignal);
            Retire(occupancy, pendingSignal);
            fieldA = fieldB = occupancy = null;
            spawnStale = true;
        }

        bool first = fieldA == null;
        if (first)
        {
            fieldNx = v.Nx;
            fieldNy = v.Ny;
            fieldNz = v.Nz;
            fieldA = device.CreateCommittedResource(HeapProperties.DefaultHeapProperties, HeapFlags.None, desc,
                ResourceStates.CopyDest, null);
            fieldB = device.CreateCommittedResource(HeapProperties.DefaultHeapProperties, HeapFlags.None, desc,
                ResourceStates.CopyDest, null);
            var srv = new ShaderResourceViewDescription
            {
                Format = Format.R16G16B16A16_Float,
                ViewDimension = Vortice.Direct3D12.ShaderResourceViewDimension.Texture3D,
                Shader4ComponentMapping = ShaderComponentMapping.Default,
                Texture3D = new Texture3DShaderResourceView { MostDetailedMip = 0, MipLevels = 1 },
            };
            var start = heap!.GetCPUDescriptorHandleForHeapStart();
            device.CreateShaderResourceView(fieldA, srv, start);
            device.CreateShaderResourceView(fieldB, srv, start + (int)descriptorSize);
        }
        else
        {
            // Roll the newest field back into A, so B can take the incoming one and Blend can walk A -> B.
            cmd.ResourceBarrierTransition(fieldA!, ResourceStates.NonPixelShaderResource, ResourceStates.CopyDest);
            cmd.ResourceBarrierTransition(fieldB!, ResourceStates.NonPixelShaderResource, ResourceStates.CopySource);
            cmd.CopyResource(fieldA!, fieldB!);
            cmd.ResourceBarrierTransition(fieldB!, ResourceStates.CopySource, ResourceStates.CopyDest);
        }

        int srcPitch = v.Nx * 4 * sizeof(ushort);
        uint dstPitch = layouts[0].Footprint.RowPitch;
        var dst = upload.Map<byte>(0, (int)totalBytes);
        var src = MemoryMarshal.AsBytes(v.Texels.AsSpan());
        for (int z = 0; z < v.Nz; z++)
        for (int y = 0; y < v.Ny; y++)
            src.Slice((z * v.Ny + y) * srcPitch, srcPitch)
               .CopyTo(dst.Slice((int)(((ulong)z * (ulong)v.Ny + (ulong)y) * dstPitch), srcPitch));
        upload.Unmap(0, null);

        cmd.CopyTextureRegion(new TextureCopyLocation(fieldB!, 0), 0, 0, 0,
            new TextureCopyLocation(upload, layouts[0]), null);
        cmd.ResourceBarrierTransition(fieldB!, ResourceStates.CopyDest, ResourceStates.NonPixelShaderResource);
        if (first)
        {
            cmd.CopyTextureRegion(new TextureCopyLocation(fieldA!, 0), 0, 0, 0,
                new TextureCopyLocation(upload, layouts[0]), null);
        }
        cmd.ResourceBarrierTransition(fieldA!, ResourceStates.CopyDest, ResourceStates.NonPixelShaderResource);
        stagingFence[stagingNext] = pendingSignal;
        stagingNext = (stagingNext + 1) % StagingRing;

        SpeedMax = v.SpeedMax;
        cloudSource = v;
        cloudDirty = true;
        fieldDims = $"{v.Nx}x{v.Ny}x{v.Nz}";
        domainMin = v.Origin;
        domainSize = v.Size;
        texelWorld = v.Dx;
        boxMin = v.BoxMin;
        boxMax = v.BoxMax;
        houseMin = v.HouseMin;
        houseMax = v.HouseMax;

        if (first) UploadOccupancy(cmd, v, pendingSignal);
        // UploadSpawn can grow the buffer and rewrites its descriptor: same in-flight rule as the particle
        // buffers. If it has to wait, remember that the table is stale rather than losing the new one.
        if (!v.SpawnReused || spawnCount == 0) spawnStale = true;
        if (spawnStale && completed >= lastUseSignal)
        {
            spawnStale = false;
            UploadSpawn(cmd, v, pendingSignal);
        }

        // First field: show it outright. Later ones: cross-fade over the measured publish interval.
        blendDuration = first ? 0 : Math.Clamp(now - lastPublish, 0.03, 0.5);
        blendStart = now;
        blend = first ? 1f : 0f;
        lastPublish = now;
        FieldUpdates++;
        // A reshape is a new domain (a simulation box came or went), so every parcel has to be re-seeded --
        // but only once the spawn table for the NEW volume is on the GPU. Re-seeding against the old table
        // puts them back at whole-building positions, where they sit outside the box drawing stale trails.
        if (first) reseedPending = true;
    }

    bool reseedPending, reshapeWait, countWait;

    /// <summary>This frame skipped the sim and the trails to let a buffer or texture swap land (render thread).</summary>
    public bool Waiting => reshapeWait || countWait;

    int fieldNx, fieldNy, fieldNz, scalarNx, scalarNy, scalarNz;

    /// <summary>A staging buffer the GPU has finished with, or null if all three are still in flight - in which
    /// case the caller keeps the field and tries again next frame instead of stalling.</summary>
    ID3D12Resource? TakeStaging(ulong bytes, ulong completed)
    {
        if (bytes > stagingBytes)
        {
            // A bigger model needs bigger upload buffers; the old ones may still be feeding a copy.
            for (int i = 0; i < StagingRing; i++) { Retire(staging[i], stagingFence[i]); staging[i] = null; stagingFence[i] = 0; }
            stagingBytes = bytes;
            stagingNext = 0;
        }
        if (stagingFence[stagingNext] > completed) return null;
        return staging[stagingNext] ??= device!.CreateCommittedResource(HeapProperties.UploadHeapProperties,
            HeapFlags.None, ResourceDescription.Buffer(stagingBytes, ResourceFlags.None, 0),
            ResourceStates.GenericRead, null);
    }

    Half[]? pendingScalars;
    int pendingScalarNx, pendingScalarNy, pendingScalarNz;
    float pendingScalarScale;

    /// <summary>Publishes one scalar volume (pressure or temperature) from an analysis pass. Any thread.</summary>
    public void SetScalarVolume(Half[] data, int nx, int ny, int nz, float scale)
    {
        pendingScalarNx = nx;
        pendingScalarNy = ny;
        pendingScalarNz = nz;
        pendingScalarScale = scale;
        Volatile.Write(ref pendingScalars, data);
    }

    void UploadScalars(ID3D12GraphicsCommandList cmd, Half[] data, ulong pendingSignal)
    {
        int nx = pendingScalarNx, ny = pendingScalarNy, nz = pendingScalarNz;
        if (nx <= 0 || data.Length < nx * ny * nz) return;
        var desc = ResourceDescription.Texture3D(Format.R16_Float, (uint)nx, (uint)ny, (ushort)nz,
            1, ResourceFlags.None, TextureLayout.Unknown, 0);
        if (scalars != null && (nx != scalarNx || ny != scalarNy || nz != scalarNz))
        {
            Retire(scalars, pendingSignal);     // this frame's signal: later than any frame that could still read it
            scalars = null;
        }
        bool fresh = scalars == null;
        scalarNx = nx;
        scalarNy = ny;
        scalarNz = nz;
        if (fresh)
            scalars = device!.CreateCommittedResource(HeapProperties.DefaultHeapProperties, HeapFlags.None, desc,
                ResourceStates.CopyDest, null);
        else
            cmd.ResourceBarrierTransition(scalars!, ResourceStates.NonPixelShaderResource, ResourceStates.CopyDest);

        var layouts = new PlacedSubresourceFootPrint[1];
        device!.GetCopyableFootprints(desc, 0, 1, 0, layouts, new uint[1], new ulong[1], out ulong totalBytes);
        if (scalarUpload == null || scalarUpload.Description.Width < totalBytes)
        {
            Retire(scalarUpload, pendingSignal);
            scalarUpload = device.CreateCommittedResource(HeapProperties.UploadHeapProperties, HeapFlags.None,
                ResourceDescription.Buffer(totalBytes, ResourceFlags.None, 0), ResourceStates.GenericRead, null);
        }

        int srcPitch = nx * sizeof(ushort);
        uint dstPitch = layouts[0].Footprint.RowPitch;
        var dst = scalarUpload!.Map<byte>(0, (int)totalBytes);
        var src = MemoryMarshal.AsBytes(data.AsSpan());
        for (int z = 0; z < nz; z++)
        for (int y = 0; y < ny; y++)
            src.Slice((z * ny + y) * srcPitch, srcPitch)
               .CopyTo(dst.Slice((int)(((ulong)z * (ulong)ny + (ulong)y) * dstPitch), srcPitch));
        scalarUpload.Unmap(0, null);

        cmd.CopyTextureRegion(new TextureCopyLocation(scalars!, 0), 0, 0, 0,
            new TextureCopyLocation(scalarUpload, layouts[0]), null);
        cmd.ResourceBarrierTransition(scalars!, ResourceStates.CopyDest, ResourceStates.NonPixelShaderResource);

        device.CreateShaderResourceView(scalars, new ShaderResourceViewDescription
        {
            Format = Format.R16_Float,
            ViewDimension = Vortice.Direct3D12.ShaderResourceViewDimension.Texture3D,
            Shader4ComponentMapping = ShaderComponentMapping.Default,
            Texture3D = new Texture3DShaderResourceView { MostDetailedMip = 0, MipLevels = 1 },
        }, heap!.GetCPUDescriptorHandleForHeapStart() + (int)(ScalarSlot * descriptorSize));
        ScalarScale = MathF.Max(pendingScalarScale, 1e-3f);
        HasScalars = true;
    }

    void UploadOccupancy(ID3D12GraphicsCommandList cmd, FieldVolume v, ulong pendingSignal)
    {
        occSize = v.OccSize;
        occTexel = v.ODx;
        occDims = $"{v.Ox}x{v.Oy}x{v.Oz}";

        var desc = ResourceDescription.Texture3D(Format.R8_UNorm, (uint)v.Ox, (uint)v.Oy, (ushort)v.Oz,
            1, ResourceFlags.None, TextureLayout.Unknown, 0);
        occupancy = device!.CreateCommittedResource(HeapProperties.DefaultHeapProperties, HeapFlags.None, desc,
            ResourceStates.CopyDest, null);

        var layouts = new PlacedSubresourceFootPrint[1];
        device.GetCopyableFootprints(desc, 0, 1, 0, layouts, new uint[1], new ulong[1], out ulong totalBytes);
        Retire(occUpload, pendingSignal);
        occUpload = device.CreateCommittedResource(HeapProperties.UploadHeapProperties, HeapFlags.None,
            ResourceDescription.Buffer(totalBytes, ResourceFlags.None, 0), ResourceStates.GenericRead, null);

        uint dstPitch = layouts[0].Footprint.RowPitch;
        var dst = occUpload.Map<byte>(0, (int)totalBytes);
        for (int z = 0; z < v.Oz; z++)
        for (int y = 0; y < v.Oy; y++)
            v.Occupancy.AsSpan((z * v.Oy + y) * v.Ox, v.Ox)
             .CopyTo(dst.Slice((int)(((ulong)z * (ulong)v.Oy + (ulong)y) * dstPitch), v.Ox));
        occUpload.Unmap(0, null);

        cmd.CopyTextureRegion(new TextureCopyLocation(occupancy, 0), 0, 0, 0,
            new TextureCopyLocation(occUpload, layouts[0]), null);
        cmd.ResourceBarrierTransition(occupancy, ResourceStates.CopyDest, ResourceStates.NonPixelShaderResource);

        device.CreateShaderResourceView(occupancy, new ShaderResourceViewDescription
        {
            Format = Format.R8_UNorm,
            ViewDimension = Vortice.Direct3D12.ShaderResourceViewDimension.Texture3D,
            Shader4ComponentMapping = ShaderComponentMapping.Default,
            Texture3D = new Texture3DShaderResourceView { MostDetailedMip = 0, MipLevels = 1 },
        }, heap!.GetCPUDescriptorHandleForHeapStart() + (int)(3 * descriptorSize));
    }

    bool spawnStale;

    void UploadSpawn(ID3D12GraphicsCommandList cmd, FieldVolume v, ulong pendingSignal)
    {
        var table = v.Spawn;
        int bytes = table.Length * 16;
        bool created = spawn == null || spawnCount < table.Length;
        if (created)
        {
            Retire(spawn, pendingSignal);
            Retire(spawnUpload, pendingSignal);
            spawn = device!.CreateCommittedResource(HeapProperties.DefaultHeapProperties, HeapFlags.None,
                ResourceDescription.Buffer((ulong)bytes, ResourceFlags.None, 0), ResourceStates.Common, null);
            spawnUpload = device.CreateCommittedResource(HeapProperties.UploadHeapProperties, HeapFlags.None,
                ResourceDescription.Buffer((ulong)bytes, ResourceFlags.None, 0), ResourceStates.GenericRead, null);
        }

        var dst = spawnUpload!.Map<Vector4>(0, table.Length);
        table.CopyTo(dst);
        spawnUpload.Unmap(0, null);

        cmd.ResourceBarrierTransition(spawn!, created ? ResourceStates.Common : ResourceStates.NonPixelShaderResource,
            ResourceStates.CopyDest);
        cmd.CopyBufferRegion(spawn!, 0, spawnUpload, 0, (ulong)bytes);
        cmd.ResourceBarrierTransition(spawn!, ResourceStates.CopyDest, ResourceStates.NonPixelShaderResource);

        spawnCount = table.Length;
        spawnRooms = v.SpawnRooms;
        spawnInflow = v.SpawnInflow;
        device!.CreateShaderResourceView(spawn, BufferSrv(spawnCount, 16),
            heap!.GetCPUDescriptorHandleForHeapStart() + (int)(2 * descriptorSize));
    }

    static ShaderResourceViewDescription BufferSrv(int elements, int stride) => new()
    {
        Format = Format.Unknown,
        ViewDimension = Vortice.Direct3D12.ShaderResourceViewDimension.Buffer,
        Shader4ComponentMapping = ShaderComponentMapping.Default,
        Buffer = new BufferShaderResourceView { FirstElement = 0, NumElements = (uint)elements, StructureByteStride = (uint)stride },
    };

    static UnorderedAccessViewDescription BufferUav(int elements, int stride) => new()
    {
        Format = Format.Unknown,
        ViewDimension = UnorderedAccessViewDimension.Buffer,
        Buffer = new BufferUnorderedAccessView { FirstElement = 0, NumElements = (uint)elements, StructureByteStride = (uint)stride },
    };

    public void Dispose()
    {
        // Called after the renderer has waited for the GPU, so everything can go now.
        foreach (var (resource, _) in retired) resource.Dispose();
        retired.Clear();
        particles?.Dispose();
        trails?.Dispose();
        particles = null;
        trails = null;
        for (int i = 0; i < StagingRing; i++) { staging[i]?.Dispose(); staging[i] = null; }
        cloudBuffer?.Dispose();
        cloudBuffer = null;
        cloudBuilt = false;
        cloudCount = 0;
        spawn?.Dispose();
        spawnUpload?.Dispose();
        occUpload?.Dispose();
        occupancy?.Dispose();
        scalars?.Dispose();
        scalarUpload?.Dispose();
        fieldA?.Dispose();
        fieldB?.Dispose();
        psoInit?.Dispose();
        psoStep?.Dispose();
        psoTrail?.Dispose();
        psoSlice?.Dispose();
        psoSliceArrow?.Dispose();
        psoCloud?.Dispose();
        computeRoot?.Dispose();
        drawRoot?.Dispose();
        sliceRoot?.Dispose();
        cloudRoot?.Dispose();
        heap?.Dispose();
        spawn = spawnUpload = occUpload = occupancy = scalars = scalarUpload = fieldA = fieldB = null;
        psoInit = psoStep = psoTrail = psoSlice = psoSliceArrow = psoCloud = null;
        computeRoot = drawRoot = sliceRoot = cloudRoot = null;

        heap = null;
        device = null;
        spawnCount = 0;
        Count = 0;
    }
}
