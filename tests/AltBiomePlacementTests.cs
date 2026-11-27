using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using BepInEx.Configuration;
using BepInEx.Logging;
using Common;
using ExpandWorldData.AltBiomes;
using ExpandWorldData.Data;
using HarmonyLib;
using NUnit.Framework;
using Service;

namespace ExpandWorldData.Tests;

[TestFixture, NonParallelizable]
public class AltBiomePlacementTests
{
  private const BindingFlags Static = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
  private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
  private static readonly Type Manager = typeof(AltBiomeManager);
  private static readonly Type AltType = Manager.GetField("Original", Static)!.FieldType.GetGenericArguments()[0];
  private static readonly FieldInfo NativeList = AltType.Assembly.GetType("AltBiomeList")!.GetField("m_altBiomes", Static)!;
  private readonly Dictionary<FieldInfo, object?> previous = [];
  private readonly List<string> errors = [];
  private object sync = null!;
  private object first = null!;
  private object second = null!;
  private ManualLogSource logger = null!;
  private bool mode;
  private object? customized;
  private string directory = null!;
  private Harmony? harmony;

  [SetUp]
  public void SetUp()
  {
    previous.Clear();
    errors.Clear();
    harmony = null;
    foreach (var name in new[] { "Original", "Active", "OriginalPlacement" })
    {
      var field = Manager.GetField(name, Static)!;
      previous.Add(field, field.GetValue(null));
    }
    previous.Add(NativeList, NativeList.GetValue(null));
    foreach (var name in new[] { "configDataMigration", "configDataAltBiomes", "configSplitDataPerMod", "valueAltBiomeData" })
    {
      var field = typeof(Configuration).GetField(name, Static)!;
      previous.Add(field, field.GetValue(null));
    }
    var baseDirectory = typeof(Yaml).GetField(nameof(Yaml.BaseDirectory))!;
    previous.Add(baseDirectory, baseDirectory.GetValue(null));
    var filePath = Manager.GetField(nameof(AltBiomeManager.FilePath))!;
    previous.Add(filePath, filePath.GetValue(null));
    var ignoredWrites = typeof(Yaml).GetField("IgnoredWrites", Static)!;
    previous.Add(ignoredWrites, ignoredWrites.GetValue(null));
    ignoredWrites.SetValue(null, Activator.CreateInstance(ignoredWrites.FieldType));
    directory = Path.Combine(Path.GetTempPath(), nameof(AltBiomePlacementTests), Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);
    Yaml.BaseDirectory = directory;
    filePath.SetValue(null, Path.Combine(directory, "expand_altbiomes.yaml"));
    var config = new ConfigFile(Path.Combine(directory, "test.cfg"), false) { SaveOnConfigSet = false };
    Configuration.configDataMigration = config.Bind("Test", "Migration", false);
    Configuration.configDataAltBiomes = config.Bind("Test", "Alt biomes", true);
    Configuration.configSplitDataPerMod = config.Bind("Test", "Split per mod", false);
    var synced = typeof(Configuration).GetField("valueAltBiomeData", Static)!;
    synced.SetValue(null, FormatterServices.GetUninitializedObject(synced.FieldType));
    Configuration.valueAltBiomeData.Value = "previous";
    var logField = typeof(Log).GetField("Logger", Static)!;
    previous.Add(logField, logField.GetValue(null));
    logger = new ManualLogSource(nameof(AltBiomePlacementTests));
    logger.LogEvent += (_, args) => { if (args.Level == LogLevel.Error) errors.Add(args.Data.ToString()); };
    Log.Init(logger);
    mode = Configuration.ServerOnly;
    SetMode(true);
    sync = Manager.GetField("Instance", Static)!.GetValue(null)!;
    var customField = typeof(YamlDataManager).GetField("Customized", Instance)!;
    customized = customField.GetValue(sync);
    customField.SetValue(sync, false);
    foreach (var name in new[] { "Pending", "Timer" })
    {
      var field = typeof(Refresh).GetField(name, Static)!;
      previous.Add(field, field.GetValue(null));
    }
    Refresh.Clear();
    first = CreateAlt("VanillaRegion", "OriginalLocation", "OriginalVegetation");
    second = CreateAlt("OtherRegion", "OtherLocation", "OtherVegetation");
    var native = (IList)Activator.CreateInstance(NativeList.FieldType)!;
    native.Add(first);
    native.Add(second);
    NativeList.SetValue(null, native);
    AltBiomeManager.Initialize();
  }

