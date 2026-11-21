using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using DiffRapid.Core.IO;

namespace DiffRapid.Core.Sync;

/// <summary>
/// Deletes a file or an empty folder. The Visual Studio extension supplies one that uses the Recycle Bin. It is only
/// given files and empty folders that are not links, and must delete just that entry: never recurse.
/// </summary>
public delegate void SyncDeleteHandler(string path, bool isFolder);

/// <summary>What happened to one planned operation.</summary>
public enum SyncOperationStatus
{
    /// <summary>The operation changed the file system as planned.</summary>
    Done,

    /// <summary>Dry run: the operation would have run.</summary>
    WouldRun,

    /// <summary>The caller left the operation out.</summary>
    NotIncluded,

    /// <summary>A skip or conflict, which never changes anything.</summary>
    Skipped,

    /// <summary>The operation failed; <see cref="SyncOperationResult.Error"/> says why. Later operations still run.</summary>
    Failed,
}

/// <summary>The outcome of one operation.</summary>
public sealed record SyncOperationResult(SyncOperation Operation, SyncOperationStatus Status, string? Error);

/// <summary>Settings for running a sync plan.</summary>
public sealed record SyncExecuteOptions
{
    /// <summary>Report what would happen without touching the file system.</summary>
    public bool DryRun { get; init; }

    /// <summary>Deletes files and empty folders; null means a plain, permanent delete.</summary>
    public SyncDeleteHandler? Delete { get; init; }

    /// <summary>Receives each result as soon as its operation finishes.</summary>
    public IProgress<SyncOperationResult>? Progress { get; init; }
}

/// <summary>
/// Runs a <see cref="SyncPlan"/>. Files are copied to a temporary file next to the target, verified by size and
/// SHA-256 hash, and only then moved over the target. Links are never followed, and a target that changed since
/// the plan was made is left alone.
/// </summary>
public static class SyncExecutor
{
    private const int BufferSize = 64 * 1024;

    /// <summary>
    /// Executes the operations of <paramref name="plan"/> that <paramref name="include"/> accepts (all when null), in
    /// plan order. A failure is recorded and the run continues; cancellation throws
    /// <see cref="OperationCanceledException"/> between or during operations, after any partial copy is removed.
    /// </summary>
    public static IReadOnlyList<SyncOperationResult> Execute(
        SyncPlan plan,
        Func<SyncOperation, bool>? include = null,
        SyncExecuteOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        if (plan is null)
        {
            throw new ArgumentNullException(nameof(plan));
        }

        options ??= new SyncExecuteOptions();
        var results = new List<SyncOperationResult>(plan.Operations.Count);
        foreach (var operation in plan.Operations)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = ExecuteOne(operation, include, options, cancellationToken);
            results.Add(result);
            options.Progress?.Report(result);
        }

