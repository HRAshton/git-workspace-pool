using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using GitWorkspacePool.Core.Application;

namespace GitWorkspacePool.Infrastructure;

/// <summary>
/// Provides a crash-safe inter-process lock without relying on thread-affine mutex ownership.
/// </summary>
public sealed class FileWorkspaceOperationLock : IWorkspaceOperationLock
{
    private readonly string _lockDirectory;

    /// <summary>
    /// Initializes the lock service in a user-owned directory.
    /// </summary>
    public FileWorkspaceOperationLock(string lockDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(lockDirectory);
        _lockDirectory = Path.GetFullPath(lockDirectory);
    }

    /// <inheritdoc/>
    public async Task<IAsyncDisposable> AcquireAsync(
        string workspacePath,
        CancellationToken cancellationToken
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspacePath);
        string canonicalPath = Path.GetFullPath(workspacePath).ToUpperInvariant();
        string name = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(canonicalPath))
        );
        Directory.CreateDirectory(_lockDirectory);
        string path = Path.Combine(_lockDirectory, name + ".lock");
        Stopwatch wait = Stopwatch.StartNew();

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                return new FileStream(
                    path,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    bufferSize: 1,
                    FileOptions.Asynchronous
                );
            }
            catch (IOException) when (wait.Elapsed < TimeSpan.FromSeconds(20))
            {
                await Task.Delay(40, cancellationToken);
            }
        }
    }
}