  [TearDown]
  public void TearDown()
  {
    harmony?.UnpatchSelf();
    foreach (var pair in previous) pair.Key.SetValue(null, pair.Value);
    typeof(YamlDataManager).GetField("Customized", Instance)!.SetValue(sync, customized);
    SetMode(mode);
    logger.Dispose();
    Directory.Delete(directory, true);
  }

  private static void SetMode(bool enabled) => typeof(Configuration).GetProperty(nameof(Configuration.ServerOnly))!.SetValue(null, enabled);

  private static object CreateAlt(string name, string location, string vegetation)
  {
    var alt = Activator.CreateInstance(AltType)!;
    AltType.GetField("m_name")!.SetValue(alt, name);
    ((IList)AltType.GetField("m_blockLocationNames")!.GetValue(alt)!).Add(location);
    ((IList)AltType.GetField("m_blockVegetationNames")!.GetValue(alt)!).Add(vegetation);
    return alt;
  }

  private SetResult Apply(Dictionary<string, string> files) => (SetResult)typeof(YamlDataManager)
    .GetMethod("Apply", Instance)!.Invoke(sync, new object[] { files })!;

  private SetResult Apply(string yaml) => Apply(new Dictionary<string, string> { ["expand_altbiomes.yaml"] = yaml });

  private static object? Field(object alt, string name) => AltType.GetField(name)!.GetValue(alt);

  private static string[] Names(object alt, string field) => ((IEnumerable)Field(alt, field)!).Cast<string>().ToArray();

  [Test]
  public void AppliesOnlyBlocklistsAndPreservesNativeObjectsAndWorldData()
  {
    var native = NativeList.GetValue(null);
    var otherFields = AltType.GetFields().Where(f => !f.IsStatic && f.Name != "m_blockLocationNames" && f.Name != "m_blockVegetationNames")
      .ToDictionary(f => f, f => f.GetValue(first));
    var yaml = "- name: VanillaRegion\n  blockLocationNames: [CustomLocation]\n  blockVegetationNames: [CustomVegetation]\n"
      + "  enabled: false\n  biome: NotABiome\n  chance: invalid\n  forceMusic: CustomMusic\n"
      + "  spawn: ignored\n  terrainTextureOverride: NotABiome\n  incompatibleAltBiomes: [OtherRegion]\n";

    Assert.That(Apply(yaml), Is.EqualTo(SetResult.Applied));
    Assert.That(Names(first, "m_blockLocationNames"), Is.EqualTo(new[] { "CustomLocation" }));
    Assert.That(Names(first, "m_blockVegetationNames"), Is.EqualTo(new[] { "CustomVegetation" }));
    Assert.That(NativeList.GetValue(null), Is.SameAs(native));
    Assert.That(((IList)native!)[0], Is.SameAs(first));
    foreach (var pair in otherFields) Assert.That(pair.Key.GetValue(first), Is.EqualTo(pair.Value), pair.Key.Name);
    Assert.That(Names(second, "m_blockLocationNames"), Is.EqualTo(new[] { "OtherLocation" }));
    Assert.That(errors, Is.Empty, "Ignored fields must not be parsed as full alt-biome data.");
    AssertNoWorldRefresh();
  }

  [Test]
  public void ReloadRestoresOmittedFieldsAndOmittedEntriesFromVanilla()
  {
    Assert.That(Apply("- name: VanillaRegion\n  blockLocationNames: [Changed]\n  blockVegetationNames: [Changed]\n"), Is.EqualTo(SetResult.Applied));
    Assert.That(Apply("- name: VanillaRegion\n  blockLocationNames: [Next]\n"), Is.EqualTo(SetResult.Applied));
    Assert.That(Names(first, "m_blockLocationNames"), Is.EqualTo(new[] { "Next" }));
    Assert.That(Names(first, "m_blockVegetationNames"), Is.EqualTo(new[] { "OriginalVegetation" }));
    Assert.That(Apply("- name: OtherRegion\n"), Is.EqualTo(SetResult.Applied));
    AssertOriginalLists();
    AssertNoWorldRefresh();
  }

