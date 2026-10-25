using System.Text.Json.Serialization;

namespace OpenVector.Core.Data;

// C# view of schemas/movement-trial.schema.json (v1). Property names map to snake_case JSON via
// OpenVector.Core.Json.OpenVectorJson. Positions are cumulative raw counts relative to the trial
// origin (x right, y down, D14); times are QPC-derived microseconds. Per D15 this record is a
// derived view of the session streams.

public enum SourceKind { Builtin, KovaaksHybrid, Osu, Aimlabs, Synthetic, Other }
public enum TargetTruth { Measured, Inferred, Unknown }
public enum CountsStage { PostDriver, PreDriver }
public enum TargetShape { Sphere, Disc, Box, Other }
public enum ReactionTimeBasis { OnScreen, Requested }
public enum ExcludedReason { Miss, Pause, Chained, SegmentationAmbiguous, Outlier, Other }

public sealed record MovementTrial
{
    public const string CurrentSchemaVersion = "1.0.0";

    public string SchemaVersion { get; init; } = CurrentSchemaVersion;
    public required string TrialId { get; init; }
    public required string SessionId { get; init; }
    public required TrialSource Source { get; init; }
    public required Provenance Provenance { get; init; }
    public DeviceInfo? Device { get; init; }
    public required TrialMapping Mapping { get; init; }
    public string? ActiveGainMapId { get; init; }
    public TrialCondition? Condition { get; init; }
    public TrialContext? Context { get; init; }
    public required TrialGeometry Geometry { get; init; }
    public required TrialOutcome Outcome { get; init; }
    public required TrialTiming Timing { get; init; }
    public required TrialKinematics Kinematics { get; init; }
    public RawPath? Path { get; init; }
    public string? PathRef { get; init; }
    public ExcludedReason? ExcludedReason { get; init; }
    public string? Notes { get; init; }
    public Derivation? Derivation { get; init; }
}

public sealed record TrialSource
{
    public required SourceKind Kind { get; init; }
    public string? Version { get; init; }
    public string? ScenarioId { get; init; }
    public string? ProtocolId { get; init; }
}

public sealed record Provenance
{
    public required TargetTruth TargetTruth { get; init; }
    public string? InferenceMethod { get; init; }
    /// <summary>Fitter weight multiplier in [0, 1]; builtin trials use 1.</summary>
    public required double Confidence { get; init; }
}

public sealed record DeviceInfo
{
    public int? Vid { get; init; }
    public int? Pid { get; init; }
    public string? Name { get; init; }
    public double? PollingHzNominal { get; init; }
    public double? PollingHzMeasured { get; init; }
    public double? Dpi { get; init; }
}

public sealed record TrialMapping
{
    public required double YawDegPerCount { get; init; }
    public required double PitchDegPerCount { get; init; }
    public double? FovHDeg { get; init; }
    public string? FovScaling { get; init; }
    public int[]? Resolution { get; init; }
    public double? Aspect { get; init; }
    public CountsStage? CountsStage { get; init; }
}

public sealed record TrialCondition
{
    public int? TrialIndex { get; init; }
    public int? Block { get; init; }
    public int? Sector { get; init; }
    public int? SectorCount { get; init; }
    public int? AmplitudeIndex { get; init; }
    public int? WidthIndex { get; init; }
}

/// <summary>Per-trial sequential/session context (D15). Diagnostic; never applied by a static profile.</summary>
public sealed record TrialContext
{
    public int? TrialIndexSession { get; init; }
    public int? TrialIndexBlock { get; init; }
    public int? Block { get; init; }
    public PreviousTrial? Previous { get; init; }
    public long? InterTrialIntervalUs { get; init; }
    public long? TimeSinceBreakUs { get; init; }
    public long? TimeSinceSessionStartUs { get; init; }
}

public sealed record PreviousTrial
{
    public string? TrialId { get; init; }
    public required double DirectionTheta { get; init; }
    public required double AmplitudeCounts { get; init; }
    public bool? Hit { get; init; }
    public string? ExcludedReason { get; init; }
    public double? AlongPrimary { get; init; }
}

