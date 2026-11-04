using System.Diagnostics;
using DiffRapid.Core.Folders;
using DiffRapid.Core.Sync;

namespace DiffRapid.Core.Tests;

public sealed class SyncTests : IDisposable
{
    private static readonly DateTime Old = new(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime New = new(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "diffrapid-sync-" + Guid.NewGuid());
    private string Left => Path.Combine(_root, "left");
    private string Right => Path.Combine(_root, "right");

    public SyncTests()
    {
        Directory.CreateDirectory(Left);
        Directory.CreateDirectory(Right);
    }

    public void Dispose()
    {
        foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }

        Directory.Delete(_root, true);
    }

    private string Write(bool left, string path, string text, DateTime? time = null)
    {
        var full = Path.Combine(left ? Left : Right, path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, text);
        File.SetLastWriteTimeUtc(full, time ?? Old);
        return full;
    }

    private string Read(bool left, string path) => File.ReadAllText(Path.Combine(left ? Left : Right, path));

    private bool Exists(bool left, string path)
    {
        var full = Path.Combine(left ? Left : Right, path);
        return File.Exists(full) || Directory.Exists(full);
    }

    private SyncPlan Plan(SyncMode mode, FolderCompareOptions? compare = null, FolderFilter? filter = null, DateTime? lastSync = null) =>
        SyncPlanner.Plan(FolderComparer.Compare(Left, Right, compare), new SyncProfile { Mode = mode, Filter = filter, LastSyncUtc = lastSync });

    private static SyncOperation Op(SyncPlan plan, string path) => Assert.Single(plan.Operations, o => o.RelativePath == path);

    private string Snapshot() => string.Join("\n", Directory.EnumerateFileSystemEntries(_root, "*", SearchOption.AllDirectories)
        .OrderBy(p => p, StringComparer.Ordinal)
        .Select(p => p + "|" + (File.Exists(p) ? File.ReadAllText(p) + "|" + File.GetLastWriteTimeUtc(p).Ticks : "dir")));

    private void AssertSidesIdentical() =>
        Assert.Equal(FolderCompareStatus.Identical, FolderComparer.Compare(Left, Right).Root.Status);

    [Fact]
    public void Mirror_left_to_right_makes_the_right_an_exact_copy()
    {
        Write(true, "same.txt", "same");
        Write(false, "same.txt", "same");
        Write(true, "changed.txt", "left", New);
        Write(false, "changed.txt", "right", New);
        Write(true, "new/deep/file.txt", "added");
        Write(false, "gone/deep/file.txt", "removed");
        Write(false, "extra.txt", "removed");

        var plan = Plan(SyncMode.MirrorLeftToRight);
        Assert.Equal(SyncAction.Overwrite, Op(plan, "changed.txt").Action);
        Assert.Equal(SyncAction.CreateFolder, Op(plan, "new").Action);
        Assert.Equal(SyncAction.Copy, Op(plan, "new/deep/file.txt").Action);
        Assert.Equal(SyncAction.Delete, Op(plan, "gone").Action);
        Assert.Equal(SyncAction.Delete, Op(plan, "extra.txt").Action);
        Assert.DoesNotContain(plan.Operations, o => o.RelativePath == "same.txt");
        Assert.All(plan.Operations, o => Assert.Equal(SyncDirection.LeftToRight, o.Direction));

        // Folder contents are deleted before the folder itself.
        var ops = plan.Operations.Select(o => o.RelativePath).ToList();
        Assert.True(ops.IndexOf("gone/deep/file.txt") < ops.IndexOf("gone/deep"));
        Assert.True(ops.IndexOf("gone/deep") < ops.IndexOf("gone"));

        var results = SyncExecutor.Execute(plan);
        Assert.All(results, r => Assert.Equal(SyncOperationStatus.Done, r.Status));
        AssertSidesIdentical();
        Assert.Equal("left", Read(false, "changed.txt"));
        Assert.False(Exists(false, "gone"));
        Assert.Empty(Directory.EnumerateFiles(_root, "*.tmp", SearchOption.AllDirectories));
        Assert.True(Plan(SyncMode.MirrorLeftToRight).IsEmpty);
    }

