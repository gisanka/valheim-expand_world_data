using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using ExpandWorldData.Data;
using SoftReferenceableAssets;
using UnityEngine;

namespace ExpandWorldData.PersistentEvents;

public static partial class PersistentEventRuntime
{
  internal static IEnumerable<CodeInstruction> SetDurationBeforePublish(IEnumerable<CodeInstruction> instructions, MethodBase __originalMethod)
  {
    var code = instructions.ToList();
    var add = AccessTools.Method(typeof(List<PersistentEventSystem.ActivePersistentEvent>), "Add");
    var matches = code.Select((instruction, index) => (instruction, index)).Where(pair => pair.instruction.Calls(add)).ToArray();
    var sources = __originalMethod.GetMethodBody()!.LocalVariables.Where(local => local.LocalType == typeof(PersistentEventSystem.PersistentEvent)).ToArray();
    if (matches.Length != 1 || sources.Length != 1 || matches[0].instruction.blocks.Count != 0)
      throw new InvalidOperationException("Persistent event start: Expected one active-list insertion and one source definition local.");
    var target = matches[0];
    var loadSource = new CodeInstruction(OpCodes.Ldloc, (short)sources[0].LocalIndex);
    loadSource.labels.AddRange(target.instruction.labels);
    target.instruction.labels.Clear();
    target.instruction.opcode = OpCodes.Call;
    target.instruction.operand = AccessTools.Method(typeof(PersistentEventRuntime), nameof(RegisterActiveEvent));
    code.Insert(target.index, loadSource);
    return code;
  }

  internal static void RegisterActiveEvent(List<PersistentEventSystem.ActivePersistentEvent> list,
    PersistentEventSystem.ActivePersistentEvent active, PersistentEventSystem.PersistentEvent source)
  {
    if (ShouldSetDuration(source))
    {
      active.startTime = ZNet.instance.GetTimeSeconds();
      active.duration = UnityEngine.Random.Range(source.minDurationInSeconds, source.maxDurationInSeconds);
    }
    list.Add(active);
  }

  private static bool ShouldSetDuration(PersistentEventSystem.PersistentEvent source)
    => source.hasDuration && (!Configuration.ServerOnly
      || PersistentEventLoader.Extra.TryGetValue(source, out var extra) && extra.OverrideDuration);

  internal static IEnumerable<CodeInstruction> AllowBlueprintLocations(IEnumerable<CodeInstruction> instructions)
  {
    var code = instructions.ToList();
    var valid = AccessTools.PropertyGetter(typeof(SoftReference<GameObject>), nameof(SoftReference<GameObject>.IsValid));
    var matches = code.Where(instruction => instruction.Calls(valid)).ToArray();
    if (matches.Length != 1)
      throw new InvalidOperationException("Persistent event location: Expected one prefab availability check.");
    matches[0].opcode = OpCodes.Call;
    matches[0].operand = AccessTools.Method(typeof(PersistentEventRuntime), nameof(IsEventLocationAvailable));
    return code;
  }

  private static bool IsEventLocationAvailable(ref SoftReference<GameObject> prefab)
    => prefab.IsValid || BlueprintManager.Has(prefab.name);
}
