using System.Text.Json;
using OpenVector.RawAccel.Model;
using OpenVector.RawAccel.Projection;
using OpenVector.RawAccel.Settings;

namespace OpenVector.RawAccel.Apply;

/// <summary>Where the user's Raw Accel lives: the extracted release folder (writer.exe, wrapper.dll, settings.json).</summary>
public sealed record RawAccelLocation(string Folder, string? SettingsPathOverride = null)
{
    public string WriterPath => Path.Combine(Folder, WriterRunner.WriterFileName);
    public string WrapperPath => Path.Combine(Folder, "wrapper.dll");
    /// <summary>The GUI reads/writes settings.json in its own folder; the logon task uses the same file.</summary>
    public string SettingsPath => SettingsPathOverride ?? Path.Combine(Folder, "settings.json");
}

public sealed record ApplyServiceOptions
{
    public string? BackupDirectory { get; init; }
    public string? StatePath { get; init; }
    public TimeSpan WriterTimeout { get; init; } = WriterRunner.DefaultTimeout;
    public string LogonTaskName { get; init; } = RawAccelLogonTask.DefaultTaskName;
}

public sealed record PreflightResult
{
    public bool Ok => Errors.Count == 0;
    public IReadOnlyList<string> Errors { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
    public Version? DriverVersion { get; init; }
    public Version? WriterVersion { get; init; }
    public string? SettingsVersion { get; init; }
    public IReadOnlyList<string> ProfileNames { get; init; } = Array.Empty<string>();
}

public enum BaselineSource { CurrentFile, Recorded }

public sealed record BaselineInfo(string ProfileName, OwnedProfileValues Owned, ProjectionBaseline Projection, BaselineSource Source, IReadOnlyList<string> Warnings);

public enum OperationStage { None, Preflight, Load, Compose, Validate, Backup, WriteFile, Writer, LogonTask }

public sealed record OperationResult
{
    public bool Succeeded { get; init; }
    public OperationStage FailedStage { get; init; }
    public string Message { get; init; } = "";
    public IReadOnlyList<string> Details { get; init; } = Array.Empty<string>();
    public BackupInfo? Backup { get; init; }
    public string? WrittenSha256 { get; init; }
    public WriterResult? Writer { get; init; }
    public ValidationReport? Validation { get; init; }
    public OwnedProfileValues? Written { get; init; }

    internal static OperationResult Fail(OperationStage s, string msg, IEnumerable<string>? details = null) =>
        new() { Succeeded = false, FailedStage = s, Message = msg, Details = details?.ToList() ?? new List<string>() };
}

public sealed record RawAccelStatus
{
    /// <summary>null if OpenVector never wrote this file; else whether its bytes still equal what we last wrote.</summary>
    public bool? FileMatchesLastWrite { get; init; }
    public string? CurrentSha256 { get; init; }
    public string? LastWrittenSha256 { get; init; }
    public DateTimeOffset? LastWrittenUtc { get; init; }
    public string? LastOperation { get; init; }
    /// <summary>True if the last operation was an Apply that has not been reverted/reset.</summary>
    public bool ProfileApplied { get; init; }
    public string? ProfileName { get; init; }
    public DriverProbeResult Driver { get; init; } = new(false, null);
    public LogonTaskStatus LogonTask { get; init; } = new(LogonTaskState.Unknown);
}

/// <summary>
/// Orchestrates Apply / Revert / Reset / Status against the user's Raw Accel installation (D4):
/// preflight → backup → merge owned fields → validate → write file → run writer.exe → record hash.
/// It never writes to the driver itself; only Raw Accel's writer.exe does.
/// </summary>
public sealed class ApplyService
{
    private readonly IRawAccelDriverProbe _driver;
    private readonly IFileVersionReader _versions;
    private readonly WriterRunner _writer;
    private readonly ApplyStateStore _state;
    private readonly TimeProvider _time;

    public BackupStore Backups { get; }
    public RawAccelLogonTask LogonTask { get; }

    public ApplyService(IProcessRunner runner, IRawAccelDriverProbe driver, IFileVersionReader versions, ApplyServiceOptions? options = null, TimeProvider? time = null)
    {
        var o = options ?? new ApplyServiceOptions();
        _driver = driver; _versions = versions; _time = time ?? TimeProvider.System;
        _writer = new WriterRunner(runner) { Timeout = o.WriterTimeout };
        _state = new ApplyStateStore(o.StatePath);
        Backups = new BackupStore(o.BackupDirectory, _time);
        LogonTask = new RawAccelLogonTask(runner) { TaskName = o.LogonTaskName };
    }

