using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace ExpandWorldData.Dungeon;

internal sealed class RoomFileStamp(string path, long length, long modified, long created, string hash, RoomFileProof? proof)
{
  internal readonly string Path = path, Hash = hash;
  internal readonly long Length = length, Modified = modified, Created = created;
  internal readonly RoomFileProof? Proof = proof;
  internal bool Config;
  internal bool Matches(FileInfo info) => info.Exists && info.Length == Length &&
    info.LastWriteTimeUtc.Ticks == Modified && info.CreationTimeUtc.Ticks == Created;
}

// Separate from room snapshots: even a snapshot miss or an EWD rebuild may
// reuse the dependency hashes. No configuration contents are stored here.
internal static class RoomFingerprintIndex
{
  private const int Format = 1, MaxEntries = 20000, MaxPayload = 32 * 1024 * 1024, Header = 40;
  private static readonly Encoding Utf8 = new UTF8Encoding(false, true);
  private static byte[] Digest(byte[] bytes)
  {
    using var sha = SHA256.Create();
    return sha.ComputeHash(bytes);
  }

  internal static Dictionary<string, RoomFileStamp> Read(string path)
  {
    using var stream = File.OpenRead(path);
    using var reader = new BinaryReader(stream, Utf8);
    if (stream.Length < Header + 4 || stream.Length > Header + MaxPayload || reader.ReadInt32() != Format)
      throw new InvalidDataException("index format or size");
    var checksum = reader.ReadBytes(32);
    var length = reader.ReadInt32();
    if (length < 4 || length > MaxPayload || stream.Length - stream.Position != length)
      throw new InvalidDataException("index length");
    var payload = reader.ReadBytes(length);
    if (payload.Length != length || !Digest(payload).SequenceEqual(checksum)) throw new InvalidDataException("index checksum");
    using var body = new MemoryStream(payload, false);
    using var data = new BinaryReader(body, Utf8);
    var count = data.ReadInt32();
    if (count < 0 || count > MaxEntries) throw new InvalidDataException("index count");
    var entries = new Dictionary<string, RoomFileStamp>(StringComparer.Ordinal);
    for (var i = 0; i < count; ++i)
    {
      var file = RoomMetadataFile.ReadString(data);
      var hash = RoomMetadataFile.ReadString(data);
      if (file == null || file.Length == 0 || hash == null || hash.Length != 64 || !hash.All(Uri.IsHexDigit))
        throw new InvalidDataException("index key or hash");
      var size = data.ReadInt64();
      var modified = data.ReadInt64();
      var created = data.ReadInt64();
      if (size < 0 || modified < 0 || created < 0) throw new InvalidDataException("index stamp");
      var config = RoomMetadataFile.ReadBoolean(data);
      RoomFileProof? proof = null;
      if (RoomMetadataFile.ReadBoolean(data))
      {
        var volume = RoomMetadataFile.ReadString(data) ?? throw new InvalidDataException("index volume");
        proof = new RoomFileProof(volume, data.ReadUInt32(), data.ReadUInt64(), data.ReadUInt64(),
          data.ReadInt64(), data.ReadInt64(), data.ReadInt64(), data.ReadInt64());
        if (!proof.Valid) throw new InvalidDataException("index USN proof");
      }
      entries.Add(file, new RoomFileStamp(file, size, modified, created, hash, proof) { Config = config });
    }
    if (body.Position != body.Length) throw new InvalidDataException("index trailing bytes");
    return entries;
  }

  internal static void Write(string path, Dictionary<string, RoomFileStamp> entries)
  {
    if (entries.Count > MaxEntries) throw new InvalidDataException("index count");
    byte[] payload;
    using (var body = new MemoryStream())
    using (var writer = new BinaryWriter(body, Utf8))
    {
      writer.Write(entries.Count);
      foreach (var stamp in entries.Values.OrderBy(e => e.Path, StringComparer.Ordinal))
      {
        RoomMetadataFile.WriteString(writer, stamp.Path);
        RoomMetadataFile.WriteString(writer, stamp.Hash);
        writer.Write(stamp.Length); writer.Write(stamp.Modified); writer.Write(stamp.Created);
        writer.Write(stamp.Config);
        writer.Write(stamp.Proof != null);
        if (stamp.Proof is { } proof)
        {
          RoomMetadataFile.WriteString(writer, proof.Volume);
          writer.Write(proof.Serial); writer.Write(proof.FileId); writer.Write(proof.Journal);
          writer.Write(proof.Usn); writer.Write(proof.Checkpoint); writer.Write(proof.Boundary); writer.Write(proof.ChangeTime);
        }
        if (body.Length > MaxPayload) throw new InvalidDataException("index payload size");
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
        writer.Write(Format); writer.Write(Digest(payload)); writer.Write(payload.Length); writer.Write(payload);
        writer.Flush(); stream.Flush(true);
      }
      if (File.Exists(path)) File.Replace(temp, path, null);
      else File.Move(temp, path);
    }
    finally { try { File.Delete(temp); } catch (Exception) { } }
  }
}
