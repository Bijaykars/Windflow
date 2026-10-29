using System.Numerics;
using System.Text.Json;

namespace WindFlow.Core;

// The solver-facing contract. No Revit types live here or anywhere in WindFlow.Core.
// Units: metres, seconds, m/s, Pa, m3/s. Axes: the host model's axes (X, Y horizontal, Z up).
// Pipeline: BuildingModel -> Voxelizer.Build -> VoxelGrid -> LbmSolver.Solve -> FlowField -> Analysis.Analyze -> SimulationResult.

/// <summary>The whole building, so wind goes over the roof and around the façades the way it really does,
/// plus which storeys are analysed. Obstacles come from every storey; rooms and sources from the analysed ones.
/// Openings come from every storey too (Revit wall solids already have the holes cut), and are forced closed on
/// unanalysed storeys. The voxelizer seals off any air the outside cannot reach, so unanalysed storeys cost nothing.</summary>
public sealed class BuildingModel
{
    public string Name = "";
    /// <summary>Where TRUE north points in model axes, degrees clockwise from model +Y (Revit: the project's rotation to true
    /// north; a mesh drawn north-up and an old file read as 0). Wind bearings and EPW sectors are true bearings; the app adds this to
    /// get the model-axis wind (P13b-04 / S3-09).</summary>
    public float NorthDeg;
    public float GroundZ;                     // bottom of the air domain: the ground surface (lowest storey)
    public List<Storey> Storeys = new();      // ordered by elevation
    public List<Mesh> Obstacles = new();
    public List<Opening> Openings = new();
    public List<Room> Rooms = new();
    public List<AirSource> Sources = new();
    public Environment Env = new();
    public List<HeatSource> Heat = new();
    public List<ContaminantSource> Contaminants = new();
    /// <summary>What the exporter dropped or guessed (Revit: elements without geometry, terminals with an unreadable connector), as plain sentences
    /// (<see cref="ExtractionNotes"/>). The app lists them with the solver's notes (S4-06).</summary>
    public List<string> Warnings = new();

    static readonly JsonSerializerOptions Json = new()
    {
        IncludeFields = true,
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals,   // HeatSource.SurfaceTemperature = NaN
    };
    /// <summary>The storey a room sits on: <see cref="Room.Level"/> matched against <see cref="Storey.Id"/> first, then <see cref="Storey.Name"/>
    /// (the Revit extractor stores the name in Room.Level and the ElementId in Storey.Id, a mesh model stores the Id); null when neither matches (RX-01).</summary>
    public Storey? StoreyOf(Room r)
    {
        if (string.IsNullOrEmpty(r.Level)) return null;
        foreach (var s in Storeys) if (s.Id == r.Level) return s;
        foreach (var s in Storeys) if (s.Name == r.Level) return s;
        return null;
    }

    public string ToJson() => JsonSerializer.Serialize(this, Json);
    public static BuildingModel FromJson(string json) => JsonSerializer.Deserialize<BuildingModel>(json, Json)!;
}

public sealed class Storey
{
    public string Id = "", Name = "";
    public float Elevation, Height;           // Height = distance to the next storey (or the roof)
    public bool Analysed;
}

/// <summary>Triangle list of something that blocks air (wall, slab, roof, column, furniture). Need not be watertight:
/// every cell a triangle touches becomes solid.</summary>
public sealed class Mesh
{
    public string Id = "";
    /// <summary>Furniture or equipment (the Revit extractor's furniture categories): voxelized after the openings are carved, so a window's carve-through never clears it (RX-05).</summary>
    public bool Furniture;
    public Vector3[] Vertices = [];
    public int[] Indices = [];
}

public enum OpeningKind { Window, Door, Opening }

/// <summary>IMP-C: what one side of a detected opening faces, as the import's detection found it. Unknown = not detected (a drawn or older model): the ledger then classifies by room loops.</summary>
public enum OpeningSide { Unknown, Outdoors, Indoor, Room }

/// <summary>A rough opening in a wall, as an oriented box.
/// Open: cells inside are carved to air with depth Depth + 2 cells, so it cuts cleanly through the wall.
/// Closed: cells inside are filled solid with depth max(Depth, 1 cell).</summary>
public sealed class Opening
{
    public string Id = "";      // stable key (Revit UniqueId)
    public string Name = "";    // label shown to the user
    public string Level = "";
    public OpeningKind Kind;
    public bool IsOpen = true;
    public Vector3 Center;      // centre of the rough opening, mid-wall
    public Vector3 Normal;      // horizontal unit vector through the wall (pointing outside when known)
    public float Width;         // horizontal, along the wall
    public float Height;        // vertical
    public float Depth;         // host wall thickness
    /// <summary>IMP-C: the detection's result for the two sides of the wall (A the low-coordinate side, B the high one along the wall's normal axis): outdoor air, indoor air in no detected room (a hall, a stair void), or a room.
    /// Both known = the ledger and the exterior classification use them (<see cref="Analysis.IsExteriorOpening(BuildingModel, Opening, float)"/>); Unknown = by room loops.</summary>
    public OpeningSide SideA, SideB;
    /// <summary>Discharge coefficient the calibrated opening model (<see cref="RunOptions.OpeningModel"/>) aims
    /// for; null = 0.61, a sharp-edged opening. Ignored by <see cref="OpeningModel.Resolved"/>.</summary>
    public float? DischargeCoefficient;
    /// <summary>RP-06: the host wall's thickness along <see cref="Normal"/> at the centre, m, as the voxeliser measured it from the geometry (Voxelizer.ComputeWallLinks);
    /// 0 = none found, and <see cref="Depth"/> is used. Runtime only: internal, so never saved.</summary>
    internal float MeasuredDepth;
}

/// <summary>Room footprint for statistics only (walls come from Obstacles). Loops use even-odd filling.</summary>
public sealed class Room
{
    public string Id = "", Name = "", Level = "";
    public List<Vector2[]> Loops = new();
    public float ZMin, ZMax;
    public int Occupants;       // people in the room: CO2 (0.0052 L/s each) and 70 W each, spread over the occupied zone
    /// <summary>True when the footprint was traced from the voxel grid itself (mesh room detection), so it carries no
    /// nominal volume of its own and ACH falls back to the voxel air volume. Rooms whose Id starts with "det-" (what
    /// RoomDetect assigns) are treated the same way, so a detector that does not set this still gets the fallback.</summary>
    public bool VoxelDerived;
    /// <summary>m3 of the whole detected room, wall face to wall face: the un-eroded outline's area x storey height
    /// (RoomDetect). <see cref="Loops"/> is that outline inset one cell, which is the right cell mask for the transport
    /// averages but loses a band of the room, so ACH divides by this instead. 0 = not known (not a detected room).</summary>
    public float DetectedVolume;
}

public enum SourceKind { Supply, Return, Fan }

/// <summary>How an <see cref="AirSource"/> is realised in the solver.
/// <para><see cref="Patch"/> is the original behaviour and the default: a rectangular patch of cells whose
/// velocity is IMPOSED (VoxelGrid.Fixed). It is simple and exact at the patch, but it injects or removes mass
/// there, which shows up in the mass ledger.</para>
/// <para>Every other kind is a real device: a region of ordinary AIR cells carrying a Guo body force that
/// accelerates the air already there toward a target velocity, so the device moves air without creating it
/// (mass conserved by construction). See <see cref="DeviceRegion"/> and LbmSolver's device forcing.</para></summary>
public enum DeviceKind
{
    /// <summary>Fixed-velocity patch (original behaviour, default).</summary>
    Patch,
    /// <summary>Actuator disc of <see cref="AirSource.Radius"/>, axis = Direction, with tangential swirl.</summary>
    CeilingFan,
    /// <summary>Same actuator disc, typically horizontal axis, no swirl by default.</summary>
    PedestalFan,
    /// <summary>Wall-mounted indoor unit: a thin jet tilted <see cref="AirSource.LouvreAngle"/> below the
    /// horizontal plus a linked return that pulls the same volume flow.</summary>
    SplitAc,
    /// <summary>Ceiling cassette: a four-way perimeter discharge around a central return.</summary>
    CassetteAc,
    /// <summary>Extract fan: drives air along Direction through an opening to the outside.</summary>
    ExhaustFan,
    /// <summary>Extract hood: an exhaust fan with a deeper capture region (see <see cref="AirSource.Radius"/>).</summary>
    Hood,
    /// <summary>Supply diffuser: a patch whose jet spreads by <see cref="AirSource.LouvreAngle"/> at the rim.</summary>
    Diffuser,
}

/// <summary>Mechanical air source. <see cref="Device"/> = <see cref="DeviceKind.Patch"/> (the default) keeps
/// the original meaning: a rectangular patch through which air is forced at a fixed velocity.
/// Supply: air leaves the patch along Direction. Return/exhaust: air enters the grille, so Direction points into it.
/// Fan: a free-standing patch inside the room; air passes through it along Direction.
/// <para>Any other <see cref="Device"/> turns the source into a body-force device (see <see cref="DeviceKind"/>);
/// the extra fields below are read only by the kinds named on each one, so old models and old JSON keep
/// working unchanged.</para></summary>
public sealed class AirSource
{
    public string Id = "", Name = "", Level = "";
    public SourceKind Kind;
    public bool Enabled = true;
    public Vector3 Center;      // centre of the patch
    public Vector3 Direction;   // unit vector, the direction the air moves
    public float Width, Height; // patch size in the plane perpendicular to Direction
    public float FlowRate;      // m3/s, >= 0; face velocity = FlowRate / (Width * Height)

    // ---- device fields (ignored when Device == Patch) ----

