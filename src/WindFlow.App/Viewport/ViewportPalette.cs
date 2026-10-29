using System.Numerics;
using Vortice.Mathematics;

namespace WindFlow.App.Viewport;

/// <summary>The viewport's design tokens, one set per Appearance. Surface and line colours are linear-light
/// (the pixel shaders gamma-encode on the way out); the background pair and the clear colour are already
/// sRGB-encoded, like the chrome tokens in App.axaml they sit next to. <see cref="Dark"/> is the original
/// palette, value for value; <see cref="Light"/> keeps every hue family (blue open, red shut, violet selection,
/// teal box, amber probe/handle) and moves the neutrals the other way so lines read dark on a light sheet.</summary>
public sealed class ViewportPalette
{
    /// <summary>0 dark, 1 light: handed to the shaders in Light.w (scene) and Theme (trails) for the few
    /// constants that live in HLSL (hemisphere ambient, rim and fresnel tints, the still-air lane colour).</summary>
    public float Theme;
    public Color4 Clear;
    public Vector4 BackgroundInner, BackgroundOuter;
    public Vector4 WallColor, GroundColor, GhostColor, EdgeClay, EdgeGhost;
    public Vector4 OpenFill, OpenLine, ShutFill, ShutLine;
    public Vector4 GridMinor, GridMajor, HoverFill, HoverLine, WindColor;
    public Vector4 XrayColor, EdgeXray, BoxColor, BandColor;
    public Vector4 HandleIdle, HandleHover, HandleActive;
    public Vector4 RoomLine, RoomHover, SpaceLine, DeletedLine;
    public Vector4 SelectFill, SelectLine, SelectQuad, RoomSelected, ProbeColor;
    public Vector4 DeviceColor, DeviceHover, DeviceSelectedColor, DeviceGhostColor, DeviceHalo, DeviceOffColor;
    // Outdoor air is context, not the subject: a low alpha ceiling keeps the eye inside the rooms.
    public float ParticleBrightness, ParticleOutdoorDim, ParticleAlpha;
    /// <summary>What the Avalonia speed legend multiplies Viridis by, so the bar shows the trail colours.</summary>
    public double LegendGain;

    public static ViewportPalette For(bool light) => light ? Light : Dark;

    public static readonly ViewportPalette Dark = new()
    {
        Theme = 0f,
        Clear = new Color4(0.039f, 0.051f, 0.071f, 1f),                              // #0A0D12
        BackgroundInner = new Vector4(0.058f, 0.077f, 0.107f, 1f),
        BackgroundOuter = new Vector4(0.020f, 0.027f, 0.039f, 1f),
        WallColor = Linear(0x99A5B4), GroundColor = Linear(0x333B46),
        GhostColor = Linear(0x9FC4DE, 0.5f),
        EdgeClay = Linear(0x0C1219, 0.45f), EdgeGhost = Linear(0xBFD9EC, 0.5f),
        OpenFill = Linear(0x4DB8FF, 0.32f), OpenLine = Linear(0x8FD6FF, 0.95f),
        ShutFill = Linear(0xC0453F, 0.24f), ShutLine = Linear(0xE06A62, 0.85f),
        GridMinor = Linear(0x243448, 0.6f), GridMajor = Linear(0x33506E, 0.8f),
        HoverFill = Linear(0x9BDCFF, 0.42f), HoverLine = Linear(0xEAF7FF, 1f),
        WindColor = Linear(0x4DB8FF, 0.75f),
        XrayColor = Linear(0x8FB4D0, 0.14f), EdgeXray = Linear(0xDCEBF7, 0.8f),
        BoxColor = Linear(0x8CFFE0, 1f), BandColor = Linear(0x8CFFE0, 0.22f),
        HandleIdle = Linear(0x9FE8D6, 0.85f), HandleHover = Linear(0xFFFFFF, 0.95f),
        HandleActive = Linear(0xFFD166, 1f),
        RoomLine = Linear(0x2C4055, 0.5f), RoomHover = Linear(0x7FD4FF, 0.85f),
        SpaceLine = Linear(0x7FD4FF, 0.55f), DeletedLine = Linear(0xFFB454, 0.95f),
        // Selection: one warm accent that nothing else in the scene uses (viridis particles, blue ghost walls and
        // openings, red shut openings, amber still air), so the picked room or opening reads at a glance.
        SelectFill = Linear(0xE08CFF, 0.30f), SelectLine = Linear(0xF2C6FF, 0.95f),
        SelectQuad = Linear(0xE08CFF, 0.55f),
        RoomSelected = Linear(0xFFFFFF, 0.95f), ProbeColor = Linear(0xFFD166, 0.95f),
        DeviceColor = Linear(0x7FD8F0, 0.85f), DeviceHover = Linear(0xBFEEFF, 0.95f),
        DeviceSelectedColor = Linear(0xFFFFFF, 0.98f), DeviceGhostColor = Linear(0x5AD19A, 0.98f),
        DeviceHalo = Linear(0x070B11, 0.82f),
        // A switched-off device: the same line art at ~30 % so it reads as "off", and no dark halo behind it.
        DeviceOffColor = Linear(0x7FD8F0, 0.28f),
        ParticleBrightness = 1.15f, ParticleOutdoorDim = 0.07f, ParticleAlpha = 0.52f,
        LegendGain = 1.0,
    };

