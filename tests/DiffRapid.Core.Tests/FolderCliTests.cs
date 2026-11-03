using System.Diagnostics;
using System.Text.Json;

namespace DiffRapid.Core.Tests;

public sealed class FolderCliTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "diffrapid-cli-" + Guid.NewGuid());
    private string Left => Path.Combine(_root, "left");
    private string Right => Path.Combine(_root, "right");
    public FolderCliTests() { Directory.CreateDirectory(Left); Directory.CreateDirectory(Right); }
    public void Dispose() => Directory.Delete(_root, true);

    private (int code, string output) Run(params string[] args)
    {
        var cli = typeof(DiffRapid.Cli.Program).Assembly.Location;
        var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        start.ArgumentList.Add(cli); start.ArgumentList.Add("dir"); start.ArgumentList.Add(Left); start.ArgumentList.Add(Right);
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        Assert.True(process.WaitForExit(30000));
        Task.WaitAll(output, error);
        return (process.ExitCode, output.Result + error.Result);
    }

    [Theory]
    [InlineData("full")]
    [InlineData("quick")]
    [InlineData("binary")]
    [InlineData("date")]
    [InlineData("date-size")]
    [InlineData("size")]
    [InlineData("existence")]
    public void Methods_and_json_are_available(string method)
    {
        File.WriteAllText(Path.Combine(Left, "a"), "same"); File.WriteAllText(Path.Combine(Right, "a"), "same");
        var (code, output) = Run("--method", method, "--json");
        Assert.Equal(0, code);
        using var json = JsonDocument.Parse(output);
        Assert.Equal("Identical", json.RootElement.GetProperty("Root").GetProperty("Status").GetString());
        Assert.Single(json.RootElement.GetProperty("Root").GetProperty("Children").EnumerateArray());
    }

    [Fact]
    public void Output_filters_and_repeated_masks_work()
    {
        File.WriteAllText(Path.Combine(Left, "a.txt"), "left"); File.WriteAllText(Path.Combine(Right, "b.cs"), "right");
        Assert.Contains("<  a.txt", Run().output); Assert.Contains(">  b.cs", Run().output);
        var left = Run("--only", "left"); Assert.Equal(1, left.code); Assert.DoesNotContain(">  b.cs", left.output);
        Assert.DoesNotContain("<  a.txt", Run("--only", "right").output);
        Assert.DoesNotContain("<  a.txt", Run("--only", "different").output);
        Assert.Equal(1, Run("--include", "*.txt", "--include", "*.cs").code);
        Assert.Equal(0, Run("--exclude", "*.txt", "--exclude", "*.cs").code);
        Assert.Equal(0, Run("--include", "*.md").code);
        Assert.Contains("LeftOnly: 1", left.output);
    }

    [Fact]
    public void Text_options_and_no_recurse_are_forwarded()
    {
        Directory.CreateDirectory(Path.Combine(Left, "nested"));
        File.WriteAllText(Path.Combine(Left, "nested", "a"), "a");
        Directory.CreateDirectory(Path.Combine(Right, "nested"));
        File.WriteAllText(Path.Combine(Right, "nested", "a"), " a ");

        // Without recursion the subfolder only counts as present on both sides.
        Assert.Equal(0, Run("--no-recurse").code);
        Assert.Equal(0, Run("--ignore-whitespace", "all").code);
        Assert.Contains("M  nested/a", Run().output);
    }

    [Fact]
    public void Reports_keep_difference_exit_code_and_json_output()
    {
        File.WriteAllText(Path.Combine(Left, "different"), "left");
        File.WriteAllText(Path.Combine(Left, "same"), "same");
        File.WriteAllText(Path.Combine(Right, "same"), "same");
        foreach (var extension in new[] { "html", "htm", "csv" })
        {
            var path = Path.Combine(_root, "report." + extension);
            var (code, output) = Run("--report", path, "--json", "--include-identical");
            Assert.Equal(1, code);
            using var json = JsonDocument.Parse(output);
            Assert.Contains("same", File.ReadAllText(path));
            var bytes = File.ReadAllBytes(path);
            Assert.Equal(extension == "csv", bytes.Take(3).SequenceEqual(new byte[] { 239, 187, 191 }));
        }
        var custom = Path.Combine(_root, "report.txt");
        Assert.Equal(1, Run("--report", custom, "--report-format", "csv").code);
        Assert.DoesNotContain("\"same\"", File.ReadAllText(custom));
        Assert.Equal(2, Run("--report", _root, "--report-format", "html").code);
        Assert.Equal(2, Run("--report", custom).code);
        Assert.Equal(2, Run("--report", custom, "--report-format", "invalid").code);
        Assert.Equal(2, Run("--report-format", "csv").code);
    }

    [Fact]
    public void Invalid_options_and_missing_roots_return_error()
    {
        Assert.Equal(2, Run("--method", "invalid").code);
        Assert.Equal(2, Run("--only", "invalid").code);
        Directory.Delete(Right);
        Assert.Equal(2, Run().code);
    }
}