  [Test]
  public void ExplicitEmptyListRemovesVanillaBlocks()
  {
    Assert.That(Apply("- name: VanillaRegion\n  blockLocationNames: []\n"), Is.EqualTo(SetResult.Applied));
    Assert.That(Names(first, "m_blockLocationNames"), Is.Empty);
    Assert.That(Names(first, "m_blockVegetationNames"), Is.EqualTo(new[] { "OriginalVegetation" }));
  }

  [TestCase("- name: UnknownRegion\n  blockLocationNames: [Invalid]\n")]
  [TestCase("- name: VanillaRegion\n- name: VanillaRegion\n")]
  [TestCase("- name: [broken\n")]
  [TestCase("- blockLocationNames: [Invalid]\n")]
  [TestCase("- null\n")]
  public void FailedReloadKeepsPreviousLists(string invalid)
  {
    Assert.That(Apply("- name: VanillaRegion\n  blockLocationNames: [Previous]\n"), Is.EqualTo(SetResult.Applied));
    Assert.That(Apply(new Dictionary<string, string>
    {
      ["first.yaml"] = "- name: OtherRegion\n  blockVegetationNames: [MustNotApply]\n",
      ["invalid.yaml"] = invalid
    }), Is.EqualTo(SetResult.Failed));
    Assert.That(Names(first, "m_blockLocationNames"), Is.EqualTo(new[] { "Previous" }));
    Assert.That(Names(second, "m_blockVegetationNames"), Is.EqualTo(new[] { "OtherVegetation" }));
    AssertNoWorldRefresh();
  }

  [Test]
  public void EmptyDocumentKeepsPreviousRules()
  {
    Assert.That(Apply("- name: VanillaRegion\n  blockLocationNames: [Previous]\n"), Is.EqualTo(SetResult.Applied));
    Assert.That(Apply("[]"), Is.EqualTo(SetResult.Failed));
    Assert.That(Names(first, "m_blockLocationNames"), Is.EqualTo(new[] { "Previous" }));
  }

  [Test]
  public void DuplicateAcrossFilesRejectsWholeReload()
  {
    Assert.That(Apply(new Dictionary<string, string>
    {
      ["first.yaml"] = "- name: VanillaRegion\n  blockLocationNames: [Changed]\n",
      ["second.yaml"] = "- name: VanillaRegion\n"
    }), Is.EqualTo(SetResult.Failed));
    AssertOriginalLists();
  }

  [Test]
  public void DisablingRestoresOriginalListsWithoutWorldRefresh()
  {
    Assert.That(Apply("- name: VanillaRegion\n  blockLocationNames: []\n  blockVegetationNames: []\n"), Is.EqualTo(SetResult.Applied));
    // The shared loader passes an empty file set when data is disabled.
    Assert.That(Apply(new Dictionary<string, string>()), Is.EqualTo(SetResult.Applied));
    AssertOriginalLists();
    AssertNoWorldRefresh();
  }

  [Test]
  public void DefaultConfigKeepsOriginalBlocklistsAfterReload()
  {
    Assert.That(Apply("- name: VanillaRegion\n  blockLocationNames: [Changed]\n"), Is.EqualTo(SetResult.Applied));
    var data = (AltBiomeYaml)Manager.GetMethod("ToDefaultData", Static)!.Invoke(null, new[] { first })!;
    Assert.That(data.blockLocationNames, Is.EqualTo(new[] { "OriginalLocation" }));
    Assert.That(data.blockVegetationNames, Is.EqualTo(new[] { "OriginalVegetation" }));
  }

  [TestCase(true, "")]
  [TestCase(false, "- name: VanillaRegion")]
  public void OnlyNormalModeSynchronizesAltBiomeData(bool serverOnly, string expected)
  {
    SetMode(serverOnly);
    var files = new Dictionary<string, string> { ["test.yaml"] = "- name: VanillaRegion" };
    var value = sync.GetType().GetMethod("ToSyncValue", Instance)!.Invoke(sync, new object[] { files });
    Assert.That(value, Is.EqualTo(expected));
  }