    /// <summary>Which real device this source represents. Patch = the fixed-velocity patch above.
    /// <para><see cref="Kind"/> still decides what the device does to the building's air balance, and it
    /// must match the hardware: Supply and Return mean outdoor air crosses the envelope here (an extract
    /// fan in a wall is a Return), while Fan means the device recirculates air already in the space (a
    /// ceiling fan, a split AC's indoor unit) and is left out of the mass ledger.</para></summary>
    public DeviceKind Device;

    /// <summary>Fans: actuator-disc radius in metres (blade tip). 0 = half of max(Width, Height).
    /// Hood: capture depth below the hood face (0 = 0.3 m).</summary>
    public float Radius;

    /// <summary>Fans: tangential (swirl) velocity as a fraction of the local axial velocity, 0..1.
    /// A ceiling fan is ~0.3; a pedestal fan with a straightening guard is nearer 0.1.</summary>
    public float Swirl = 0.3f;

    /// <summary>SplitAc/CassetteAc: supply air temperature in °C. NaN (the default) = no temperature effect.
    /// Only used when <see cref="RunOptions.Thermal"/> is on.</summary>
    public float SupplyTemperature = float.NaN;

    /// <summary>SplitAc/CassetteAc: centre of the linked return (intake) grille. NaN (the default) places it
    /// automatically - on the wall behind a split unit, max(0.5 m, 2 cells) above its outlet centre, or as high as
    /// the ceiling allows (a warning is raised when outlet and return are under 0.4 m apart) (the intake on the
    /// top face of the unit), in the middle of the discharge ring for a cassette. The return temperature is
    /// read one cell into the room in front of the grille, outside the outlet jet; a user-placed return
    /// inside that jet gets a Warning. The return pulls the same volume flow the outlet pushes, so the pair moves no net mass.</summary>
    public Vector3 ReturnCenter = new(float.NaN, float.NaN, float.NaN);

    /// <summary>Size of the linked return grille, metres, in the plane perpendicular to Direction.
    /// &lt;= 0 (the default) = the same size as the outlet patch.</summary>
    public float ReturnWidth, ReturnHeight;

    /// <summary>SplitAc/CassetteAc: outlet tilt below the horizontal, degrees (louvre setting), clamped to
    /// 0..75. Diffuser: half the spread angle of the jet at the rim of the patch.</summary>
    public float LouvreAngle = 30f;

    /// <summary>Optional thrust in newtons. &gt; 0 overrides <see cref="FlowRate"/> as the scale of a fan's
    /// actuator disc: the mean axial speed becomes sqrt(Thrust / (rho * disc area)). 0 = derive everything
    /// from FlowRate and the device's own area (the normal case).</summary>
    public float Thrust;

    /// <summary>Isotropic inlet turbulence intensity Tu of this supply's face (rms fraction of the face velocity), read
    /// per link-wise supply patch when <see cref="RunOptions.SupplyTuPerSource"/> is on (HighAccuracy). null = none.
    /// The global <see cref="RunOptions.SupplyPerturbation"/> overrides it. Defaults by supply type:
    /// <see cref="HighAccuracy.DefaultSupplyTu"/>.</summary>
    public float? TurbulenceIntensity;
}

// ---- Advanced inputs (all optional; Basic mode leaves them at defaults) ----

public sealed class Environment
{
    public float OutdoorTemperature = 20;   // °C, also the far-field air temperature
    public float IndoorTemperature = 22;    // °C, initial indoor air temperature, and the set point a held run keeps each room at (RunOptions.HoldIndoorTemperature)
    public float RelativeHumidity = 50;     // %, used by comfort only
    public float OutdoorCo2 = 420;          // ppm, far-field and initial concentration
}

public enum HeatKind { People, Equipment, Lighting, Surface }

/// <summary>How Surface HeatSource faces exchange heat (<see cref="RunOptions.SurfaceExchange"/>). Correlation (the product case: room-scale
/// grids do not resolve the thermal boundary layer): a convection correlation applied as a source in the first fluid cell (6e, P14-03).
/// Resolved: the face is a fixed-temperature wall (D3Q7 anti-bounce-back, Hybrid half-cell conduction), the pre-6e behaviour, for benchmarks
/// whose grid resolves the boundary layer (the heated cavity).</summary>
public enum SurfaceExchange { Correlation, Resolved }

/// <summary>Sensible heat released inside an axis-aligned box (people cluster, equipment, luminaires) or a surface
/// held at a fixed temperature (sunlit glazing, chilled ceiling). Surface: the faces whose solid cell centre (P14-02), or for a wall
/// thinner than a cell whose cut-link crossing (RY-07), lies inside the box are at SurfaceTemperature and exchange heat with the next
/// air cell by a convection correlation (6e, P14-03: Alamdari-Hammond natural convection blended with a forced term, see
/// LbmSolver.SurfaceConvection), so the box must reach into the wall/slab; other walls are adiabatic. Other kinds: Watts spread evenly
/// over the box's air cells.</summary>
public sealed class HeatSource
{
    public string Id = "", Name = "", Level = "";
    public HeatKind Kind;
    public bool Enabled = true;
    public Vector3 Center;
    public float Width = 1, Depth = 1, Height = 1;   // metres, box centred on Center (Width along X, Depth along Y)
    public float Watts;                              // sensible heat; People: 70 W each if 0
    public int People;                               // People kind: count, also a CO2 source (0.0052 L/s each)
    public float SurfaceTemperature = float.NaN;     // Surface kind: °C
}

/// <summary>Point release of a tracer (cough, spill, smoke test) with an arbitrary unit rate; results are relative.</summary>
public sealed class ContaminantSource
{
    public string Id = "", Name = "", Level = "";
    public bool Enabled = true;
    public Vector3 Center;
    public float Rate = 1;      // units/s
}

public enum Backend { Auto, Cpu, Gpu }
/// <summary>Refine is what the app calls Accurate. HighAccuracy is the validated-pieces mode (<see cref="HighAccuracy"/>):
/// it does not change a run by itself; <see cref="HighAccuracy.Configure"/> fills the RunOptions.</summary>
public enum Quality { Preview, Refine, HighAccuracy }
/// <summary>The automatic wind domain (6c, P13b-03). Compact: margins tied to the plan size (about 10 % blockage on a house). Full: COST 732 /
/// Franke 2007 margins tied to the building height for the run's wind direction, blockage at most 3 %, stepped down to fit graphics memory.</summary>
public enum DomainMode { Compact, Full }

/// <summary>An axis-aligned box in model metres: the simulation domain a user drew (a section box), used as
/// <see cref="RunOptions.Domain"/> instead of the automatic building-plus-wind-tunnel envelope.</summary>
public readonly record struct DomainBox(Vector3 Min, Vector3 Max)
{
    public Vector3 Size => Max - Min;

    /// <summary>At least a metre on every axis: below that there is nothing a building-scale lattice
    /// (Dx >= 0.1 m) can resolve, and Voxelizer.Build ignores the box.</summary>
    public bool IsValid => Size.X >= 1f && Size.Y >= 1f && Size.Z >= 1f;
}

public sealed class RunOptions
{
    /// <summary>A shallow copy (every field; the reference-typed ones are shared), for a caller that layers a recipe on top.</summary>
    public RunOptions Clone() => (RunOptions)MemberwiseClone();

    public Quality Quality = Quality.Preview;
    public Backend Backend = Backend.Auto;
    /// <summary>P17-07: set with <see cref="Backend"/> = Cpu when a run was restarted on the CPU after a GPU failure; the session puts it in the grid's notes.</summary>
    public string? GpuLossNote;
    /// <summary>RU-12: called by <see cref="Simulation.Run"/> after a GPU failure switched this run to the CPU, before it restarts: the caller re-plans the cell budget
    /// (<see cref="TargetCells"/> and what hangs on it) for the CPU tier. Null = the run keeps its budget.</summary>
    public Action<RunOptions>? CpuReplan;
    public float WindProfileExponent = 0.22f;   // power-law wind profile u(z) = U * (z / 10 m)^a, suburban terrain; 0 = uniform
    public int? TargetCells;                    // overrides the quality preset

    // Advanced
    public bool Thermal;                        // temperature lattice + buoyancy; needs Env + Heat sources to matter

    /// <summary>How temperature is solved on a <see cref="Thermal"/> run. Hybrid (default): the D3Q19 LBM keeps the flow and T is a
    /// conservative finite-volume energy equation with TVD (van Leer MUSCL) advection and no diffusivity floor (Lallemand and Luo 2003,
    /// JCP 184:406; hybrid recursive-regularized LBM + FV energy equation, Feng, Boivin, Sagaut et al.); CPU and GPU backends. Lbm: the
    /// D3Q7 thermal lattice (its diffusivity floor over-mixes heated-room stratification). The default can be set from the environment
    /// (WINDFLOW_THERMAL_SCHEME=lbm) so harnesses that build their own RunOptions can switch it.</summary>
    public ThermalScheme ThermalScheme =
        string.Equals(System.Environment.GetEnvironmentVariable("WINDFLOW_THERMAL_SCHEME"), "lbm", StringComparison.OrdinalIgnoreCase)
            ? ThermalScheme.Lbm : ThermalScheme.Hybrid;
    /// <summary>6j (P14-01): on a <see cref="Thermal"/> run, each room with an outline gets an ideal heater/cooler that holds its room-VOLUME
    /// mean air temperature (S2; the occupied-zone mean in the Temp column can differ in a stratified room) at
    /// <see cref="Environment.IndoorTemperature"/>; its time-mean duty is RoomResult.HoldDutyWatts. A room with a conditioning device (one with
    /// its own supply temperature) is not held: the device conditions it. False = free-running (the indoor temperature only seeds the start).</summary>
    public bool HoldIndoorTemperature = true;
    /// <summary>6e addendum: how Surface HeatSource faces exchange heat, see <see cref="WindFlow.Core.SurfaceExchange"/>.</summary>
    public SurfaceExchange SurfaceExchange = SurfaceExchange.Correlation;
    public bool AirQuality;                     // CO2, contaminants and mean age of air on the final flow field
    public bool AgeOfAir;                       // mean age of air only (the fresh-air ACH of each room); AirQuality implies it
    /// <summary>6k (P14-06): the session also solves the age of air as a transient scalar on its own lattice (LbmSession.StartOnlineAge; GPU and
    /// CPU), which <see cref="AveragedPass"/> starts at the end of its settle. It only reserves the per-step eddy-viscosity plane here.</summary>
    public bool OnlineAge;
    /// <summary>Standard: with <see cref="OnlineAge"/>, <see cref="AveragedPass"/> lands the online age only in the rooms where it settled and the steady age in the rest (marked). Off: the online age lands everywhere (High).</summary>
    public bool OnlineAgePerRoom;
    public bool Comfort;                        // PMV/PPD/draught rate; implies Thermal
    public int SnapshotCount;                   // 0 = none; else this many velocity(+T) snapshots over the averaging phase
    public float Clothing = 0.6f;               // clo, comfort
    public float MetabolicRate = 1.1f;          // met, comfort