    [Fact]
    public void Mirror_right_to_left_changes_only_the_left()
    {
        Write(true, "changed.txt", "left");
        Write(false, "changed.txt", "right");
        Write(true, "extra.txt", "removed");
        Write(false, "new.txt", "added", New);

        var plan = Plan(SyncMode.MirrorRightToLeft);
        Assert.All(plan.Operations, o => Assert.Equal(SyncDirection.RightToLeft, o.Direction));
        SyncExecutor.Execute(plan);

        AssertSidesIdentical();
        Assert.Equal("right", Read(true, "changed.txt"));
        Assert.False(Exists(true, "extra.txt"));
        Assert.Equal(New, File.GetLastWriteTimeUtc(Path.Combine(Left, "new.txt")));
    }

    [Fact]
    public void Update_copies_missing_and_newer_files_and_never_deletes()
    {
        Write(true, "left-newer.txt", "new", New);
        Write(false, "left-newer.txt", "old", Old);
        Write(true, "right-newer.txt", "old", Old);
        Write(false, "right-newer.txt", "new", New);
        Write(true, "same-time.txt", "a", Old);
        Write(false, "same-time.txt", "b", Old);
        Write(true, "missing/file.txt", "added");
        Write(false, "right-only.txt", "kept");

        var plan = Plan(SyncMode.Update);
        Assert.Equal(SyncAction.Overwrite, Op(plan, "left-newer.txt").Action);
        Assert.Equal(SyncAction.Skip, Op(plan, "right-newer.txt").Action);
        Assert.Equal(SyncAction.Skip, Op(plan, "same-time.txt").Action);
        Assert.Equal(SyncAction.Skip, Op(plan, "right-only.txt").Action);
        Assert.Equal(0, plan.Count(SyncAction.Delete));
        Assert.False(plan.HasConflicts);

        SyncExecutor.Execute(plan);
        Assert.Equal("new", Read(false, "left-newer.txt"));
        Assert.Equal("new", Read(false, "right-newer.txt"));
        Assert.Equal("old", Read(true, "right-newer.txt"));
        Assert.Equal("added", Read(false, "missing/file.txt"));
        Assert.True(Exists(false, "right-only.txt"));
        Assert.False(Exists(true, "right-only.txt"));
    }

    [Fact]
    public void Update_both_copies_each_way_and_newer_wins()
    {
        Write(true, "left-newer.txt", "left", New);
        Write(false, "left-newer.txt", "right", Old);
        Write(true, "right-newer.txt", "left", Old);
        Write(false, "right-newer.txt", "right", New);
        Write(true, "left-only.txt", "l");
        Write(false, "sub/right-only.txt", "r");

        var plan = Plan(SyncMode.UpdateBoth);
        Assert.Equal(SyncDirection.LeftToRight, Op(plan, "left-newer.txt").Direction);
        Assert.Equal(SyncDirection.RightToLeft, Op(plan, "right-newer.txt").Direction);
        Assert.Equal(SyncAction.CreateFolder, Op(plan, "sub").Action);
        Assert.Equal(SyncDirection.RightToLeft, Op(plan, "sub").Direction);

        SyncExecutor.Execute(plan);
        AssertSidesIdentical();
        Assert.Equal("left", Read(false, "left-newer.txt"));
        Assert.Equal("right", Read(true, "right-newer.txt"));
    }

