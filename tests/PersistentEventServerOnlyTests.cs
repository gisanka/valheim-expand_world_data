using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.Serialization;
using BepInEx.Logging;
using Common;
using ExpandWorldData.Data;
using ExpandWorldData.PersistentEvents;
using HarmonyLib;
using NUnit.Framework;
using Service;

namespace ExpandWorldData.Tests;

[TestFixture, NonParallelizable]
public class PersistentEventServerOnlyTests
{
  private const BindingFlags Static = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
  private const BindingFlags Instance = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
  private static readonly Type Manager = typeof(PersistentEventManager);
  private static readonly FieldInfo OriginalsField = Manager.GetField("Originals")!;
  private static readonly Type EventType = OriginalsField.FieldType.GetGenericArguments()[0];
  private static readonly FieldInfo ExtrasField = typeof(PersistentEventLoader).GetField("Extra")!;
  private static readonly FieldInfo ObjectExtrasField = typeof(PersistentEventLoader).GetField("ObjectExtra")!;
  private static readonly FieldInfo OriginalPrefabsField = Manager.GetField("OriginalPrefabs", Static)!;
  private readonly Dictionary<FieldInfo, object?> previous = [];
  private readonly List<string> errors = [];
  private object first = null!;
  private object second = null!;
  private ManualLogSource logger = null!;
  private bool mode;
  private Harmony? prefabHost;

  [SetUp]
  public void SetUp()
  {
    previous.Clear();
    errors.Clear();
    prefabHost = null;
    foreach (var field in new[] { OriginalsField, ExtrasField, ObjectExtrasField, OriginalPrefabsField, typeof(Log).GetField("Logger", Static)! })
      previous.Add(field, field.GetValue(null));
    logger = new ManualLogSource(nameof(PersistentEventServerOnlyTests));
    logger.LogEvent += (_, args) => { if (args.Level == LogLevel.Error) errors.Add(args.Data.ToString()); };
    Log.Init(logger);
    mode = Configuration.ServerOnly;
    SetMode(true);
    foreach (var name in new[] { "Pending", "Timer" })
    {
      var field = typeof(Refresh).GetField(name, Static)!;
      previous.Add(field, field.GetValue(null));
    }
    Refresh.Clear();
    first = CreateEvent("VanillaEventA");
    second = CreateEvent("VanillaEventB");
    var originals = (IList)Activator.CreateInstance(OriginalsField.FieldType)!;
    originals.Add(first);
    originals.Add(second);
    OriginalsField.SetValue(null, originals);
    ExtrasField.SetValue(null, Activator.CreateInstance(ExtrasField.FieldType));
    ObjectExtrasField.SetValue(null, Activator.CreateInstance(ObjectExtrasField.FieldType));
    OriginalPrefabsField.SetValue(null, Activator.CreateInstance(OriginalPrefabsField.FieldType));
  }

  [TearDown]
  public void TearDown()
  {
    prefabHost?.UnpatchSelf();
    foreach (var pair in previous) pair.Key.SetValue(null, pair.Value);
    SetMode(mode);
    logger.Dispose();
  }

  private static void SetMode(bool enabled) => typeof(Configuration).GetProperty(nameof(Configuration.ServerOnly))!.SetValue(null, enabled);

  private static object CreateEvent(string name)
  {
    var ev = Activator.CreateInstance(EventType)!;
    foreach (var pair in new Dictionary<string, object>
    {
      ["internalName"] = name, ["mapTokenString"] = "$original", ["locationToSpawn"] = "OriginalLocation",
      ["environmentOverride"] = "OriginalEnvironment", ["perBiomeEnvironments"] = true,
      ["minRadius"] = 70f, ["maxRadius"] = 90f, ["maxConcurrent"] = 3, ["hasDuration"] = true,
      ["minDurationInSeconds"] = 20f, ["maxDurationInSeconds"] = 40f,
      ["minDistanceFromCenter"] = 100f, ["maxDistanceFromCenter"] = 2000f,
      ["minHeight"] = 25f, ["maxHeight"] = 900f, ["maxSlope"] = 40f, ["minDistanceFromSimilar"] = 150f
    }) EventType.GetField(pair.Key)!.SetValue(ev, pair.Value);
    var biome = EventType.GetField("biomes")!;
    biome.SetValue(ev, Enum.Parse(biome.FieldType, "Meadows"));
    var effects = EventType.GetField("graphicalEffects")!;
    effects.SetValue(ev, Enum.ToObject(effects.FieldType, 1));
    var groupsField = EventType.GetField("objectsToSpawn")!;
    var groups = (IList)Activator.CreateInstance(groupsField.FieldType)!;
    groupsField.SetValue(ev, groups);
    var group = Activator.CreateInstance(groups.GetType().GetGenericArguments()[0])!;
    group.GetType().GetField("name", Instance)!.SetValue(group, "OriginalGroup");
    groups.Add(group);
    return ev;
  }

