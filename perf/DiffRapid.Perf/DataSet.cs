using System;
using System.IO;
using System.Text;
using System.Threading;

namespace DiffRapid.Perf;

internal static class DataSet
{
    public const int LineCount = 200000;
    public const int FolderCount = 20000;
    public const int FilesPerFolder = 10;
    private const string Version = "diffrapid-perf-v1";

    public static string Prepare(string directory, bool includeFolders, CancellationToken cancellationToken)
    {
        var root = Path.Combine(directory, Version);
        var marker = Path.Combine(root, "complete.txt");
        if (File.Exists(marker))
        {
            return root;
        }

        Directory.CreateDirectory(root);
        var textMarker = Path.Combine(root, "text-complete.txt");
        if (!File.Exists(textMarker))
        {
            WriteText(root, cancellationToken);
            File.WriteAllText(textMarker, Version);
        }

        if (includeFolders)
        {
            WriteFolders(root, cancellationToken);
            File.WriteAllText(marker, Version);
        }

        return root;
    }

    private static void WriteText(string root, CancellationToken cancellationToken)
    {
        var random = new Random(2531);
        using var left = new StreamWriter(Path.Combine(root, "left.txt"), false, new UTF8Encoding(false));
        using var scattered = new StreamWriter(Path.Combine(root, "scattered.txt"), false, new UTF8Encoding(false));
        using var different = new StreamWriter(Path.Combine(root, "different.txt"), false, new UTF8Encoding(false));
        for (var index = 0; index < LineCount; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var payload = random.NextInt64().ToString("x16");
            var line = $"{index:D6} original {payload} text for comparison 12345";
            var changed = index % 4000 >= 1900 && index % 4000 < 1980;
            left.Write(line + "\n");
            scattered.Write((changed ? line.Replace("original", "modified", StringComparison.Ordinal) : line) + "\n");
            different.Write($"{index:D6} replaced {payload} other input payload 67890\n");
        }
    }

    private static void WriteFolders(string root, CancellationToken cancellationToken)
    {
        var random = new Random(2532);
        var timestamp = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        for (var folder = 0; folder < FolderCount; folder++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var name = folder.ToString("D5");
            var left = Directory.CreateDirectory(Path.Combine(root, "tree-left", name)).FullName;
            var right = Directory.CreateDirectory(Path.Combine(root, "tree-right", name)).FullName;
            for (var file = 0; file < FilesPerFolder; file++)
            {
                var bytes = new byte[64];
                random.NextBytes(bytes);
                var fileName = file.ToString("D2") + ".bin";
                var leftPath = Path.Combine(left, fileName);
                var rightPath = Path.Combine(right, fileName);
                File.WriteAllBytes(leftPath, bytes);
                var changed = ((folder * FilesPerFolder) + file) % 100 == 0;
                if (changed)
                {
                    bytes[0] ^= 127;
                }

                File.WriteAllBytes(rightPath, bytes);
                File.SetLastWriteTimeUtc(leftPath, timestamp);
                File.SetLastWriteTimeUtc(rightPath, changed ? timestamp.AddSeconds(5) : timestamp);
            }

            if (folder % 2000 == 0)
            {
                Console.WriteLine($"Generated {folder:N0}/{FolderCount:N0} folder pairs.");
            }
        }
    }
}
