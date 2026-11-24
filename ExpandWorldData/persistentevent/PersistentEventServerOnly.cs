using System;
using System.Collections.Generic;
using System.Linq;
using ExpandWorldData.Data;
using Service;

namespace ExpandWorldData.PersistentEvents;

// Nullable values distinguish omitted settings from explicit zero and false.
public class PersistentEventServerOnlyYaml
{
  public string name = "";
  public bool? enabled;
  public float? customChance;
  public float? customInterval;
  public StringList? requiredGlobalKeys;
  public StringList? forbiddenGlobalKeys;
  public int? maxConcurrent;
  public float? minRadius;
  public float? maxRadius;
  public float? minDuration;
  public float? maxDuration;
  public string? location;
  public string? startMessage;
  public string? endMessage;
  public string[]? startCommands;
  public string[]? endCommands;
  public PersistentEventObjectYaml[]? objects;
  public string? biome;
  public float? minDistance;
  public float? maxDistance;
  public float? minAltitude;
  public float? maxAltitude;
  public float? maxSlope;
  public float? minDistanceFromSimilar;
}

public partial class PersistentEventManager
{
  private static SetResult LoadServerOnly(PersistentEventSystem system, Dictionary<string, string> files)
  {
    var result = BuildServerOnly(files, out var data, out var extras, out var objectExtras);
    if (result != SetResult.Applied) return result;
    // sourceEventId is shared with saves and unmodded clients. Never reorder or remove slots.
    system.m_possibleEvents = data;
    PersistentEventLoader.Extra = extras;
    PersistentEventLoader.ObjectExtra = objectExtras;
    HasData = true;
    Log.Info($"Reloading persistent event rules ({extras.Count} entries).");
    return SetResult.Applied;
  }

  private static SetResult BuildServerOnly(Dictionary<string, string> files,
    out List<PersistentEventSystem.PersistentEvent> data,
    out Dictionary<PersistentEventSystem.PersistentEvent, PersistentEventExtra> extras,
    out Dictionary<PersistentEventSystem.ObjectSpawnSettings, PersistentEventObjectExtra> objectExtras)
  {
    data = [];
    extras = [];
    objectExtras = [];
    var candidate = new List<PersistentEventSystem.PersistentEvent>(Originals);
    Dictionary<PersistentEventSystem.PersistentEvent, PersistentEventExtra> candidateExtras = [];
    Dictionary<PersistentEventSystem.ObjectSpawnSettings, PersistentEventObjectExtra> candidateObjects = [];
    HashSet<string> names = new(StringComparer.Ordinal);
    foreach (var file in files)
    {
      try
      {
        // Client appearance stays native; all other supported settings are merged by name.
        var entries = Yaml.DeserializerUnSafe().Deserialize<List<PersistentEventServerOnlyYaml>>(file.Value) ?? [];
        foreach (var entry in entries)
        {
          if (entry == null || string.IsNullOrWhiteSpace(entry.name))
            throw new ArgumentException("Persistent event rules require a name.");
          var matches = Originals.Select((ev, index) => (ev, index)).Where(pair => pair.ev.internalName == entry.name).ToArray();
          if (matches.Length != 1)
            throw new ArgumentException($"Server-only rules require an existing, unambiguous persistent event name: '{entry.name}'.");
          if (!names.Add(entry.name))
            throw new ArgumentException($"Duplicate persistent event rules for '{entry.name}'.");
          var original = matches[0];
          var effective = ApplyServerOnly(original.ev, entry);
          if (entry.location != null) ValidateServerOnlyLocation(entry.location);
          if (entry.objects != null) effective.objectsToSpawn = BuildServerOnlyObjects(entry.objects, file.Key, candidateObjects);
          candidate[original.index] = effective;
          candidateExtras.Add(effective, new PersistentEventExtra(new PersistentEventYaml
          {
            enabled = entry.enabled ?? true,
            customChance = entry.customChance ?? 0f,
            customInterval = entry.customInterval ?? 0f,
            requiredGlobalKeys = entry.requiredGlobalKeys,
            forbiddenGlobalKeys = entry.forbiddenGlobalKeys,
            startMessage = entry.startMessage ?? "",
            endMessage = entry.endMessage ?? "",
            startCommands = entry.startCommands,
            endCommands = entry.endCommands
          }) { OverrideDuration = entry.minDuration.HasValue || entry.maxDuration.HasValue });
        }
      }
      catch (Exception e)
      {
        Log.Error($"{file.Key}: {e.Message}");
        return SetResult.Failed;
      }
    }
    if (names.Count == 0)
    {
      Log.Warning("Failed to load any persistent event rules. No changes done.");
      return SetResult.Failed;
    }
    data = candidate;
    extras = candidateExtras;
    objectExtras = candidateObjects;
    return SetResult.Applied;
  }