  // The production builder runs without PersistentEventSystem's Unity shader initialization.
  private static (SetResult Status, IList Events, IDictionary Extras, IDictionary Objects) Build(Dictionary<string, string> files)
  {
    object?[] args = { files, null, null, null };
    var status = (SetResult)Manager.GetMethod("BuildServerOnly", Static)!.Invoke(null, args)!;
    return (status, (IList)args[1]!, (IDictionary)args[2]!, (IDictionary)args[3]!);
  }

  private static (SetResult Status, IList Events, IDictionary Extras, IDictionary Objects) Build(string yaml)
    => Build(new Dictionary<string, string> { ["events.yaml"] = yaml });

  private static object? Field(object ev, string name) => EventType.GetField(name)!.GetValue(ev);
  private static Dictionary<FieldInfo, object?> Snapshot(object ev) => EventType.GetFields().Where(f => !f.IsStatic).ToDictionary(f => f, f => (object?)f.GetValue(ev));

  [Test]
  public void ReversedYamlKeepsIndicesAndOmittedEntries()
  {
    var result = Build("- name: VanillaEventB\n  maxConcurrent: 1\n- name: VanillaEventA\n  maxConcurrent: 2\n");
    Assert.That(result.Status, Is.EqualTo(SetResult.Applied));
    Assert.That(Field(result.Events[0]!, "internalName"), Is.EqualTo("VanillaEventA"));
    Assert.That(Field(result.Events[1]!, "internalName"), Is.EqualTo("VanillaEventB"));
    Assert.That(Field(result.Events[0]!, "maxConcurrent"), Is.EqualTo(2));
    Assert.That(Field(result.Events[1]!, "maxConcurrent"), Is.EqualTo(1));
    Assert.That(Field(first, "maxConcurrent"), Is.EqualTo(3));
    result = Build("- name: VanillaEventA\n");
    Assert.That(result.Events[1], Is.SameAs(second));
    Assert.That(Field(result.Events[0]!, "maxConcurrent"), Is.EqualTo(3));
  }

  [Test]
  public void MissingFieldsAndIgnoredContentPreserveAllNativeValues()
  {
    var original = Snapshot(first);
    var result = Build("- name: VanillaEventA\n  enabled: false\n"
      + "  mapToken: Changed\n  environment: Changed\n  graphicalEffects: Invalid\n  perBiomeEnvironments: false\n");
    Assert.That(result.Status, Is.EqualTo(SetResult.Applied));
    foreach (var pair in original) Assert.That(pair.Key.GetValue(result.Events[0]), Is.EqualTo(pair.Value), pair.Key.Name);
    var extra = (PersistentEventExtra)result.Extras[result.Events[0]!]!;
    Assert.That(extra.Enabled, Is.False);
    Assert.That(extra.StartCommands, Is.Null);
    Assert.That(extra.EndMessage, Is.Empty);
    Assert.That(extra.OverrideDuration, Is.False);
    Assert.That(Field(result.Events[0]!, "objectsToSpawn"), Is.SameAs(Field(first, "objectsToSpawn")));
    Assert.That((IDictionary)ObjectExtrasField.GetValue(null)!, Is.Empty);
    Assert.That(errors, Is.Empty);
    AssertNoWorldRefresh();
  }

