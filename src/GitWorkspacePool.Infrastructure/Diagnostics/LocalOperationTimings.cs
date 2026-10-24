using System.Text;
using System.Text.Json;
using GitWorkspacePool.Core.Domain;
using GitWorkspacePool.Infrastructure.Snapshots;

namespace GitWorkspacePool.Infrastructure.Diagnostics;

/// <summary>Bounded local timing evidence. Call only when the user explicitly enabled timing diagnostics.</summary>
public sealed class LocalOperationTimings
{
    private const int MaxBytes = 1024 * 1024;
    private readonly string _path;
    private readonly object _gate = new();

    /// <summary>Uses a user-owned diagnostics file, separate from repositories and recovery evidence.</summary>
    public LocalOperationTimings(string path)
    {
        _path = Path.GetFullPath(path);
    }

    /// <summary>Records only fixed operation/outcome categories and elapsed time, rotating bounded diagnostic data.</summary>
    public void Record(WorkspaceOperation operation, TimeSpan elapsed, WorkspaceOperationOutcome outcome)
    {
        if (!Enum.IsDefined(operation) || !Enum.IsDefined(outcome) || elapsed < TimeSpan.Zero
            || elapsed > TimeSpan.FromDays(1))
        {
            throw new ArgumentException("Invalid operation timing.");
        }
        byte[] line = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(
            new WorkspaceTimingSample(DateTimeOffset.UtcNow, operation, elapsed.TotalMilliseconds, outcome)) + "\n");
        lock (_gate)
        {
            WorkspaceSnapshotPathPolicy.EnsureNoLinksInAncestors(_path);
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            if (File.Exists(_path) && new FileInfo(_path).Length + line.Length > MaxBytes)
            {
                WorkspaceSnapshotPathPolicy.EnsureNoLinksInAncestors(_path + ".previous");
                File.Move(_path, _path + ".previous", overwrite: true);
            }
            using FileStream stream = new(_path, FileMode.Append, FileAccess.Write, FileShare.Read);
            stream.Write(line);
        }
    }

    /// <summary>Returns the current bounded samples and counts incomplete/unrecognizable records explicitly.</summary>
    public (IReadOnlyList<WorkspaceTimingSample> Samples, int UnreadableRecords) Read()
    {
        lock (_gate)
        {
            WorkspaceSnapshotPathPolicy.EnsureNoLinksInAncestors(_path);
            if (!File.Exists(_path)) return (Array.Empty<WorkspaceTimingSample>(), 0);
            using FileStream stream = new(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            long length = stream.Length;
            if (length > MaxBytes) throw new InvalidOperationException("Timing file exceeds the safety limit.");
            byte[] bytes = new byte[checked((int)length)];
            stream.ReadExactly(bytes);
            List<WorkspaceTimingSample> samples = [];
            int unreadable = 0;
            foreach (string line in Encoding.UTF8.GetString(bytes).Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                try
                {
                    using JsonDocument document = JsonDocument.Parse(line);
                    JsonElement root = document.RootElement;
                    if (root.ValueKind != JsonValueKind.Object
                        || !root.TryGetProperty("Timestamp", out _)
                        || !root.TryGetProperty("Operation", out _)
                        || !root.TryGetProperty("ElapsedMilliseconds", out _)
                        || !root.TryGetProperty("Outcome", out _))
                    {
                        unreadable++;
                        continue;
                    }
                    WorkspaceTimingSample? sample = root.Deserialize<WorkspaceTimingSample>();
                    if (sample is null || sample.Timestamp == default || !Enum.IsDefined(sample.Operation) || !Enum.IsDefined(sample.Outcome)
                        || !double.IsFinite(sample.ElapsedMilliseconds) || sample.ElapsedMilliseconds < 0
                        || sample.ElapsedMilliseconds > TimeSpan.FromDays(1).TotalMilliseconds)
                    {
                        unreadable++;
                    }
                    else samples.Add(sample);
                }
                catch (JsonException) { unreadable++; }
            }
            return (samples, unreadable);
        }
    }

    /// <summary>Calculates nearest-rank percentiles from completed application timings, not visible-editor latency.</summary>
    public static double Percentile(IEnumerable<double> values, double percentile)
    {
        if (!double.IsFinite(percentile) || percentile <= 0 || percentile > 1) throw new ArgumentOutOfRangeException(nameof(percentile));
        double[] sorted = values.Order().ToArray();
        return sorted.Length == 0 ? 0 : sorted[(int)Math.Ceiling(percentile * sorted.Length) - 1];
    }
}
