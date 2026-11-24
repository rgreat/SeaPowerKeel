using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Mono.Cecil;

namespace SeaPowerKeel;

internal enum StartupTiming { Normal, Early, Blocking }

/// <summary>
/// Opt-in "start early" (asked for by rgreat, author of the Alternative mods, 2026-11-12). On the 3D globe build Keel starts mods
/// only after the game's start-up load screen is done, because mods started during it can keep the unit data from loading (and
/// can crash in Noesis). A mod that knows it is safe there, and needs to be running while the game loads, asks for it, without
/// referencing Keel:
/// - in its <c>_info.ini</c>: a <c>[Keel]</c> section with <c>StartEarly=true</c> (every plugin in that mod folder), or
/// - in its DLL: <c>[assembly: System.Reflection.AssemblyMetadata("Keel.StartEarly", "true")]</c> (every plugin in that DLL).
/// Early mods start as soon as Keel has checked them, before the load screen ends; every other mod waits as before. An early mod
/// that needs (or must start after) a mod that waits starts with the others, and the report says why.
/// </summary>
internal static class EarlyStart
{
    public const string InfoKey = "StartEarly", MetadataKey = "Keel.StartEarly";
    public const string BlockingInfoKey = "BlockingStart", BlockingMetadataKey = "Keel.BlockingStart";

    /// <summary>True when <paramref name="infoIni"/> (the text of a mod's _info.ini) has [Keel] StartEarly=true.</summary>
    public static bool InInfoIni(string infoIni) => InInfoIni(infoIni, InfoKey);

    /// <summary>True when <paramref name="infoIni"/> has [Keel] <paramref name="key"/>=true.</summary>
    public static bool InInfoIni(string infoIni, string key)
    {
        bool inKeel = false;
        foreach (string raw in (infoIni ?? "").Split('\n'))
        {
            string line = raw.Split(';', '#')[0].Trim();
            if (line.StartsWith("["))
            {
                inKeel = line.Equals("[Keel]", StringComparison.OrdinalIgnoreCase);
                continue;
            }
            int eq = line.IndexOf('=');
            if (inKeel && eq > 0 && line.Substring(0, eq).Trim().Equals(key, StringComparison.OrdinalIgnoreCase))
                return IsTrue(line.Substring(eq + 1));
        }
        return false;
    }

    public static StartupTiming FolderTiming(string folder)
    {
        try
        {
            string path = Path.Combine(folder, "_info.ini");
            if (File.Exists(path))
            {
                string ini = File.ReadAllText(path);
                if (InInfoIni(ini, BlockingInfoKey)) return StartupTiming.Blocking;
                if (InInfoIni(ini, InfoKey)) return StartupTiming.Early;
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return StartupTiming.Normal;
    }

    public static StartupTiming AssemblyTiming(AssemblyDefinition assembly)
    {
        if (AssemblyHas(assembly, BlockingMetadataKey)) return StartupTiming.Blocking;
        return AssemblyHas(assembly, MetadataKey) ? StartupTiming.Early : StartupTiming.Normal;
    }

    // The first-screen hook needs this answer before Unity resumes. Only file and Cecil reads run in parallel.
    public static StartupTiming Discover(IList<ModFolder> folders)
    {
        string[] enabled = folders.Where(f => f.Enabled).Select(f => f.Path).ToArray();
        var iniTiming = StartupTiming.Normal;
        foreach (string path in enabled)
        {
            var timing = FolderTiming(path);
            if (timing == StartupTiming.Blocking) return timing;
            if (timing == StartupTiming.Early) iniTiming = timing;
        }
        if (enabled.Length == 0) return iniTiming;

        var dllTiming = enabled.AsParallel().WithDegreeOfParallelism(Math.Min(4, Environment.ProcessorCount))
                               .Select(ScanFolder).Max();
        return dllTiming > iniTiming ? dllTiming : iniTiming;
    }

    private static StartupTiming ScanFolder(string path)
    {
        var timing = StartupTiming.Normal;
        try
        {
            foreach (string dll in Directory.EnumerateFiles(path, "*.dll", SearchOption.AllDirectories))
            {
                if (Scanner.ExcludedReason(dll) != null) continue;
                try
                {
                    using var module = ModuleDefinition.ReadModule(dll);
                    var request = AssemblyTiming(module.Assembly);
                    if (request == StartupTiming.Blocking) return request;
                    if (request == StartupTiming.Early) timing = request;
                }
                catch (Exception)
                {
                    // Native and malformed DLLs cannot supply a timing hint. Scanner diagnoses them on its normal pass.
                }
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return timing;
    }

    private static bool AssemblyHas(AssemblyDefinition assembly, string key) =>
        assembly?.CustomAttributes.Any(a => a.AttributeType.FullName == "System.Reflection.AssemblyMetadataAttribute"
                                            && a.ConstructorArguments.Count == 2
                                            && a.ConstructorArguments[0].Value as string == key
                                            && IsTrue(a.ConstructorArguments[1].Value as string)) == true;

    private static bool IsTrue(string v) => (v ?? "").Trim().Trim('"').Equals("true", StringComparison.OrdinalIgnoreCase) || (v ?? "").Trim() == "1";

    /// <summary>
    /// The plugins that start early: those that asked and are still to start, less every one that depends on, or must start after,
    /// a plugin Keel starts later (repeated until stable). <paramref name="note"/> gets one line per plugin moved to the later start.
    /// </summary>
    public static HashSet<PluginEntry> Set(IList<PluginEntry> all, Action<PluginEntry, string> note, bool blocking = false)
    {
        var early = new HashSet<PluginEntry>(all.Where(p => (blocking ? p.BlockingStart : p.StartEarly) && p.State == PluginState.Pending));
        var byGuid = all.Where(p => p.State == PluginState.Pending).GroupBy(p => p.Guid).ToDictionary(g => g.Key, g => g.First());
        bool changed = true;
        while (changed)
        {
            changed = false;
            foreach (PluginEntry p in early.ToList())
            {
                // what must be running before p starts: its dependencies, and mods that say they load before it
                string late = p.Dependencies.Select(d => d.Guid)
                               .Concat(all.Where(x => x.LoadBefore.Contains(p.Guid)).Select(x => x.Guid))
                               .Where(g => byGuid.TryGetValue(g, out PluginEntry x) && !early.Contains(x))
                               .Select(g => byGuid[g].Display).FirstOrDefault();
                if (late == null)
                    continue;
                early.Remove(p);
                changed = true;
                note?.Invoke(p, blocking ? $"Asked for a blocking start, but it needs {late}, which does not hold the game up: it starts early without it."
                           : $"Asked to start early, but it needs {late}, which starts after the load screen: it starts with the others.");
            }
        }
        return early;
    }
}
