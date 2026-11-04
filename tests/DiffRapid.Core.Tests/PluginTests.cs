using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using DiffRapid.Cli;
using DiffRapid.Core.Plugins;

namespace DiffRapid.Core.Tests;

public sealed class PluginTests
{
    private static readonly string SortProgram = OperatingSystem.IsWindows() ? "sort.exe" : "sort";

    [Fact]
    public void ParsesStepsArgumentsAndQuotes()
    {
        var steps = PluginPipelineParser.Parse(" PrettifyJson indent=4 | ReplaceRegex \"a | b\" 'x y' | Exec \"say \"\"hi\"\"\" C:\\tools\\a.exe ");

        Assert.Equal(["PrettifyJson", "ReplaceRegex", "Exec"], steps.Select(step => step.Name));
        Assert.Equal(["indent=4"], steps[0].Arguments);
        Assert.Equal(["a | b", "x y"], steps[1].Arguments);
        Assert.Equal(["say \"hi\"", @"C:\tools\a.exe"], steps[2].Arguments);
        Assert.Equal(["\"quoted\"", "ab"], PluginPipelineParser.Parse("X '\"quoted\"' a'b'")[0].Arguments);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("SortLines |")]
    [InlineData("| SortLines")]
    [InlineData("SortLines || ToLower")]
    [InlineData("ReplaceRegex \"unterminated")]
    [InlineData("ReplaceRegex 'unterminated")]
    public void RejectsMalformedPipelines(string pipeline)
    {
        Assert.Throws<FormatException>(() => PluginPipelineParser.Parse(pipeline));
    }

    [Fact]
    public void RejectsUnknownPluginsAndUnpackersInThePredifferPipeline()
    {
        Assert.Throws<FormatException>(() => PluginPipeline.ApplyPrediffers("x", "a.txt", new PluginOptions { Prediffer = "NoSuchThing" }));
        var unpackerAsPrediffer = Assert.Throws<FormatException>(() =>
            PluginPipeline.ApplyPrediffers("x", "a.txt", new PluginOptions { Prediffer = "ZipEntries" }));
        Assert.Contains("unpacker", unpackerAsPrediffer.Message);
        Assert.Throws<FormatException>(() => PluginPipeline.Prepare([], "a.txt", new PluginOptions { Unpacker = "NoSuchThing" }));
    }

    [Fact]
    public void RegistryFindsByNameSelectsByPatternAndReplaces()
    {
        var registry = PluginRegistry.CreateDefault();
        Assert.Same(registry.FindPrediffer("SortLines"), registry.FindPrediffer("sortlines"));
        Assert.Equal("ZipEntries", registry.SelectUnpacker("Package.NUPKG")?.Name);
        Assert.Null(registry.SelectUnpacker("readme.txt"));

        var custom = new ExternalCommandPlugin("PdfText", "pdftotext", ["{file}", "-"], "*.pdf;*.PDFX");
        registry.Register(custom);
        Assert.Same(custom, registry.SelectUnpacker(@"C:\docs\report.pdf"));
        Assert.Same(custom, registry.FindPrediffer("pdftext"));

        var replacement = new ExternalCommandPlugin("SortLines", SortProgram);
        registry.Register(replacement);
        Assert.Same(replacement, registry.FindPrediffer("SortLines"));
        Assert.Equal(1, registry.Prediffers.Count(plugin => plugin.Name == "SortLines"));
        Assert.NotSame(replacement, PluginRegistry.Default.FindPrediffer("SortLines"));
    }

    [Fact]
    public void PrettifyJsonIndentsSortsKeysAndKeepsNumbers()
    {
        const string json = "{\"b\":[1,2.50,{\"z\":null}],\"a\":\"é\" /* comment */,}";

        Assert.Equal("{\n  \"b\": [\n    1,\n    2.50,\n    {\n      \"z\": null\n    }\n  ],\n  \"a\": \"é\"\n}\n", Transform("PrettifyJson", json));
        Assert.Equal("{\n    \"a\": \"é\",\n    \"b\": [\n        1,\n        2.50,\n        {\n            \"z\": null\n        }\n    ]\n}\n",
            Transform("PrettifyJson", json, "sort", "indent=4"));
        Assert.Throws<PluginException>(() => Transform("PrettifyJson", "{ not json"));
        Assert.Throws<ArgumentException>(() => Transform("PrettifyJson", "{}", "indent=0"));
        Assert.Throws<ArgumentException>(() => Transform("PrettifyJson", "{}", "unknown"));
    }

