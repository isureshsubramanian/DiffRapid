using DiffRapid.Cli;
using DiffRapid.Core.Structured;

namespace DiffRapid.Core.Tests;

public sealed class StructuredTests
{
    [Fact]
    public void ReorderedJsonKeysAreIdentical()
    {
        var left = """{ "name": "app", "version": "1.0", "nested": { "a": 1, "b": [true, null] } }""";
        var right = """
            {
              "nested": { "b": [true, null], "a": 1 },
              "version": "1.0",
              "name": "app"
            }
            """;

        var result = StructuredComparer.Compare(left, right, new StructuredCompareOptions { Format = StructuredFormat.Json });

        Assert.True(result.IsIdentical);
    }

    [Fact]
    public void JsonValueChangesCarryPathsAndLineSpans()
    {
        var left = "{\n  \"a\": 1,\n  \"b\": {\n    \"c\": \"x\"\n  }\n}";
        var right = "{\n  \"b\": {\n    \"c\": \"y\"\n  },\n  \"d\": false\n}";

        var differences = StructuredComparer.Compare(left, right, new StructuredCompareOptions { Format = StructuredFormat.Json }).Differences;

        var changed = Assert.Single(differences, difference => difference.Kind == StructuredChangeKind.Changed);
        Assert.Equal("/b/c", changed.Path);
        Assert.Equal(new StructuredLineSpan(4, 4), changed.LeftSpan);
        Assert.Equal(new StructuredLineSpan(3, 3), changed.RightSpan);
        Assert.Equal("/a", Assert.Single(differences, difference => difference.Kind == StructuredChangeKind.Removed).Path);
        var added = Assert.Single(differences, difference => difference.Kind == StructuredChangeKind.Added);
        Assert.Equal("/d", added.Path);
        Assert.Null(added.LeftSpan);
        Assert.Equal(3, differences.Count);
    }

    [Fact]
    public void JsonObjectSpansCoverTheirLines()
    {
        var root = JsonTreeReader.Read("{\n  \"b\": {\n    \"c\": 1\n  },\n  \"s\": \"t\"\n}");

        Assert.Equal(new StructuredLineSpan(1, 6), root.Span);
        Assert.Equal(new StructuredLineSpan(2, 4), root.Children[0].Span);
        Assert.Equal(StructuredNodeKind.Number, root.Children[0].Children[0].Kind);
        Assert.Equal("t", root.Children[1].Value);
    }

    [Fact]
    public void JsonStringAndNumberWithTheSameTextDiffer()
    {
        var differences = StructuredComparer.Compare("""{"a": "1"}""", """{"a": 1}""", new StructuredCompareOptions { Format = StructuredFormat.Json }).Differences;

        Assert.Equal(StructuredChangeKind.Changed, Assert.Single(differences).Kind);
    }

    [Fact]
    public void JsonArraysMatchByIndexByDefault()
    {
        var differences = StructuredComparer.Compare("""{"list": [1, 2, 3]}""", """{"list": [1, 3]}""", new StructuredCompareOptions { Format = StructuredFormat.Json }).Differences;

        Assert.Collection(
            differences,
            difference =>
            {
                Assert.Equal(StructuredChangeKind.Changed, difference.Kind);
                Assert.Equal("/list[1]", difference.Path);
            },
            difference =>
            {
                Assert.Equal(StructuredChangeKind.Removed, difference.Kind);
                Assert.Equal("/list[2]", difference.Path);
            });
    }