    /// <summary>Restricts the air domain to this box instead of the building plus its automatic wind-tunnel
    /// margin. The whole cell budget then lands inside the box, so Dx falls (and the resolution rises) as the
    /// box shrinks; cells outside it do not exist, and rooms/openings outside it drop out of the analysis
    /// (a room with no air cells is skipped with a Warning).
    /// <para>LIMITATION (v1): the box faces are ordinary domain boundaries and keep the usual ring classes
    /// (inflow / outflow / free-slip by face). A face cutting through indoor air therefore behaves like an
    /// open or free-slip window onto nothing rather than like the rest of the building, so put the faces in
    /// open air, or outside the walls of the part being studied. One-way nesting -- driving the box faces
    /// from a coarse whole-building run -- is the upgrade path.</para></summary>
    public DomainBox? Domain;

    /// <summary>6c (P13b-03): the size of the automatic wind domain when no <see cref="Domain"/> box is given. Compact (the default) is the
    /// plan-sized wind tunnel as before; Full is the COST 732 domain at the same Dx (see <see cref="Voxelizer.PlanFull"/>).</summary>
    public DomainMode DomainMode = DomainMode.Compact;

    /// <summary>Lowers the 0.1 m cell-size floor (Voxelizer's Dx clamp) so a RunOptions.Domain box can be
    /// resolved finer than the default building-scale floor. Null = today's 0.1 m floor unchanged. A value
    /// above 0.1 m is clamped down to 0.1 m -- this can only make the floor finer, never coarser.</summary>
    public float? MinDx;
    /// <summary>Ultra quality (null = not an Ultra run): checkpoints, a long cap, raised step ceilings, no CPU fallback. See <see cref="Ultra"/>.</summary>
    public UltraSettings? Ultra;

    // Caps the worker count of the Parallel.For loops in LbmSession.ToField, Analysis.Analyze and
    // Voxelizer.Build (null = the framework default, i.e. every core). An interactive host that runs a
    // solve alongside its own render loop leaves a core or two free with this; it does not change any
    // result, only how many threads the pass saturates.
    public int? MaxDegreeOfParallelism;

    /// <summary>PLAN_V2 2.2 / 6f: run <see cref="AveragedPass"/> (the app's Accurate pass: settle, batches until
    /// <see cref="ConvergenceMonitor"/> says every analysed room's ACH batch-mean has settled or <see cref="WallCapSeconds"/> ends it, then the age
    /// pass) instead of the fixed warm-up + averaging step count. Default off, so every existing caller keeps its exact step count and check numbers.</summary>
    public bool AutoConverge;

    /// <summary>Preview-to-Refine warm start (independent of <see cref="AutoConverge"/>): seeds the run's
    /// initial populations at equilibrium for this (typically coarser) field's time-averaged velocity,
    /// trilinearly interpolated onto the new grid, instead of rest. Isothermal only (matches
    /// <see cref="LbmNested"/>'s own precedent); ignored on a thermal run. Null = cold start, unchanged.</summary>
    public FlowField? WarmStart;

    /// <summary>How window/door openings discharge, see <see cref="WindFlow.Core.OpeningModel"/>. Calibrated by default:
    /// on the Tominaga-Blocken cross-ventilation case it takes Q/(UH A) from 0.552 to 0.463 (opening-flux references
    /// 0.40-0.47) and halves the velocity sign errors (27 -> 15 of 63 points), 2026-09-28.</summary>
    public OpeningModel OpeningModel = OpeningModel.Calibrated;

    /// <summary>PLAN_V2 1.4b: the BGK stability floor on lattice viscosity. The effective Re at a supply or opening
    /// is about u_lat * cells / floor, so the floor, not the collision operator, decides whether a jet goes turbulent
    /// (scratch jet study: at 1e-4 the round-jet centreline decay is near the classic law; at 5e-4 the jet stays
    /// laminar to ~13 D). The calibrated opening model's Cd_solver table is keyed on this value. The default can be
    /// set from the environment (WINDFLOW_NU_FLOOR) so harnesses that build their own RunOptions can sweep it.</summary>
    public float ViscosityFloor = EnvFloat("WINDFLOW_NU_FLOOR", 5e-4f);

    /// <summary>PLAN_V2 1.4b: rms amplitude of a seeded velocity perturbation on link-wise Supply faces, as a
    /// fraction of the face velocity (0 = off; 0.02-0.05 intended). Defined as the isotropic turbulence intensity
    /// Tu = sqrt((u'^2 + v'^2 + w'^2) / 3) / U: the axial speed and each of two tangential wall-velocity components (independent
    /// streams, same correlation) carry rms Tu x U. Mass-neutral over each face, correlated over
    /// about the patch's D/U, host-side so CPU and GPU see the same sequence. Default from WINDFLOW_SUPPLY_PERTURB.</summary>
    public float SupplyPerturbation = EnvFloat("WINDFLOW_SUPPLY_PERTURB", 0f);

    /// <summary>Each link-wise supply uses its own <see cref="AirSource.TurbulenceIntensity"/> (null = 0). Off by default, so
    /// Preview and Accurate are unchanged; HighAccuracy turns it on. A non-zero <see cref="SupplyPerturbation"/> overrides it.</summary>
    public bool SupplyTuPerSource;

    /// <summary>Wall-time cap of a <see cref="AutoConverge"/> run, seconds (0 = none, the default). A run that hits it
    /// stops with <see cref="ConvergenceStopReason.Capped"/> and every room that had not settled is reported as such.</summary>
    public double WallCapSeconds;

    /// <summary>Flow-throughs (longest analysed room's V/Q) of warm-up after the session's own fixed warm-up in a cold
    /// <see cref="AutoConverge"/> run. 1 = the original behaviour; HighAccuracy uses 2.</summary>
    public int WarmupFlowThroughs = 1;

    /// <summary>A 2:1 fine box (<see cref="LbmSolver.StartNested"/>, driving the coarse session) around one focus room, used by an
    /// <see cref="AutoConverge"/> run: the rooms wholly inside it are analysed on the fine field, the others on the coarse one.
    /// Null = none. Isothermal only (a thermal run ignores it). HighAccuracy fills it from <see cref="HighAccuracy.Plan"/>.</summary>
    public DomainBox? FocusBox;

    /// <summary>Seed of <see cref="SupplyPerturbation"/>.</summary>
    public int PerturbationSeed = 1;

    /// <summary>Measurement seam (accuracy step 1): n &gt; 0 adds a deterministic random perturbation, each component uniform in +-1e-3 x ULat (lattice units),
    /// to every Air cell's initial velocity of a cold isothermal start (System.Random(n), cell-index order, same on CPU and GPU). 0 = off, the default.
    /// Default from WINDFLOW_IC_SEED.</summary>
    public int IcSeed = EnvInt("WINDFLOW_IC_SEED", 0);

    /// <summary>Equilibrium wall function (Werner-Wengle) imposed as a tangential slip velocity through the moving-wall term
    /// of the Bouzidi bounce-back (Han, Ooka and Kikumoto 2021). None = plain no-slip, the default; the environment variable
    /// WINDFLOW_WALL_MODEL=ww turns it on for harnesses that build their own RunOptions. Isothermal runs only.</summary>
    public WallModel WallModel =
        string.Equals(System.Environment.GetEnvironmentVariable("WINDFLOW_WALL_MODEL"), "ww", StringComparison.OrdinalIgnoreCase)
            ? WallModel.WernerWengle : WallModel.None;

    /// <summary>Sandbox test hooks (WM2/WM3): a constant body force along +x in lattice units, and x-periodic ghost columns
    /// (period Nx - 2). Either one routes the step through the wall-model kernel, with the model itself off when
    /// <see cref="WallModel"/> is None.</summary>
    internal float TestBodyForceX;
    internal bool TestPeriodicX;

    /// <summary>Synthetic approach-flow turbulence on the wind inflow ring (random Fourier modes, seeded by
    /// <see cref="PerturbationSeed"/>); null = off, the default.</summary>
    public WindTurbulence? WindTurbulence;

    static int EnvInt(string name, int fallback) =>
        int.TryParse(System.Environment.GetEnvironmentVariable(name), System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out var v) && v >= 0 ? v : fallback;

    static float EnvFloat(string name, float fallback) =>
        float.TryParse(System.Environment.GetEnvironmentVariable(name), System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var v) && v >= 0f ? v : fallback;
}

