using System.Text.Json.Nodes;
using OpenVector.Core.Analysis;
using OpenVector.Core.Data;
using OpenVector.Core.Json;
using OpenVector.Core.Profiles;

namespace OpenVector.Core.Tests;

/// <summary>The C# analysis must match the Python reference (reference/python/gen_golden.py).</summary>
public class GoldenTests
{
    private const double Tol = 1e-9;

    private static void Close(double expected, double actual, double tol = Tol, string what = "") =>
        Assert.True(Math.Abs(expected - actual) <= tol * Math.Max(1.0, Math.Abs(expected)),
            $"{what}: expected {expected:R}, got {actual:R}");

    private static void Close(JsonNode? expected, double? actual, double tol = Tol, string what = "")
    {
        if (expected is null) { Assert.Null(actual); return; }
        Assert.NotNull(actual);
        Close(expected.GetValue<double>(), actual!.Value, tol, what);
    }

    [Fact]
    public void SplitMix64MatchesReferenceValues()
    {
        var r = new SplitMix64(0);
        Assert.Equal(0xE220A8397B1DCDAFUL, r.NextUInt64());
        Assert.Equal(0x6E789E6AA1B965F4UL, r.NextUInt64());
    }

    [Fact]
    public void TrialPlanMatchesPython()
    {
        var g = TestFiles.Golden("plan.json");
        var plan = TrialPlanGenerator.Generate(new TrialPlanOptions
        {
            Seed = g["seed"]!.GetValue<ulong>(), Sectors = g["sectors"]!.GetValue<int>(),
            Amplitudes = TestFiles.Doubles(g["amplitudes"]), RepsPerCell = g["reps"]!.GetValue<int>(),
        });
        var exp = g["trials"]!.AsArray();
        Assert.Equal(exp.Count, plan.Count);
        for (int i = 0; i < plan.Count; i++)
        {
            Assert.Equal(exp[i]!["sector"]!.GetValue<int>(), plan[i].Sector);
            Assert.Equal(exp[i]!["amplitude_index"]!.GetValue<int>(), plan[i].AmplitudeIndex);
            Assert.Equal(exp[i]!["theta"]!.GetValue<double>(), plan[i].Theta); // bit-exact
        }
    }

    [Fact]
    public void SegmentationMatchesPython()
    {
        var cases = TestFiles.Golden("segmentation.json")["cases"]!.AsArray();
        Assert.NotEmpty(cases);
        foreach (var c in cases)
        {
            var path = c!["path"]!;
            long end = c["t_end_us"]!.GetValue<long>();
            var t = path["t_us"]!.AsArray().Select(x => x!.GetValue<long>()).ToArray();
            int m = t.TakeWhile(x => x <= end).Count();
            var dx = path["dx"]!.AsArray().Select(x => x!.GetValue<int>()).Take(m).ToArray();
            var dy = path["dy"]!.AsArray().Select(x => x!.GetValue<int>()).Take(m).ToArray();
            var r = Segmenter.Segment(t.AsSpan(0, m), dx, dy, c["t_ref_us"]!.GetValue<long>(), c["direction"]!.GetValue<double>());
            var e = c["expected"]!;
            Assert.NotNull(r);
            Assert.Equal(e["t_onset_us"]!.GetValue<long>(), r!.TOnsetUs);
            Assert.Equal(e["t_peak_us"]!.GetValue<long>(), r.TPeakUs);
            Assert.Equal(e["t_primary_end_us"]!.GetValue<long>(), r.TPrimaryEndUs);
            Assert.Equal(e["t_end_us"]!.GetValue<long>(), r.TEndUs);
            Close(e["peak_speed"]!.GetValue<double>(), r.PeakSpeed, 1e-9, "peak");
            var pe = TestFiles.Doubles(e["primary_end"]);
            Close(pe[0], r.PrimaryEndX, 1e-9, "pex"); Close(pe[1], r.PrimaryEndY, 1e-9, "pey");
            var fe = TestFiles.Doubles(e["final_end"]);
            Close(fe[0], r.FinalEndX, 1e-9, "fex"); Close(fe[1], r.FinalEndY, 1e-9, "fey");
            Close(e["path_length"]!.GetValue<double>(), r.PathLength, 1e-9, "path");
            var subs = e["submovements"]!.AsArray();
            Assert.Equal(subs.Count, r.Submovements.Count);
            for (int i = 0; i < subs.Count; i++)
            {
                Assert.Equal(subs[i]!["t_start_us"]!.GetValue<long>(), r.Submovements[i].TStartUs);
                Assert.Equal(subs[i]!["t_end_us"]!.GetValue<long>(), r.Submovements[i].TEndUs);
                Close(subs[i]!["dx"]!.GetValue<double>(), r.Submovements[i].Dx, 1e-9, "sdx");
            }
            // The truth (generator) agrees with the segmentation on the number of submovements.
            Assert.Equal(c["truth"]!["n_submovements"]!.GetValue<int>(), r.Submovements.Count);
        }
    }

