using System.Text;
using System.Xml.Linq;

namespace OpenVector.RawAccel.Apply;

public enum LogonTaskState { NotInstalled, Enabled, Disabled, Unknown }

public sealed record LogonTaskStatus(LogonTaskState State, string? Command = null, string? Arguments = null, string? Detail = null)
{
    public bool Exists => State is LogonTaskState.Enabled or LogonTaskState.Disabled;
}

public sealed record LogonTaskResult(bool Succeeded, string Message);

/// <summary>
/// Manages the Task Scheduler task <c>\OpenVector\ReapplyRawAccel</c> (D4): at the user's logon it runs Raw Accel's
/// own writer.exe with the user's settings.json and exits. No OpenVector process is involved. Off by default.
/// Implemented with schtasks.exe and an XML task definition, through <see cref="IProcessRunner"/>.
/// </summary>
public sealed class RawAccelLogonTask
{
    public const string DefaultTaskName = @"\OpenVector\ReapplyRawAccel";
    private static readonly XNamespace Ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
    private readonly IProcessRunner _runner;

    public string TaskName { get; init; } = DefaultTaskName;
    public string SchtasksPath { get; init; } = Path.Combine(Environment.SystemDirectory is { Length: > 0 } s ? s : @"C:\Windows\System32", "schtasks.exe");
    public TimeSpan CommandTimeout { get; init; } = TimeSpan.FromSeconds(30);
    /// <summary>Delay after logon before writer.exe runs (gives device enumeration time).</summary>
    public TimeSpan LogonDelay { get; init; } = TimeSpan.FromSeconds(5);
    /// <summary>Directory for the temporary XML file passed to schtasks /XML.</summary>
    public string TempDirectory { get; init; } = Path.GetTempPath();

    public RawAccelLogonTask(IProcessRunner runner) => _runner = runner;

    /// <summary>Builds the task XML (Task Scheduler schema 1.2). <paramref name="userId"/> is DOMAIN\user.</summary>
    public string BuildTaskXml(string writerPath, string settingsPath, string userId)
    {
        string workDir = Path.GetDirectoryName(writerPath) ?? "";
        var doc = new XDocument(new XDeclaration("1.0", "UTF-16", null),
            new XElement(Ns + "Task", new XAttribute("version", "1.2"),
                new XElement(Ns + "RegistrationInfo",
                    new XElement(Ns + "Author", "OpenVector"),
                    new XElement(Ns + "Description", "Re-applies your Raw Accel settings.json at sign-in by running Raw Accel's writer.exe once. Created by OpenVector; remove it from OpenVector or here."),
                    new XElement(Ns + "URI", TaskName)),
                new XElement(Ns + "Triggers",
                    new XElement(Ns + "LogonTrigger",
                        new XElement(Ns + "Enabled", "true"),
                        new XElement(Ns + "UserId", userId),
                        new XElement(Ns + "Delay", System.Xml.XmlConvert.ToString(LogonDelay)))),
                new XElement(Ns + "Principals",
                    new XElement(Ns + "Principal", new XAttribute("id", "Author"),
                        new XElement(Ns + "UserId", userId),
                        new XElement(Ns + "LogonType", "InteractiveToken"),
                        new XElement(Ns + "RunLevel", "LeastPrivilege"))),
                new XElement(Ns + "Settings",
                    new XElement(Ns + "MultipleInstancesPolicy", "IgnoreNew"),
                    new XElement(Ns + "DisallowStartIfOnBatteries", "false"),
                    new XElement(Ns + "StopIfGoingOnBatteries", "false"),
                    new XElement(Ns + "AllowHardTerminate", "true"),
                    new XElement(Ns + "StartWhenAvailable", "false"),
                    new XElement(Ns + "RunOnlyIfNetworkAvailable", "false"),
                    new XElement(Ns + "IdleSettings",
                        new XElement(Ns + "StopOnIdleEnd", "false"),
                        new XElement(Ns + "RestartOnIdle", "false")),
                    new XElement(Ns + "AllowStartOnDemand", "true"),
                    new XElement(Ns + "Enabled", "true"),
                    new XElement(Ns + "Hidden", "false"),
                    new XElement(Ns + "RunOnlyIfIdle", "false"),
                    new XElement(Ns + "WakeToRun", "false"),
                    new XElement(Ns + "ExecutionTimeLimit", "PT1M"),
                    new XElement(Ns + "Priority", "7")),
                new XElement(Ns + "Actions", new XAttribute("Context", "Author"),
                    new XElement(Ns + "Exec",
                        new XElement(Ns + "Command", writerPath),
                        new XElement(Ns + "Arguments", Quote(settingsPath)),
                        new XElement(Ns + "WorkingDirectory", workDir)))));
        var sb = new StringBuilder();
        using (var w = new Utf16StringWriter(sb)) doc.Save(w);
        return sb.ToString();
    }