        return results.AsReadOnly();
    }

    private static SyncOperationResult ExecuteOne(
        SyncOperation operation,
        Func<SyncOperation, bool>? include,
        SyncExecuteOptions options,
        CancellationToken cancellationToken)
    {
        if (include is not null && !include(operation))
        {
            return new SyncOperationResult(operation, SyncOperationStatus.NotIncluded, null);
        }

        if (!operation.ChangesFiles)
        {
            return new SyncOperationResult(operation, SyncOperationStatus.Skipped, operation.Reason);
        }

        if (options.DryRun)
        {
            return new SyncOperationResult(operation, SyncOperationStatus.WouldRun, null);
        }

        try
        {
            Run(operation, options, cancellationToken);
            return new SyncOperationResult(operation, SyncOperationStatus.Done, null);
        }
        catch (IOException ex)
        {
            return new SyncOperationResult(operation, SyncOperationStatus.Failed, ex.Message);
        }
        catch (UnauthorizedAccessException ex)
        {
            return new SyncOperationResult(operation, SyncOperationStatus.Failed, ex.Message);
        }
    }

    private static void Run(SyncOperation operation, SyncExecuteOptions options, CancellationToken cancellationToken)
    {
        var target = operation.TargetPath ?? throw new IOException("The operation has no target path.");
        switch (operation.Action)
        {
            case SyncAction.CreateFolder:
                CreateFolder(target);
                break;
            case SyncAction.Copy:
                RequireAbsent(target);
                CopySafely(RequireSource(operation), target, cancellationToken);
                break;
            case SyncAction.Overwrite:
                RequireUnchanged(operation, target);
                CopySafely(RequireSource(operation), target, cancellationToken);
                break;
            case SyncAction.Delete:
                Delete(operation, target, options.Delete ?? DeletePermanently);
                break;
            default:
                throw new IOException($"Unknown sync action {operation.Action}.");
        }
    }

    private static void CreateFolder(string target)
    {
        if (File.Exists(target))
        {
            throw new IOException("A file already has the folder's name: " + target);
        }

        Directory.CreateDirectory(target);
    }

    private static string RequireSource(SyncOperation operation)
    {
        var source = operation.SourcePath ?? throw new IOException("The operation has no source path.");
        var attributes = File.GetAttributes(source);
        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException("Links are not followed: " + source);
        }

        if ((attributes & FileAttributes.Directory) != 0)
        {
            throw new IOException("The source is now a folder: " + source);
        }

        return source;
    }

    private static void RequireAbsent(string target)
    {
        if (File.Exists(target) || Directory.Exists(target))
        {
            throw new IOException("The target appeared after the plan was made: " + target);
        }
    }

    private static void RequireUnchanged(SyncOperation operation, string target)
    {
        var attributes = File.GetAttributes(target);
        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException("Links are not followed: " + target);
        }

        if ((attributes & FileAttributes.Directory) != 0 || operation.ExpectedTarget is not { } expected)
        {
            return;
        }

        var info = new FileInfo(target);
        if (info.Length != expected.Size || info.LastWriteTimeUtc != expected.LastWriteUtc)
        {
            throw new IOException("The target changed after the plan was made: " + target);
        }
    }

    private static void Delete(SyncOperation operation, string target, SyncDeleteHandler delete)
    {
        var attributes = File.GetAttributes(target);
        var isFolder = (attributes & FileAttributes.Directory) != 0;
        if (!isFolder)
        {
            RequireUnchanged(operation, target);
        }
        else if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            // A handler such as the Recycle Bin could act on what the link points to.
            throw new IOException("Links are not followed: " + target);
        }
        else if (Directory.EnumerateFileSystemEntries(target).Any())
        {
            // Contents are deleted one by one by earlier operations; anything left was not planned (or not included).
            throw new IOException("The folder is not empty: " + target);
        }

        delete(target, isFolder);
    }

    // A link is removed itself; Directory.Delete without recursion never touches what a folder link points to.
    private static void DeletePermanently(string path, bool isFolder)
    {
        if (isFolder)
        {
            Directory.Delete(path, recursive: false);
        }
        else
        {
            File.Delete(path);
        }
    }

    private static void CopySafely(string source, string target, CancellationToken cancellationToken)
    {
        // The copy is checked on disk against the source's hash before it replaces the target. A read-only target is
        // replaced as planned.
        byte[]? sourceHash = null;
        var lastWrite = DateTime.MinValue;
        AtomicFile.Write(
            target,
            output => sourceHash = CopyWithHash(source, output, cancellationToken),
            replaceReadOnly: true,
            beforeReplace: temp =>
            {
                Verify(source, temp, sourceHash!, cancellationToken);
                lastWrite = File.GetLastWriteTimeUtc(source);
                File.SetLastWriteTimeUtc(temp, lastWrite);
            });
        File.SetLastWriteTimeUtc(target, lastWrite);
    }

    private static byte[] CopyWithHash(string source, Stream output, CancellationToken cancellationToken)
    {
        using var hash = SHA256.Create();
        using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize);
        var buffer = new byte[BufferSize];
        int read;
        while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            hash.TransformBlock(buffer, 0, read, null, 0);
            output.Write(buffer, 0, read);
        }

        hash.TransformFinalBlock(buffer, 0, 0);
        return hash.Hash;
    }

    private static void Verify(string source, string temp, byte[] sourceHash, CancellationToken cancellationToken)
    {
        if (new FileInfo(temp).Length != new FileInfo(source).Length)
        {
            throw new IOException("The copied file has the wrong size: " + source);
        }

        if (!HashFile(temp, cancellationToken).SequenceEqual(sourceHash))
        {
            throw new IOException("The copied file does not match its source: " + source);
        }
    }

    private static byte[] HashFile(string path, CancellationToken cancellationToken)
    {
        using var hash = SHA256.Create();
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize);
        var buffer = new byte[BufferSize];
        int read;
        while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            hash.TransformBlock(buffer, 0, read, null, 0);
        }

        hash.TransformFinalBlock(buffer, 0, 0);
        return hash.Hash;
    }
}
