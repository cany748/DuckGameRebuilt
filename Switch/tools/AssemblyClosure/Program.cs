// Prints the transitive closure of assembly references starting from the given roots.
// usage: AssemblyClosure <search-dir;search-dir;...> <root.dll>...
// Search directories are tried in order, mirroring the runtime's assembly_dir precedence.
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

var searchDirs = args[0].Split(';', StringSplitOptions.RemoveEmptyEntries);
var pending = new Queue<string>(args.Skip(1).Select(Path.GetFullPath));
var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
var missing = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);

string? Resolve(string name)
{
    foreach (var dir in searchDirs)
        foreach (var ext in new[] { ".dll", ".exe" })
        {
            var path = Path.Combine(dir, name + ext);
            if (File.Exists(path))
                return Path.GetFullPath(path);
        }
    return null;
}

while (pending.Count > 0)
{
    var path = pending.Dequeue();
    if (!seen.Add(Path.GetFileNameWithoutExtension(path)))
        continue;
    Console.WriteLine(path);

    using var stream = File.OpenRead(path);
    using var pe = new PEReader(stream);
    if (!pe.HasMetadata)
        continue;
    var md = pe.GetMetadataReader();
    foreach (var handle in md.AssemblyReferences)
    {
        var name = md.GetString(md.GetAssemblyReference(handle).Name);
        if (seen.Contains(name))
            continue;
        var resolved = Resolve(name);
        if (resolved == null)
            missing.Add(name);
        else
            pending.Enqueue(resolved);
    }
}

foreach (var name in missing)
    Console.Error.WriteLine($"unresolved: {name}");
