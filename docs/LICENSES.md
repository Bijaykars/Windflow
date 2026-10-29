# Licences

## Licences

Packages shipped in the app's publish output:

| Package | Licence |
|---|---|
| ILGPU | NCSA Open Source (permissive) |
| Avalonia (+ Avalonia.Desktop, Avalonia.Themes.Fluent, Avalonia.Skia, Avalonia.Win32, MicroCom) | MIT |
| Vortice.Direct3D12, Vortice.DXGI, Vortice.D3DCompiler, Vortice.MediaFoundation, Vortice.DirectX, Vortice.Mathematics, SharpGen.Runtime(.COM) | MIT |
| Velopack (+ `vpk` build tool) | MIT |
| PDFsharp | MIT |
| SkiaSharp, HarfBuzzSharp (managed) | MIT |
| libSkiaSharp.dll (native), av_libglesv2.dll / ANGLE (native) | BSD-3-Clause |
| libraries compiled into libSkiaSharp.dll: libpng, expat, libjpeg-turbo, zlib, Wuffs, SPIR-V | libpng / expat / IJG+BSD / zlib / Apache-2.0 (see [`THIRD-PARTY-NOTICES.txt`](../THIRD-PARTY-NOTICES.txt)) |
| Silk.NET.Assimp, Silk.NET.Core, Silk.NET.Maths | MIT |
| Ultz.Native.Assimp (Assimp64.dll, assimp 6.0.2) | BSD-3-Clause |
| libraries compiled into Assimp64.dll: poly2tri, Clipper, rapidjson, draco, Open3DGC, openddl-parser, zlib | BSD-3-Clause / Boost / MIT / Apache-2.0 / zlib |
| Microsoft.Extensions.Logging.Abstractions, Microsoft.Extensions.DependencyInjection.Abstractions, Microsoft.Extensions.DependencyModel, Microsoft.DotNet.PlatformAbstractions | MIT (Microsoft) |
| .NET 10 runtime (self-contained installer) | MIT (Microsoft), plus its third-party notice |
| Velopack `Update.exe` (Rust, installer/updater) | MIT (Velopack) plus its Rust crates (mostly MIT / Apache-2.0) |
| libHarfBuzzSharp.dll (native) | "Old MIT" |
| Tmds.DBus.Protocol | MIT |
| WPF (PresentationCore, PresentationFramework, WindowsBase) — from the .NET Windows Desktop runtime, used by `WindFlow.Viz`/`WindFlow.Report` | MIT (Microsoft) |

MP4 export calls the H.264 encoder built into Windows through Media Foundation; WindFlow does not ship a
video codec. Full text and copyright notices: [`THIRD-PARTY-NOTICES.txt`](../THIRD-PARTY-NOTICES.txt).

Not shipped: `WindFlow.Revit` links the Revit API from the local Revit install and never redistributes it.
`WindFlow.Viz` and `WindFlow.Report` are WindFlow's own code and ship inside the app.

[`THIRD-PARTY-NOTICES.txt`](../THIRD-PARTY-NOTICES.txt) is copied next to `WindFlow.App.exe` by the build and packaged by `pack.ps1`; the app's
About / licences dialog (Export menu) shows the version and opens it.

Licence check: `tools/licence-inventory.ps1` is a manual script (the build does not run it): it re-resolves every
shipped package's licence from the local NuGet cache and reports a copyleft/unknown licence or a gap in
[`THIRD-PARTY-NOTICES.txt`](../THIRD-PARTY-NOTICES.txt). It does not look inside the native DLLs or at the `Microsoft.*` packages.