  [Test]
  public void ExplicitZeroFalseAndAltitudeConversionAreApplied()
  {
    var result = Build("- name: VanillaEventA\n  enabled: false\n  maxConcurrent: 0\n  customChance: 0\n  customInterval: 0\n"
      + "  minDistance: 0\n  maxDistance: 0\n  minAltitude: -10\n  maxAltitude: 0\n  maxSlope: 0\n  minDistanceFromSimilar: 0\n");
    Assert.That(result.Status, Is.EqualTo(SetResult.Applied));
    foreach (var name in new[] { "minDistanceFromCenter", "maxDistanceFromCenter", "maxSlope", "minDistanceFromSimilar" })
      Assert.That(Field(result.Events[0]!, name), Is.EqualTo(0f), name);
    Assert.That(Field(result.Events[0]!, "minHeight"), Is.EqualTo(20f));
    Assert.That(Field(result.Events[0]!, "maxHeight"), Is.EqualTo(30f));
    Assert.That(Field(result.Events[0]!, "maxConcurrent"), Is.EqualTo(0));
    var extra = (PersistentEventExtra)result.Extras[result.Events[0]!]!;
    Assert.That(extra.Enabled, Is.False);
    Assert.That(extra.Chance, Is.Zero);
    Assert.That(extra.Interval, Is.Zero);
  }

  [Test]
  public void AutomaticStartSettingsAndKeyListsAreKept()
  {
    var result = Build("- name: VanillaEventA\n  customChance: 75\n  customInterval: 60\n"
      + "  requiredGlobalKeys: [boss_a, 'boss_b, boss_c']\n  forbiddenGlobalKeys: blocked\n");
    Assert.That(result.Status, Is.EqualTo(SetResult.Applied));
    var extra = (PersistentEventExtra)result.Extras[result.Events[0]!]!;
    Assert.That(extra.Chance, Is.EqualTo(75f));
    Assert.That(extra.Interval, Is.EqualTo(60f));
    Assert.That(extra.RequiredKeys, Has.Count.EqualTo(2));
    Assert.That(extra.RequiredKeys![1], Is.EqualTo(new[] { "boss_b", "boss_c" }));
    Assert.That(extra.ForbiddenKeys![0], Is.EqualTo(new[] { "blocked" }));
    var defaults = Build("- name: VanillaEventA\n");
    var defaultExtra = (PersistentEventExtra)defaults.Extras[defaults.Events[0]!]!;
    Assert.That(defaultExtra.Enabled, Is.True);
    Assert.That(defaultExtra.Interval, Is.Zero);
    Assert.That(defaultExtra.RequiredKeys, Is.Null);
  }

  [TestCase("BlackForest, Meadows", 9)]
  [TestCase("None", 0)]
  [TestCase("", 895)]
  [TestCase("Land", 639)]
  public void AcceptsNativeBiomeMasks(string biome, int expected)
  {
    var result = Build($"- name: VanillaEventA\n  biome: '{biome}'\n");
    Assert.That(result.Status, Is.EqualTo(SetResult.Applied), string.Join("\n", errors));
    Assert.That(Convert.ToInt32(Field(result.Events[0]!, "biomes")), Is.EqualTo(expected));
  }

  [TestCase("- name: UnknownEvent\n")]
  [TestCase("- name: VanillaEventB\n")]
  [TestCase("- name: [broken\n")]
  [TestCase("- null\n")]
  [TestCase("- enabled: true\n")]
  [TestCase("- name: VanillaEventA\n  minDistance: 3000\n")]
  [TestCase("- name: VanillaEventA\n  maxAltitude: -20\n")]
  [TestCase("- name: VanillaEventA\n  maxSlope: 91\n")]
  [TestCase("- name: VanillaEventA\n  minDistanceFromSimilar: -1\n")]
  [TestCase("- name: VanillaEventA\n  customInterval: -1\n")]
  [TestCase("- name: VanillaEventA\n  customChance: 101\n")]
  [TestCase("- name: VanillaEventA\n  customChance: .nan\n")]
  [TestCase("- name: VanillaEventA\n  customInterval: .inf\n")]
  [TestCase("- name: VanillaEventA\n  biome: CustomBiome\n")]
  [TestCase("- name: VanillaEventA\n  biome: 128\n")]
  [TestCase("- name: VanillaEventA\n  minRadius: -1\n")]
  [TestCase("- name: VanillaEventA\n  minRadius: 200\n")]
  [TestCase("- name: VanillaEventA\n  minDuration: -1\n")]
  [TestCase("- name: VanillaEventA\n  minDuration: 50\n")]
  [TestCase("- name: VanillaEventA\n  startCommands: [null]\n")]
  public void InvalidReloadProducesNoPartialCandidateOrGlobalChanges(string invalid)
  {
    var original = Snapshot(first);
    var extras = ExtrasField.GetValue(null);
    var result = Build(new Dictionary<string, string>
    {
      ["first.yaml"] = "- name: VanillaEventB\n  maxConcurrent: 2\n",
      ["invalid.yaml"] = invalid
    });
    Assert.That(result.Status, Is.EqualTo(SetResult.Failed));
    Assert.That(result.Events, Is.Empty);
    Assert.That(result.Extras, Is.Empty);
    Assert.That(result.Objects, Is.Empty);
    Assert.That(ExtrasField.GetValue(null), Is.SameAs(extras));
    foreach (var pair in original) Assert.That(pair.Key.GetValue(first), Is.EqualTo(pair.Value), pair.Key.Name);
    Assert.That(errors, Has.Count.EqualTo(1));
    Assert.That(errors[0], Does.Contain("invalid.yaml"));
    AssertNoWorldRefresh();
  }

