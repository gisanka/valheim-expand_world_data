using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using ConnectionValues = ExpandWorldData.Dungeon.RoomRebuildCache.ConnectionValues;

namespace ExpandWorldData.Dungeon;

// One bounded, integrity-checked binary file. No serialized runtime objects.
// Identity covers the known environment; keys include actual bundle contents.
internal static class RoomMetadataFile
{
  private const int Format = 2;
  private const int HeaderBytes = 4 + 32 + 32 + 4;
  private const int MaxPayload = 16 * 1024 * 1024;
  private const int MaxString = 4096;
  private const int MaxConnections = 1024;
  internal const int MaxEntries = 4096;
  private static readonly Encoding Utf8 = new UTF8Encoding(false, true);

  internal static byte[] Identity() => Hash(Utf8.GetBytes(string.Join("\n", new[]
  {
    typeof(RoomMetadataFile).Module.ModuleVersionId.ToString(),
    typeof(Room).Module.ModuleVersionId.ToString(),
    typeof(SoftReferenceableAssets.AssetID).Module.ModuleVersionId.ToString()
  })));

  internal static bool CanStore(ConnectionValues[] values) => values.Length <= MaxConnections;

  internal static Dictionary<(string Name, string Id), ConnectionValues[]> Read(string path, byte[] identity, out long bytesRead)
  {
    bytesRead = 0;
    using var stream = File.OpenRead(path);
    using var reader = new BinaryReader(stream, Utf8);
    try
    {
      if (stream.Length < HeaderBytes || stream.Length > HeaderBytes + MaxPayload) throw new InvalidDataException("length");
      if (reader.ReadInt32() != Format || !reader.ReadBytes(32).SequenceEqual(identity)) throw new InvalidDataException("version");
      var checksum = reader.ReadBytes(32);
      var length = reader.ReadInt32();
      if (length < 4 || length > MaxPayload || stream.Length - stream.Position != length) throw new InvalidDataException("payload length");
      var payload = reader.ReadBytes(length);
      if (payload.Length != length || !Hash(payload).SequenceEqual(checksum)) throw new InvalidDataException("checksum");
      using var body = new MemoryStream(payload, false);
      using var data = new BinaryReader(body, Utf8);
      var entries = new Dictionary<(string Name, string Id), ConnectionValues[]>();
      var count = Count(data, MaxEntries, 12); // two string lengths and a connection count
      for (var i = 0; i < count; ++i)
      {
        var name = ReadString(data);
        var id = ReadString(data);
        if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(id)) throw new InvalidDataException("key");
        var connections = new ConnectionValues[Count(data, MaxConnections, 39)]; // 2 lengths + 7 floats + 3 flags
        for (var j = 0; j < connections.Length; ++j) connections[j] = ConnectionValues.Read(data);
        entries.Add((name!, id!), connections);
      }
      if (body.Position != body.Length) throw new InvalidDataException("trailing payload");
      return entries;
    }
    finally { bytesRead = stream.Position; }
  }

  internal static long Write(string path, byte[] identity, Dictionary<(string Name, string Id), ConnectionValues[]> entries)
  {
    if (identity.Length != 32 || entries.Count > MaxEntries) throw new InvalidDataException("header or entry count");
    byte[] payload;
    using (var body = new MemoryStream())
    using (var writer = new BinaryWriter(body, Utf8))
    {
      writer.Write(entries.Count);
      foreach (var entry in entries.OrderBy(e => e.Key.Name, StringComparer.Ordinal).ThenBy(e => e.Key.Id, StringComparer.Ordinal))
      {
        if (string.IsNullOrEmpty(entry.Key.Name) || string.IsNullOrEmpty(entry.Key.Id) || !CanStore(entry.Value))
          throw new InvalidDataException("snapshot");
        WriteString(writer, entry.Key.Name);
        WriteString(writer, entry.Key.Id);
        writer.Write(entry.Value.Length);
        foreach (var connection in entry.Value)
        {
          connection.Write(writer);
          if (body.Length > MaxPayload) throw new InvalidDataException("payload too large");
        }
        if (body.Length > MaxPayload) throw new InvalidDataException("payload too large");
      }
      writer.Flush();
      payload = body.ToArray();
    }
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
    try
    {
      using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
      using (var writer = new BinaryWriter(stream, Utf8))
      {
        writer.Write(Format);
        writer.Write(identity);
        writer.Write(Hash(payload));
        writer.Write(payload.Length);
        writer.Write(payload);
        writer.Flush();
        stream.Flush(true);
      }
      if (File.Exists(path)) File.Replace(temp, path, null);
      else File.Move(temp, path);
      return HeaderBytes + payload.Length;
    }
    finally { try { File.Delete(temp); } catch (Exception) { } }
  }

  private static int Count(BinaryReader reader, int max, int minBytes)
  {
    var count = reader.ReadInt32();
    if (count < 0 || count > max || (long)count * minBytes > reader.BaseStream.Length - reader.BaseStream.Position)
      throw new InvalidDataException("count");
    return count;
  }

  internal static void WriteString(BinaryWriter writer, string? value)
  {
    if (value == null) { writer.Write(-1); return; }
    var length = Utf8.GetByteCount(value);
    if (length > MaxString) throw new InvalidDataException("string length");
    writer.Write(length);
    if (length > 0) writer.Write(Utf8.GetBytes(value));
  }

  internal static string? ReadString(BinaryReader reader)
  {
    var length = reader.ReadInt32();
    if (length == -1) return null;
    if (length < 0 || length > MaxString || length > reader.BaseStream.Length - reader.BaseStream.Position)
      throw new InvalidDataException("string length");
    var bytes = reader.ReadBytes(length);
    if (bytes.Length != length) throw new EndOfStreamException();
    return Utf8.GetString(bytes);
  }

  internal static bool ReadBoolean(BinaryReader reader)
  {
    var value = reader.ReadByte();
    if (value > 1) throw new InvalidDataException("boolean");
    return value != 0;
  }

  private static byte[] Hash(byte[] bytes)
  {
    using var sha = SHA256.Create();
    return sha.ComputeHash(bytes);
  }
}
