namespace WindFlow.App.Viewport;

/// <summary>The whole viewport shader set. Compiled at start-up with D3DCompile (shader model 5.1, which every
/// D3D12 device supports and needs no extra native DLL).
/// ponytail: SM 5.1 covers the compute-shader particle pass too; move to Vortice.Dxc only if wave intrinsics
/// or 16-bit types turn out to be worth it.</summary>
public static class Shaders
{
    public const string Profile5Vs = "vs_5_1", Profile5Ps = "ps_5_1", Profile5Cs = "cs_5_1";

    /// <summary>Particle advection (compute) and trail drawing (vertex/pixel). Trails are world-space ring
    /// buffers, not a faded framebuffer: the camera orbits, so screen-space accumulation would smear.</summary>
    public const string ParticleSource = """
struct Particle
{
    float3 pos;
    float  age;      // seconds of wall-clock life
    float  stall;    // seconds spent below MinSpeed
    float  pool;     // 0 = room air, 1 = inflow at an opening, 2 = outdoor
    float  life;     // seconds until respawn
    float  pad;      // keeps the 32-byte stride
};

cbuffer Sim : register(b0)
{
    float3 DomainMin;  float TexelWorld;
    float3 DomainSize; float Dt;         // advection seconds = real dt * time scale
    float3 BoxMin;     float RealDt;     // the building plus a margin: outside it particles are recycled
    float3 BoxMax;     float MinSpeed;   // below this for 0.6 s a parcel is recycled
    float3 OccSize;    float OccTexel;   // full-resolution occupancy volume, same origin as the velocity one
    uint   Count;      uint  Head;       // Head = ring-buffer slot written this step
    uint   TrailLen;   uint  SpawnCount;
    uint   SpawnRooms; uint  SpawnInflow;
    uint   Frame;      float Blend;      // 1 = FieldB only; < 1 interpolates from FieldA (the previous field)
    float  MaxLife;    float3 SimPad;
};

Texture3D<float4>        FieldA : register(t0);     // previous published field
Texture3D<float4>        FieldB : register(t1);     // newest published field
StructuredBuffer<float4> Spawn  : register(t2);
Texture3D<float>         Occupancy : register(t3);  // full grid resolution, 1 = air, 0 = solid
RWStructuredBuffer<Particle> Particles : register(u0);
RWStructuredBuffer<float4>   Trails    : register(u1);   // xyz = position, w = speed
SamplerState LinearClamp : register(s0);
SamplerState PointClamp  : register(s1);

uint Hash(uint x)
{
    x ^= x >> 16; x *= 0x7feb352du;
    x ^= x >> 15; x *= 0x846ca68bu;
    x ^= x >> 16;
    return x;
}
float Rand(uint x) { return (Hash(x) & 0xFFFFFFu) / 16777215.0; }

// Velocity: trilinear on the downsampled volume, interpolated between the last two published fields.
float3 SampleVelocity(float3 p)
{
    float3 uvw = (p - DomainMin) / DomainSize;
    if (any(uvw < 0.0) || any(uvw > 1.0)) return float3(0, 0, 0);
    float3 b = FieldB.SampleLevel(LinearClamp, uvw, 0).xyz;
    if (Blend >= 1.0) return b;
    return lerp(FieldA.SampleLevel(LinearClamp, uvw, 0).xyz, b, Blend);
}

// Solidity: point-sampled at the full grid resolution, so a one-cell interior partition still blocks even
// though the velocity volume is downsampled past it.
bool IsSolid(float3 p)
{
    float3 uvw = (p - DomainMin) / OccSize;
    if (any(uvw < 0.0) || any(uvw > 1.0)) return true;
    return Occupancy.SampleLevel(PointClamp, uvw, 0) < 0.5;
}

void Place(inout Particle p, uint i, uint idx, uint rnd)
{
    float4 s = Spawn[min(idx, SpawnCount - 1)];
    float3 jitter = (float3(Rand(rnd + 11), Rand(rnd + 23), Rand(rnd + 37)) - 0.5) * TexelWorld;
    p.pos   = s.xyz + jitter;
    p.pool  = s.w;
    p.pad   = 0.0;
    p.age   = 0.0;
    p.stall = 0.0;
    p.life  = MaxLife * (0.35 + 0.65 * Rand(rnd + 51));
    // Collapse the whole trail onto the new position, otherwise the old tail streaks across the building.
    for (uint t = 0; t < TrailLen; t++) Trails[i * TrailLen + t] = float4(p.pos, 0.0);
}

void Respawn(inout Particle p, uint i, uint rnd)
{
    Place(p, i, Hash(rnd) % SpawnCount, rnd);
}

[numthreads(256, 1, 1)]
void CSInit(uint3 id : SV_DispatchThreadID)
{
    uint i = id.x;
    if (i >= Count) return;
    Particle p = (Particle)0;
    uint rnd = Hash(i * 2654435761u + 17u);
    Place(p, i, rnd % SpawnCount, rnd);
    p.age = p.life * Rand(i * 977u + 5u);      // stagger the first respawn wave
    Particles[i] = p;
}

[numthreads(256, 1, 1)]
void CSStep(uint3 id : SV_DispatchThreadID)
{
    uint i = id.x;
    if (i >= Count) return;
    Particle p = Particles[i];
    uint rnd = Hash(i * 2654435761u + Frame * 40503u);

    if (IsSolid(p.pos))                         // inside a wall or outside the domain
    {
        Respawn(p, i, rnd);
        Particles[i] = p;
        return;
    }

    float3 v0  = SampleVelocity(p.pos);
    float3 mid = p.pos + v0 * (Dt * 0.5);
    // RK2. No occupancy fetch at the midpoint: solid cells average to ~zero velocity, so a midpoint inside a
    // wall already falls back to Euler, and the fetch costs real bandwidth on an integrated GPU.
    float3 v1  = SampleVelocity(mid);
    float  spd = length(v1);
    if (spd < 1e-4) { v1 = v0; spd = length(v0); }

    // CFL clamp against the *occupancy* texel: a step longer than one cell can jump straight through a
    // 250 mm wall and leave a streak inside the house.
    float3 step = v1 * Dt;
    float  len  = length(step);
    float  cap  = OccTexel * 0.6;
    if (len > cap) step *= cap / len;
    float3 np = p.pos + step;

    if (IsSolid(np) || any(np < BoxMin) || any(np > BoxMax))
    {
        Respawn(p, i, rnd);
        Particles[i] = p;
        return;
    }

    p.age  += RealDt;
    p.stall = (MinSpeed > 0.0 && spd < MinSpeed) ? p.stall + RealDt : 0.0;
    if (p.age > p.life || p.stall > 0.6)
    {
        Respawn(p, i, rnd);
        Particles[i] = p;
        return;
    }

    p.pos = np;
    Particles[i] = p;
    Trails[i * TrailLen + Head] = float4(np, spd);
}

// ---------------------------------------------------------------- trail drawing

cbuffer Draw : register(b0)
{
    float4x4 ViewProj;
    uint  DrawHead;   uint  DrawTrailLen;   // segments drawn; the ring itself is always RingLen
    float SpeedMax;   float Brightness;
    float OutdoorDim; float BaseAlpha;
    float StillAlpha; float StillCut;     // Still view: alpha scale and the cloud threshold (0 = Speed view)
    float3 HouseMin;  float RingLenF;
    float3 HouseMax;  float Theme;         // 0 dark, 1 light: the still-air lane colour
    float4 IsoMin;    float4 IsoMax;         // room isolate box, w > 0.5 enables
};

StructuredBuffer<Particle> ParticlesSrv : register(t0);
StructuredBuffer<float4>   TrailsSrv    : register(t1);

// Viridis, Matt Zucker's polynomial fit: perceptually even and legible on a near-black blue canvas.
// The same coefficients as Palette.Viridis (MainWindow) and WindFlow.Viz RenderKit.Viridis, which renders the
// report plates: keep all three byte-identical.
float3 Viridis(float t)
{
    const float3 c0 = float3( 0.2777273272,  0.0054073445,  0.3340998053);
    const float3 c1 = float3( 0.1050930431,  1.4046135299,  1.3845901626);
    const float3 c2 = float3(-0.3308618287,  0.2148475595,  0.0950951630);
    const float3 c3 = float3(-4.6342304990, -5.7991009734, -19.3324409563);
    const float3 c4 = float3( 6.2282699363, 14.1799333668,  56.6905526007);
    const float3 c5 = float3( 4.7763849977, -13.7451453777, -65.3530326334);
    const float3 c6 = float3(-5.4354558559,  4.6458526122,  26.3124352496);
    t = saturate(t);
    return saturate(c0 + t * (c1 + t * (c2 + t * (c3 + t * (c4 + t * (c5 + t * c6))))));
}


struct TrailOut { float4 pos : SV_Position; float4 color : COLOR0; };

TrailOut VSTrail(uint vid : SV_VertexID)
{
    uint RingLen = (uint)RingLenF;
    uint perParticle = (DrawTrailLen - 1) * 2;
    uint pi   = vid / perParticle;
    uint rem  = vid - pi * perParticle;
    uint seg  = rem >> 1;
    uint end  = rem & 1;
    uint back = seg + end;                       // 0 = head, DrawTrailLen-1 = tail
    uint slot = (DrawHead + RingLen - back) % RingLen;

    TrailOut o;
    float4 t = TrailsSrv[pi * RingLen + slot];

    // Bright inside the building envelope, a dim stub outside it. Tested per trail point, not per particle, so
    // air leaving through a window fades exactly at the facade instead of dragging a bright plume outdoors.
    float indoor = (all(t.xyz > HouseMin) && all(t.xyz < HouseMax)) ? 1.0 : 0.0;
    float span = indoor > 0.5 ? (float)(DrawTrailLen - 1) : 4.0;
    float fade = saturate(1.0 - (float)back / span);

    // Weight by speed as well as by trail age, so stagnant air recedes into haze and the jets through
    // windows, doors and the stair void carry the image.
    // On paper the slow end of Viridis is a dark ink, so still air gets less weight there and the jets keep theirs.
    float moving = lerp(0.26, 0.14, Theme) + lerp(0.74, 0.86, Theme) * saturate(t.w / SpeedMax);
    float3 rgb = Viridis(0.20 + 0.80 * saturate(t.w / SpeedMax)) * Brightness;
    if (StillCut > 0.0)
    {
        // Still view: cyan-white flow lanes (steel blue on paper). Air slower than the cloud threshold is the
        // cloud's job, so it is not drawn at all and a lane only appears where there is no amber under it.
        moving = saturate((t.w - StillCut) / StillCut) * (0.35 + 0.65 * saturate(t.w / SpeedMax));
        rgb = lerp(float3(0.62, 0.93, 1.00), float3(0.22, 0.48, 0.68), Theme) * Brightness;
    }
    float alpha = fade * fade * moving * BaseAlpha * StillAlpha * (indoor > 0.5 ? 1.0 : OutdoorDim);
    // Room isolate: tested per trail point, like the envelope, so a trail is cut exactly at the room's margin.
    if (IsoMin.w > 0.5 && (any(t.xyz < IsoMin.xyz) || any(t.xyz > IsoMax.xyz))) alpha = 0.0;

    // Invisible segments are thrown behind the near plane instead of rasterised. Both endpoints of a dead
    // segment land there, so it is clipped whole - a real saving on a fill-rate-bound integrated GPU.
    if (alpha < 0.003)
    {
        o.pos = float4(0.0, 0.0, -2.0, 1.0);
        o.color = 0.0;
        return o;
    }

    o.pos = mul(ViewProj, float4(t.xyz, 1.0));
    o.color = float4(rgb * alpha, alpha);        // premultiplied
    return o;
}

float4 PSTrail(TrailOut i) : SV_Target { return i.color; }

// ---------------------------------------------------------------- still-air cloud
// One camera-facing soft sprite per stagnant velocity texel, rebuilt on the CPU only when a field arrives.
// Instance data: xyz = texel centre, w = stillness, 1 at rest falling to 0 at the threshold.

cbuffer CloudCb : register(b0)
{
    float4x4 CloudViewProj;
    float3 CloudRight;   float CloudRadius;   // world half-size of a sprite
    float3 CloudUp;      float CloudAlpha;
    float4 CloudClipMin; float4 CloudClipMax;
};

StructuredBuffer<float4> CloudSrv : register(t0);

struct CloudOut
{
    float4 pos   : SV_Position;
    float2 uv    : TEXCOORD0;
    float4 color : COLOR0;
    float3 wpos  : TEXCOORD1;
};

CloudOut VSCloud(uint vid : SV_VertexID, uint iid : SV_InstanceID)
{
    float4 c = CloudSrv[iid];
    float2 q = float2((vid == 1 || vid == 4 || vid == 5) ? 1.0 : -1.0,
                      (vid == 2 || vid == 4 || vid == 3) ? 1.0 : -1.0);
    // Slower air draws a little larger as well as brighter, so the deadest pockets merge into one cloud.
    float r = CloudRadius * (0.85 + 0.30 * c.w);
    float3 p = c.xyz + (CloudRight * q.x + CloudUp * q.y) * r;
    CloudOut o;
    o.pos = mul(CloudViewProj, float4(p, 1.0));
    o.uv = q;
    o.wpos = c.xyz;
    // Amber, from a dim ember at the threshold to a warm glow for air that is not moving at all.
    // ponytail: stillness is speed only. Colour by mean age of air once the air-quality scalars reach the
    // live field (Core ScalarFields.Age): pack age into w on the CPU side and ramp on it here.
    // Alpha rises with the square of stillness: air near the threshold is a faint veil, dead air is solid.
    float3 dim = float3(0.55, 0.20, 0.03), hot = float3(1.00, 0.62, 0.16);
    float a = CloudAlpha * (0.10 + 0.90 * c.w * c.w);
    o.color = float4(lerp(dim, hot, c.w) * a, a);
    return o;
}

float4 PSCloud(CloudOut i) : SV_Target
{
    if (CloudClipMin.w > 0.5 && (any(i.wpos < CloudClipMin.xyz) || any(i.wpos > CloudClipMax.xyz))) discard;
    float d2 = dot(i.uv, i.uv);
    if (d2 > 1.0) discard;
    float soft = (exp(-2.4 * d2) - 0.0907) / 0.9093;   // gaussian, 1 at the centre and 0 at the rim
    return i.color * soft;                       // premultiplied
}

// ---------------------------------------------------------------- slice plane

cbuffer SliceCb : register(b0)
{
    float4x4 SliceViewProj;
    float3 SliceMin;     float SliceAlpha;
    float3 SliceMax;     float SliceScale;    // speed at the top of the colour ramp
    float3 SliceDomain;  float SlicePos;      // velocity volume size; plane coordinate along its axis
    float3 SliceOrigin;  float SliceAxis;     // 0 = horizontal (z), 1 = x plane, 2 = y plane
    float3 SliceOccSize; float SliceMode;     // 0 speed, 1 pressure, 2 temperature
    float3 SliceArrow;   float SliceScalarScale; // arrows: x grid spacing (m), y columns, z pass (0 outline, 1 core)
    float4 SliceClipMin; float4 SliceClipMax;
};

Texture3D<float4> SliceField : register(t0);
Texture3D<float>  SliceOcc   : register(t1);
Texture3D<float>  SliceScalar : register(t2);
SamplerState SliceLinear : register(s0);
SamplerState SlicePoint  : register(s1);

struct SliceOut { float4 pos : SV_Position; float3 wpos : TEXCOORD0; };

SliceOut VSSlice(uint vid : SV_VertexID)
{
    // Two triangles spanning the focus box in the two axes the plane does not own.
    float2 c = float2((vid == 1 || vid == 4 || vid == 5) ? 1.0 : 0.0,
                      (vid == 2 || vid == 4 || vid == 3) ? 1.0 : 0.0);
    float3 p;
    if (SliceAxis < 0.5)      p = float3(lerp(SliceMin.x, SliceMax.x, c.x), lerp(SliceMin.y, SliceMax.y, c.y), SlicePos);
    else if (SliceAxis < 1.5) p = float3(SlicePos, lerp(SliceMin.y, SliceMax.y, c.x), lerp(SliceMin.z, SliceMax.z, c.y));
    else                      p = float3(lerp(SliceMin.x, SliceMax.x, c.x), SlicePos, lerp(SliceMin.z, SliceMax.z, c.y));

    SliceOut o;
    o.pos = mul(SliceViewProj, float4(p, 1.0));
    o.wpos = p;
    return o;
}

float4 PSSlice(SliceOut i) : SV_Target
{
    if (SliceClipMin.w > 0.5 && (any(i.wpos < SliceClipMin.xyz) || any(i.wpos > SliceClipMax.xyz))) discard;
    float3 occUv = (i.wpos - SliceOrigin) / SliceOccSize;
    if (any(occUv < 0.0) || any(occUv > 1.0)) discard;
    if (SliceOcc.SampleLevel(SlicePoint, occUv, 0) < 0.5) discard;      // inside a wall

    float3 uvw = (i.wpos - SliceOrigin) / SliceDomain;
    float3 rgb;
    float a;
    if (SliceMode < 0.5)
    {
        float speed = length(SliceField.SampleLevel(SliceLinear, uvw, 0).xyz);
        float t = saturate(speed / max(SliceScale, 1e-3));
        rgb = Viridis(0.06 + 0.94 * t);
        a = SliceAlpha * (0.90 + 0.10 * t);                             // a solid sheet; still air a touch quieter
    }
    else
    {
        // Pressure and temperature: diverging about zero (pressure) or about the mid value (temperature).
        float v = SliceScalar.SampleLevel(SliceLinear, uvw, 0);
        float t = clamp(v / max(SliceScalarScale, 1e-3), -1.0, 1.0);
        float3 cold = float3(0.20, 0.52, 0.92), mid = float3(0.86, 0.88, 0.92), warm = float3(0.94, 0.42, 0.30);
        rgb = t < 0.0 ? lerp(mid, cold, -t) : lerp(mid, warm, t);
        a = SliceAlpha * (0.90 + 0.10 * abs(t));
    }
    return float4(rgb * a, a);
}

// ---------------------------------------------------------------- slice arrows
// One in-plane velocity arrow per grid cell of the cut, the vector plot of a CFD figure. Nine vertices per
// instance: a shaft quad and a head triangle, generated here from the instance id. Drawn twice: a light,
// slightly fatter outline pass, then a dark core, so the same arrow reads on the dark and the bright end of
// the ramp alike. Length follows the in-plane speed on the plane's own scale, capped so neighbours never
// touch; arrows in a wall, past the clip box or in near-still air collapse behind the near plane.
TrailOut VSSliceArrow(uint vid : SV_VertexID, uint iid : SV_InstanceID)
{
    TrailOut o;
    o.pos = float4(0.0, 0.0, -2.0, 1.0);
    o.color = 0.0;

    float spacing = SliceArrow.x;
    uint  columns = max((uint)SliceArrow.y, 1u);
    bool  core    = SliceArrow.z > 0.5;
    float3 ea, eb, n;
    if (SliceAxis < 0.5)      { ea = float3(1, 0, 0); eb = float3(0, 1, 0); n = float3(0, 0, 1); }
    else if (SliceAxis < 1.5) { ea = float3(0, 1, 0); eb = float3(0, 0, 1); n = float3(1, 0, 0); }
    else                      { ea = float3(1, 0, 0); eb = float3(0, 0, 1); n = float3(0, 1, 0); }
    float2 cell = float2(iid % columns, iid / columns);
    float3 c = ea * (dot(SliceMin, ea) + (cell.x + 0.5) * spacing)
             + eb * (dot(SliceMin, eb) + (cell.y + 0.5) * spacing)
             + n * SlicePos;
    if (dot(c, eb) > dot(SliceMax, eb)) return o;
    if (SliceClipMin.w > 0.5 && (any(c < SliceClipMin.xyz) || any(c > SliceClipMax.xyz))) return o;

    float3 uvw = (c - SliceOrigin) / SliceDomain;
    if (any(uvw < 0.0) || any(uvw > 1.0)) return o;
    float3 u = SliceField.SampleLevel(SliceLinear, uvw, 0).xyz;
    u -= n * dot(u, n);                                   // in-plane component
    float speed = length(u);
    if (speed < 0.05) return o;                           // near-still air: no direction worth drawing
    float3 d = u / speed;
    float3 p = normalize(cross(n, d));

    float  t    = saturate(speed / max(SliceScale, 1e-3));
    float  len  = spacing * (0.30 + 0.60 * t);           // a floor keeps slow air legible, a cap keeps cells apart
    float  head = 0.40 * len;
    float  w    = spacing * 0.040;                        // shaft half-width
    float  hw   = spacing * 0.105;                        // head half-width
    float  tail = 0.0, tip = len;
    if (!core)
    {
        float e = spacing * 0.034;                        // outline rim
        w += e; hw += e; tail -= e;
        tip += e * sqrt(1.0 + (head * head) / (hw * hw));   // the apex of an offset isosceles triangle
    }
    // The ends must be in air too, or an arrow would poke through a partition.
    float3 occA = (c + d * tail - SliceOrigin) / SliceOccSize, occB = (c + d * tip - SliceOrigin) / SliceOccSize;
    if (any(occA < 0.0) || any(occA > 1.0) || any(occB < 0.0) || any(occB > 1.0)) return o;
    if (SliceOcc.SampleLevel(SlicePoint, occA, 0) < 0.5 || SliceOcc.SampleLevel(SlicePoint, occB, 0) < 0.5) return o;

    float shaftEnd = len - head;
    float2 v;
    switch (vid)
    {
        case 0: v = float2(tail, -w); break;
        case 1: v = float2(tail,  w); break;
        case 2: v = float2(shaftEnd,  w); break;
        case 3: v = float2(tail, -w); break;
        case 4: v = float2(shaftEnd,  w); break;
        case 5: v = float2(shaftEnd, -w); break;
        case 6: v = float2(shaftEnd, -hw); break;
        case 7: v = float2(shaftEnd,  hw); break;
        default: v = float2(tip, 0.0); break;
    }
    float3 wp = c + d * v.x + p * v.y;
    o.pos = mul(SliceViewProj, float4(wp, 1.0));
    float4 col = core ? float4(0.05, 0.07, 0.11, 0.92) : float4(0.95, 0.97, 0.99, 0.80);
    o.color = float4(col.rgb * col.a, col.a);             // premultiplied, like the trails
    return o;
}

""";