    [Fact]
    public void JsonArraysMatchByKeyPropertyWhenGiven()
    {
        var left = """{"items": [{"id": 1, "name": "one"}, {"id": 2, "name": "two"}, {"id": 3, "name": "three"}]}""";
        var right = """{"items": [{"id": 3, "name": "three"}, {"id": 1, "name": "one"}, {"id": 2, "name": "TWO"}]}""";
        var keyed = new StructuredCompareOptions { Format = StructuredFormat.Json, ArrayKey = "id" };

        var byKey = StructuredComparer.Compare(left, right, keyed).Differences;
        var found = StructuredComparer.Compare(left, right, keyed with { ArrayKey = null }).Differences;
        var byIndex = StructuredComparer.Compare(left, right, keyed with { ArrayKey = "missing" }).Differences;
        var ordered = StructuredComparer.Compare(left, right, keyed with { IgnoreOrder = false }).Differences;

        var changed = Assert.Single(byKey);
        Assert.Equal("/items[id=2]/name", changed.Path);
        Assert.Equal("two", changed.Left!.Value);
        Assert.Equal("TWO", changed.Right!.Value);
        Assert.Equal("/items[id=2]/name", Assert.Single(found).Path);
        Assert.True(byIndex.Count > 1);
        Assert.Equal("/items[id=3]", Assert.Single(ordered, difference => difference.Kind == StructuredChangeKind.Moved).Path);
    }

    [Theory]
    [InlineData("""{"l": [{"name": "a", "v": 1}]}""", """{"l": [{"name": "b", "v": 0}, {"name": "a", "v": 2}]}""", "/l[name=a]/v")]
    [InlineData("l:\n  - key: x\n    v: 1\n  - key: y\n    v: 1\n", "l:\n  - key: y\n    v: 1\n  - key: x\n    v: 2\n", "/l[key=x]/v")]
    public void ListsOfNamedObjectsMatchByTheirIdentity(string left, string right, string changedPath)
    {
        var differences = StructuredComparer.Compare(left, right, new StructuredCompareOptions()).Differences;

        Assert.Contains(differences, difference => difference.Kind == StructuredChangeKind.Changed && difference.Path == changedPath);
        Assert.DoesNotContain(differences, difference => difference.Kind == StructuredChangeKind.Removed);
    }

    [Fact]
    public void OrderedComparisonReportsMovedProperties()
    {
        var options = new StructuredCompareOptions { Format = StructuredFormat.Json, IgnoreOrder = false };

        var differences = StructuredComparer.Compare("""{"a": 1, "b": 2, "c": 3}""", """{"b": 2, "c": 3, "a": 1}""", options).Differences;

        var moved = Assert.Single(differences);
        Assert.Equal(StructuredChangeKind.Moved, moved.Kind);
        Assert.Equal("/a", moved.Path);
    }

    [Theory]
    [InlineData("{\n  \"a\": 1,\n  \"b\": 2,\n}", 4, "expected a property name")]
    [InlineData("{\n  \"a\": 1\n  \"b\": 2\n}", 3, "expected ','")]
    [InlineData("[1, 2", 1, "end of the file")]
    [InlineData("{\"a\": 1, \"a\": 2}", 1, "duplicate property 'a'")]
    [InlineData("{\"a\": 01}", 1, "leading zeros")]
    [InlineData("{\"a\": \"x\n\"}", 1, "control character")]
    [InlineData("", 1, "empty")]
    [InlineData("{} {}", 1, "after the JSON value")]
    public void MalformedJsonReportsTheLine(string text, int line, string reason)
    {
        var error = Assert.Throws<StructuredFormatException>(() => JsonTreeReader.Read(text));

        Assert.Equal(line, error.Line);
        Assert.Contains(reason, error.Message);
        Assert.StartsWith("line " + line, error.Message);
    }

    [Fact]
    public void DeeplyNestedJsonIsRejected()
    {
        var text = new string('[', 600) + new string(']', 600);

        Assert.Contains("too deeply", Assert.Throws<StructuredFormatException>(() => JsonTreeReader.Read(text)).Message);
    }

