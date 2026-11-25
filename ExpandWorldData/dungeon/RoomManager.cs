using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using Service;
using UnityEngine;
using Common;
using ExpandWorldData.Data;

namespace ExpandWorldData.Dungeon;

public class RoomManager
{
  public static string FileName = "expand_rooms.yaml";
  public static string FilePath = Path.Combine(Yaml.BaseDirectory, FileName);
  public static string Pattern = "expand_rooms*.yaml";
  private static readonly List<RoomYaml> ExtraRoomYamls = [];

  private class Config : YamlDataManager
  {
    protected override string FilePath => RoomManager.FilePath;
    protected override string Pattern => RoomManager.Pattern;
    protected override bool DataEnabled => Configuration.DataRooms;
    protected override SetResult Set(Dictionary<string, string> files) => RoomManager.Set(files);
    protected override void WriteDefaultConfig() => ToFile();
  }
  private static readonly Config Instance = new();

  public static void AddRoom(RoomYaml yaml)
  {
    ExtraRoomYamls.Add(yaml);
  }


  private static List<DungeonDB.RoomData> DefaultEntries = [];


  // For finding rooms with wrong case.
  private static Dictionary<string, string> RoomNames = [];
  public static List<string> ParseRooms(string names) => DataManager.ToList(names).Select(s => RoomNames.TryGetValue(s, out var name) ? name : s).ToList();
  public static void Initialize()
  {
    DefaultEntries.Clear();
    RoomSpawning.Prefabs.Clear();
    CreatedObjects.ForEach(UnityEngine.Object.Destroy);
    ClearData();
    if (Helper.IsServer())
      SetDefaultEntries();
    ReadConfigs();
  }
  private static void ToFile()
  {
    var data = DefaultEntries.Select(ToData).Where(d => d != null).ToList();
    if (ExtraRoomYamls.Count > 0)
      data.AddRange(ExtraRoomYamls);
    Save(data, false);
  }

  // Hard coded to not go through the enum patch.
  private static readonly Dictionary<string, Room.Theme> DefaultNameToTheme = new() {
    {"Crypt", Room.Theme.Crypt},
    {"SunkenCrypt", Room.Theme.SunkenCrypt},
    {"Cave", Room.Theme.Cave},
    {"ForestCrypt", Room.Theme.ForestCrypt},
    {"GoblinCamp", Room.Theme.GoblinCamp},
    {"MeadowsVillage", Room.Theme.MeadowsVillage},
    {"MeadowsFarm", Room.Theme.MeadowsFarm},
    {"DvergerTown", Room.Theme.DvergerTown},
    {"DvergerBoss", Room.Theme.DvergerBoss},
    {"ForestCryptHildir", Room.Theme.ForestCryptHildir},
    {"CaveHildir", Room.Theme.CaveHildir},
    {"PlainsFortHildir", Room.Theme.PlainsFortHildir},
    {"AshlandRuins", Room.Theme.AshlandRuins},
    {"FortressRuins", Room.Theme.FortressRuins},
    {"Hole", Room.Theme.Hole},
    {"NorthVillage", Room.Theme.NorthVillage},
    {"MorkHalla", Room.Theme.MorkHalla}
  };

  // For extra custom room themes.
  private static Dictionary<string, Room.Theme> CreateNameToTheme()
  {
    var themes = DefaultNameToTheme.ToDictionary(kvp => kvp.Key.ToLowerInvariant(), kvp => kvp.Value);
    foreach (var theme in themes.Values.Distinct().ToArray())
      themes[((int)theme).ToString()] = theme;
    return themes;
  }

  public static Dictionary<string, Room.Theme> NameToTheme = CreateNameToTheme();
  public static Dictionary<Room.Theme, string> ThemeToName = DefaultNameToTheme.ToDictionary(kvp => kvp.Value, kvp => kvp.Key);
  public static bool TryGetTheme(string name, out Room.Theme theme) => NameToTheme.TryGetValue(name.ToLowerInvariant(), out theme);

