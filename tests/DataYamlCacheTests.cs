using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using BepInEx.Logging;
using NUnit.Framework;
using Service;
using Data;

namespace ExpandWorldData.Tests;

[TestFixture, NonParallelizable]
public class DataYamlCacheTests
{
  private string root = null!;
  private string cache = null!;
  private ManualLogSource logger = null!;
  private readonly List<string> errors = [];
  private readonly List<string> infos = [];
  private static readonly Type CacheType = typeof(global::Data.DataLoading).Assembly.GetType("Data.DataYamlCache", true)!;
  private static readonly Type BinaryType = typeof(global::Data.DataLoading).Assembly.GetType("Data.DataYamlBinary", true)!;

  [SetUp]
  public void SetUp()
  {
    root = Path.Combine(Path.GetTempPath(), "EwdDataYamlCacheTests", Guid.NewGuid().ToString("N"));
    cache = Path.Combine(root, "cache");
    Directory.CreateDirectory(root);
    errors.Clear();
    infos.Clear();
    logger = new ManualLogSource("DataYamlCacheTests");
    logger.LogEvent += (_, args) =>
    {
      if (args.Level == LogLevel.Error) errors.Add(args.Data.ToString());
      if (args.Level == LogLevel.Info) infos.Add(args.Data.ToString());
    };
    Log.Init(logger);
  }

  [TearDown]
  public void TearDown()
  {
    logger.Dispose();
    Directory.Delete(root, true);
  }

  private string FileWith(string name, string text)
  {
    var path = Path.Combine(root, name + ".yaml");
    File.WriteAllText(path, text);
    return path;
  }

  private List<DataYaml> Read(params string[] files) =>
    (List<DataYaml>)CacheType.GetMethod("ReadFiles", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, new object[] { files.ToList(), cache })!;

  private string LastReport => infos.Last(line => line.StartsWith("Data YAML cache:"));

  [Test]
  public void WarmReadMatchesStrictYamlAndReturnsFreshObjects()
  {
    var file = FileWith("all", "- name: Test\n  values: []\n  ints: [a, b]\n  items:\n  - prefab: Wood\n    chance: 0.25\n    customData: {second: b, first: a}\n  item: {}\n- valueGroup: Group\n  values: [x, y]\n");
    var expected = Yaml.Read<DataYaml>([file]);
    var cold = Read(file);
    Assert.That(LastReport, Does.Contain("hits=0").And.Contain("missing=1"));
    Assert.That(Directory.GetFiles(cache, "*.bin").Length, Is.EqualTo(1));
    cold[0].ints![0] = "changed";
    cold[0].items![0].customData!["first"] = "changed";
    var warm = Read(file);
    Assert.That(LastReport, Does.Contain("hits=1").And.Contain("misses=0"));
    Assert.That(warm.Count, Is.EqualTo(expected.Count));
    Assert.That(warm[0].name, Is.EqualTo(expected[0].name));
    Assert.That(warm[0].ints, Is.EqualTo(expected[0].ints));
    Assert.That(warm[0].values, Is.Empty);
    Assert.That(warm[0].strings, Is.Null);
    Assert.That(warm[0].items![0].chance, Is.EqualTo(expected[0].items![0].chance));
    Assert.That(warm[0].items![0].customData!.ToArray(), Is.EqualTo(expected[0].items![0].customData!.ToArray()));
    Assert.That(warm[0].item!.chance, Is.EqualTo(expected[0].item!.chance));
    Assert.That(warm[1].values, Is.EqualTo(expected[1].values));
    Assert.That(warm[0], Is.Not.SameAs(cold[0]));
  }

  [Test]
  public void ContentChangeAndFileOrderRemainAuthoritative()
  {
    var a = FileWith("a", "- name: A\n");
    var b = FileWith("b", "- name: B\n");
    Read(a, b);
    File.WriteAllText(a, "- name: C\n");
    Assert.That(Read(b, a).Select(x => x.name), Is.EqualTo(new[] { "B", "C" }));
    Assert.That(LastReport, Does.Contain("hits=1").And.Contain("content=1"));
    File.Delete(b);
    Assert.That(Read(a).Select(x => x.name), Is.EqualTo(new[] { "C" }));
    var c = Path.Combine(root, "renamed.yaml");
    File.Move(a, c);
    Assert.That(Read(c).Single().name, Is.EqualTo("C"));
    Assert.That(LastReport, Does.Contain("missing=1"));
  }

  [Test]
  public void CorruptionAndVersionMismatchFallBackToYaml()
  {
    var file = FileWith("a", "- name: A\n");
    Read(file);
    var binary = Directory.GetFiles(cache, "*.bin").Single();
    var bytes = File.ReadAllBytes(binary);
    bytes[0] ^= 1;
    File.WriteAllBytes(binary, bytes);
    Assert.That(Read(file).Single().name, Is.EqualTo("A"));
    Assert.That(LastReport, Does.Contain("version=1"));
    bytes = File.ReadAllBytes(binary);
    bytes[bytes.Length - 1] ^= 1;
    File.WriteAllBytes(binary, bytes);
    Assert.That(Read(file).Single().name, Is.EqualTo("A"));
    Assert.That(LastReport, Does.Contain("read-or-integrity=1"));
    Assert.That(Read(file).Single().name, Is.EqualTo("A"));
    Assert.That(LastReport, Does.Contain("hits=1"));
  }

  [TestCase("- name: A\n  unknownField: value\n")]
  [TestCase("- name: [invalid\n")]
  public void TolerantOrFailedParseDoesNotCreateCacheAndKeepsDiagnostics(string yaml)
  {
    var file = FileWith("invalid", yaml);
    var expected = Yaml.Read<DataYaml>([file]);
    var originalErrors = errors.ToArray();
    Assert.That(originalErrors, Is.Not.Empty);
    errors.Clear();
    Assert.That(Read(file).Select(x => x.name), Is.EqualTo(expected.Select(x => x.name)));
    Assert.That(errors, Is.EqualTo(originalErrors));
    Assert.That(Directory.GetFiles(cache, "*.bin"), Is.Empty);
    errors.Clear();
    Read(file);
    Assert.That(errors, Is.EqualTo(originalErrors));
    Assert.That(LastReport, Does.Contain("hits=0"));
  }

  [Test]
  public void SharedMutableDtoReferencesAreRejectedByCodec()
  {
    var shared = new[] { "x" };
    var data = new List<DataYaml> { new() { ints = shared }, new() { ints = shared } };
    var error = Assert.Throws<TargetInvocationException>(() => BinaryType.GetMethod("Encode", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, new object[] { data }));
    Assert.That(error!.InnerException, Is.TypeOf<InvalidDataException>());
  }

  [Test]
  public void UnavailableCacheDirectoryDoesNotPreventYamlLoading()
  {
    var file = FileWith("a", "- name: A\n");
    File.WriteAllText(cache, "blocked");
    Assert.That(Read(file).Single().name, Is.EqualTo("A"));
  }
}
