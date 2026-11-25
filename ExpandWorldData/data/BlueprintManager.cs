using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using ExpandWorldData.Dungeon;
using Service;
using Common;
using Data;

namespace ExpandWorldData.Data;

//<summary>External blueprint data from configs (not directly from the blueprint file).
public class BlueprintMetaData(string[] snapPieces)
{
  public string[] SnapPieces = snapPieces;
}
public class BlueprintManager
{
  public static bool Has(string name) => BlueprintCache.IsLoaded(Parse.Name(name));
  public static bool TryGet(string name, out Blueprint bp)
  {
    if (!Has(name)) Load(name);
    return BlueprintCache.TryGet(Parse.Name(name), out bp);
  }
  private static readonly Dictionary<string, BlueprintMetaData> MetaData = [];
  // Snap points must be loaded before the blueprint is centered.
  public static void ApplySnapPieces(Blueprint bp)
  {
    if (MetaData.TryGetValue(bp.Name, out var data))
      bp.LoadSnapPoints(data.SnapPieces);
  }
  public static bool Load(string name) => Load(name, []);
  [MethodImpl(MethodImplOptions.NoInlining)]
  public static bool Load(string name, string[] snapPieces)
  {
    var hash = name.GetStableHashCode();
    if (ZNetScene.instance.m_namedPrefabs.ContainsKey(hash)) return true;
    if (BlueprintCache.IsLoaded(name) && MetaData.TryGetValue(name, out var data))
    {
      // Already loaded so no point to check again.
      if (data.SnapPieces == snapPieces) return true;
      // Equality alone is not enough for nested blueprints: another parent may
      // have changed a child's metadata. Only reuse verified leaves within this
      // room rebuild, and require the identical cached instance (watcher/alias safe).
      if (CanReuseLeaf(name, snapPieces, data))
      {
        MetaData[name] = new(snapPieces);
        return ReuseLeaf(name);
      }
    }
    var rebuild = RoomRebuildCache.Current;
    rebuild?.Blueprints.Remove(name);
    MetaData[name] = new(snapPieces);
    BlueprintCache.Remove(name);
    if (BlueprintCache.TryGet(name, out var bp))
    {
      foreach (var obj in bp.Objects)
      {
        if (obj.Chance == 0) continue;
        Load(obj.Prefab, snapPieces);
      }
      if (rebuild != null && bp.Name == name && IsLeaf(bp))
        rebuild.Blueprints[name] = new(bp, snapPieces);
      return true;
    }
    Log.Warning($"Object / blueprint {name} not found!");
    return false;
  }
  private static bool IsLeaf(Blueprint bp) => bp.Objects.All(obj => obj.Chance == 0 ||
    ZNetScene.instance.m_namedPrefabs.ContainsKey(obj.Prefab.GetStableHashCode()));

  private static bool CanReuseLeaf(string name, string[] snapPieces, BlueprintMetaData data)
  {
    var rebuild = RoomRebuildCache.Current;
    return rebuild != null && rebuild.Blueprints.TryGetValue(name, out var leaf) &&
      leaf.SnapPieces.SequenceEqual(snapPieces, System.StringComparer.Ordinal) &&
      leaf.SnapPieces.SequenceEqual(data.SnapPieces, System.StringComparer.Ordinal) &&
      BlueprintCache.TryGet(name, out var current) && ReferenceEquals(current, leaf.Blueprint) && IsLeaf(current);
  }

  // Dedicated optional profiler hook for actual content-equality hits.
  [MethodImpl(MethodImplOptions.NoInlining)]
  private static bool ReuseLeaf(string name) => true;

  // The watcher has already removed the blueprint from the cache.
  public static void Reload(string name, bool wasLoaded)
  {
    if (!wasLoaded) return;
    Log.Info($"Reloading blueprint {name}.");
    if (MetaData.TryGetValue(name, out var data))
      Load(name, data.SnapPieces);
    else
      Load(name);
  }
}
