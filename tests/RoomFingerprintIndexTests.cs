using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Reflection;
using BepInEx.Logging;
using NUnit.Framework;
using Service;

namespace ExpandWorldData.Tests;

[TestFixture, NonParallelizable]
public class RoomFingerprintIndexTests
{
  private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.NonPublic;
  private const BindingFlags Static = BindingFlags.Static | BindingFlags.NonPublic;
  private static readonly Assembly Assembly = typeof(global::Data.DataLoading).Assembly;
  private static Type TypeFor(string name) => Assembly.GetType("ExpandWorldData.Dungeon." + name, true)!;
  private static readonly Type ProofType = TypeFor("RoomFileProof");
  private static readonly Type IndexType = TypeFor("RoomFingerprintIndex");
  private static readonly Type StampType = TypeFor("RoomFileStamp");
  private static readonly Type FingerprintType = TypeFor("RoomMetadataFingerprint");
  private static object Construct(Type type, params object?[] arguments) =>
    Activator.CreateInstance(type, Instance | BindingFlags.Public, null, arguments, null)!;
  private static object Proof(string volume = "volume-A", uint serial = 1, ulong id = 42, ulong journal = 7,
    long usn = 80, long checkpoint = 100, long boundary = 20, long changeTime = 1234) =>
    Construct(ProofType, volume, serial, id, journal, usn, checkpoint, boundary, changeTime);
  private static bool Reuse(object current, object old) => (bool)ProofType.GetMethod("CanReuse", Instance)!.Invoke(current, new[] { old })!;
  private static object? Value(object target, string name) => target.GetType().GetProperty(name, Instance)!.GetValue(target);
  private string root = null!, index = null!;
  private ManualLogSource logger = null!;
  private readonly System.Collections.Generic.List<string> logs = [];

