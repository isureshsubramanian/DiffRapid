using System.Diagnostics;
using System.Text;
using DiffRapid.Cli;
using DiffRapid.Core.Comparison;
using DiffRapid.Core.Text;
using DiffRapid.Semantic;

namespace DiffRapid.Core.Tests;

public sealed class SemanticTests
{
    private const string Shapes =
        """
        namespace Demo
        {
            public class Shape
            {
                public int Width { get; set; }

                public int Area(int scale)
                {
                    var total = Width * Width;
                    return total * scale;
                }

                public static int Helper(int x)
                {
                    var y = x * 2;
                    y += 3;
                    return y;
                }
            }

            public class Other
            {
                private int _value;
            }
        }
        """;

    [Fact]
    public void ListsMembersWithKindsSignaturesAndSpans()
    {
        var text = "namespace N;\nclass C<T>\n{\n    /// <summary>Doc.</summary>\n    int M(ref int a, string[] b) => a;\n    int _x, _y;\n    event System.Action E;\n    C() { }\n    static C() { }\n    int this[int i] => i;\n    public static C<T> operator +(C<T> a, C<T> b) => a;\n    enum Color { Red, Green }\n}\n";
        var roots = MemberMatcher.ListMembers(text);
        var ns = Assert.Single(roots);
        Assert.Equal(CSharpMemberKind.Namespace, ns.Kind);
        var type = Assert.Single(ns.Children);
        Assert.Equal("C<T>", type.Signature);
        Assert.Equal("N.C<T>", type.QualifiedName);
        var signatures = type.Children.Select(member => member.Kind + " " + member.Signature).ToArray();
        Assert.Equal(
            [
                "Method M(ref int, string[])",
                "Field _x",
                "Field _y",
                "Event E",
                "Constructor C()",
                "Constructor static C()",
                "Indexer this[int]",
                "Operator operator +(C<T>, C<T>)",
                "Enum Color",
            ],
            signatures);
        var method = type.Children[0];
        Assert.Equal(3, method.Span.StartLine);
        Assert.Equal(4, method.Span.EndLine);
        Assert.StartsWith("/// <summary>", text.Substring(method.Span.Start));
        Assert.Equal(["Red", "Green"], type.Children[8].Children.Select(member => member.Name));
    }

    [Fact]
    public void IdenticalFilesAreEqual()
    {
        var result = MemberMatcher.Compare(Shapes, Shapes);
        Assert.True(result.AreEqual);
        Assert.All(result.Flatten(), entry => Assert.Equal(MemberDiffStatus.Unchanged, entry.Status));
        Assert.Equal(7, result.Flatten().Count());
    }

    [Fact]
    public void DetectsRenamedMethod()
    {
        var right = Shapes.Replace("public int Area(int scale)", "public int ComputeArea(int scale)");
        var result = MemberMatcher.Compare(Shapes, right);
        var renamed = Assert.Single(result.Flatten(), entry => entry.Status == MemberDiffStatus.Renamed);
        Assert.Equal("Area(int)", renamed.Left!.Signature);
        Assert.Equal("ComputeArea(int)", renamed.Right!.Signature);
        Assert.False(renamed.TextChanged);
        Assert.Equal(1.0, renamed.Similarity);
        Assert.Equal(1, result.Flatten().Count(entry => entry.Status != MemberDiffStatus.Unchanged));
    }

    [Fact]
    public void RenamedMethodWithEditedBodyIsStillARename()
    {
        var right = Shapes
            .Replace("public static int Helper(int x)", "public static int Twice(int x)")
            .Replace("y += 3;", "y += 4;");
        var result = MemberMatcher.Compare(Shapes, right);
        var renamed = Assert.Single(result.Flatten(), entry => entry.Status == MemberDiffStatus.Renamed);
        Assert.Equal("Twice(int)", renamed.Signature);
        Assert.True(renamed.TextChanged);
        Assert.NotNull(renamed.TextDiff);
        Assert.InRange(renamed.Similarity, 0.8, 0.99);
    }

    [Fact]
    public void UnrelatedMembersAreAddedAndRemovedNotRenamed()
    {
        var right = Shapes.Replace(
            "public static int Helper(int x)\n        {\n            var y = x * 2;\n            y += 3;\n            return y;\n        }",
            "public static string Describe(string name, bool loud)\n        {\n            return loud ? name.ToUpperInvariant() + \"!\" : name;\n        }");
        var result = MemberMatcher.Compare(Shapes, right);
        Assert.Contains(result.Flatten(), entry => entry.Status == MemberDiffStatus.Removed && entry.Signature == "Helper(int)");
        Assert.Contains(result.Flatten(), entry => entry.Status == MemberDiffStatus.Added && entry.Signature == "Describe(string, bool)");
        Assert.DoesNotContain(result.Flatten(), entry => entry.Status == MemberDiffStatus.Renamed);
    }

