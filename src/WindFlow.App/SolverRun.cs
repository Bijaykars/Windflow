using System.Numerics;
using WindFlow.Core;

namespace WindFlow.App;

/// <summary>One solver run on a background task. The solver picks its own backend (ILGPU/CUDA or CPU); it
/// never touches the D3D12 device, so the viewport keeps rendering while it works.</summary>
public sealed class SolverRun
{
    readonly CancellationTokenSource cancel = new();

    public Task<SimulationResult>? Task { get; private set; }
    public bool Running => Task is { IsCompleted: false };

    /// <summary>Wind velocity in model axes from a compass bearing the wind blows FROM (0 = north, clockwise).
    /// Same convention as WindFlow.Revit's RevitExtractor.WindVector: TrueNorthToModel(fromDeg + 180) * speed,
    /// where TrueNorthToModel(d) = (sin d, cos d) with model +X = east and +Y = north. <paramref name="northDeg"/> is
    /// <see cref="BuildingModel.NorthDeg"/>: true north lies that far clockwise of model +Y, so the model-axis bearing of a
    /// true bearing d is d + northDeg (0 = model +Y is north).</summary>
    public static Vector2 WindVector(double fromDeg, double speed, double northDeg = 0)
    {
        double th = (fromDeg + 180 + northDeg) * Math.PI / 180;
        return new Vector2((float)Math.Sin(th), (float)Math.Cos(th)) * (float)speed;
    }

    public void Start(BuildingModel model, Vector2 wind, IProgress<double> progress)
    {
        var options = new RunOptions { Quality = Quality.Preview, Backend = Backend.Auto, OpeningModel = MainWindow.openingModelSetting };
        var token = cancel.Token;
        Task = System.Threading.Tasks.Task.Run(() => Simulation.Run(model, wind, options, progress, token), token);
    }

    public void Cancel() => cancel.Cancel();

    /// <summary>One line for the status strip: where it ran, how big, how long, and whether the mass ledger
    /// (the trust gate) closed.</summary>
    public static string Summary(SimulationResult r)
    {
        var g = r.Field.Grid;
        string ledger = r.Ledger.IsPhysical
            ? $"ledger {r.Ledger.Residual * 100:0.#}%"
            : $"ledger {r.Ledger.Residual * 100:0.#}% — not physical";
        return $"{r.Field.Backend} · {g.Count / 1000.0:0.#}k cells ({g.Nx}×{g.Ny}×{g.Nz}, {g.Dx * 1000:0} mm) · " +
               $"{r.Field.Seconds:0.0} s · {ledger}";
    }
}
