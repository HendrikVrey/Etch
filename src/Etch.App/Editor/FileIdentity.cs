using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Etch.App.Editor;

/// <summary>
/// Answers "are these two paths the same file?" without believing the paths.
/// </summary>
/// <remarks>
/// <para>
/// Comparing path strings is wrong in more ways than it looks. Windows will hand the
/// same file back under an 8.3 short name (<c>C:\PROGRA~1\x.log</c>), through a
/// junction or symlink, through a mapped drive that is really a UNC share, and through
/// a hard link with an entirely different name in a different directory. Every one of
/// those defeats <see cref="string.Equals(string, string, StringComparison)"/>, and
/// each produced a second tab over one file: two shadow copies journaling the same
/// buffer and then racing each other on <c>Ctrl+S</c>, with no way for the user to
/// tell which write won.
/// </para>
/// <para>
/// <b>One comparison mode, not two.</b> The identity is reduced to a single opaque
/// string so that equality is ordinary string equality and the hash code is consistent
/// with it by construction. A type that compares by file id "when available" and by
/// path otherwise has two modes, and mixed comparisons between them are not
/// transitive, which is exactly the kind of defect that survives every test and then
/// loses somebody's file.
/// </para>
/// <para>
/// The strong key is the volume serial number plus the 128-bit file id, which is the
/// identity the filesystem itself uses: it survives all four aliases above, including
/// hard links. The full 128 bits are read rather than the older 64-bit index because
/// ReFS genuinely uses more than 64 and truncating them invents collisions.
/// </para>
/// <para>
/// When the filesystem cannot supply an id, some network redirectors do not, the key
/// falls back to the canonical path from <c>GetFinalPathNameByHandle</c>, which still
/// resolves short names, junctions, symlinks and mapped drives. That is strictly better
/// than the raw path it replaces; only hard links slip through it.
/// </para>
/// </remarks>
internal readonly partial record struct FileIdentity
{
    /// <summary>
    /// First guess at a canonical path length. Comfortably over <c>MAX_PATH</c>, so the
    /// second call is only ever needed for genuinely long paths.
    /// </summary>
    private const int InitialPathBuffer = 512;

    private FileIdentity(string key) => Key = key;

    /// <summary>The opaque key. Equal keys mean the same file.</summary>
    public string Key { get; }

    /// <summary>True when this identity was actually established.</summary>
    public bool IsKnown => !string.IsNullOrEmpty(Key);

    /// <summary>
    /// Reads the identity of <paramref name="path"/>, opening it briefly.
    /// </summary>
    /// <remarks>
    /// Read access with the most permissive sharing Etch can ask for, because the
    /// likeliest input is a log file another process still holds open. Nothing is read
    /// from the handle; it exists only to be asked what it points at.
    /// </remarks>
    /// <returns>False when the file cannot be opened at all, which the caller reports.</returns>
    public static bool TryRead(string path, out FileIdentity identity)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        try
        {
            using var handle = File.OpenHandle(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);

            identity = FromHandle(handle, path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            identity = default;
            return false;
        }
    }

    /// <summary>
    /// Reads the identity of an already-open handle.
    /// </summary>
    /// <remarks>
    /// Preferred over <see cref="TryRead"/> wherever a handle is already in hand: it is
    /// one fewer open, and it closes the window in which the path could be repointed at
    /// a different file between the identity check and the read.
    /// </remarks>
    /// <param name="handle">A handle to the file.</param>
    /// <param name="fallbackPath">Used only if every interop route fails.</param>
    public static FileIdentity FromHandle(SafeFileHandle handle, string fallbackPath)
    {
        ArgumentNullException.ThrowIfNull(handle);
        ArgumentException.ThrowIfNullOrWhiteSpace(fallbackPath);

        if (TryReadFileId(handle, out var volumeSerial, out var low, out var high))
        {
            return new FileIdentity($"id:{volumeSerial:x16}:{high:x16}{low:x16}");
        }

        var canonical = TryReadCanonicalPath(handle) ?? SafeFullPath(fallbackPath);

        // Upper-cased for the key rather than compared case-insensitively later, because
        // the key's whole purpose is that one ordinal comparison is the only rule.
        // Invariant casing, not the current culture: a Turkish locale maps 'I' to a
        // dotless lower-case form, so ToUpper on the current culture would make two
        // spellings of the same path produce two different keys on some machines.
        return new FileIdentity($"path:{canonical.ToUpperInvariant()}");
    }

    /// <summary>Best-effort absolute path, never throwing, this is already the fallback.</summary>
    private static string SafeFullPath(string path)
    {
        try
        {
            return Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return path;
        }
    }

    private static bool TryReadFileId(SafeFileHandle handle, out ulong volumeSerial, out ulong low, out ulong high)
    {
        volumeSerial = 0;
        low = 0;
        high = 0;

        FileIdInfo info = default;

        if (!Native.GetFileInformationByHandleEx(
                handle,
                Native.FileIdInfoClass,
                ref info,
                (uint)Marshal.SizeOf<FileIdInfo>()))
        {
            return false;
        }

        volumeSerial = info.VolumeSerialNumber;
        low = info.FileIdLow;
        high = info.FileIdHigh;

        // An all-zero id is not an identity, it is the absence of one, and treating it
        // as a real value would make every file on that volume identical to every other,
        // so the second file opened from such a share would silently activate the
        // first one's tab and never open at all.
        //
        // The volume serial is deliberately not part of this test. Some network
        // redirectors report a serial and a zero file id, which is precisely the case
        // that collides; including the serial would let a non-zero one vouch for an id
        // that says nothing.
        return (low | high) != 0;
    }

    private static unsafe string? TryReadCanonicalPath(SafeFileHandle handle)
    {
        const uint Flags = Native.FileNameNormalized | Native.VolumeNameDos;

        // The return value means two different things depending on whether it fitted:
        // the length *excluding* the terminator on success, and the length *including*
        // it when the buffer was too small. Sizing from a first attempt rather than from
        // a zero-length probe keeps the common case to a single call.
        var buffer = new char[InitialPathBuffer];

        for (var attempt = 0; attempt < 2; attempt++)
        {
            uint written;

            fixed (char* pinned = buffer)
            {
                written = Native.GetFinalPathNameByHandleW(handle, pinned, (uint)buffer.Length, Flags);
            }

            if (written == 0)
            {
                return null;
            }

            if (written < buffer.Length)
            {
                return Strip(new string(buffer, 0, (int)written));
            }

            // Too small. `written` now includes the terminator, so it is exactly the
            // size to allocate. One retry only: a path that grows again between the two
            // calls is being renamed underneath us, and looping on that is a hang.
            buffer = new char[written];
        }

        return null;
    }

    /// <summary>
    /// Removes the extended-length prefix, so the key and any message built from it
    /// read as the path the user recognises.
    /// </summary>
    private static string Strip(string path)
    {
        const string UncPrefix = @"\\?\UNC\";
        const string DevicePrefix = @"\\?\";

        if (path.StartsWith(UncPrefix, StringComparison.Ordinal))
        {
            return string.Concat(@"\\", path.AsSpan(UncPrefix.Length));
        }

        return path.StartsWith(DevicePrefix, StringComparison.Ordinal)
            ? path[DevicePrefix.Length..]
            : path;
    }

    /// <summary>
    /// The native <c>FILE_ID_INFO</c>.
    /// </summary>
    /// <remarks>
    /// The 128-bit id is carried as two <see cref="ulong"/>s rather than as a
    /// <see cref="UInt128"/> on purpose. Native layout puts the id at offset 8, after
    /// an 8-byte-aligned <c>ULONGLONG</c>; <see cref="UInt128"/> requires 16-byte
    /// alignment, so declaring it that way would have the marshaller place it at offset
    /// 16 and read eight bytes of padding as data. Two <c>ulong</c>s keep the offsets
    /// the same as the native struct's and stay blittable.
    /// </remarks>
    [StructLayout(LayoutKind.Sequential)]
    private struct FileIdInfo
    {
        public ulong VolumeSerialNumber;
        public ulong FileIdLow;
        public ulong FileIdHigh;
    }

    private static partial class Native
    {
        /// <summary><c>FILE_INFO_BY_HANDLE_CLASS.FileIdInfo</c>.</summary>
        public const int FileIdInfoClass = 18;

        /// <summary><c>FILE_NAME_NORMALIZED</c>.</summary>
        public const uint FileNameNormalized = 0x0;

        /// <summary><c>VOLUME_NAME_DOS</c>: a drive letter, or a UNC path for a mapped drive.</summary>
        public const uint VolumeNameDos = 0x0;

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool GetFileInformationByHandleEx(
            SafeFileHandle handle,
            int infoClass,
            ref FileIdInfo info,
            uint size);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        public static unsafe partial uint GetFinalPathNameByHandleW(
            SafeFileHandle handle,
            char* buffer,
            uint bufferLength,
            uint flags);
    }
}