public sealed record CountPoint
{
    public required double XCounts { get; init; }
    /// <summary>Positive = down (D14).</summary>
    public required double YCounts { get; init; }
    public double? YawDeg { get; init; }
    /// <summary>Positive = up (game convention); informational.</summary>
    public double? PitchDeg { get; init; }

    public static CountPoint At(double x, double y) => new() { XCounts = x, YCounts = y };
}

public sealed record TargetPoint
{
    public required double XCounts { get; init; }
    public required double YCounts { get; init; }
    public double? YawDeg { get; init; }
    public double? PitchDeg { get; init; }
    public required double RadiusCounts { get; init; }
    public double? RadiusDeg { get; init; }
    public TargetShape? Shape { get; init; }
    public double? VelocityXCountsPerS { get; init; }
    public double? VelocityYCountsPerS { get; init; }
}

public sealed record TrialGeometry
{
    public required CountPoint Start { get; init; }
    public required TargetPoint Target { get; init; }
    /// <summary>|target − start| in counts (D12).</summary>
    public required double AmplitudeCounts { get; init; }
    public double? AmplitudeDeg { get; init; }
    /// <summary>atan2(target − start) in count space, radians (D14).</summary>
    public required double DirectionTheta { get; init; }
}

public sealed record TrialOutcome
{
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public CountPoint? EndpointPrimary { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public CountPoint? EndpointFinal { get; init; }
    public bool? Hit { get; init; }
    public int? ShotsFired { get; init; }
    public Click[]? Clicks { get; init; }
    /// <summary>Derived, informational; Core recomputes these.</summary>
    public TrialErrors? Errors { get; init; }
}

public sealed record Click
{
    public required long TUs { get; init; }
    public required double XCounts { get; init; }
    public required double YCounts { get; init; }
    public bool? Hit { get; init; }
}

public sealed record TrialErrors
{
    public double? AlongPrimary { get; init; }
    public double? AcrossPrimary { get; init; }
    public double? AlongFinal { get; init; }
    public double? AcrossFinal { get; init; }
}

public sealed record TrialTiming
{
    public string? Clock { get; init; }
    public long? UtcOffsetUs { get; init; }
    /// <summary>Target spawn requested (simulation time).</summary>
    public required long TStimulusUs { get; init; }
    /// <summary>Estimated first frame on screen (present/vblank stats, D15).</summary>
    public long? TStimulusOnScreenUs { get; init; }
    public long? TOnsetUs { get; init; }
    public long? TPeakVelocityUs { get; init; }
    public long? TPrimaryEndUs { get; init; }
    public long? TFinalClickUs { get; init; }
    public long? ReactionTimeUs { get; init; }
    public ReactionTimeBasis? ReactionTimeBasis { get; init; }
    public long? MovementTimeUs { get; init; }
    public long? PrimaryDurationUs { get; init; }
}

public sealed record TrialKinematics
{
    /// <summary>D12: logged for every trial; null only if no movement was detected.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public double? PeakSpeedCountsPerS { get; init; }
    public double? PeakSpeedDegPerS { get; init; }
    public double? PathLengthCounts { get; init; }
    public int? NCorrections { get; init; }
    public SubmovementRecord[]? Submovements { get; init; }
    public string? Segmentation { get; init; }
}

public sealed record SubmovementRecord
{
    public required long TStartUs { get; init; }
    public required long TEndUs { get; init; }
    public required double DxCounts { get; init; }
    public required double DyCounts { get; init; }
    public required double PeakSpeedCountsPerS { get; init; }
}

/// <summary>Raw, unaccumulated per-packet counts (columnar). Packets start at the stimulus;
/// start + cumulative sum = position.</summary>
public sealed record RawPath
{
    public required long[] TUs { get; init; }
    public required int[] Dx { get; init; }
    public required int[] Dy { get; init; }
    public int[]? Buttons { get; init; }
}

public sealed record Derivation
{
    public string? Deriver { get; init; }
    public string? SessionFile { get; init; }
}