    public const string Source = """
cbuffer Scene : register(b0)
{
    float4x4 ViewProj;   // row-major upload; HLSL reads it column-major, so mul(M, v) is the row-vector product
    float4   Eye;        // xyz = camera position, w = distance fade for lines (0 = none)
    float4   Albedo;     // rgb linear, a = alpha
    float4   Light;      // xyz = direction the sunlight travels (handles: xy = NDC half size)
    float4   ClipMin;    // xyz = section box min, w > 0.5 enables clipping
    float4   ClipMax;
    float4   BoxMin;     // the simulation box itself, drawn as wireframe edges
    float4   BoxMax;
};

struct VSIn  { float3 pos : POSITION; float3 nrm : NORMAL; };
struct VSOut { float4 pos : SV_Position; float3 wpos : TEXCOORD0; float3 nrm : TEXCOORD1; };
struct FSOut { float4 pos : SV_Position; float2 uv : TEXCOORD0; };

// Background: one oversized triangle, no vertex buffer, no depth. Values are already sRGB-encoded.
FSOut VSBackground(uint id : SV_VertexID)
{
    FSOut o;
    float2 p = float2((id << 1) & 2, id & 2);
    o.uv  = p;
    o.pos = float4(p * float2(2.0, -2.0) + float2(-1.0, 1.0), 0.0, 1.0);
    return o;
}

// Albedo is the centre colour and Light the rim, from the palette of the current Appearance.
float4 PSBackground(FSOut i) : SV_Target
{
    float d = saturate(length((i.uv - float2(0.5, 0.56)) * float2(1.0, 1.30)) * 1.5);
    float3 c = lerp(Albedo.rgb, Light.rgb, d * d);
    return float4(c, 1.0);
}

VSOut VSMain(VSIn i)
{
    VSOut o;
    o.pos  = mul(ViewProj, float4(i.pos, 1.0));
    o.wpos = i.pos;
    o.nrm  = i.nrm;
    return o;
}

float3 Gamma(float3 c) { return pow(saturate(c), 1.0 / 2.2); }

// Section box: every surface shader discards outside it when ClipMin.w is set.
bool Clipped(float3 p)
{
    if (ClipMin.w < 0.5) return false;
    return any(p < ClipMin.xyz) || any(p > ClipMax.xyz);
}


// Light.w is the Appearance: 0 dark, 1 light. The few lighting constants that live here lerp on it.
// Hemisphere ambient: cool sky from +Z, near-black bounce from the ground; on paper a bright overcast sky
// and a grey bounce, so a light wall shades softly instead of dropping to black.
float3 Hemisphere(float3 n)
{
    float3 sky    = lerp(float3(0.24, 0.31, 0.44), float3(0.60, 0.64, 0.70), Light.w);
    float3 ground = lerp(float3(0.030, 0.034, 0.042), float3(0.34, 0.35, 0.37), Light.w);
    return lerp(ground, sky, saturate(n.z * 0.5 + 0.5));
}

float3 Clay(float3 n, float3 v, float3 albedo)
{
    float3 l   = normalize(-Light.xyz);
    float  ndl = saturate(dot(n, l));
    float3 sun = float3(1.00, 0.95, 0.88) * ndl * lerp(1.40, 1.05, Light.w);
    float3 fill = float3(0.20, 0.30, 0.45) * saturate(dot(n, normalize(float3(0.55, -0.75, 0.15)))) * lerp(0.35, 0.20, Light.w);
    return albedo * (Hemisphere(n) + sun + fill);
}

// Meshes out of Revit are not reliably wound, and ghost mode needs back faces: shade two-sided.
float3 FaceNormal(float3 n, float3 v) { n = normalize(n); return dot(n, v) < 0.0 ? -n : n; }

float4 PSClay(VSOut i) : SV_Target
{
    if (Clipped(i.wpos)) discard;
    float3 v = normalize(Eye.xyz - i.wpos);
    float3 n = FaceNormal(i.nrm, v);
    float3 c = Clay(n, v, Albedo.rgb);
    // Cool rim on dark; on light the rim darkens instead, so the silhouette separates from the sheet.
    c += pow(1.0 - saturate(dot(n, v)), 3.5) * lerp(float3(0.07, 0.16, 0.26), float3(-0.12, -0.10, -0.07), Light.w);
    return float4(Gamma(c), 1.0);
}

// Ghost walls: fresnel-weighted, premultiplied alpha, no depth write, so the interior stays readable.
float4 PSGhost(VSOut i) : SV_Target
{
    if (Clipped(i.wpos)) discard;
    float3 v = normalize(Eye.xyz - i.wpos);
    float3 n = FaceNormal(i.nrm, v);
    float  f = 1.0 - saturate(dot(n, v));
    // Blue fresnel glow on dark; on light the grazing faces go darker and cooler, so the volume still has an edge.
    float3 c = Clay(n, v, Albedo.rgb) * 0.85 + lerp(float3(0.06, 0.16, 0.27), float3(-0.10, -0.07, -0.02), Light.w) * pow(f, 1.7);
    float  a = saturate(Albedo.a * (0.30 + 0.70 * pow(f, 1.4)));
    return float4(Gamma(c) * a, a);
}

// Flat colour for openings and the ground grid; Eye.w > 0 fades it out with distance.

// ---------------------------------------------------------------- section box and handles

// Twelve wireframe edges of the simulation box, generated from BoxMin/BoxMax with no vertex buffer.
static const uint BoxEdge[24] = { 0,1, 2,3, 4,5, 6,7,  0,2, 1,3, 4,6, 5,7,  0,4, 1,5, 2,6, 3,7 };

VSOut VSBoxEdges(uint vid : SV_VertexID)
{
    uint c = BoxEdge[min(vid, 23u)];
    float3 p = float3((c & 1) ? BoxMax.x : BoxMin.x,
                      (c & 2) ? BoxMax.y : BoxMin.y,
                      (c & 4) ? BoxMax.z : BoxMin.z);
    VSOut o;
    o.pos = mul(ViewProj, float4(p, 1.0));
    o.wpos = p;
    o.nrm = float3(0, 0, 1);
    return o;
}

// A drag handle: one screen-aligned quad at Eye.xyz, Light.xy wide in NDC, so it never changes size.
VSOut VSHandle(uint vid : SV_VertexID)
{
    float2 corner = float2((vid == 1 || vid == 4 || vid == 5) ? 1.0 : -1.0,
                           (vid == 2 || vid == 4 || vid == 3) ? 1.0 : -1.0);
    float4 clip = mul(ViewProj, float4(Eye.xyz, 1.0));
    clip.xy += corner * Light.xy * max(clip.w, 0.001);
    VSOut o;
    o.pos = clip;
    o.wpos = Eye.xyz;
    o.nrm = float3(corner, 0);
    return o;
}

float4 PSHandle(VSOut i) : SV_Target
{
    float r = length(i.nrm.xy);
    if (r > 1.0) discard;
    float edge = smoothstep(1.0, 0.72, r);          // soft rim
    float core = smoothstep(0.62, 0.34, r);
    float3 rgb = lerp(Albedo.rgb * 0.35, Albedo.rgb, core);
    float a = Albedo.a * edge;
    return float4(Gamma(rgb) * a, a);
}

// ---------------------------------------------------------------- gizmos

// A transform gizmo laid out in pixels, so it never changes size with distance: Eye.xyz anchors it in the
// world, Light.xy is the pixel->NDC scale, Light.z the shape (0 arrow, 1 ring, 2 square), Light.w the stroke
// half-width. ClipMin.xy and ClipMax.xy are the shape's U and V pixel vectors: Gizmos.Layout computes them and
// the picker tests the very same numbers. ClipMin.z fattens the whole shape (the dark halo pass drawn under
// the stroke). ClipMax.z is the ring's knob angle or the square's fill alpha; ClipMax.w > 0 draws the knob.
static const float2 StrokeCorner[6] = { float2(0, -1), float2(0, 1), float2(1, -1), float2(1, -1), float2(0, 1), float2(1, 1) };

float2 Stroke(float2 a, float2 b, float w, uint k)         // one thick segment as two triangles, k in 0..5
{
    float2 d = b - a;
    float2 n = float2(-d.y, d.x) / max(length(d), 1e-4) * w;
    float2 q = StrokeCorner[k];
    return lerp(a, b, q.x) + n * q.y;
}

float2 Disc(float2 c, float r, uint k)                     // eight-triangle fan, k in 0..23
{
    uint tri = k / 3, v = k % 3;
    if (v == 0) return c;
    float a = (tri + (v == 1 ? 0.0 : 1.0)) * 0.7853982;
    return c + float2(cos(a), sin(a)) * r;
}

VSOut VSGizmo(uint vid : SV_VertexID)
{
    float2 U = ClipMin.xy, V = ClipMax.xy;
    float w = Light.w, grow = ClipMin.z;
    uint shape = (uint)(Light.z + 0.5);
    float2 p = float2(0, 0);
    float alpha = 1.0;
    if (shape == 0)                                        // double-headed arrow along U; Eye.w > 0 leaves the
    {                                                      // middle empty (a device gizmo), else a dot marks the anchor
        float L = length(U), gap = Eye.w;
        float2 u = L > 1e-4 ? U / L : float2(0, -1);
        float2 n = float2(-u.y, u.x);
        const float head = 10.0, headHalf = 5.0;
        bool full = L > head + gap + 4.0;                  // seen end-on the arrow has collapsed: dot only
        if (vid < 6)
            p = full ? Stroke(u * (gap > 0.0 ? gap : -(L - head)), u * (L - head), w, vid) : float2(0, 0);
        else if (vid < 12)
        {
            float s = vid < 9 ? 1.0 : -1.0;
            uint k = (vid - 6) % 3;
            float2 tip = u * (s * (L + grow)), base = u * (s * (L - head));
            p = k == 0 ? tip : base + n * ((headHalf + grow) * (k == 1 ? 1.0 : -1.0));
            if (!full) p = float2(0, 0);
        }
        else if (vid < 18)
            p = full && gap > 0.0 ? Stroke(-u * gap, -u * (L - head), w, vid - 12) : float2(0, 0);
        else
            p = gap > 0.0 ? float2(0, 0) : Disc(float2(0, 0), 3.0 + grow, vid - 18);
    }
    else if (shape == 1)                                   // ring, then the knob at the current angle
    {
        if (vid < 288)
        {
            uint seg = vid / 6, k = vid % 6;
            float a0 = seg * 0.1308997, a1 = (seg + 1) * 0.1308997;    // tau / 48
            p = Stroke(U * cos(a0) + V * sin(a0), U * cos(a1) + V * sin(a1), w, k);
        }
        else if (ClipMax.w > 0.5)
            p = Disc(U * cos(ClipMax.z) + V * sin(ClipMax.z), 4.0 + grow, vid - 288);
    }
    else                                                   // square: translucent plate, then stroked edges
    {
        float2 c0 = U + V, c1 = V - U, c2 = -U - V, c3 = U - V;
        if (vid < 6)
        {
            p = vid == 0 ? c0 : vid == 1 ? c1 : vid == 2 ? c3 : vid == 3 ? c3 : vid == 4 ? c1 : c2;
            alpha = ClipMax.z;
        }
        else
        {
            uint e = (vid - 6) / 6, k = (vid - 6) % 6;
            float2 a = e == 0 ? c0 : e == 1 ? c1 : e == 2 ? c2 : c3;
            float2 b = e == 0 ? c1 : e == 1 ? c2 : e == 2 ? c3 : c0;
            p = Stroke(a, b, w, k);
        }
    }
    float4 clip = mul(ViewProj, float4(Eye.xyz, 1.0));
    clip.xy += p * Light.xy * max(clip.w, 0.001);
    VSOut o;
    o.pos = clip;
    o.wpos = Eye.xyz;
    o.nrm = float3(alpha, 0, 0);
    return o;
}

float4 PSGizmo(VSOut i) : SV_Target
{
    float a = Albedo.a * i.nrm.x;
    return float4(Gamma(Albedo.rgb) * a, a);
}

float4 PSFlat(VSOut i) : SV_Target
{
    if (Clipped(i.wpos)) discard;
    float a = Albedo.a;
    if (Eye.w > 0.0)
    {
        float d = saturate(distance(Eye.xyz, i.wpos) / Eye.w);
        a *= (1.0 - d) * (1.0 - d);
    }
    return float4(Gamma(Albedo.rgb) * a, a);
}
""";
}
