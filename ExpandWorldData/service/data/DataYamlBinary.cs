using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;

namespace Data;

// Fixed wire schema: no type names or arbitrary objects are read from disk.
internal static class DataYamlBinary
{
  private const int MaxCount = 1_000_000;
  private const int MaxStringBytes = 32 * 1024 * 1024;
  private static readonly Encoding Encoding = new UTF8Encoding(false, true);
  private static readonly (string Name, Type Type)[] DataYamlFields = [
    (nameof(DataYaml.name), typeof(string)),
    (nameof(DataYaml.prefab), typeof(string)),
    (nameof(DataYaml.position), typeof(string)),
    (nameof(DataYaml.rotation), typeof(string)),
    (nameof(DataYaml.connection), typeof(string)),
    (nameof(DataYaml.bools), typeof(string[])),
    (nameof(DataYaml.ints), typeof(string[])),
    (nameof(DataYaml.hashes), typeof(string[])),
    (nameof(DataYaml.floats), typeof(string[])),
    (nameof(DataYaml.strings), typeof(string[])),
    (nameof(DataYaml.longs), typeof(string[])),
    (nameof(DataYaml.vecs), typeof(string[])),
    (nameof(DataYaml.quats), typeof(string[])),
    (nameof(DataYaml.bytes), typeof(string[])),
    (nameof(DataYaml.items), typeof(ItemYaml[])),
    (nameof(DataYaml.item), typeof(ItemYaml)),
    (nameof(DataYaml.containerSize), typeof(string)),
    (nameof(DataYaml.itemAmount), typeof(string)),
    (nameof(DataYaml.valueGroup), typeof(string)),
    (nameof(DataYaml.value), typeof(string)),
    (nameof(DataYaml.values), typeof(string[])),
    (nameof(DataYaml.persistent), typeof(string)),
    (nameof(DataYaml.distant), typeof(string)),
    (nameof(DataYaml.priority), typeof(string))
  ];
  private static readonly (string Name, Type Type)[] ItemYamlFields = [
    (nameof(ItemYaml.pos), typeof(string)),
    (nameof(ItemYaml.chance), typeof(float)),
    (nameof(ItemYaml.prefab), typeof(string)),
    (nameof(ItemYaml.stack), typeof(string)),
    (nameof(ItemYaml.quality), typeof(string)),
    (nameof(ItemYaml.variant), typeof(string)),
    (nameof(ItemYaml.durability), typeof(string)),
    (nameof(ItemYaml.crafterID), typeof(string)),
    (nameof(ItemYaml.crafterName), typeof(string)),
    (nameof(ItemYaml.worldLevel), typeof(string)),
    (nameof(ItemYaml.equipped), typeof(string)),
    (nameof(ItemYaml.pickedUp), typeof(string)),
    (nameof(ItemYaml.cheated), typeof(string)),
    (nameof(ItemYaml.customData), typeof(Dictionary<string, string>))
  ];
  internal static bool SupportedSchema => Matches(typeof(DataYaml), DataYamlFields) && Matches(typeof(ItemYaml), ItemYamlFields);
  private static bool Matches(Type type, (string Name, Type Type)[] expected)
  {
    var fields = type.GetFields(BindingFlags.Instance | BindingFlags.Public);
    return type.GetProperties(BindingFlags.Instance | BindingFlags.Public).Length == 0 &&
      fields.Length == expected.Length && expected.All(e => fields.Any(f => f.Name == e.Name && f.FieldType == e.Type));
  }

  internal static byte[] Encode(List<DataYaml> data)
  {
    using var stream = new MemoryStream();
    using var writer = new BinaryWriter(stream, Encoding);
    var seen = new HashSet<object>();
    Count(writer, data.Count);
    foreach (var entry in data) WriteData(writer, entry, seen);
    writer.Flush();
    return stream.ToArray();
  }

  internal static List<DataYaml> Decode(byte[] payload)
  {
    using var stream = new MemoryStream(payload, false);
    using var reader = new BinaryReader(stream, Encoding);
    var count = Count(reader);
    var data = new List<DataYaml>(count);
    for (int i = 0; i < count; i++) data.Add(ReadData(reader)!);
    if (stream.Position != stream.Length) throw new InvalidDataException("Trailing cache data");
    return data;
  }

  // Do not cache shared mutable references: YAML aliases must keep their original identity.
  private static bool Present(BinaryWriter w, object? value, HashSet<object> seen, Type type)
  {
    w.Write(value != null);
    if (value == null) return false;
    if (value.GetType() != type || !seen.Add(value)) throw new InvalidDataException("Shared or unsupported DTO reference");
    return true;
  }

  private static void Count(BinaryWriter w, int count)
  {
    if (count < 0 || count > MaxCount) throw new InvalidDataException("Cache collection too large");
    w.Write(count);
  }
  private static int Count(BinaryReader r)
  {
    var count = r.ReadInt32();
    if (count < 0 || count > MaxCount || count > r.BaseStream.Length - r.BaseStream.Position) throw new InvalidDataException("Invalid cache collection length");
    return count;
  }

  private static void WriteString(BinaryWriter w, string? value)
  {
    if (value == null) { w.Write(-1); return; }
    var length = Encoding.GetByteCount(value);
    if (length > MaxStringBytes) throw new InvalidDataException("Cache string too large");
    w.Write(length);
    if (length > 0) w.Write(Encoding.GetBytes(value));
  }
  private static string? ReadString(BinaryReader r)
  {
    var length = r.ReadInt32();
    if (length == -1) return null;
    if (length < 0 || length > MaxStringBytes || length > r.BaseStream.Length - r.BaseStream.Position) throw new InvalidDataException("Invalid cache string length");
    var bytes = r.ReadBytes(length);
    if (bytes.Length != length) throw new EndOfStreamException();
    return Encoding.GetString(bytes);
  }