    [Fact]
    public void PrettifyXmlIndentsAndKeepsDeclarationAndComments()
    {
        const string xml = "\uFEFF<?xml version=\"1.0\" encoding=\"utf-8\"?><!DOCTYPE a><!-- top --><a x=\"1\"><b>t</b><c/><?pi data?></a>";

        Assert.Equal("<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<!-- top -->\n<a x=\"1\">\n    <b>t</b>\n    <c />\n    <?pi data?>\n</a>\n",
            Transform("PrettifyXml", xml, "indent=4"));
        Assert.Equal("<a>\n  <b />\n</a>\n", Transform("PrettifyXml", "<a>  <b/>\n</a>"));
        Assert.Throws<PluginException>(() => Transform("PrettifyXml", "<a><b></a>"));
    }

    [Fact]
    public void JsonQuerySelectsPathsItemsAndKeys()
    {
        const string json = "{\"name\":\"x\",\"items\":[{\"id\":1,\"tags\":[\"a\"]},{\"id\":2}],\"my key\":{\"b\":1,\"a\":2}}";

        Assert.Equal("\"x\"\n", Transform("JsonQuery", json, ".name"));
        Assert.Equal("1\n2\n", Transform("JsonQuery", json, ".items[].id"));
        Assert.Equal("{\n  \"id\": 2\n}\n", Transform("JsonQuery", json, ".items[-1]"));
        Assert.Equal("\"a\"\n", Transform("JsonQuery", json, ".items.[0].tags[0]"));
        Assert.Equal("null\n", Transform("JsonQuery", json, ".missing.deeper"));
        Assert.Equal("null\nnull\n", Transform("JsonQuery", json, ".items[].missing"));
        Assert.Equal("[\n  \"a\",\n  \"b\"\n]\n", Transform("JsonQuery", json, ".\"my key\"", "keys"));
        Assert.Equal("[\n  \"a\",\n  \"b\"\n]\n", Transform("JsonQuery", json, ".[\"my key\"]", "keys"));
        Assert.Equal("[\n  \"items\",\n  \"my key\",\n  \"name\"\n]\n", Transform("JsonQuery", json, "keys"));
        Assert.Equal("[\n  0,\n  1\n]\n", Transform("JsonQuery", json, ".items", "keys"));
        Assert.Equal("1\n2\n", Transform("JsonQuery", "[1,2]", ".[]"));
        Assert.Throws<PluginException>(() => Transform("JsonQuery", json, ".name.first"));
        Assert.Throws<PluginException>(() => Transform("JsonQuery", json, ".name[0]"));
        Assert.Throws<PluginException>(() => Transform("JsonQuery", json, ".name[]"));
        Assert.Throws<ArgumentException>(() => Transform("JsonQuery", json, "name"));
        Assert.Throws<ArgumentException>(() => Transform("JsonQuery", json, ".a..b"));
        Assert.Throws<ArgumentException>(() => Transform("JsonQuery", json, ".a[x]"));
    }

    [Fact]
    public void SortLinesKeepsTheLineEndingStyle()
    {
        Assert.Equal("a\r\nb\r\nB\r\nc\r\n",Transform("SortLines", "c\r\nb\r\nB\r\na\r\n", "ignorecase"));
        Assert.Equal("c\nb\na", Transform("SortLines", "b\na\nc", "desc"));
        Assert.Equal("B\na\nb\n", Transform("SortLines", "b\na\nB\n"));
        Assert.Equal("a\nb\n", Transform("SortLinesUnique", "b\na\nb\na\n"));
        Assert.Equal("a\nB\n", Transform("SortLinesUnique", "B\na\nb\n", "ignorecase"));
        Assert.Equal(string.Empty, Transform("SortLines", string.Empty));
    }

    [Fact]
    public void IgnoreColumnsRemovesCharacterRanges()
    {
        Assert.Equal("cdgh\nc\n", Transform("IgnoreColumns", "abcdefghij\nabc\n", "1-2,5-6,9-"));
        Assert.Equal("de", Transform("IgnoreColumns", "abcde", "-3"));
        Assert.Equal("acde", Transform("IgnoreColumns", "abcde", "2"));
        foreach (var invalid in new[] { "0", "3-1", "-", "a", "1,,2" })
        {
            Assert.Throws<ArgumentException>(() => Transform("IgnoreColumns", "abc", invalid));
        }

        Assert.Throws<ArgumentException>(() => Transform("IgnoreColumns", "abc"));
    }