  [Test]
  public void CleanupRestoresBlocklistsBeforeReleasingNativeObjects()
  {
    Assert.That(Apply("- name: VanillaRegion\n  blockLocationNames: []\n"), Is.EqualTo(SetResult.Applied));
    AltBiomeManager.CleanUp();
    AssertOriginalLists();
    Assert.That((IList)NativeList.GetValue(null)!, Is.Empty);
  }

  private void EnableBackfill()
  {
    Configuration.configDataMigration.Value = true;
    // Replace only the native host check; parsing, writing and rereading use the production path.
    harmony = new Harmony(nameof(AltBiomePlacementTests) + Guid.NewGuid().ToString("N"));
    harmony.Patch(AccessTools.Method(typeof(Helper), nameof(Helper.IsServer)),
      prefix: new HarmonyMethod(typeof(AltBiomePlacementTests), nameof(AsServer)));
    foreach (var name in new[] { "PrefabToBundleMapping", "BundleToModMapping" })
    {
      var field = typeof(AssetTracker).GetField(name, Static)!;
      previous.Add(field, field.GetValue(null));
      field.SetValue(null, new Dictionary<string, string>());
    }
    ((Dictionary<string, string>)typeof(AssetTracker).GetField("PrefabToBundleMapping", Static)!.GetValue(null)!)
      .Add("otherregion", "sample_bundle");
    ((Dictionary<string, string>)typeof(AssetTracker).GetField("BundleToModMapping", Static)!.GetValue(null)!)
      .Add("sample_bundle", "Sample Mod");
  }

  private static bool AsServer(ref bool __result)
  {
    __result = true;
    return false;
  }

  [TestCase(false)]
  [TestCase(true)]
  public void ReadConfigsBackfillsMissingNativeAltBiomesAndRereadsWithoutDuplicates(bool splitPerMod)
  {
    EnableBackfill();
    Configuration.configSplitDataPerMod.Value = splitPerMod;
    const string yaml = "# Keep this comment\n- name: VanillaRegion\n  blockLocationNames: [CustomLocation]\n  chance: invalid\n  spawn: ignored\n";
    File.WriteAllText(AltBiomeManager.FilePath, yaml);
    var native = NativeList.GetValue(null);

    AltBiomeManager.ReadConfigs();

    var files = Directory.GetFiles(directory, AltBiomeManager.Pattern).ToDictionary(path => path, File.ReadAllText);
    var entries = files.Values.SelectMany(text => Yaml.DeserializerUnSafe().Deserialize<List<AltBiomePlacementYaml>>(text)).ToList();
    Assert.That(entries.Select(entry => entry.name), Is.EquivalentTo(new[] { "VanillaRegion", "OtherRegion" }));
    Assert.That(File.ReadAllText(AltBiomeManager.FilePath), Does.StartWith(yaml));
    Assert.That(File.Exists(Path.Combine(directory, "expand_altbiomes_sample_mod.yaml")), Is.EqualTo(splitPerMod));
    Assert.That(Names(first, "m_blockLocationNames"), Is.EqualTo(new[] { "CustomLocation" }));
    Assert.That(Names(second, "m_blockVegetationNames"), Is.EqualTo(new[] { "OtherVegetation" }));
    Assert.That(NativeList.GetValue(null), Is.SameAs(native));
    Assert.That(((IList)native!)[1], Is.SameAs(second));
    Assert.That(Configuration.valueAltBiomeData.Value, Is.Empty);
    Assert.That(errors, Is.Empty);
    AssertNoWorldRefresh();

    AltBiomeManager.ReadConfigs();
    Assert.That(Directory.GetFiles(directory, AltBiomeManager.Pattern).ToDictionary(path => path, File.ReadAllText), Is.EquivalentTo(files));
    AssertNoWorldRefresh();
  }

  [Test]
  public void BackfillRewriteKeepsCurrentRulesUntilReread()
  {
    EnableBackfill();
    const string yaml = "- name: VanillaRegion\n  blockLocationNames: [Changed]\n";
    File.WriteAllText(AltBiomeManager.FilePath, yaml);

    Assert.That(Apply(yaml), Is.EqualTo(SetResult.Rewritten));
    AssertOriginalLists();
    AssertNoWorldRefresh();

    AltBiomeManager.ReadConfigs();
    Assert.That(Names(first, "m_blockLocationNames"), Is.EqualTo(new[] { "Changed" }));
    Assert.That(errors, Is.Empty);
  }

