using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using BepInEx;
using BepInEx.Configuration;
using Service;
using ConnectionValues = ExpandWorldData.Dungeon.RoomRebuildCache.ConnectionValues;

namespace ExpandWorldData.Dungeon;

// P3.2: content-based invalidation of file-backed assets and the known mod
// environment. Still opt-in: arbitrary runtime mutations are not observable.
internal static class PersistentRoomMetadataCache
{
  private static ConfigEntry<bool>? Enabled;
  [ThreadStatic] private static Session? Active;
  internal static Session? Current => Active;

  internal static void Init(ConfigFile config) => Enabled = config.Bind("Data", "Experimental persistent room metadata cache", false,
    "Opt-in: reuse native room connection snapshots when bundle contents and known mod/config dependencies match. Arbitrary runtime modifications are not covered. Applies on the next room load.");

  internal static Scope Begin(bool hasNativeRooms) => new(hasNativeRooms && Enabled?.Value == true);

  internal sealed class Scope : IDisposable
  {
    private readonly Session? Previous = Active;
    private readonly Session? Session;
    private bool Disposed;

    internal Scope(bool enabled)
    {
      Active = null;
      if (!enabled) return;
      try
      {
        var path = Path.GetFullPath(Path.Combine(Paths.ConfigPath, "..", "cache", "expand_world_data", "room-metadata-v2.bin"));
        Session = new Session(path, new RoomMetadataDependencies());
        Active = Session;
      }
      catch (Exception ex) { Log.Warning("Room metadata cache unavailable: " + ex.GetType().Name + ": " + ex.Message); }
    }

    internal void Commit() => Session?.Commit();
    public void Dispose()
    {
      if (Disposed) return;
      Disposed = true;
      Active = Previous;
      Session?.Report();
    }
  }

  internal sealed class Session
  {
    private readonly string Path;
    private readonly RoomMetadataDependencies Dependencies;
    private readonly Dictionary<DungeonDB.RoomData, (string Name, string Id)?> Keys = [];
    private readonly Dictionary<(string Name, string Id), ConnectionValues[]> Loaded = [];
    private readonly Dictionary<(string Name, string Id), ConnectionValues[]> Pending = [];
    private readonly Stopwatch Total = Stopwatch.StartNew();
    private string ReadStatus = "missing", WriteStatus = "not-needed";
    private long ReadTicks, LookupTicks, WriteTicks, ReadBytes, WrittenBytes;
    private int Hits, Misses, ChangedKeys, KeyRejected, MusicExcluded, LoadFailures, SnapshotRejected;
    private bool Applied;
    private string LookupFailure = "none";

    internal Session(string path, RoomMetadataDependencies dependencies)
    {
      Path = path;
      Dependencies = dependencies;
      var start = Stopwatch.GetTimestamp();
      try
      {
        if (!File.Exists(path)) return;
        Loaded = RoomMetadataFile.Read(path, Dependencies.Identity, out ReadBytes);
        ReadStatus = "valid";
      }
      catch (Exception ex)
      {
        // No partially decoded dictionary escapes the reader.
        ReadStatus = "rejected:" + ex.GetType().Name + (ex is InvalidDataException ? ":" + ex.Message : "");
      }
      finally { ReadTicks = Stopwatch.GetTimestamp() - start; }
    }

    internal bool TryGet(string name, DungeonDB.RoomData room, out ConnectionValues[] values)
    {
      var start = Stopwatch.GetTimestamp();
      values = null!;
      try
      {
        if (!Dependencies.TryAssetKey(name, room, out var key))
        { Keys[room] = null; KeyRejected++; return false; }
        Keys[room] = key;
        // Only pre-existing disk snapshots count as persistent hits. Cold snapshots
        // are reused only by the existing P3 cache, not by this lookup.
        if (Loaded.TryGetValue(key, out var found)) { Hits++; values = found; return true; }
        Misses++;
        // Drop stale fingerprints for this base room; a newly captured snapshot
        // replaces them on successful commit.
        foreach (var stale in Loaded.Keys.Where(k => k.Name == name && k != key).ToArray())
          if (Loaded.Remove(stale)) ChangedKeys++;
        return false;
      }
      catch (Exception ex)
      {
        // A changed game API or a cache-only failure must not prevent normal room loading.
        Keys[room] = null;
        KeyRejected++;
        LookupFailure = ex.GetType().Name;
        return false;
      }
      finally { LookupTicks += Stopwatch.GetTimestamp() - start; }
    }