  [Test]
  public void EmptyDocumentProducesNoCandidate()
  {
    var result = Build("[]");
    Assert.That(result.Status, Is.EqualTo(SetResult.Failed));
    Assert.That(result.Events, Is.Empty);
    Assert.That(result.Extras, Is.Empty);
  }

  [TestCase(true, "")]
  [TestCase(false, "- name: VanillaEventA")]
  public void ServerOnlyRulesAreNotSynchronized(bool serverOnly, string expected)
  {
    SetMode(serverOnly);
    var sync = Manager.GetField("Instance", Static)!.GetValue(null)!;
    var value = sync.GetType().GetMethod("ToSyncValue", Instance)!.Invoke(sync,
      new object[] { new Dictionary<string, string> { ["test.yaml"] = "- name: VanillaEventA" } });
    Assert.That(value, Is.EqualTo(expected));
  }

  private static void AssertNoWorldRefresh()
  {
    foreach (var flag in new[] { Regen.Seed, Regen.Biomes, Regen.Terrain, Regen.Clutter, Regen.Water, Regen.Minimap })
      Assert.That(Refresh.IsPending(flag), Is.False, flag.ToString());
  }

  [Test]
  public void RadiusDurationMessagesAndCommandsAreConfigurable()
  {
    var result = Build("- name: VanillaEventA\n  minRadius: 10\n  maxRadius: 200\n  minDuration: 5\n  maxDuration: 10\n"
      + "  startMessage: Started\n  endMessage: Ended\n  startCommands: ['echo start']\n  endCommands: ['echo end']\n");
    Assert.That(result.Status, Is.EqualTo(SetResult.Applied), string.Join("\n", errors));
    var ev = result.Events[0]!;
    Assert.That(Field(ev, "minRadius"), Is.EqualTo(10f));
    Assert.That(Field(ev, "maxRadius"), Is.EqualTo(200f));
    Assert.That(Field(ev, "minDurationInSeconds"), Is.EqualTo(5f));
    Assert.That(Field(ev, "maxDurationInSeconds"), Is.EqualTo(10f));
    var extra = (PersistentEventExtra)result.Extras[ev]!;
    Assert.That(extra.OverrideDuration, Is.True);
    Assert.That(extra.StartMessage, Is.EqualTo("Started"));
    Assert.That(extra.EndMessage, Is.EqualTo("Ended"));
    Assert.That(extra.StartCommands, Is.EqualTo(new[] { "echo start" }));
    Assert.That(extra.EndCommands, Is.EqualTo(new[] { "echo end" }));
    AssertNoWorldRefresh();
  }

  [Test]
  public void ExplicitEmptyContentRemovesNativeContentWithoutChangingOriginals()
  {
    var result = Build("- name: VanillaEventA\n  location: ''\n  objects: []\n");
    Assert.That(result.Status, Is.EqualTo(SetResult.Applied), string.Join("\n", errors));
    Assert.That(Field(result.Events[0]!, "locationToSpawn"), Is.Empty);
    Assert.That((IList)Field(result.Events[0]!, "objectsToSpawn")!, Is.Empty);
    Assert.That(Field(first, "locationToSpawn"), Is.EqualTo("OriginalLocation"));
    Assert.That((IList)Field(first, "objectsToSpawn")!, Has.Count.EqualTo(1));
    Assert.That(result.Objects, Is.Empty);
  }

