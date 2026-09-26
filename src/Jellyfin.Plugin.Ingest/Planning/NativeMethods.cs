using System;
using System.IO;
using System.Runtime.InteropServices;

namespace Jellyfin.Plugin.Ingest.Planning;

/// <summary>
/// Hard links, which .NET has no API for: <c>link(2)</c> on Linux and macOS, <c>CreateHardLinkW</c> on Windows.
/// </summary>
internal static partial class NativeMethods
{
    /// <summary>
    /// Makes a hard link, if the file system allows it.
    /// </summary>
    /// <param name="existing">The existing file.</param>
    /// <param name="link">The new name.</param>
    /// <returns>Whether it was made.</returns>
    public static bool TryHardLink(string existing, string link)
    {
        try
        {
            return OperatingSystem.IsWindows() ? CreateHardLinkW(link, existing, IntPtr.Zero) : LinkUnix(existing, link) == 0;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }

    [LibraryImport("libc", EntryPoint = "link", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static partial int LinkUnix(string existing, string link);

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CreateHardLinkW(string fileName, string existingFileName, IntPtr securityAttributes);
}
