using System.Globalization;
using System.IO;
using System.Numerics;

namespace WindFlow.Core;

public sealed class EpwHour
{
    public int Month, Day, Hour;        // Hour 1..24 as in the file
    public float DryBulb;               // °C
    public float RelHumidity;           // %
    public float WindDirection;         // degrees the wind comes FROM, 0 = N, 90 = E
    public float WindSpeed;             // m/s at 10 m, at the met station: WindRose.SiteSpeedFactor converts it to the site
}

/// <summary>EnergyPlus weather file (EPW): 8 header lines then 8760 hourly rows. Parsed in-house; the format is a
/// plain CSV documented in the EnergyPlus Auxiliary Programs manual.</summary>
public sealed class Epw
{
    public string City = "", Country = "";
    public float Latitude, Longitude, TimeZone;
    public List<EpwHour> Hours = new();
    public int SkippedRows;             // malformed data rows (< 22 columns or unparseable), not counted

    public static Epw Load(string path)
    {
        var lines = File.ReadAllLines(path);
        var epw = new Epw();

        if (lines.Length > 0)
        {
            var loc = lines[0].Split(',');
            // LOCATION,City,State/Region,Country,Source,WMO,Latitude,Longitude,TimeZone,Elevation
            if (loc.Length > 8)
            {
                epw.City = loc.Length > 1 ? loc[1] : "";
                epw.Country = loc.Length > 3 ? loc[3] : "";
                float.TryParse(loc[6], NumberStyles.Float, CultureInfo.InvariantCulture, out epw.Latitude);
                float.TryParse(loc[7], NumberStyles.Float, CultureInfo.InvariantCulture, out epw.Longitude);
                float.TryParse(loc[8], NumberStyles.Float, CultureInfo.InvariantCulture, out epw.TimeZone);
            }
        }

        for (int i = 8; i < lines.Length; i++)
        {
            var line = lines[i];
            if (string.IsNullOrWhiteSpace(line)) continue;
            var cols = line.Split(',');
            if (cols.Length < 22)
            {
                epw.SkippedRows++;
                continue;
            }

            int month = 0, day = 0, hour = 0;
            float dryBulb = 0, rh = 0, dir = 0, speed = 0;
            bool ok = int.TryParse(cols[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out month)
                && int.TryParse(cols[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out day)
                && int.TryParse(cols[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out hour)
                && float.TryParse(cols[6], NumberStyles.Float, CultureInfo.InvariantCulture, out dryBulb)
                && float.TryParse(cols[8], NumberStyles.Float, CultureInfo.InvariantCulture, out rh)
                && float.TryParse(cols[20], NumberStyles.Float, CultureInfo.InvariantCulture, out dir)
                && float.TryParse(cols[21], NumberStyles.Float, CultureInfo.InvariantCulture, out speed);
            if (!ok)
            {
                epw.SkippedRows++;
                continue;
            }

            if (dir >= 999f || speed >= 999f) continue;   // missing-data codes: drop the hour (not malformed)

            epw.Hours.Add(new EpwHour
            {
                Month = month, Day = day, Hour = hour,
                DryBulb = dryBulb, RelHumidity = rh, WindDirection = dir, WindSpeed = speed,
            });
        }

        return epw;
    }
}

public sealed class WindBin
{
    public int Sector;                  // 0..Sectors-1, sector 0 centred on north, clockwise
    public float DirectionDeg;          // sector centre, wind FROM
    public float SpeedLow, SpeedHigh;   // m/s bin edges
    public float SpeedMid;              // representative speed used for the run
    public int Hours;                   // hours in this bin (after the filter)
}

public static class WindRose
{
    public static readonly float[] DefaultSpeedEdges = { 0.5f, 2f, 4f, 7f, 30f };   // calm below 0.5 is dropped

    /// <summary>EPW speeds are met-station values (open terrain, z0 about 0.05 m, 10 m). Ratio site / station of the mean wind
    /// at <paramref name="height"/> for the same large-scale wind, EN 1991-1-4 eq. 4.3-4.5: v_m = k_r(z0) ln(z/z0) v_b with
    /// k_r = 0.19 (z0/0.05)^0.07 (P13b-06). Multiply an EPW speed by it to get the site's speed at that height.</summary>
    public static float SiteSpeedFactor(float siteZ0, float metZ0 = 0.05f, float height = 10f)
    {
        static double Cr(double z0, double z) => 0.19 * Math.Pow(z0 / 0.05, 0.07) * Math.Log(z / z0);
        return (float)(Cr(siteZ0, height) / Cr(metZ0, height));
    }

    /// <summary>Bins the filtered hours by direction sector and speed. Calm hours (below the first edge) are
    /// returned as a single bin with Sector = -1 so the caller can count them as "no wind".</summary>
    public static List<WindBin> Build(Epw epw, int sectors = 16, float[]? speedEdges = null, Func<EpwHour, bool>? filter = null)
    {
        speedEdges ??= DefaultSpeedEdges;
        float sectorWidth = 360f / sectors;

        var speedSum = new Dictionary<(int Sector, int SpeedBin), double>();
        var hourCount = new Dictionary<(int Sector, int SpeedBin), int>();
        double calmSpeedSum = 0;
        int calmHours = 0;

        foreach (var h in epw.Hours)
        {
            if (filter != null && !filter(h)) continue;

            if (h.WindSpeed < speedEdges[0])
            {
                calmSpeedSum += h.WindSpeed;
                calmHours++;
                continue;
            }

            int sector = (int)MathF.Round(h.WindDirection / sectorWidth) % sectors;
            if (sector < 0) sector += sectors;

            int speedBin = 0;
            for (int k = 0; k < speedEdges.Length - 1; k++)
                if (h.WindSpeed >= speedEdges[k]) speedBin = k;

            var key = (sector, speedBin);
            speedSum[key] = speedSum.GetValueOrDefault(key) + h.WindSpeed;
            hourCount[key] = hourCount.GetValueOrDefault(key) + 1;
        }

        var result = new List<WindBin>();
        if (calmHours > 0)
        {
            result.Add(new WindBin
            {
                Sector = -1, DirectionDeg = float.NaN,
                SpeedLow = 0, SpeedHigh = speedEdges[0],
                SpeedMid = (float)(calmSpeedSum / calmHours),
                Hours = calmHours,
            });
        }

        foreach (var (key, hours) in hourCount)
        {
            if (hours == 0) continue;   // drop empty bins
            float lo = speedEdges[key.SpeedBin], hi = speedEdges[key.SpeedBin + 1];
            result.Add(new WindBin
            {
                Sector = key.Sector,
                DirectionDeg = key.Sector * sectorWidth,
                SpeedLow = lo, SpeedHigh = hi,
                SpeedMid = (float)(speedSum[key] / hours),   // fallback to the midpoint only if hours == 0 (never here)
                Hours = hours,
            });
        }

        result.Sort((a, b) => a.Sector != b.Sector ? a.Sector.CompareTo(b.Sector) : a.SpeedLow.CompareTo(b.SpeedLow));
        return result;
    }
}

public sealed class AnnualRoomResult
{
    public Room Room = new();
    public float VentilatedShare;       // share of binned hours with outdoor-air ACH >= threshold (air entering this room straight from outside)
    public float MeanAch;               // hour-weighted, same outdoor-air ACH
    public float BestDirectionDeg = float.NaN, WorstDirectionDeg = float.NaN;
}

public sealed class AnnualResult
{
    public List<WindBin> Bins = new();
    public List<SimulationResult> Runs = new();     // one per bin, same order
    public List<AnnualRoomResult> Rooms = new();
    public int CalmHours;                           // counted as not ventilated by wind
    /// <summary>Whole building: share of hours with a fresh-air rate (3600 / volume-weighted mean age of air, MassLedger.FreshAch; one age
    /// solve per bin) at or above the threshold, and the hour-weighted mean of that rate. Calm hours count as 0.</summary>
    public float BuildingVentilatedShare, BuildingMeanFreshAch;
    /// <summary>The rooms the building rate leaves out because they are sealed (no air path) in at least one bin run, and the most volume they hold in any run: RW-03's
    /// labels for the annual mean (<see cref="MassLedger.VolumeText"/>).</summary>
    public List<string> SealedRooms = new();
    public float SealedVolume;
    public string VolumeText => Analysis.VolumeText(SealedVolume, SealedRooms);
}

/// <summary>Runs one simulation per wind bin and weights room results by hours. With the GPU each run takes
/// seconds, so 16 sectors × 4 speeds is a coffee break, not a batch job.</summary>
public static class Annual
{
    /// <summary>The rooms sealed (left out of the building rate) in at least one run, and the most sealed volume in any run.</summary>
    public static (List<string> Rooms, float Volume) SealedAcross(IEnumerable<MassLedger> ledgers)
    {
        var rooms = new List<string>(); float volume = 0f;
        foreach (var l in ledgers)
        {
            volume = MathF.Max(volume, l.SealedVolume);
            foreach (var name in l.SealedRooms) if (!rooms.Contains(name)) rooms.Add(name);
        }
        return (rooms, volume);
    }

    /// <param name="windOf">converts a bin (compass FROM direction, speed) to the model-axes wind vector</param>
    public static AnnualResult Run(BuildingModel model, List<WindBin> bins, Func<WindBin, Vector2> windOf, float achThreshold,
        RunOptions options, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        var nonCalm = new List<WindBin>();
        int calmHours = 0;
        foreach (var b in bins)
        {
            if (b.Sector < 0) calmHours += b.Hours;
            else nonCalm.Add(b);
        }

        options = options.Clone();
        options.AgeOfAir = true;   // P15-07: the building fresh-air rate comes from the age of air, solved per bin

        // Sequential: the GPU (or CPU threads) is one device, runs cannot overlap usefully.
        var runs = new List<SimulationResult>(nonCalm.Count);
        for (int i = 0; i < nonCalm.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            runs.Add(Simulation.Run(model, windOf(nonCalm[i]), options, null, ct));
            progress?.Report((i + 1.0) / Math.Max(1, nonCalm.Count));
        }

        var roomIds = new List<string>();
        var roomById = new Dictionary<string, Room>();
        if (runs.Count > 0)
            foreach (var rr in runs[0].Rooms)
                if (roomById.TryAdd(rr.Room.Id, rr.Room)) roomIds.Add(rr.Room.Id);

        int buildingHours = 0; double buildingSum = 0;
        var ventHours = new Dictionary<string, int>();
        var achHourSum = new Dictionary<string, double>();
        var bestAch = new Dictionary<string, float>();
        var worstAch = new Dictionary<string, float>();
        var bestDir = new Dictionary<string, float>();
        var worstDir = new Dictionary<string, float>();
        foreach (var id in roomIds)
        {
            ventHours[id] = 0;
            achHourSum[id] = 0;
            bestAch[id] = float.NegativeInfinity;
            worstAch[id] = float.PositiveInfinity;
            bestDir[id] = float.NaN;
            worstDir[id] = float.NaN;
        }

        int binnedHours = 0;
        for (int i = 0; i < nonCalm.Count; i++)
        {
            var bin = nonCalm[i];
            binnedHours += bin.Hours;
            float building = float.IsNaN(runs[i].Ledger.FreshAch) ? 0f : runs[i].Ledger.FreshAch;
            if (building >= achThreshold) buildingHours += bin.Hours;
            buildingSum += (double)building * bin.Hours;
            foreach (var rr in runs[i].Rooms)
            {
                if (!ventHours.ContainsKey(rr.Room.Id)) continue;
                float ach = rr.OutdoorAch;   // D3: a per-bin age solve is too dear; the room's own outdoor air is what is known
                if (ach >= achThreshold) ventHours[rr.Room.Id] += bin.Hours;
                achHourSum[rr.Room.Id] += (double)ach * bin.Hours;
                if (ach > bestAch[rr.Room.Id]) { bestAch[rr.Room.Id] = ach; bestDir[rr.Room.Id] = bin.DirectionDeg; }
                if (ach < worstAch[rr.Room.Id]) { worstAch[rr.Room.Id] = ach; worstDir[rr.Room.Id] = bin.DirectionDeg; }
            }
        }

        int totalHours = binnedHours + calmHours;   // calm hours count as "not ventilated" in the denominator
        var roomResults = new List<AnnualRoomResult>();
        foreach (var id in roomIds)
        {
            roomResults.Add(new AnnualRoomResult
            {
                Room = roomById[id],
                VentilatedShare = totalHours > 0 ? (float)ventHours[id] / totalHours : 0f,
                MeanAch = totalHours > 0 ? (float)(achHourSum[id] / totalHours) : 0f,   // calm hours count as 0 ACH, same denominator as VentilatedShare
                BestDirectionDeg = bestDir[id],
                WorstDirectionDeg = worstDir[id],
            });
        }

        var (sealedRooms, sealedVolume) = SealedAcross(runs.Select(r => r.Ledger));

        progress?.Report(1);
        return new AnnualResult
        {
            Bins = bins, Runs = runs, Rooms = roomResults, CalmHours = calmHours, SealedRooms = sealedRooms, SealedVolume = sealedVolume,
            BuildingVentilatedShare = totalHours > 0 ? (float)buildingHours / totalHours : 0f,
            BuildingMeanFreshAch = totalHours > 0 ? (float)(buildingSum / totalHours) : 0f,
        };
    }
}
