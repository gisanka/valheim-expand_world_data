using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using BepInEx;
using BepInEx.Bootstrap;
using HarmonyLib;
using Service;
using SoftReferenceableAssets;
using AssetRuntime = SoftReferenceableAssets.Runtime;
using UnityEngine;

namespace ExpandWorldData.Dungeon;

// One instance per rebuild. Hash each distinct dependency file only once.
// Files must be stationary during a rebuild; changes observed before commit
// prevent writing. This cannot detect arbitrary in-memory changes by other mods.
internal sealed class RoomMetadataFingerprint
{
  private sealed class Stamp(string path, long length, DateTime modified, DateTime created, string hash)
  {
    internal readonly string Path = path, Hash = hash;
    internal readonly long Length = length;
    internal readonly DateTime Modified = modified, Created = created;
    internal bool Matches(FileInfo info) => info.Exists && info.Length == Length &&
      info.LastWriteTimeUtc == Modified && info.CreationTimeUtc == Created;
  }

  private readonly Dictionary<string, Stamp> Files = new(StringComparer.Ordinal);
  internal int FileCount => Files.Count;
  internal long Bytes { get; private set; }
  internal long Ticks { get; private set; }

  internal static byte[] HashRows(IEnumerable<string> rows)
  {
    using var buffer = new MemoryStream();
    using (var writer = new BinaryWriter(buffer, new UTF8Encoding(false, true), true))
      foreach (var row in rows) writer.Write(row); // length-prefixed, no delimiter ambiguity
    using var sha = SHA256.Create();
    return sha.ComputeHash(buffer.ToArray());
  }

  internal static string Hex(byte[] bytes) => BitConverter.ToString(bytes).Replace("-", "");

  // Reject links/reparse points, including ancestor directories. A link could
  // redirect an ostensibly vanilla bundle to an untracked mod asset.
  internal static void CheckPath(string path)
  {
    for (string? current = path; !string.IsNullOrEmpty(current); current = System.IO.Path.GetDirectoryName(current))
      if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
        throw new InvalidDataException("linked dependency");
  }

  internal string HashFile(string path)
  {
    path = Path.GetFullPath(path);
    var start = Stopwatch.GetTimestamp();
    try
    {
      var info = new FileInfo(path);
      if (Files.TryGetValue(path, out var previous))
      {
        if (!previous.Matches(info)) throw new IOException("dependency changed during rebuild");
        return previous.Hash;
      }
      CheckPath(path);
      var length = info.Length;
      var modified = info.LastWriteTimeUtc;
      var created = info.CreationTimeUtc;
      string hash;
      using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.SequentialScan))
      using (var sha = SHA256.Create())
        hash = Hex(sha.ComputeHash(stream));
      Bytes += length;
      info.Refresh();
      var stamp = new Stamp(path, length, modified, created, hash);
      if (!stamp.Matches(info)) throw new IOException("dependency changed during read");
      Files.Add(path, stamp);
      return hash;
    }
    finally { Ticks += Stopwatch.GetTimestamp() - start; }
  }

  internal bool Unchanged() => Files.Values.All(file => file.Matches(new FileInfo(file.Path)));
}

internal sealed class RoomMetadataDependencies
{
  internal readonly RoomMetadataFingerprint Fingerprint = new();
  internal readonly byte[] Identity;
  private readonly AssetBundleLoader Loader;
  private readonly string VanillaRoot;
  private readonly string[] PatchRows;
  internal long IdentityTicks { get; }
  internal long AssetTicks { get; private set; }
  internal string LastRejection { get; private set; } = "none";
  internal string IdentityParts { get; private set; } = "";

  internal RoomMetadataDependencies()
  {
    var start = Stopwatch.GetTimestamp();
    if (AssetRuntime.Loader is not AssetBundleLoader loader || !loader.Initialized || !ReferenceEquals(loader, AssetBundleLoader.Instance))
      throw new InvalidDataException("unsupported or uninitialized asset loader");
    Loader = loader;
    VanillaRoot = Path.GetFullPath(SoftReferenceableAssets.Shared.GetBuildDirectory());
    RoomMetadataFingerprint.CheckPath(VanillaRoot);
    PatchRows = ReadPatches();
    Identity = BuildIdentity();
    IdentityTicks = Stopwatch.GetTimestamp() - start;
  }

  private static readonly HashSet<string> ConfigExtensions = new(StringComparer.OrdinalIgnoreCase)
  { ".cfg", ".yaml", ".yml", ".json", ".xml", ".toml", ".ini", ".txt", ".csv", ".blueprint", ".vbuild" };