  public static void ReadConfigs() => Instance.ReadConfigs();

  private static void ClearData()
  {
    RoomSpawning.Data = [];
    RoomSpawning.Blueprints = [];
    RoomSpawning.RoomSizes = [];
    DungeonObjects.Objects = [];
    DungeonObjects.ObjectSwaps = [];
    DungeonObjects.ObjectData = [];
    CreatedObjects = [];
    NameToTheme = CreateNameToTheme();
    ThemeToName = DefaultNameToTheme.ToDictionary(kvp => kvp.Value, kvp => kvp.Key);
  }

  private static SetResult Set(Dictionary<string, string> files)
  {
    var previousData = RoomSpawning.Data;
    var previousBlueprints = RoomSpawning.Blueprints;
    var previousSizes = RoomSpawning.RoomSizes;
    var previousObjects = DungeonObjects.Objects;
    var previousSwaps = DungeonObjects.ObjectSwaps;
    var previousObjectData = DungeonObjects.ObjectData;
    var previousCreated = CreatedObjects;
    var previousNameToTheme = NameToTheme;
    var previousThemeToName = ThemeToName;
    ClearData();
    var result = SetResult.Failed;
    try
    {
      using var reuse = RoomRebuildCache.Begin();
      return result = LoadAndApply(files);
    }
    finally
    {
      if (result == SetResult.Applied)
        previousCreated.ForEach(UnityEngine.Object.Destroy);
      else
      {
        CreatedObjects.ForEach(UnityEngine.Object.Destroy);
        RoomSpawning.Data = previousData;
        RoomSpawning.Blueprints = previousBlueprints;
        RoomSpawning.RoomSizes = previousSizes;
        DungeonObjects.Objects = previousObjects;
        DungeonObjects.ObjectSwaps = previousSwaps;
        DungeonObjects.ObjectData = previousObjectData;
        CreatedObjects = previousCreated;
        NameToTheme = previousNameToTheme;
        ThemeToName = previousThemeToName;
      }
    }
  }

  private static SetResult LoadAndApply(Dictionary<string, string> files)
  {
    List<DungeonDB.RoomData> data = [];
    if (files.Count == 0)
    {
      data = DefaultEntries;
      Log.Info($"Reloading default room data ({DefaultEntries.Count} entries).");
    }
    else
    {
      if (!Yaml.TryDeserialize<RoomYaml, DungeonDB.RoomData>(files, FromData, out var parsed)) return SetResult.Failed;
      data = parsed.Where(room => room != null && room.m_prefab != null && !string.IsNullOrWhiteSpace(room.m_prefab.m_name)).ToList();
    }
    if (data.Count == 0)
    {
      Log.Warning("Failed to load any room data.");
      return SetResult.Failed;
    }
    if (files.Count > 0 && YamlDataManager.BackfillEnabled && AddMissingEntries(data))
      return SetResult.Rewritten;
    Log.Info($"Reloading room themes ({NameToTheme.Count} entries).");
    Log.Info($"Reloading room data ({data.Count} entries).");

    RoomNames = Helper.ToDict(data, item => item.m_prefab.name.ToLowerInvariant(), item => item.m_prefab.name);
    DungeonDB.instance.m_rooms = data;
    return SetResult.Applied;
  }
  private static List<GameObject> CreatedObjects = [];
  // Named non-inlined stages are also optional reflection hooks for the EWP startup profiler.
  [MethodImpl(MethodImplOptions.NoInlining)]
  public static DungeonDB.RoomData CreateRoomData(string name, string[] snapPieces)
  {
    DungeonDB.RoomData roomData = new();
    var room = CreateRoomProxy(name);
    roomData.m_loadedRoom = room;
    var baseName = Parse.Name(name);
    if (RoomSpawning.Prefabs.TryGetValue(baseName, out var baseRoom))
    {
      roomData.m_prefab = baseRoom.m_prefab;
      PopulateBaseRoom(baseName, name, room, baseRoom, snapPieces.Length);
    }
    else if (BlueprintManager.Load(baseName, snapPieces))
    {
      RoomSpawning.Blueprints[roomData] = baseName;
      roomData.m_prefab = new(new())
      {
        m_name = name,
        m_loadedAsset = room.gameObject
      };
      room.m_roomConnections = CreateBlueprintConnections(room, snapPieces.Length);
    }
    return roomData;
  }

