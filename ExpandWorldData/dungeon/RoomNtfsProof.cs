using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace ExpandWorldData.Dungeon;

// An observation of the last file USN in a known journal instance. The
// checkpoint is NextUsn at observation time, not the age of the file itself.
internal sealed class RoomFileProof(string volume, uint serial, ulong fileId, ulong journal,
  long usn, long checkpoint, long boundary, long changeTime)
{
  internal readonly string Volume = volume;
  internal readonly uint Serial = serial;
  internal readonly ulong FileId = fileId, Journal = journal;
  internal readonly long Usn = usn, Checkpoint = checkpoint, Boundary = boundary, ChangeTime = changeTime;
  internal bool Valid => Volume.Length != 0 && FileId != 0 && Journal != 0 && Usn > 0 &&
    Checkpoint > Usn && Boundary >= 0 && Boundary <= Checkpoint && ChangeTime > 0;

  internal bool CanReuse(RoomFileProof old) => Valid && old.Valid &&
    string.Equals(Volume, old.Volume, StringComparison.OrdinalIgnoreCase) && Serial == old.Serial &&
    FileId == old.FileId && Journal == old.Journal && Usn == old.Usn && ChangeTime == old.ChangeTime &&
    Checkpoint >= old.Checkpoint && Boundary <= old.Checkpoint;
}

// Windows-only, read-only probes. Never create a journal or request elevation.
// All unsupported/denied probes fall back to reading the actual file contents.
internal sealed class RoomNtfsProof
{
  private const uint ReadFileUsn = 0x000900EB, QueryJournal = 0x000900F4;
  private readonly Dictionary<string, string> Unavailable = new(StringComparer.OrdinalIgnoreCase);
  internal string LastFailure { get; private set; } = "none";

  [StructLayout(LayoutKind.Sequential)]
  private struct FileInformation
  {
    internal uint Attributes, CreationLow, CreationHigh, AccessLow, AccessHigh, WriteLow, WriteHigh;
    internal uint Serial, SizeHigh, SizeLow, Links, IdHigh, IdLow;
    internal ulong FileId => ((ulong)IdHigh << 32) | IdLow;
    internal ulong Length => ((ulong)SizeHigh << 32) | SizeLow;
  }

  [StructLayout(LayoutKind.Sequential)]
  private struct BasicInformation
  {
    internal long CreationTime, LastAccessTime, LastWriteTime, ChangeTime;
    internal uint Attributes;
  }

