using System.Diagnostics;
using System.IO.Compression;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using DiffRapid.Mcp;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace DiffRapid.Core.Tests;

public sealed class McpServerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "diffrapid-mcp-tests-" + Guid.NewGuid().ToString("N"));

    public McpServerTests()
    {
        Directory.CreateDirectory(_root);
    }

    [Fact]
    public async Task ProtocolInitializesListsToolsCallsToolsAndPingsOverStreams()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var toServer = new AnonymousPipeServerStream(PipeDirection.Out);
        using var serverInput = new AnonymousPipeClientStream(PipeDirection.In, toServer.ClientSafePipeHandle);
        using var fromServer = new AnonymousPipeServerStream(PipeDirection.Out);
        using var clientInput = new AnonymousPipeClientStream(PipeDirection.In, fromServer.ClientSafePipeHandle);
        using var writer = new StreamWriter(toServer, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
        using var reader = new StreamReader(clientInput);
        await using var server = McpServer.Create(new StreamServerTransport(serverInput, fromServer), ComparisonServer.CreateOptions());
        var running = server.RunAsync(cancellation.Token);
        try
        {
            var initialized = await Request(1, "initialize", new { protocolVersion = "2025-11-25", capabilities = new { }, clientInfo = new { name = "tests", version = "1" } });
            Assert.Equal("diffrapid-mcp", initialized.GetProperty("serverInfo").GetProperty("name").GetString());
            Assert.True(initialized.GetProperty("capabilities").TryGetProperty("tools", out _));
            await writer.WriteLineAsync("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}".AsMemory(), cancellation.Token);

            var listed = await Request(2, "tools/list", new { });
            var tools = listed.GetProperty("tools").EnumerateArray().ToArray();
            Assert.Equal(8, tools.Length);
            Assert.Equal(8, tools.Select(tool => tool.GetProperty("name").GetString()).Distinct().Count());
            foreach (var tool in tools)
            {
                Assert.False(string.IsNullOrWhiteSpace(tool.GetProperty("description").GetString()));
                var schema = tool.GetProperty("inputSchema");
                Assert.Equal("object", schema.GetProperty("type").GetString());
                Assert.True(schema.GetProperty("required").GetArrayLength() >= 2);
                Assert.True(tool.GetProperty("annotations").GetProperty("readOnlyHint").GetBoolean());
            }

            var compared = await Request(3, "tools/call", new { name = "compare_text", arguments = new { left_text = "old\n", right_text = "new\n" } });
            Assert.False(compared.GetProperty("isError").GetBoolean());
            Assert.Equal(1, compared.GetProperty("structuredContent").GetProperty("differenceCount").GetInt32());
            var error = await Request(4, "tools/call", new { name = "compare_files", arguments = new { left = "missing", right = "missing" } });
            Assert.True(error.GetProperty("isError").GetBoolean());
            var ping = await Request(5, "ping", new { });
            Assert.Equal(JsonValueKind.Object, ping.ValueKind);
        }
        finally
        {
            cancellation.Cancel();
            try
            {
                await running;
            }
            catch (OperationCanceledException)
            {
            }
        }

        async Task<JsonElement> Request(int id, string method, object parameters)
        {
            await writer.WriteLineAsync(JsonSerializer.Serialize(new { jsonrpc = "2.0", id, method, @params = parameters }).AsMemory(), cancellation.Token);
            var line = await reader.ReadLineAsync(cancellation.Token);
            Assert.NotNull(line);
            using var response = JsonDocument.Parse(line);
            Assert.Equal("2.0", response.RootElement.GetProperty("jsonrpc").GetString());
            Assert.Equal(id, response.RootElement.GetProperty("id").GetInt32());
            return response.RootElement.GetProperty("result").Clone();
        }
    }

    [Fact]
    public void CompareFilesReturnsReportCountsRangesAndUnifiedText()
    {
        var left = File("left.txt", "same\nold\nend\n");
        var right = File("right.txt", "same\nnew\nextra\nend\n");
        var result = Call("compare_files", new { left, right, context = 0 });
        Assert.Equal(1, result.GetProperty("version").GetInt32());
        Assert.Equal("file", result.GetProperty("kind").GetString());
        Assert.False(result.GetProperty("identical").GetBoolean());
        Assert.Equal(1, result.GetProperty("differenceCount").GetInt32());
        Assert.Equal(1, result.GetProperty("lines").GetProperty("changed").GetInt32());
        Assert.Equal(1, result.GetProperty("lines").GetProperty("added").GetInt32());
        var hunk = Assert.Single(result.GetProperty("hunks").EnumerateArray());
        Assert.Equal(2, hunk.GetProperty("leftLine").GetInt32());
        Assert.Equal(1, hunk.GetProperty("leftCount").GetInt32());
        Assert.Equal(2, hunk.GetProperty("rightCount").GetInt32());
        Assert.Contains("-old\n+new\n+extra\n", result.GetProperty("unifiedText").GetString());
        Assert.DoesNotContain(" same\n", result.GetProperty("unifiedText").GetString());
        Assert.False(result.GetProperty("truncated").GetBoolean());
    }

    [Theory]
    [InlineData("default")]
    [InlineData("minimal")]
    [InlineData("patience")]
    [InlineData("histogram")]
    [InlineData("none")]
    public void CompareTextSupportsAlgorithmsAndIgnoreOptions(string algorithm)
    {
        var result = Call("compare_text", new
        {
            left_text = "HELLO world\n\nend\n",
            right_text = "hello   world\nend\n",
            ignore_whitespace = true,
            ignore_case = true,
            ignore_blank_lines = true,
            algorithm,
        });
        Assert.True(result.GetProperty("identical").GetBoolean());
        Assert.Equal(0, result.GetProperty("differenceCount").GetInt32());
    }

    [Fact]
    public void CompareTextAllowsEmptyContentAndContext()
    {
        var empty = Call("compare_text", new { left_text = "", right_text = "" });
        Assert.True(empty.GetProperty("identical").GetBoolean());
        var insertion = Call("compare_text", new { left_text = "", right_text = "added\n" });
        Assert.Equal(0, insertion.GetProperty("hunks")[0].GetProperty("leftCount").GetInt32());
        Assert.Equal(1, insertion.GetProperty("hunks")[0].GetProperty("leftLine").GetInt32());
        var context = Call("compare_text", new { left_text = "same\nold\n", right_text = "same\nnew\n", context = 1 });
        Assert.Contains(" same\n", context.GetProperty("unifiedText").GetString());
    }

    [Fact]
    public void CompareTextTruncatesHunksAndPreservesFullCounts()
    {
        var result = Call("compare_text", new { left_text = "a\nsame\nb\n", right_text = "x\nsame\ny\n", max_hunks = 1 });
        Assert.Equal(2, result.GetProperty("differenceCount").GetInt32());
        Assert.Single(result.GetProperty("hunks").EnumerateArray());
        Assert.True(result.GetProperty("truncated").GetBoolean());
        Assert.DoesNotContain("+y\n", result.GetProperty("unifiedText").GetString());
        var zero = Call("compare_text", new { left_text = "a", right_text = "b", max_hunks = 0 });
        Assert.Empty(zero.GetProperty("hunks").EnumerateArray());
        Assert.True(zero.GetProperty("truncated").GetBoolean());
    }

    [Fact]
    public void CompareTextBoundsSingleHugeHunkAndUnifiedText()
    {
        var result = Call("compare_text", new { left_text = new string('a', 100_000), right_text = new string('b', 100_000) });
        Assert.True(result.GetProperty("truncated").GetBoolean());
        Assert.True(result.GetRawText().Length < 150_000);
        Assert.True(result.GetProperty("unifiedText").GetString()!.Length <= 65_536);
    }

    [Theory]
    [InlineData("full")]
    [InlineData("quick")]
    [InlineData("binary")]
    [InlineData("date")]
    [InlineData("date-size")]
    [InlineData("size")]
    [InlineData("existence")]
    [InlineData("hash")]
    public void CompareFoldersSupportsMethodsAndDefaultIdenticalFiltering(string method)
    {
        var left = DirectoryPath("left");
        var right = DirectoryPath("right");
        File("left/same.txt", "same");
        File("right/same.txt", "same");
        File("left/only.txt", "left only");
        var result = Call("compare_folders", new { left, right, method });
        Assert.Equal("folder", result.GetProperty("kind").GetString());
        Assert.Equal(1, result.GetProperty("counts").GetProperty("identical").GetInt32());
        Assert.Equal(1, result.GetProperty("counts").GetProperty("leftOnly").GetInt32());
        Assert.Single(result.GetProperty("items").EnumerateArray());
        Assert.False(result.GetProperty("truncated").GetBoolean());
        var all = Call("compare_folders", new { left, right, method, include_identical = true });
        Assert.Equal(2, all.GetProperty("items").GetArrayLength());
    }

    [Fact]
    public void CompareFoldersSupportsFilterRecursionAndTruncation()
    {
        var left = DirectoryPath("left");
        var right = DirectoryPath("right");
        File("left/a.cs", "a");
        File("left/b.txt", "b");
        DirectoryPath("left/nested");
        DirectoryPath("right/nested");
        File("left/nested/c.cs", "c");
        var filtered = Call("compare_folders", new { left, right, filter = "*.cs", recursive = false });
        var paths = filtered.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("path").GetString()).ToArray();
        Assert.Contains("a.cs", paths);
        Assert.DoesNotContain("b.txt", paths);
        Assert.DoesNotContain("nested/c.cs", paths);
        var limited = Call("compare_folders", new { left, right, max_entries = 1 });
        Assert.Single(limited.GetProperty("items").EnumerateArray());
        Assert.True(limited.GetProperty("truncated").GetBoolean());
    }

    [Fact]
    public void MergeReturnsCleanTextAndConflictRangesWithoutWrites()
    {
        var ancestor = File("base.txt", "a\nb\nc\n");
        var left = File("left.txt", "A\nb\nc\n");
        var right = File("right.txt", "a\nb\nC\n");
        var clean = Call("merge_three_way", new { @base = ancestor, left, right });
        Assert.Equal("A\nb\nC\n", clean.GetProperty("mergedText").GetString());
        Assert.Equal(0, clean.GetProperty("conflictCount").GetInt32());
        System.IO.File.WriteAllText(right, "B\nb\nc\n");
        var conflict = Call("merge_three_way", new { @base = ancestor, left, right });
        Assert.Equal(1, conflict.GetProperty("conflictCount").GetInt32());
        Assert.Equal(1, conflict.GetProperty("conflicts")[0].GetProperty("baseLine").GetInt32());
        Assert.Contains("<<<<<<< left", conflict.GetProperty("mergedText").GetString());
        Assert.Equal("a\nb\nc\n", System.IO.File.ReadAllText(ancestor));
        Assert.Equal(3, Directory.GetFiles(_root).Length);
    }

    [Theory]
    [InlineData("json", "{\"x\":1}", "{\"x\":2}")]
    [InlineData("xml", "<root><x>1</x></root>", "<root><x>2</x></root>")]
    [InlineData("yaml", "x: 1\n", "x: 2\n")]
    [InlineData("csproj", "<Project><PropertyGroup><X>1</X></PropertyGroup></Project>", "<Project><PropertyGroup><X>2</X></PropertyGroup></Project>")]
    [InlineData("resx", "<root><data name=\"x\"><value>1</value></data></root>", "<root><data name=\"x\"><value>2</value></data></root>")]
    public void CompareStructureReturnsKeyPathsValuesAndSpans(string extension, string leftText, string rightText)
    {
        var left = File("left." + extension, leftText);
        var right = File("right." + extension, rightText);
        var result = Call("compare_structure", new { left, right });
        Assert.False(result.GetProperty("identical").GetBoolean());
        var change = Assert.Single(result.GetProperty("changes").EnumerateArray());
        Assert.False(string.IsNullOrWhiteSpace(change.GetProperty("path").GetString()));
        Assert.Equal("1", change.GetProperty("left").GetString());
        Assert.Equal("2", change.GetProperty("right").GetString());
        Assert.NotEqual(JsonValueKind.Null, change.GetProperty("leftSpan").ValueKind);
    }

    [Fact]
    public void CompareStructureIgnoresKeyOrder()
    {
        var left = File("left.json", "{\"a\":1,\"b\":2}");
        var right = File("right.json", "{\"b\":2,\"a\":1}");
        Assert.True(Call("compare_structure", new { left, right }).GetProperty("identical").GetBoolean());
    }

    [Fact]
    public void CompareApiHandlesAssembliesExecutablesAndPackages()
    {
        var assembly = typeof(ComparisonServer).Assembly.Location;
        var identical = Call("compare_api", new { left = assembly, right = assembly });
        Assert.True(identical.GetProperty("identical").GetBoolean());
        Assert.False(identical.GetProperty("hasBreakingChanges").GetBoolean());
        var changed = Call("compare_api", new { left = assembly, right = typeof(DiffRapid.Core.Comparison.TextComparer).Assembly.Location });
        Assert.False(changed.GetProperty("identical").GetBoolean());
        Assert.True(changed.GetProperty("hasBreakingChanges").GetBoolean());
        var executable = Path.Combine(_root, "assembly.exe");
        System.IO.File.Copy(assembly, executable);
        Assert.True(Call("compare_api", new { left = assembly, right = executable }).GetProperty("identical").GetBoolean());
        var package = Package("one.nupkg", assembly);
        var packageResult = Call("compare_api", new { left = package, right = package });
        Assert.True(packageResult.GetProperty("identical").GetBoolean());
        Assert.Single(packageResult.GetProperty("libraries").EnumerateArray());
    }

    [Fact]
    public void SemanticDiffReturnsMemberChangesAndParseErrors()
    {
        var left = File("left.cs", "class C { int Value() => 1; }");
        var right = File("right.cs", "class C { int Value() => 2; }");
        var result = Call("semantic_diff", new { left, right });
        Assert.False(result.GetProperty("identical").GetBoolean());
        Assert.Contains(result.GetProperty("members").EnumerateArray(), member => member.GetProperty("signature").GetString()!.Contains("Value", StringComparison.Ordinal));
        Assert.True(result.GetProperty("members")[0].GetProperty("left").GetProperty("line").GetInt32() >= 1);
        System.IO.File.WriteAllText(right, "class C { int Value(");
        Assert.True(Call("semantic_diff", new { left, right }).GetProperty("rightHasErrors").GetBoolean());
    }

    [Fact]
    public void CompareApiPropagatesLibraryTruncationToPackage()
    {
        var oldAssembly = Path.Combine(_root, "old.dll");
        var newAssembly = Path.Combine(_root, "new.dll");
        CompileAssembly(oldAssembly, "public class C { }");
        var fields = string.Join("\n", Enumerable.Range(0, 1001).Select(index => $"public int F{index};"));
        CompileAssembly(newAssembly, "public class C {\n" + fields + "\n}");
        var left = Package("old.nupkg", oldAssembly);
        var right = Package("new.nupkg", newAssembly);
        var result = Call("compare_api", new { left, right });
        Assert.True(result.GetProperty("truncated").GetBoolean());
        var library = result.GetProperty("libraries")[0].GetProperty("comparison");
        Assert.True(library.GetProperty("truncated").GetBoolean());
        Assert.Equal(1001, library.GetProperty("differenceCount").GetInt32());
        Assert.Equal(1000, library.GetProperty("changes").GetArrayLength());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CreatePatchReturnsFileOrFolderPatchWithoutWrites(bool folders)
    {
        var leftFile = File("left/a.txt", "before\n");
        var rightFile = File("right/a.txt", "after\n");
        var left = folders ? Path.GetDirectoryName(leftFile)! : leftFile;
        var right = folders ? Path.GetDirectoryName(rightFile)! : rightFile;
        var result = Call("create_patch", new { left, right });
        Assert.Contains("--- a/a.txt\n+++ b/a.txt\n", result.GetProperty("patch").GetString());
        Assert.Contains("-before\n+after\n", result.GetProperty("patch").GetString());
        Assert.False(result.GetProperty("truncated").GetBoolean());
        Assert.Equal("before\n", System.IO.File.ReadAllText(leftFile));
        Assert.Equal("after\n", System.IO.File.ReadAllText(rightFile));
        Assert.Equal(2, Directory.GetFiles(_root, "*", SearchOption.AllDirectories).Length);
    }

    [Fact]
    public void CreatePatchTruncatesLongOutput()
    {
        var left = File("left.txt", new string('a', 100_000));
        var right = File("right.txt", new string('b', 100_000));
        var result = Call("create_patch", new { left, right });
        Assert.True(result.GetProperty("truncated").GetBoolean());
        Assert.Equal(65_536, result.GetProperty("patch").GetString()!.Length);
    }

    [Fact]
    public void FolderPatchIncludesAddedRemovedEmptyAndBinaryFiles()
    {
        var left = DirectoryPath("left");
        var right = DirectoryPath("right");
        File("left/removed.txt", "removed\n");
        File("right/added.txt", "added\n");
        File("right/empty.txt", "");
        File("left/binary.dat", "\0old");
        File("right/binary.dat", "\0new");
        var patch = Call("create_patch", new { left, right }).GetProperty("patch").GetString();
        Assert.Contains("--- /dev/null\n+++ b/added.txt\n", patch);
        Assert.Contains("--- a/removed.txt\n+++ /dev/null\n", patch);
        Assert.Contains("--- /dev/null\n+++ b/empty.txt\n", patch);
        Assert.Contains("Binary files a/binary.dat and b/binary.dat differ\n", patch);
    }

    [Fact]
    public void MergeAndStructureBoundLargeValues()
    {
        var left = File("left.txt", new string('a', 100_000));
        var right = File("right.txt", "base");
        var merge = Call("merge_three_way", new { @base = right, left, right });
        Assert.True(merge.GetProperty("truncated").GetBoolean());
        Assert.Equal(65_536, merge.GetProperty("mergedText").GetString()!.Length);
        left = File("left.json", JsonSerializer.Serialize(new { value = new string('a', 100_000) }));
        right = File("right.json", "{\"value\":\"b\"}");
        var structure = Call("compare_structure", new { left, right });
        Assert.True(structure.GetProperty("truncated").GetBoolean());
        Assert.Equal(1, structure.GetProperty("differenceCount").GetInt32());
    }

    [Fact]
    public void SemanticDiffLimitsMemberEntries()
    {
        var left = File("left.cs", "class C { }");
        var fields = string.Join("\n", Enumerable.Range(0, 1001).Select(index => $"int Field{index};"));
        var right = File("right.cs", "class C {\n" + fields + "\n}");
        var result = Call("semantic_diff", new { left, right });
        Assert.True(result.GetProperty("truncated").GetBoolean());
        Assert.Equal(1000, result.GetProperty("members").GetArrayLength());
        Assert.True(result.GetProperty("differenceCount").GetInt32() > 1000);
    }

    [Fact]
    public void CompareFilesExtractsOfficeDocuments()
    {
        var left = Path.Combine(_root, "left.docx");
        using (var document = WordprocessingDocument.Create(left, WordprocessingDocumentType.Document))
        {
            var main = document.AddMainDocumentPart();
            var paragraph = new Paragraph(new Run(new DocumentFormat.OpenXml.Wordprocessing.Text("Hello")));
            main.Document = new Document(new Body(paragraph));
        }

        var right = File("right.txt", "Hello\n");
        Assert.True(Call("compare_files", new { left, right }).GetProperty("identical").GetBoolean());
    }

    [Theory]
    [InlineData("compare_files")]
    [InlineData("compare_folders")]
    [InlineData("merge_three_way")]
    [InlineData("compare_structure")]
    [InlineData("compare_api")]
    [InlineData("semantic_diff")]
    [InlineData("create_patch")]
    public void MissingPathsProduceReadableToolErrors(string name)
    {
        var arguments = new Dictionary<string, object> { ["left"] = Path.Combine(_root, "missing"), ["right"] = Path.Combine(_root, "missing") };
        if (name == "merge_three_way")
        {
            arguments["base"] = Path.Combine(_root, "missing");
        }

        var result = ComparisonServer.CallTool(name, JsonSerializer.SerializeToElement(arguments));
        Assert.True(result.IsError);
        Assert.Contains("does not exist", Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text);
    }

    [Theory]
    [InlineData("compare_text", "{}")]
    [InlineData("compare_text", "{\"left_text\":null,\"right_text\":\"\"}")]
    [InlineData("compare_text", "{\"left_text\":\"a\",\"right_text\":\"b\",\"context\":-1}")]
    [InlineData("compare_text", "{\"left_text\":\"a\",\"right_text\":\"b\",\"max_hunks\":1001}")]
    [InlineData("compare_text", "{\"left_text\":\"a\",\"right_text\":\"b\",\"algorithm\":\"bad\"}")]
    [InlineData("compare_text", "{\"left_text\":\"a\",\"right_text\":\"b\",\"ignore_case\":\"true\"}")]
    [InlineData("compare_text", "{\"left_text\":\"a\",\"right_text\":\"b\",\"context\":1.5}")]
    [InlineData("compare_text", "{\"left_text\":\"a\",\"right_text\":\"b\",\"extra\":1}")]
    [InlineData("compare_text", "[]")]
    [InlineData("unknown", "{}")]
    public void BadArgumentsProduceToolErrors(string name, string json)
    {
        using var document = JsonDocument.Parse(json);
        var result = ComparisonServer.CallTool(name, document.RootElement);
        Assert.True(result.IsError);
        Assert.False(string.IsNullOrWhiteSpace(Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text));
    }

    [Fact]
    public void InvalidFileContentProducesToolErrors()
    {
        var invalid = File("invalid.json", "{");
        var binary = File("invalid.dll", "not an assembly");
        Assert.True(ComparisonServer.CallTool("compare_structure", JsonSerializer.SerializeToElement(new { left = invalid, right = invalid })).IsError);
        Assert.True(ComparisonServer.CallTool("compare_api", JsonSerializer.SerializeToElement(new { left = binary, right = binary })).IsError);
        var nul = File("binary.txt", "\0\0\0\0");
        Assert.True(ComparisonServer.CallTool("compare_files", JsonSerializer.SerializeToElement(new { left = nul, right = nul })).IsError);
    }

    [Fact]
    public void CancellationIsPropagated()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => ComparisonServer.CallTool("compare_text", JsonSerializer.SerializeToElement(new { left_text = "a", right_text = "b" }), cancellation.Token));
    }

    [Fact]
    public async Task CliPrintsRegistrationWithoutStartingServer()
    {
        var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add(typeof(DiffRapid.Cli.Program).Assembly.Location);
        start.ArgumentList.Add("mcp");
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await process.WaitForExitAsync(cancellation.Token);
        Assert.Equal(0, process.ExitCode);
        Assert.Equal("", await error);
        using var registration = JsonDocument.Parse(await output);
        var server = registration.RootElement.GetProperty("mcpServers").GetProperty("diffrapid");
        var executable = OperatingSystem.IsWindows() ? "diffrapid-mcp.exe" : "diffrapid-mcp";
        var sibling = Path.Combine(AppContext.BaseDirectory, executable);
        Assert.Equal(System.IO.File.Exists(sibling) ? sibling : executable, server.GetProperty("command").GetString());
        Assert.Empty(server.GetProperty("args").EnumerateArray());
    }

    private static JsonElement Call(string name, object arguments)
    {
        var result = ComparisonServer.CallTool(name, JsonSerializer.SerializeToElement(arguments));
        Assert.False(result.IsError, result.Content.OfType<TextContentBlock>().FirstOrDefault()?.Text);
        using var document = JsonDocument.Parse(Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text);
        return document.RootElement.Clone();
    }

    private string File(string relativePath, string text)
    {
        var path = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        System.IO.File.WriteAllText(path, text, new UTF8Encoding(false));
        return path;
    }

    private string DirectoryPath(string relativePath)
    {
        var path = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(path);
        return path;
    }

    private string Package(string name, string assembly)
    {
        var path = Path.Combine(_root, name);
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        WriteEntry(archive, "Example.nuspec", "<package><metadata><id>Example</id><version>1.0.0</version><authors>Example</authors><description>Example</description></metadata></package>");
        archive.CreateEntryFromFile(assembly, "lib/net10.0/Example.dll");
        return path;
    }

    private static void CompileAssembly(string path, string source)
    {
        var compilation = CSharpCompilation.Create("Fixture",
            syntaxTrees: [CSharpSyntaxTree.ParseText(source)],
            references: [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)],
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var result = compilation.Emit(path);
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
    }

    private static void WriteEntry(ZipArchive archive, string path, string text)
    {
        using var writer = new StreamWriter(archive.CreateEntry(path).Open());
        writer.Write(text);
    }

    public void Dispose()
    {
        Directory.Delete(_root, recursive: true);
    }
}