    [Fact]
    public void MSBuildVersionInAttributeOrElementComparesAsMetadata()
    {
        var left = """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <PackageReference Include="Newtonsoft.Json" Version="13.0.1" />
                <PackageReference Include="xunit" Version="2.9.0" />
              </ItemGroup>
            </Project>
            """;
        var right = """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <PackageReference Include="xunit" Version="2.9.0" />
                <PackageReference Include="Newtonsoft.Json">
                  <Version>13.0.3</Version>
                </PackageReference>
              </ItemGroup>
            </Project>
            """;
        var sameVersion = right.Replace("13.0.3", "13.0.1");
        var options = new StructuredCompareOptions { Format = StructuredFormat.MSBuild };

        var changed = Assert.Single(StructuredComparer.Compare(left, right, options).Differences);
        var identical = StructuredComparer.Compare(left, sameVersion, options);

        Assert.Equal(StructuredChangeKind.Changed, changed.Kind);
        Assert.Equal("/Project/PackageReference[Include=Newtonsoft.Json]/@Version", changed.Path);
        Assert.Equal("13.0.1", changed.Left!.Value);
        Assert.Equal("13.0.3", changed.Right!.Value);
        Assert.Equal(new StructuredLineSpan(3, 3), changed.LeftSpan);
        Assert.Equal(new StructuredLineSpan(5, 5), changed.RightSpan);
        Assert.True(identical.IsIdentical);
    }

    [Fact]
    public void MSBuildItemsMatchAcrossItemGroups()
    {
        var left = """
            <Project>
              <ItemGroup>
                <ProjectReference Include="..\Core\Core.csproj" />
                <Compile Include="A.cs" />
              </ItemGroup>
              <ItemGroup>
                <None Update="data.json" CopyToOutputDirectory="Always" />
              </ItemGroup>
            </Project>
            """;
        var right = """
            <Project>
              <ItemGroup>
                <None Update="data.json" CopyToOutputDirectory="PreserveNewest" />
                <Compile Include="A.cs" />
                <Compile Include="B.cs" />
              </ItemGroup>
              <ItemGroup>
                <ProjectReference Include="..\Core\Core.csproj" />
              </ItemGroup>
            </Project>
            """;

        var differences = StructuredComparer.Compare(left, right, new StructuredCompareOptions { Format = StructuredFormat.MSBuild }).Differences;

        Assert.Equal(2, differences.Count);
        Assert.Contains(differences, difference => difference.Kind == StructuredChangeKind.Changed && difference.Path == "/Project/None[Update=data.json]/@CopyToOutputDirectory");
        Assert.Contains(differences, difference => difference.Kind == StructuredChangeKind.Added && difference.Path == "/Project/Compile[Include=B.cs]");
    }

    [Fact]
    public void MSBuildConditionsArePartOfTheKey()
    {
        var left = """
            <Project>
              <PropertyGroup>
                <OutputType>Exe</OutputType>
              </PropertyGroup>
              <PropertyGroup Condition="'$(Configuration)' == 'Debug'">
                <DefineConstants>DEBUG;TRACE</DefineConstants>
              </PropertyGroup>
              <PropertyGroup Condition="'$(Configuration)' == 'Release'">
                <DefineConstants>TRACE</DefineConstants>
              </PropertyGroup>
            </Project>
            """;
        var right = """
            <Project>
              <PropertyGroup Condition="'$(Configuration)' == 'Release'">
                <DefineConstants>TRACE;RELEASE</DefineConstants>
              </PropertyGroup>
              <PropertyGroup>
                <OutputType>Exe</OutputType>
                <DefineConstants Condition="'$(Configuration)' == 'Debug'">DEBUG;TRACE</DefineConstants>
              </PropertyGroup>
            </Project>
            """;

        var differences = StructuredComparer.Compare(left, right, new StructuredCompareOptions { Format = StructuredFormat.MSBuild }).Differences;

        var changed = Assert.Single(differences);
        Assert.Equal(StructuredChangeKind.Changed, changed.Kind);
        Assert.Equal("/Project/DefineConstants[Condition='$(Configuration)' == 'Release']", changed.Path);
        Assert.Equal("TRACE;RELEASE", changed.Right!.Value);
    }

