using System.Xml.Linq;
using OpenVector.RawAccel.Apply;

namespace OpenVector.RawAccel.Tests;

public class WriterRunnerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ov-writer-" + Guid.NewGuid().ToString("N"));
    public WriterRunnerTests() { Directory.CreateDirectory(_dir); File.WriteAllText(Path.Combine(_dir, "writer.exe"), ""); }
    public void Dispose() => Directory.Delete(_dir, true);

    [Theory]
    [InlineData(0, false, true, WriterOutcome.Success)]
    [InlineData(1, false, true, WriterOutcome.ReportedError)]
    [InlineData(-532462766, false, true, WriterOutcome.UnexpectedExitCode)]
    [InlineData(null, true, true, WriterOutcome.TimedOut)]
    [InlineData(null, false, false, WriterOutcome.LaunchFailed)]
    public async Task InterpretsOutcomes(int? exit, bool timedOut, bool started, WriterOutcome expected)
    {
        var fake = new FakeProcessRunner { Writer = _ => new ProcessRunResult { Started = started, ExitCode = exit, TimedOut = timedOut, LaunchError = started ? null : "denied" } };
        var w = new WriterRunner(fake) { Timeout = TimeSpan.FromSeconds(3) };
        var r = await w.RunAsync(Path.Combine(_dir, "writer.exe"), Path.Combine(_dir, "my settings.json"));
        Assert.Equal(expected, r.Outcome);
        Assert.Equal(expected == WriterOutcome.Success, r.Succeeded);
        var call = Assert.Single(fake.Calls);
        Assert.Equal(new[] { Path.Combine(_dir, "my settings.json") }, call.Arguments); // one argument, spaces preserved
        Assert.Equal(_dir, call.WorkingDirectory);
    }

    [Fact]
    public async Task MissingWriter_DoesNotLaunch()
    {
        var fake = new FakeProcessRunner();
        var r = await new WriterRunner(fake).RunAsync(Path.Combine(_dir, "nope", "writer.exe"), "s.json");
        Assert.Equal(WriterOutcome.LaunchFailed, r.Outcome);
        Assert.Empty(fake.Calls);
    }

    [Fact]
    public async Task RealProcessRunner_TimesOutAndKills()
    {
        if (OperatingSystem.IsWindows()) return; // uses /bin/sh
        var r = await new ProcessRunner().RunAsync(new ProcessSpec("/bin/sh", new[] { "-c", "sleep 30" }), TimeSpan.FromMilliseconds(300));
        Assert.True(r.Started); Assert.True(r.TimedOut);
        var ok = await new ProcessRunner().RunAsync(new ProcessSpec("/bin/sh", new[] { "-c", "echo hi; exit 1" }), TimeSpan.FromSeconds(10));
        Assert.Equal(1, ok.ExitCode); Assert.Equal("hi", ok.StandardOutput.Trim());
        var missing = await new ProcessRunner().RunAsync(new ProcessSpec("/nonexistent/writer.exe", Array.Empty<string>()), TimeSpan.FromSeconds(1));
        Assert.False(missing.Started); Assert.NotNull(missing.LaunchError);
    }
}

public class LogonTaskTests
{
    private static readonly XNamespace Ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";

    [Fact]
    public void TaskXml_RunsWriterWithSettingsAtLogon()
    {
        var t = new RawAccelLogonTask(new FakeProcessRunner());
        string xml = t.BuildTaskXml(@"C:\Tools\RawAccel\writer.exe", @"C:\Tools\RawAccel\settings.json", @"PC\hans");
        Assert.StartsWith("<?xml version=\"1.0\" encoding=\"utf-16\"?>", xml, StringComparison.OrdinalIgnoreCase);
        var d = XDocument.Parse(xml).Root!;
        Assert.Equal(@"\OpenVector\ReapplyRawAccel", d.Element(Ns + "RegistrationInfo")!.Element(Ns + "URI")!.Value);
        var trig = d.Element(Ns + "Triggers")!.Element(Ns + "LogonTrigger")!;
        Assert.Equal(@"PC\hans", trig.Element(Ns + "UserId")!.Value);
        Assert.Equal("PT5S", trig.Element(Ns + "Delay")!.Value);
        var pr = d.Element(Ns + "Principals")!.Element(Ns + "Principal")!;
        Assert.Equal("InteractiveToken", pr.Element(Ns + "LogonType")!.Value);
        Assert.Equal("LeastPrivilege", pr.Element(Ns + "RunLevel")!.Value);
        var exec = d.Element(Ns + "Actions")!.Element(Ns + "Exec")!;
        Assert.Equal(@"C:\Tools\RawAccel\writer.exe", exec.Element(Ns + "Command")!.Value);
        Assert.Equal("\"C:\\Tools\\RawAccel\\settings.json\"", exec.Element(Ns + "Arguments")!.Value);
        Assert.Equal("PT1M", d.Element(Ns + "Settings")!.Element(Ns + "ExecutionTimeLimit")!.Value);
        Assert.DoesNotContain("OpenVector.exe", xml, StringComparison.OrdinalIgnoreCase); // no OpenVector process at logon
    }

