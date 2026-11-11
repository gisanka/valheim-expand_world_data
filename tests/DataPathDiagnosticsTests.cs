using System;
using System.Collections.Generic;
using System.IO;
using BepInEx.Logging;
using NUnit.Framework;
using Service;

namespace ExpandWorldData.Tests;

[TestFixture, NonParallelizable]
public class DataPathDiagnosticsTests
{
  private string root = null!;
  private ManualLogSource logger = null!;
  private readonly List<string> messages = [];

  [SetUp]
  public void SetUp()
  {
    root = Path.Combine(Path.GetTempPath(), "EwdPathDiagnosticsTests", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    messages.Clear();
    logger = new ManualLogSource("DataPathDiagnosticsTests");
    logger.LogEvent += (_, args) => messages.Add(args.Data.ToString());
    Log.Init(logger);
  }

  [TearDown]
  public void TearDown()
  {
    logger.Dispose();
    Directory.Delete(root, true);
  }

  [Test]
  public void EnumeratesNestedYamlWithoutChangingContents()
  {
    var nested = Path.Combine(root, "nested");
    Directory.CreateDirectory(nested);
    var yaml = Path.Combine(nested, "data.yaml");
    File.WriteAllText(yaml, "- name: sample");
    File.WriteAllText(Path.Combine(root, "ignored.txt"), "ignored");
    Assert.That(DataPathDiagnostics.GetFiles("ProfilePath", root, "*.yaml"), Is.EqualTo(new[] { yaml }));
    Assert.That(File.ReadAllText(yaml), Is.EqualTo("- name: sample"));
    Assert.That(messages.Exists(m => m.Contains("ProfilePath: enumeration succeeded; files=1")), Is.True);
  }

  [Test]
  public void MissingDirectoryIsReportedAndRethrownWithoutCreatingIt()
  {
    var missing = Path.Combine(root, "missing");
    Assert.Throws<DirectoryNotFoundException>(() => DataPathDiagnostics.GetFiles("GamePath", missing, "*.yaml"));
    Assert.That(Directory.Exists(missing), Is.False);
    Assert.That(messages.Exists(m => m.Contains("GamePath: enumeration failed") && m.Contains(missing)), Is.True);
    Assert.That(messages.Exists(m => m.Contains("GamePath parent 1")), Is.True);
  }

  [Test]
  public void FileInPlaceOfDirectoryKeepsOriginalExceptionType()
  {
    var path = Path.Combine(root, "data");
    File.WriteAllText(path, "unchanged");
    var original = Assert.Catch(() => Directory.GetFiles(path, "*.yaml", SearchOption.AllDirectories));
    var diagnosed = Assert.Catch(() => DataPathDiagnostics.GetFiles("GamePath", path, "*.yaml"));
    Assert.That(diagnosed!.GetType(), Is.EqualTo(original!.GetType()));
    Assert.That(File.ReadAllText(path), Is.EqualTo("unchanged"));
    Assert.That(messages.Exists(m => m.Contains("fileExists=True")), Is.True);
  }
}
