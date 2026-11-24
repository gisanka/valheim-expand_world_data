using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BepInEx.Logging;
using Common;
using ExpandWorldData.AltBiomes;
using ExpandWorldData.Data;
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

  [SetUp]
  public void SetUp()
  {
    previous.Clear();
    errors.Clear();
    foreach (var name in new[] { "Original", "Active", "OriginalPlacement" })
    {
      var field = Manager.GetField(name, Static)!;
      previous.Add(field, field.GetValue(null));
    }
    previous.Add(NativeList, NativeList.GetValue(null));
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
    foreach (var pair in previous) pair.Key.SetValue(null, pair.Value);
    typeof(YamlDataManager).GetField("Customized", Instance)!.SetValue(sync, customized);
    SetMode(mode);
    logger.Dispose();
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