  [MethodImpl(MethodImplOptions.NoInlining)]
  private static Room CreateRoomProxy(string name)
  {
    var room = new GameObject(name).AddComponent<Room>();
    CreatedObjects.Add(room.gameObject);
    return room;
  }

  [MethodImpl(MethodImplOptions.NoInlining)]
  private static RoomConnection CreateConnection(Room room, string name)
  {
    var conn = new GameObject(name).AddComponent<RoomConnection>();
    CreatedObjects.Add(conn.gameObject);
    conn.transform.parent = room.transform;
    return conn;
  }

  [MethodImpl(MethodImplOptions.NoInlining)]
  private static RoomConnection[] CreateBlueprintConnections(Room room, int count)
  {
    var connections = new RoomConnection[count];
    for (var i = 0; i < count; ++i)
      connections[i] = CreateConnection(room, "");
    return connections;
  }

  [MethodImpl(MethodImplOptions.NoInlining)]
  private static void PopulateBaseRoom(string baseName, string name, Room room, DungeonDB.RoomData baseRoom, int snapCount)
  {
    var cache = RoomRebuildCache.Current;
    if (cache != null && cache.Connections.TryGetValue(baseRoom, out var saved))
    {
      WarnConnectionCount(name, baseRoom, snapCount, saved.Length);
      ApplyCachedConnections(baseName, room, saved);
      return;
    }

    // No asset handle or component is kept by the optimization. Retain the existing
    // SafeLoad failure policy, including its null/exception behavior.
    var asset = Helper.SafeLoad(baseRoom);
    if (asset == null) return;
    try
    {
      var original = ReadBaseRoom(baseName, baseRoom);
      room.m_musicPrefab = original.m_musicPrefab;
      var connections = ReadBaseConnections(baseName, original);
      WarnConnectionCount(name, baseRoom, snapCount, connections.Length);
      // A Unity component cannot safely be snapshotted across an asset release.
      // Only rooms with a genuinely null music reference qualify for load elision.
      var canReuse = cache != null && ReferenceEquals(original.m_musicPrefab, null);
      var savedValues = CopyBaseConnections(room, connections, canReuse);
      if (canReuse) cache!.Connections[baseRoom] = savedValues!;
    }
    finally { Helper.ReleaseRoomAsset(baseRoom); }
  }

  [MethodImpl(MethodImplOptions.NoInlining)]
  private static Room ReadBaseRoom(string baseName, DungeonDB.RoomData room) => room.RoomInPrefab;

  [MethodImpl(MethodImplOptions.NoInlining)]
  private static RoomConnection[] ReadBaseConnections(string baseName, Room room) => room.GetConnections();

  private static void WarnConnectionCount(string name, DungeonDB.RoomData baseRoom, int count, int actual)
  {
    if (actual != count)
      Log.Warning($"Room {name} has {count} connections, but base room {baseRoom.m_prefab.name} has {actual} connections.");
  }

  [MethodImpl(MethodImplOptions.NoInlining)]
  private static RoomRebuildCache.ConnectionValues[]? CopyBaseConnections(Room room, RoomConnection[] source, bool remember)
  {
    var copies = new RoomConnection[source.Length];
    var values = remember ? new RoomRebuildCache.ConnectionValues[source.Length] : null;
    for (var i = 0; i < source.Length; ++i)
    {
      var original = source[i];
      var conn = CreateConnection(room, original.name);
      var value = new RoomRebuildCache.ConnectionValues(original);
      value.Apply(conn);
      if (values != null) values[i] = value;
      copies[i] = conn;
    }
    room.m_roomConnections = copies;
    return values;
  }

