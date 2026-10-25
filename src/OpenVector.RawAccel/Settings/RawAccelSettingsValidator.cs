using System.Text.Json;
using System.Text.Json.Nodes;

namespace OpenVector.RawAccel.Settings;

public enum IssueSeverity { Warning, Error }

public sealed record ValidationIssue(IssueSeverity Severity, string Path, string Message)
{
    public override string ToString() => $"{Severity}: {Path}: {Message}";
}

public sealed record ValidationReport(IReadOnlyList<ValidationIssue> Issues)
{
    public bool IsValid => Issues.All(i => i.Severity != IssueSeverity.Error);
    public IEnumerable<ValidationIssue> Errors => Issues.Where(i => i.Severity == IssueSeverity.Error);
    public override string ToString() => string.Join(Environment.NewLine, Issues);
}

/// <summary>
/// Validates a settings document the way writer.exe would accept it: required keys and types
/// (Newtonsoft ItemRequired / StringEnumConverter), plus every check in common/rawaccel-validate.hpp
/// (v1.7.1). Missing AccelArgs/SpeedArgs keys are errors here, although writer.exe would silently use 0/false.
/// Unknown keys inside typed objects are warnings (they would be ignored by writer.exe).
/// </summary>
public static class RawAccelSettingsValidator
{
    public const int MaxNameLength = 256;   // MAX_NAME_LEN (incl. terminator)
    public const int MaxDeviceIdLength = 200;
    public const double DefaultTimeMin = 1000.0 / 8000 / 2;
    public const double DefaultTimeMax = 100;

