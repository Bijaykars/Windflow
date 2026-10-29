// Machine-readable record of every check line, written to out/checks.json at the end of a run, plus the
// `--compare <baseline.json>` delta report. A check line is "[PASS] <id> <name>  <detail>"; every
// `key=value` numeric token in the detail becomes values[key].

using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace WindFlow.Sandbox;

static class CheckLog
{
    public sealed class Entry
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string Status { get; set; } = "";   // PASS, FAIL, WAIT, NOTE
        public bool Pass { get; set; }
        public string Detail { get; set; } = "";
        public Dictionary<string, double> Values { get; set; } = new();
    }

    static readonly List<Entry> entries = new();
    static readonly JsonSerializerOptions json = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    // key=value where value is a plain or exponent number; a trailing unit/percent sign is ignored.
    static readonly Regex KeyValue = new(@"(?<![\w.])([A-Za-z_][\w.\[\]/]*)\s*=\s*([-+]?(?:\d+\.?\d*|\.\d+)(?:[eE][-+]?\d+)?)",
        RegexOptions.Compiled);

    public static void Record(string status, string label, string detail)
    {
        label = label.Trim();
        int sp = label.IndexOf(' ');
        string id = sp < 0 ? label : label[..sp];
        string name = sp < 0 ? "" : label[(sp + 1)..];
        // Same id twice in one run (a branch printing a different label) keeps both: "13a", "13a#2".
        string key = id;
        for (int n = 2; entries.Any(e => e.Id == key); n++) key = $"{id}#{n}";

        var e = new Entry { Id = key, Name = name, Status = status, Pass = status != "FAIL", Detail = detail };
        foreach (Match m in KeyValue.Matches(detail))
        {
            if (!double.TryParse(m.Groups[2].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double v)) continue;
            string k = m.Groups[1].Value;
            string kk = k;
            for (int n = 2; e.Values.ContainsKey(kk); n++) kk = $"{k}#{n}";
            e.Values[kk] = v;
        }
        entries.Add(e);
    }

    public static void Write(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(entries, json));
        Console.WriteLine($"checks: {entries.Count} entries -> {path}");
    }

    /// <summary>Prints status flips first, then every value that moved by more than 0.5 % relative
    /// (with a 1e-6 absolute floor), grouped by check. Checks absent from this run are counted, not listed.</summary>
    public static void Compare(string baselinePath)
    {
        var baseline = JsonSerializer.Deserialize<List<Entry>>(File.ReadAllText(baselinePath), json) ?? new();
        var byId = baseline.ToDictionary(e => e.Id);
        Console.WriteLine();
        Console.WriteLine($"== compare against {baselinePath} ==");

        int flips = 0;
        Console.WriteLine("-- status flips --");
        foreach (var e in entries)
        {
            if (!byId.TryGetValue(e.Id, out var b)) { Console.WriteLine($"  NEW  {e.Id} [{e.Status}] {e.Name}"); continue; }
            if (b.Status == e.Status) continue;
            flips++;
            Console.WriteLine($"  FLIP {e.Id} {b.Status} -> {e.Status}  {e.Name}");
        }
        if (flips == 0) Console.WriteLine("  (none)");

        Console.WriteLine("-- moved values (> 0.5 % rel, > 1e-6 abs) --");
        int moved = 0;
        foreach (var e in entries)
        {
            if (!byId.TryGetValue(e.Id, out var b)) continue;
            var lines = new List<string>();
            foreach (var (k, v) in e.Values)
            {
                if (!b.Values.TryGetValue(k, out double bv)) { lines.Add($"    {k}: (new) {Fmt(v)}"); continue; }
                double d = v - bv;
                if (Math.Abs(d) <= Math.Max(0.005 * Math.Abs(bv), 1e-6)) continue;
                string rel = bv != 0 ? $"{100 * d / Math.Abs(bv):+0.0;-0.0} %" : "n/a";
                lines.Add($"    {k}: {Fmt(bv)} -> {Fmt(v)}  ({rel})");
            }
            foreach (var k in b.Values.Keys.Where(k => !e.Values.ContainsKey(k)))
                lines.Add($"    {k}: {Fmt(b.Values[k])} -> (gone)");
            if (lines.Count == 0) continue;
            moved++;
            Console.WriteLine($"  {e.Id} [{e.Status}] {e.Name}");
            foreach (var l in lines) Console.WriteLine(l);
        }
        if (moved == 0) Console.WriteLine("  (none)");

        int missing = baseline.Count(b => entries.All(e => e.Id != b.Id));
        Console.WriteLine($"-- {flips} flip(s), {moved} check(s) with moved values, {missing} baseline check(s) not run --");
    }

    static string Fmt(double v) => v.ToString("G6", CultureInfo.InvariantCulture);
}