  [TestCase(null, null, false)]
  [TestCase(5f, 10f, true)]
  [TestCase(0f, 0f, false)]
  public void DurationIsOnlyAppliedWhenConfiguredInServerOnly(float? min, float? max, bool expected)
  {
    var yaml = "- name: VanillaEventA\n";
    if (min.HasValue) yaml += "  minDuration: " + min.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\n";
    if (max.HasValue) yaml += "  maxDuration: " + max.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\n";
    var result = Build(yaml);
    Assert.That(result.Status, Is.EqualTo(SetResult.Applied));
    var old = ExtrasField.GetValue(null);
    try
    {
      ExtrasField.SetValue(null, result.Extras);
      var method = typeof(PersistentEventRuntime).GetMethod("ShouldSetDuration", Static)!;
      Assert.That(method.Invoke(null, new[] { result.Events[0] }), Is.EqualTo(expected));
      SetMode(false);
      Assert.That(method.Invoke(null, new[] { first }), Is.True, "Normal-mode duration behavior remains available.");
    }
    finally { ExtrasField.SetValue(null, old); }
  }

  private static MethodInfo NativeStart => EventType.DeclaringType!.GetMethod("RPC_RequestStartEvent", Instance)!;

  private static List<CodeInstruction> Transform(IEnumerable<CodeInstruction> instructions)
    => ((IEnumerable<CodeInstruction>)typeof(PersistentEventRuntime).GetMethod("SetDurationBeforePublish", Static)!
      .Invoke(null, new object[] { instructions, NativeStart })!).ToList();

  [Test]
  public void NativeDurationHookRunsAtInsertionBeforeTheFirstBroadcast()
  {
    var original = PatchProcessor.GetOriginalInstructions(NativeStart);
    var transformed = Transform(original);
    var register = typeof(PersistentEventRuntime).GetMethod("RegisterActiveEvent", Static)!;
    var index = transformed.FindIndex(code => code.Calls(register));
    var publish = transformed.FindIndex(code => code.operand is MethodInfo method && method.Name == "UpdateClientEventsList");
    Assert.That(index, Is.GreaterThan(0));
    Assert.That(publish, Is.GreaterThan(index));
    Assert.That(transformed.Count(code => code.Calls(register)), Is.EqualTo(1));
    Assert.That(transformed[index - 1].opcode, Is.EqualTo(OpCodes.Ldloc));
    var local = NativeStart.GetMethodBody()!.LocalVariables.Single(variable => variable.LocalType == EventType);
    Assert.That(Convert.ToInt32(transformed[index - 1].operand), Is.EqualTo(local.LocalIndex));
    Assert.That(transformed.Count, Is.EqualTo(original.Count + 1));
  }

  [TestCase(0)]
  [TestCase(2)]
  public void DurationHookRejectsMissingOrAmbiguousInsertions(int count)
  {
    var original = PatchProcessor.GetOriginalInstructions(NativeStart);
    var insertion = original.Single(code => code.operand is MethodInfo method && method.Name == "Add"
      && method.DeclaringType!.IsGenericType && method.DeclaringType.GetGenericArguments()[0].Name == "ActivePersistentEvent");
    original.Remove(insertion);
    for (var i = 0; i < count; i++) original.Add(new CodeInstruction(insertion));
    var error = Assert.Throws<TargetInvocationException>(() => Transform(original));
    Assert.That(error!.InnerException, Is.TypeOf<InvalidOperationException>());
  }

  [TestCase("CanSpawnInWater")]
  [TestCase("CanNotSpawnInWater")]
  [TestCase("OnlySpawnInWater")]
  public void ObjectPlacementSettingsUseNativeWaterRules(string water)
  {
    var settings = new PersistentEventObjectYaml { prefabs = new[] { "Pickable_Flint" }, water = water };
    Assert.DoesNotThrow(() => Manager.GetMethod("ValidateServerOnlyObjectSettings", Static)!.Invoke(null, new object[] { settings }));
  }