    public static ValidationReport Validate(RawAccelSettingsDocument doc)
    {
        var k = doc.Keys;
        var issues = new List<ValidationIssue>();
        void Err(string path, string msg) => issues.Add(new(IssueSeverity.Error, path, msg));
        void Warn(string path, string msg) => issues.Add(new(IssueSeverity.Warning, path, msg));

        var root = doc.Root;
        if (root[k.Version]?.GetValueKind() != JsonValueKind.String) Err(k.Version, "missing version string");

        // defaultDeviceConfig
        if (root[k.DefaultDeviceConfig] is JsonObject ddc) CheckDeviceConfig(ddc, k.DefaultDeviceConfig);
        else Err(k.DefaultDeviceConfig, "missing or not an object");

        // profiles
        var profileNames = new HashSet<string>(StringComparer.Ordinal);
        if (root[k.Profiles] is JsonArray profiles)
        {
            if (profiles.Count == 0) Warn(k.Profiles, "no profiles (writer.exe would add a default profile)");
            for (int i = 0; i < profiles.Count; i++)
            {
                string path = $"{k.Profiles}[{i}]";
                if (profiles[i] is not JsonObject p) { Err(path, "not an object"); continue; }
                CheckProfile(p, path);
                if (p[k.Name] is JsonValue nv && nv.TryGetValue<string>(out var name) && !profileNames.Add(name))
                    Warn(path, $"duplicate profile name '{name}'");
            }
        }
        else Err(k.Profiles, "missing or not an array");

        // devices
        if (root[k.Devices] is JsonArray devices)
        {
            for (int i = 0; i < devices.Count; i++)
            {
                string path = $"{k.Devices}[{i}]";
                if (devices[i] is not JsonObject d) { Err(path, "not an object"); continue; }
                foreach (var key in k.DeviceKeys) if (d[key] is null) Err($"{path}.{key}", "required key missing");
                foreach (var key in new[] { k.DeviceName, k.DeviceProfile, k.DeviceId })
                    if (d[key] is not null && d[key]!.GetValueKind() != JsonValueKind.String) Err($"{path}.{key}", "must be a string");
                if (d[k.DeviceId] is JsonValue idv && idv.TryGetValue<string>(out var id) && id.Length >= MaxDeviceIdLength)
                    Warn($"{path}.{k.DeviceId}", "device id longer than the driver's buffer; it will be truncated");
                if (d[k.DeviceConfigKey] is JsonObject cfg) CheckDeviceConfig(cfg, $"{path}.{k.DeviceConfigKey}");
                else if (d[k.DeviceConfigKey] is not null) Err($"{path}.{k.DeviceConfigKey}", "not an object");
            }
        }
        else Err(k.Devices, "missing or not an array");

        return new ValidationReport(issues);

        void CheckProfile(JsonObject p, string path)
        {
            foreach (var key in k.ProfileKeys) if (p[key] is null) Err($"{path}.{key}", "required key missing");
            foreach (var kv in p) if (!k.ProfileKeys.Contains(kv.Key)) Warn($"{path}.{kv.Key}", "unknown key (ignored by writer.exe; wrong Raw Accel version?)");

            string? name = p[k.Name] is JsonValue nv && nv.TryGetValue<string>(out var s) ? s : null;
            if (name is null) Err($"{path}.{k.Name}", "must be a string");
            else if (name.Length == 0) Err($"{path}.{k.Name}", "profile name can not be empty");
            else if (name.Length >= MaxNameLength) Warn($"{path}.{k.Name}", "name longer than the driver's buffer; it will be truncated");

            var speedMax = N(p, k.SpeedCap, path);
            if (speedMax < 0) Err($"{path}.{k.SpeedCap}", "speed cap is negative");
            var snap = N(p, k.Snap, path);
            if (snap is < 0 or > 45) Err($"{path}.{k.Snap}", "snap angle must be between 0 and 45 degrees");
            N(p, k.Rotation, path);
            if (N(p, k.OutputDpi, path) == 0) Err($"{path}.{k.OutputDpi}", "output DPI is 0");
            if (N(p, k.YxRatio, path) == 0) Err($"{path}.{k.YxRatio}", "Y/X output DPI ratio is 0");
            var lr = N(p, k.LrRatio, path); var ud = N(p, k.UdRatio, path);
            if (lr is <= 0 || ud is <= 0) Err(path, "output DPI ratio must be positive");

            var dom = V2(p, k.DomainWeights, path);
            if (dom is { } dw && (dw.x <= 0 || dw.y <= 0)) Err($"{path}.{k.DomainWeights}", "domain weights must be positive");
            var rng = V2(p, k.RangeWeights, path);
            if (rng is { } rw && (rw.x < 0 || rw.y < 0)) Err($"{path}.{k.RangeWeights}", "range weights must be positive");

            bool whole = true;
            if (p[k.SpeedArgs] is JsonObject sp)
            {
                foreach (var key in k.SpeedArgKeys) if (sp[key] is null) Err($"{path}.{k.SpeedArgs}.{key}", "key missing (writer.exe would silently use 0/false)");
                foreach (var kv in sp) if (!k.SpeedArgKeys.Contains(kv.Key)) Warn($"{path}.{k.SpeedArgs}.{kv.Key}", "unknown key");
                if (sp[k.Whole] is JsonValue wv && wv.TryGetValue<bool>(out var wb)) whole = wb;
                else if (sp[k.Whole] is not null) Err($"{path}.{k.SpeedArgs}.{k.Whole}", "must be a boolean");
                if (N(sp, k.LpNorm, $"{path}.{k.SpeedArgs}") is <= 0) Err($"{path}.{k.SpeedArgs}.{k.LpNorm}", "Lp norm must be positive (default=2)");
                N(sp, k.InputSmoothHalflife, $"{path}.{k.SpeedArgs}"); N(sp, k.ScaleSmoothHalflife, $"{path}.{k.SpeedArgs}"); N(sp, k.OutputSmoothHalflife, $"{path}.{k.SpeedArgs}");
            }
            else if (p[k.SpeedArgs] is not null) Err($"{path}.{k.SpeedArgs}", "not an object");

            if (p[k.AccelX] is JsonObject ax) CheckAccel(ax, $"{path}.{k.AccelX}", true);
            else if (p[k.AccelX] is not null) Err($"{path}.{k.AccelX}", "not an object");
            if (p[k.AccelY] is JsonObject ay) CheckAccel(ay, $"{path}.{k.AccelY}", !whole);
            else if (p[k.AccelY] is not null) Err($"{path}.{k.AccelY}", "not an object");
        }

        void CheckAccel(JsonObject a, string path, bool semantic)
        {
            foreach (var key in k.AccelArgKeys) if (a[key] is null) Err($"{path}.{key}", "key missing (writer.exe would silently use 0/false/'classic')");
            foreach (var kv in a) if (!k.AccelArgKeys.Contains(kv.Key)) Warn($"{path}.{kv.Key}", "unknown key");
            string? mode = a[k.Mode] is JsonValue mv && mv.TryGetValue<string>(out var ms) ? ms : null;
            if (a[k.Mode] is not null && (mode is null || !k.AccelModes.Contains(mode))) Err($"{path}.{k.Mode}", $"invalid accel mode (expected one of {string.Join(", ", k.AccelModes)})");
            string? capMode = a[k.CapMode] is JsonValue cv && cv.TryGetValue<string>(out var cs) ? cs : null;
            if (a[k.CapMode] is not null && (capMode is null || !k.CapModes.Contains(capMode))) Err($"{path}.{k.CapMode}", $"invalid cap mode (expected one of {string.Join(", ", k.CapModes)})");
            if (a[k.Gain] is not null && !(a[k.Gain] is JsonValue gv && gv.TryGetValue<bool>(out _))) Err($"{path}.{k.Gain}", "must be a boolean");

            int length = 0;
            if (a[k.Data] is JsonArray data)
            {
                length = data.Count;
                for (int i = 0; i < data.Count; i++)
                    if (!(data[i] is JsonValue dv && dv.TryGetValue<double>(out var f) && double.IsFinite(f))) { Err($"{path}.{k.Data}[{i}]", "not a number"); break; }
            }
            else if (a[k.Data] is not null) Err($"{path}.{k.Data}", "not an array");

            // a missing AccelArgs key is deserialised as 0 by writer.exe; mirror that for the checks below
            double A(string key) => N(a, key, path) ?? 0;
            double inOff = A(k.InputOffset), outOff = A(k.OutputOffset), acc = A(k.Acceleration),
                   decay = A(k.DecayRate), gamma = A(k.Gamma), mot = A(k.Motivity),
                   expC = A(k.ExponentClassic), scale = A(k.Scale), expP = A(k.ExponentPower),
                   limit = A(k.Limit), sync = A(k.SyncSpeed), smooth = A(k.Smooth);
            var cap = V2(a, k.Cap, path) ?? (0, 0);
            if (!semantic) return; // validate.hpp only checks argsY in by-component mode

            const int raw = 514;
            if (mode == "lut")
            {
                if (length < 4) Err($"{path}.{k.Data}", "lookup mode requires at least 2 points");
                else if (length > raw) Err($"{path}.{k.Data}", "too many data points (max=257)");
            }
            else if (length > raw) Err($"{path}.{k.Data}", "data size > max");
            if (inOff < 0) Err($"{path}.{k.InputOffset}", "offset can not be negative");
            if (outOff < 0) Err($"{path}.{k.OutputOffset}", "offset can not be negative");
            bool jumpOrIoCap = mode == "jump" || ((mode == "classic" || mode == "power") && capMode == "in_out");
            if (cap.x < 0) Err($"{path}.{k.Cap}", "cap (input) can not be negative");
            else if (cap.x == 0 && jumpOrIoCap) Err($"{path}.{k.Cap}", "cap (input) can not be 0");
            if (cap.y < 0) Err($"{path}.{k.Cap}", "cap (output) can not be negative");
            else if (cap.y == 0 && jumpOrIoCap) Err($"{path}.{k.Cap}", "cap (output) can not be 0");
            if ((mode == "classic" && cap.x > 0 && cap.x < inOff && capMode != "output") ||
                (mode == "power" && cap.y > 0 && cap.y < outOff && capMode != "input"))
                Err($"{path}.{k.Cap}", "cap < offset");
            if (acc <= 0) Err($"{path}.{k.Acceleration}", "acceleration must be positive");
            if (scale <= 0) Err($"{path}.{k.Scale}", "scale must be positive");
            if (gamma <= 0) Err($"{path}.{k.Gamma}", "gamma must be positive");
            if (decay <= 0) Err($"{path}.{k.DecayRate}", "decay rate must be positive");
            if (mot <= 1) Err($"{path}.{k.Motivity}", "motivity must be greater than 1");
            if (expC <= 1) Err($"{path}.{k.ExponentClassic}", "exponent must be greater than 1");
            if (expP <= 0) Err($"{path}.{k.ExponentPower}", "exponent must be positive");
            if (limit <= 0) Err($"{path}.{k.Limit}", "limit must be positive");
            if (sync <= 0) Err($"{path}.{k.SyncSpeed}", "synchronous speed must be positive");
            if (smooth is < 0 or > 1) Err($"{path}.{k.Smooth}", "smooth must be between 0 and 1");
        }

        void CheckDeviceConfig(JsonObject c, string path)
        {
            foreach (var key in k.DeviceConfigRequiredKeys) if (c[key] is null) Err($"{path}.{key}", "required key missing");
            foreach (var kv in c) if (!k.DeviceConfigRequiredKeys.Contains(kv.Key) && !k.DeviceConfigOptionalKeys.Contains(kv.Key)) Warn($"{path}.{kv.Key}", "unknown key");
            foreach (var key in new[] { k.Disable, k.SetExtraInfo, k.PollTimeLock })
                if (c[key] is not null && !(c[key] is JsonValue bv && bv.TryGetValue<bool>(out _))) Err($"{path}.{key}", "must be a boolean");
            int? dpi = I(c, k.Dpi, path), rate = I(c, k.PollingRate, path);
            if (dpi < 0) Err($"{path}.{k.Dpi}", "dpi can not be negative");
            if (rate < 0) Err($"{path}.{k.PollingRate}", "polling rate can not be negative");
            double min = c[k.MinimumTime] is null ? DefaultTimeMin : N(c, k.MinimumTime, path) ?? DefaultTimeMin;
            double max = c[k.MaximumTime] is null ? DefaultTimeMax : N(c, k.MaximumTime, path) ?? DefaultTimeMax;
            if (min <= 0) Err($"{path}.{k.MinimumTime}", "minimum time must be positive");
            if (max < min) Err($"{path}.{k.MaximumTime}", "max time is less than min time");
        }

        double? N(JsonObject o, string key, string path)
        {
            var n = o[key];
            if (n is null) return null; // reported as missing elsewhere
            if (n is JsonValue v && v.TryGetValue<double>(out var d) && double.IsFinite(d)) return d;
            Err($"{path}.{key}", "must be a number"); return null;
        }

        int? I(JsonObject o, string key, string path)
        {
            var n = o[key];
            if (n is null) return null;
            if (n is JsonValue v && v.TryGetValue<int>(out var d)) return d;
            Err($"{path}.{key}", "must be an integer"); return null;
        }

        (double x, double y)? V2(JsonObject o, string key, string path)
        {
            if (o[key] is null) return null;
            if (o[key] is not JsonObject v) { Err($"{path}.{key}", "not an object"); return null; }
            var x = N(v, k.X, $"{path}.{key}"); var y = N(v, k.Y, $"{path}.{key}");
            if (v[k.X] is null) Err($"{path}.{key}.{k.X}", "key missing");
            if (v[k.Y] is null) Err($"{path}.{key}.{k.Y}", "key missing");
            return x is null || y is null ? null : (x.Value, y.Value);
        }
    }
}