    /// <summary>Real implementations; on non-Windows the driver probe reports "not installed".</summary>
    public static ApplyService CreateDefault(ApplyServiceOptions? options = null) =>
        new(new ProcessRunner(),
            OperatingSystem.IsWindows() ? new WindowsRawAccelDriverProbe() : new UnavailableRawAccelDriverProbe(),
            new FileVersionReader(), options);

    public ApplyState? State => _state.Load();

    // ------------------------------------------------------------------ preflight

    public PreflightResult Preflight(RawAccelLocation loc)
    {
        var errors = new List<string>(); var warnings = new List<string>();
        Version? driverVersion = null, writerVersion = null; string? settingsVersion = null;
        IReadOnlyList<string> profiles = Array.Empty<string>();

        if (!OperatingSystem.IsWindows()) warnings.Add("Not running on Windows: Raw Accel cannot be applied on this host.");
        if (!Directory.Exists(loc.Folder)) errors.Add($"Raw Accel folder '{loc.Folder}' does not exist.");
        if (!File.Exists(loc.WriterPath)) errors.Add($"writer.exe not found in '{loc.Folder}'. Select the folder you extracted Raw Accel into.");
        else
        {
            writerVersion = _versions.GetFileVersion(loc.WriterPath);
            if (writerVersion is null) warnings.Add("Could not read writer.exe's version.");
            else if (writerVersion.Major != 1 || writerVersion.Minor != 7) errors.Add($"writer.exe version {writerVersion} is not supported (tested: 1.7.x).");
        }
        if (!File.Exists(loc.WrapperPath)) warnings.Add("wrapper.dll not found next to writer.exe; writer.exe will fail without it.");

        var d = _driver.GetVersion();
        if (!d.Installed) errors.Add("The Raw Accel driver is not installed (run Raw Accel's installer.exe and reboot). " + d.Error);
        else
        {
            driverVersion = d.Version;
            if (d.Version is null || !RawAccelKeyMap.SupportedDriverVersions.Contains(d.Version))
                errors.Add($"Raw Accel driver version {d.Version} is not supported (tested: {string.Join(", ", RawAccelKeyMap.SupportedDriverVersions)}).");
        }

        if (!File.Exists(loc.SettingsPath)) errors.Add($"'{loc.SettingsPath}' not found. Start the Raw Accel GUI once so it creates settings.json.");
        else
        {
            try
            {
                var doc = RawAccelSettingsDocument.Load(loc.SettingsPath);
                settingsVersion = doc.Keys.SettingsVersion;
                profiles = doc.ProfileNames;
                var rep = RawAccelSettingsValidator.Validate(doc);
                errors.AddRange(rep.Errors.Select(e => "Your current settings.json is invalid: " + e));
                warnings.AddRange(rep.Issues.Where(i => i.Severity == IssueSeverity.Warning).Select(i => i.ToString()));
            }
            catch (UnsupportedRawAccelVersionException e) { settingsVersion = e.Version; errors.Add(e.Message); }
            catch (Exception e) when (e is JsonException or InvalidDataException or IOException or InvalidOperationException)
            { errors.Add($"Could not read settings.json: {e.Message}"); }
        }
        return new PreflightResult { Errors = errors, Warnings = warnings, DriverVersion = driverVersion, WriterVersion = writerVersion, SettingsVersion = settingsVersion, ProfileNames = profiles };
    }

    // ------------------------------------------------------------------ baseline

    /// <summary>
    /// The baseline the projection must be computed against: the recorded pre-OpenVector values if the file still
    /// holds what OpenVector last applied, else the values currently in the file.
    /// </summary>
    public BaselineInfo GetBaseline(RawAccelLocation loc, string? profileName = null) =>
        GetBaseline(RawAccelSettingsDocument.Load(loc.SettingsPath), loc.SettingsPath, profileName);