  [SetUp]
  public void SetUp()
  {
    root = Path.Combine(Path.GetTempPath(), "EwdRoomFingerprintTests", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    index = Path.Combine(root, "index.bin");
    logs.Clear();
    logger = new ManualLogSource("RoomFingerprintIndexTests");
    logger.LogEvent += (_, args) => logs.Add(args.Data.ToString());
    Log.Init(logger);
  }

  [TearDown]
  public void TearDown()
  {
    logger.Dispose();
    Directory.Delete(root, true);
  }

  [Test]
  public void ProofRequiresSameVolumeFileAndJournalWithContinuousCheckpoint()
  {
    var old = Proof();
    Assert.That(Reuse(Proof(checkpoint: 130, boundary: 90), old), Is.True);
    Assert.That(Reuse(Proof(volume: "volume-B"), old), Is.False);
    Assert.That(Reuse(Proof(serial: 2), old), Is.False);
    Assert.That(Reuse(Proof(id: 43), old), Is.False);
    Assert.That(Reuse(Proof(journal: 8), old), Is.False);
    Assert.That(Reuse(Proof(usn: 81), old), Is.False);
    Assert.That(Reuse(Proof(changeTime: 1235), old), Is.False);
    Assert.That(Reuse(Proof(checkpoint: 130, boundary: 101), old), Is.False);
    Assert.That(Reuse(Proof(checkpoint: 99), old), Is.False);
    Assert.That(Reuse(Proof(usn: 0), old), Is.False);
    Assert.That(Reuse(Proof(usn: 100), old), Is.False);
  }

  [Test]
  public void ParsesOnlyCompleteV2RecordForExpectedFile()
  {
    var parse = TypeFor("RoomNtfsProof").GetMethod("TryParseRecord", Static)!;
    var bytes = new byte[64];
    BitConverter.GetBytes(64u).CopyTo(bytes, 0);
    BitConverter.GetBytes((ushort)2).CopyTo(bytes, 4);
    BitConverter.GetBytes(42ul).CopyTo(bytes, 8);
    BitConverter.GetBytes(80L).CopyTo(bytes, 24);
    object[] args = { bytes, 64u, 42ul, 0L };
    Assert.That(parse.Invoke(null, args), Is.True);
    Assert.That(args[3], Is.EqualTo(80L));
    args[1] = 63u;
    Assert.That(parse.Invoke(null, args), Is.False);
    args[1] = 64u; args[2] = 43ul;
    Assert.That(parse.Invoke(null, args), Is.False);
    args[2] = 42ul; bytes[4] = 3;
    Assert.That(parse.Invoke(null, args), Is.False);
  }

  private static IDictionary Entries() => (IDictionary)Activator.CreateInstance(IndexType.GetMethod("Write", Static)!.GetParameters()[1].ParameterType)!;
  private void Write(IDictionary entries) => IndexType.GetMethod("Write", Static)!.Invoke(null, new object[] { index, entries });
  private IDictionary Read() => (IDictionary)IndexType.GetMethod("Read", Static)!.Invoke(null, new object[] { index })!;

  [Test]
  public void IndexRoundTripsProofAndHashAndRejectsCorruption()
  {
    var entries = Entries();
    var path = Path.Combine(root, "bundle");
    var stamp = Construct(StampType, path, 12L, 3L, 2L, new string('A', 64), Proof());
    StampType.GetField("Config", Instance)!.SetValue(stamp, true);
    entries.Add(path, stamp);
    Write(entries);
    var loaded = Read()[path]!;
    Assert.That(StampType.GetField("Hash", Instance)!.GetValue(loaded), Is.EqualTo(new string('A', 64)));
    Assert.That(StampType.GetField("Config", Instance)!.GetValue(loaded), Is.True);
    Assert.That(Reuse(Proof(checkpoint: 120), StampType.GetField("Proof", Instance)!.GetValue(loaded)!), Is.True);
    var bytes = File.ReadAllBytes(index);
    bytes[bytes.Length - 1] ^= 1;
    File.WriteAllBytes(index, bytes);
    Assert.That(Assert.Throws<TargetInvocationException>(() => Read())!.InnerException, Is.TypeOf<InvalidDataException>());
    Write(entries);
    Assert.That(Read().Count, Is.EqualTo(1));
    Assert.That(Directory.GetFiles(root, "*.tmp"), Is.Empty);
  }

  private object Fingerprint() => Construct(FingerprintType, index);
  private static string Hash(object fingerprint, string file) => (string)FingerprintType.GetMethod("HashFile", Instance)!.Invoke(fingerprint, new object[] { file })!;
  private static void Mark(object fingerprint, string file) => FingerprintType.GetMethod("MarkConfiguration", Instance)!.Invoke(fingerprint, new object[] { file });
  private static void Save(object fingerprint) => FingerprintType.GetMethod("SaveIndex", Instance)!.Invoke(fingerprint, null);

  [Test]
  public void ReportsExactChangedAddedRemovedConfigPathsWithoutValues()
  {
    var changed = Path.Combine(root, "changed.cfg");
    var removed = Path.Combine(root, "removed.cfg");
    var added = Path.Combine(root, "added.cfg");
    var stable = Path.Combine(root, "stable.cfg");
    foreach (var file in new[] { changed, removed, stable }) File.WriteAllText(file, "secret-one");
    var first = Fingerprint();
    foreach (var file in new[] { changed, removed, stable }) { Mark(first, file); Hash(first, file); }
    Save(first);
    Assert.That(Value(first, "IndexWrite"), Is.EqualTo("written"));
    File.WriteAllText(changed, "secret-two");
    File.WriteAllText(stable, "secret-one");
    File.WriteAllText(added, "secret-two");
    File.Delete(removed);
    var second = Fingerprint();
    foreach (var file in new[] { changed, added, stable }) { Mark(second, file); Hash(second, file); }
    Save(second);
    Assert.That(Value(second, "Changes"), Is.EqualTo("added=1, changed=1, removed=1, config=3"));
    Assert.That(logs.Any(l => l.Contains("changed (config): " + changed)), Is.True);
    Assert.That(logs.Any(l => l.Contains("added (config): " + added)), Is.True);
    Assert.That(logs.Any(l => l.Contains("removed (config): " + removed)), Is.True);
    Assert.That(logs.Any(l => l.Contains("secret-one") || l.Contains("secret-two") || l.Contains("stable.cfg")), Is.False);
  }

  [Test]
  public void NoProofNeverPermitsPersistedHashReuse()
  {
    var file = Path.Combine(root, "bundle");
    File.WriteAllText(file, "abc");
    var info = new FileInfo(file);
    var entries = Entries();
    entries.Add(file, Construct(StampType, file, info.Length, info.LastWriteTimeUtc.Ticks, info.CreationTimeUtc.Ticks, new string('0', 64), null));
    Write(entries);
    var fingerprint = Fingerprint();
    Assert.That(Hash(fingerprint, file), Is.Not.EqualTo(new string('0', 64)));
    Assert.That(Value(fingerprint, "IndexHits"), Is.Zero);
    Assert.That(Value(fingerprint, "HashedBytes"), Is.EqualTo(3L));
  }

  [Test]
  public void NativeProofReusesUnchangedFileAndRejectsSameLengthRestoredTimestampWrite()
  {
    var file = Path.Combine(root, "bundle");
    File.WriteAllText(file, "abc");
    var first = Fingerprint();
    var original = Hash(first, file);
    if ((int)Value(first, "ProofUnavailable")! != 0) Assert.Ignore("NTFS USN proof unavailable: " + Value(first, "ProofFailure"));
    Save(first);
    var warm = Fingerprint();
    Assert.That(Hash(warm, file), Is.EqualTo(original));
    Assert.That(Value(warm, "IndexHits"), Is.EqualTo(1));
    var modified = File.GetLastWriteTimeUtc(file);
    File.WriteAllText(file, "xyz");
    File.SetLastWriteTimeUtc(file, modified);
    var changed = Fingerprint();
    Assert.That(Hash(changed, file), Is.Not.EqualTo(original));
    Assert.That(Value(changed, "IndexHits"), Is.Zero);
  }
}