    [Fact]
    public void ResxReportsRenamedKeysAndValueAndCommentChanges()
    {
        var left = Resx(
            """<data name="Greeting" xml:space="preserve"><value>Hello</value><comment>Shown first</comment></data>""",
            """<data name="OldName" xml:space="preserve"><value>Same text</value></data>""",
            """<data name="Farewell" xml:space="preserve"><value>Bye</value></data>""");
        var right = Resx(
            """<data name="Farewell" xml:space="preserve"><value>Goodbye</value></data>""",
            """<data name="NewName" xml:space="preserve"><value>Same text</value></data>""",
            """<data name="Greeting" xml:space="preserve"><value>Hello</value><comment>Shown on start</comment></data>""");

        var result = StructuredComparer.Compare(left, right, fileName: "Strings.resx");

        Assert.Equal(StructuredFormat.Resx, result.Format);
        Assert.Equal(3, result.Differences.Count);
        Assert.Contains(result.Differences, difference => difference.Kind == StructuredChangeKind.Changed && difference.Path == "/root/data[name=Greeting]/comment");
        Assert.Contains(result.Differences, difference => difference.Kind == StructuredChangeKind.Changed && difference.Path == "/root/data[name=Farewell]/value");
        var renamed = Assert.Single(result.Differences, difference => difference.Kind == StructuredChangeKind.Renamed);
        Assert.Equal("/root/data[name=OldName]", renamed.Path);
        Assert.Equal("/root/data[name=NewName]", renamed.Right!.Path);
    }

    [Fact]
    public void ResxLeavesOutTheSchema()
    {
        var root = XmlTreeReader.Read(Resx("""<data name="A"><value>1</value></data>"""), StructuredFormat.Resx);
        var generic = XmlTreeReader.Read(Resx("""<data name="A"><value>1</value></data>"""));

        Assert.DoesNotContain(root.Children[0].Children, child => child.Name == "xsd:schema");
        Assert.Contains(generic.Children[0].Children, child => child.Name == "xsd:schema");
    }

    [Fact]
    public void XmlElementsWithoutIdentityAreNumberedInOrder()
    {
        var root = XmlTreeReader.Read("<a>\n  <b>1</b>\n  <b>2</b>\n  <c id=\"x\"/>\n</a>");
        var element = root.Children[0];

        Assert.Equal(["b", "b#2", "c[id=x]"], element.Children.Select(child => child.Key));
        Assert.Equal("/a/b#2", element.Children[1].Path);
        Assert.Equal(new StructuredLineSpan(1, 5), element.Span);
    }

    [Theory]
    [InlineData("<a>\n  <b>\n</a>", 3)]
    [InlineData("<a>\n<b x=\"1\" x=\"2\"/></a>", 2)]
    [InlineData("<a/>\n<b/>", 2)]
    public void MalformedXmlReportsTheLine(string text, int line)
    {
        var error = Assert.Throws<StructuredFormatException>(() => XmlTreeReader.Read(text));

        Assert.Equal(line, error.Line);
        Assert.StartsWith("line " + line, error.Message);
    }

    [Fact]
    public void XmlWithADocumentTypeIsRejected()
    {
        Assert.Throws<StructuredFormatException>(() => XmlTreeReader.Read("<!DOCTYPE a [<!ENTITY e \"x\">]><a>&e;</a>"));
    }

