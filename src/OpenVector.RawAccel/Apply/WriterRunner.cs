namespace OpenVector.RawAccel.Apply;

public enum WriterOutcome
{
    /// <summary>Exit code 0: the driver accepted the settings (writer.exe is silent on success).</summary>
    Success,
    /// <summary>Exit code 1: writer.exe showed a modal error dialog (which the user dismissed) and exited.</summary>
    ReportedError,
    /// <summary>No exit within the timeout: almost certainly an error dialog waiting for the user. The process was killed.</summary>
    TimedOut,
    /// <summary>Any other exit code (crash, missing .NET Framework, ...).</summary>
    UnexpectedExitCode,
    /// <summary>writer.exe could not be started (missing file, blocked by policy, not Windows).</summary>
    LaunchFailed,
}

public sealed record WriterResult(WriterOutcome Outcome, int? ExitCode, string Message)
{
    public bool Succeeded => Outcome == WriterOutcome.Success;
}

/// <summary>
/// Runs Raw Accel's own <c>writer.exe &lt;settings file&gt;</c>. writer.exe parses the JSON, validates it, and
/// writes it to the driver (which sleeps ~1 s per write). On any error it shows a MessageBox and exits 1, so the
/// error text cannot be captured; validate first with RawAccelSettingsValidator.
/// </summary>
public sealed class WriterRunner
{
    public const string WriterFileName = "writer.exe";
    /// <summary>Default timeout: driver write delay (1 s) + .NET Framework start-up, with margin.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(20);

    private readonly IProcessRunner _runner;
    public TimeSpan Timeout { get; init; } = DefaultTimeout;

    public WriterRunner(IProcessRunner runner) => _runner = runner;

    public async Task<WriterResult> RunAsync(string writerPath, string settingsPath, CancellationToken ct = default)
    {
        if (!File.Exists(writerPath))
            return new WriterResult(WriterOutcome.LaunchFailed, null, $"writer.exe not found at '{writerPath}'.");
        var spec = new ProcessSpec(writerPath, new[] { Path.GetFullPath(settingsPath) }, Path.GetDirectoryName(Path.GetFullPath(writerPath)));
        var r = await _runner.RunAsync(spec, Timeout, ct).ConfigureAwait(false);
        return Interpret(r);
    }

    public static WriterResult Interpret(ProcessRunResult r)
    {
        if (!r.Started) return new WriterResult(WriterOutcome.LaunchFailed, null, $"Could not start writer.exe: {r.LaunchError}");
        if (r.TimedOut) return new WriterResult(WriterOutcome.TimedOut, null,
            "writer.exe did not finish in time. It most likely showed an error dialog (e.g. driver not installed or version mismatch); it was closed. The driver settings were not changed.");
        return r.ExitCode switch
        {
            0 => new WriterResult(WriterOutcome.Success, 0, "Raw Accel accepted the settings."),
            1 => new WriterResult(WriterOutcome.ReportedError, 1, "writer.exe reported an error (shown in its dialog). The driver settings were not changed."),
            var c => new WriterResult(WriterOutcome.UnexpectedExitCode, c, $"writer.exe exited with unexpected code {c}."),
        };
    }
}
