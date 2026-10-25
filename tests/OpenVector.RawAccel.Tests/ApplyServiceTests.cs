using System.Text.Json.Nodes;
using OpenVector.RawAccel.Apply;
using OpenVector.RawAccel.Model;
using OpenVector.RawAccel.Projection;
using OpenVector.RawAccel.Settings;

namespace OpenVector.RawAccel.Tests;

public class ApplyServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ov-apply-" + Guid.NewGuid().ToString("N"));
    private readonly RawAccelLocation _loc;
    private readonly FakeProcessRunner _runner = new();
    private readonly FakeDriver _driver = FakeDriver.V170;
    private readonly FakeTime _time = new(new DateTimeOffset(2026, 10, 25, 12, 0, 0, TimeSpan.Zero));
    private readonly ApplyService _svc;
    private static RawAccelKeyMap K => RawAccelKeyMap.V1_7_0;

    public ApplyServiceTests()
    {
        string ra = Path.Combine(_root, "Raw Accel v1.7.1");
        Directory.CreateDirectory(ra);
        File.WriteAllText(Path.Combine(ra, "writer.exe"), "");
        File.WriteAllText(Path.Combine(ra, "wrapper.dll"), "");
        File.Copy(SettingsTests.FixturePath, Path.Combine(ra, "settings.json"));
        _loc = new RawAccelLocation(ra);
        _svc = NewService(new Version(1, 7, 0));
    }

    private ApplyService NewService(Version? writerVersion) => new(_runner, _driver, new FakeVersions(writerVersion), new ApplyServiceOptions
    {
        BackupDirectory = Path.Combine(_root, "backups"),
        StatePath = Path.Combine(_root, "state.json"),
    }, _time);

    public void Dispose() => Directory.Delete(_root, true);

    private ProjectionResult Project(string? profile = null) =>
        new RawAccel17Projection().Project(
            ProjectionTarget.Sample(t => Math.Exp(0.04 * Math.Cos(t) - 0.03 * Math.Sin(t) + 0.05 * Math.Cos(2 * t)), 360),
            null, _svc.GetBaseline(_loc, profile).Projection);

    private byte[] SettingsBytes => File.ReadAllBytes(_loc.SettingsPath);

    [Fact]
    public void Preflight_OkForSupportedInstall()
    {
        var p = _svc.Preflight(_loc);
        Assert.True(p.Ok, string.Join("\n", p.Errors));
        Assert.Equal(new Version(1, 7, 0), p.DriverVersion);
        Assert.Equal("1.7.0", p.SettingsVersion);
        Assert.Equal(2, p.ProfileNames.Count);
    }

    [Fact]
    public void Preflight_RefusesWrongVersionsAndMissingPieces()
    {
        _driver.Result = new DriverProbeResult(true, new Version(1, 6, 0));
        Assert.Contains(_svc.Preflight(_loc).Errors, e => e.Contains("driver version 1.6.0"));
        _driver.Result = new DriverProbeResult(false, null, "nope");
        Assert.Contains(_svc.Preflight(_loc).Errors, e => e.Contains("not installed"));
        _driver.Result = FakeDriver.V170.Result;
        Assert.Contains(NewService(new Version(1, 6, 1)).Preflight(_loc).Errors, e => e.Contains("writer.exe version"));
        File.Delete(_loc.WriterPath);
        Assert.Contains(_svc.Preflight(_loc).Errors, e => e.Contains("writer.exe not found"));
        File.WriteAllText(_loc.WriterPath, "");
        File.WriteAllText(_loc.SettingsPath, File.ReadAllText(SettingsTests.FixturePath).Replace("\"1.7.0\"", "\"1.6.1\""));
        Assert.Contains(_svc.Preflight(_loc).Errors, e => e.Contains("'1.6.1' is not supported"));
    }

    [Fact]
    public async Task Apply_BacksUpMergesValidatesWritesAndRecords()
    {
        var original = SettingsBytes;
        var proj = Project();
        var r = await _svc.ApplyAsync(_loc, proj);
        Assert.True(r.Succeeded, r.Message + string.Join("\n", r.Details));
        Assert.Equal(1, _runner.WriterCalls);
        Assert.Equal(SettingsBytes, _runner.SettingsBytesSeenByWriter);   // writer saw the merged file
        Assert.Equal(original, File.ReadAllBytes(r.Backup!.Path));         // backup = original bytes
        Assert.StartsWith(Path.Combine(_root, "backups"), r.Backup.Path);

        var doc = RawAccelSettingsDocument.Load(_loc.SettingsPath);
        var owned = doc.ReadOwned("default");
        Assert.Equal(AccelMode.Lut, owned.AccelX.Mode);
        Assert.Equal(800 * proj.Params.G0 / 2, owned.OutputDpi, 9);         // base Output DPI 800, k(0) = c = 2
        Assert.Equal(0.95 * proj.Params.Rho, owned.YxRatio, 12);           // composed with the user's Y/X 0.95
        Assert.Equal(2.5, doc.ReadProfile("default").DegreesRotation);      // not owned -> unchanged
        var before = JsonNode.Parse(System.Text.Encoding.UTF8.GetString(original))!;
        Assert.True(JsonNode.DeepEquals(before[K.Profiles]![1], doc.Root[K.Profiles]![1]));
        Assert.True(JsonNode.DeepEquals(before[K.Devices], doc.Root[K.Devices]));

        var st = _svc.State!;
        Assert.Equal(RawAccelSettingsDocument.Sha256Hex(SettingsBytes), st.LastWrittenSha256);
        Assert.Equal(800, st.Baseline.OutputDpi);
        Assert.Equal(r.Backup.Path, st.LastApplyBackupPath);

        var status = await _svc.GetStatusAsync(_loc);
        Assert.True(status.FileMatchesLastWrite);
        Assert.True(status.ProfileApplied);
        Assert.Equal(LogonTaskState.NotInstalled, status.LogonTask.State); // off by default
        Assert.Equal(new Version(1, 7, 0), status.Driver.Version);
    }

    [Fact]
    public async Task Reapply_ComposesWithRecordedBaseline_NotWithAppliedValues()
    {
        Assert.True((await _svc.ApplyAsync(_loc, Project())).Succeeded);
        var bi = _svc.GetBaseline(_loc);
        Assert.Equal(BaselineSource.Recorded, bi.Source);
        Assert.Equal(800, bi.Owned.OutputDpi);
        Assert.Equal(0.95, bi.Projection.YxRatio);
        Assert.True(bi.Projection.ShapeAvailable);
        var second = await _svc.ApplyAsync(_loc, Project());
        Assert.True(second.Succeeded);
        Assert.Equal(800, _svc.State!.Baseline.OutputDpi);
    }

    [Fact]
    public async Task Apply_WriterError_RestoresFileAndRecordsNothing()
    {
        var original = SettingsBytes;
        _runner.Writer = _ => new ProcessRunResult { Started = true, ExitCode = 1 };
        var r = await _svc.ApplyAsync(_loc, Project());
        Assert.False(r.Succeeded);
        Assert.Equal(OperationStage.Writer, r.FailedStage);
        Assert.Equal(WriterOutcome.ReportedError, r.Writer!.Outcome);
        Assert.Equal(original, SettingsBytes);
        Assert.Null(_svc.State);
    }

    [Fact]
    public async Task Apply_WriterHang_IsReportedAsDialog()
    {
        _runner.Writer = _ => new ProcessRunResult { Started = true, TimedOut = true };
        var r = await _svc.ApplyAsync(_loc, Project());
        Assert.Equal(WriterOutcome.TimedOut, r.Writer!.Outcome);
        Assert.Contains("error dialog", r.Message);
    }

    [Fact]
    public async Task Apply_PreflightFailure_ChangesNothing()
    {
        var original = SettingsBytes;
        var proj = Project();
        _driver.Result = new DriverProbeResult(false, null);
        var r = await _svc.ApplyAsync(_loc, proj);
        Assert.Equal(OperationStage.Preflight, r.FailedStage);
        Assert.Equal(original, SettingsBytes);
        Assert.False(Directory.Exists(Path.Combine(_root, "backups")));
        Assert.Equal(0, _runner.WriterCalls);
    }

    [Fact]
    public async Task Apply_RefusesStaleProjection()
    {
        var proj = Project();
        var doc = RawAccelSettingsDocument.Load(_loc.SettingsPath);
        doc.GetProfileNode(null)[K.YxRatio] = 1.2; // user changed Raw Accel in the meantime
        File.WriteAllBytes(_loc.SettingsPath, doc.ToUtf8Bytes());
        var r = await _svc.ApplyAsync(_loc, proj);
        Assert.False(r.Succeeded);
        Assert.Contains("changed since the projection", r.Message);
        Assert.Equal(0, _runner.WriterCalls);
    }

    [Fact]
    public async Task Apply_ToProfileWithAccelCurve_UsesAxisOnlyAndKeepsCurve()
    {
        var bi = _svc.GetBaseline(_loc, "Classic for desktop");
        Assert.False(bi.Projection.ShapeAvailable);
        var proj = new RawAccel17Projection().Project(ProjectionTarget.Sample(t => Math.Exp(0.05 * Math.Cos(t)), 360), null, bi.Projection);
        Assert.True(proj.AxisOnly);
        var before = RawAccelSettingsDocument.Load(_loc.SettingsPath).GetProfileNode("Classic for desktop")[K.AccelX]!.DeepClone();
        var r = await _svc.ApplyAsync(_loc, proj, "Classic for desktop");
        Assert.True(r.Succeeded, r.Message);
        var doc = RawAccelSettingsDocument.Load(_loc.SettingsPath);
        Assert.True(JsonNode.DeepEquals(before, doc.GetProfileNode("Classic for desktop")[K.AccelX]));
        Assert.Equal(1600 * proj.Params.G0, doc.ReadOwned("Classic for desktop").OutputDpi, 9);
        Assert.Equal(AccelMode.NoAccel, doc.ReadOwned("default").AccelX.Mode); // other profile untouched
    }

    [Fact]
    public async Task Revert_RestoresOriginalBytesAndRunsWriter()
    {
        var original = SettingsBytes;
        Assert.True((await _svc.ApplyAsync(_loc, Project())).Succeeded);
        var r = await _svc.RevertAsync(_loc);
        Assert.True(r.Succeeded, r.Message);
        Assert.Equal(original, SettingsBytes);
        Assert.Equal(2, _runner.WriterCalls);
        var status = await _svc.GetStatusAsync(_loc);
        Assert.False(status.ProfileApplied);
        Assert.True(status.FileMatchesLastWrite);
        Assert.Equal("revert", status.LastOperation);
        Assert.Equal(BaselineSource.CurrentFile, _svc.GetBaseline(_loc).Source);
        Assert.Equal(3, _svc.Backups.List().Count + 1); // apply backup + revert backup (+ the original is the first)
    }

    [Fact]
    public async Task Revert_WithoutBackup_Fails()
    {
        var r = await _svc.RevertAsync(_loc);
        Assert.False(r.Succeeded);
        Assert.Equal(0, _runner.WriterCalls);
    }

    [Fact]
    public async Task Reset_WritesNeutralAndRemovesLogonTask()
    {
        Assert.True((await _svc.ApplyAsync(_loc, Project())).Succeeded);
        Assert.True((await _svc.SetLogonTaskEnabledAsync(_loc, true)).Succeeded);
        Assert.Equal(LogonTaskState.Enabled, (await _svc.GetStatusAsync(_loc)).LogonTask.State);

        var r = await _svc.ResetToNeutralAsync(_loc);
        Assert.True(r.Succeeded, r.Message);
        var owned = RawAccelSettingsDocument.Load(_loc.SettingsPath).ReadOwned(null);
        Assert.Equal(800, owned.OutputDpi);         // the user's prior Output DPI
        Assert.Equal(1, owned.YxRatio); Assert.Equal(1, owned.LrRatio); Assert.Equal(1, owned.UdRatio);
        Assert.Equal(AccelMode.NoAccel, owned.AccelX.Mode);
        Assert.Equal((1.0, 1.0), (owned.RangeWeightX, owned.RangeWeightY));
        var status = await _svc.GetStatusAsync(_loc);
        Assert.Equal(LogonTaskState.NotInstalled, status.LogonTask.State);
        Assert.False(status.ProfileApplied);
        Assert.True(RawAccelSettingsValidator.Validate(RawAccelSettingsDocument.Load(_loc.SettingsPath)).IsValid);
    }

    [Fact]
    public async Task Reset_RestoreBaselineMode_KeepsUsersOwnRatio()
    {
        Assert.True((await _svc.ApplyAsync(_loc, Project())).Succeeded);
        Assert.True((await _svc.ResetToNeutralAsync(_loc, null, NeutralMode.RestoreBaseline)).Succeeded);
        Assert.Equal(0.95, RawAccelSettingsDocument.Load(_loc.SettingsPath).ReadOwned(null).YxRatio);
    }

    [Fact]
    public async Task Uninstall_WithoutRawAccel_StillRemovesTask()
    {
        Assert.True((await _svc.SetLogonTaskEnabledAsync(_loc, true)).Succeeded);
        var r = await _svc.UninstallAsync(null);
        Assert.True(r.Succeeded);
        Assert.Empty(_runner.Tasks);
    }

    [Fact]
    public async Task Status_DetectsExternalEdits()
    {
        Assert.Null((await _svc.GetStatusAsync(_loc)).FileMatchesLastWrite);
        Assert.True((await _svc.ApplyAsync(_loc, Project())).Succeeded);
        File.AppendAllText(_loc.SettingsPath, " ");
        var s = await _svc.GetStatusAsync(_loc);
        Assert.False(s.FileMatchesLastWrite);
        Assert.False(s.ProfileApplied);
    }

    [Fact]
    public async Task LogonTask_PointsAtUsersWriterAndSettings()
    {
        var r = await _svc.SetLogonTaskEnabledAsync(_loc, true);
        Assert.True(r.Succeeded, r.Message);
        Assert.Contains(_loc.WriterPath, _runner.LastTaskXml);
        Assert.Contains("\"" + _loc.SettingsPath + "\"", _runner.LastTaskXml);
        Assert.True((await _svc.SetLogonTaskEnabledAsync(_loc, false)).Succeeded);
        Assert.Empty(_runner.Tasks);
    }

    [Fact]
    public async Task ResetWithoutRecord_UndoesLeftoverOpenVectorLut()
    {
        // simulate an earlier OpenVector apply whose state file was lost
        var doc = RawAccelSettingsDocument.Load(_loc.SettingsPath);
        doc.WriteOwned(null, RawAccelParameterMapping.Compose(doc.ReadOwned(null), new DirectionalParams(1, 0.9, 1, 1, 1)));
        File.WriteAllBytes(_loc.SettingsPath, doc.ToUtf8Bytes());
        Assert.Contains(_svc.GetBaseline(_loc).Warnings, w => w.Contains("earlier OpenVector profile"));
        Assert.True((await _svc.ResetToNeutralAsync(_loc)).Succeeded);
        var owned = RawAccelSettingsDocument.Load(_loc.SettingsPath).ReadOwned(null);
        Assert.Equal(800, owned.OutputDpi, 9);
        Assert.Equal(AccelMode.NoAccel, owned.AccelX.Mode);
    }
}
