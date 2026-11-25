using System;
using System.Collections;
using System.IO;
using System.Reflection;
using System.Text;
using NUnit.Framework;

namespace ExpandWorldData.Tests;

[TestFixture]
public class RoomMetadataFileTests
{
  private static readonly Assembly Assembly = typeof(global::Data.DataLoading).Assembly;
  private static readonly Type FileType = Assembly.GetType("ExpandWorldData.Dungeon.RoomMetadataFile", true)!;
  private static readonly Type ValuesType = Assembly.GetType("ExpandWorldData.Dungeon.RoomRebuildCache+ConnectionValues", true)!;
  private const BindingFlags Static = BindingFlags.Static | BindingFlags.NonPublic;
  private static readonly MethodInfo ReadMethod = FileType.GetMethod("Read", Static)!;
  private static readonly MethodInfo WriteMethod = FileType.GetMethod("Write", Static)!;
  private string folder = null!;
  private string path = null!;
  private readonly byte[] identity = new byte[32];

  [SetUp]
  public void SetUp()
  {
    folder = Path.Combine(Path.GetTempPath(), "EwdRoomMetadataTests", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(folder);
    path = Path.Combine(folder, "rooms.bin");
  }

  [TearDown]
  public void TearDown() => Directory.Delete(folder, true);

  private static IDictionary Entries() => (IDictionary)Activator.CreateInstance(WriteMethod.GetParameters()[2].ParameterType)!;
  private IDictionary Read(byte[]? version = null) => (IDictionary)ReadMethod.Invoke(null, new object[] { path, version ?? identity, 0L })!;
  private long Write(IDictionary entries) => (long)WriteMethod.Invoke(null, new object[] { path, identity, entries })!;

  // Independent fixture for the complete scalar connection record, including
  // Unicode names, null type, signed floats, quaternion components and all flags.
  private static byte[] Fixture()
  {
    using var stream = new MemoryStream();
    using var writer = new BinaryWriter(stream, Encoding.UTF8);
    var name = Encoding.UTF8.GetBytes("Tür_1");
    writer.Write(name.Length);
    writer.Write(name);
    writer.Write(-1);
    foreach (var value in new[] { -1.5f, 2.25f, 0f, 0.125f, -0.25f, 0.5f, 0.75f }) writer.Write(value);
    writer.Write(true);
    writer.Write(false);
    writer.Write(true);
    writer.Flush();
    return stream.ToArray();
  }

  private static object DecodeConnection(byte[] bytes)
  {
    using var stream = new MemoryStream(bytes);
    using var reader = new BinaryReader(stream, Encoding.UTF8);
    var value = ValuesType.GetMethod("Read", Static)!.Invoke(null, new object[] { reader })!;
    Assert.That(stream.Position, Is.EqualTo(stream.Length));
    return value;
  }

  private static byte[] EncodeConnection(object value)
  {
    using var stream = new MemoryStream();
    using var writer = new BinaryWriter(stream, Encoding.UTF8);
    ValuesType.GetMethod("Write", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(value, new object[] { writer });
    writer.Flush();
    return stream.ToArray();
  }

  private static IDictionary Sample()
  {
    var entries = Entries();
    var connections = Array.CreateInstance(ValuesType, 1);
    connections.SetValue(DecodeConnection(Fixture()), 0);
    entries.Add(("base", "asset-A"), connections);
    entries.Add(("base", "asset-B"), Array.CreateInstance(ValuesType, 0));
    return entries;
  }

  [Test]
  public void SnapshotsRoundTripWithoutUnityObjectsAndKeepAssetKeysDistinct()
  {
    var entries = Sample();
    Assert.That(Write(entries), Is.EqualTo(new FileInfo(path).Length));
    var loaded = Read();
    Assert.That(loaded.Count, Is.EqualTo(2));
    var connections = (Array)loaded[("base", "asset-A")]!;
    Assert.That(connections.Length, Is.EqualTo(1));
    Assert.That(EncodeConnection(connections.GetValue(0)!), Is.EqualTo(Fixture()));
    Assert.That(((Array)loaded[("base", "asset-B")]!).Length, Is.Zero);
    Assert.That(connections, Is.Not.SameAs(entries[("base", "asset-A")]));
  }

  [TestCase("version")]
  [TestCase("checksum")]
  [TestCase("truncation")]
  public void DamagedOrWrongVersionFilesAreRejected(string scenario)
  {
    Write(Sample());
    var bytes = File.ReadAllBytes(path);
    var version = (byte[])identity.Clone();
    if (scenario == "version") version[0] ^= 1;
    else if (scenario == "checksum") bytes[bytes.Length - 1] ^= 1;
    else Array.Resize(ref bytes, bytes.Length - 1);
    File.WriteAllBytes(path, bytes);
    var exception = Assert.Throws<TargetInvocationException>(() => Read(version));
    Assert.That(exception!.InnerException, Is.TypeOf<InvalidDataException>());
  }

  [Test]
  public void ExistingFileIsReplacedAndOversizedSnapshotCannotOverwriteIt()
  {
    Write(Sample());
    var updated = Entries();
    updated.Add(("other", "asset-C"), Array.CreateInstance(ValuesType, 0));
    Write(updated);
    Assert.That(Read().Contains(("other", "asset-C")), Is.True);
    Assert.That(Read().Contains(("base", "asset-A")), Is.False);
    var valid = File.ReadAllBytes(path);
    updated.Add(("oversized", "asset-D"), Array.CreateInstance(ValuesType, 1025));
    var exception = Assert.Throws<TargetInvocationException>(() => Write(updated));
    Assert.That(exception!.InnerException, Is.TypeOf<InvalidDataException>());
    Assert.That(File.ReadAllBytes(path), Is.EqualTo(valid));
    Assert.That(Directory.GetFiles(folder, "*.tmp"), Is.Empty);
  }

  private static readonly Type FingerprintType = Assembly.GetType("ExpandWorldData.Dungeon.RoomMetadataFingerprint", true)!;
  private static object Fingerprint() => Activator.CreateInstance(FingerprintType, true)!;
  private static string HashFile(object fingerprint, string file) => (string)FingerprintType.GetMethod("HashFile", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(fingerprint, new object[] { file })!;
  private static byte[] HashRows(params string[] rows) => (byte[])FingerprintType.GetMethod("HashRows", Static)!.Invoke(null, new object[] { rows })!;

  [Test]
  public void DependencyFingerprintUsesContentsEvenWithUnchangedLengthAndTimestamp()
  {
    var bundle = Path.Combine(folder, "bundle");
    File.WriteAllText(bundle, "abc");
    var time = File.GetLastWriteTimeUtc(bundle);
    var first = HashFile(Fingerprint(), bundle);
    File.WriteAllText(bundle, "xyz");
    File.SetLastWriteTimeUtc(bundle, time);
    Assert.That(new FileInfo(bundle).Length, Is.EqualTo(3));
    Assert.That(File.GetLastWriteTimeUtc(bundle), Is.EqualTo(time));
    Assert.That(HashFile(Fingerprint(), bundle), Is.Not.EqualTo(first));
  }

  [Test]
  public void DependencyHashIsReusedWithinRebuildButChangedFileIsRejected()
  {
    var dependency = Path.Combine(folder, "dependency");
    File.WriteAllText(dependency, "abc");
    var fingerprint = Fingerprint();
    var first = HashFile(fingerprint, dependency);
    Assert.That(HashFile(fingerprint, dependency), Is.EqualTo(first));
    var count = FingerprintType.GetProperty("FileCount", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(fingerprint);
    Assert.That(count, Is.EqualTo(1));
    File.AppendAllText(dependency, "changed");
    Assert.That(Assert.Throws<TargetInvocationException>(() => HashFile(fingerprint, dependency))!.InnerException, Is.TypeOf<IOException>());
  }

  [Test]
  public void DependencyOrderAndConfigValuesAffectIdentityWithoutDelimiterCollisions()
  {
    Assert.That(HashRows("a", "b"), Is.Not.EqualTo(HashRows("b", "a")));
    Assert.That(HashRows("a\nb", "c"), Is.Not.EqualTo(HashRows("a", "b\nc")));
    Assert.That(HashRows("config", "room", "true"), Is.Not.EqualTo(HashRows("config", "room", "false")));
  }

}