  private static void WriteStrings(BinaryWriter w, string[]? value, HashSet<object> seen)
  {
    if (value == null) { w.Write(false); return; }
    if (!Present(w, value, seen, typeof(string[]))) return;
    Count(w, value.Length);
    foreach (var s in value) WriteString(w, s);
  }
  private static string[]? ReadStrings(BinaryReader r)
  {
    if (!r.ReadBoolean()) return null;
    var result = new string[Count(r)];
    for (int i = 0; i < result.Length; i++) result[i] = ReadString(r)!;
    return result;
  }
  private static void WriteItems(BinaryWriter w, ItemYaml[]? value, HashSet<object> seen)
  {
    if (value == null) { w.Write(false); return; }
    if (!Present(w, value, seen, typeof(ItemYaml[]))) return;
    Count(w, value.Length);
    foreach (var item in value) WriteItem(w, item, seen);
  }
  private static ItemYaml[]? ReadItems(BinaryReader r)
  {
    if (!r.ReadBoolean()) return null;
    var result = new ItemYaml[Count(r)];
    for (int i = 0; i < result.Length; i++) result[i] = ReadItem(r)!;
    return result;
  }
  private static void WriteDictionary(BinaryWriter w, Dictionary<string, string>? value, HashSet<object> seen)
  {
    if (value == null) { w.Write(false); return; }
    if (!Present(w, value, seen, typeof(Dictionary<string, string>))) return;
    if (!object.Equals(value.Comparer, EqualityComparer<string>.Default)) throw new InvalidDataException("Unsupported dictionary comparer");
    Count(w, value.Count);
    foreach (var kv in value) { WriteString(w, kv.Key); WriteString(w, kv.Value); }
  }
  private static Dictionary<string, string>? ReadDictionary(BinaryReader r)
  {
    if (!r.ReadBoolean()) return null;
    var count = Count(r);
    var result = new Dictionary<string, string>(count);
    for (int i = 0; i < count; i++) result.Add(ReadString(r) ?? throw new InvalidDataException(), ReadString(r)!);
    return result;
  }
  private static void WriteData(BinaryWriter w, DataYaml? value, HashSet<object> seen)
  {
    if (value == null) { w.Write(false); return; }
    if (!Present(w, value, seen, typeof(DataYaml))) return;
    WriteString(w, value.name);
    WriteString(w, value.prefab);
    WriteString(w, value.position);
    WriteString(w, value.rotation);
    WriteString(w, value.connection);
    WriteStrings(w, value.bools, seen);
    WriteStrings(w, value.ints, seen);
    WriteStrings(w, value.hashes, seen);
    WriteStrings(w, value.floats, seen);
    WriteStrings(w, value.strings, seen);
    WriteStrings(w, value.longs, seen);
    WriteStrings(w, value.vecs, seen);
    WriteStrings(w, value.quats, seen);
    WriteStrings(w, value.bytes, seen);
    WriteItems(w, value.items, seen);
    WriteItem(w, value.item, seen);
    WriteString(w, value.containerSize);
    WriteString(w, value.itemAmount);
    WriteString(w, value.valueGroup);
    WriteString(w, value.value);
    WriteStrings(w, value.values, seen);
    WriteString(w, value.persistent);
    WriteString(w, value.distant);
    WriteString(w, value.priority);
  }
  private static DataYaml? ReadData(BinaryReader r)
  {
    if (!r.ReadBoolean()) return null;
    return new DataYaml
    {
      name = ReadString(r),
      prefab = ReadString(r),
      position = ReadString(r),
      rotation = ReadString(r),
      connection = ReadString(r),
      bools = ReadStrings(r),
      ints = ReadStrings(r),
      hashes = ReadStrings(r),
      floats = ReadStrings(r),
      strings = ReadStrings(r),
      longs = ReadStrings(r),
      vecs = ReadStrings(r),
      quats = ReadStrings(r),
      bytes = ReadStrings(r),
      items = ReadItems(r),
      item = ReadItem(r),
      containerSize = ReadString(r),
      itemAmount = ReadString(r),
      valueGroup = ReadString(r),
      value = ReadString(r),
      values = ReadStrings(r),
      persistent = ReadString(r),
      distant = ReadString(r),
      priority = ReadString(r),
    };
  }
  private static void WriteItem(BinaryWriter w, ItemYaml? value, HashSet<object> seen)
  {
    if (value == null) { w.Write(false); return; }
    if (!Present(w, value, seen, typeof(ItemYaml))) return;
    WriteString(w, value.pos);
    w.Write(value.chance);
    WriteString(w, value.prefab);
    WriteString(w, value.stack);
    WriteString(w, value.quality);
    WriteString(w, value.variant);
    WriteString(w, value.durability);
    WriteString(w, value.crafterID);
    WriteString(w, value.crafterName);
    WriteString(w, value.worldLevel);
    WriteString(w, value.equipped);
    WriteString(w, value.pickedUp);
    WriteString(w, value.cheated);
    WriteDictionary(w, value.customData, seen);
  }
  private static ItemYaml? ReadItem(BinaryReader r)
  {
    if (!r.ReadBoolean()) return null;
    return new ItemYaml
    {
      pos = ReadString(r)!,
      chance = r.ReadSingle(),
      prefab = ReadString(r)!,
      stack = ReadString(r),
      quality = ReadString(r),
      variant = ReadString(r),
      durability = ReadString(r),
      crafterID = ReadString(r),
      crafterName = ReadString(r),
      worldLevel = ReadString(r),
      equipped = ReadString(r),
      pickedUp = ReadString(r),
      cheated = ReadString(r),
      customData = ReadDictionary(r),
    };
  }
}