    [Fact]
    public void DetectsMethodMovedToAnotherType()
    {
        var helper = "\n        public static int Helper(int x)\n        {\n            var y = x * 2;\n            y += 3;\n            return y;\n        }\n";
        var right = Shapes.Replace(helper, "").Replace("private int _value;\n", "private int _value;\n" + helper);
        Assert.NotEqual(Shapes, right);
        var result = MemberMatcher.Compare(Shapes, right);
        var moved = Assert.Single(result.Flatten(), entry => entry.Status == MemberDiffStatus.Moved);
        Assert.Equal("Demo.Shape.Helper(int)", moved.Left!.QualifiedName);
        Assert.Equal("Demo.Other.Helper(int)", moved.Right!.QualifiedName);
        Assert.False(moved.TextChanged);
        var other = result.Flatten().Single(entry => entry.Signature == "Other");
        Assert.Contains(moved, other.Children);
    }

    [Fact]
    public void DetectsMemberReorderedWithinType()
    {
        var area = "\n        public int Area(int scale)\n        {\n            var total = Width * Width;\n            return total * scale;\n        }\n";
        var right = Shapes.Replace(area, "").Replace("            return y;\n        }\n", "            return y;\n        }\n" + area);
        var result = MemberMatcher.Compare(Shapes, right);
        var moved = Assert.Single(result.Flatten(), entry => entry.Status == MemberDiffStatus.Moved);
        Assert.Equal("Area(int)", moved.Signature);
        var shape = result.Flatten().Single(entry => entry.Signature == "Shape");
        Assert.Equal(["Width", "Helper(int)", "Area(int)"], shape.Children.Select(child => child.Signature));
    }

    [Fact]
    public void MatchesOverloadsByParameterTypes()
    {
        var left = "class C\n{\n    void Print(int value) { Write(value); }\n    void Print(string value) { Write(value); }\n    void Print<T>(T value) { Write(value); }\n}\n";
        var right = "class C\n{\n    void Print(string value) { Write(value + 1); }\n    void Print<U>(U value) { Write(value); }\n    void Print(int value) { Write(value); }\n}\n";
        var result = MemberMatcher.Compare(left, right);
        var entries = result.Flatten().Where(entry => entry.Kind == CSharpMemberKind.Method).ToList();
        Assert.Equal(3, entries.Count);
        Assert.All(entries, entry => Assert.Equal(entry.Left!.Key, entry.Right!.Key));
        Assert.Equal(["Print(string)", "Print<U>(U)"], entries.Where(entry => entry.TextChanged).Select(entry => entry.Signature));
        var changed = entries[0];
        Assert.NotNull(changed.TextDiff);
        Assert.Equal(1, changed.TextDiff!.DifferenceCount);
        var unchanged = entries.Single(entry => entry.Signature == "Print(int)");
        Assert.Equal(MemberDiffStatus.Moved, unchanged.Status);
        Assert.False(unchanged.TextChanged);
        Assert.DoesNotContain(entries, entry => entry.Status is MemberDiffStatus.Added or MemberDiffStatus.Removed);
    }

    [Fact]
    public void ChangedParameterListOfSameNameIsAChange()
    {
        var left = "class C\n{\n    int Sum(int a, int b)\n    {\n        var total = a + b;\n        return total;\n    }\n}\n";
        var right = "class C\n{\n    int Sum(int a, long b)\n    {\n        var total = a + b;\n        return total;\n    }\n}\n";
        var result = MemberMatcher.Compare(left, right);
        var method = Assert.Single(result.Flatten(), entry => entry.Kind == CSharpMemberKind.Method);
        Assert.Equal(MemberDiffStatus.Changed, method.Status);
        Assert.Equal("Sum(int, int)", method.Left!.Signature);
        Assert.Equal("Sum(int, long)", method.Right!.Signature);
    }