/// <summary>PLAN_V2 1.4b. <see cref="Resolved"/>: an opening is only its carved hole, so its discharge is whatever the
/// lattice resolves -- Cd 0.71-0.75 at 4-16 cells across for a sharp square orifice (vs ~0.61 measured), because the
/// jet barely contracts at that resolution. <see cref="Calibrated"/>: a sub-grid Forchheimer loss in the opening's
/// throat cells adds the pressure loss the lattice misses, K = 1/Cd_target^2 - 1/Cd_solver(n)^2, so the opening
/// discharges at <see cref="Opening.DischargeCoefficient"/> (default 0.61).</summary>
public enum OpeningModel { Resolved, Calibrated }

/// <summary>Temperature solver of <see cref="RunOptions.ThermalScheme"/>.</summary>
public enum ThermalScheme { Lbm, Hybrid }

/// <summary>Wall treatment of <see cref="RunOptions.WallModel"/>.</summary>
public enum WallModel { None, WernerWengle }

/// <summary>Uniform cubic cells. Index = x + Nx * (y + Ny * z). Layer z = 0 is the ground (solid), so
/// Origin.Z = BuildingModel.GroundZ - Dx and the first air layer starts at the ground surface.</summary>
public sealed class VoxelGrid
{
    public const byte Air = 0, Solid = 1, Fixed = 2;   // Fixed: air whose velocity is imposed (source patches)
    public int Nx, Ny, Nz;
    public float Dx;
    public Vector3 Origin;                              // min corner of cell (0,0,0)
    public byte[] Cell = [];
    /// <summary>Optional distances for analytical hand-built grids whose fluid/solid centres are known
    /// to straddle a surface. Geometry-built grids use actual WallLinks instead of inferring signs.</summary>
    public float[] Distance = [];
    /// <summary>Final-geometry wall intersections in pull-link order q*Count+cell. Zero means unknown
    /// (halfway bounce-back); bytes 1..255 encode fractions (b-1)/254. Empty on analytical hand-built grids.
    /// 6a: a nonzero byte toward a neighbour that is not Solid is a cut link (a wall thinner than a cell between two fluid cells):
    /// the LBM bounces there too; zero toward such a neighbour is a free link.</summary>
    public byte[] WallLinks = [];
    /// <summary>6a (P12-01): cells that are Solid in <see cref="Cell"/> only as the end of a cut link nearer its crossing (a wall thinner than
    /// a cell, a link clipping a corner). Cell is the conservative mask every non-LBM consumer reads; the LBM solvers stream these cells as
    /// air and bounce off the cut link instead (<see cref="WallLinks"/>), see <see cref="LbmView"/>. Empty when there are none.</summary>
    public bool[] LbmAir = [];
    /// <summary>The grid as the LBM-family solvers see it: <see cref="Cell"/> with every still-Solid <see cref="LbmAir"/> cell as Air (the
    /// cell-centre mask). This grid itself when it has no such cells; otherwise a copy sharing every other field.</summary>
    internal VoxelGrid LbmView()
    {
        if (LbmAir.Length != Count) return this;
        var view = (VoxelGrid)MemberwiseClone();
        view.Cell = (byte[])Cell.Clone();
        for (int i = 0; i < view.Cell.Length; i++) if (LbmAir[i] && view.Cell[i] == Solid) view.Cell[i] = Air;
        return view;
    }
    /// <summary>Standalone domain boxes may exchange through Z faces. Whole-building and nested grids
    /// retain their own ground/lid or coarse-driven conditions.</summary>
    public bool OpenZMin, OpenZMax;
    public List<FixedCell> FixedCells = new();          // one entry per Fixed cell
    public List<DeviceRegion> Devices = new();          // one entry per body-force device (AirSource.Device != Patch)
    public List<string> Warnings = new();               // e.g. a source patch that could not be placed in air
    /// <summary>RW-01: the flow, m3/s, a Supply or Return patch actually runs at, by index in BuildingModel.Sources, where it differs from
    /// the source's set FlowRate: a sealed room's extract is scaled to its supply (Voxelizer.BalanceSealedPatches), 0 when the room has only one
    /// of the two. Every reader of a patch's target (the LBM flux controller, the age solve) goes through <see cref="PatchFlow"/>.</summary>
    public Dictionary<int, float> PatchFlowOverride = new();
    /// <summary>RW-05: Diffuser sources (index in BuildingModel.Sources) in a sealed room; they stir the room's air but bring in no outdoor air.</summary>
    public HashSet<int> NoFreshAir = new();
    public float PatchFlow(int sourceIndex, float setFlow) => PatchFlowOverride.TryGetValue(sourceIndex, out var f) ? f : setFlow;
    /// <summary>Plan width, plan depth and height (m) of the building inside the automatic wind-tunnel domain, for the blockage note
    /// (<see cref="Envelope.Blockage"/>); zero for a hand-built grid or a user simulation box.</summary>
    public Vector3 BuildingSize;
    public int Count => Nx * Ny * Nz;
    public int Index(int x, int y, int z) => x + Nx * (y + Ny * z);
    public bool IsSolid(int i) => Cell[i] == Solid;
    internal VoxelGrid ForAnalysis()
    {
        var copy = (VoxelGrid)MemberwiseClone();
        copy.Warnings = new List<string>(Warnings);
        return copy;
    }

    public Vector3 CellCenter(int x, int y, int z) => Origin + new Vector3(x + 0.5f, y + 0.5f, z + 0.5f) * Dx;
}

/// <summary>One Fixed (imposed-velocity) cell. <paramref name="Supply"/> is the index in BuildingModel.Sources of the
/// Supply patch that owns it: the solver treats such a cell as a link-wise velocity inlet (moving-wall bounce-back
/// on the links into the room, flux-controlled to the source's FlowRate). -1 (Return/Fan patches, hand-built
/// grids) keeps the equilibrium reset: the cell is rewritten to feq(rho, Velocity) every step. Since batch 3b Return and Fan
/// patches are link-wise and flux-controlled too (<paramref name="Supply"/> is then their source index); <paramref name="Mode"/> is
/// 0 for a Supply (air enters the room), 1 for a Return (a sink: the links carry air out), 2 for a Fan (through-flow: both sides).</summary>
public readonly record struct FixedCell(int Index, Vector3 Velocity, int Supply = -1, byte Mode = 0);

/// <summary>The cells one body-force device drives, with the velocity it drives them toward. The cells stay
/// ordinary AIR (they stream and collide like any other cell); the solver only adds a force density
/// f = rho * (Target - u) / tau_relax there, so the device pushes the air that is already present and creates
/// none. Built by <see cref="Voxelizer.ApplyDevices"/>, consumed by LbmSolver.
/// <para>Cells[0, OutletCount) are the driven outlet/discharge cells; Cells[OutletCount, ..) are the linked
/// return (intake) cells of an AC unit, which pull the same volume flow in - the pair therefore moves no net
/// mass. A fan or an exhaust has no return cells.</para></summary>
public sealed class DeviceRegion
{
    public string SourceId = "", SourceName = "";
    public int SourceIndex = -1;        // position in BuildingModel.Sources, for matching without relying on Id
    public DeviceKind Kind;
    public int[] Cells = [];            // air-cell indices
    public Vector3[] Target = [];       // m/s, per cell, same order as Cells
    public int OutletCount;             // Cells[0, OutletCount) = outlet; the rest = linked return
    public int OutletLayers = 1;        // cell layers the outlet is thick along its axis (flux = sum / layers)
    /// <summary>Outlet face normal (unit) that <see cref="FlowRate"/> is defined across; MeasureDevices takes the flux
    /// through that face (P13a-01). Zero (the cassette, whose "face" is the band's plan area) = the cell's own jet direction.</summary>
    public Vector3 Normal;
    public float FlowRate;              // m3/s the device is set to move
    public float SupplyTemperature = float.NaN;   // °C at the outlet cells (AC units, thermal runs only)
    /// <summary>AC units: the cells whose temperature is the unit's return temperature -- one cell upstream
    /// (room side) of each return cell, minus the outlet and the first cells of its jet, so the unit meters
    /// the room air it draws in rather than its own cold discharge. Empty = fall back to the return cells
    /// (or, with no return, the outlet cells).</summary>
    public int[] GatherCells = [];
    /// <summary>Fans: metres of clear air on the intake side of the disc, as far as the check looked (NaN
    /// for other kinds), and whether that is short of what the intake needs. A short intake is only reported
    /// (as "choked") when the run also shows the fan fell well short of its set flow.</summary>
    public float IntakeClearance = float.NaN;
    public bool IntakeShort;
}

/// <summary>Time-averaged solver output on the grid cells.</summary>
public sealed class FlowField
{
    public VoxelGrid Grid = new();
    public float[] Ux = [], Uy = [], Uz = [];   // m/s, 0 in solids, the imposed velocity in Fixed cells
    public float[] P = [];                      // Pa relative to the far field, 0 in solids
    public float[] T = [];                      // °C, empty unless RunOptions.Thermal
    public float[] Tu = [];                     // turbulence intensity 0..1 from the eddy viscosity, empty if unavailable
    public float[] Age = [];                    // s, 6k: the online age of air (time mean of the transient scalar), NaN where the scalar is held (outdoors, pinned fresh air, solid); empty unless the session solved it
    public List<Snapshot> Snapshots = new();    // empty unless RunOptions.SnapshotCount > 0
    public List<DeviceResult> Devices = new();  // one per VoxelGrid.DeviceRegion, measured on this field
    public List<SupplyResult> Supplies = new(); // one per link-wise Supply patch: its flux controller's state
    public List<RoomFlux> RoomFluxes = new();
    public List<OpeningLinkFlux> OpeningFluxes = new();   // per open axis-aligned opening: throat-plane link flux (empty = Analysis falls back to u.n)
    public List<RoomHold> RoomHolds = new();    // 6j: per held room, its ideal heater/cooler's time-mean duty (empty when free-running)
    public long AverageSamples;
    public long NonFiniteSkipped;               // RT-09: NaN/Inf samples FlowFieldMean.Add skipped (kept out of the mean), summed over the batches
    public int Steps;
    public double Seconds;                      // wall-clock solve time
    public string Backend = "";                 // e.g. "CUDA · NVIDIA GeForce RTX 5070 Ti" or "CPU · 16 threads"
}

