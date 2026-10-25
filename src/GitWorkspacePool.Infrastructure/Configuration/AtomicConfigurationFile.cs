using System.Security.Cryptography;
using System.Text;

namespace GitWorkspacePool.Infrastructure.Configuration;

/// <summary>A bounded settings read with a fingerprint for conflict-aware editing.</summary>
public sealed record ConfigurationSnapshot(string Text, string Fingerprint);

/// <summary>Publishes settings atomically while retaining the previous complete file as a backup.</summary>
public sealed class AtomicConfigurationFile
{
    private const int MaxBytes = 1024 * 1024;
    private readonly string _path;

    /// <summary>Uses an explicit user-owned settings path; links are never followed.</summary>
    public AtomicConfigurationFile(string path)
    {
        _path = Path.GetFullPath(path);
    }

    /// <summary>Reads a bounded UTF-8 file and fingerprints the exact bytes, including any BOM.</summary>
    public ConfigurationSnapshot Read()
    {
        WorkspacePathSafety.EnsureNoLinksInAncestors(_path);
        using FileStream stream = new(_path, FileMode.Open, FileAccess.Read, FileShare.Read);
        long length = stream.Length;
        if (length > MaxBytes)
        {
            throw new InvalidOperationException("Settings exceed the 1 MiB safety limit.");
        }
        byte[] bytes = new byte[checked((int)length)];
        stream.ReadExactly(bytes);
        string text = new UTF8Encoding(false, true).GetString(bytes).TrimStart('\uFEFF');
        return new ConfigurationSnapshot(text, Convert.ToHexString(SHA256.HashData(bytes)));
    }

    /// <summary>Rejects a stale edit and atomically preserves the previous settings as settings.json.previous.</summary>
    public ConfigurationSnapshot Save(string text, string expectedFingerprint)
    {
        byte[] bytes = new UTF8Encoding(false, true).GetBytes(text);
        if (bytes.Length > MaxBytes)
        {
            throw new InvalidOperationException("Settings exceed the 1 MiB safety limit.");
        }
        WorkspacePathSafety.EnsureNoLinksInAncestors(_path);
        WorkspacePathSafety.EnsureNoLinksInAncestors(_path + ".lock");
        WorkspacePathSafety.EnsureNoLinksInAncestors(_path + ".previous");
        using FileStream ownership = new(_path + ".lock", FileMode.OpenOrCreate,
            FileAccess.ReadWrite, FileShare.None);
        ConfigurationSnapshot current = Read();
        if (!current.Fingerprint.Equals(expectedFingerprint, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Settings changed while this window was open. Reload before saving; no edit was overwritten.");
        }
        string pending = _path + ".pending-" + Guid.NewGuid().ToString("N");
        using (FileStream target = new(pending, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            target.Write(bytes);
            target.Flush(flushToDisk: true);
        }
        // File.Replace keeps either the old or new complete settings at the destination.
        // On failure the pending file remains available and the live file is not truncated.
        File.Replace(pending, _path, _path + ".previous");
        return new ConfigurationSnapshot(text, Convert.ToHexString(SHA256.HashData(bytes)));
    }
}
