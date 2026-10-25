using System.Globalization;

namespace OpenVector.RawAccel.Settings;

public sealed record BackupInfo(string Path, DateTimeOffset CreatedUtc, string Sha256);

/// <summary>
/// Timestamped copies of the user's settings.json, default dir %LOCALAPPDATA%\OpenVector\backups.
/// File name: settings-yyyyMMddTHHmmssfffZ.json (a numeric suffix is added on collision).
/// </summary>
public sealed class BackupStore
{
    private const string Prefix = "settings-";
    private const string Format = "yyyyMMdd'T'HHmmssfff'Z'";
    private readonly TimeProvider _time;

    public string Directory { get; }
    /// <summary>Oldest backups beyond this count are deleted after each new backup (0 = keep all).</summary>
    public int MaxBackups { get; init; } = 200;

    public BackupStore(string? directory = null, TimeProvider? time = null)
    {
        Directory = directory ?? DefaultDirectory;
        _time = time ?? TimeProvider.System;
    }

    public static string DefaultDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenVector", "backups");

    public BackupInfo Create(string sourcePath)
    {
        System.IO.Directory.CreateDirectory(Directory);
        var now = _time.GetUtcNow();
        var bytes = File.ReadAllBytes(sourcePath);
        string stem = Prefix + now.UtcDateTime.ToString(Format, CultureInfo.InvariantCulture);
        string path = Path.Combine(Directory, stem + ".json");
        for (int i = 1; File.Exists(path); i++) path = Path.Combine(Directory, $"{stem}-{i}.json");
        using (var fs = new FileStream(path, FileMode.CreateNew, FileAccess.Write)) fs.Write(bytes);
        Prune();
        return new BackupInfo(path, now, RawAccelSettingsDocument.Sha256Hex(bytes));
    }

    /// <summary>Backups, newest first.</summary>
    public IReadOnlyList<BackupInfo> List()
    {
        if (!System.IO.Directory.Exists(Directory)) return Array.Empty<BackupInfo>();
        var list = new List<(BackupInfo info, int seq)>();
        foreach (var f in System.IO.Directory.EnumerateFiles(Directory, Prefix + "*.json"))
        {
            string name = Path.GetFileNameWithoutExtension(f)[Prefix.Length..];
            string ts = name.Length >= 19 ? name[..19] : name;
            if (!DateTime.TryParseExact(ts, Format, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var dt)) continue;
            int seq = name.Length > 20 && name[19] == '-' && int.TryParse(name[20..], NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : 0;
            list.Add((new BackupInfo(f, new DateTimeOffset(dt, TimeSpan.Zero), RawAccelSettingsDocument.Sha256Hex(File.ReadAllBytes(f))), seq));
        }
        return list.OrderByDescending(b => b.info.CreatedUtc).ThenByDescending(b => b.seq).Select(b => b.info).ToList();
    }

    /// <summary>Copies a backup over the destination atomically (write temp + rename).</summary>
    public void Restore(BackupInfo backup, string destinationPath) => AtomicFile.WriteAllBytes(destinationPath, File.ReadAllBytes(backup.Path));

    private void Prune()
    {
        if (MaxBackups <= 0) return;
        foreach (var old in List().Skip(MaxBackups)) File.Delete(old.Path);
    }
}

internal static class AtomicFile
{
    public static void WriteAllBytes(string path, byte[] bytes)
    {
        string dir = Path.GetDirectoryName(Path.GetFullPath(path))!;
        System.IO.Directory.CreateDirectory(dir);
        string tmp = Path.Combine(dir, "." + Path.GetFileName(path) + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp");
        try
        {
            using (var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write)) { fs.Write(bytes); fs.Flush(true); }
            File.Move(tmp, path, overwrite: true);
        }
        finally { if (File.Exists(tmp)) File.Delete(tmp); }
    }
}
