using System.IO.Compression;
using System.Text;
using DiffRapid.Cli;
using DiffRapid.Core.Assemblies;
using DiffRapid.Core.Patching;
using DiffRapid.Core.Tests.ApiFixtures;

namespace DiffRapid.Core.Tests.ApiFixtures
{
    public interface IVariant<in TIn, out TOut>
    {
        TOut Convert(TIn value);
    }

    public enum Shade : byte
    {
        Light = 1,
        Dark = 2,
    }

    public delegate TResult Producer<out TResult>(int count) where TResult : class;

    public struct Point
    {
        public int X;

        public static readonly Point Origin;
    }

    public abstract class Sample : IDisposable
    {
        public const string Greeting = "hi \"you\"";

        protected Sample()
        {
        }

        public event EventHandler? Changed;

        public string? Title { get; set; }

        public string Name { get; protected set; } = string.Empty;

        public int Id { get; init; }

        public List<string?> Items { get; } = [];

        public int? Count { get; set; }

        public string this[int index] => index.ToString();

        public abstract void Run();

        public virtual T? Find<T>(IEnumerable<T> source, int skip = 2)
            where T : class, new() => null;

        public bool TryGet(string key, out int value, ref int counter, in Point origin, params object[] rest)
        {
            value = rest.Length + counter + origin.X + key.Length;
            Changed?.Invoke(this, EventArgs.Empty);
            return true;
        }

        public void Dispose()
        {
            GC.SuppressFinalize(this);
        }

        public static Sample operator +(Sample left, Sample right) => left;

        protected internal void Shared()
        {
        }

        internal void Hidden()
        {
        }

        public class Nested
        {
        }

        private class Secret
        {
        }
    }

    public static class SampleExtensions
    {
        public static (int Low, string High) Split(this Sample sample, char[]? separators = null) => (sample.Id, separators?.Length.ToString() ?? string.Empty);
    }

    internal sealed class InternalOnly
    {
    }
}

namespace DiffRapid.Core.Tests
{
    public sealed class AssemblyApiTests : IDisposable
    {
        private const string FixtureNamespace = "DiffRapid.Core.Tests.ApiFixtures";

        private readonly string _root = Path.Combine(Path.GetTempPath(), "diffrapid-api-" + Guid.NewGuid());

        public AssemblyApiTests()
        {
            Directory.CreateDirectory(_root);
        }

        public void Dispose()
        {
            Directory.Delete(_root, true);
        }

        private static string CorePath => typeof(Patch).Assembly.Location;

        private static string TestPath => typeof(AssemblyApiTests).Assembly.Location;

        private string At(string name) => Path.Combine(_root, name);

        private static IReadOnlyList<string> Members(ApiSurface surface, string typeName) => surface.Entries
            .Where(entry => entry.Kind == ApiEntryKind.Member && entry.TypeName == FixtureNamespace + "." + typeName)
            .Select(entry => entry.Declaration)
            .ToList();

        [Fact]
        public void ReadsAssemblyInformation()
        {
            var info = ApiReader.Read(CorePath, TestContext.Current.CancellationToken).Info;
            Assert.Equal("DiffRapid.Core", info.Name);
            Assert.Equal(".NETStandard,Version=v2.0", info.TargetFramework);
            Assert.Equal("AnyCPU", info.Architecture);
            Assert.Null(info.PublicKeyToken);
            Assert.Contains(info.References, reference => reference.StartsWith("netstandard, Version=2.0.0.0, PublicKeyToken=cc7b13ffcd2ddd51", StringComparison.Ordinal));
            Assert.Contains(info.ToProperties(), property => property.Key == "Version" && property.Value == info.Version);
        }

        [Fact]
        public void ReadsPublicTypesAndMembersOfCore()
        {
            var surface = ApiReader.Read(CorePath, TestContext.Current.CancellationToken);
            var lines = surface.ToLines();
            Assert.Contains("namespace DiffRapid.Core.Patching", lines);
            Assert.Contains("    public static DiffRapid.Core.Patching.Patch Parse(string text)", lines);
            Assert.Contains(surface.Entries, entry => entry.Kind == ApiEntryKind.Type && entry.Declaration.StartsWith("public sealed class DiffRapid.Core.Patching.Patch : System.IEquatable<", StringComparison.Ordinal));
            Assert.DoesNotContain(surface.Entries, entry => entry.TypeName.Contains("ApiSurfaceBuilder", StringComparison.Ordinal));
            Assert.DoesNotContain(surface.Entries, entry => entry.TypeName.Contains("TypeShape", StringComparison.Ordinal));
        }

