using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace OpenVector.Core.Profiles;

// C# view of schemas/profile.schema.json (v1): the canonical, hardware-agnostic directional gain profile.
// Only projection backends interpret Projections[backendId].Params (ARCHITECTURE ownership rule).

public enum ValidationStatus { None, Pending, Passed, Failed, Inconclusive }

public sealed record Profile
{
    public const string CurrentSchemaVersion = "1.0.0";
    public const string D14AngleConvention = "atan2_dy_dx_counts_rad";

    public string SchemaVersion { get; init; } = CurrentSchemaVersion;
    public required string Id { get; init; }
    public string? ParentId { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public string? Name { get; init; }
    public string AngleConvention { get; init; } = D14AngleConvention;
    public required ProfileMapping Mapping { get; init; }
    public required GainModel Model { get; init; }
    public required GainBounds Bounds { get; init; }
    public required DisplayBin[] Bins { get; init; }
    public required string[] Sessions { get; init; }
    public FitDiagnostics? Fit { get; init; }
    public Dictionary<string, ProjectionEntry> Projections { get; init; } = new();
    public ValidationResult? Validation { get; init; }

    /// <summary>Clamped gain g(theta) for movement direction theta (D14, radians).</summary>
    public double Gain(double theta) => Model.Gain(theta, Bounds);

    /// <summary>
    /// Sampled target for projection backends (D14): theta_j = 2*pi*j/n, gain_j = g(theta_j), and
    /// weight_j = trial density of the calibration data around theta_j — a von Mises kernel
    /// (kappa = 26, about one 32-sector SD) over the display-bin counts, normalised to mean 1 with a
    /// 5 % floor, w = (d/mean(d) + 0.05) / 1.05, so no direction is completely unconstrained.
    /// All weights are 1 when the bins carry no counts.
    /// </summary>
    public ProjectionSamples SampleForProjection(int n) => ProjectionSampler.Sample(Model, Bounds, Bins, n);
}

public sealed record ProfileMapping
{
    public double? Dpi { get; init; }
    public required double YawDegPerCount { get; init; }
    public required double PitchDegPerCount { get; init; }
    public double? CmPer360 { get; init; }
    public double? FovHDeg { get; init; }
    public double? PollingHz { get; init; }
}

/// <summary>
/// Canonical model: f(theta) = c0 + sum_k (c[2k−1] cos kθ + c[2k] sin kθ) is the user's relative
/// along-axis primary-endpoint bias b(theta); gain g = clamp(1 / (1 + f), bounds).
/// </summary>
public sealed record GainModel
{
    public string Type { get; init; } = "fourier";
    public string Quantity { get; init; } = "relative_along_bias";
    public string Link { get; init; } = "inverse_one_plus";
    public required int Order { get; init; }
    public required double[] Coeffs { get; init; }
    public double[][]? Cov { get; init; }
    /// <summary>Phase 2 placeholder (D12). v1 refuses to evaluate a model that has one.</summary>
    public SpeedDimension? Speed { get; init; }

    public double Bias(double theta)
    {
        if (Speed is not null)
            throw new NotSupportedException("Profile has a speed dimension; v1 Core evaluates g(theta) only.");
        if (Type != "fourier" || Quantity != "relative_along_bias" || Link != "inverse_one_plus")
            throw new NotSupportedException($"Unsupported model {Type}/{Quantity}/{Link}.");
        return FourierSeries.Evaluate(Coeffs, theta);
    }

    public double Gain(double theta, GainBounds bounds) => GainFromBias(Bias(theta), bounds);

    public static double GainFromBias(double bias, GainBounds bounds)
    {
        double den = 1.0 + bias;
        double g = den > 0 ? 1.0 / den : double.PositiveInfinity;
        return Math.Clamp(g, bounds.MinGain, bounds.MaxGain);
    }
}

public sealed record SpeedDimension
{
    public string Type { get; init; } = "bspline_ln_speed";
    public string? SpeedUnit { get; init; }
    public required int Degree { get; init; }
    public required double[] KnotsLnSpeed { get; init; }
    [JsonPropertyName("coeffs_2d")]
    public required double[][] Coeffs2D { get; init; }
}

public sealed record GainBounds
{
    public required double MinGain { get; init; }
    public required double MaxGain { get; init; }

    /// <summary>D9 default: ±15 %.</summary>
    public static GainBounds Default { get; } = new() { MinGain = 0.85, MaxGain = 1.15 };
}

public sealed record DisplayBin
{
    public required double Theta { get; init; }
    public required double Gain { get; init; }
    public required double CiLow { get; init; }
    public required double CiHigh { get; init; }
    public required int N { get; init; }
}

public sealed record FitDiagnostics
{
    public string? Method { get; init; }
    public string? Signal { get; init; }
    public int? NTrials { get; init; }
    public int? NUsed { get; init; }
    public int? OrderSelected { get; init; }
    public double? Lambda { get; init; }
    public double? ResidualSd { get; init; }
    public int? Strata { get; init; }
    public double? MinTrialsPerStratum { get; init; }
    public int[]? SparseStrata { get; init; }
    public double[]? CvScores { get; init; }
    public int? BootstrapReps { get; init; }
    public long? Seed { get; init; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; init; }
}

/// <summary>One backend's compiled parameters (opaque to Core) plus common residual metrics.</summary>
public sealed record ProjectionEntry
{
    public required JsonObject Params { get; init; }
    public DateTimeOffset? CreatedAt { get; init; }
    public double? ResidualRms { get; init; }
    public double? ExplainedVar { get; init; }
    public double? MaxTiltDeg { get; init; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; init; }
}

/// <summary>A/B validation result (D8). Placeholder in v1.</summary>
public sealed record ValidationResult
{
    public required ValidationStatus Status { get; init; }
    public string? Backend { get; init; }
    public string? Metric { get; init; }
    public double? Effect { get; init; }
    public double? CiLow { get; init; }
    public double? CiHigh { get; init; }
    public string[]? Sessions { get; init; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; init; }
}

/// <summary>D14 projection input: parallel arrays, no Core types needed by the backend.</summary>
public sealed record ProjectionSamples(double[] Theta, double[] Gain, double[] Weight);
