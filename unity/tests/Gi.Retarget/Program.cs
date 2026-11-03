using Mono.Cecil;

var lowered = new Dictionary<string, Version>
{
    ["System.Runtime"] = new(4, 1, 2, 0),
    ["System.Runtime.InteropServices"] = new(4, 1, 2, 0),
    ["System.Runtime.Intrinsics"] = new(4, 0, 0, 0),
    ["System.Memory"] = new(4, 0, 2, 0),
    ["System.Threading"] = new(4, 0, 11, 0),
};

if (args.Length == 2 && args[0] == "--check-shims") return CheckShims(args[1], lowered);
if (args.Length != 3)
{
    Console.Error.WriteLine("usage: Gi.Retarget <netstandard.dll> <input.dll> <output.dll>");
    Console.Error.WriteLine("   or: Gi.Retarget --check-shims <shims-dir>");
    return 2;
}

var netstandard = ModuleDefinition.ReadModule(args[0]);
var defined = new HashSet<string>(netstandard.Types.Select(t => t.FullName));
foreach (var exported in netstandard.ExportedTypes) defined.Add(exported.FullName);

var module = ModuleDefinition.ReadModule(args[1], new ReaderParameters { InMemory = true });
var target = new AssemblyNameReference("netstandard", new Version(2, 1, 0, 0))
{
    PublicKeyToken = new byte[] { 0xcc, 0x7b, 0x13, 0xff, 0xcd, 0x2d, 0xdd, 0x51 },
};
module.AssemblyReferences.Add(target);

var moved = 0;
var kept = new SortedSet<string>(StringComparer.Ordinal);
foreach (var reference in module.GetTypeReferences())
{
    if (reference.DeclaringType != null || reference.Scope is not AssemblyNameReference scope || scope.Name == "netstandard") continue;
    if (defined.Contains(reference.FullName))
    {
        reference.Scope = target;
        moved++;
    }
    else kept.Add(scope.Name + ": " + reference.FullName);
}

var used = new HashSet<string> { "netstandard" };
foreach (var reference in module.GetTypeReferences())
    if (reference.DeclaringType == null && reference.Scope is AssemblyNameReference scope) used.Add(scope.Name);

foreach (var assembly in module.AssemblyReferences.ToArray())
{
    if (!used.Contains(assembly.Name)) module.AssemblyReferences.Remove(assembly);
    else if (assembly.Name != "netstandard")
    {
        if (!lowered.TryGetValue(assembly.Name, out var version))
        {
            Console.Error.WriteLine("no lowered version for " + assembly.Name);
            return 1;
        }

        assembly.Version = version;
    }
}

foreach (var attribute in module.Assembly.CustomAttributes)
{
    if (attribute.AttributeType.FullName != "System.Runtime.Versioning.TargetFrameworkAttribute") continue;
    attribute.ConstructorArguments[0] = new CustomAttributeArgument(attribute.ConstructorArguments[0].Type, ".NETStandard,Version=v2.1");
    attribute.Properties.Clear();
}

module.Write(args[2], new WriterParameters { DeterministicMvid = true });
Console.WriteLine("moved " + moved + " kept " + kept.Count);
foreach (var line in kept) Console.WriteLine("  " + line);
return 0;

static int CheckShims(string dir, Dictionary<string, Version> lowered)
{
    var failures = 0;
    foreach (var entry in lowered)
    {
        if (entry.Key == "System.Runtime.Intrinsics") continue;
        var path = Path.Combine(dir, entry.Key + ".dll");
        if (!File.Exists(path))
        {
            Console.Error.WriteLine("missing shim " + entry.Key);
            failures++;
            continue;
        }

        using var shim = ModuleDefinition.ReadModule(path);
        if (shim.Assembly.Name.Version != entry.Value)
        {
            Console.Error.WriteLine(entry.Key + " is " + shim.Assembly.Name.Version + ", table says " + entry.Value);
            failures++;
        }
    }

    var intrinsics = Path.Combine(dir, "System.Runtime.Intrinsics.dll");
    if (File.Exists(intrinsics))
    {
        Console.Error.WriteLine("System.Runtime.Intrinsics shim appeared; the lowered table needs a real version");
        failures++;
    }

    Console.WriteLine(failures == 0 ? "shims match the lowered table" : "shim table mismatch");
    return failures == 0 ? 0 : 1;
}
