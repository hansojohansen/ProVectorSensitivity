namespace OpenVector.Core.Analysis;

public sealed record ThroughputCell(int Sector, int AmplitudeIndex, int N, double We, double Ae, double IDe, double MtS, double Tp);
public sealed record SectorThroughput(int Sector, double Theta, int N, double? Tp);
public sealed record ThroughputResult(double? OverallTp, IReadOnlyList<SectorThroughput> Sectors, IReadOnlyList<ThroughputCell> Cells);

/// <summary>
/// Per-direction effective throughput (ISO 9241-411; Soukoreff &amp; MacKenzie 2004), per direction sector
/// (default 8, centred on 2πs/8) × amplitude condition:
/// dx = (F−S)·u − A (final endpoint, counts); We = 4.133·SD(dx); Ae = mean((F−S)·u); IDe = log2(Ae/We + 1);
/// MT = mean(final click − onset) in s; TP = IDe/MT. Sector TP = mean of its condition TPs; overall = mean of all cells.
/// Cells need ≥ minTrials trials and SD &gt; 0.
/// </summary>
public static class FittsThroughput
{
    public static ThroughputResult Compute(IEnumerable<TrialMetrics?> metrics, int directionSectors = 8, int minTrials = 3)
    {
        var cells = new SortedDictionary<(int, int), List<(double Dev, double Eff, double Mt)>>();
        foreach (var m in metrics)
        {
            if (m is null || m.AlongFinal is not { } af || m.MovementTimeUs is not { } mt || mt <= 0) continue;
            var key = (Angles.SectorOf(m.Theta, directionSectors), m.AmplitudeIndex);
            if (!cells.TryGetValue(key, out var list)) cells[key] = list = [];
            list.Add((af * m.AmplitudeCounts, (1.0 + af) * m.AmplitudeCounts, mt * 1e-6));
        }
        var outCells = new List<ThroughputCell>();
        foreach (var ((sec, amp), v) in cells)
        {
            if (v.Count < minTrials) continue;
            double sd = Stats.StdDev(v.Select(x => x.Dev).ToArray());
            if (!(sd > 0)) continue;
            double we = 4.133 * sd;
            double ae = Stats.Mean(v.Select(x => x.Eff).ToArray());
            double ide = Math.Log2(ae / we + 1.0);
            double mtS = Stats.Mean(v.Select(x => x.Mt).ToArray());
            outCells.Add(new ThroughputCell(sec, amp, v.Count, we, ae, ide, mtS, ide / mtS));
        }
        var sectors = Enumerable.Range(0, directionSectors).Select(s =>
        {
            var cs = outCells.Where(c => c.Sector == s).ToArray();
            return new SectorThroughput(s, Angles.SectorCenter(s, directionSectors), cs.Sum(c => c.N),
                cs.Length > 0 ? Stats.Mean(cs.Select(c => c.Tp).ToArray()) : null);
        }).ToArray();
        double? overall = outCells.Count > 0 ? Stats.Mean(outCells.Select(c => c.Tp).ToArray()) : null;
        return new ThroughputResult(overall, sectors, outCells);
    }
}