    public static TheoryData<string> FitCases => new() { "fit_rich.json", "fit_sparse.json" };

    private static (JsonNode Golden, List<MovementTrial> Trials, GainFitOptions Options) LoadFit(string file)
    {
        var g = TestFiles.Golden(file);
        var trials = g["trials"]!.AsArray().Select(t => OpenVectorJson.Deserialize<MovementTrial>(t!.ToJsonString())).ToList();
        var o = g["options"]!;
        var opts = new GainFitOptions
        {
            MaxOrder = o["max_order"]!.GetValue<int>(), Lambda = o["lam"]!.GetValue<double>(), Strata = o["strata"]!.GetValue<int>(),
            MinTrialsPerStratum = o["min_per_stratum"]!.GetValue<double>(), NeighbourReach = o["neighbour_reach"]!.GetValue<int>(),
            Folds = o["folds"]!.GetValue<int>(),
            Bounds = new GainBounds { MinGain = o["min_gain"]!.GetValue<double>(), MaxGain = o["max_gain"]!.GetValue<double>() },
            MaxAbsAlongError = o["max_abs_along_error"]!.GetValue<double>(), BootstrapReps = o["bootstrap_reps"]!.GetValue<int>(),
            Seed = o["seed"]!.GetValue<ulong>(), DisplayBins = o["display_bins"]!.GetValue<int>(),
            OrderRule = o["order_rule"]!.GetValue<string>() == "1se" ? OrderRule.OneStandardError : OrderRule.MinCv,
        };
        return (g, trials, opts);
    }

    [Theory]
    [MemberData(nameof(FitCases))]
    public void GainFitMatchesPython(string file)
    {
        var (g, trials, opts) = LoadFit(file);
        var exp = g["expected"]!["fit"]!;
        var fit = GainMapEstimator.Fit(trials, opts);

        Assert.Equal(exp["order"]!.GetValue<int>(), fit.Order);
        Assert.Equal(exp["n_used"]!.GetValue<int>(), fit.NUsed);
        Assert.Equal(exp["n_trials"]!.GetValue<int>(), fit.NTrials);
        Assert.Equal(exp["sparse_strata"]!.AsArray().Select(x => x!.GetValue<int>()), fit.SparseStrata);
        var coeffs = TestFiles.Doubles(exp["coeffs"]);
        Assert.Equal(coeffs.Length, fit.Coeffs.Length);
        for (int i = 0; i < coeffs.Length; i++) Close(coeffs[i], fit.Coeffs[i], 1e-10, $"c{i}");
        var cv = TestFiles.Doubles(exp["cv_scores"]);
        for (int i = 0; i < cv.Length; i++) Close(cv[i], fit.CvScores[i], 1e-10, $"cv{i}");
        Close(exp["residual_sd"]!.GetValue<double>(), fit.ResidualSd, 1e-10, "sd");
        var cov = exp["cov"]!.AsArray();
        for (int i = 0; i < cov.Count; i++)
        {
            var row = TestFiles.Doubles(cov[i]);
            for (int j = 0; j < row.Length; j++) Close(row[j], fit.Cov[i][j], 1e-8, $"cov{i},{j}");
        }
        var bins = exp["bins"]!.AsArray();
        Assert.Equal(bins.Count, fit.Bins.Length);
        for (int k = 0; k < bins.Count; k++)
        {
            var b = bins[k]!;
            Close(b["theta"]!.GetValue<double>(), fit.Bins[k].Theta, 0, "theta");
            Close(b["gain"]!.GetValue<double>(), fit.Bins[k].Gain, 1e-10, $"gain{k}");
            Close(b["ci_low"]!.GetValue<double>(), fit.Bins[k].CiLow, 1e-9, $"lo{k}");
            Close(b["ci_high"]!.GetValue<double>(), fit.Bins[k].CiHigh, 1e-9, $"hi{k}");
            Assert.Equal(b["n"]!.GetValue<int>(), fit.Bins[k].N);
        }
    }