  private object PrepareOriginalPrefab(string name)
  {
    // Replace Unity's liveness check in EWD only; the test host has no native engine.
    prefabHost = new Harmony(nameof(PersistentEventServerOnlyTests) + Guid.NewGuid().ToString("N"));
    var checks = new HarmonyMethod(typeof(PersistentEventServerOnlyTests), nameof(ManagedPrefabChecks));
    foreach (var method in new[] {
      typeof(PersistentEventLoader).GetMethod("GetOriginalPrefab", Static),
      typeof(PersistentEventLoader).GetMethod("FindPrefab", Static),
      Manager.GetMethod("ValidateServerOnlyPrefab", Static),
      typeof(PersistentEventRuntime).GetMethod("FindOriginalObject", Static)
    }.Where(method => method != null)) prefabHost.Patch(method, transpiler: checks);
    foreach (var method in typeof(PersistentEventLoader).GetNestedTypes(Instance).SelectMany(type => type.GetMethods(Instance))
      .Where(method => method.ReturnType == typeof(bool) && method.GetParameters().Length == 1
        && method.GetParameters()[0].ParameterType.FullName == "UnityEngine.GameObject"))
      prefabHost.Patch(method, transpiler: checks);
    var sceneType = EventType.Assembly.GetType("ZNetScene")!;
    var instance = sceneType.GetField("s_instance", Static)!;
    previous.Add(instance, instance.GetValue(null));
    var scene = FormatterServices.GetUninitializedObject(sceneType);
    var named = sceneType.GetField("m_namedPrefabs", Instance)!;
    named.SetValue(scene, Activator.CreateInstance(named.FieldType));
    instance.SetValue(null, scene);
    var loaded = typeof(global::Data.BlueprintCache).GetField("Loaded", Static)!;
    previous.Add(loaded, loaded.GetValue(null));
    loaded.SetValue(null, Activator.CreateInstance(loaded.FieldType));
    ((IDictionary)loaded.GetValue(null)!).Add(name, null);
    var prefab = CreatePrefabHandle();
    ((IDictionary)OriginalPrefabsField.GetValue(null)!).Add(name, prefab);
    var group = ((IList)Field(first, "objectsToSpawn")!)[0]!;
    var prefabs = group.GetType().GetField("prefabs", Instance)!;
    var list = (IList)Activator.CreateInstance(prefabs.FieldType)!;
    list.Add(prefab);
    prefabs.SetValue(group, list);
    return prefab;
  }

  private static IEnumerable<CodeInstruction> ManagedPrefabChecks(IEnumerable<CodeInstruction> instructions)
  {
    foreach (var code in instructions)
    {
      if (code.operand is MethodInfo method && method.DeclaringType?.FullName == "UnityEngine.Object" && method.Name == "op_Implicit")
        code.operand = AccessTools.Method(typeof(PersistentEventServerOnlyTests), nameof(ManagedExists));
      yield return code;
    }
  }

  private static bool ManagedExists(object? value) => value != null;

  private static object CreatePrefabHandle()
  {
    // No native GameObject is created or dereferenced.
    var type = OriginalPrefabsField.FieldType.GetGenericArguments()[1];
    return FormatterServices.GetUninitializedObject(type);
  }

  private static string ObjectsYaml(params string[] names)
  {
    var yaml = Yaml.Serializer().Serialize(new[] { new PersistentEventObjectYaml { prefabs = names } });
    return "- name: VanillaEventA\n  objects:\n" + string.Join("\n", yaml.Split('\n').Select(line => "    " + line));
  }

  [TestCase("NativeEventContainer")]
  [TestCase("OtherEventContainer")]
  public void ExportedObjectSettingsResolveOriginalReferencesAndKeepNativePlacement(string name)
  {
    var prefab = PrepareOriginalPrefab(name);
    var result = Build(ObjectsYaml(name));
    Assert.That(result.Status, Is.EqualTo(SetResult.Applied), string.Join("\n", errors));
    var group = ((IList)Field(result.Events[0]!, "objectsToSpawn")!)[0]!;
    Assert.That(((IList)group.GetType().GetField("prefabs", Instance)!.GetValue(group)!), Has.Count.EqualTo(1));
    Assert.That(((IList)group.GetType().GetField("prefabs", Instance)!.GetValue(group)!)[0], Is.SameAs(prefab));
    Assert.That(result.Objects, Is.Empty, "Native groups must not be replaced by blueprint placement.");
    AssertNoWorldRefresh();
  }

