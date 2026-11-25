using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using BepInEx;
using BepInEx.Configuration;
using Service;
using YamlDotNet.Serialization;

namespace Data;

// The cache stores only strictly parsed DataYaml DTOs, before the two runtime passes.
internal static class DataYamlCache
{
  private const int Format = 1;
  private const int MaxPayload = 128 * 1024 * 1024;
  private static readonly Encoding Encoding = new UTF8Encoding(false, true);
  private static ConfigEntry<bool>? Enabled;

  internal static void Init(ConfigFile config) => Enabled = config.Bind("Data", "Persistent data YAML cache", true,
    "Reuse parsed data entries and value groups from unchanged YAML files. Applies on the next data load.");

  internal static List<DataYaml> Read(List<string> files)
  {
    if (Enabled?.Value != true) return Yaml.Read<DataYaml>(files);
    string root;
    try
    {
      // Outside config, data and expand_world watcher trees.
      root = Path.GetFullPath(Path.Combine(Paths.ConfigPath, "..", "cache", "expand_world_data", "data-yaml-v1"));
    }
    catch (Exception)
    {
      return Yaml.Read<DataYaml>(files);
    }
    return ReadFiles(files, root);
  }

  internal static List<DataYaml> ReadFiles(List<string> files, string root)
  {
    var stats = new Stats();
    byte[] version;
    try
    {
      if (!DataYamlBinary.SupportedSchema) throw new InvalidOperationException("Unsupported DTO schema");
      var culture = CultureInfo.CurrentCulture;
      var number = culture.NumberFormat;
      // Assembly MVIDs invalidate parser, converters, DTO and codec changes on rebuild.
      version = Hash(Encoding.GetBytes(string.Join("\n", new[] {
        typeof(DataYaml).AssemblyQualifiedName!, typeof(DataYamlCache).Module.ModuleVersionId.ToString(),
        typeof(IDeserializer).Module.ModuleVersionId.ToString(), culture.Name, CultureInfo.CurrentUICulture.Name,
        number.NumberDecimalSeparator, number.NumberGroupSeparator, number.PositiveSign, number.NegativeSign,
        number.NaNSymbol, number.PositiveInfinitySymbol, number.NegativeInfinitySymbol
      })));
      Directory.CreateDirectory(root);
    }
    catch (Exception e)
    {
      Log.Warning($"Data YAML cache unavailable: {e.Message}");
      return Yaml.Read<DataYaml>(files);
    }

    List<DataYaml> result = [];
    HashSet<string> active = new(StringComparer.Ordinal);
    foreach (var file in files)
    {
      stats.Files++;
      var fileStart = Stopwatch.GetTimestamp();
      try
      {
        var start = Stopwatch.GetTimestamp();
        string raw;
        try { raw = File.ReadAllText(file); }
        finally { stats.YamlRead += Stopwatch.GetTimestamp() - start; }
        string? path = null;
        byte[]? fingerprint = null;
        start = Stopwatch.GetTimestamp();
        try
        {
          var bytes = Encoding.GetBytes(raw);
          stats.YamlBytes += bytes.Length;
          fingerprint = Hash(bytes);
          path = Path.Combine(root, Hex(Hash(Encoding.GetBytes(Path.GetFullPath(file)))) + ".bin");
          active.Add(path);
        }
        catch (Exception) { stats.Miss("fingerprint"); }
        finally { stats.Fingerprint += Stopwatch.GetTimestamp() - start; }

        if (path != null && fingerprint != null && TryRead(path, version, fingerprint, stats, out var cached))
        {
          stats.Hits++;
          result.AddRange(cached);
          continue;
        }
        start = Stopwatch.GetTimestamp();
        List<DataYaml> parsed;
        bool strict;
        try { parsed = Parse(raw, file, out strict); }
        finally { stats.YamlParse += Stopwatch.GetTimestamp() - start; }
        // The tolerant path must continue to emit the original strict-parser diagnostic on every load.
        if (strict && path != null && fingerprint != null)
          TryWrite(file, path, version, fingerprint, parsed, stats);
        else if (!strict) stats.StrictRejected++;
        result.AddRange(parsed);
      }
      catch (Exception ex)
      {
        stats.FileErrors++;
        Log.Error($"Error reading {Path.GetFileName(file)}: {ex.Message}");
      }
      finally { stats.RecordFile(file, Stopwatch.GetTimestamp() - fileStart); }
    }
    // Do not let transient source-discovery failures delete recently useful entries.
    try
    {
      var cutoff = DateTime.UtcNow.AddDays(-30);
      foreach (var path in Directory.EnumerateFiles(root, "*.bin"))
        if (IsHexName(Path.GetFileNameWithoutExtension(path), 64) && !active.Contains(path) && File.GetLastWriteTimeUtc(path) < cutoff)
          File.Delete(path);
      foreach (var path in Directory.EnumerateFiles(root, "*.tmp"))
        if (IsCacheTemp(path) && File.GetLastWriteTimeUtc(path) < cutoff) File.Delete(path);
    }
    catch (Exception) { stats.CleanupFailures++; }
    stats.Report();
    return result;
  }

