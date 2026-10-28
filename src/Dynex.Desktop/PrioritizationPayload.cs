using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

namespace Dynex.Desktop;

/// <summary>The elevated installer accepts only the exact helper bits embedded by the package build.</summary>
internal sealed class PrioritizationPayload
{
    internal sealed record Entry(string Name, long Length, string Sha256);
    internal sealed record Manifest(int Schema, Entry[] Files);
    internal const string ResourceName = "Dynex.Prioritization.Payload.json";
    internal const string ReceiptName = "payload-manifest.json";
    internal Manifest Contents { get; }
    internal string Hash { get; }
    private readonly byte[] bytes;

    internal PrioritizationPayload(byte[] manifest)
    {
        if (manifest.Length is 0 or > 1048576) throw new InvalidDataException("Der Netzwerkhelfer-Nachweis ist ungültig.");
        bytes = manifest.ToArray();
        Contents = JsonSerializer.Deserialize<Manifest>(bytes, new JsonSerializerOptions
        { UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow }) ?? throw new InvalidDataException("Der Netzwerkhelfer-Nachweis fehlt.");
        if (Contents.Schema != 1 || Contents.Files is not { Length: > 0 and <= 2048 } ||
            Contents.Files.Any(file => file is null || !ValidName(file.Name) || file.Length is < 0 or > 268435456 || !ValidHash(file.Sha256)) ||
            Contents.Files.Sum(file => file.Length) is <= 0 or > 536870912 ||
            Contents.Files.Select(file => file.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != Contents.Files.Length ||
            !Contents.Files.Any(file => file.Name == "Dynex.Shaper.exe") || !Contents.Files.Any(file => file.Name == "Dynex.Shaper.dll"))
            throw new InvalidDataException("Der Netzwerkhelfer-Nachweis enthält nicht unterstützte Dateien.");
        Hash = Convert.ToHexString(SHA256.HashData(bytes));
    }

    internal static PrioritizationPayload Load() => new(ReadResource());

    private static byte[] ReadResource()
    {
        using var resource = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName)
            ?? throw new InvalidDataException("Der geprüfte Netzwerkhelfer fehlt in diesem Build. Bitte das vollständige DYNEX-Setup verwenden.");
        using var result = new MemoryStream();
        resource.CopyTo(result);
        return result.ToArray();
    }

    internal static bool ValidHash(string? value) => value is { Length: 64 } && value.All(c => c is >= '0' and <= '9' or >= 'A' and <= 'F');

    internal static bool ValidName(string? name) => name is { Length: > 0 and <= 240 } &&
        !name.StartsWith('/') && !name.EndsWith('/') && !name.Contains('\\') && !name.Contains(':') &&
        name.Split('/').All(part => part is not ("" or "." or "..") && !part.EndsWith('.') && !part.EndsWith(' ') &&
            part.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_')) &&
        !string.Equals(name, ReceiptName, StringComparison.OrdinalIgnoreCase);

    internal static void RejectReparsePoints(string path)
    {
        for (var item = new DirectoryInfo(Path.GetFullPath(path)); item is not null; item = item.Parent)
            if (item.Exists && (item.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Der Netzwerkhelfer-Pfad enthält eine Verzeichnisverknüpfung.");
    }

    // Locks every input until copying finishes. A mutable installation folder cannot
    // substitute a DLL/config after its digest was checked by the elevated setup.
    internal void CopyVerified(string source, string destination)
    {
        RejectReparsePoints(source);
        RejectReparsePoints(destination);
        var locked = new List<(Entry Entry, FileStream Stream)>();
        try
        {
            VerifyNames(source, allowReceipt: false);
            foreach (var entry in Contents.Files)
            {
                var path = Path.Combine(source, entry.Name.Replace('/', Path.DirectorySeparatorChar));
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("Eine Netzwerkhelfer-Datei ist eine Verknüpfung.");
                var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                locked.Add((entry, input));
                Verify(input, entry);
            }
            foreach (var (entry, input) in locked)
            {
                var path = Path.Combine(destination, entry.Name.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                input.Position = 0;
                input.CopyTo(output);
                output.Flush(true);
            }
            using var receipt = new FileStream(Path.Combine(destination, ReceiptName), FileMode.CreateNew, FileAccess.Write, FileShare.None);
            receipt.Write(bytes);
            receipt.Flush(true);
        }
        finally { foreach (var (_, input) in locked) input.Dispose(); }
    }

    internal void VerifyDirectory(string directory, bool allowReceipt = true)
    {
        RejectReparsePoints(directory);
        VerifyNames(directory, allowReceipt);
        foreach (var entry in Contents.Files)
        {
            var path = Path.Combine(directory, entry.Name.Replace('/', Path.DirectorySeparatorChar));
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Verknüpfte Netzwerkhelfer-Datei.");
            using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            Verify(input, entry);
        }
        if (allowReceipt && !File.ReadAllBytes(Path.Combine(directory, ReceiptName)).AsSpan().SequenceEqual(bytes))
            throw new InvalidDataException("Der geschützte Netzwerkhelfer-Nachweis wurde verändert.");
    }

    private static void Verify(FileStream stream, Entry entry)
    {
        if (stream.Length != entry.Length || Convert.ToHexString(SHA256.HashData(stream)) != entry.Sha256)
            throw new InvalidDataException("Eine Netzwerkhelfer-Datei stimmt nicht mit dem geprüften DYNEX-Paket überein: " + entry.Name);
    }

    private void VerifyNames(string directory, bool allowReceipt)
    {
        var expected = Contents.Files.Select(file => file.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (allowReceipt) expected.Add(ReceiptName);
        var actual = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in EnumerateFiles(directory))
            actual.Add(Path.GetRelativePath(directory, file).Replace('\\', '/'));
        if (!actual.SetEquals(expected)) throw new InvalidDataException("Der Netzwerkhelfer enthält fehlende oder zusätzliche Dateien.");
    }

    private static IEnumerable<string> EnumerateFiles(string directory)
    {
        foreach (var path in Directory.EnumerateFileSystemEntries(directory))
        {
            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Verknüpfung im Netzwerkhelfer-Paket.");
            if ((attributes & FileAttributes.Directory) == 0) yield return path;
            else foreach (var child in EnumerateFiles(path)) yield return child;
        }
    }
}