    [Fact]
    public void YamlSubsetReadsMapsSequencesAndScalars()
    {
        var text = """
            # leading comment
            ---
            name: build   # trailing comment
            on: [push, pull_request]
            env: {CI: true, LEVEL: 3}
            count: 0x1F
            empty:
            quoted: "a: b # not a comment\t"
            single: 'it''s'
            jobs:
              - id: one
                steps:
                - run: echo 1
                - run: echo 2
              - id: two
                tags: []
            script: |
              line 1
              line 2
            folded: >-
              one
              two

              three
            ...
            """;

        var root = YamlTreeReader.Read(text);
        StructuredNode Child(StructuredNode node, string key) => node.Children.Single(child => child.Key == key);

        Assert.Equal(StructuredNodeKind.Object, root.Kind);
        Assert.Equal("build", Child(root, "name").Value);
        Assert.Equal(new[] { "push", "pull_request" }, Child(root, "on").Children.Select(child => child.Value));
        Assert.Equal(StructuredNodeKind.Boolean, Child(Child(root, "env"), "CI").Kind);
        Assert.Equal(StructuredNodeKind.Number, Child(Child(root, "env"), "LEVEL").Kind);
        Assert.Equal(StructuredNodeKind.Number, Child(root, "count").Kind);
        Assert.Equal(StructuredNodeKind.Null, Child(root, "empty").Kind);
        Assert.Equal("a: b # not a comment\t", Child(root, "quoted").Value);
        Assert.Equal("it's", Child(root, "single").Value);
        var jobs = Child(root, "jobs");
        Assert.Equal(2, jobs.Children.Count);
        Assert.Equal("echo 2", Child(Child(Child(jobs, "[id=one]"), "steps"), "[1]").Children[0].Value);
        Assert.Equal(StructuredNodeKind.Array, Child(Child(jobs, "[id=two]"), "tags").Kind);
        Assert.Equal("line 1\nline 2\n", Child(root, "script").Value);
        Assert.Equal(new StructuredLineSpan(17, 19), Child(root, "script").Span);
        Assert.Equal("one two\nthree", Child(root, "folded").Value);
        Assert.Equal(new StructuredLineSpan(10, 16), jobs.Span);
    }

    [Fact]
    public void YamlComparisonMatchesKeysAndSequenceItemsByKey()
    {
        var left = """
            services:
              - name: web
                image: web:1
              - name: db
                image: postgres:15
            version: 2
            """;
        var right = """
            version: 2
            services:
            - name: db
              image: postgres:16
            - name: web
              image: web:1
            """;
        var options = new StructuredCompareOptions { Format = StructuredFormat.Yaml, ArrayKey = "name" };

        var changed = Assert.Single(StructuredComparer.Compare(left, right, options).Differences);

        Assert.Equal("/services[name=db]/image", changed.Path);
        Assert.Equal(new StructuredLineSpan(5, 5), changed.LeftSpan);
        Assert.Equal(new StructuredLineSpan(4, 4), changed.RightSpan);
    }

    [Theory]
    [InlineData("a: 1\n\tb: 2", 2, "tabs")]
    [InlineData("a: &anchor 1\nb: *anchor", 1, "anchors")]
    [InlineData("a: !Ref x", 1, "tags")]
    [InlineData("a: 1\n   b: 2", 2, "unexpected indentation")]
    [InlineData("a: 1\na: 2", 2, "duplicate key 'a'")]
    [InlineData("a: \"open\nb: 1", 1, "not closed")]
    [InlineData("a: [1, 2\nb: 1", 1, "several lines")]
    [InlineData("a: 1\n---\nb: 2", 2, "several documents")]
    [InlineData("? complex\n: value", 1, "complex keys")]
    [InlineData("a: 1\n- b", 2, "expected 'key: value'")]
    [InlineData("a: plain\n  continued", 2, "next line")]
    [InlineData("%YAML 1.2\n---\na: 1", 1, "directives")]
    public void MalformedOrUnsupportedYamlReportsTheLine(string text, int line, string reason)
    {
        var error = Assert.Throws<StructuredFormatException>(() => YamlTreeReader.Read(text));

        Assert.Equal(line, error.Line);
        Assert.Contains(reason, error.Message);
    }

    [Fact]
    public void YamlNullAndBooleanSpellingsCompareEqual()
    {
        var options = new StructuredCompareOptions { Format = StructuredFormat.Yaml };

        Assert.True(StructuredComparer.Compare("a:\nb: True\nc: ~", "a: null\nb: true\nc: NULL", options).IsIdentical);
        Assert.False(StructuredComparer.Compare("a: 'true'", "a: true", options).IsIdentical);
    }