/// <summary>
/// What the file looked like when Etch last agreed with it.
/// </summary>
/// <remarks>
/// <para>
/// Held so that <c>Ctrl+S</c> can notice somebody else changed the file since it was
/// opened. Without it the one write Etch makes outside its own data directory is a
/// silent overwrite of another editor's work, and it is silent in the worst way,
/// because the user asked for a save and got one.
/// </para>
/// <para>
/// Length and last-write time together, rather than a hash: hashing means reading the
/// whole file back on every save, which is unaffordable at the sizes this editor
/// opens. The pair misses only a change that preserves both, which needs deliberate
/// effort to construct. This is a guard against accident, and it says so.
/// </para>
/// </remarks>
/// <param name="Length">Size in bytes at the moment of reading.</param>
/// <param name="LastWriteUtc">Last-write timestamp at the moment of reading.</param>
internal readonly record struct FileWitness(long Length, DateTime LastWriteUtc)
{
    /// <summary>Reads the witness from an open handle.</summary>
    public static FileWitness FromHandle(SafeFileHandle handle)
    {
        ArgumentNullException.ThrowIfNull(handle);

        return new FileWitness(RandomAccess.GetLength(handle), File.GetLastWriteTimeUtc(handle));
    }

    /// <summary>Reads the witness of <paramref name="path"/>, opening it briefly.</summary>
    /// <returns>False when the file cannot be opened, which is itself worth reporting.</returns>
    public static bool TryRead(string path, [NotNullWhen(true)] out FileWitness? witness)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        try
        {
            using var handle = File.OpenHandle(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);

            witness = FromHandle(handle);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            witness = null;
            return false;
        }
    }
}