  [Test]
  public void OriginalReferenceIsAlsoResolvedByTheNormalObjectLoader()
  {
    var prefab = PrepareOriginalPrefab("NativeEventContainer");
    SetMode(false);
    var settings = new PersistentEventObjectYaml { prefabs = new[] { "NativeEventContainer" } };
    var group = typeof(PersistentEventLoader).GetMethod("FromObject", Static)!
      .Invoke(null, new[] { settings, (object)"test", ObjectExtrasField.GetValue(null)! })!;
    Assert.That(((IList)group.GetType().GetField("prefabs", Instance)!.GetValue(group)!)[0], Is.SameAs(prefab));
  }

  [Test]
  public void RegisteredPrefabStillTakesPrecedenceOverAnOriginalReference()
  {
    PrepareOriginalPrefab("NativeEventContainer");
    var registered = CreatePrefabHandle();
    var sceneType = EventType.Assembly.GetType("ZNetScene")!;
    var scene = sceneType.GetField("s_instance", Static)!.GetValue(null)!;
    ((IDictionary)sceneType.GetField("m_namedPrefabs", Instance)!.GetValue(scene)!).Add(Parse.Hash(new[] { "NativeEventContainer" }, 0), registered);
    Assert.That(typeof(PersistentEventLoader).GetMethod("FindPrefab", Static)!.Invoke(null, new object[] { "NativeEventContainer" }), Is.SameAs(registered));
  }

  [Test]
  public void OriginalContainerWithCustomDataIsRejectedBeforeDataResolution()
  {
    PrepareOriginalPrefab("NativeEventContainer");
    var result = Build("- name: VanillaEventA\n  objects:\n  - prefabs: [NativeEventContainer]\n    data: CustomData\n");
    Assert.That(result.Status, Is.EqualTo(SetResult.Failed));
    Assert.That(errors.Single(), Does.Contain("NativeEventContainer").And.Contain("network prefab"));
    Assert.That(result.Events, Is.Empty);
    Assert.That(result.Objects, Is.Empty);
  }

  [Test]
  public void UnknownObjectNameStillRejectsTheWholeCandidate()
  {
    PrepareOriginalPrefab("NativeEventContainer");
    ((IDictionary)typeof(global::Data.BlueprintCache).GetField("Loaded", Static)!.GetValue(null)!).Add("UnknownObject", null);
    var result = Build(ObjectsYaml("UnknownObject"));
    Assert.That(result.Status, Is.EqualTo(SetResult.Failed));
    Assert.That(errors.Single(), Does.Contain("UnknownObject").And.Contain("was not found"));
    Assert.That(result.Events, Is.Empty);
  }

  [Test]
  public void OriginalContainersCanBeMixedWithBlueprintAlternatives()
  {
    var prefab = PrepareOriginalPrefab("NativeEventContainer");
    var blueprint = new global::Data.Blueprint("ExtraBlueprint");
    ((IDictionary)typeof(global::Data.BlueprintCache).GetField("Loaded", Static)!.GetValue(null)!).Add(blueprint.Name, blueprint);
    var result = Build(ObjectsYaml("NativeEventContainer", "ExtraBlueprint"));
    Assert.That(result.Status, Is.EqualTo(SetResult.Applied), string.Join("\n", errors));
    Assert.That(result.Objects, Has.Count.EqualTo(1));
    var group = ((IList)Field(result.Events[0]!, "objectsToSpawn")!)[0]!;
    Assert.That(((IList)group.GetType().GetField("prefabs", Instance)!.GetValue(group)!)[0], Is.SameAs(prefab));
    Assert.That(((PersistentEventObjectExtra)result.Objects[group]!).Prefabs,
      Is.EqualTo(new[] { "NativeEventContainer", "ExtraBlueprint" }));
  }