    [Fact]
    public void IgnoreFieldsBlanksDelimitedFields()
    {
        Assert.Equal("a,,c,\n1,,3\n", Transform("IgnoreFields", "a,b,c,d\n1,2,3\n", "2,4-"));
        Assert.Equal("a\t\tc", Transform("IgnoreFields", "a\tb\tc", "2", "delimiter=tab"));
        Assert.Equal("a;;c", Transform("IgnoreFields", "a;b;c", "2", "delimiter=;"));
        Assert.Throws<ArgumentException>(() => Transform("IgnoreFields", "a", "1", "delimiter=ab"));
    }

    [Fact]
    public void IgnoreCommentsRemovesEachStyleAndKeepsLineCount()
    {
        const string code = "int a = 1; // note\n/* block\n   comment */ int b; s = \"// kept\"; c = '/*';\n";
        Assert.Equal("int a = 1;\n\n int b; s = \"// kept\"; c = '/*';\n", Transform("IgnoreComments", code));
        Assert.Equal("x = 1\ny = \"#no\"\n", Transform("IgnoreComments", "x = 1 # set x\ny = \"#no\"\n", "script"));
        Assert.Equal("select 1\nfrom t\n", Transform("IgnoreComments", "select 1 -- one\nfrom /* table */t\n", "sql"));
        Assert.Equal("<a>\n</a> it's\n", Transform("IgnoreComments", "<a><!-- multi\nline --></a> it's\n", "xml"));
        Assert.Equal("a\nb\n", Transform("IgnoreComments", "a // x\nb # y\n", "c", "script"));
        Assert.Equal("s = \"a\\\"//b\"\n", Transform("IgnoreComments", "s = \"a\\\"//b\" // c\n"));
        Assert.Equal("open \"x\n", Transform("IgnoreComments", "open \"x\n"));
        Assert.Throws<ArgumentException>(() => Transform("IgnoreComments", "x", "cobol"));
    }

    [Fact]
    public void IgnoreLineNumbersReplaceRegexAndCase()
    {
        Assert.Equal("PRINT \"A\"\nGOTO 10\nno number\n", Transform("IgnoreLineNumbers", "10 PRINT \"A\"\n  20\tGOTO 10\nno number\n"));
        Assert.Equal("v# build #\nv#", Transform("ReplaceRegex", "v1 build 22\nv3", @"\d+", "#"));
        Assert.Equal("X-X\n", Transform("ReplaceRegex", "a-A\n", "^a|a$", "X", "ignorecase"));
        Assert.Equal("[ab]", Transform("ReplaceRegex", "ab", "(.+)", "[$1]"));
        Assert.Throws<ArgumentException>(() => Transform("ReplaceRegex", "x", "["));
        Assert.Throws<ArgumentException>(() => Transform("ReplaceRegex", "x", "only-pattern"));
        Assert.Equal("mixed ı\n", Transform("ToLower", "MiXeD ı\n"));
        Assert.Equal("MIXED\n", Transform("ToUpper", "mixed\n"));
    }

    [Fact]
    public void TabifyAndUntabifyUseTabStops()
    {
        Assert.Equal("\tx\n\t\t y\nz  w\n",Transform("Tabify", "    x\n \t     y\nz  w\n"));
        Assert.Equal("\t x", Transform("Tabify", "   x", "tabsize=2"));
        Assert.Equal("    x\nab  c\n", Transform("Untabify", "\tx\nab\tc\n"));
        Assert.Equal("a       b", Transform("Untabify", "a\tb", "tabsize=8"));
        Assert.Throws<ArgumentException>(() => Transform("Untabify", "x", "tabsize=zero"));
    }

    [Fact]
    public void ZipEntriesListsEntriesAndText()
    {
        var zip = CreateZip(("b.txt", "beta\n"u8.ToArray()), ("a/", []), ("a/one.json", "{\"k\":1}"u8.ToArray()), ("bin.dat", [0, 1, 0, 2]));
        var unpacker = PluginRegistry.Default.FindUnpacker("ZipEntries")!;

        var result = unpacker.Unpack(new MemoryStream(zip), PluginContext.ForArguments());

        Assert.Equal(["a/", "a/one.json", "b.txt", "bin.dat"], result.Entries.Select(entry => entry.Name));
        Assert.True(result.Entries[3].IsBinary);
        Assert.Null(result.Entries[3].Text);
        Assert.Equal("{\"k\":1}", result.Entries[1].Text);
        Assert.Equal(
            "a/\t0 bytes\na/one.json\t7 bytes\nb.txt\t5 bytes\nbin.dat\t4 bytes (binary)\n\n=== a/one.json ===\n{\"k\":1}\n\n=== b.txt ===\nbeta\n",
            result.Text);

        var listed = unpacker.Unpack(new MemoryStream(zip), PluginContext.ForArguments("list"));
        Assert.Equal("a/\t0 bytes\na/one.json\t7 bytes\nb.txt\t5 bytes\nbin.dat\t4 bytes\n", listed.Text);
        Assert.Throws<PluginException>(() => unpacker.Unpack(new MemoryStream("not a zip"u8.ToArray()), PluginContext.ForArguments()));
    }

