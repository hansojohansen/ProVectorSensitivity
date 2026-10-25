namespace OpenVector.RawAccel.Projection;

/// <summary>
/// Sampled target directional gain (D14): θ in radians (atan2(dy, dx) in raw-count coordinates, 0 = right,
/// π/2 = down), gain relative to the user's base sensitivity (identity 1.0), optional non-negative weights
/// (e.g. angle-occurrence density). This is the only input contract with OpenVector.Core.
/// </summary>
public sealed record ProjectionTarget(IReadOnlyList<double> Theta, IReadOnlyList<double> Gain, IReadOnlyList<double>? Weight = null)
{
    public void Validate()
    {
        if (Theta.Count == 0) throw new ArgumentException("Target has no samples.");
        if (Gain.Count != Theta.Count) throw new ArgumentException("theta and gain lengths differ.");
        if (Weight is not null && Weight.Count != Theta.Count) throw new ArgumentException("weight length differs from theta.");
        for (int i = 0; i < Theta.Count; i++)
        {
            if (!double.IsFinite(Theta[i])) throw new ArgumentException($"theta[{i}] is not finite.");
            if (!(Gain[i] > 0) || !double.IsFinite(Gain[i])) throw new ArgumentException($"gain[{i}] must be finite and > 0.");
            if (Weight is not null && (!(Weight[i] >= 0) || !double.IsFinite(Weight[i]))) throw new ArgumentException($"weight[{i}] must be finite and >= 0.");
        }
        if (Weight is not null && Weight.Sum() <= 0) throw new ArgumentException("All weights are zero.");
    }

    /// <summary>Uniform samples of a function g(θ) on [0, 2π).</summary>
    public static ProjectionTarget Sample(Func<double, double> gain, int count = 360)
    {
        var th = new double[count]; var g = new double[count];
        for (int i = 0; i < count; i++) { th[i] = 2 * Math.PI * i / count; g[i] = gain(th[i]); }
        return new ProjectionTarget(th, g);
    }
}

/// <summary>Which knobs the fit may use.</summary>
public enum ProjectionMode
{
    /// <summary>Use the vertical shape factor κ when the baseline allows it, else fall back to <see cref="AxisOnly"/>.</summary>
    Auto,
    /// <summary>All 5 DOF (G0, κ, ρ, L, U). Fails if the baseline has its own accel curve.</summary>
    Full,
    /// <summary>Only G0, ρ, L, U. Used when the user already has a Raw Accel accel curve (D4).</summary>
    AxisOnly,
}

public sealed record ProjectionOptions
{
    /// <summary>Direction-tilt penalty λ (log-gain units per radian), A §2.4 step 3. Default 1.</summary>
    public double DirectionPenalty { get; init; } = 1.0;
    public ProjectionMode Mode { get; init; } = ProjectionMode.Auto;
    /// <summary>Bounds on the multipliers ρ, L, U (relative to baseline). A §2.4 suggests 0.5..2.</summary>
    public double MinRatio { get; init; } = 0.5;
    public double MaxRatio { get; init; } = 2.0;
    /// <summary>Bounds on κ = k(90°)/k(0°). With c = 2 every κ in [0.5, 2] maps to valid range weights.</summary>
    public double MinKappa { get; init; } = 0.5;
    public double MaxKappa { get; init; } = 2.0;
    public double MinG0 { get; init; } = 0.25;
    public double MaxG0 { get; init; } = 4.0;
    public int MaxIterations { get; init; } = 200;

    public static ProjectionOptions Default { get; } = new();
}

/// <summary>
/// The user's pre-OpenVector state of the knobs the projection composes with. All multipliers in
/// <see cref="DirectionalParams"/> are relative to this baseline, so a user-chosen Y/X ratio or rotation is kept.
/// </summary>
public sealed record ProjectionBaseline
{
    public double YxRatio { get; init; } = 1;
    public double LrRatio { get; init; } = 1;
    public double UdRatio { get; init; } = 1;
    /// <summary>User rotation (degrees). Applied before everything by the driver; the fit evaluates at θ + α.</summary>
    public double RotationDeg { get; init; }
    /// <summary>False when the user has their own accel curve (or by-component curves), so κ is unavailable.</summary>
    public bool ShapeAvailable { get; init; } = true;
    public string? ShapeUnavailableReason { get; init; }
    /// <summary>Angle snapping is not modelled by the fit; reported as a warning.</summary>
    public double SnapDeg { get; init; }

    public static ProjectionBaseline Identity { get; } = new();
}

/// <summary>
/// Reduced Raw Accel parameters (A §2.1), as multipliers relative to <see cref="ProjectionBaseline"/>:
/// G0 = overall gain at 0°, κ = k(90°)/k(0°) (range weights + constant LUT), ρ = Y/X, L = left, U = up.
/// </summary>
public sealed record DirectionalParams(double G0, double Kappa, double Rho, double L, double U)
{
    public static DirectionalParams Identity { get; } = new(1, 1, 1, 1, 1);
    public double[] ToArray() => new[] { G0, Kappa, Rho, L, U };
}

public sealed record ProjectionResidualCurve(
    IReadOnlyList<double> Theta,
    IReadOnlyList<double> TargetGain,
    IReadOnlyList<double> AppliedGain,
    /// <summary>applied/target − 1.</summary>
    IReadOnlyList<double> RelativeResidual,
    /// <summary>Output-direction change ψ − θ added by the profile, degrees.</summary>
    IReadOnlyList<double> TiltDeg);

public sealed record ProjectionResult
{
    public required string BackendId { get; init; }
    public required DirectionalParams Params { get; init; }
    public required ProjectionBaseline Baseline { get; init; }
    public required ProjectionOptions Options { get; init; }
    /// <summary>True when κ was not used (fixed at 1).</summary>
    public bool AxisOnly { get; init; }
    public required ProjectionResidualCurve Residual { get; init; }
    /// <summary>Weighted RMS of applied/target − 1.</summary>
    public double RmsResidual { get; init; }
    public double MaxAbsResidual { get; init; }
    /// <summary>Weighted RMS relative deviation of the target from its best constant gain (the "bias").</summary>
    public double BiasRms { get; init; }
    /// <summary>1 − SS_res/SS_tot of log gain about its weighted mean (1 if the target is constant).</summary>
    public double ExplainedVariance { get; init; }
    /// <summary>1 − RmsResidual/BiasRms, clamped to [0, 1]: share of the RMS bias the applied profile removes.</summary>
    public double FractionOfBiasApplied { get; init; }
    public double MaxTiltDeg { get; init; }
    public int Iterations { get; init; }
    public bool Converged { get; init; }
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
}

/// <summary>
/// A projection backend compiles a sampled target g(θ) into one input layer's parameters (D2, D14).
/// Proposed cross-module interface; Core adapts its profile model to <see cref="ProjectionTarget"/>.
/// </summary>
public interface IProjectionBackend
{
    string Id { get; }
    ProjectionResult Project(ProjectionTarget target, ProjectionOptions? options = null, ProjectionBaseline? baseline = null);
}
