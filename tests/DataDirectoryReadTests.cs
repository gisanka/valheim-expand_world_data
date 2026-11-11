using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using BepInEx.Logging;
using NUnit.Framework;
using Service;

namespace ExpandWorldData.Tests;

[TestFixture, NonParallelizable]
public class DataDirectoryReadTests
{
  private string root = null!;
  private ManualLogSource logger = null!;
  private readonly List<string> warnings = [];

  [SetUp]
  public void SetUp()
  {
    root = Path.Combine(Path.GetTempPath(), "EwdDataDirectoryTests", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    warnings.Clear();
    logger = new ManualLogSource("DataDirectoryReadTests");
    logger.LogEvent += (_, args) =>
    {
      if (args.Level == LogLevel.Warning) warnings.Add(args.Data.ToString());
    };
    Log.Init(logger);
  }

  [TearDown]
  public void TearDown()
  {
    logger.Dispose();
    Directory.Delete(root, true);
  }

  private static string[] Read(string path, string pattern = "*.yaml") =>
    (string[])typeof(global::Data.DataLoading).GetMethod("GetFiles", BindingFlags.NonPublic | BindingFlags.Static)!
      .Invoke(null, new object[] { path, pattern })!;

  [Test]
  public void MissingSourceDoesNotPreventReadingOtherSources()
  {
    var missing = Path.Combine(root, "missing");
    var nested = Path.Combine(root, "nested");
    Directory.CreateDirectory(nested);
    var yaml = Path.Combine(nested, "sample.yaml");
    File.WriteAllText(yaml, "[]");
    File.WriteAllText(Path.Combine(nested, "ignored.txt"), "ignored");
    Assert.That(Read(missing).Concat(Read(root)).ToArray(), Is.EqualTo(new[] { yaml }));
    Assert.That(warnings.Count, Is.EqualTo(1));
    Assert.That(warnings[0], Does.Contain(missing).And.Contain("DirectoryNotFoundException"));
    Assert.That(Directory.Exists(missing), Is.False);
  }

  [Test]
  public void AllFailedSourcesProduceNoFilesAndOneWarningEach()
  {
    var files = Enumerable.Range(0, 3).SelectMany(i => Read(Path.Combine(root, "missing" + i))).ToArray();
    Assert.That(files, Is.Empty);
    Assert.That(warnings.Count, Is.EqualTo(3));
  }

  [Test]
  public void FileInsteadOfDirectoryAlsoProducesWarningAndNoFiles()
  {
    var path = Path.Combine(root, "data");
    File.WriteAllText(path, "unchanged");
    Assert.That(Read(path), Is.Empty);
    Assert.That(warnings.Count, Is.EqualTo(1));
    Assert.That(warnings[0], Does.Contain(path));
    Assert.That(File.ReadAllText(path), Is.EqualTo("unchanged"));
  }

  [Test]
  public void EmptyReadableDirectoryProducesNoWarning()
  {
    Assert.That(Read(root), Is.Empty);
    Assert.That(warnings, Is.Empty);
  }
}