    public static string CurrentUserId() =>
        string.IsNullOrEmpty(Environment.UserDomainName) ? Environment.UserName : $"{Environment.UserDomainName}\\{Environment.UserName}";

    /// <summary>Creates or replaces the task (schtasks /Create /XML /F).</summary>
    public async Task<LogonTaskResult> EnableAsync(string writerPath, string settingsPath, string? userId = null, CancellationToken ct = default)
    {
        string xml = BuildTaskXml(Path.GetFullPath(writerPath), Path.GetFullPath(settingsPath), userId ?? CurrentUserId());
        Directory.CreateDirectory(TempDirectory);
        string tmp = Path.Combine(TempDirectory, $"openvector-task-{Guid.NewGuid():N}.xml");
        try
        {
            await File.WriteAllTextAsync(tmp, xml, new UnicodeEncoding(false, true), ct).ConfigureAwait(false);
            var r = await _runner.RunAsync(new ProcessSpec(SchtasksPath, new[] { "/Create", "/TN", TaskName, "/XML", tmp, "/F" }), CommandTimeout, ct).ConfigureAwait(false);
            return r is { Started: true, ExitCode: 0 }
                ? new LogonTaskResult(true, "Logon task created.")
                : new LogonTaskResult(false, Describe("create", r));
        }
        finally { try { File.Delete(tmp); } catch (IOException) { } }
    }

    /// <summary>Deletes the task. Succeeds if it did not exist.</summary>
    public async Task<LogonTaskResult> DisableAsync(CancellationToken ct = default)
    {
        var status = await QueryAsync(ct).ConfigureAwait(false);
        if (status.State == LogonTaskState.NotInstalled) return new LogonTaskResult(true, "Logon task was not installed.");
        var r = await _runner.RunAsync(new ProcessSpec(SchtasksPath, new[] { "/Delete", "/TN", TaskName, "/F" }), CommandTimeout, ct).ConfigureAwait(false);
        return r is { Started: true, ExitCode: 0 }
            ? new LogonTaskResult(true, "Logon task removed.")
            : new LogonTaskResult(false, Describe("delete", r));
    }

    /// <summary>schtasks /Query /TN name /XML: exit 0 + XML when present, non-zero when absent.</summary>
    public async Task<LogonTaskStatus> QueryAsync(CancellationToken ct = default)
    {
        var r = await _runner.RunAsync(new ProcessSpec(SchtasksPath, new[] { "/Query", "/TN", TaskName, "/XML" }), CommandTimeout, ct).ConfigureAwait(false);
        if (!r.Started || r.TimedOut) return new LogonTaskStatus(LogonTaskState.Unknown, Detail: Describe("query", r));
        if (r.ExitCode != 0) return new LogonTaskStatus(LogonTaskState.NotInstalled, Detail: r.StandardError.Trim());
        return ParseQueryXml(r.StandardOutput);
    }

    public static LogonTaskStatus ParseQueryXml(string xml)
    {
        try
        {
            int start = xml.IndexOf('<');
            var doc = XDocument.Parse(start > 0 ? xml[start..] : xml);
            var ns = doc.Root?.Name.Namespace ?? Ns;
            var settingsEnabled = doc.Root?.Element(ns + "Settings")?.Element(ns + "Enabled")?.Value;
            var exec = doc.Root?.Element(ns + "Actions")?.Element(ns + "Exec");
            bool enabled = !string.Equals(settingsEnabled?.Trim(), "false", StringComparison.OrdinalIgnoreCase);
            return new LogonTaskStatus(enabled ? LogonTaskState.Enabled : LogonTaskState.Disabled,
                exec?.Element(ns + "Command")?.Value, exec?.Element(ns + "Arguments")?.Value);
        }
        catch (System.Xml.XmlException e)
        {
            return new LogonTaskStatus(LogonTaskState.Unknown, Detail: "Could not parse schtasks output: " + e.Message);
        }
    }

    private static string Quote(string s) => "\"" + s.Replace("\"", "") + "\"";

    private static string Describe(string op, ProcessRunResult r) =>
        !r.Started ? $"schtasks {op} could not start: {r.LaunchError}" :
        r.TimedOut ? $"schtasks {op} timed out." :
        $"schtasks {op} failed (exit {r.ExitCode}): {(r.StandardError + " " + r.StandardOutput).Trim()}";

    private sealed class Utf16StringWriter(StringBuilder sb) : StringWriter(sb)
    {
        public override Encoding Encoding => Encoding.Unicode;
    }
}