    [Fact]
    public void MatchesPartialClassPartsInOrderAndMovesBetweenThem()
    {
        var left = "partial class P\n{\n    void A() { Run(1); Run(2); }\n    void B() { Run(3); Run(4); }\n}\n\npartial class P\n{\n    void C() { Run(5); Run(6); }\n}\n";
        var right = "partial class P\n{\n    void A() { Run(1); Run(2); }\n}\n\npartial class P\n{\n    void C() { Run(5); Run(6); }\n    void B() { Run(3); Run(4); }\n}\n";
        var result = MemberMatcher.Compare(left, right);
        Assert.Equal(2, result.Roots.Count);
        Assert.All(result.Roots, root => Assert.Equal(MemberDiffStatus.Unchanged, root.Status));
        Assert.Equal(["A()"], result.Roots[0].Children.Select(child => child.Signature));
        Assert.Equal(["C()", "B()"], result.Roots[1].Children.Select(child => child.Signature));
        var moved = Assert.Single(result.Flatten(), entry => entry.Status == MemberDiffStatus.Moved);
        Assert.Equal("B()", moved.Signature);
        Assert.Same(result.LeftMembers[0], moved.Left!.Parent);
        Assert.Same(result.RightMembers[1], moved.Right!.Parent);
    }

    [Fact]
    public void ReportsAddedAndRemovedMembersInOrder()
    {
        var left = "class C\n{\n    int a;\n    int b;\n    void Gone() { }\n    int c;\n}\n";
        var right = "class C\n{\n    int a;\n    void Fresh(string s) { System.Console.WriteLine(s); }\n    int b;\n    int c;\n}\n";
        var result = MemberMatcher.Compare(left, right);
        var type = Assert.Single(result.Roots);
        Assert.Equal(
            ["Unchanged a", "Added Fresh(string)", "Unchanged b", "Removed Gone()", "Unchanged c"],
            type.Children.Select(child => child.Status + " " + child.Signature));
        Assert.False(result.AreEqual);
    }

    [Fact]
    public void ReorderedUsingsNormalizeAndClassifyAsUnimportant()
    {
        var left = "using System;\nusing System.IO;\nusing System.Linq;\n\nclass C { }\n";
        var right = "using System.Linq;\nusing System;\nusing System.IO;\n\nclass C { }\n";
        Assert.Equal(
            CSharpNormalizer.Normalize(left, CSharpIgnoreOptions.UsingOrder).Text,
            CSharpNormalizer.Normalize(right, CSharpIgnoreOptions.UsingOrder).Text);
        var normalized = CSharpNormalizer.Normalize(right, CSharpIgnoreOptions.UsingOrder);
        Assert.Equal(["using System;", "using System.IO;", "using System.Linq;", "", "class C { }"], normalized.Lines);
        Assert.Equal([1, 2, 0, 3, 4], normalized.OriginalLines);

        var unimportant = Classify(left, right);
        Assert.NotEmpty(unimportant);
        Assert.All(unimportant, hunk => Assert.Equal(CSharpUnimportantReason.UsingOrder, hunk.Reason));
        Assert.Equal(Compare(left, right).Hunks.Count, unimportant.Count);
        Assert.True(MemberMatcher.Compare(left, right).AreEqual);
    }

    [Fact]
    public void DifferentUsingsAreImportant()
    {
        var left = "using System;\nusing System.IO;\n\nclass C { }\n";
        var right = "using System.IO;\nusing System.Text;\n\nclass C { }\n";
        Assert.Empty(Classify(left, right));
    }

    [Fact]
    public void FormattingOnlyChangeIsIgnoredWhenAsked()
    {
        var left = "class C\n{\n    int Add(int a, int b)\n    {\n        return a + b;\n    }\n}\n";
        var right = "class C {\n    int Add( int a,int b ) {\n\n        return a+b;\n    }\n}\n";
        Assert.Equal(
            string.Join(" ", CSharpNormalizer.Normalize(left, CSharpIgnoreOptions.Formatting).Lines),
            string.Join(" ", CSharpNormalizer.Normalize(right, CSharpIgnoreOptions.Formatting).Lines));

        var unimportant = Classify(left, right);
        Assert.Equal(Compare(left, right).Hunks.Count, unimportant.Count);
        Assert.All(unimportant, hunk => Assert.Equal(CSharpUnimportantReason.Formatting, hunk.Reason));

        Assert.False(MemberMatcher.Compare(left, right).AreEqual);
        var ignored = MemberMatcher.Compare(left, right, new MemberMatchOptions { Ignore = CSharpIgnoreOptions.Formatting });
        Assert.True(ignored.AreEqual);
    }

    [Fact]
    public void FormattingNormalizationKeepsLineStructureAndStrings()
    {
        var text = "class C\n{\n    string s = \"a   b\";   // note\n\n    int  x =  1;\n}\n";
        var normalized = CSharpNormalizer.Normalize(text, CSharpIgnoreOptions.Formatting);
        Assert.Equal(["class C", "{", "string s = \"a   b\" ; // note", "int x = 1 ;", "}"], normalized.Lines);
        Assert.Equal([0, 1, 2, 4, 5], normalized.OriginalLines);
        Assert.Equal(-1, normalized.ToNormalizedLine(3));
        Assert.Equal(3, normalized.ToNormalizedLine(4));
        Assert.Equal(4, normalized.ToOriginalLine(3));
        Assert.Equal(5, normalized.ToDocument().LineCount);
    }

