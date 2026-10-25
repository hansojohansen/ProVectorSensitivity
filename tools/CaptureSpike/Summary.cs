using System.Text.Json.Serialization;
using OpenVector.Capture;
using OpenVector.Capture.Timing;
using OpenVector.Capture.Windows;

namespace OpenVector.Tools.CaptureSpike;

internal sealed class SpikeSummary
{
    public string Tool { get; init; } = "";
    public string ToolVersion { get; init; } = "";
    public string CreatedUtc { get; init; } = "";
    public string Os { get; init; } = "";
    public string OsVersion { get; init; } = "";
    public string Dotnet { get; init; } = "";
    public string ProcessArchitecture { get; init; } = "";
    public int ProcessorCount { get; init; }
    public long QpcFrequency { get; init; }
    public double? ExpectedRateHz { get; init; }
    public List<DeviceSummary> Devices { get; init; } = [];
    public List<PhaseSummary> Phases { get; init; } = [];
}

internal sealed class LogMetadata
{
    public string Tool { get; init; } = "";
    public string ToolVersion { get; init; } = "";
    public string Mode { get; init; } = "";
    public int Seconds { get; init; }
    public double? ExpectedRateHz { get; init; }
    public string Os { get; init; } = "";
    public string ProcessArchitecture { get; init; } = "";
    public List<DeviceSummary> Devices { get; init; } = [];
}

internal sealed class DeviceSummary
{
    public string Handle { get; init; } = "";
    public string Name { get; init; } = "";
    public string VidPid { get; init; } = "";
    public uint ButtonCount { get; init; }
    public uint SampleRate { get; init; }
    public bool HasHorizontalWheel { get; init; }

    public static DeviceSummary From(MouseDeviceInfo m) => new()
    {
        Handle = "0x" + m.Handle.ToString("X", System.Globalization.CultureInfo.InvariantCulture),
        Name = m.Name,
        VidPid = m.VidPid,
        ButtonCount = m.ButtonCount,
        SampleRate = m.SampleRate,
        HasHorizontalWheel = m.HasHorizontalWheel,
    };
}

internal sealed class GcSummary
{
    public int Gen0 { get; init; }
    public int Gen1 { get; init; }
    public int Gen2 { get; init; }
    public double TotalPauseMs { get; init; }
    public string LatencyMode { get; init; } = "";
    public long MainThreadAllocatedBytes { get; init; }
}

internal sealed class PhaseSummary
{
    public string Mode { get; init; } = "";
    public string LogFile { get; init; } = "";
    public bool EndedByWindowClose { get; init; }
    public double RecordedSeconds { get; init; }
    public double ForegroundSeconds { get; init; }
    public CaptureCounters Counters { get; init; } = new();
    public long RecorderMissingPackets { get; init; }
    public long RecorderCoalescingMarkers { get; init; }
    public long RetainOverflow { get; init; }
    public long MarkersDropped { get; init; }
    public GcSummary Gc { get; init; } = new();
    public PollingReport Polling { get; init; } = new();
    /// <summary>Same analysis without the --expected-hz override (only when one was given).</summary>
    public PollingReport? PollingWithoutExpectedRate { get; init; }
    public Dictionary<string, long> PacketsPerDevice { get; init; } = [];
    public double ExtraInfoNonZeroFraction { get; init; }
    public bool RawAccelSetExtraInfoLikely { get; init; }
    public long ButtonTransitionPackets { get; init; }
    public long WheelPackets { get; init; }
}

[JsonSourceGenerationOptions(WriteIndented = true, NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals)]
[JsonSerializable(typeof(SpikeSummary))]
[JsonSerializable(typeof(LogMetadata))]
internal sealed partial class SpikeJsonContext : JsonSerializerContext;
