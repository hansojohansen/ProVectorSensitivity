using OpenVector.RawAccel.Apply;

namespace OpenVector.RawAccel.Tests;

internal sealed class FakeProcessRunner : IProcessRunner
{
    public List<ProcessSpec> Calls { get; } = new();
    public Func<ProcessSpec, ProcessRunResult> Writer { get; set; } = _ => new ProcessRunResult { Started = true, ExitCode = 0 };
    /// <summary>Simulated Task Scheduler: task XML by name.</summary>
    public Dictionary<string, string> Tasks { get; } = new(StringComparer.OrdinalIgnoreCase);
    public string? LastTaskXml { get; private set; }
    public byte[]? SettingsBytesSeenByWriter { get; private set; }

    public Task<ProcessRunResult> RunAsync(ProcessSpec spec, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        Calls.Add(spec);
        if (spec.FileName.EndsWith("writer.exe", StringComparison.OrdinalIgnoreCase))
        {
            SettingsBytesSeenByWriter = File.Exists(spec.Arguments[0]) ? File.ReadAllBytes(spec.Arguments[0]) : null;
            return Task.FromResult(Writer(spec));
        }
        if (spec.FileName.EndsWith("schtasks.exe", StringComparison.OrdinalIgnoreCase)) return Task.FromResult(Schtasks(spec.Arguments));
        return Task.FromResult(new ProcessRunResult { LaunchError = "unknown program" });
    }

    private ProcessRunResult Schtasks(IReadOnlyList<string> a)
    {
        string name = a[a.ToList().IndexOf("/TN") + 1];
        switch (a[0])
        {
            case "/Create":
                string path = a[a.ToList().IndexOf("/XML") + 1];
                LastTaskXml = File.ReadAllText(path); // UTF-16 with BOM is detected
                Tasks[name] = LastTaskXml;
                return new ProcessRunResult { Started = true, ExitCode = 0, StandardOutput = "SUCCESS" };
            case "/Delete":
                return Tasks.Remove(name) ? new ProcessRunResult { Started = true, ExitCode = 0 }
                    : new ProcessRunResult { Started = true, ExitCode = 1, StandardError = "ERROR: The system cannot find the file specified." };
            case "/Query":
                return Tasks.TryGetValue(name, out var xml) ? new ProcessRunResult { Started = true, ExitCode = 0, StandardOutput = xml }
                    : new ProcessRunResult { Started = true, ExitCode = 1, StandardError = "ERROR: The system cannot find the file specified." };
        }
        return new ProcessRunResult { Started = true, ExitCode = 1 };
    }

    public int WriterCalls => Calls.Count(c => c.FileName.EndsWith("writer.exe", StringComparison.OrdinalIgnoreCase));
}

internal sealed class FakeDriver(DriverProbeResult result) : IRawAccelDriverProbe
{
    public DriverProbeResult Result { get; set; } = result;
    public DriverProbeResult GetVersion() => Result;
    public static FakeDriver V170 => new(new DriverProbeResult(true, new Version(1, 7, 0)));
}

internal sealed class FakeVersions(Version? v) : IFileVersionReader
{
    public Version? GetFileVersion(string path) => v;
}
