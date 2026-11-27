using System;
using System.Collections.Generic;
using System.Linq;
using ExpandWorldData.Data;
using Service;

namespace ExpandWorldData.AltBiomes;

// Other alt-biome fields are deliberately not deserialized in server-only mode.
public class AltBiomePlacementYaml
{
  public string name = "";
  public string[]? blockLocationNames;
  public string[]? blockVegetationNames;
}

public static partial class AltBiomeManager
{
  private static Dictionary<AltBiome, AltBiomePlacementYaml> OriginalPlacement = [];

  private static SetResult SetPlacementRules(Dictionary<string, string> files)
  {
    Dictionary<AltBiome, AltBiomePlacementYaml> rules = [];
    foreach (var file in files)
    {
      List<AltBiomePlacementYaml> entries;
      try
      {
        entries = Yaml.DeserializerUnSafe().Deserialize<List<AltBiomePlacementYaml>>(file.Value) ?? [];
      }
      catch (Exception e)
      {
        Log.Error($"{file.Key}: {e.Message}");
        return SetResult.Failed;
      }
      foreach (var entry in entries)
      {
        if (entry == null || string.IsNullOrWhiteSpace(entry.name))
        {
          Log.Error($"{file.Key}: Alt biome placement rules require a name.");
          return SetResult.Failed;
        }
        var matches = Original.Where(alt => alt.m_name == entry.name).ToArray();
        if (matches.Length != 1)
        {
          Log.Error($"{file.Key}: Server-only placement rules require an existing, unambiguous alt biome name: '{entry.name}'.");
          return SetResult.Failed;
        }
        if (rules.ContainsKey(matches[0]))
        {
          Log.Error($"{file.Key}: Duplicate alt biome placement rules for '{entry.name}'.");
          return SetResult.Failed;
        }
        rules.Add(matches[0], entry);
      }
    }
    if (files.Count > 0 && rules.Count == 0)
    {
      Log.Warning("Failed to load any alt biome placement rules. No changes done.");
      return SetResult.Failed;
    }
    if (rules.Count > 0 && YamlDataManager.BackfillEnabled && AddMissingEntries(rules.Keys.Select(alt => alt.m_name)))
      return SetResult.Rewritten;
    ApplyPlacementRules(rules);
    Log.Info($"Reloading alt biome placement rules ({rules.Count} entries).");
    return SetResult.Applied;
  }

  private static void ApplyPlacementRules(Dictionary<AltBiome, AltBiomePlacementYaml> rules)
  {
    // Sectors retain these objects. Change only their lists, without regenerating the world.
    foreach (var pair in OriginalPlacement)
    {
      rules.TryGetValue(pair.Key, out var rule);
      pair.Key.m_blockLocationNames = (rule?.blockLocationNames ?? pair.Value.blockLocationNames!).ToList();
      pair.Key.m_blockVegetationNames = (rule?.blockVegetationNames ?? pair.Value.blockVegetationNames!).ToList();
    }
  }

  private static AltBiomeYaml ToDefaultData(AltBiome alt)
  {
    var data = ToData(alt);
    if (OriginalPlacement.TryGetValue(alt, out var original))
    {
      data.blockLocationNames = original.blockLocationNames!.Length > 0 ? original.blockLocationNames : null;
      data.blockVegetationNames = original.blockVegetationNames!.Length > 0 ? original.blockVegetationNames : null;
    }
    return data;
  }
}
