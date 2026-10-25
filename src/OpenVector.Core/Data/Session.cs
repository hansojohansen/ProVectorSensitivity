using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenVector.Core.Data;

// C# view of schemas/session.schema.json (v1, D15): session metadata + references to the lossless
// stream files. Stream formats are owned by the Trainer; Core only carries path + format id + version.

public enum SessionKind { Calibration, AbValidation, Research, Other }
public enum StreamKind { RawInput, Frames, Events, Trials, Other }
public enum StreamCompression { None, Zstd, Gzip, ParquetInternal }
public enum BreakReason { Scheduled, UserPause, FocusLoss, Other }

public sealed record Session
{
    public const string CurrentSchemaVersion = "1.0.0";

    public string SchemaVersion { get; init; } = CurrentSchemaVersion;
    public required string SessionId { get; init; }
    public required SessionKind Kind { get; init; }
    public required SessionClock Clock { get; init; }
    public DateTimeOffset? StartedAt { get; init; }
    public DateTimeOffset? EndedAt { get; init; }
    public long? TStartUs { get; init; }
    public long? TEndUs { get; init; }
    public long? DurationUs { get; init; }
    public required TrainerInfo Trainer { get; init; }
    public required DeviceInfo Device { get; init; }
    public DisplayInfo? Display { get; init; }
    public required ViewInfo View { get; init; }
    public RawAccelState? Rawaccel { get; init; }
    public string? ActiveGainMapId { get; init; }
    public SessionBreak[]? Breaks { get; init; }
    public SelfReport? SelfReport { get; init; }
    public required StreamRef[] Streams { get; init; }
    public string? Notes { get; init; }
}

public sealed record SessionClock
{
    public string Type { get; init; } = "qpc_us";
    public long? QpcFrequencyHz { get; init; }
    public required long UtcOffsetUs { get; init; }
}

public sealed record TrainerInfo
{
    public required string Version { get; init; }
    public string? Build { get; init; }
    public string? ProtocolId { get; init; }
    public long? Seed { get; init; }
    public Dictionary<string, JsonElement>? Plan { get; init; }
}

public sealed record DisplayInfo
{
    public double? RefreshHz { get; init; }
    public int[]? Resolution { get; init; }
    public bool? Vrr { get; init; }
    public string? PresentMode { get; init; }
}

public sealed record ViewInfo
{
    public required double YawDegPerCount { get; init; }
    public required double PitchDegPerCount { get; init; }
    public double? FovHDeg { get; init; }
    public string? FovScaling { get; init; }
    [JsonPropertyName("cm_per_360")]
    public double? CmPer360 { get; init; }
    public string? GamePreset { get; init; }
}

public sealed record RawAccelState
{
    public bool? Detected { get; init; }
    public string? Version { get; init; }
    [JsonPropertyName("settings_sha256")]
    public string? SettingsSha256 { get; init; }
}

public sealed record SessionBreak
{
    public required long TStartUs { get; init; }
    public required long TEndUs { get; init; }
    public BreakReason? Reason { get; init; }
}

public sealed record SelfReport
{
    public bool? WarmedUp { get; init; }
    [JsonPropertyName("fatigue_1_5")]
    public int? Fatigue1To5 { get; init; }
    public string? Notes { get; init; }
}

public sealed record StreamRef
{
    public required StreamKind Kind { get; init; }
    /// <summary>Relative to the session.json file.</summary>
    public required string Path { get; init; }
    public required string Format { get; init; }
    public required string FormatSchemaVersion { get; init; }
    public StreamCompression? Compression { get; init; }
    [JsonPropertyName("sha256")]
    public string? Sha256 { get; init; }
    public long? Records { get; init; }
}