  // Matches Yaml.Deserialize<T>, including its silent failure of the tolerant parser.
  private static List<DataYaml> Parse(string raw, string file, out bool strict)
  {
    try
    {
      var result = Yaml.Deserializer().Deserialize<List<DataYaml>>(raw) ?? [];
      strict = true;
      return result;
    }
    catch (Exception ex)
    {
      strict = false;
      Log.Error($"{file}: {ex.Message}");
      try { return Yaml.DeserializerUnSafe().Deserialize<List<DataYaml>>(raw) ?? []; }
      catch (Exception) { return []; }
    }
  }

  private static bool TryRead(string path, byte[] version, byte[] fingerprint, Stats stats, out List<DataYaml> result)
  {
    result = [];
    byte[] payload;
    var start = Stopwatch.GetTimestamp();
    try
    {
      if (!File.Exists(path)) { stats.Miss("missing"); return false; }
      using var stream = File.OpenRead(path);
      using var reader = new BinaryReader(stream, Encoding);
      try
      {
        if (stream.Length < 104 || stream.Length > MaxPayload + 104L) throw new InvalidDataException();
        if (reader.ReadInt32() != Format || !reader.ReadBytes(32).SequenceEqual(version))
        { stats.Miss("version"); return false; }
        if (!reader.ReadBytes(32).SequenceEqual(fingerprint))
        { stats.Miss("content"); return false; }
        var checksum = reader.ReadBytes(32);
        int length = reader.ReadInt32();
        if (length < 0 || length > MaxPayload || stream.Length - stream.Position != length) throw new InvalidDataException();
        payload = reader.ReadBytes(length);
        if (payload.Length != length || !Hash(payload).SequenceEqual(checksum)) throw new InvalidDataException();
      }
      finally { stats.CacheBytesRead += stream.Position; }
    }
    catch (Exception) { stats.Miss("read-or-integrity"); return false; }
    finally { stats.CacheRead += Stopwatch.GetTimestamp() - start; }
    start = Stopwatch.GetTimestamp();
    try { result = DataYamlBinary.Decode(payload); return true; }
    catch (Exception) { stats.Miss("decode"); return false; }
    finally { stats.Decode += Stopwatch.GetTimestamp() - start; }
  }

  private static void TryWrite(string file, string path, byte[] version, byte[] fingerprint, List<DataYaml> data, Stats stats)
  {
    byte[] payload;
    var start = Stopwatch.GetTimestamp();
    try
    {
      payload = DataYamlBinary.Encode(data);
      if (payload.Length > MaxPayload) throw new InvalidDataException("Cache payload too large");
    }
    catch (Exception ex) { stats.RejectEncode(file, ex); return; }
    finally { stats.Encode += Stopwatch.GetTimestamp() - start; }
    start = Stopwatch.GetTimestamp();
    string? temp = null;
    try
    {
      temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
      using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
      using (var writer = new BinaryWriter(stream, Encoding))
      {
        writer.Write(Format);
        writer.Write(version);
        writer.Write(fingerprint);
        writer.Write(Hash(payload));
        writer.Write(payload.Length);
        writer.Write(payload);
        writer.Flush();
        stream.Flush(true);
      }
      if (File.Exists(path)) File.Replace(temp, path, null);
      else File.Move(temp, path);
      stats.CacheBytesWritten += payload.Length + 104L;
    }
    catch (Exception) { stats.WriteFailures++; }
    finally
    {
      if (temp != null) { try { File.Delete(temp); } catch (Exception) { } }
      stats.Write += Stopwatch.GetTimestamp() - start;
    }
  }