/// <summary>What one body-force device actually did, measured on the time-averaged field.</summary>
public sealed class DeviceResult
{
    public string SourceId = "", SourceName = "";
    public int SourceIndex = -1;                    // position in BuildingModel.Sources
    public DeviceKind Kind;
    public float FlowRate;                          // m3/s measured through the outlet cells
    public float SetFlowRate;                       // m3/s the device was asked for
    public float SupplyTemperature = float.NaN;     // °C, as configured
    public float ReturnTemperature = float.NaN;     // °C measured at the linked return cells
    /// <summary>Sensible cooling the unit takes out of the room air, watts: rho * cp * V * (T_in - T_supply),
    /// integrated over the outlet cells from the temperature relaxation actually applied there, so it
    /// balances against the room's own enthalpy change. 0 for devices that move air without conditioning it.
    /// <para>Compare with <see cref="ReturnTemperature"/>: a return grille sitting in its own discharge
    /// re-ingests cold air and reads below the room, which is a real short-circuit, not an error.</para></summary>
    public float HeatRemovedWatts;
    public int Cells;
}

/// <summary>A Supply patch's flux controller (LbmSolver): the patch is a moving-wall bounce-back on the links from
/// its cells into the room, and a PI loop scales that wall velocity until the flux the links deliver equals the set
/// FlowRate. Scale above 1 means links were missing (a wall or another patch cell next to the patch, a thick or staircased
/// patch) and the rest had to carry their share. The wall velocity is first set to carry FlowRate through the
/// carved cells' area (AreaRatio), so Scale corrects only what the links lose.</summary>
public sealed class SupplyResult
{
    public int SourceIndex = -1;                    // position in BuildingModel.Sources
    public float SetFlowRate;                       // m3/s the source was asked for
    public float FlowRate;                          // m3/s the patch-face links deliver at the current scale
    public float AreaRatio = 1f;                    // drawn Width x Height / carved cells' area (feed-forward on the wall velocity)
    public float Scale = 1f;                        // wall-velocity multiplier the controller settled on
    public float Gain = 1f;                         // the last update's scale step (new/old), clamped
    public int Updates;                             // controller updates so far
    public int Cells, Links;                        // Fixed cells, and links from them into the room
}

/// <summary>Instantaneous field at one moment of the run, half precision to keep dozens of them affordable.</summary>
public sealed class Snapshot
{
    public float Time;                          // seconds since the start of averaging
    public Half[] Ux = [], Uy = [], Uz = [];
    public Half[] T = [];                       // empty unless thermal
}

/// <summary>Steady scalar fields on the final flow field (RunOptions.AirQuality). Empty arrays when not computed.</summary>
public sealed class ScalarFields
{
    public float[] Co2 = [];            // ppm
    public float[] Age = [];            // s, mean age of air (Sandberg); 0 at inflow boundaries and supplies
    /// <summary>AGE-HIGH: with the online age landed (High), the steady age on the averaged flow, the conservative estimate; empty otherwise.</summary>
    public float[] SteadyAge = [];
    /// <summary>Standard with some room on the steady age: the raw online age field (before the steady rooms were laid over it), for the comparison column only; empty otherwise.</summary>
    public float[] OnlineAge = [];
    public float[] Contaminant = [];    // units/m3, relative
}

/// <summary>Comfort indices per cell (RunOptions.Comfort); NaN outside the analysed storeys' air.</summary>
public sealed class ComfortFields
{
    public float[] Pmv = [];            // ISO 7730 / ASHRAE 55
    public float[] Ppd = [];            // %
    public float[] Dr = [];             // draught rate %, Fanger 1988
}

/// <summary>Mass balance over the analysed storeys: what came in through openings and supplies vs what left.
/// IsPhysical is the trust gate the UI shows; a failed gate is displayed with a warning, never silently.</summary>
public sealed class MassLedger
{
    public float In, Out;               // m3/s
    public float Residual;              // |In - Out| / max(In, Out), 0 when both are 0
    public bool IsPhysical;             // Residual <= 0.10 and no NaN anywhere
    /// <summary>Largest per-room |NetFlux| / max(Inflow, Outflow) over the analysed rooms (0 when none moves air).
    /// Reported, not gated: a room holding a Fixed patch or device has a legitimate net flux.</summary>
    public float WorstRoomResidual;
    /// <summary>Whole-building fresh-air rate, air changes per hour = 3600 / the volume-weighted mean age of air over every indoor room
    /// cell (P15-07: the exterior-opening ledger reads ~0 for a single-sided building). NaN while the age is not solved.</summary>
    public float FreshAch = float.NaN;
    /// <summary>AGE-HIGH: the same from the steady age on the averaged flow (the conservative estimate beside the online <see cref="FreshAch"/> in High); NaN otherwise.</summary>
    public float ConservativeFreshAch = float.NaN;
    /// <summary>RW-03: m3 of indoor air at the age cap (no air path: sealed rooms) that <see cref="FreshAch"/> leaves out, and the rooms they belong to.</summary>
    public float SealedVolume;
    public List<string> SealedRooms = new();
    /// <summary>What <see cref="FreshAch"/> averages over, for every label: "all indoor volume", or the same without the sealed rooms, named.</summary>
    public string VolumeText => Analysis.VolumeText(SealedVolume, SealedRooms);
    public List<string> Notes = new();
}

public sealed class OpeningFlow
{
    public Opening Opening = new();
    public float FlowRate;      // m3/s through the opening, positive = air moving along +Normal: u.n integrated over the carved throat cells
    public float MeanSpeed;     // m/s, mean velocity along +Normal over the carved throat (FlowRate / CarvedArea); an oblique opening with a link flux: FlowRate / (Width x Height)
    /// <summary>Air area of the carved throat (Analysis.ThroatCells x Dx^2), m2. Differs from Width x Height
    /// by the voxel rounding of the hole. For an oblique (not axis-aligned) opening it is only the cell count of
    /// the one-cell slab (0.63-1.50 of the opening's area by phase), not an area to divide by (RX-09b).</summary>
    public float CarvedArea;
    /// <summary>Mean static pressure on the -Normal side minus the +Normal side, Pa, each averaged over the
    /// opening's footprint Depth/2 + 1.5 Dx clear of the wall's mid-plane. NaN when a side has no air.</summary>
    public float PressureDrop = float.NaN;
    /// <summary>|FlowRate| / (Width x Height x sqrt(2 |PressureDrop| / rho)): the discharge coefficient the run
    /// implies on the NOMINAL area. A sharp-edged opening is ~0.6-0.65; outside 0.5-0.8 on an opening at least
    /// 4 cells across raises a grid warning. NaN when there is no pressure drop to read.</summary>
    public float EffectiveCd = float.NaN;
    /// <summary>Calibrated opening model only: the discharge coefficient the throat loss is sized for
    /// (<see cref="Opening.DischargeCoefficient"/> or 0.61). NaN under <see cref="OpeningModel.Resolved"/>.</summary>
    public float ModelledCd = float.NaN;
    /// <summary>Calibrated opening model only: loss coefficient K (on the throat velocity head) applied in the throat
    /// cells. 0 under <see cref="OpeningModel.Resolved"/> or when the opening already resolves below its target.</summary>
    public float LossK;
}

public sealed class SourceFlow
{
    public AirSource Source = new();
    public float FlowRate;      // m3/s measured through the patch along Direction (should match Source.FlowRate)
    /// <summary>Body-force devices only (AirSource.Device != Patch): sensible cooling this unit removes
    /// from the room air, watts (see DeviceResult.HeatRemovedWatts). 0 for a plain patch, a fan, or a
    /// non-thermal run.</summary>
    public float HeatRemovedWatts;
    /// <summary>Conditioning devices with a linked return: the temperature measured at the intake grille,
    /// °C. NaN for everything else. Well below the room mean means the return is short-circuiting.</summary>
    public float ReturnTemperature = float.NaN;
}