    [Fact]
    public void CommentsOnlyChangeIsIgnoredWhenAsked()
    {
        var left = "class C\n{\n    // Adds two numbers.\n    int Add(int a, int b) => a + b; // fast\n    /* block\n       comment */\n    int Zero() => 0;\n}\n";
        var right = "class C\n{\n    /// <summary>Sum.</summary>\n    int Add(int a, int b) => a + b;\n    int Zero() => 0;\n}\n";
        var normalizedLeft = CSharpNormalizer.Normalize(left, CSharpIgnoreOptions.Comments);
        var normalizedRight = CSharpNormalizer.Normalize(right, CSharpIgnoreOptions.Comments);
        Assert.Equal(normalizedLeft.Text, normalizedRight.Text);
        Assert.Equal("    int Add(int a, int b) => a + b;", normalizedLeft.Lines[2]);
        Assert.Equal([0, 1, 3, 6, 7], normalizedLeft.OriginalLines);

        var unimportant = Classify(left, right);
        Assert.Equal(Compare(left, right).Hunks.Count, unimportant.Count);
        Assert.All(unimportant, hunk => Assert.Equal(CSharpUnimportantReason.Comments, hunk.Reason));

        var plain = MemberMatcher.Compare(left, right);
        Assert.Contains(plain.Flatten(), entry => entry.Status == MemberDiffStatus.Changed && entry.Signature == "Add(int, int)");
        Assert.True(MemberMatcher.Compare(left, right, new MemberMatchOptions { Ignore = CSharpIgnoreOptions.Comments }).AreEqual);
    }

    [Fact]
    public void RegionLinesAreIgnoredWhenAsked()
    {
        var left = "class C\n{\n    #region Fields\n    int _a;\n    #endregion\n}\n";
        var right = "class C\n{\n    int _a;\n}\n";
        Assert.Equal(
            CSharpNormalizer.Normalize(left, CSharpIgnoreOptions.Regions).Text,
            CSharpNormalizer.Normalize(right, CSharpIgnoreOptions.Regions).Text);
        var unimportant = Classify(left, right);
        Assert.Equal(2, unimportant.Count);
        Assert.All(unimportant, hunk => Assert.Equal(CSharpUnimportantReason.Regions, hunk.Reason));
        Assert.Equal("only #region lines differ", unimportant[0].Description);
    }

    [Fact]
    public void RealChangesAreNotClassifiedAsUnimportant()
    {
        var left = "class C\n{\n    int Add(int a, int b) => a + b;\n    // same\n}\n";
        var right = "class C\n{\n    int Add(int a, int b) => a - b;\n    // other\n}\n";
        var result = Compare(left, right);
        var unimportant = Classify(left, right);
        Assert.NotEmpty(result.Hunks);
        Assert.Empty(unimportant);
    }

    [Fact]
    public void ClassifierHandlesWindowsLineEndings()
    {
        var left = "class C\r\n{\r\n    int  x = 1;\r\n}\r\n";
        var right = "class C\r\n{\r\n    int x = 1;\r\n}\r\n";
        var unimportant = Classify(left, right);
        var hunk = Assert.Single(unimportant);
        Assert.Equal(2, hunk.Hunk.LeftStart);
        Assert.Equal(CSharpUnimportantReason.Formatting, hunk.Reason);
    }

    [Fact]
    public void SyntaxErrorsAreComparedBestEffort()
    {
        var left = "class C\n{\n    void A() { Run(1); }\n    void B( { Run(2) }\n    int _x;\n}\n";
        var right = "class C\n{\n    void A() { Run(1); }\n    void B() { Run(2); }\n    int _x;\n    int _y;\n";
        var result = MemberMatcher.Compare(left, right);
        Assert.True(result.LeftHasErrors);
        Assert.True(result.RightHasErrors);
        var type = Assert.Single(result.Roots);
        Assert.Contains(type.Children, child => child.Signature == "A()" && child.Status == MemberDiffStatus.Unchanged);
        Assert.Contains(type.Children, child => child.Signature == "_y" && child.Status == MemberDiffStatus.Added);
        Assert.NotEmpty(CSharpNormalizer.Normalize(left, CSharpIgnoreOptions.All).Lines);
        Assert.NotNull(Classify(left, right));
    }