  [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
  private static extern bool GetFileInformationByHandle(SafeFileHandle file, out FileInformation info);
  [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
  private static extern bool GetFileInformationByHandleEx(SafeFileHandle file, int infoClass, out BasicInformation info, uint size);
  [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
  private static extern uint GetFinalPathNameByHandleW(SafeFileHandle file, StringBuilder path, uint length, uint flags);
  [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
  private static extern bool GetVolumeInformationW(string root, IntPtr name, uint nameSize,
    out uint serial, out uint maxComponent, out uint flags, StringBuilder fileSystem, uint fileSystemSize);
  [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
  private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security,
    uint disposition, uint flags, IntPtr template);
  [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
  private static extern bool DeviceIoControl(SafeFileHandle handle, uint code, byte[]? input, uint inputSize,
    [Out] byte[] output, uint outputSize, out uint returned, IntPtr overlapped);

  private RoomFileProof? Fail(string reason)
  {
    LastFailure = reason;
    return null;
  }

  private RoomFileProof? FailVolume(string volume, string reason)
  {
    Unavailable[volume] = reason;
    return Fail(reason);
  }

  // USN_JOURNAL_DATA_V0 is the common 56-byte prefix of later versions.
  private static bool Journal(SafeFileHandle volume, out ulong id, out long next, out long boundary)
  {
    id = 0; next = boundary = 0;
    var data = new byte[128];
    if (!DeviceIoControl(volume, QueryJournal, null, 0, data, (uint)data.Length, out var size, IntPtr.Zero) || size < 56)
      return false;
    id = BitConverter.ToUInt64(data, 0);
    next = BitConverter.ToInt64(data, 16);
    boundary = Math.Max(BitConverter.ToInt64(data, 8), BitConverter.ToInt64(data, 24));
    return id != 0 && boundary >= 0 && next > 0 && boundary <= next;
  }

  // Accept only the NTFS V2 record layout and its exact file reference number.
  internal static bool TryParseRecord(byte[] data, uint size, ulong fileId, out long usn)
  {
    usn = 0;
    if (size < 60 || size > data.Length || BitConverter.ToUInt32(data, 0) < 60 ||
        BitConverter.ToUInt32(data, 0) > size || BitConverter.ToUInt16(data, 4) != 2 ||
        BitConverter.ToUInt16(data, 6) != 0 || BitConverter.ToUInt64(data, 8) != fileId) return false;
    usn = BitConverter.ToInt64(data, 24);
    return usn > 0;
  }

  internal RoomFileProof? Read(FileStream stream)
  {
    if (System.Environment.OSVersion.Platform != PlatformID.Win32NT) return Fail("not Windows");
    try
    {
      var file = stream.SafeFileHandle;
      if (!GetFileInformationByHandle(file, out var info) || info.Length != (ulong)stream.Length ||
          (info.Attributes & (uint)(FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
        return Fail("file identity unavailable");
      // NTFS ChangeTime is an additional check for coalesced USN records.
      if (!GetFileInformationByHandleEx(file, 0, out var basic, (uint)Marshal.SizeOf(typeof(BasicInformation))))
        return Fail("file change time unavailable");
      // Resolve the actual volume from the open file, not a drive-letter guess.
      var path = new StringBuilder(4096);
      var length = GetFinalPathNameByHandleW(file, path, (uint)path.Capacity, 1); // VOLUME_NAME_GUID
      if (length == 0 || length >= path.Capacity) return Fail("volume GUID unavailable");
      var resolved = path.ToString();
      var end = resolved.IndexOf('}');
      if (!resolved.StartsWith(@"\\?\Volume{", StringComparison.OrdinalIgnoreCase) || end < 0 ||
          end + 1 >= resolved.Length || resolved[end + 1] != '\\') return Fail("not a local volume GUID");
      var root = resolved.Substring(0, end + 2);
      if (Unavailable.TryGetValue(root, out var reason)) return Fail(reason);
      var fs = new StringBuilder(32);
      if (!GetVolumeInformationW(root, IntPtr.Zero, 0, out var serial, out _, out _, fs, (uint)fs.Capacity) ||
          !string.Equals(fs.ToString(), "NTFS", StringComparison.OrdinalIgnoreCase) || serial != info.Serial)
        return FailVolume(root, "not verified NTFS volume");
      using var volume = CreateFileW(root.TrimEnd('\\'), 0, 7, IntPtr.Zero, 3, 0, IntPtr.Zero);
      if (volume.IsInvalid) return FailVolume(root, "volume open error " + Marshal.GetLastWin32Error());
      if (!Journal(volume, out var id, out var next, out _))
        return FailVolume(root, "journal query error " + Marshal.GetLastWin32Error());
      var record = new byte[1024];
      var versions = new byte[] { 2, 0, 2, 0 }; // READ_FILE_USN_DATA min/max V2
      if (!DeviceIoControl(file, ReadFileUsn, versions, (uint)versions.Length, record, (uint)record.Length,
          out var size, IntPtr.Zero)) return Fail("file USN error " + Marshal.GetLastWin32Error());
      if (!TryParseRecord(record, size, info.FileId, out var usn)) return Fail("invalid file USN record");
      if (!Journal(volume, out var afterId, out var afterNext, out var boundary) || id != afterId ||
          afterNext < next || boundary > next) return Fail("journal changed during probe");
      var proof = new RoomFileProof(root, info.Serial, info.FileId, id, usn, afterNext, boundary, basic.ChangeTime);
      return proof.Valid ? proof : Fail("USN outside journal range");
    }
    catch (Exception ex) { return Fail(ex.GetType().Name); }
  }
}