public sealed class RoomResult
{
    public Room Room = new();
    /// <summary>Always false since batch 5: a room's numbers come from the whole-building run, never from a nested fine box (see <see cref="Simulation.AnalyzeNested"/>); kept for the headless table's column.</summary>
    public bool FromFineBox;
    /// <summary>m3 the room's <see cref="Ach"/> divides by: the nominal volume (footprint area x height) when the model
    /// gives one, else <see cref="VoxelVolume"/> (see <see cref="VolumeIsNominal"/>). The voxel grid grows solid around
    /// thin partitions, so the air it holds is 4-28 % under the drawn room and ACH from it read that much high.</summary>
    public float Volume;
    /// <summary>m3 of air cells in the room mask: the control volume of the solved transport (scalar age, flow-through
    /// time, air change effectiveness all use this one).</summary>
    public float VoxelVolume;
    /// <summary>m3 from the model's footprint loops (even-odd) x (ZMax - ZMin); NaN when the room has none that can be
    /// trusted (see <see cref="VolumeNote"/>).</summary>
    public float NominalVolume = float.NaN;
    /// <summary>True when <see cref="Volume"/> is the nominal volume; false when it fell back to <see cref="VoxelVolume"/>.</summary>
    public bool VolumeIsNominal;
    /// <summary>Why <see cref="Volume"/> is the voxel volume (detected room, no height/footprint, room cropped by the
    /// domain); empty when it is nominal.</summary>
    public string VolumeNote = "";
    public float Inflow;           // m3/s entering across the room boundary through real connections (see InternalExchange)
    /// <summary>m3/s leaving across the room boundary through real connections (time-resolved, like <see cref="Inflow"/>),
    /// plus Return-patch extraction. Gross <b>exchange</b> rates, not fresh-air supply: with reversing flow both are
    /// positive although the room receives no net outdoor air, and <see cref="Ach"/> (from Inflow) is an exchange
    /// rate, not a ventilation delivery rate.</summary>
    public float Outflow;
    /// <summary>Inflow - Outflow, m3/s. About 0 for a room with no source or sink (incompressible mean flow);
    /// a persistent value means leakage through the outline or an unconverged average.</summary>
    public float NetFlux;
    /// <summary>m3/s crossing the room outline through plain air -- not an opening throat, a device or a
    /// Fixed patch, or the domain boundary. That is the loop cutting through a larger air space (an
    /// open-plan area split into two rooms, a detected room whose outline sits in open air), and what
    /// crosses it is mostly recirculation, so it is kept out of <see cref="Inflow"/> and
    /// <see cref="Ach"/>; &gt; 0 comes with a Warning on the grid.</summary>
    public float InternalExchange;
    /// <summary>D10: m3/s entering a detected room through its one-cell band where an air path leads straight on through the wall (a hole nothing
    /// listed as an opening). In neither <see cref="Inflow"/> nor <see cref="InternalExchange"/>, so <see cref="Ach"/> leaves it out; 0 for other rooms.</summary>
    public float UnlistedInflow;
    /// <summary>m3/s of <see cref="Inflow"/> that enters straight from outside: exterior-opening faces, supply patches,
    /// devices and the domain edge. Transfer air through an interior door is not in it.</summary>
    public float OutdoorInflow;
    /// <summary>TOTAL air exchange, air changes per hour = (Inflow + InternalExchange) * 3600 / Volume: every cubic metre
    /// entering the room volume, from outside and from other rooms, through openings, devices and plain-air faces (stair
    /// voids, open-plan edges). Not a ventilation delivery rate; see <see cref="FreshAch"/>.</summary>
    public float Ach;
    /// <summary>Fresh-air air changes per hour from the mean age of air, 3600 / <see cref="MeanAge"/> (Sandberg 1981; ISO
    /// 16000-8). Equals Q * 3600 / V for a perfectly mixed room supplied with outdoor air Q, and credits air from other
    /// rooms at its real age. NaN while the age is not solved. At the solver cap (<see cref="AgeSealed"/>) the room has no air path.</summary>
    public float FreshAch = float.NaN;
    /// <summary>AGE-HIGH: fresh-air ACH from the steady age on the averaged flow, the conservative estimate beside the online <see cref="FreshAch"/> in High; NaN otherwise.</summary>
    public float ConservativeFreshAch = float.NaN;
    /// <summary>Portal step 1: m3/s of air entering the room through the planes of the openings that belong to it (sum of the inflow side of each opening's
    /// throat-plane flux, time-averaged like the opening flows). A fixed physical surface, unlike the ragged room-mask boundary behind <see cref="Ach"/>;
    /// NaN when the field carries no opening plane fluxes (CPU fallback / not solved).</summary>
    public float ThroughOpeningInflow = float.NaN;
    /// <summary>m3/s of net outdoor air through the room's exterior openings only: per opening (in - out), floored at 0, summed. NaN like <see cref="ThroughOpeningInflow"/>.</summary>
    public float NetOutdoorInflow = float.NaN;
    /// <summary>Share (0-1) of all air entering the room (total exchange plus <see cref="UnlistedInflow"/>) that does not pass through its openings' planes; NaN like <see cref="ThroughOpeningInflow"/>.</summary>
    public float UnexplainedShare = float.NaN;
    /// <summary><see cref="ThroughOpeningInflow"/> in air changes per hour (on <see cref="Volume"/>).</summary>
    public float ThroughOpeningAch => Volume > 0f ? ThroughOpeningInflow * 3600f / Volume : float.NaN;
    /// <summary><see cref="NetOutdoorInflow"/> in air changes per hour (on <see cref="Volume"/>).</summary>
    public float NetOutdoorAch => Volume > 0f ? NetOutdoorInflow * 3600f / Volume : float.NaN;
    /// <summary>Outdoor air entering this room straight from outside, air changes per hour (<see cref="OutdoorInflow"/> x 3600 / <see cref="Volume"/>).</summary>
    public float OutdoorAch => Volume > 0f ? OutdoorInflow * 3600f / Volume : 0f;
    /// <summary>A room loop drawn through open air (typically a detected room on an imported mesh): next to nothing enters it through an
    /// opening, a device or the domain edge (under 5 % of what crosses its outline through plain air), so its numbers are not a room's.
    /// The UI shows "–" for it and the report keeps it out of Best / Least ventilated.</summary>
    public bool ExchangeOnly => InternalExchange > 1e-3f && Inflow < 0.05f * InternalExchange;
    /// <summary>The age sits at the solver's 24 h cap: no fresh air reaches this room (sealed, or only through a closed door).</summary>
    public bool AgeSealed => !float.IsNaN(VolumeMeanAge) ? VolumeMeanAge >= ScalarSolver.AgeCapSeconds * 0.999f
                                                       : !float.IsNaN(MeanAge) && MeanAge >= ScalarSolver.AgeCapSeconds * 0.999f;
    /// <summary>IMP-C: the room is under <see cref="Envelope.MinRoomCells"/> cells across at this grid; its numbers are not to be trusted (<see cref="TooFewCellsLabel"/>).</summary>
    public bool TooFewCells;
    public const string TooFewCellsLabel = "unreliable (too few cells)";
    public float MeanSpeed;         // m/s in the occupied zone (0.1-1.8 m above floor)
    public float StagnantFraction;  // share of occupied-zone cells below Analysis.StagnantSpeed

    // Advanced, NaN when the corresponding option was off
    public float MeanTemperature = float.NaN;       // °C, occupied zone
    /// <summary>6j: W, time-mean duty of the room's ideal heater/cooler on a held-temperature thermal run (positive = heating); NaN when the
    /// room is not held (free-running, isothermal, or no outline).</summary>
    public float HoldDutyWatts = float.NaN;
    public float MeanCo2 = float.NaN;               // ppm, occupied zone
    public float MeanAge = float.NaN;               // s, occupied zone (0.1-1.8 m): the "Age (occupied zone)" column
    /// <summary>s, mean age over the room's whole volume (all heights): what <see cref="FreshAch"/> and the air-change efficiency use (RV-03).</summary>
    public float VolumeMeanAge = float.NaN;
    /// <summary>s, the online (time-averaged) volume-mean and occupied-zone age of a room that landed the steady age instead (<see cref="ScalarFields.OnlineAge"/>); NaN when there is none. Comparison only, never the headline.</summary>
    public float OnlineAgeRaw = float.NaN, OnlineMeanAgeRaw = float.NaN;
    /// <summary>Air-change efficiency, ISO 16000-8 / Sandberg &amp; Sjoeberg 1983: exhaust age / (2 x mean age) = 0.5 for a fully
    /// mixed room, up to 1 for piston flow. Only for a room where at least 90 % of the inflow comes straight from outside; NaN otherwise.</summary>
    public float AirChangeEffectiveness = float.NaN;

    /// <summary>A fine-box room has no air age of its own (the scalars are solved on the coarse grid only): take the coarse room's.</summary>
    public void CarryAgeFrom(RoomResult coarse)
    {
        if (!float.IsNaN(MeanAge) || !float.IsNaN(VolumeMeanAge)) return;
        MeanAge = coarse.MeanAge; VolumeMeanAge = coarse.VolumeMeanAge; FreshAch = coarse.FreshAch; ConservativeFreshAch = coarse.ConservativeFreshAch; AirChangeEffectiveness = coarse.AirChangeEffectiveness;
        OnlineAgeRaw = coarse.OnlineAgeRaw; OnlineMeanAgeRaw = coarse.OnlineMeanAgeRaw;
    }
    public float ComfortFraction = float.NaN;       // share of occupied zone with |PMV| <= 0.5
    public float DraughtFraction = float.NaN;       // share of occupied zone with DR > 20 %
}

public sealed class Streamline
{
    public Vector3[] Points = [];
    public float[] Speeds = [];     // m/s at each point
}

public sealed class SimulationResult
{
    public BuildingModel Model = new();
    public FlowField Field = new();
    public Vector2 Wind;                    // m/s at 10 m height, direction the air moves toward, model axes
    public RunOptions Options = new();
    public List<OpeningFlow> Openings = new();
    public List<RoomResult> Rooms = new();
    public List<SourceFlow> Sources = new();
    public List<Streamline> Streamlines = new();
    public float ColorMax;                  // colour scale 0..ColorMax m/s = max(|Wind|, source face velocities, 0.1)
    public ScalarFields Scalars = new();    // empty unless AirQuality
    public ComfortFields Comfort = new();   // empty unless Comfort
    public MassLedger Ledger = new();
    /// <summary>How an <see cref="RunOptions.AutoConverge"/> run ended; null for a fixed-length run.</summary>
    public ConvergenceReport? Convergence;
    /// <summary>The fine box's own field and analysis when the run used <see cref="RunOptions.FocusBox"/>; null otherwise.</summary>
    public FlowField? FineField;
    public SimulationResult? FineResult;

    /// <summary>Total sensible cooling removed by every conditioning device (SplitAc/CassetteAc with a
    /// SupplyTemperature) on a thermal run; 0 otherwise.</summary>
    public float HeatRemovedWatts
    {
        get { float w = 0f; foreach (var d in Field.Devices) w += d.HeatRemovedWatts; return w; }
    }
}

/// <summary>Everything known at one point, for the probe tool. NaN where not computed.</summary>
public readonly record struct ProbeValues(Vector3 Velocity, float Speed, float Pressure, float Temperature,
    float Co2, float Age, float Pmv, float Ppd, float Dr, string Room);