  [MethodImpl(MethodImplOptions.NoInlining)]
  private static void ApplyCachedConnections(string baseName, Room room, RoomRebuildCache.ConnectionValues[] values)
  {
    room.m_musicPrefab = null;
    var copies = new RoomConnection[values.Length];
    for (var i = 0; i < values.Length; ++i)
    {
      var conn = CreateConnection(room, values[i].Name);
      values[i].Apply(conn);
      copies[i] = conn;
    }
    room.m_roomConnections = copies;
  }

  [MethodImpl(MethodImplOptions.NoInlining)]
  private static void UpdateConnections(Room room, RoomConnectionYaml[] data)
  {
    for (var i = 0; i < data.Length; ++i)
    {
      var connData = data[i];
      if (i >= room.m_roomConnections.Length)
      {
        var newConn = CreateConnection(room, connData.type);
        var connections = room.m_roomConnections;
        Array.Resize(ref connections, connections.Length + 1);
        connections[connections.Length - 1] = newConn;
        room.m_roomConnections = connections;
      }
      var conn = room.m_roomConnections[i];
      if (conn.name == "")
        conn.name = connData.type;
      conn.m_type = connData.exitOnly ? $">{connData.type}" : connData.type;
      conn.m_entrance = connData.entrance;
      conn.m_allowDoor = connData.door == "true";
      conn.m_doorOnlyIfOtherAlsoAllowsDoor = connData.door == "other";
      // Old yamls won't have rotation set so this is important to keep them working.
      // Also it doesn't hurt that the position can be missing as well.
      var split = Parse.Split(connData.position);
      if (split.Length > 2)
        conn.transform.localPosition = Parse.VectorXZY(split, 0);
      if (split.Length > 3)
        conn.transform.localRotation = Parse.AngleYXZ(split, 3);
    }
  }
  [MethodImpl(MethodImplOptions.NoInlining)]
  private static DungeonDB.RoomData FromData(RoomYaml data, string fileName)
  {
    var snapPieces = data.connections.Select(c => c.position).ToArray();
    var roomData = CreateRoomData(data.name, snapPieces);
    RoomSpawning.Data[roomData] = data;
    var room = roomData.RoomInPrefab;
    var missingThemes = DataManager.ToList(data.theme).Where(s => !NameToTheme.ContainsKey(s.ToLowerInvariant())).ToArray();
    var maxValue = NameToTheme.Values.Max(value => (int)value);
    // Note: Parsing of numeric values not supported, would be quite complex with multiple files.
    foreach (var theme in missingThemes)
    {
      if (NameToTheme.ContainsValue((Room.Theme)int.MinValue))
        throw new InvalidOperationException($"Cannot create room theme '{theme}': the maximum theme value has been reached.");
      maxValue *= 2;
      var maxTheme = (Room.Theme)maxValue;
      NameToTheme[theme.ToLowerInvariant()] = maxTheme;
      NameToTheme[maxValue.ToString()] = maxTheme;
      ThemeToName[maxTheme] = theme;
    }
    room.m_theme = DataManager.ToEnum<Room.Theme>(data.theme);
    room.m_entrance = data.entrance;
    room.m_endCap = data.endCap;
    room.m_divider = data.divider;
    room.m_enabled = data.enabled;
    var size = Parse.VectorXZY(data.size);
    RoomSpawning.RoomSizes[room] = size;
    room.m_size = new((int)size.x, (int)size.y, (int)size.z);
    room.m_minPlaceOrder = data.minPlaceOrder;
    room.m_weight = data.weight;
    room.m_faceCenter = data.faceCenter;
    room.m_perimeter = data.perimeter;
    room.m_endCapPrio = data.endCapPriority;
    roomData.m_theme = room.m_theme;
    roomData.m_enabled = room.m_enabled;
    UpdateConnections(room, data.connections);
    if (data.objects != null) LoadRoomObjects(roomData, data, fileName);
    if (data.objectSwap != null) LoadRoomSwaps(roomData, data);
    if (data.objectData != null) LoadRoomData(roomData, data, fileName);
    return roomData;
  }
  [MethodImpl(MethodImplOptions.NoInlining)]
  private static void LoadRoomObjects(DungeonDB.RoomData roomData, RoomYaml data, string fileName) =>
    DungeonObjects.Objects[roomData] = Helper.ParseObjects(data.objects!, fileName);
  [MethodImpl(MethodImplOptions.NoInlining)]
  private static void LoadRoomSwaps(DungeonDB.RoomData roomData, RoomYaml data) =>
    DungeonObjects.ObjectSwaps[roomData] = Create.LoadSwaps(data.objectSwap!);
  [MethodImpl(MethodImplOptions.NoInlining)]
  private static void LoadRoomData(DungeonDB.RoomData roomData, RoomYaml data, string fileName) =>
    DungeonObjects.ObjectData[roomData] = Create.LoadData(data.objectData!, fileName);