    [Fact]
    public void PrepareRunsUnpackerThenPrediffers()
    {
        var zip = CreateZip(("b.txt", "Beta\n"u8.ToArray()), ("a.txt", "Alpha\n"u8.ToArray()));

        var prepared = PluginPipeline.Prepare(zip, "pack.zip", new PluginOptions { Unpacker = "auto", Prediffer = "ToUpper" });

        Assert.Equal("ZipEntries", prepared.UnpackerName);
        Assert.Equal(2, prepared.Entries.Count);
        Assert.Contains("=== a.txt ===\nAlpha\n", prepared.Text);
        Assert.Contains("=== A.TXT ===\nALPHA\n", prepared.CompareText);

        var chained = PluginPipeline.Prepare("{\"b\":1,\"a\":2}"u8.ToArray(), "x.json",
            new PluginOptions { Unpacker = "PrettifyJson sort | SortLines" });
        Assert.Equal("  \"a\": 2,\n  \"b\": 1\n{\n}\n", chained.Text);
        Assert.Equal(chained.Text, chained.CompareText);

        var plain = PluginPipeline.Prepare("x\n"u8.ToArray(), "x.txt", new PluginOptions { Unpacker = "auto" });
        Assert.Null(plain.UnpackerName);
        Assert.Equal("x\n", plain.Text);

        var binary = PluginPipeline.Prepare([0, 1, 2, 0], "x.bin", new PluginOptions { Prediffer = "ToUpper" });
        Assert.True(binary.IsBinary);
        Assert.Equal(string.Empty, binary.CompareText);
        Assert.Throws<OperationCanceledException>(() =>
            PluginPipeline.Prepare("x"u8.ToArray(), "x.txt", new PluginOptions { Prediffer = "ToUpper" }, new CancellationToken(true)));
    }

    [Fact]
    public void ExternalCommandReadsStandardInputOrATempFile()
    {
        var registry = PluginRegistry.CreateDefault();
        registry.Register(new ExternalCommandPlugin("SortFile", SortProgram, [ExternalCommandPlugin.FilePlaceholder]));
        var options = new PluginOptions { Registry = registry, Prediffer = "Exec " + SortProgram };

        // Windows sort.exe only splits lines at CRLF, so the input uses the platform line ending.
        Assert.Equal("a\nb\nc\n", Normalize(PluginPipeline.ApplyPrediffers(Lines("c", "b", "a"), "x.txt", options)));
        Assert.Equal("a\nb\n", Normalize(PluginPipeline.ApplyPrediffers(Lines("b", "a"), "x.txt", options with { Prediffer = "SortFile" })));

        using var input = new MemoryStream(Encoding.UTF8.GetBytes(Lines("z", "y")));
        var unpacked = registry.FindUnpacker("SortFile")!.Unpack(input, new PluginContext("in.c16tmp", [], TimeSpan.FromSeconds(30), CancellationToken.None));
        Assert.Equal("y\nz\n", Normalize(unpacked.Text));
        Assert.Empty(Directory.GetFiles(Path.GetTempPath(), "diffrapid-*.c16tmp"));
    }

    [Fact]
    public void ExternalCommandReportsTimeoutsFailuresAndMissingPrograms()
    {
        var sleeper = OperatingSystem.IsWindows()
            ? new ExternalCommandPlugin("Sleep", "ping.exe", ["-n", "30", "127.0.0.1"])
            : new ExternalCommandPlugin("Sleep", "sleep", ["30"]);
        var stopwatch = Stopwatch.StartNew();
        var timeout = Assert.Throws<PluginException>(() =>
            sleeper.Transform("x", new PluginContext("x.txt", [], TimeSpan.FromMilliseconds(300), CancellationToken.None)));
        Assert.Contains("did not finish", timeout.Message);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(15));

        var failing = OperatingSystem.IsWindows()
            ? new ExternalCommandPlugin("Find", "findstr.exe", ["no-such-text", ExternalCommandPlugin.FilePlaceholder])
            : new ExternalCommandPlugin("Find", "grep", ["no-such-text", ExternalCommandPlugin.FilePlaceholder]);
        var failure = Assert.Throws<PluginException>(() => failing.Transform("abc\n", PluginContext.ForArguments()));
        Assert.Contains("exited with code 1", failure.Message);