/// <summary>
/// What is at a path right now, including the possibility that nothing is.
/// </summary>
/// <remarks>
/// <para>
/// Exists so that "the file is missing" is a value rather than a null, which matters
/// because this type is compared against a record of what the user was warned about.
/// With a bare <c>FileWitness?</c>, a tab that had never been warned and a file that
/// had been deleted were both null, so they compared equal and the overwrite guard
/// waved through the very case it was written for. Two nulls meaning two different
/// things is the defect; giving the second one a value removes it.
/// </para>
/// </remarks>
/// <param name="Presence">Whether the file is there, gone, or merely unreadable.</param>
/// <param name="Witness">Meaningless unless <paramref name="Presence"/> is <see cref="DiskPresence.Present"/>.</param>
internal readonly record struct DiskState(DiskPresence Presence, FileWitness Witness)
{
    /// <summary>Reads what is at <paramref name="path"/> now.</summary>
    public static DiskState Read(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        try
        {
            using var handle = File.OpenHandle(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);

            return new DiskState(DiskPresence.Present, FileWitness.FromHandle(handle));
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return new DiskState(DiskPresence.Missing, default);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            // A scanner holding the file for a moment is not a deleted file, and saying
            // so would be the cry-wolf this guard is meant to avoid.
            return new DiskState(DiskPresence.Unreadable, default);
        }
    }

    /// <summary>True when this is the same file state <paramref name="expected"/> describes.</summary>
    public bool Matches(FileWitness expected) => Presence == DiskPresence.Present && Witness == expected;
}

/// <summary>Whether a path could be read, and if not, why not.</summary>
internal enum DiskPresence
{
    /// <summary>
    /// The file, or its directory, is not there. First so that a default
    /// <see cref="DiskState"/> claims nothing rather than claiming a zero-length file.
    /// </summary>
    Missing,

    /// <summary>The file was opened and measured.</summary>
    Present,

    /// <summary>Something is there but it could not be opened: a lock, or a denial.</summary>
    Unreadable,
}