    [Fact]
    public async Task EnableQueryDisable()
    {
        var fake = new FakeProcessRunner();
        var tmp = Path.Combine(Path.GetTempPath(), "ov-task-" + Guid.NewGuid().ToString("N"));
        var t = new RawAccelLogonTask(fake) { SchtasksPath = "schtasks.exe", TempDirectory = tmp };
        Assert.Equal(LogonTaskState.NotInstalled, (await t.QueryAsync()).State);
        var d0 = await t.DisableAsync();
        Assert.True(d0.Succeeded);
        Assert.DoesNotContain(fake.Calls, c => c.Arguments[0] == "/Delete");

        var e = await t.EnableAsync("/ra/writer.exe", "/ra/settings.json", "PC\\hans");
        Assert.True(e.Succeeded, e.Message);
        var create = fake.Calls.Single(c => c.Arguments[0] == "/Create");
        Assert.Equal(new[] { "/Create", "/TN", @"\OpenVector\ReapplyRawAccel", "/XML" }, create.Arguments.Take(4));
        Assert.Equal("/F", create.Arguments[^1]);
        Assert.Empty(Directory.GetFiles(tmp)); // temp XML removed

        var q = await t.QueryAsync();
        Assert.Equal(LogonTaskState.Enabled, q.State);
        Assert.EndsWith("writer.exe", q.Command);
        Assert.True((await t.DisableAsync()).Succeeded);
        Assert.Equal(LogonTaskState.NotInstalled, (await t.QueryAsync()).State);
        Directory.Delete(tmp, true);
    }

    [Fact]
    public void ParseQuery_DisabledAndGarbage()
    {
        var xml = new RawAccelLogonTask(new FakeProcessRunner()).BuildTaskXml("w.exe", "s.json", "u");
        var doc = XDocument.Parse(xml);
        doc.Root!.Element(Ns + "Settings")!.Element(Ns + "Enabled")!.Value = "false";
        Assert.Equal(LogonTaskState.Disabled, RawAccelLogonTask.ParseQueryXml(doc.ToString()).State);
        Assert.Equal(LogonTaskState.Unknown, RawAccelLogonTask.ParseQueryXml("not xml").State);
    }

    [Fact]
    public async Task SchtasksFailure_IsReported()
    {
        var runner = new FailingRunner();
        var t = new RawAccelLogonTask(runner) { TempDirectory = Path.GetTempPath() };
        var r = await t.EnableAsync("w.exe", "s.json", "u");
        Assert.False(r.Succeeded);
        Assert.Contains("Access is denied", r.Message);
        Assert.Equal(LogonTaskState.Unknown, (await new RawAccelLogonTask(new NotStartingRunner()).QueryAsync()).State);
    }

    private sealed class FailingRunner : IProcessRunner
    {
        public Task<ProcessRunResult> RunAsync(ProcessSpec spec, TimeSpan timeout, CancellationToken ct = default) =>
            Task.FromResult(new ProcessRunResult { Started = true, ExitCode = 1, StandardError = "ERROR: Access is denied." });
    }
    private sealed class NotStartingRunner : IProcessRunner
    {
        public Task<ProcessRunResult> RunAsync(ProcessSpec spec, TimeSpan timeout, CancellationToken ct = default) =>
            Task.FromResult(new ProcessRunResult { LaunchError = "not windows" });
    }
}