        var missing = new ExternalCommandPlugin("Missing", "diffrapid-no-such-program-" + Guid.NewGuid().ToString("N"));
        Assert.Throws<PluginException>(() => missing.Transform("x", PluginContext.ForArguments()));
        Assert.Throws<ArgumentException>(() => PluginPipeline.ApplyPrediffers("x", "x.txt", new PluginOptions { Prediffer = "Exec" }));
    }

    [Fact]
    public void CliDiffRunsUnpackersAndPrediffers()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(directory);
        try
        {
            var left = Path.Combine(directory, "left.txt");
            var right = Path.Combine(directory, "right.txt");
            File.WriteAllText(left, "b // one\na\n");
            File.WriteAllText(right, "a\nb // two\n");
            Assert.Equal(1, Program.Main(["diff", left, right, "--quiet"]));
            Assert.Equal(0, Program.Main(["diff", left, right, "--quiet", "--prediffer", "IgnoreComments c | SortLines"]));
            Assert.Equal(1, Program.Main(["diff", left, right, "--quiet", "--prediffer", "SortLines"]));
            Assert.Equal(2, Program.Main(["diff", left, right, "--quiet", "--prediffer", "SortLines |"]));
            Assert.Equal(2, Program.Main(["diff", left, right, "--quiet", "--prediffer", "NoSuchPlugin"]));
            Assert.Equal(2, Program.Main(["diff", left, right, "--quiet", "--prediffer", "PrettifyJson"]));
            Assert.Equal(2, Program.Main(["diff", left, right, "--quiet", "--prediffer"]));

            var leftZip = Path.Combine(directory, "left.zip");
            var rightZip = Path.Combine(directory, "right.zip");
            var changedZip = Path.Combine(directory, "changed.zip");
            File.WriteAllBytes(leftZip, CreateZip(("a.txt", "one\n"u8.ToArray()), ("b.txt", "two\n"u8.ToArray())));
            File.WriteAllBytes(rightZip, CreateZip(("b.txt", "two\n"u8.ToArray()), ("a.txt", "one\n"u8.ToArray())));
            File.WriteAllBytes(changedZip, CreateZip(("a.txt", "one\n"u8.ToArray()), ("b.txt", "TWO\n"u8.ToArray())));
            Assert.Equal(2, Program.Main(["diff", leftZip, rightZip, "--quiet"]));
            Assert.Equal(0, Program.Main(["diff", leftZip, rightZip, "--quiet", "--unpacker", "auto"]));
            Assert.Equal(1, Program.Main(["diff", leftZip, changedZip, "--quiet", "--unpacker", "ZipEntries"]));
            Assert.Equal(0, Program.Main(["diff", leftZip, changedZip, "--quiet", "--unpacker", "ZipEntries", "--prediffer", "ToLower"]));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void CliListsEveryBuiltInPlugin()
    {
        var listing = Program.FormatPlugins(PluginRegistry.Default);
        string[] names =
        [
            "ZipEntries", "Exec", "PrettifyJson", "PrettifyXml", "JsonQuery", "SortLines", "SortLinesUnique", "IgnoreColumns",
            "IgnoreFields", "IgnoreComments", "IgnoreLineNumbers", "ReplaceRegex", "ToLower", "ToUpper", "Tabify", "Untabify",
        ];

        Assert.All(names, name => Assert.Contains("  " + name, listing));
        Assert.Contains("IgnoreColumns <ranges such as 1-8,20->", listing);
        Assert.Equal(0, Program.Main(["plugins"]));
        Assert.Equal(2, Program.Main(["plugins", "extra"]));
        var parsed = Program.ParsedArgs.Parse(["diff", "a", "b", "--unpacker", "auto", "--prediffer", "SortLines desc"], 1);
        var options = Program.ToPluginOptions(parsed, null)!;
        Assert.Equal(["a", "b"], parsed.Positionals);
        Assert.Equal("auto", options.Unpacker);
        Assert.Equal("SortLines desc", options.Prediffer);
        Assert.Null(Program.ToPluginOptions(Program.ParsedArgs.Parse(["diff", "a", "b"], 1), null));
    }

    private static string Transform(string name, string text, params string[] arguments) =>
        PluginRegistry.Default.FindPrediffer(name)!.Transform(text, PluginContext.ForArguments(arguments));

    private static string Normalize(string text) => text.Replace("\r\n", "\n");

    private static string Lines(params string[] lines) => string.Concat(lines.Select(line => line + Environment.NewLine));

    private static byte[] CreateZip(params (string Name, byte[] Content)[] entries)
    {
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in entries)
            {
                using var stream = archive.CreateEntry(name).Open();
                stream.Write(content);
            }
        }

        return buffer.ToArray();
    }
}