  [TestCase("NativeEventContainer", "network prefab")]
  [TestCase("ExtraBlueprint", "Recursive")]
  public void BlueprintContentsDoNotBypassNetworkOrRecursionChecks(string childName, string message)
  {
    PrepareOriginalPrefab("NativeEventContainer");
    var blueprint = new global::Data.Blueprint("ExtraBlueprint");
    var child = (global::Data.BlueprintObject)FormatterServices.GetUninitializedObject(typeof(global::Data.BlueprintObject));
    child.Prefab = childName;
    child.Chance = 1f;
    blueprint.Objects.Add(child);
    ((IDictionary)typeof(global::Data.BlueprintCache).GetField("Loaded", Static)!.GetValue(null)!).Add(blueprint.Name, blueprint);
    var result = Build(ObjectsYaml("ExtraBlueprint"));
    Assert.That(result.Status, Is.EqualTo(SetResult.Failed));
    Assert.That(errors.Single(), Does.Contain(message));
    Assert.That(result.Events, Is.Empty);
    Assert.That(result.Objects, Is.Empty);
  }

  [Test]
  public void ExtendedPlacementContainsSeparateNativeAndBlueprintCreationPaths()
  {
    var create = typeof(PersistentEventRuntime).GetMethod("CreateObjects", Static)!;
    var instructions = PatchProcessor.GetOriginalInstructions(create);
    var lookup = instructions.FindIndex(code => code.operand is MethodInfo method && method.Name == "FindOriginalObject");
    var instantiate = instructions.FindIndex(code => code.operand is MethodInfo method
      && method.DeclaringType?.FullName == "UnityEngine.Object" && method.Name == "Instantiate");
    var blueprint = instructions.FindIndex(code => code.operand is MethodInfo method && method.Name == "BPO");
    Assert.That(lookup, Is.GreaterThan(0));
    Assert.That(instantiate, Is.GreaterThan(lookup));
    Assert.That(blueprint, Is.GreaterThan(instantiate));
  }

  private static MethodInfo MidGameLocation => EventType.Assembly.GetType("ZoneSystem")!.GetMethod("SpawnLocationMidGame", Instance)!;

  [Test]
  public void BlueprintLocationHookPreservesNativeRegistrationAndOtherChecks()
  {
    var original = PatchProcessor.GetOriginalInstructions(MidGameLocation);
    var expectedCount = original.Count;
    var transformed = ((IEnumerable<CodeInstruction>)typeof(PersistentEventRuntime).GetMethod("AllowBlueprintLocations", Static)!
      .Invoke(null, new object[] { original })!).ToList();
    var available = typeof(PersistentEventRuntime).GetMethod("IsEventLocationAvailable", Static)!;
    Assert.That(transformed.Count, Is.EqualTo(expectedCount));
    Assert.That(transformed.Count(code => code.Calls(available)), Is.EqualTo(1));
    Assert.That(transformed.Count(code => code.operand is MethodInfo method && method.Name == "RegisterLocation"), Is.EqualTo(1));
    Assert.That(transformed.Count(code => code.operand is MethodInfo method && method.Name == "IsServer"), Is.EqualTo(1));
  }

  [TestCase(0)]
  [TestCase(2)]
  public void BlueprintLocationHookRejectsMissingOrAmbiguousChecks(int count)
  {
    var original = PatchProcessor.GetOriginalInstructions(MidGameLocation);
    var check = original.Single(code => code.operand is MethodInfo method && method.Name == "get_IsValid"
      && method.DeclaringType!.Name.StartsWith("SoftReference"));
    original.Remove(check);
    for (var i = 0; i < count; i++) original.Add(new CodeInstruction(check));
    var error = Assert.Throws<TargetInvocationException>(() => typeof(PersistentEventRuntime).GetMethod("AllowBlueprintLocations", Static)!
      .Invoke(null, new object[] { original }));
    Assert.That(error!.InnerException, Is.TypeOf<InvalidOperationException>());
  }

  [TestCase(-1, 1, 90f, "CanSpawnInWater")]
  [TestCase(2, 1, 90f, "CanSpawnInWater")]
  [TestCase(0, 1, 91f, "CanSpawnInWater")]
  [TestCase(0, 1, 90f, "UnknownWaterRule")]
  public void InvalidObjectPlacementIsRejectedBeforeAssetResolution(int min, int max, float slope, string water)
  {
    var settings = new PersistentEventObjectYaml { prefabs = new[] { "Pickable_Flint" }, minCount = min, maxCount = max, maxSlope = slope, water = water };
    var error = Assert.Throws<TargetInvocationException>(() => Manager.GetMethod("ValidateServerOnlyObjectSettings", Static)!.Invoke(null, new object[] { settings }));
    Assert.That(error!.InnerException, Is.TypeOf<ArgumentException>());
  }
}