        [Fact]
        public void OutputIsSortedAndStable()
        {
            var first = ApiReader.Read(CorePath, TestContext.Current.CancellationToken);
            var second = ApiReader.Read(CorePath, TestContext.Current.CancellationToken);
            Assert.Equal(first.ToText(), second.ToText());
            Assert.Equal(first.Entries.OrderBy(entry => entry, ApiEntry.Order), first.Entries);
        }

        [Fact]
        public void WritesTypeDeclarations()
        {
            var types = ApiReader.Read(TestPath, TestContext.Current.CancellationToken).Entries
                .Where(entry => entry.Kind == ApiEntryKind.Type && entry.Namespace == FixtureNamespace)
                .Select(entry => entry.Declaration)
                .ToList();
            Assert.Contains($"public interface {FixtureNamespace}.IVariant<in TIn, out TOut>", types);
            Assert.Contains($"public enum {FixtureNamespace}.Shade : byte", types);
            Assert.Contains($"public delegate TResult {FixtureNamespace}.Producer<out TResult>(int count) where TResult : class", types);
            Assert.Contains($"public struct {FixtureNamespace}.Point", types);
            Assert.Contains($"public abstract class {FixtureNamespace}.Sample : System.IDisposable", types);
            Assert.Contains($"public class {FixtureNamespace}.Sample.Nested", types);
            Assert.Contains($"public static class {FixtureNamespace}.SampleExtensions", types);
            Assert.DoesNotContain(types, type => type.Contains("Secret", StringComparison.Ordinal) || type.Contains("InternalOnly", StringComparison.Ordinal));
        }

        [Fact]
        public void WritesMemberSignaturesWithNullableAnnotations()
        {
            var surface = ApiReader.Read(TestPath, TestContext.Current.CancellationToken);
            var members = Members(surface, "Sample");
            Assert.Contains("public const string Greeting = \"hi \\\"you\\\"\"", members);
            Assert.Contains("protected Sample()", members);
            Assert.Contains("public event System.EventHandler? Changed", members);
            Assert.Contains("public string? Title { get; set; }", members);
            Assert.Contains("public string Name { get; protected set; }", members);
            Assert.Contains("public int Id { get; init; }", members);
            Assert.Contains("public System.Collections.Generic.List<string?> Items { get; }", members);
            Assert.Contains("public int? Count { get; set; }", members);
            Assert.Contains("public string this[int index] { get; }", members);
            Assert.Contains("public abstract void Run()", members);
            Assert.Contains("public virtual T? Find<T>(System.Collections.Generic.IEnumerable<T> source, int skip = 2) where T : class, new()", members);
            Assert.Contains($"public bool TryGet(string key, out int value, ref int counter, in {FixtureNamespace}.Point origin, params object[] rest)", members);
            Assert.Contains("public void Dispose()", members);
            Assert.Contains($"public static {FixtureNamespace}.Sample op_Addition({FixtureNamespace}.Sample left, {FixtureNamespace}.Sample right)", members);
            Assert.Contains("protected void Shared()", members);
            Assert.DoesNotContain(members, member => member.Contains("Hidden", StringComparison.Ordinal));

            Assert.Equal(["Dark = 2", "Light = 1"], Members(surface, "Shade"));
            Assert.Contains("public int X", Members(surface, "Point"));
            Assert.Contains($"public static readonly {FixtureNamespace}.Point Origin", Members(surface, "Point"));
            Assert.Contains($"public static (int, string) Split(this {FixtureNamespace}.Sample sample, char[]? separators = null)", Members(surface, "SampleExtensions"));
            Assert.Contains("public TOut Convert(TIn value)", Members(surface, "IVariant<TIn, TOut>"));
        }

        [Fact]
        public void IdenticalSurfacesHaveNoChanges()
        {
            var surface = ApiReader.Read(CorePath, TestContext.Current.CancellationToken);
            var comparison = AssemblyComparer.Compare(surface, ApiReader.Read(CorePath, TestContext.Current.CancellationToken));
            Assert.True(comparison.IsIdentical);
            Assert.False(comparison.HasBreakingChanges);
        }