  [MethodImpl(MethodImplOptions.NoInlining)]
  private static RoomYaml ToData(DungeonDB.RoomData roomData)
  {
    var baseName = roomData.m_prefab.name;
    var asset = Helper.SafeLoad(roomData);
    if (asset == null)
      return null!;
    try
    {
      var room = ReadBaseRoom(baseName, roomData);
      RoomYaml data = new()
      {
        name = room.gameObject.name,
        theme = DataManager.FromEnum(room.m_theme),
        entrance = room.m_entrance,
        endCap = room.m_endCap,
        divider = room.m_divider,
        enabled = room.m_enabled,
        size = $"{room.m_size.x},{room.m_size.z},{room.m_size.y}",
        minPlaceOrder = room.m_minPlaceOrder,
        weight = room.m_weight,
        faceCenter = room.m_faceCenter,
        perimeter = room.m_perimeter,
        endCapPriority = room.m_endCapPrio,
        connections = ReadBaseConnections(baseName, room).Select(connection => new RoomConnectionYaml
        {
          position = $"{Helper.Print(connection.transform.localPosition)},{Helper.Print(connection.transform.localRotation)}",
          type = connection.m_type,
          entrance = connection.m_entrance,
          door = connection.m_allowDoor ? "true" : connection.m_doorOnlyIfOtherAlsoAllowsDoor ? "other" : "false"
        }).ToArray(),
      };
      return data;
    }
    finally { Helper.ReleaseRoomAsset(roomData); }
  }

  private static void SetDefaultEntries()
  {
    DefaultEntries = DungeonDB.instance.m_rooms.Where(room => room.m_prefab.IsValid).ToList();
    RoomSpawning.Prefabs = DefaultEntries.ToDictionary(entry => entry.m_prefab.name, entry => entry);
  }
  private static bool AddMissingEntries(List<DungeonDB.RoomData> entries)
  {
    var missingEntries = YamlFiles.GetMissingEntries(DefaultEntries, entries.Select(entry => entry.m_prefab.name), entry => entry.m_prefab.name);
    if (missingEntries.Count == 0) return false;
    var missing = missingEntries.Select(ToData).Where(d => d != null).ToList();
    Log.Warning($"Adding {missing.Count} missing rooms to the expand_rooms.yaml file.");
    Save(missing, true);
    return true;
  }
  private static void Save(List<RoomYaml> data, bool log)
  {
    YamlFiles.SavePerMod("rooms", data, item => item.name, log);
  }
  public static void SetupWatcher()
  {
    Yaml.SetupDataWatcher(Pattern, Configuration.configDataRooms, ReadConfigs);
  }
}