    [Fact]
    public void Update_both_reports_conflicts_and_leaves_them_alone()
    {
        var lastSync = new DateTime(2024, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        Write(true, "both-changed.txt", "left", New);
        Write(false, "both-changed.txt", "right", New.AddHours(1));
        Write(true, "one-changed.txt", "left", New);
        Write(false, "one-changed.txt", "right", Old);
        Write(true, "same-time.txt", "left", Old);
        Write(false, "same-time.txt", "right", Old);
        Write(true, "kind", "file");
        Directory.CreateDirectory(Path.Combine(Right, "kind"));

        var plan = Plan(SyncMode.UpdateBoth, lastSync: lastSync);
        Assert.True(plan.HasConflicts);
        Assert.Equal(SyncAction.Conflict, Op(plan, "both-changed.txt").Action);
        Assert.Equal(SyncAction.Conflict, Op(plan, "same-time.txt").Action);
        Assert.Equal(SyncAction.Conflict, Op(plan, "kind").Action);
        Assert.Equal(SyncAction.Overwrite, Op(plan, "one-changed.txt").Action);
        Assert.Equal(SyncAction.Overwrite, Op(Plan(SyncMode.UpdateBoth), "both-changed.txt").Action);
        Assert.Equal(SyncAction.Skip, Op(Plan(SyncMode.MirrorLeftToRight), "kind").Action);

        var results = SyncExecutor.Execute(plan);
        Assert.Equal(3, results.Count(r => r.Status == SyncOperationStatus.Skipped));
        Assert.Equal("right", Read(false, "both-changed.txt"));
        Assert.Equal("left", Read(true, "both-changed.txt"));
        Assert.Equal("left", Read(false, "one-changed.txt"));
    }

    [Fact]
    public void Filters_keep_excluded_entries_out_of_the_sync()
    {
        Write(true, "a.txt", "a");
        Write(true, "a.log", "log");
        Write(false, "old/keep.tmp", "excluded");
        Write(false, "old/drop.txt", "removed");
        Write(false, "bin/output.dll", "noise");

        var plan = Plan(SyncMode.MirrorLeftToRight,
            new FolderCompareOptions { ExcludeFiles = ["*.tmp"] },
            FolderFilter.Parse("f: \\.log$"));
        Assert.Equal(SyncAction.Skip, Op(plan, "a.log").Action);
        Assert.Equal(SyncAction.Skip, Op(plan, "old/keep.tmp").Action);
        Assert.Equal(SyncAction.Delete, Op(plan, "old/drop.txt").Action);
        Assert.Equal(SyncAction.Skip, Op(plan, "old").Action);
        Assert.Equal(SyncAction.Skip, Op(plan, "bin").Action);

        SyncExecutor.Execute(plan);
        Assert.True(Exists(false, "a.txt"));
        Assert.False(Exists(false, "a.log"));
        Assert.True(Exists(false, "old/keep.tmp"));
        Assert.False(Exists(false, "old/drop.txt"));
        Assert.True(Exists(false, "bin/output.dll"));
    }

    [Theory]
    [InlineData(SyncMode.MirrorLeftToRight)]
    [InlineData(SyncMode.MirrorRightToLeft)]
    [InlineData(SyncMode.Update)]
    [InlineData(SyncMode.UpdateBoth)]
    public void Dry_run_writes_nothing(SyncMode mode)
    {
        Write(true, "changed.txt", "left", New);
        Write(false, "changed.txt", "right", Old);
        Write(true, "left/only.txt", "l");
        Write(false, "right/only.txt", "r");
        var before = Snapshot();
        var deleted = new List<string>();

        var plan = Plan(mode);
        var results = SyncExecutor.Execute(plan, options: new SyncExecuteOptions { DryRun = true, Delete = (path, _) => deleted.Add(path) });

        Assert.Equal(before, Snapshot());
        Assert.Empty(deleted);
        Assert.Contains(results, r => r.Status == SyncOperationStatus.WouldRun);
        Assert.DoesNotContain(results, r => r.Status is SyncOperationStatus.Done or SyncOperationStatus.Failed);
    }

    [Fact]
    public void Failures_midway_are_reported_and_the_rest_still_runs()
    {
        Write(true, "a-appeared.txt", "left");
        Write(true, "b-changed.txt", "left", New);
        Write(false, "b-changed.txt", "right", Old);
        Write(true, "c-source-gone.txt", "left");
        Write(true, "d-fine.txt", "fine");
        var plan = Plan(SyncMode.MirrorLeftToRight);

        Write(false, "a-appeared.txt", "someone else");
        Write(false, "b-changed.txt", "edited meanwhile", New);
        File.Delete(Path.Combine(Left, "c-source-gone.txt"));
        var results = SyncExecutor.Execute(plan);

        Assert.Equal(SyncOperationStatus.Failed, results.Single(r => r.Operation.RelativePath == "a-appeared.txt").Status);
        Assert.Equal(SyncOperationStatus.Failed, results.Single(r => r.Operation.RelativePath == "b-changed.txt").Status);
        Assert.Equal(SyncOperationStatus.Failed, results.Single(r => r.Operation.RelativePath == "c-source-gone.txt").Status);
        Assert.Equal(SyncOperationStatus.Done, results.Single(r => r.Operation.RelativePath == "d-fine.txt").Status);
        Assert.All(results.Where(r => r.Status == SyncOperationStatus.Failed), r => Assert.False(string.IsNullOrEmpty(r.Error)));
        Assert.Equal("someone else", Read(false, "a-appeared.txt"));
        Assert.Equal("edited meanwhile", Read(false, "b-changed.txt"));
        Assert.Equal("fine", Read(false, "d-fine.txt"));
        Assert.Empty(Directory.EnumerateFiles(_root, "*.tmp", SearchOption.AllDirectories));
    }

    [Fact]
    public void Include_selects_operations_and_the_delete_hook_replaces_plain_deletes()
    {
        Write(true, "copy.txt", "copy");
        Write(true, "skip.txt", "skip");
        Write(false, "gone.txt", "gone");
        var deleted = new List<(string Path, bool IsFolder)>();
        var reported = new List<SyncOperationResult>();

        var results = SyncExecutor.Execute(
            Plan(SyncMode.MirrorLeftToRight),
            op => op.RelativePath != "skip.txt",
            new SyncExecuteOptions { Delete = (path, isFolder) => deleted.Add((path, isFolder)), Progress = new SyncProgress(reported) });

        Assert.Equal(SyncOperationStatus.NotIncluded, results.Single(r => r.Operation.RelativePath == "skip.txt").Status);
        Assert.False(Exists(false, "skip.txt"));
        Assert.True(Exists(false, "copy.txt"));
        Assert.Equal((Path.Combine(Right, "gone.txt"), false), Assert.Single(deleted));
        Assert.True(Exists(false, "gone.txt"));
        Assert.Equal(results, reported);
    }

    [Fact]
    public void Overwrite_replaces_read_only_targets_and_keeps_the_source_time()
    {
        Write(true, "file.txt", "new contents", New);
        var target = Write(false, "file.txt", "old", Old);
        File.SetAttributes(target, FileAttributes.ReadOnly);

        var result = Assert.Single(SyncExecutor.Execute(Plan(SyncMode.MirrorLeftToRight)));
        Assert.Equal(SyncOperationStatus.Done, result.Status);
        Assert.Equal("new contents", Read(false, "file.txt"));
        Assert.Equal(New, File.GetLastWriteTimeUtc(target));
    }

    [Fact]
    public void Cancellation_stops_the_run()
    {
        Write(true, "a.txt", "a");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var plan = Plan(SyncMode.MirrorLeftToRight);
        Assert.Throws<OperationCanceledException>(() => SyncExecutor.Execute(plan, cancellationToken: cancellation.Token));
        Assert.Throws<OperationCanceledException>(() =>
            SyncPlanner.Plan(FolderComparer.Compare(Left, Right), null, cancellation.Token));
        Assert.False(Exists(false, "a.txt"));
    }

    [Fact]
    public void Cli_prints_the_plan_without_yes_and_syncs_with_it()
    {
        Write(true, "a.txt", "a");
        Write(false, "b.txt", "b");

        var (code, output) = RunCli("--profile", "mirror-right");
        Assert.Equal(1, code);
        Assert.Contains("Copy", output);
        Assert.Contains("--yes", output);
        Assert.False(Exists(false, "a.txt"));

        (code, _) = RunCli("--profile", "mirror-right", "--dry-run", "--yes");
        Assert.Equal(1, code);
        Assert.False(Exists(false, "a.txt"));

        (code, output) = RunCli("--profile", "mirror-right", "--yes");
        Assert.Equal(0, code);
        Assert.Contains("Done", output);
        AssertSidesIdentical();
        Assert.Equal(0, RunCli("--profile", "mirror-right").code);
        Assert.Equal(2, RunCli("--profile", "sideways").code);
    }

    private (int code, string output) RunCli(params string[] args)
    {
        var cli = typeof(DiffRapid.Cli.Program).Assembly.Location;
        var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        start.ArgumentList.Add(cli);
        start.ArgumentList.Add("sync");
        start.ArgumentList.Add(Left);
        start.ArgumentList.Add(Right);
        foreach (var arg in args)
        {
            start.ArgumentList.Add(arg);
        }

        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        Assert.True(process.WaitForExit(30000));
        Task.WaitAll(output, error);
        return (process.ExitCode, output.Result + error.Result);
    }

    // Progress<T> posts to the thread pool; this reports synchronously so the test can compare lists.
    private sealed class SyncProgress(List<SyncOperationResult> results) : IProgress<SyncOperationResult>
    {
        public void Report(SyncOperationResult value) => results.Add(value);
    }
}