        [Fact]
        public void ClassifiesRemovedChangedAndAddedDeclarations()
        {
            var info = new AssemblyInfo("Lib", "1.0.0.0", null, null, null, null, "AnyCPU", []);
            var type = Type("Ns.Widget", "public class Ns.Widget");
            var left = new ApiSurface(info, [
                type,
                Member("Ns.Widget", "Run", "public void Run(int count)"),
                Member("Ns.Widget", "Stop", "public void Stop()"),
                Type("Ns.Gone", "public class Ns.Gone"),
                Member("Ns.Gone", "Go", "public void Go()"),
            ]);
            var right = new ApiSurface(info with { Version = "2.0.0.0" }, [
                type,
                Member("Ns.Widget", "Run", "public void Run(long count)"),
                Member("Ns.Widget", "Pause", "public void Pause()"),
                Type("Ns.Fresh", "public class Ns.Fresh"),
                Member("Ns.Fresh", "Go", "public void Go()"),
            ]);

            var comparison = AssemblyComparer.Compare(left, right);

            Assert.Equal(
                [
                    "+ public class Ns.Fresh",
                    "- public class Ns.Gone",
                    "+ Ns.Widget: public void Pause()",
                    "~ Ns.Widget: public void Run(int count)  =>  public void Run(long count)",
                    "- Ns.Widget: public void Stop()",
                ],
                comparison.Changes.Select(change => change.ToString()));
            Assert.Equal(3, comparison.Changes.Count(change => change.IsBreaking));
            Assert.Equal([new ApiInfoChange("Version", "1.0.0.0", "2.0.0.0", false)], comparison.InfoChanges);
            Assert.True(comparison.HasBreakingChanges);

            var additive = AssemblyComparer.Compare(left, new ApiSurface(info, left.Entries.Append(Member("Ns.Widget", "Pause", "public void Pause()"))));
            Assert.False(additive.HasBreakingChanges);
            Assert.Equal(ApiChangeKind.Added, Assert.Single(additive.Changes).Kind);
        }

        [Fact]
        public void PublicKeyTokenChangeIsBreaking()
        {
            var info = new AssemblyInfo("Lib", "1.0.0.0", null, null, null, null, "AnyCPU", []);
            var comparison = AssemblyComparer.Compare(new ApiSurface(info, []), new ApiSurface(info with { PublicKeyToken = "0123456789abcdef" }, []));
            Assert.True(Assert.Single(comparison.InfoChanges).IsBreaking);
        }

        [Fact]
        public void ReadsNuGetPackage()
        {
            CreatePackage("left.nupkg", "1.0.0", ("netstandard2.0", CorePath), ("net10.0", TestPath));

            var package = NuGetPackageReader.Read(At("left.nupkg"), TestContext.Current.CancellationToken);

            Assert.Equal("Example.Package", package.Id);
            Assert.Equal("1.0.0", package.Version);
            Assert.Equal([".NETStandard2.0", "net10.0"], package.DependencyGroups.Select(group => group.TargetFramework));
            var dependency = Assert.Single(package.DependencyGroups[0].Dependencies);
            Assert.Equal(new NuGetDependency("System.Reflection.Metadata", "9.0.0"), dependency);
            Assert.Empty(package.DependencyGroups[1].Dependencies);
            Assert.Equal(["lib/net10.0/DiffRapid.Core.Tests.dll", "lib/netstandard2.0/DiffRapid.Core.dll"], package.Libraries.Select(library => library.Path));
            Assert.Equal("netstandard2.0", package.Libraries[1].TargetFramework);
            Assert.Contains("    public static DiffRapid.Core.Patching.Patch Parse(string text)", package.Libraries[1].Api.ToLines());
        }