  private static PersistentEventSystem.PersistentEvent ApplyServerOnly(PersistentEventSystem.PersistentEvent original, PersistentEventServerOnlyYaml entry)
  {
    var result = CopyOriginal(original);
    result.minRadius = entry.minRadius ?? original.minRadius;
    result.maxRadius = entry.maxRadius ?? original.maxRadius;
    result.locationToSpawn = entry.location ?? original.locationToSpawn;
    if (entry.minDuration.HasValue || entry.maxDuration.HasValue)
    {
      result.minDurationInSeconds = entry.minDuration ?? original.minDurationInSeconds;
      result.maxDurationInSeconds = entry.maxDuration ?? original.maxDurationInSeconds;
      result.hasDuration = result.minDurationInSeconds > 0f || result.maxDurationInSeconds > 0f;
      ValidateRange(entry.name, "duration", result.minDurationInSeconds, result.maxDurationInSeconds, 0f);
    }
    ValidateCommands(entry.name, entry.startCommands);
    ValidateCommands(entry.name, entry.endCommands);
    result.maxConcurrent = entry.maxConcurrent ?? original.maxConcurrent;
    result.minDistanceFromCenter = entry.minDistance ?? original.minDistanceFromCenter;
    result.maxDistanceFromCenter = entry.maxDistance ?? original.maxDistanceFromCenter;
    result.minHeight = entry.minAltitude.HasValue ? Helper.AltitudeToHeight(entry.minAltitude.Value) : original.minHeight;
    result.maxHeight = entry.maxAltitude.HasValue ? Helper.AltitudeToHeight(entry.maxAltitude.Value) : original.maxHeight;
    result.maxSlope = entry.maxSlope ?? original.maxSlope;
    result.minDistanceFromSimilar = entry.minDistanceFromSimilar ?? original.minDistanceFromSimilar;
    if (entry.biome != null) result.biomes = (Heightmap.Biome)ParseServerOnlyBiome(entry.biome);
    ValidateRange(entry.name, "distance", result.minDistanceFromCenter, result.maxDistanceFromCenter, 0f);
    ValidateRange(entry.name, "radius", result.minRadius, result.maxRadius, 0f);
    ValidateRange(entry.name, "altitude", result.minHeight, result.maxHeight, float.MinValue);
    ValidateRange(entry.name, "maxSlope", result.maxSlope, result.maxSlope, 0f, 90f);
    ValidateRange(entry.name, "minDistanceFromSimilar", result.minDistanceFromSimilar, result.minDistanceFromSimilar, 0f);
    var chance = entry.customChance ?? 0f;
    ValidateRange(entry.name, "customChance", chance, chance, 0f, 100f);
    var interval = entry.customInterval ?? 0f;
    ValidateRange(entry.name, "customInterval", interval, interval, 0f);
    return result;
  }

  private static PersistentEventSystem.PersistentEvent CopyOriginal(PersistentEventSystem.PersistentEvent original)
  {
    var result = new PersistentEventSystem.PersistentEvent
    {
      internalName = original.internalName,
      mapTokenString = original.mapTokenString,
      minRadius = original.minRadius,
      maxRadius = original.maxRadius,
      maxConcurrent = original.maxConcurrent,
      locationToSpawn = original.locationToSpawn,
      hasDuration = original.hasDuration,
      minDurationInSeconds = original.minDurationInSeconds,
      maxDurationInSeconds = original.maxDurationInSeconds,
      objectsToSpawn = original.objectsToSpawn,
      biomes = original.biomes,
      minDistanceFromCenter = original.minDistanceFromCenter,
      maxDistanceFromCenter = original.maxDistanceFromCenter,
      minHeight = original.minHeight,
      maxHeight = original.maxHeight,
      maxSlope = original.maxSlope,
      minDistanceFromSimilar = original.minDistanceFromSimilar,
      environmentOverride = original.environmentOverride,
      perBiomeEnvironments = original.perBiomeEnvironments,
      graphicalEffects = original.graphicalEffects
    };
    return result;
  }

  private static int ParseServerOnlyBiome(string value)
  {
    if (string.IsNullOrWhiteSpace(value)) return (int)Heightmap.Biome.All;
    var biome = Convert.ToInt32(Enum.Parse(typeof(Heightmap.Biome), value, true));
    if ((biome & ~(int)Heightmap.Biome.All) != 0)
      throw new ArgumentException($"Invalid vanilla biome names or flags: '{value}'.");
    return biome;
  }

  private static void ValidateRange(string name, string field, float min, float max, float lower, float upper = float.MaxValue)
  {
    if (float.IsNaN(min) || float.IsInfinity(min) || float.IsNaN(max) || float.IsInfinity(max)
      || min < lower || max > upper || min > max)
      throw new ArgumentException($"Event '{name}': Invalid {field} range ({min}, {max}).");
  }

  private static void ValidateCommands(string name, string[]? commands)
  {
    if (commands != null && commands.Any(command => command == null))
      throw new ArgumentException($"Event '{name}': Commands must not contain null entries.");
  }
}