    [Fact]
    public void EmptyAndTopLevelTextIsHandled()
    {
        Assert.True(MemberMatcher.Compare("", "").AreEqual);
        Assert.Empty(CSharpNormalizer.Normalize("", CSharpIgnoreOptions.All).Lines);
        var result = MemberMatcher.Compare("", "class C { }");
        Assert.Equal(MemberDiffStatus.Added, Assert.Single(result.Roots).Status);
    }

    [Fact]
    public void CompareHonoursCancellation()
    {
        using var source = new CancellationTokenSource();
        source.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => MemberMatcher.Compare(Shapes, Shapes, cancellationToken: source.Token));
        Assert.ThrowsAny<OperationCanceledException>(() => CSharpNormalizer.Normalize(Shapes, CSharpIgnoreOptions.All, source.Token));
    }

    [Fact]
    public void TenThousandLineFileComparesInUnderASecond()
    {
        var left = LargeFile(1_250, changed: -1);
        var right = LargeFile(1_250, changed: 600);
        Assert.True(left.Count(c => c == '\n') >= 10_000);

        // Warm up so that the measurement does not include loading and compiling the parser.
        MemberMatcher.Compare(Shapes, Shapes, new MemberMatchOptions { Ignore = CSharpIgnoreOptions.All });
        Classify(Shapes, Shapes);

        var watch = Stopwatch.StartNew();
        var result = MemberMatcher.Compare(left, right, new MemberMatchOptions { Ignore = CSharpIgnoreOptions.All });
        watch.Stop();
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(1), $"Member compare took {watch.Elapsed}.");
        var changed = Assert.Single(result.Flatten(), entry => entry.Status != MemberDiffStatus.Unchanged);
        Assert.Equal("Method600(int)", changed.Signature);

        watch.Restart();
        var normalized = CSharpNormalizer.Normalize(left, CSharpIgnoreOptions.All);
        watch.Stop();
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(1), $"Normalize took {watch.Elapsed}.");
        Assert.Equal(6 + 1_250 * 6 + 2, normalized.Lines.Count);
    }

    [Fact]
    public void CliPrintsTheMemberTreeWithExitCodes()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(directory);
        var originalOut = Console.Out;
        try
        {
            var left = Path.Combine(directory, "left.cs");
            var right = Path.Combine(directory, "right.cs");
            File.WriteAllText(left, Shapes);
            File.WriteAllText(right, Shapes.Replace("public int Area(int scale)", "public int ComputeArea(int scale)"));
            var output = new StringWriter();
            Console.SetOut(output);
            Assert.Equal(1, Program.Main(["semantic", left, right]));
            var text = output.ToString();
            Assert.Contains("Unchanged  namespace Demo", text);
            Assert.Contains("    Renamed    method ComputeArea(int) (was Area(int))", text);

            File.WriteAllText(right, "  // comment\n" + Shapes.Replace("    ", "  "));
            Assert.Equal(1, Program.Main(["semantic", left, right]));
            Assert.Equal(0, Program.Main(["semantic", left, right, "--ignore", "format,comments"]));
            Assert.Equal(0, Program.Main(["semantic", left, left]));
            Assert.Equal(2, Program.Main(["semantic", left, right, "--ignore", "bogus"]));
            Assert.Equal(2, Program.Main(["semantic", left]));
            Assert.Equal(2, Program.Main(["semantic", left, Path.Combine(directory, "missing.cs")]));
        }
        finally
        {
            Console.SetOut(originalOut);
            Directory.Delete(directory, true);
        }
    }

    private static TextDiffResult Compare(string left, string right) =>
        TextComparer.Compare(TextDocument.Parse(left), TextDocument.Parse(right));

    private static IReadOnlyList<UnimportantHunk> Classify(string left, string right) =>
        CSharpDiffClassifier.Classify(Compare(left, right));

    /// <summary>A file of about eight lines per method; one method gets a different body when <paramref name="changed"/> is set.</summary>
    private static string LargeFile(int methods, int changed)
    {
        var builder = new StringBuilder();
        builder.Append("using System;\nusing System.Text;\n\nnamespace Large\n{\n    public sealed class Generated\n    {\n");
        for (var i = 0; i < methods; i++)
        {
            var step = i == changed ? i + 1 : i;
            builder.Append($"        // Method {i}.\n        public int Method{i}(int value)\n        {{\n            var result = value * {step};\n            result += {i % 7};\n            return result;\n        }}\n\n");
        }

        builder.Append("    }\n}\n");
        return builder.ToString();
    }
}