        [Fact]
        public void ComparesPackagesByLibrary()
        {
            CreatePackage("left.nupkg", "1.0.0", ("netstandard2.0", CorePath), ("net10.0", TestPath));
            CreatePackage("right.nupkg", "1.1.0", ("netstandard2.0", CorePath), ("net11.0", TestPath));

            var comparison = AssemblyComparer.Compare(
                NuGetPackageReader.Read(At("left.nupkg"), TestContext.Current.CancellationToken),
                NuGetPackageReader.Read(At("right.nupkg"), TestContext.Current.CancellationToken));

            Assert.True(comparison.HasBreakingChanges);
            Assert.Contains(new ApiInfoChange("Package version", "1.0.0", "1.1.0", false), comparison.InfoChanges);
            Assert.Contains(new ApiInfoChange("Library lib/net10.0/DiffRapid.Core.Tests.dll", "present", null, true), comparison.InfoChanges);
            Assert.Contains(new ApiInfoChange("Library lib/net11.0/DiffRapid.Core.Tests.dll", null, "present", false), comparison.InfoChanges);
            var library = Assert.Single(comparison.Libraries);
            Assert.Equal("lib/netstandard2.0/DiffRapid.Core.dll", library.Path);
            Assert.True(library.Comparison.IsIdentical);
        }

        [Fact]
        public void RejectsFilesThatAreNotAssembliesOrPackages()
        {
            File.WriteAllText(At("text.dll"), "not an assembly");
            File.WriteAllText(At("text.nupkg"), "not a zip");
            Assert.Throws<InvalidDataException>(() => ApiReader.Read(At("text.dll"), TestContext.Current.CancellationToken));
            Assert.Throws<InvalidDataException>(() => NuGetPackageReader.Read(At("text.nupkg"), TestContext.Current.CancellationToken));
        }

        [Fact]
        public void HonoursCancellation()
        {
            using var source = new CancellationTokenSource();
            source.Cancel();
            Assert.Throws<OperationCanceledException>(() => ApiReader.Read(CorePath, source.Token));
        }

        [Fact]
        public void CliReportsApiChanges()
        {
            Assert.Equal(0, Program.Main(["api", CorePath, CorePath]));
            Assert.Equal(1, Program.Main(["api", CorePath, TestPath]));
            Assert.Equal(0, Program.Main(["api", CorePath]));
            CreatePackage("left.nupkg", "1.0.0", ("netstandard2.0", CorePath));
            CreatePackage("right.nupkg", "1.0.0", ("netstandard2.0", CorePath), ("net10.0", TestPath));
            Assert.Equal(1, Program.Main(["api", At("left.nupkg"), At("right.nupkg")]));
            Assert.Equal(0, Program.Main(["api", At("left.nupkg"), At("right.nupkg"), "--breaking-only"]));
            Assert.Equal(1, Program.Main(["api", At("right.nupkg"), At("left.nupkg"), "--breaking-only"]));
        }

        [Fact]
        public void CliRejectsMixedOrInvalidInputs()
        {
            CreatePackage("left.nupkg", "1.0.0", ("netstandard2.0", CorePath));
            File.WriteAllText(At("text.dll"), "not an assembly");
            Assert.Equal(2, Program.Main(["api", CorePath, At("left.nupkg")]));
            Assert.Equal(2, Program.Main(["api", At("text.dll"), CorePath]));
            Assert.Equal(2, Program.Main(["api"]));
        }

        private static ApiEntry Type(string name, string declaration) =>
            new(ApiEntryKind.Type, "Ns", name, string.Empty, declaration);

        private static ApiEntry Member(string type, string name, string declaration) =>
            new(ApiEntryKind.Member, "Ns", type, name, declaration);

        private void CreatePackage(string name, string version, params (string Framework, string Assembly)[] libraries)
        {
            using var archive = ZipFile.Open(At(name), ZipArchiveMode.Create);
            var groups = string.Concat(libraries.Select(library => library.Framework == "netstandard2.0"
                ? "<group targetFramework=\".NETStandard2.0\"><dependency id=\"System.Reflection.Metadata\" version=\"9.0.0\" /></group>"
                : $"<group targetFramework=\"{library.Framework}\" />"));
            var nuspec = $"""
                <?xml version="1.0" encoding="utf-8"?>
                <package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd">
                  <metadata>
                    <id>Example.Package</id>
                    <version>{version}</version>
                    <dependencies>{groups}</dependencies>
                  </metadata>
                </package>
                """;
            using (var writer = new StreamWriter(archive.CreateEntry("Example.Package.nuspec").Open(), new UTF8Encoding(false)))
            {
                writer.Write(nuspec);
            }

            foreach (var (framework, assembly) in libraries)
            {
                archive.CreateEntryFromFile(assembly, $"lib/{framework}/{Path.GetFileName(assembly)}");
            }
        }
    }
}