    [Fact]
    public void ByteOrderMarksAreSkipped()
    {
        Assert.Equal(StructuredNodeKind.Object, JsonTreeReader.Read("\uFEFF{}").Kind);
        Assert.Equal("1", YamlTreeReader.Read("\uFEFFa: 1").Children[0].Value);
        Assert.Equal(StructuredFormat.Json, StructuredReader.DetectFormat(null, "\uFEFF{}"));
        Assert.Equal("\u00a0", YamlTreeReader.Read("a: \"\\_\"").Children[0].Value);
    }

    [Fact]
    public void ComparingStopsWhenCancelled()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(() => StructuredComparer.Compare("{}", "{}", new StructuredCompareOptions { Format = StructuredFormat.Json }, cancellationToken: cancellation.Token));
    }

    [Fact]
    public void JsonCanonicalTextIsKeyOrdered()
    {
        var root = JsonTreeReader.Read("""{"b": [3, {"y": 1, "x": "q\""}], "a": {}, "c": null}""");

        var text = StructuredTextWriter.Write(root, StructuredFormat.Json);

        Assert.Equal("{\n  \"a\": {},\n  \"b\": [\n    3,\n    {\n      \"x\": \"q\\\"\",\n      \"y\": 1\n    }\n  ],\n  \"c\": null\n}\n", text);
        Assert.Empty(StructuredComparer.Compare(root, JsonTreeReader.Read(text)));
    }

    [Fact]
    public void CanonicalTextOrdersListsOfNamedObjectsByTheirIdentity()
    {
        var left = JsonTreeReader.Read("""{"f": [{"id": "search", "on": true}, {"id": "cart", "on": true}]}""");
        var right = JsonTreeReader.Read("""{"f": [{"on": true, "id": "cart"}, {"on": false, "id": "search"}]}""");

        var leftText = StructuredTextWriter.Write(left, StructuredFormat.Json);
        var rightText = StructuredTextWriter.Write(right, StructuredFormat.Json);

        Assert.True(leftText.IndexOf("cart", StringComparison.Ordinal) < leftText.IndexOf("search", StringComparison.Ordinal));
        Assert.Equal(leftText.Replace("\"on\": true\n    }\n  ]", "\"on\": false\n    }\n  ]"), rightText);
        Assert.Contains("- name: a", StructuredTextWriter.Write(YamlTreeReader.Read("l:\n  - name: b\n  - name: a\n"), StructuredFormat.Yaml).Split('\n')[1]);
    }

    [Theory]
    [InlineData("""{"l": ["b", "a"]}""")]
    [InlineData("""{"l": [{"id": 2}, {"id": 1}, {"x": 0}]}""")]
    [InlineData("""{"l": [{"id": 2}, {"id": 2, "v": 1}, {"id": 1}]}""")]
    public void CanonicalTextKeepsTheOrderOfOtherLists(string json)
    {
        var text = StructuredTextWriter.Write(JsonTreeReader.Read(json), StructuredFormat.Json);

        Assert.True(text.IndexOf('2') < text.IndexOf('1') || text.IndexOf("\"b\"", StringComparison.Ordinal) < text.IndexOf("\"a\"", StringComparison.Ordinal));
    }

    [Fact]
    public void YamlCanonicalTextReadsBackUnchanged()
    {
        var root = YamlTreeReader.Read("""
            z: "needs: quotes"
            a:
              - x: 1
                w: [true, "1", ~]
              - - nested
              - plain
            m: {}
            s: "line\nbreak"
            """);

        var text = StructuredTextWriter.Write(root, StructuredFormat.Yaml);

        Assert.StartsWith("a:\n  - w:\n      - true\n      - \"1\"\n      - null\n    x: 1\n  - - nested\n  - plain\nm: {}\n", text);
        Assert.Empty(StructuredComparer.Compare(root, YamlTreeReader.Read(text)));
    }

    [Fact]
    public void MSBuildCanonicalTextAlignsAttributeAndElementMetadata()
    {
        var attribute = XmlTreeReader.Read("<Project><ItemGroup><PackageReference Include=\"A\" Version=\"1\" /></ItemGroup></Project>", StructuredFormat.MSBuild);
        var element = XmlTreeReader.Read("<Project>\n<ItemGroup>\n<PackageReference Include=\"A\">\n<Version>1</Version>\n</PackageReference>\n</ItemGroup>\n</Project>", StructuredFormat.MSBuild);

        var expected = "<Project>\n  <PackageReference Include=\"A\" Version=\"1\" />\n</Project>\n";

        Assert.Equal(expected, StructuredTextWriter.Write(attribute, StructuredFormat.MSBuild));
        Assert.Equal(expected, StructuredTextWriter.Write(element, StructuredFormat.MSBuild));
    }

    [Theory]
    [InlineData("App.csproj", "", StructuredFormat.MSBuild)]
    [InlineData("Directory.Build.props", "", StructuredFormat.MSBuild)]
    [InlineData("Strings.resx", "", StructuredFormat.Resx)]
    [InlineData("package.json", "", StructuredFormat.Json)]
    [InlineData("ci.YML", "", StructuredFormat.Yaml)]
    [InlineData("web.config", "", StructuredFormat.Xml)]
    [InlineData(null, "  <a/>", StructuredFormat.Xml)]
    [InlineData("data.txt", "\n[1]", StructuredFormat.Json)]
    [InlineData(null, "a: 1", StructuredFormat.Yaml)]
    public void DetectsTheFormat(string? fileName, string text, StructuredFormat expected)
    {
        Assert.Equal(expected, StructuredReader.DetectFormat(fileName, text));
    }

    [Fact]
    public void CommandReturnsExitCodesAndFormatsDifferences()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(directory);
        try
        {
            var left = Path.Combine(directory, "left.json");
            var right = Path.Combine(directory, "right.json");
            var same = Path.Combine(directory, "same.json");
            var broken = Path.Combine(directory, "broken.yaml");
            File.WriteAllText(left, """{"a": 1, "b": [{"id": "x", "v": 1}]}""");
            File.WriteAllText(right, """{"b": [{"v": 2, "id": "x"}], "a": 1}""");
            File.WriteAllText(same, """{"b": [{"v": 1, "id": "x"}], "a": 1}""");
            File.WriteAllText(broken, "a: 1\n\tb: 2");

            Assert.Equal(0, Program.Main(["struct", left, same]));
            Assert.Equal(1, Program.Main(["struct", left, right, "--array-key", "id"]));
            Assert.Equal(2, Program.Main(["struct", broken, broken]));
            Assert.Equal(2, Program.Main(["struct", left, right, "--format", "toml"]));
            Assert.Equal(2, Program.Main(["struct", left]));
            Assert.Equal(2, Program.Main(["struct", left, broken, "--format", "json"]));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void DifferenceLinesShowKindPathSpansAndValues()
    {
        var differences = StructuredComparer.Compare("{\n\"a\": 1,\n\"b\": \"x\"\n}", "{\n\"a\": 2\n}", new StructuredCompareOptions { Format = StructuredFormat.Json }).Differences;

        Assert.Equal("Changed  /a  (left 2, right 2)  1 -> 2", Program.FormatDifference(differences[0]));
        Assert.Equal("Removed  /b  (left 3)", Program.FormatDifference(differences[1]));
    }

    private static string Resx(params string[] data) =>
        $"""
        <?xml version="1.0" encoding="utf-8"?>
        <root>
          <xsd:schema id="root" xmlns="" xmlns:xsd="http://www.w3.org/2001/XMLSchema">
            <xsd:element name="root" />
          </xsd:schema>
          <resheader name="resmimetype"><value>text/microsoft-resx</value></resheader>
          {string.Join("\n  ", data)}
        </root>
        """;
}