public static class Simulation
{
    /// <summary>Progress runs 0..1 over the whole pipeline (voxelize, flow, scalars, analysis).</summary>
    public static SimulationResult Run(BuildingModel model, Vector2 wind, RunOptions? options = null,
        IProgress<double>? progress = null, CancellationToken ct = default)
    {
        options ??= new RunOptions();
        // P17-07: a GPU failure (init, a device loss, any accelerator exception while stepping or reading back) restarts the run ONCE on the CPU, through
        // AveragedPass's policy; the options get their backend back afterwards so a later run tries the GPU again.
        var backend = options.Backend;
        bool gpu = backend != Backend.Cpu && LbmSolver.GpuAvailable;
        try { return RunCore(model, wind, options, progress, ct); }
        catch (Exception e)
        {
            if (options.Ultra != null || !AveragedPass.TryGpuRestart(options, gpu, e, out _)) throw;   // Ultra never falls back to the CPU (UltraDriver retries once on the GPU)   // after the run's sessions are disposed, not in a filter
        }
        int? cellsBefore = options.TargetCells;
        options.CpuReplan?.Invoke(options);   // RU-12: the CPU's cell budget, not the GPU-sized grid
        try { return RunCore(model, wind, options, progress, ct); }
        finally { options.Backend = backend; options.GpuLossNote = null; options.TargetCells = cellsBefore; }
    }

    static SimulationResult RunCore(BuildingModel model, Vector2 wind, RunOptions options,
        IProgress<double>? progress, CancellationToken ct)
    {
        if (options.Comfort) options.Thermal = true;
        bool gpu = options.Backend != Backend.Cpu && LbmSolver.GpuAvailable;
        int cells = options.TargetCells ?? TargetCells(options.Quality, gpu);
        var grid = Voxelizer.Build(model, cells, options.MaxDegreeOfParallelism, options.Domain,
            minDx: options.MinDx is { } m ? Math.Min(m, 0.1f) : 0.1f, mode: options.DomainMode, wind: wind,
            maxCells: options.DomainMode == DomainMode.Full ? LbmSolver.CellLimit(gpu, options.Thermal, options.Ultra != null) : 0, fixedAutoDx: options.Ultra?.FixedDx ?? 0f);
        bool scalarPass = options.AirQuality || options.AgeOfAir;   // D1: the fresh-air ACH needs the age of air wherever ACH is shown
        var flowProgress = scalarPass ? Scale(progress, 0.0, 0.7) : progress;
        ConvergenceReport? convergence = null;
        FlowField? fineField = null;
        ScalarFields? passScalars = null;
        FlowField field;
        if (options.AutoConverge)
        {
            // 6f: the same averaged pass as the app's Accurate (AveragedPass): settle, batches, cap, stop rule, landing and the age (online when the session can).
            var outcome = RunAveragedPass(grid, model, wind, options, flowProgress, ct);
            field = outcome.Field; fineField = outcome.Fine; convergence = outcome.Report;
            if (outcome.Scalars.Age.Length == field.Grid.Count) passScalars = outcome.Scalars;
        }
        else field = LbmSolver.Solve(grid, model, wind, options, flowProgress, ct);
        var scalars = passScalars ?? (scalarPass ? ScalarSolver.Solve(model, field, options, Scale(progress, 0.7, 0.95), ct, ageOnly: !options.AirQuality) : new ScalarFields());
        SimulationResult result;
        if (fineField != null)
        {
            // Finish runs inside AnalyzeNested, before the fine rooms replace the coarse ones; the scalars are the coarse grid's only.
            result = AnalyzeNested(model, field, fineField, wind, options, out var fineResult, finish: true, scalars);
            result.FineField = fineField; result.FineResult = fineResult;
        }
        else
        {
            result = Analysis.Analyze(model, field, wind, options);
            result.Scalars = scalars;
        }
        result.Convergence = convergence;
        if (options.Comfort) result.Comfort = Comfort.Compute(model, field, options);
        if (fineField == null) Analysis.Finish(result);   // room extras from scalars/comfort, ledger
        progress?.Report(1);
        return result;
    }

    static IProgress<double>? Scale(IProgress<double>? p, double from, double to)
        => p == null ? null : new Progress<double>(v => p.Report(from + (to - from) * Math.Clamp(v, 0, 1)));

