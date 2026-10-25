namespace OpenVector.RawAccel.Settings;

/// <summary>
/// Raw Accel settings.json key names for one tested version. Verified against wrapper/wrapper.cpp at
/// RawAccelOfficial/rawaccel commit 53a7213 (release v1.7.1, whose internal version string is "1.7.0").
/// Unannotated fields use Newtonsoft's default name (the C++/CLI field name).
/// writer.exe deserialises with DefaultValueHandling.Populate, so a wrong key silently becomes a default:
/// that is why only versions in <see cref="Supported"/> are accepted.
/// </summary>
public sealed record RawAccelKeyMap
{
    /// <summary>Value of the top-level "version" field (RA_VER_STRING).</summary>
    public required string SettingsVersion { get; init; }

    // DriverConfig ([JsonObject(ItemRequired = Always)])
    public string Version { get; init; } = "version";
    public string DefaultDeviceConfig { get; init; } = "defaultDeviceConfig";
    public string Profiles { get; init; } = "profiles";
    public string Devices { get; init; } = "devices";

    // Profile ([JsonObject(ItemRequired = Always)])
    public string Name { get; init; } = "name";
    public string DomainWeights { get; init; } = "Stretches domain for horizontal vs vertical inputs";
    public string RangeWeights { get; init; } = "Stretches accel range for horizontal vs vertical inputs";
    public string AccelX { get; init; } = "Whole or horizontal accel parameters";
    public string AccelY { get; init; } = "Vertical accel parameters";
    public string SpeedArgs { get; init; } = "Input speed calculation parameters";
    public string OutputDpi { get; init; } = "Output DPI";
    public string YxRatio { get; init; } = "Y/X output DPI ratio (vertical sens multiplier)";
    public string LrRatio { get; init; } = "L/R output DPI ratio (left sens multiplier)";
    public string UdRatio { get; init; } = "U/D output DPI ratio (up sens multiplier)";
    public string Rotation { get; init; } = "Degrees of rotation";
    public string Snap { get; init; } = "Degrees of angle snapping";
    public string SpeedCap { get; init; } = "Input Speed Cap";

    // Vec2
    public string X { get; init; } = "x";
    public string Y { get; init; } = "y";

    // AccelArgs (value struct, not ItemRequired: a missing key becomes 0/false/first enum!)
    public string Mode { get; init; } = "mode";
    public string Gain { get; init; } = "Gain / Velocity";
    public string InputOffset { get; init; } = "inputOffset";
    public string OutputOffset { get; init; } = "outputOffset";
    public string Acceleration { get; init; } = "acceleration";
    public string DecayRate { get; init; } = "decayRate";
    public string Gamma { get; init; } = "gamma";
    public string Motivity { get; init; } = "motivity";
    public string ExponentClassic { get; init; } = "exponentClassic";
    public string Scale { get; init; } = "scale";
    public string ExponentPower { get; init; } = "exponentPower";
    public string Limit { get; init; } = "limit";
    public string SyncSpeed { get; init; } = "syncSpeed";
    public string Smooth { get; init; } = "smooth";
    public string Cap { get; init; } = "Cap / Jump";
    public string CapMode { get; init; } = "Cap mode";
    public string Data { get; init; } = "data";

    // SpeedArgs
    public string Whole { get; init; } = "Whole/combined accel (set false for 'by component' mode)";
    public string LpNorm { get; init; } = "lpNorm";
    public string InputSmoothHalflife { get; init; } = "Time in ms after which an input is weighted at half its original value.";
    public string ScaleSmoothHalflife { get; init; } = "Time in ms after which scale is weighted at half its original value.";
    public string OutputSmoothHalflife { get; init; } = "Time in ms after which an output is weighted at half its original value.";

    // DeviceConfig ([JsonObject(ItemRequired = Always)], some Required.Default)
    public string Disable { get; init; } = "disable";
    public string SetExtraInfo { get; init; } = "setExtraInfo";
    public string PollTimeLock { get; init; } = "Use constant time interval based on polling rate";
    public string Dpi { get; init; } = "DPI (normalizes input speed unit: counts/ms -> in/s)";
    public string PollingRate { get; init; } = "Polling rate Hz (keep at 0 for automatic adjustment)";
    public string MinimumTime { get; init; } = "minimumTime";
    public string MaximumTime { get; init; } = "maximumTime";

    // DeviceSettings ([JsonObject(ItemRequired = Always)])
    public string DeviceName { get; init; } = "name";
    public string DeviceProfile { get; init; } = "profile";
    public string DeviceId { get; init; } = "id";
    public string DeviceConfigKey { get; init; } = "config";

    /// <summary>Enum string values (StringEnumConverter).</summary>
    public IReadOnlyList<string> AccelModes { get; init; } = new[] { "classic", "jump", "natural", "synchronous", "power", "lut", "noaccel" };
    public IReadOnlyList<string> CapModes { get; init; } = new[] { "in_out", "input", "output" };

    public IEnumerable<string> ProfileKeys => new[] { Name, DomainWeights, RangeWeights, AccelX, AccelY, SpeedArgs, OutputDpi, YxRatio, LrRatio, UdRatio, Rotation, Snap, SpeedCap };
    public IEnumerable<string> AccelArgKeys => new[] { Mode, Gain, InputOffset, OutputOffset, Acceleration, DecayRate, Gamma, Motivity, ExponentClassic, Scale, ExponentPower, Limit, SyncSpeed, Smooth, Cap, CapMode, Data };
    public IEnumerable<string> SpeedArgKeys => new[] { Whole, LpNorm, InputSmoothHalflife, ScaleSmoothHalflife, OutputSmoothHalflife };
    public IEnumerable<string> DeviceConfigRequiredKeys => new[] { Disable, Dpi, PollingRate };
    public IEnumerable<string> DeviceConfigOptionalKeys => new[] { SetExtraInfo, PollTimeLock, MinimumTime, MaximumTime };
    public IEnumerable<string> DeviceKeys => new[] { DeviceName, DeviceProfile, DeviceId, DeviceConfigKey };

    /// <summary>Raw Accel 1.7.0 / 1.7.1 (both report "1.7.0"; v1.7.1 changed only the GUI).</summary>
    public static RawAccelKeyMap V1_7_0 { get; } = new() { SettingsVersion = "1.7.0" };

    /// <summary>Tested settings versions. Anything else is refused.</summary>
    public static IReadOnlyDictionary<string, RawAccelKeyMap> Supported { get; } =
        new Dictionary<string, RawAccelKeyMap>(StringComparer.Ordinal) { ["1.7.0"] = V1_7_0 };

    /// <summary>Driver versions (GET_VERSION IOCTL) this key map is tested with.</summary>
    public static IReadOnlyList<Version> SupportedDriverVersions { get; } = new[] { new Version(1, 7, 0) };

    public static RawAccelKeyMap? ForVersion(string? version) =>
        version is not null && Supported.TryGetValue(version, out var m) ? m : null;
}
