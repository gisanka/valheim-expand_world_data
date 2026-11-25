using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using Data;
using UnityEngine;

namespace ExpandWorldData.Dungeon;

// Never persists across a Set, backfill retry, failed load, or world switch.
// The cache contains copied values only for native rooms; no loaded asset handles.
internal sealed class RoomRebuildCache
{
  private static ConfigEntry<bool>? Enabled;
  [ThreadStatic] private static RoomRebuildCache? Active;
  internal static RoomRebuildCache? Current => Active;
  internal static void Init(ConfigFile config) => Enabled = config.Bind("Data", "Reuse room rebuild metadata", true,
    "Reuse connection snapshots and identical leaf blueprints during a single room rebuild. Applies on the next room load.");
  internal static IDisposable Begin() => new Scope(Enabled?.Value == true);
  private sealed class Scope : IDisposable
  {
    private readonly RoomRebuildCache? Previous = Active;
    private bool Disposed;
    internal Scope(bool enabled) => Active = enabled ? new RoomRebuildCache() : null;
    public void Dispose()
    {
      if (Disposed) return;
      Disposed = true;
      Active = Previous;
    }
  }

  internal readonly Dictionary<DungeonDB.RoomData, ConnectionValues[]> Connections = [];
  // References only to blueprints already in BlueprintCache, and only for this rebuild.
  internal readonly Dictionary<string, LeafBlueprint> Blueprints = new(StringComparer.Ordinal);
  internal sealed class LeafBlueprint(Blueprint blueprint, string[] snapPieces)
  {
    internal readonly Blueprint Blueprint = blueprint;
    internal readonly string[] SnapPieces = (string[])snapPieces.Clone();
  }

  internal readonly struct ConnectionValues
  {
    internal readonly string Name;
    private readonly string Type;
    private readonly Vector3 Position;
    private readonly Quaternion Rotation;
    private readonly bool Entrance, AllowDoor, DoorOnlyIfOtherAllows;

    internal ConnectionValues(RoomConnection connection)
    {
      Name = connection.name;
      Position = connection.transform.localPosition;
      Rotation = connection.transform.localRotation;
      Type = connection.m_type;
      Entrance = connection.m_entrance;
      AllowDoor = connection.m_allowDoor;
      DoorOnlyIfOtherAllows = connection.m_doorOnlyIfOtherAlsoAllowsDoor;
    }

    internal void Apply(RoomConnection connection)
    {
      connection.transform.localPosition = Position;
      connection.transform.localRotation = Rotation;
      connection.m_type = Type;
      connection.m_entrance = Entrance;
      connection.m_allowDoor = AllowDoor;
      connection.m_doorOnlyIfOtherAlsoAllowsDoor = DoorOnlyIfOtherAllows;
    }
  }
}