    [Theory]
    [MemberData(nameof(FitCases))]
    public void ProjectionSamplesMatchPython(string file)
    {
        var (g, _, opts) = LoadFit(file);
        var exp = g["expected"]!;
        var fitNode = exp["fit"]!;
        var model = new GainModel { Order = fitNode["order"]!.GetValue<int>(), Coeffs = TestFiles.Doubles(fitNode["coeffs"]) };
        var bins = fitNode["bins"]!.AsArray().Select(b => new DisplayBin
        {
            Theta = b!["theta"]!.GetValue<double>(), Gain = b["gain"]!.GetValue<double>(), CiLow = b["ci_low"]!.GetValue<double>(),
            CiHigh = b["ci_high"]!.GetValue<double>(), N = b["n"]!.GetValue<int>(),
        }).ToArray();
        var ps = exp["projection_samples"]!;
        var s = ProjectionSampler.Sample(model, opts.Bounds, bins, ps["n"]!.GetValue<int>());
        var th = TestFiles.Doubles(ps["theta"]); var ga = TestFiles.Doubles(ps["gain"]); var we = TestFiles.Doubles(ps["weight"]);
        for (int j = 0; j < th.Length; j++)
        {
            Close(th[j], s.Theta[j], 1e-15, "theta");
            Close(ga[j], s.Gain[j], 1e-10, "gain");
            Close(we[j], s.Weight[j], 1e-10, "weight");
        }
        Close(1.0, s.Weight.Average(), 1e-12, "mean weight");
    }

    [Fact]
    public void TrialMetricsThroughputAndSequentialMatchPython()
    {
        var (g, trials, opts) = LoadFit("fit_rich.json");
        var exp = g["expected"]!;
        var metrics = trials.Select(TrialMetrics.Compute).ToList();
        var em = exp["metrics_first20"]!.AsArray();
        for (int i = 0; i < em.Count; i++)
        {
            var e = em[i]!; var m = metrics[i]!;
            Close(e["theta"], m.Theta); Close(e["amplitude_counts"], m.AmplitudeCounts);
            Close(e["along_primary"], m.AlongPrimary); Close(e["across_primary"], m.AcrossPrimary);
            Close(e["along_final"], m.AlongFinal); Close(e["across_final"], m.AcrossFinal);
            Close(e["overshoot_ratio"], m.OvershootRatio); Close(e["path_efficiency"], m.PathEfficiency);
            Assert.Equal(e["movement_time_us"]!.GetValue<long>(), m.MovementTimeUs);
            Assert.Equal(e["primary_duration_us"]!.GetValue<long>(), m.PrimaryDurationUs);
            Assert.Equal(e["n_corrections"]!.GetValue<int>(), m.NCorrections);
        }

        var tp = FittsThroughput.Compute(metrics);
        var et = exp["throughput"]!;
        Close(et["overall_tp"], tp.OverallTp);
        var cells = et["cells"]!.AsArray();
        Assert.Equal(cells.Count, tp.Cells.Count);
        for (int i = 0; i < cells.Count; i++)
        {
            Assert.Equal(cells[i]!["n"]!.GetValue<int>(), tp.Cells[i].N);
            Close(cells[i]!["we"], tp.Cells[i].We); Close(cells[i]!["tp"], tp.Cells[i].Tp);
        }
        var es = et["sectors"]!.AsArray();
        for (int s = 0; s < es.Count; s++) Close(es[s]!["tp"], tp.Sectors[s].Tp);

        var fit = GainMapEstimator.Fit(trials, opts with { BootstrapReps = 0 });
        var seq = SequentialEffects.Compute(trials, fit.Model);
        var eq = exp["sequential"]!;
        foreach (var (name, actual) in new[] { ("same", seq.Same), ("orthogonal", seq.Orthogonal), ("reversal", seq.Reversal) })
        {
            Assert.Equal(eq["classes"]![name]!["n"]!.GetValue<int>(), actual.N);
            Close(eq["classes"]![name]!["mean"], actual.Mean, 1e-9, name);
            Close(eq["classes"]![name]!["se"], actual.Se, 1e-9, name);
        }
        Close(eq["reversal_minus_same"]!["diff"], seq.ReversalMinusSame!.Diff, 1e-9);
        var bd = eq["by_delta"]!.AsArray();
        for (int k = 0; k < bd.Count; k++)
        {
            Assert.Equal(bd[k]!["n"]!.GetValue<int>(), seq.ByDelta[k].N);
            Close(bd[k]!["mean"], seq.ByDelta[k].Mean, 1e-9);
        }
        var rt = eq["reaction_time"]!.AsArray();
        for (int k = 0; k < rt.Count; k++)
        {
            Assert.Equal(rt[k]!["n"]!.GetValue<int>(), seq.ReactionTime[k].N);
            Close(rt[k]!["mean_us"], seq.ReactionTime[k].MeanUs);
            Close(rt[k]!["median_us"], seq.ReactionTime[k].MedianUs);
        }
    }
}