  private static byte[] Hash(byte[] bytes)
  {
    using var sha = SHA256.Create();
    return sha.ComputeHash(bytes);
  }
  private static string Hex(byte[] bytes) => BitConverter.ToString(bytes).Replace("-", "");
  private static bool IsHexName(string name, int length) => name.Length == length && name.All(Uri.IsHexDigit);
  private static bool IsCacheTemp(string path)
  {
    var parts = Path.GetFileName(path).Split('.');
    return parts.Length == 4 && IsHexName(parts[0], 64) && parts[1] == "bin" && IsHexName(parts[2], 32) && parts[3] == "tmp";
  }

  private sealed class Stats
  {
    private readonly Stopwatch Total = Stopwatch.StartNew();
    private readonly int Gc0 = GC.CollectionCount(0), Gc1 = GC.CollectionCount(1), Gc2 = GC.CollectionCount(2);
    private readonly long Heap = GC.GetTotalMemory(false);
    private readonly Dictionary<string, int> Misses = [];
    private readonly Dictionary<string, int> EncodeReasons = [];
    private readonly List<string> EncodeDetails = [];
    private const int MaxEncodeDetails = 30;
    private int DroppedEncodeDetails;
    internal void RejectEncode(string file, Exception exception)
    {
      EncodeRejected++;
      // Codec errors contain only fixed diagnostics/type names, not DTO contents.
      // Other exception messages may contain arbitrary user data; log their type only.
      var reason = exception.GetType().Name;
      if (exception is InvalidDataException) reason += ": " + exception.Message;
      reason = reason.Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ');
      if (reason.Length > 256) reason = reason.Substring(0, 256);
      EncodeReasons[reason] = EncodeReasons.TryGetValue(reason, out var count) ? count + 1 : 1;
      if (EncodeDetails.Count < MaxEncodeDetails)
      {
        var source = file.Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ');
        EncodeDetails.Add(source + " => " + reason);
      }
      else DroppedEncodeDetails++;
    }
    internal int Files, Hits, StrictRejected, EncodeRejected, WriteFailures, FileErrors, CleanupFailures;
    private readonly List<(string File, long Ticks)> Slowest = [];
    internal void RecordFile(string file, long ticks)
    {
      if (Slowest.Count == 5 && ticks <= Slowest[Slowest.Count - 1].Ticks) return;
      Slowest.Add((Path.GetFileName(file), ticks));
      Slowest.Sort((a, b) => b.Ticks.CompareTo(a.Ticks));
      if (Slowest.Count > 5) Slowest.RemoveAt(5);
    }
    internal long YamlBytes, CacheBytesRead, CacheBytesWritten, YamlRead, Fingerprint, CacheRead, Decode, YamlParse, Encode, Write;
    internal void Miss(string reason) => Misses[reason] = Misses.TryGetValue(reason, out var count) ? count + 1 : 1;
    private static string Ms(long ticks) => (ticks * 1000.0 / Stopwatch.Frequency).ToString("F2", CultureInfo.InvariantCulture);
    internal void Report()
    {
      Log.Info($"Data YAML cache: files={Files}, hits={Hits}, misses={Misses.Values.Sum()} ({string.Join(", ", Misses.Select(kv => $"{kv.Key}={kv.Value}"))}), strictRejected={StrictRejected}, encodeRejected={EncodeRejected}, writeFailures={WriteFailures}, fileErrors={FileErrors}, cleanupFailures={CleanupFailures}, " +
      $"yamlUtf8Bytes={YamlBytes}, cacheReadBytes={CacheBytesRead}, cacheWriteBytes={CacheBytesWritten}, " +
      $"ms: yamlRead={Ms(YamlRead)}, fingerprint={Ms(Fingerprint)}, cacheReadAndVerify={Ms(CacheRead)}, binaryRead={Ms(Decode)}, yamlParse={Ms(YamlParse)}, binaryWrite={Ms(Encode)}, cacheWrite={Ms(Write)}, total={Total.ElapsedMilliseconds}, " +
      $"processGc={GC.CollectionCount(0)-Gc0}/{GC.CollectionCount(1)-Gc1}/{GC.CollectionCount(2)-Gc2}, processHeapDelta={GC.GetTotalMemory(false)-Heap}; slowest files (ms): {string.Join(", ", Slowest.Select(f => $"{f.File}={Ms(f.Ticks)}"))}");
      if (EncodeRejected > 0)
      {
        Log.Info($"Data YAML cache encode rejected: reasons={string.Join("; ", EncodeReasons.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => $"{kv.Key}={kv.Value}"))}; omittedDetails={DroppedEncodeDetails}");
        foreach (var detail in EncodeDetails) Log.Info("Data YAML cache encode rejected file: " + detail);
      }
    }
  }
}
