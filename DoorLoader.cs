using System.Reflection;
using CWGaming.Shared;

namespace CWGamingServ;

/// <summary>
/// Discovers and loads a mounted "door" at runtime so the host has no compile-time dependency on any
/// door (the MajorBBS / MBBSEmu model: the host owns the SDK and loads modules). Door assemblies are
/// taken from the <c>CWGAMING_DOOR_ASSEMBLIES</c> env var (platform-path-separated DLL paths) or, if
/// unset, by scanning a <c>doors/</c> directory next to the host binary. The first assembly exposing an
/// <see cref="IBbsDoorFactory"/> wins.
/// </summary>
internal static class DoorLoader
{
    public const string DoorAssembliesEnvironmentVariable = "CWGAMING_DOOR_ASSEMBLIES";
    public const string DefaultDoorsDirectoryName = "doors";

    public static IBbsDoorFactory LoadDoorFactory()
    {
        var probedPaths = ResolveDoorAssemblyPaths().ToList();

        foreach (var path in probedPaths)
        {
            if (!File.Exists(path))
                continue;

            // LoadFrom resolves the door's own dependencies from its directory while reusing assemblies
            // the host already loaded (CWGaming.Shared, Npgsql) by identity — so the door and host share
            // one copy of the contract types and casts to IBbsDoorFactory/IBbsDoor succeed.
            var assembly = Assembly.LoadFrom(path);
            var factoryType = assembly.GetTypes()
                .FirstOrDefault(t => typeof(IBbsDoorFactory).IsAssignableFrom(t) && !t.IsAbstract && !t.IsInterface);

            if (factoryType != null)
                return (IBbsDoorFactory)Activator.CreateInstance(factoryType)!;
        }

        string probed = probedPaths.Count == 0 ? "(none)" : string.Join(", ", probedPaths);
        throw new InvalidOperationException(
            $"No door (IBbsDoorFactory) was found. Set {DoorAssembliesEnvironmentVariable} to one or more " +
            $"door assembly paths, or place door DLLs in a '{DefaultDoorsDirectoryName}/' directory next to " +
            $"the host. Probed: {probed}.");
    }

    private static IEnumerable<string> ResolveDoorAssemblyPaths()
    {
        var configured = Environment.GetEnvironmentVariable(DoorAssembliesEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(configured))
            return configured.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var doorsDirectory = Path.Combine(AppContext.BaseDirectory, DefaultDoorsDirectoryName);
        if (Directory.Exists(doorsDirectory))
            return Directory.GetFiles(doorsDirectory, "*.dll");

        return [];
    }
}