    private BaselineInfo GetBaseline(RawAccelSettingsDocument doc, string settingsPath, string? profileName)
    {
        string name = doc.ResolveProfileName(profileName);
        var current = doc.ReadOwned(name);
        var profile = doc.ReadProfile(name);
        var warnings = new List<string>();
        var st = _state.Load();
        OwnedProfileValues baseline = current; var source = BaselineSource.CurrentFile;
        if (st is not null && SamePath(st.SettingsPath, settingsPath) && st.ProfileName == name && st.Applied is not null && current.ApproximatelyEquals(st.Applied))
        { baseline = st.Baseline; source = BaselineSource.Recorded; }
        else if (RawAccelParameterMapping.IsOpenVectorLut(current.AccelX))
            warnings.Add("This profile contains a constant LUT that looks like an earlier OpenVector profile, but no matching record exists. Use 'Reset to neutral' first for the full fit.");

        bool shape = RawAccelParameterMapping.BaselineAllowsShape(baseline, profile.AccelY);
        string? reason = shape ? null : $"Accel curve '{baseline.AccelX.Mode}' is in use.";
        if (doc.HasSmoothing(name)) warnings.Add("Raw Accel smoothing is enabled in this profile; its effect on direction-dependent gain is not modelled.");
        var pb = new ProjectionBaseline
        {
            YxRatio = baseline.YxRatio, LrRatio = baseline.LrRatio, UdRatio = baseline.UdRatio,
            RotationDeg = profile.DegreesRotation, SnapDeg = profile.DegreesSnap,
            ShapeAvailable = shape, ShapeUnavailableReason = reason,
        };
        return new BaselineInfo(name, baseline, pb, source, warnings);
    }

    // ------------------------------------------------------------------ apply

    public async Task<OperationResult> ApplyAsync(RawAccelLocation loc, ProjectionResult projection, string? profileName = null, CancellationToken ct = default)
    {
        var pre = Preflight(loc);
        if (!pre.Ok) return OperationResult.Fail(OperationStage.Preflight, "Preflight failed.", pre.Errors);
        if (projection.BackendId != RawAccel17Projection.BackendId)
            return OperationResult.Fail(OperationStage.Compose, $"Projection backend '{projection.BackendId}' cannot be applied to Raw Accel 1.7.");

        RawAccelSettingsDocument doc; BaselineInfo bi; byte[] original;
        try
        {
            original = await File.ReadAllBytesAsync(loc.SettingsPath, ct).ConfigureAwait(false);
            doc = RawAccelSettingsDocument.Parse(System.Text.Encoding.UTF8.GetString(original));
            bi = GetBaseline(doc, loc.SettingsPath, profileName);
        }
        catch (Exception e) when (e is JsonException or InvalidDataException or IOException or KeyNotFoundException or UnsupportedRawAccelVersionException)
        { return OperationResult.Fail(OperationStage.Load, e.Message); }

        if (bi.Projection != projection.Baseline)
            return OperationResult.Fail(OperationStage.Compose, "Your Raw Accel settings changed since the projection was computed. Re-run the projection with the current baseline.");

        OwnedProfileValues owned;
        try { owned = RawAccelParameterMapping.Compose(bi.Owned, projection.Params); }
        catch (InvalidOperationException e) { return OperationResult.Fail(OperationStage.Compose, e.Message); }

        doc.WriteOwned(bi.ProfileName, owned);
        var report = RawAccelSettingsValidator.Validate(doc);
        if (!report.IsValid) return OperationResult.Fail(OperationStage.Validate, "The merged settings failed validation; nothing was changed.", report.Errors.Select(e => e.ToString())) with { Validation = report };

        var r = await WriteAndActivateAsync(loc, doc.ToUtf8Bytes(), original, restoreOnWriterFailure: true, ct).ConfigureAwait(false);
        if (!r.Succeeded) return r with { Validation = report };
        _state.Save(new ApplyState
        {
            SettingsPath = Path.GetFullPath(loc.SettingsPath), ProfileName = bi.ProfileName, Baseline = bi.Owned, Applied = owned,
            AppliedParams = projection.Params, LastWrittenSha256 = r.WrittenSha256, LastWrittenUtc = _time.GetUtcNow(),
            LastOperation = "apply", LastApplyBackupPath = r.Backup?.Path,
        });
        return r with { Message = "Profile applied.", Validation = report, Written = owned, Details = bi.Warnings.Concat(projection.Warnings).ToList() };
    }

    // ------------------------------------------------------------------ revert

