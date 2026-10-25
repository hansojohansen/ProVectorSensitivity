using System.Diagnostics;
using System.Text;

namespace OpenVector.RawAccel.Apply;

public sealed record ProcessSpec(string FileName, IReadOnlyList<string> Arguments, string? WorkingDirectory = null);

public sealed record ProcessRunResult
{
    public bool Started { get; init; }
    public int? ExitCode { get; init; }
    public bool TimedOut { get; init; }
    public string StandardOutput { get; init; } = "";
    public string StandardError { get; init; } = "";
    /// <summary>Launch failure message (file not found, access denied...).</summary>
    public string? LaunchError { get; init; }
}

/// <summary>Abstraction over process launching so writer.exe / schtasks.exe logic is testable without Windows.</summary>
public interface IProcessRunner
{
    Task<ProcessRunResult> RunAsync(ProcessSpec spec, TimeSpan timeout, CancellationToken cancellationToken = default);
}

/// <summary>Real implementation (System.Diagnostics.Process). Kills the process tree on timeout.</summary>
public sealed class ProcessRunner : IProcessRunner
{
    public async Task<ProcessRunResult> RunAsync(ProcessSpec spec, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        var psi = new ProcessStartInfo(spec.FileName)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in spec.Arguments) psi.ArgumentList.Add(a);
        if (spec.WorkingDirectory is not null) psi.WorkingDirectory = spec.WorkingDirectory;

        using var p = new Process { StartInfo = psi };
        try
        {
            if (!p.Start()) return new ProcessRunResult { LaunchError = "process did not start" };
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or PlatformNotSupportedException)
        {
            return new ProcessRunResult { LaunchError = e.Message };
        }
        var stdout = p.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = p.StandardError.ReadToEndAsync(cancellationToken);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);
        try
        {
            await p.WaitForExitAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            cancellationToken.ThrowIfCancellationRequested();
            return new ProcessRunResult { Started = true, TimedOut = true };
        }
        return new ProcessRunResult
        {
            Started = true,
            ExitCode = p.ExitCode,
            StandardOutput = await stdout.ConfigureAwait(false),
            StandardError = await stderr.ConfigureAwait(false),
        };
    }
}
