using System.Text.Json;
using System.Text.Json.Serialization;
using OpenVector.RawAccel.Model;
using OpenVector.RawAccel.Projection;
using OpenVector.RawAccel.Settings;

namespace OpenVector.RawAccel.Apply;

/// <summary>What OpenVector last did to one Raw Accel settings file (persisted as JSON).</summary>
public sealed record ApplyState
{
    public int SchemaVersion { get; init; } = 1;
    public required string SettingsPath { get; init; }
    public required string ProfileName { get; init; }
    /// <summary>The user's owned values before OpenVector's first apply (composition base and reset target).</summary>
    public required OwnedProfileValues Baseline { get; init; }
    /// <summary>The owned values OpenVector last wrote; null after revert/reset.</summary>
    public OwnedProfileValues? Applied { get; init; }
    public DirectionalParams? AppliedParams { get; init; }
    /// <summary>SHA-256 of the settings.json bytes OpenVector last wrote (any operation).</summary>
    public string? LastWrittenSha256 { get; init; }
    public DateTimeOffset? LastWrittenUtc { get; init; }
    public string? LastOperation { get; init; }
    /// <summary>Backup taken immediately before the last Apply (Revert restores it).</summary>
    public string? LastApplyBackupPath { get; init; }
}

public sealed class ApplyStateStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public string Path { get; }
    public ApplyStateStore(string? path = null) => Path = path ?? DefaultPath;

    public static string DefaultPath => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenVector", "rawaccel-state.json");

    public ApplyState? Load()
    {
        if (!File.Exists(Path)) return null;
        try { return JsonSerializer.Deserialize<ApplyState>(File.ReadAllText(Path), Options); }
        catch (JsonException) { return null; }
    }

    public void Save(ApplyState state) =>
        AtomicFile.WriteAllBytes(Path, JsonSerializer.SerializeToUtf8Bytes(state, Options));

    public void Delete() { if (File.Exists(Path)) File.Delete(Path); }
}

