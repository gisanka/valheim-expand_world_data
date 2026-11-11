using System;
using System.IO;
using BepInEx;

namespace Service;

// Read-only diagnostics. Do not touch Data.DataLoading here: doing so during Awake
// would change when its static paths are captured and could hide the original bug.
public static class DataPathDiagnostics
{
  public const string Build = "data-paths-2026-11-11";
  private static string? capturedWorkingDirectory;

  public static string CaptureGamePath()
  {
    capturedWorkingDirectory = Directory.GetCurrentDirectory();
    return Path.GetFullPath(Path.Combine("BepInEx", "config", "data"));
  }

  public static void Snapshot(string stage)
  {
    try
    {
      Log.Info($"[EWD path diagnostic {Build}] {stage}; autoReload={ExpandWorldData.Configuration.DataReload}; cwd='{Directory.GetCurrentDirectory()}'; config='{Paths.ConfigPath}'; base='{Yaml.BaseDirectory}'; capturedCwd='{capturedWorkingDirectory ?? "not captured yet"}'");
    }
    catch (Exception ex)
    {
      Log.Warning($"[EWD path diagnostic] Snapshot failed: {ex}");
    }
  }

  public static void PathsSnapshot(string stage, string gamePath, string profilePath)
  {
    Snapshot(stage);
    Log.Info($"[EWD path diagnostic] {stage}; GamePath='{gamePath}'; ProfilePath='{profilePath}'; lexicalEqual={string.Equals(gamePath, profilePath, StringComparison.Ordinal)}");
    Describe("GamePath", gamePath);
    Describe("ProfilePath", profilePath);
    Describe("BaseDirectory", Yaml.BaseDirectory);
  }

  private static void Describe(string label, string path)
  {
    try
    {
      Log.Info($"[EWD path diagnostic] {label}: path='{path}'; directoryExists={Directory.Exists(path)}; fileExists={File.Exists(path)}");
      // Unlike Exists, GetAttributes preserves the reason for a failed lookup.
      Log.Info($"[EWD path diagnostic] {label}: attributes={File.GetAttributes(path)}");
    }
    catch (Exception ex)
    {
      Log.Warning($"[EWD path diagnostic] {label}: attribute lookup failed; HResult=0x{ex.HResult:X8}; {ex}");
    }
  }

  public static string[] GetFiles(string label, string path, string pattern)
  {
    Log.Info($"[EWD path diagnostic] Enumerating {label}: path='{path}'; pattern='{pattern}'; recursive=true");
    try
    {
      var files = Directory.GetFiles(path, pattern, SearchOption.AllDirectories);
      Log.Info($"[EWD path diagnostic] {label}: enumeration succeeded; files={files.Length}");
      return files;
    }
    catch (Exception ex)
    {
      Log.Error($"[EWD path diagnostic] {label}: enumeration failed; path='{path}'; HResult=0x{ex.HResult:X8}; {ex}");
      Snapshot("enumeration failure");
      Describe(label, path);
      // Inspect the literal parent chain, without resolving or modifying links.
      try
      {
        var parent = Path.GetDirectoryName(path);
        for (var depth = 0; depth < 8 && !string.IsNullOrEmpty(parent); depth++)
        {
          Describe($"{label} parent {depth + 1}", parent!);
          parent = Path.GetDirectoryName(parent);
        }
      }
      catch (Exception diagnosticError)
      {
        Log.Warning($"[EWD path diagnostic] Parent inspection failed: {diagnosticError}");
      }
      throw;
    }
  }
}