  [Test]
  public void MissingPreviouslyCustomizedEntryIsBackfilledWithNativeBlocklists()
  {
    Assert.That(Apply("- name: OtherRegion\n  blockLocationNames: [Changed]\n"), Is.EqualTo(SetResult.Applied));
    EnableBackfill();
    File.WriteAllText(AltBiomeManager.FilePath, "- name: VanillaRegion\n");

    AltBiomeManager.ReadConfigs();

    var entries = Yaml.Deserializer().Deserialize<List<AltBiomeYaml>>(File.ReadAllText(AltBiomeManager.FilePath));
    Assert.That(entries.Single(entry => entry.name == "OtherRegion").blockLocationNames, Is.EqualTo(new[] { "OtherLocation" }));
    AssertOriginalLists();
    AssertNoWorldRefresh();
  }

  [Test]
  public void NormalModeUsesTheSameDefaultBackfillWriter()
  {
    EnableBackfill();
    SetMode(false);
    File.WriteAllText(AltBiomeManager.FilePath, "- name: VanillaRegion\n  chance: 0.5\n");
    var backfill = Manager.GetMethod("AddMissingEntries", Static)!;

    Assert.That(backfill.Invoke(null, new object[] { new[] { "VanillaRegion" } }), Is.True);

    var entries = Yaml.Deserializer().Deserialize<List<AltBiomeYaml>>(File.ReadAllText(AltBiomeManager.FilePath));
    Assert.That(entries.Select(entry => entry.name), Is.EquivalentTo(new[] { "VanillaRegion", "OtherRegion" }));
    Assert.That(entries.Single(entry => entry.name == "VanillaRegion").chance, Is.EqualTo(0.5f));
    Assert.That(backfill.Invoke(null, new object[] { entries.Select(entry => entry.name) }), Is.False);
    AssertOriginalLists();
    AssertNoWorldRefresh();
    Assert.That(errors, Is.Empty);
  }

  [Test]
  public void DisablingAltBiomeDataRestoresDefaultsWithoutBackfilling()
  {
    Assert.That(Apply("- name: VanillaRegion\n  blockLocationNames: [Changed]\n"), Is.EqualTo(SetResult.Applied));
    EnableBackfill();
    const string yaml = "- name: VanillaRegion\n";
    File.WriteAllText(AltBiomeManager.FilePath, yaml);
    Configuration.configDataAltBiomes.Value = false;

    AltBiomeManager.ReadConfigs();

    Assert.That(File.ReadAllText(AltBiomeManager.FilePath), Is.EqualTo(yaml));
    AssertOriginalLists();
    AssertNoWorldRefresh();
  }

  [TestCase(false)]
  [TestCase(true)]
  public void DisabledMigrationOrInvalidRulesDoNotBackfill(bool invalid)
  {
    EnableBackfill();
    Configuration.configDataMigration.Value = invalid;
    var yaml = "- name: " + (invalid ? "UnknownRegion" : "VanillaRegion") + "\n";
    File.WriteAllText(AltBiomeManager.FilePath, yaml);

    AltBiomeManager.ReadConfigs();

    Assert.That(File.ReadAllText(AltBiomeManager.FilePath), Is.EqualTo(yaml));
    Assert.That(Directory.GetFiles(directory, AltBiomeManager.Pattern), Has.Length.EqualTo(1));
    Assert.That(errors.Count, invalid ? Is.GreaterThan(0) : Is.EqualTo(0));
    AssertOriginalLists();
    AssertNoWorldRefresh();
  }

  private void AssertOriginalLists()
  {
    Assert.That(Names(first, "m_blockLocationNames"), Is.EqualTo(new[] { "OriginalLocation" }));
    Assert.That(Names(first, "m_blockVegetationNames"), Is.EqualTo(new[] { "OriginalVegetation" }));
  }

  private static void AssertNoWorldRefresh()
  {
    foreach (var flag in new[] { Regen.Seed, Regen.Biomes, Regen.Terrain, Regen.Clutter, Regen.Water, Regen.Minimap })
      Assert.That(Refresh.IsPending(flag), Is.False, flag.ToString());
  }
}