    /// <summary>6f: a headless run with <see cref="RunOptions.AutoConverge"/>: opens the session (and, with a <see cref="RunOptions.FocusBox"/>, its nested fine
    /// box; thermal runs are not nested) and runs <see cref="AveragedPass"/> on it, the pass the app's Accurate runs. Cap: <see cref="RunOptions.WallCapSeconds"/>
    /// (0 = the Standard 10 minutes); a warm start seeds the pass and skips its settle.</summary>
    static PassOutcome RunAveragedPass(VoxelGrid grid, BuildingModel model, Vector2 wind, RunOptions options,
        IProgress<double>? progress, CancellationToken ct)
    {
        // A focus box: the fine session starts WITH the coarse one and every step drives both (one coarse step = two fine), so the box
        // settles through the same warm-up as the building and the batch averages cover both. Thermal runs are not nested (isothermal only).
        bool nested = options.FocusBox is { IsValid: true } && !options.Thermal && options.WarmStart == null;
        var o = options.Clone();
        // Online age: High lands it everywhere (steady age rides along), Standard per room where it settled (AccurateRecipe.Options, AveragedPass.PerRoom).
        var warm = options.Thermal || nested ? null : options.WarmStart;
        if (options.Ultra != null)
        {
            var ucfg = new PassSettings { CapSeconds = options.WallCapSeconds > 0 ? options.WallCapSeconds : AveragedPass.CapSecondsFor(false, null), SettleFactor = PassSettings.ColdSettleFactor, RawClockFactor = Ultra.RawClockFactor };
            return UltraDriver.Run(grid, model, wind, o, ucfg, options.Ultra, ct, progress);
        }
        using var session = LbmSolver.Start(grid, model, wind, o, warm);
        if (session.WarmupSteps <= 0 && session.PlannedSteps <= 0)
        {
            // Inert session (nothing can drive a flow): nothing to converge, return the (all-zero) field.
            return new PassOutcome { Field = session.ToField() };
        }
        LbmSolver.NestedSession? nest = null;
        if (nested)
        {
            try { nest = LbmSolver.StartNested(session, options.FocusBox!.Value, o, driveCoarse: true); }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException or NotSupportedException)
            {
                grid.Warnings.Add($"focus box refused ({e.Message}): the whole building was solved at the coarse cell size");
                Console.Error.WriteLine($"[Convergence] focus box refused: {e.Message}");
            }
        }
        using var nestHolder = nest;
        var cfg = new PassSettings
        {
            CapSeconds = options.WallCapSeconds > 0 ? options.WallCapSeconds : AveragedPass.CapSecondsFor(false, null),
            SettleFactor = PassSettings.SettleFactorFor(options.Thermal, warm != null),
            Seed = warm,
        };
        var outcome = AveragedPass.Run(new LbmPassSession(session, nest), model, wind, o, cfg, ct, progress);
        var report = outcome.Report;
        Console.Error.WriteLine($"[Convergence] reason={report.Status.Reason} steps={report.Steps} batches={report.Batches} {report.AgeLabel} " +
            string.Join(" ", report.Status.Rooms.Select(r =>
                $"{r.RoomId}(mean={r.Mean:F2} halfWidth={r.HalfWidth:F3} tol={r.Tolerance:F3} n={r.BatchCount} conv={r.Converged})")));
        return outcome;
    }

    /// <summary>The batch-means loop of PLAN_V2 2.2, on a session the caller owns (6f: kept for the sandbox's convergence checks 53/54, F9b and the audit
    /// check, which drive it; the app and <see cref="RunOptions.AutoConverge"/> run <see cref="AveragedPass"/>). Returns the final status and the number of batches run;
    /// the session is left holding the last batch's average.
    /// <para>Warm-up, cold start (<paramref name="seed"/> null): the session's fixed warm-up, then a probe analysis
    /// for each room's flow-through time V/Q, then the rest of the largest flow-through if it is longer. Warm start
    /// (the field the session was seeded from): no fixed warm-up -- the seed IS a developed flow, so the probe is
    /// the seed's own analysis and batches start at once; whatever transient the seed still carries is dropped by
    /// the monitor's start-up truncation (<see cref="ConvergenceMonitor.TransientBatches"/>).</para></summary>
    public static (ConvergenceStatus Status, int Batches) Converge(LbmSolver.LbmSession session, BuildingModel model, Vector2 wind,
        RunOptions options, FlowField? seed, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        var (status, batches, _, _, _) = ConvergeCore(new SessionDriver(session), model, wind, options, seed, progress, ct, System.Diagnostics.Stopwatch.StartNew());
        return (status, batches);
    }

    /// <summary>What <see cref="ConvergeCore"/> steps and reads (6f: only <see cref="Converge"/>'s session now; the app and the headless run use <see cref="AveragedPass"/>).</summary>
    internal abstract class ConvergeDriver
    {
        public abstract int WarmupSteps { get; }
        public abstract int PlannedSteps { get; }
        /// <summary>Coarse steps taken.</summary>
        public abstract long Steps { get; }
        public abstract float TimeStepSeconds { get; }
        public abstract void Step(int n, CancellationToken ct);
        public abstract void ResetAveraging(bool startNow = false);
        /// <summary>The current average window: the coarse field and, with a nested box, the fine one.</summary>
        public abstract (FlowField Coarse, FlowField? Fine) Pull();
    }

    sealed class SessionDriver(LbmSolver.LbmSession s) : ConvergeDriver
    {
        public override int WarmupSteps => s.WarmupSteps;
        public override int PlannedSteps => s.PlannedSteps;
        public override long Steps => s.Steps;
        public override float TimeStepSeconds => s.TimeStepSeconds;
        public override void Step(int n, CancellationToken ct) => s.Step(n, ct);
        public override void ResetAveraging(bool startNow = false) => s.ResetAveraging(startNow);
        public override (FlowField, FlowField?) Pull() => (s.ToField(), null);
    }

    /// <summary>Analyses a pulled window: the coarse field alone, or the coarse one with the fine box answering for the sources
    /// (devices) two fine cells inside it. Same rule as the app's live analysis. Room ACH, opening flows, the ledger, the
    /// field and everything else stay the whole-building (coarse) ones (batch 5; the box sharpens the local field view only). <paramref name="fine"/> is the fine box's own analysis.</summary>
    public static SimulationResult AnalyzeNested(BuildingModel m, FlowField coarseField, FlowField? fineField, Vector2 w, RunOptions o,
        out SimulationResult? fine, bool finish = false, ScalarFields? scalars = null, CancellationToken ct = default, bool portal = true)
    {
        var result = Analysis.Analyze(m, coarseField, w, o, ct, portal);
        if (scalars != null && scalars.Age.Length == coarseField.Grid.Count) result.Scalars = scalars;   // coarse grid only
        if (finish) Analysis.Finish(result);   // before the substitution below: Finish reads each room against the coarse field
        fine = null;
        if (fineField == null) return result;
        fine = Analysis.Analyze(m, fineField, w, o, ct, portal);
        if (finish) Analysis.Finish(fine);
        var fg = fineField.Grid;
        float inset = 2 * fg.Dx;
        bool Inside(Vector3 p, float ins)
        {
            var min = fg.Origin + new Vector3(ins);
            var max = fg.Origin + new Vector3(fg.Nx, fg.Ny, fg.Nz) * fg.Dx - new Vector3(ins);
            return p.X >= min.X && p.Y >= min.Y && p.Z >= min.Z && p.X <= max.X && p.Y <= max.Y && p.Z <= max.Z;
        }
        for (int i = 0; i < result.Sources.Count; i++)
        {
            var src = result.Sources[i].Source;
            if (!Inside(src.Center, inset)) continue;
            foreach (var f in fine.Sources) if (f.Source.Id == src.Id) { result.Sources[i] = f; Analysis.DropDeviceMovedNote(result, src); break; }   // RW-08
        }
        return result;
    }

    /// <summary>Steps <paramref name="n"/> in slices so <see cref="RunOptions.WallCapSeconds"/> can interrupt a long warm-up or
    /// batch; false when the cap was hit before all n steps ran. Without a cap it is one plain Step call.</summary>
    static bool StepCapped(ConvergeDriver session, int n, CancellationToken ct, System.Diagnostics.Stopwatch wall, double capSeconds)
    {
        if (capSeconds <= 0) { session.Step(n, ct); return true; }
        int slice = Math.Clamp(n / 32, 1, 250);   // a batch can be 100k+ steps: a slice must stay a couple of seconds at most
        for (int done = 0; done < n;)
        {
            if (wall.Elapsed.TotalSeconds >= capSeconds) return false;
            int k = Math.Min(slice, n - done);
            session.Step(k, ct);
            done += k;
        }
        return true;
    }

    /// <summary>Returns the batch-means loop's result plus the SAMPLE-WEIGHTED MEAN of the batches the monitor keeps (those after its
    /// start-up truncation; coarse and, with a nested box, fine): null with fewer than 2 complete batches, so a caller falls back
    /// to the session's own window. The mean field analyses to the same room ACH the monitor reports (fluxes average linearly).</summary>
    static (ConvergenceStatus Status, int Batches, bool WallCapHit, FlowField? Mean, FlowField? MeanFine) ConvergeCore(ConvergeDriver session,
        BuildingModel model, Vector2 wind,
        RunOptions options, FlowField? seed, IProgress<double>? progress, CancellationToken ct, System.Diagnostics.Stopwatch wall,
        ConvergenceReport? sink = null)
    {
        SimulationResult AnalyzePull((FlowField Coarse, FlowField? Fine) p) =>
            p.Fine == null ? Analysis.Analyze(model, p.Coarse, wind, options) : AnalyzeNested(model, p.Coarse, p.Fine, wind, options, out _);
        var meanC = new BatchWindowMean();
        var meanF = new BatchWindowMean();
        double cap = options.WallCapSeconds;
        bool capHit = false;
        int fallbackWarmup = session.WarmupSteps;
        SimulationResult probe;
        if (seed != null) probe = Analysis.Analyze(model, seed, wind, options);
        else
        {
            // The session only starts averaging AT its warm-up step, so a probe taken right there reads an empty
            // average (every room Inflow 0, no flow-through time, batches falling back to a fixed length); average
            // a short window first.
            capHit |= !StepCapped(session, fallbackWarmup, ct, wall, cap);
            session.ResetAveraging();
            capHit |= !StepCapped(session, Math.Max(50, fallbackWarmup / 5), ct, wall, cap);
            probe = AnalyzePull(session.Pull());
        }

        float longestFlowThrough = 0f;
        // Flow-through time of the air the solver actually holds, so the voxel volume, not the nominal one ACH uses.
        foreach (var r in probe.Rooms) if (r.Inflow > 1e-6f) longestFlowThrough = MathF.Max(longestFlowThrough, r.VoxelVolume / r.Inflow);
        int flowThroughSteps = ConvergencePlan.WarmupSteps(probe.Rooms.Select(r => (r.VoxelVolume, r.Inflow)), session.TimeStepSeconds, 0);
        // Warm-up after the fixed one: WarmupFlowThroughs x the longest room's flow-through (1 = the original rule; HighAccuracy 2).
        long extraWarmupL = seed != null ? 0 : (long)flowThroughSteps * Math.Max(1, options.WarmupFlowThroughs);
        int extraWarmup = (int)Math.Min(extraWarmupL, int.MaxValue);
        if (extraWarmup > 0) capHit |= !StepCapped(session, extraWarmup, ct, wall, cap);

        int batchSteps = ConvergencePlan.BatchSteps(longestFlowThrough, session.TimeStepSeconds, fallbackSteps: Math.Max(50, fallbackWarmup / 5));
        if (sink != null) { sink.FlowThroughSeconds = longestFlowThrough; sink.FlowThroughSteps = flowThroughSteps; sink.BatchSteps = batchSteps; }
        var monitor = new ConvergenceMonitor();
        // Total steps this self-stopping run is allowed, on top of its own fixed warm-up: a room with an
        // enormous flow-through time would otherwise take a very long time to even reach MinBatches once --
        // this bounds it independently of MonitorBatches, the same role RunOptions' fixed MaxSteps/
        // ThermalMaxSteps play for the non-self-stopping path.
        // (Same budget for a warm start, which skips the warm-up but still needs its batches.)
        // Never below the steps already spent (warm-up) plus MaxBatches + 1 batches, so MinTotalBatches stays reachable.
        long hardStepCap = Math.Max(Math.Max(session.PlannedSteps, fallbackWarmup + flowThroughSteps) * 8L, session.Steps + (monitor.MaxBatches + 1L) * batchSteps);

        var status = new ConvergenceStatus();
        int batches = 0;
        for (; batches < monitor.MaxBatches;)
        {
            if (session.Steps + batchSteps > hardStepCap) { status.Reason = ConvergenceStopReason.Capped; break; }
            if (capHit || (cap > 0 && wall.Elapsed.TotalSeconds >= cap))
            {
                // Wall-time cap. Leave a short averaged window behind so the returned field is not empty.
                capHit = true; status.Reason = ConvergenceStopReason.Capped;
                if (batches == 0)
                {
                    // 50 steps always, then up to batchSteps / 20 within 5 % of the cap's wall time past it.
                    session.ResetAveraging(startNow: true);
                    session.Step(50, ct);
                    StepCapped(session, Math.Max(0, batchSteps / 20 - 50), ct, wall, cap * 1.05);
                }
                break;
            }
            session.ResetAveraging(startNow: true);
            if (!StepCapped(session, batchSteps, ct, wall, cap))
            {
                // Cap hit inside a batch: the partial batch is not a batch mean and is dropped. With >= 2 completed batches the returned
                // field is their kept-window mean (meanC); only with fewer does the caller fall back to the session's partial window.
                capHit = true; status.Reason = ConvergenceStopReason.Capped;
                break;
            }
            batches++;
            var pulled = session.Pull();
            var batchResult = AnalyzePull(pulled);
            monitor.AddBatch(batchResult.Rooms.Select(r => (r.Room.Id, r.Ach)));
            status = monitor.Evaluate();
            // Mean field of the SAME batches the monitor keeps: (TransientBatches, batches]. BatchWindowMean keeps the aliasing rules
            // of FlowFieldMean.Add (it mutates what it is given), and each pulled field is used once.
            meanC.Add(pulled.Coarse); if (pulled.Fine != null) meanF.Add(pulled.Fine);
            progress?.Report(Math.Clamp((double)session.Steps / Math.Max(1, hardStepCap), 0, 0.95));
            if (status.Reason != ConvergenceStopReason.Running) break;
        }
        if (status.Reason == ConvergenceStopReason.Running) status.Reason = ConvergenceStopReason.Capped;
        return (status, batches, capHit, meanC.Mean, meanF.Mean);
    }

    public static int TargetCells(Quality quality, bool gpu) => (quality, gpu) switch
    {
        (Quality.Preview, true) => 1_500_000,
        (Quality.Preview, false) => 250_000,
        (Quality.Refine, true) => 6_000_000,
        (Quality.HighAccuracy, true) => 12_000_000,   // fallback only: HighAccuracy.Plan sets RunOptions.TargetCells from the GPU memory
        _ => 800_000,
    };
}