    /// <summary>Restores the backup taken before the last Apply (or the given backup) and re-runs writer.exe.</summary>
    public async Task<OperationResult> RevertAsync(RawAccelLocation loc, string? backupPath = null, CancellationToken ct = default)
    {
        var st = _state.Load();
        string? path = backupPath ?? st?.LastApplyBackupPath;
        if (path is null || !File.Exists(path)) return OperationResult.Fail(OperationStage.Load, "No backup to revert to.");
        var bytes = await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
        var details = new List<string>();
        try { RawAccelSettingsDocument.Parse(System.Text.Encoding.UTF8.GetString(bytes)); }
        catch (Exception e) when (e is JsonException or UnsupportedRawAccelVersionException) { details.Add("The backup is not a supported Raw Accel 1.7 file: " + e.Message); }

        var pre = Preflight(loc);
        bool canRunWriter = pre.Ok || pre.Errors.All(e => e.StartsWith("Your current settings.json is invalid", StringComparison.Ordinal));
        byte[]? current = File.Exists(loc.SettingsPath) ? await File.ReadAllBytesAsync(loc.SettingsPath, ct).ConfigureAwait(false) : null;
        OperationResult r;
        if (canRunWriter) r = await WriteAndActivateAsync(loc, bytes, current, restoreOnWriterFailure: false, ct).ConfigureAwait(false);
        else
        {
            var b = current is null ? null : Backups.Create(loc.SettingsPath);
            AtomicFile.WriteAllBytes(loc.SettingsPath, bytes);
            r = new OperationResult { Succeeded = false, FailedStage = OperationStage.Preflight, Backup = b, WrittenSha256 = RawAccelSettingsDocument.Sha256Hex(bytes),
                Message = "The backup was restored to settings.json, but writer.exe could not be run.", Details = pre.Errors };
        }
        if (st is not null)
            _state.Save(st with { Applied = null, AppliedParams = null, LastWrittenSha256 = r.WrittenSha256, LastWrittenUtc = _time.GetUtcNow(), LastOperation = "revert" });
        return r with { Message = r.Succeeded ? "Reverted to the backup." : r.Message, Details = details.Concat(r.Details).ToList() };
    }

    // ------------------------------------------------------------------ reset / uninstall

    /// <summary>
    /// Writes neutral owned values (D4) to the profile, runs writer.exe, and always removes the logon task.
    /// If writer.exe fails the neutral file is kept (it is safe), and the failure is reported.
    /// </summary>
    public async Task<OperationResult> ResetToNeutralAsync(RawAccelLocation loc, string? profileName = null, NeutralMode mode = NeutralMode.Identity, CancellationToken ct = default)
    {
        var task = await LogonTask.DisableAsync(ct).ConfigureAwait(false);
        var details = new List<string> { task.Message };
        if (!File.Exists(loc.SettingsPath))
            return OperationResult.Fail(OperationStage.Load, "settings.json not found; nothing to reset.", details) with { Succeeded = task.Succeeded };

        RawAccelSettingsDocument doc; byte[] original; string name;
        try
        {
            original = await File.ReadAllBytesAsync(loc.SettingsPath, ct).ConfigureAwait(false);
            doc = RawAccelSettingsDocument.Parse(System.Text.Encoding.UTF8.GetString(original));
            name = doc.ResolveProfileName(profileName);
        }
        catch (Exception e) when (e is JsonException or InvalidDataException or IOException or KeyNotFoundException or UnsupportedRawAccelVersionException)
        { return OperationResult.Fail(OperationStage.Load, e.Message, details); }

        var st = _state.Load();
        var baseline = st is not null && SamePath(st.SettingsPath, loc.SettingsPath) && st.ProfileName == name ? st.Baseline : null;
        var neutral = NeutralProfile.Compute(doc.ReadOwned(name), baseline, mode);
        doc.WriteOwned(name, neutral);
        var report = RawAccelSettingsValidator.Validate(doc);
        if (!report.IsValid) return OperationResult.Fail(OperationStage.Validate, "The neutral settings failed validation; nothing was changed.", details.Concat(report.Errors.Select(e => e.ToString()))) with { Validation = report };

        var pre = Preflight(loc);
        OperationResult r;
        if (pre.Ok) r = await WriteAndActivateAsync(loc, doc.ToUtf8Bytes(), original, restoreOnWriterFailure: false, ct).ConfigureAwait(false);
        else
        {
            var b = Backups.Create(loc.SettingsPath);
            var bytes = doc.ToUtf8Bytes();
            AtomicFile.WriteAllBytes(loc.SettingsPath, bytes);
            r = new OperationResult { Succeeded = false, FailedStage = OperationStage.Preflight, Backup = b, WrittenSha256 = RawAccelSettingsDocument.Sha256Hex(bytes),
                Message = "settings.json was reset, but writer.exe could not be run; the driver keeps its current settings until the next write or reboot.", Details = pre.Errors };
        }
        if (st is not null)
            _state.Save(st with { Applied = null, AppliedParams = null, LastWrittenSha256 = r.WrittenSha256, LastWrittenUtc = _time.GetUtcNow(), LastOperation = "reset" });
        return r with
        {
            Succeeded = r.Succeeded && task.Succeeded,
            FailedStage = !task.Succeeded && r.Succeeded ? OperationStage.LogonTask : r.FailedStage,
            Message = r.Succeeded ? (task.Succeeded ? "Reset to neutral." : "Reset to neutral, but the logon task could not be removed.") : r.Message,
            Details = details.Concat(r.Details).ToList(), Validation = report, Written = neutral,
        };
    }