    internal void RecordMusicExcluded() => MusicExcluded++;
    internal void RecordLoadFailure() => LoadFailures++;

    internal void Remember(string name, DungeonDB.RoomData room, ConnectionValues[] values)
    {
      if (!Keys.TryGetValue(room, out var found) || !found.HasValue || found.Value.Name != name ||
          !Dependencies.CanCapture(room.m_prefab.m_assetID) || !RoomMetadataFile.CanStore(values))
      { SnapshotRejected++; return; }
      var key = found.Value;
      if (Loaded.ContainsKey(key) || Pending.ContainsKey(key)) return;
      if (Loaded.Count + Pending.Count >= RoomMetadataFile.MaxEntries) { SnapshotRejected++; return; }
      Pending.Add(key, values);
    }

    internal void Commit()
    {
      Applied = true;
      var start = Stopwatch.GetTimestamp();
      try
      {
        if (!Dependencies.Unchanged())
        {
          WriteStatus = "dependency-changed";
          Log.Warning("Room metadata dependencies changed during the rebuild; cache not written. Reload after stopping concurrent changes.");
          return;
        }
        if (Pending.Count == 0) return;
        var combined = new Dictionary<(string Name, string Id), ConnectionValues[]>(Loaded);
        foreach (var entry in Pending) combined.Add(entry.Key, entry.Value);
        WrittenBytes = RoomMetadataFile.Write(Path, Dependencies.Identity, combined);
        WriteStatus = "written";
      }
      catch (Exception ex)
      {
        WriteStatus = "failed:" + ex.GetType().Name;
        // A cache write failure must not turn an already applied room rebuild into a failure.
      }
      finally { WriteTicks = Stopwatch.GetTimestamp() - start; }
    }

    private static string Ms(long ticks) => (ticks * 1000d / Stopwatch.Frequency).ToString("F2", CultureInfo.InvariantCulture);
    internal void Report() => Log.Info($"Room metadata cache: applied={Applied}, read={ReadStatus}, diskEntries={Loaded.Count}, diskHits={Hits}, diskMisses={Misses}, changedKeys={ChangedKeys}, " +
      $"keyRejected={KeyRejected}, musicExcluded={MusicExcluded}, loadFailures={LoadFailures}, snapshotRejected={SnapshotRejected}, captured={Pending.Count}, write={WriteStatus}, " +
      $"readBytes={ReadBytes}, writtenBytes={WrittenBytes}, ms: diskReadAndDecode={Ms(ReadTicks)}, lookup={Ms(LookupTicks)}, encodeAndWrite={Ms(WriteTicks)}, rebuildTotal={Total.ElapsedMilliseconds}; " +
      $"dependencyFiles={Dependencies.Fingerprint.FileCount}, dependencyBytes={Dependencies.Fingerprint.Bytes}, dependencyRejected={Dependencies.LastRejection}, lookupFailure={LookupFailure}, " +
      $"identity={RoomMetadataFingerprint.Hex(Dependencies.Identity)}, identityMs={Ms(Dependencies.IdentityTicks)}, assetKeyMs={Ms(Dependencies.AssetTicks)}, fileHashMs={Ms(Dependencies.Fingerprint.Ticks)}; " +
      "opt-in: untracked runtime modifications are not covered; " + Dependencies.IdentityParts);
  }
}