    // Light: a paper-grey sheet (#F1F3F6 centre to #DCE0E6 rim), graphite lines, the same hue per meaning.
    // Trails: premultiplied "over" already; the dark set relies on brightness > 1 to glow, which on paper only
    // washes out, so the light set trades brightness for opacity (0.90 x, 0.80 alpha) and the legend follows.
    public static readonly ViewportPalette Light = new()
    {
        Theme = 1f,
        Clear = new Color4(0.863f, 0.878f, 0.902f, 1f),                              // #DCE0E6
        BackgroundInner = new Vector4(0.945f, 0.953f, 0.965f, 1f),
        BackgroundOuter = new Vector4(0.863f, 0.878f, 0.902f, 1f),
        WallColor = Linear(0xB9C1CB), GroundColor = Linear(0xC2C9D1),
        GhostColor = Linear(0x7FA3C0, 0.5f),
        EdgeClay = Linear(0x1F2733, 0.6f), EdgeGhost = Linear(0x2F4A66, 0.7f),
        OpenFill = Linear(0x1E90FF, 0.30f), OpenLine = Linear(0x0B6FD6, 0.95f),
        ShutFill = Linear(0xC0453F, 0.28f), ShutLine = Linear(0xB3302A, 0.90f),
        GridMinor = Linear(0x8A94A0, 0.42f), GridMajor = Linear(0x6B7684, 0.60f),
        HoverFill = Linear(0x5BB0FF, 0.40f), HoverLine = Linear(0x0B5FB8, 1f),
        WindColor = Linear(0x1C7ED6, 0.90f),
        XrayColor = Linear(0xC3D0DC, 0.18f), EdgeXray = Linear(0x33404F, 0.8f),
        BoxColor = Linear(0x0E8F76, 1f), BandColor = Linear(0x0E8F76, 0.25f),
        HandleIdle = Linear(0x12A58A, 0.90f), HandleHover = Linear(0x16202B, 0.95f),
        HandleActive = Linear(0xD98E00, 1f),
        RoomLine = Linear(0x7A8A9C, 0.55f), RoomHover = Linear(0x1C7ED6, 0.85f),
        SpaceLine = Linear(0x1C7ED6, 0.55f), DeletedLine = Linear(0xC46A00, 0.95f),
        SelectFill = Linear(0xE08CFF, 0.30f), SelectLine = Linear(0x9B3BD6, 0.95f),
        SelectQuad = Linear(0xE08CFF, 0.55f),
        RoomSelected = Linear(0x1A2230, 0.95f), ProbeColor = Linear(0xD98E00, 0.95f),
        DeviceColor = Linear(0x0F7FA3, 0.90f), DeviceHover = Linear(0x0A5F7A, 0.95f),
        DeviceSelectedColor = Linear(0x111B26, 0.98f), DeviceGhostColor = Linear(0x1F9A62, 0.98f),
        DeviceHalo = Linear(0xF4F6F8, 0.85f),
        DeviceOffColor = Linear(0x0F7FA3, 0.30f),
        ParticleBrightness = 0.90f, ParticleOutdoorDim = 0.07f, ParticleAlpha = 0.80f,
        LegendGain = 0.90,
    };

    static Vector4 Linear(uint rgb, float alpha = 1f)
    {
        static float S(float c) => c <= 0.04045f ? c / 12.92f : MathF.Pow((c + 0.055f) / 1.055f, 2.4f);
        return new Vector4(S(((rgb >> 16) & 0xFF) / 255f), S(((rgb >> 8) & 0xFF) / 255f), S((rgb & 0xFF) / 255f), alpha);
    }
}