    /// <summary>Uninstaller hook: reset to neutral (if Raw Accel is present) and remove the logon task. Backups are kept.</summary>
    public async Task<OperationResult> UninstallAsync(RawAccelLocation? loc, CancellationToken ct = default)
    {
        if (loc is not null && File.Exists(loc.SettingsPath)) return await ResetToNeutralAsync(loc, _state.Load()?.ProfileName, NeutralMode.Identity, ct).ConfigureAwait(false);
        var t = await LogonTask.DisableAsync(ct).ConfigureAwait(false);
        return new OperationResult { Succeeded = t.Succeeded, FailedStage = t.Succeeded ? OperationStage.None : OperationStage.LogonTask, Message = t.Message };
    }

    // ------------------------------------------------------------------ logon task

    public async Task<LogonTaskResult> SetLogonTaskEnabledAsync(RawAccelLocation loc, bool enabled, CancellationToken ct = default)
    {
        if (!enabled) return await LogonTask.DisableAsync(ct).ConfigureAwait(false);
        if (!File.Exists(loc.WriterPath)) return new LogonTaskResult(false, "writer.exe not found.");
        if (!File.Exists(loc.SettingsPath)) return new LogonTaskResult(false, "settings.json not found.");
        return await LogonTask.EnableAsync(loc.WriterPath, loc.SettingsPath, ct: ct).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------ status

    public async Task<RawAccelStatus> GetStatusAsync(RawAccelLocation loc, CancellationToken ct = default)
    {
        var st = _state.Load();
        string? current = File.Exists(loc.SettingsPath) ? RawAccelSettingsDocument.Sha256Hex(await File.ReadAllBytesAsync(loc.SettingsPath, ct).ConfigureAwait(false)) : null;
        bool relevant = st is not null && SamePath(st.SettingsPath, loc.SettingsPath);
        return new RawAccelStatus
        {
            CurrentSha256 = current,
            LastWrittenSha256 = relevant ? st!.LastWrittenSha256 : null,
            FileMatchesLastWrite = relevant && st!.LastWrittenSha256 is not null ? current == st.LastWrittenSha256 : null,
            LastWrittenUtc = relevant ? st!.LastWrittenUtc : null,
            LastOperation = relevant ? st!.LastOperation : null,
            ProfileApplied = relevant && st!.Applied is not null && current == st.LastWrittenSha256,
            ProfileName = relevant ? st!.ProfileName : null,
            Driver = _driver.GetVersion(),
            LogonTask = await LogonTask.QueryAsync(ct).ConfigureAwait(false),
        };
    }

    // ------------------------------------------------------------------ internals

    private async Task<OperationResult> WriteAndActivateAsync(RawAccelLocation loc, byte[] bytes, byte[]? previous, bool restoreOnWriterFailure, CancellationToken ct)
    {
        BackupInfo? backup = null;
        try { if (previous is not null) backup = Backups.Create(loc.SettingsPath); }
        catch (IOException e) { return OperationResult.Fail(OperationStage.Backup, "Could not create a backup; nothing was changed: " + e.Message); }
        catch (UnauthorizedAccessException e) { return OperationResult.Fail(OperationStage.Backup, "Could not create a backup; nothing was changed: " + e.Message); }

        try { AtomicFile.WriteAllBytes(loc.SettingsPath, bytes); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        { return OperationResult.Fail(OperationStage.WriteFile, "Could not write settings.json: " + e.Message) with { Backup = backup }; }
        string hash = RawAccelSettingsDocument.Sha256Hex(bytes);

        var w = await _writer.RunAsync(loc.WriterPath, loc.SettingsPath, ct).ConfigureAwait(false);
        if (w.Succeeded) return new OperationResult { Succeeded = true, Backup = backup, WrittenSha256 = hash, Writer = w, Message = w.Message };

        if (restoreOnWriterFailure && previous is not null)
        {
            AtomicFile.WriteAllBytes(loc.SettingsPath, previous);
            return OperationResult.Fail(OperationStage.Writer, w.Message + " settings.json was restored to its previous content.") with { Backup = backup, Writer = w };
        }
        return OperationResult.Fail(OperationStage.Writer, w.Message) with { Backup = backup, Writer = w, WrittenSha256 = hash };
    }

    private static bool SamePath(string a, string b) =>
        string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}