  private static bool Under(string path, string root)
  {
    var comparison = Path.DirectorySeparatorChar == '\\' ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    return path.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar, comparison);
  }

  private static bool DirectoryPresent(string path)
  {
    try { return (File.GetAttributes(path) & FileAttributes.Directory) != 0; }
    catch (FileNotFoundException) { return false; }
    catch (DirectoryNotFoundException) { return false; }
  }

  private static IEnumerable<string> InputFiles(string root, bool configOnly)
  {
    root = Path.GetFullPath(root);
    if (!DirectoryPresent(root)) yield break;
    RoomMetadataFingerprint.CheckPath(root);
    var dirs = new Stack<string>();
    dirs.Push(root);
    while (dirs.Count > 0)
    {
      var dir = dirs.Pop();
      foreach (var child in Directory.EnumerateDirectories(dir))
      {
        // Known generated EWD/EWP output, not a configuration source.
        if (configOnly && (string.Equals(child, Path.Combine(Paths.ConfigPath, "expand_world", "logs"), StringComparison.OrdinalIgnoreCase) ||
          string.Equals(child, Yaml.BackupDirectory, StringComparison.OrdinalIgnoreCase))) continue;
        RoomMetadataFingerprint.CheckPath(child);
        dirs.Push(child);
      }
      foreach (var file in Directory.EnumerateFiles(dir))
        if (!configOnly || ConfigExtensions.Contains(Path.GetExtension(file))) yield return Path.GetFullPath(file);
    }
  }

  private void AddAssembly(List<string> rows, Assembly assembly, string? fallback = null)
  {
    if (assembly.IsDynamic) throw new InvalidDataException("dynamic assembly");
    // BepInEx can load a preloader-patched game assembly from bytes, without
    // Location. Hash the original managed file, active MVID and patcher inputs.
    var path = string.IsNullOrEmpty(assembly.Location) ? fallback : assembly.Location;
    if (path == null || path.Length == 0) throw new InvalidDataException("assembly without file");
    rows.Add(assembly.FullName);
    rows.Add(assembly.ManifestModule.ModuleVersionId.ToString());
    rows.Add(Path.GetFullPath(path));
    rows.Add(Fingerprint.HashFile(path));
  }

  private string HashFileRows(IEnumerable<string> paths)
  {
    var rows = new List<string>();
    foreach (var path in paths.Select(Path.GetFullPath).Distinct(StringComparer.Ordinal).OrderBy(p => p, StringComparer.Ordinal))
    {
      rows.Add(path); rows.Add(Fingerprint.HashFile(path));
    }
    return RoomMetadataFingerprint.Hex(RoomMetadataFingerprint.HashRows(rows));
  }

  private byte[] BuildIdentity()
  {
    var rows = new List<string> { "room-metadata-dependencies-v2", RoomMetadataFingerprint.Hex(RoomMetadataFile.Identity()),
      Application.version, Application.unityVersion, Helper.IsServer() ? "server" : "client", VanillaRoot };
    foreach (var assembly in new[] { typeof(Room).Assembly, typeof(AssetID).Assembly, typeof(UnityEngine.Object).Assembly,
      typeof(Harmony).Assembly, typeof(BaseUnityPlugin).Assembly }.Distinct())
      AddAssembly(rows, assembly, Path.Combine(Paths.ManagedPath, assembly.GetName().Name + ".dll"));
    var core = RoomMetadataFingerprint.Hex(RoomMetadataFingerprint.HashRows(rows));
    rows.Clear();
    // Include active plugin identities, actual binaries, and live ConfigEntry values
    // (not just the on-disk config, which may lag synchronized settings).
    foreach (var pair in Chainloader.PluginInfos.OrderBy(p => p.Key, StringComparer.Ordinal))
    {
      rows.Add("plugin"); rows.Add(pair.Key); rows.Add(pair.Value.Metadata.Version.ToString());
      var instance = pair.Value.Instance;
      if (instance == null) { rows.Add("inactive"); continue; }
      AddAssembly(rows, instance.GetType().Assembly, pair.Value.Location);
      foreach (var entry in instance.Config.OrderBy(e => e.Key.Section, StringComparer.Ordinal).ThenBy(e => e.Key.Key, StringComparer.Ordinal))
      {
        rows.Add("config"); rows.Add(entry.Key.Section); rows.Add(entry.Key.Key); rows.Add(entry.Value.GetSerializedValue());
      }
    }
    var plugins = RoomMetadataFingerprint.Hex(RoomMetadataFingerprint.HashRows(rows));
    var configRoots = new[] { Paths.ConfigPath, Path.Combine(Paths.GameRootPath, "BepInEx", "config"), Yaml.BaseDirectory,
      Configuration.BlueprintGlobalFolder, Configuration.BlueprintLocalFolder };
    var configs = HashFileRows(configRoots.SelectMany(root => InputFiles(root, true)));
    var binaries = HashFileRows(InputFiles(Paths.PluginPath, false).Concat(InputFiles(Paths.PatcherPluginPath, false))
      .Concat(Directory.EnumerateFiles(Paths.ManagedPath, "*.dll"))
      .Concat(Directory.EnumerateFiles(Paths.BepInExAssemblyDirectory, "*.dll")));
    var patches = RoomMetadataFingerprint.Hex(RoomMetadataFingerprint.HashRows(PatchRows));
    // These digests identify which part changed, without logging config values.
    IdentityParts = $"core={core}, pluginsAndLiveConfig={plugins}, configFiles={configs}, modAndManagedFiles={binaries}, patches={patches}";
    return RoomMetadataFingerprint.HashRows(new[] { "room-metadata-dependencies-v2", core, plugins, configs, binaries, patches });
  }

  private static bool IsSkippedMethod(MethodBase method)
  {
    var type = method.DeclaringType;
    if (type == null) return false;
    if (type.IsGenericType) type = type.GetGenericTypeDefinition();
    if (type == typeof(Room) || type == typeof(RoomConnection) || type == typeof(DungeonDB.RoomData) ||
        type == typeof(SoftReference<>) || type == typeof(AssetBundleLoader) || type == typeof(AssetLoader) ||
        type == typeof(BundleLoader) || type == typeof(AssetRuntime) || type == typeof(AssetBundle) ||
        type == typeof(AssetBundleDatabase) || type == typeof(SoftReferenceableAssets.AssetBundleManifest)) return true;
    if (type == typeof(Helper)) return method.Name is "SafeLoad" or "LoadRoomAsset" or "ReadRoomAsset" or "ReleaseRoomAsset";
    if (type == typeof(RoomManager)) return method.Name is "ReadBaseRoom" or "ReadBaseConnections" or "CopyBaseConnections";
    if (type == typeof(GameObject) || type == typeof(Component)) return method.Name.StartsWith("GetComponent", StringComparison.Ordinal);
    if (type == typeof(Transform)) return method.Name is "get_localPosition" or "get_localRotation";
    return type == typeof(UnityEngine.Object) && method.Name == "get_name";
  }

  private bool IsKnownObserver(MethodBase target, HarmonyLib.Patch patch, int groupIndex)
  {
    var method = patch.PatchMethod;
    if (patch.owner == "expand_world_prefabs.startup-profiler" &&
      method.DeclaringType?.FullName == "ExpandWorld.Prefab.StartupProfiler" &&
      method.Name is "MeasurePrefix" or "MeasureFinalizer" or "RoomNamePrefix" or "RoomAssetPrefix" or "RoomLoaderPrefix")
      return groupIndex != 2;

    // Reviewed installed VCP DLL: these two hooks only record elapsed time.
    // Do not trust a whole owner, or a future build with the same method names.
    if (patch.owner != "MidnightsFX.ValheimCommunityPatch" ||
      target.DeclaringType != typeof(AssetBundleLoader) || target.Name != "Load" ||
      method.DeclaringType?.FullName != "ValheimCommunityPatch.Patches.Performance.WorldGenTimingStats+AssetLoadHook" ||
      method.Module.ModuleVersionId != new Guid("60e16937-3ca2-4928-a3f0-27d6a3a13725") ||
      !method.IsStatic || method.ReturnType != typeof(void)) return false;

    var targetParameters = target.GetParameters();
    if (targetParameters.Length != 1 || targetParameters[0].ParameterType != typeof(AssetID)) return false;
    var parameters = method.GetParameters();
    if (parameters.Length != 1 || parameters[0].Name != "__state") return false;
    var prefix = groupIndex == 0 && method.Name == "Prefix" &&
      parameters[0].IsOut && parameters[0].ParameterType == typeof(long).MakeByRefType();
    var finalizer = groupIndex == 3 && method.Name == "Finalizer" &&
      parameters[0].ParameterType == typeof(long);
    if (!prefix && !finalizer) return false;

    var path = method.Module.Assembly.Location;
    return path.Length != 0 && Fingerprint.HashFile(path) ==
      "D429A728C9D44A258504E5170F4B6F45806E0141C9BB2657007C7543816D8DE2";
  }

  private string[] ReadPatches()
  {
    var rows = new List<string>();
    foreach (var method in Harmony.GetAllPatchedMethods().Where(m => IsSkippedMethod(m) ||
      m.DeclaringType == typeof(DungeonDB) || m.DeclaringType == typeof(RoomManager) ||
      (m.DeclaringType == typeof(ZNetScene) && (m.Name is "Awake" or "Start")))
      .OrderBy(m => m.DeclaringType?.FullName, StringComparer.Ordinal).ThenBy(m => m.ToString(), StringComparer.Ordinal))
    {
      var info = Harmony.GetPatchInfo(method);
      if (info == null) throw new InvalidDataException("unknown patch state");
      rows.Add(method.DeclaringType?.FullName ?? ""); rows.Add(method.ToString());
      var groups = new[] { info.Prefixes, info.Postfixes, info.Transpilers, info.Finalizers };
      for (var groupIndex = 0; groupIndex < groups.Length; ++groupIndex)
      {
        var group = groups[groupIndex];
        rows.Add("patch group"); rows.Add(group.Count().ToString(System.Globalization.CultureInfo.InvariantCulture));
        foreach (var patch in group)
        {
          if (IsSkippedMethod(method) && !IsKnownObserver(method, patch, groupIndex))
            throw new InvalidDataException("unverified patch: " + patch.owner + " / " + method.DeclaringType?.Name + "." + method.Name);
          rows.Add(patch.owner); rows.Add(patch.PatchMethod.Module.ModuleVersionId.ToString());
          rows.Add(patch.PatchMethod.DeclaringType?.FullName ?? "");
          rows.Add(patch.PatchMethod.ToString()); rows.Add(patch.priority.ToString(System.Globalization.CultureInfo.InvariantCulture));
          var before = patch.before ?? Array.Empty<string>();
          var after = patch.after ?? Array.Empty<string>();
          rows.Add(before.Length.ToString(System.Globalization.CultureInfo.InvariantCulture)); rows.AddRange(before);
          rows.Add(after.Length.ToString(System.Globalization.CultureInfo.InvariantCulture)); rows.AddRange(after);
        }
      }
    }
    return rows.ToArray();
  }

  internal bool TryAssetKey(string name, DungeonDB.RoomData room, out (string Name, string Id) key)
  {
    var start = Stopwatch.GetTimestamp();
    key = default;
    try
    {
      var reference = room.m_prefab;
      var id = reference.m_assetID;
      // A live or synthetic asset can contain runtime mutations not represented
      // in bundle bytes. Only cold assets in the stock loader are eligible.
      if (string.IsNullOrEmpty(name) || !id.IsValid || reference.m_loadedAsset != null || room.m_loadedRoom != null ||
        reference.IsLoaded || reference.IsLoading || !ReferenceEquals(AssetRuntime.Loader, Loader) ||
        !Loader.m_assetIDToLoaderIndex.TryGetValue(id, out var assetIndex))
        throw new InvalidDataException("not a cold file-backed asset");
      var asset = Loader.m_assetLoaders[assetIndex];
      if (!asset.m_assetID.Equals(id) || string.IsNullOrEmpty(asset.m_assetPathInBundle)) throw new InvalidDataException("asset mapping");
      if (asset.m_loadedCallbacks != null && asset.m_loadedCallbacks.Count != 0) throw new InvalidDataException("pending asset callbacks");
      var bundleIndex = asset.m_bundleLoaderIndex;
      var indices = Loader.m_bundleLoaders[bundleIndex].m_bundleLoaderIndicesOfThisAndDependencies;
      if (indices == null || !indices.Contains(bundleIndex)) throw new InvalidDataException("dependency mapping");
      var rows = new List<string> { id.ToString(), asset.m_assetPathInBundle };
      // These are the exact indices used by AssetLoader.Tick; include the root
      // bundle explicitly and preserve the loader's dependency order.
      rows.Add(Loader.m_bundleLoaders[bundleIndex].m_bundleName);
      foreach (var index in indices)
      {
        var bundle = Loader.m_bundleLoaders[index];
        if (!Path.IsPathRooted(bundle.m_bundlePath)) throw new InvalidDataException("relative bundle path");
        var path = Path.GetFullPath(bundle.m_bundlePath);
        if (!Under(path, VanillaRoot)) throw new InvalidDataException("non-vanilla bundle");
        rows.Add(bundle.m_bundleName); rows.Add(path); rows.Add(Fingerprint.HashFile(path));
      }
      key = (name, id + ":" + RoomMetadataFingerprint.Hex(RoomMetadataFingerprint.HashRows(rows)));
      return true;
    }
    catch (Exception ex)
    {
      LastRejection = ex is InvalidDataException ? ex.Message : ex.GetType().Name;
      return false;
    }
    finally { AssetTicks += Stopwatch.GetTimestamp() - start; }
  }

  internal bool Unchanged() => ReferenceEquals(AssetRuntime.Loader, Loader) && Fingerprint.Unchanged() && ReadPatches().SequenceEqual(PatchRows);
}
