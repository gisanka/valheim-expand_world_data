using HarmonyLib;

namespace ExpandWorldData.PersistentEvents;

public static class Patcher
{
  public static void Patch(Harmony harmony)
  {
    var isServer = Helper.IsServer();
    var hasData = PersistentEventManager.HasData;
    var fullData = hasData && !Configuration.ServerOnly;
    var system = typeof(PersistentEventSystem);
    var runtime = typeof(PersistentEventRuntime);
    Patches.Postfix(harmony, hasData, system, "Awake", runtime, nameof(PersistentEventRuntime.Awake));
    Patches.Postfix(harmony, hasData && isServer, system, nameof(PersistentEventSystem.Load), runtime, nameof(PersistentEventRuntime.AfterLoad));
    Patches.Postfix(harmony, fullData && !isServer, system, "RPC_UpdateClientEventsList", runtime, nameof(PersistentEventRuntime.AfterClientUpdate));
    Patches.Prefix(harmony, hasData && isServer, system, "RPC_RequestStartEvent", runtime, nameof(PersistentEventRuntime.BeforeStart));
    Patches.Transpiler(harmony, hasData && isServer, system, "RPC_RequestStartEvent", runtime, nameof(PersistentEventRuntime.SetDurationBeforePublish));
    Patches.Transpiler(harmony, hasData && isServer && Configuration.ServerOnly, typeof(ZoneSystem), nameof(ZoneSystem.SpawnLocationMidGame), runtime, nameof(PersistentEventRuntime.AllowBlueprintLocations));
    Patches.Postfix(harmony, hasData && isServer, system, "UpdateClientEventsList", runtime, nameof(PersistentEventRuntime.AfterListUpdate), argumentTypes: [typeof(long)]);
    Patches.Postfix(harmony, hasData && isServer, system, "Update", runtime, nameof(PersistentEventRuntime.RollRandomEvents));
    Patches.Prefix(harmony, hasData && isServer, typeof(PersistentEventSystem.ObjectSpawnSettings), nameof(PersistentEventSystem.ObjectSpawnSettings.Create), runtime, nameof(PersistentEventRuntime.CreateObjects));
  }
}
