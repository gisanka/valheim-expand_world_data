using System;
using System.Reflection;
using NUnit.Framework;

namespace ExpandWorldData.Tests;

[TestFixture, NonParallelizable]
public class RoomRebuildCacheTests
{
  private static readonly Type CacheType = typeof(global::Data.DataLoading).Assembly.GetType("ExpandWorldData.Dungeon.RoomRebuildCache", true)!;
  private static readonly Type ScopeType = CacheType.GetNestedType("Scope", BindingFlags.NonPublic)!;
  private static object? Current => CacheType.GetProperty("Current", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null);
  private static IDisposable Begin(bool enabled) => (IDisposable)Activator.CreateInstance(ScopeType,
    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new object[] { enabled }, null)!;

  [Test]
  public void ScopesAreIsolatedAndRestorePreviousState()
  {
    var previous = Current;
    object? first;
    using (Begin(true))
    {
      first = Current;
      Assert.That(first, Is.Not.Null);
      using (Begin(false))
        Assert.That(Current, Is.Null);
      Assert.That(Current, Is.SameAs(first));
      using (Begin(true))
        Assert.That(Current, Is.Not.SameAs(first));
      Assert.That(Current, Is.SameAs(first));
    }
    Assert.That(ReferenceEquals(Current, previous), Is.True);
    using (Begin(true))
      Assert.That(Current, Is.Not.SameAs(first));
    Assert.That(ReferenceEquals(Current, previous), Is.True);
  }

  [Test]
  public void FailedRebuildRestoresPreviousState()
  {
    var previous = Current;
    Assert.Throws<InvalidOperationException>(() =>
    {
      using var scope = Begin(true);
      Assert.That(Current, Is.Not.Null);
      throw new InvalidOperationException("Simulated room rebuild failure");
    });
    Assert.That(ReferenceEquals(Current, previous), Is.True);
  }
}
