using System;
using System.Collections.Generic;
using System.Linq;
using Data;
using ExpandWorldData.Data;
using Common;

namespace ExpandWorldData.PersistentEvents;

public partial class PersistentEventManager
{
  private static void ValidateServerOnlyLocation(string name)
  {
    if (name == "") return;
    var location = ZoneSystem.instance ? ZoneSystem.instance.GetLocation(name) : null;
    if (location == null)
      throw new ArgumentException($"Persistent event location '{name}' is not configured. Add it to location data first.");
    if (!location.m_prefab.IsValid)
    {
      if (!BlueprintManager.TryGet(location.m_prefab.Name, out _))
        throw new ArgumentException($"Persistent event location '{name}' has no prefab or blueprint.");
      ValidateServerOnlyPrefabs(new[] { location.m_prefab.Name });
    }
  }

  private static List<PersistentEventSystem.ObjectSpawnSettings> BuildServerOnlyObjects(PersistentEventObjectYaml[] objects,
    string fileName, Dictionary<PersistentEventSystem.ObjectSpawnSettings, PersistentEventObjectExtra> metadata)
  {
    List<PersistentEventSystem.ObjectSpawnSettings> result = [];
    foreach (var obj in objects)
    {
      ValidateServerOnlyObjectSettings(obj);
      var nativeOnly = ValidateServerOnlyPrefabs(obj.prefabs) && obj.data == "";
      var group = PersistentEventLoader.FromObject(obj, fileName, metadata);
      if (obj.data != "" && metadata[group].Data == null)
        throw new ArgumentException($"Persistent event object data '{obj.data}' was not found.");
      // Native prefabs without data need no replacement of the game's placement algorithm.
      if (nativeOnly) metadata.Remove(group);
      result.Add(group);
    }
    return result;
  }

  internal static bool ValidateServerOnlyPrefabs(IEnumerable<string> names)
  {
    var nativeOnly = true;
    foreach (var name in names)
      nativeOnly &= ValidateServerOnlyPrefab(name, new HashSet<string>(StringComparer.Ordinal));
    return nativeOnly;
  }

  private static void ValidateServerOnlyObjectSettings(PersistentEventObjectYaml obj)
  {
    if (obj == null || obj.prefabs == null || obj.prefabs.Length == 0 || obj.prefabs.Any(string.IsNullOrWhiteSpace))
      throw new ArgumentException("Persistent event object groups require prefab or blueprint names.");
    if (obj.data == null) throw new ArgumentException("Persistent event object data must be a string.");
    if (obj.minCount < 0 || obj.minCount > obj.maxCount)
      throw new ArgumentException("Persistent event object counts must be nonnegative and minCount must not exceed maxCount.");
    ValidateRange("objects", "minDistanceFromOtherObjects", obj.minDistanceFromOtherObjects, obj.minDistanceFromOtherObjects, 0f);
    ValidateRange("objects", "maxDistance", obj.maxDistance, obj.maxDistance, float.MinValue);
    ValidateRange("objects", "maxSlope", obj.maxSlope, obj.maxSlope, 0f, 90f);
    var waterType = typeof(PersistentEventSystem.ObjectSpawnSettings.WaterSpawnRule);
    if (obj.water == null || !Enum.IsDefined(waterType, Enum.Parse(waterType, obj.water, true)))
      throw new ArgumentException($"Invalid persistent event water rule: '{obj.water}'.");
  }

  // Returns true for a native prefab, false for a blueprint made from network prefabs.
  private static bool ValidateServerOnlyPrefab(string name, HashSet<string> visiting)
  {
    var prefab = ZNetScene.instance.GetPrefab(name);
    if (prefab)
    {
      if (!prefab.GetComponent<ZNetView>())
        throw new ArgumentException($"Persistent event prefab '{name}' has no ZNetView and cannot be sent to vanilla clients.");
      return true;
    }
    var blueprintName = Parse.Name(name);
    if (!visiting.Add(blueprintName))
      throw new ArgumentException($"Recursive persistent event blueprint '{name}'.");
    if (!BlueprintCache.TryGet(blueprintName, out var blueprint))
      throw new ArgumentException($"Persistent event prefab or blueprint '{name}' was not found.");
    foreach (var obj in blueprint.Objects)
    {
      if (obj.Chance == 0f || obj.Prefab == "") continue;
      ValidateServerOnlyPrefab(obj.Prefab, visiting);
    }
    visiting.Remove(blueprintName);
    return false;
  }
}
