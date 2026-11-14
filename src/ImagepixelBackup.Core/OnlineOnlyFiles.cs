using System.IO.Enumeration;
using System.Runtime.Versioning;

namespace ImagepixelBackup.Core;

/// <summary>Where the content of a file in a cloud folder is.</summary>
public enum CloudFileState
{
    NotFound,

    /// <summary>Only stored online. Reading the file downloads it.</summary>
    OnlineOnly,

    /// <summary>Stored on this computer, and the service may free it up again.</summary>
    Local,

    /// <summary>The user chose to always keep the file on this computer.</summary>
    Pinned,
}

/// <summary>What the backup needs to know about, and do with, files of cloud storage services.</summary>
public interface ICloudFileSystem
{
    /// <summary>Returns the files below a folder that are only stored online.</summary>
    IEnumerable<string> FindOnlineOnly(string folder);

    CloudFileState GetState(string path);

    /// <summary>
    /// Asks the cloud service to make a file online-only again. The service does so
    /// shortly afterwards. Returns false if the request was not possible right now.
    /// </summary>
    bool FreeUp(string path);
}

/// <summary>
/// Keeps a backup from filling the disk. Backing up a file that is only stored online
/// makes the cloud service download it. This class remembers which files were
/// online-only before the backup and frees them up again as soon as they are saved.
/// Files the user chose to always keep on the computer are never touched: they are
/// not online-only to begin with, and a file that was pinned meanwhile is left alone.
/// </summary>
public sealed class OnlineOnlyGuard
{
    private readonly ICloudFileSystem _fileSystem;
    private readonly HashSet<string> _pending;
    private int _freed;

    private OnlineOnlyGuard(ICloudFileSystem fileSystem, HashSet<string> onlineOnly)
    {
        _fileSystem = fileSystem;
        _pending = onlineOnly;
        Count = onlineOnly.Count;
    }

    /// <summary>How many files were online-only before the backup.</summary>
    public int Count { get; }

    /// <summary>How many files have been made online-only again.</summary>
    public int Freed => _freed;

    /// <summary>Looks for online-only files below a folder. Returns null if there are none.</summary>
    public static OnlineOnlyGuard? TryCreate(string folder, ICloudFileSystem fileSystem)
    {
        var onlineOnly = new HashSet<string>(fileSystem.FindOnlineOnly(folder), StringComparer.OrdinalIgnoreCase);
        return onlineOnly.Count == 0 ? null : new OnlineOnlyGuard(fileSystem, onlineOnly);
    }

    /// <summary>Called for every file the backup has finished with.</summary>
    public void FileSaved(string path)
    {
        lock (_pending)
        {
            if (_pending.Contains(path) && TryFreeUp(path))
            {
                _pending.Remove(path);
            }
        }
    }

    /// <summary>
    /// Frees up what is still on the computer after the backup has ended, whether it
    /// succeeded, failed or was cancelled.
    /// </summary>
    public void Finish()
    {
        lock (_pending)
        {
            _pending.RemoveWhere(TryFreeUp);
        }
    }

    // Returns true when nothing is left to do for the file.
    private bool TryFreeUp(string path)
    {
        switch (_fileSystem.GetState(path))
        {
            case CloudFileState.Local:
                if (_fileSystem.FreeUp(path))
                {
                    _freed++;
                    return true;
                }

                // still in use, the final pass tries again
                return false;

            default:
                // never downloaded, pinned by the user meanwhile, or gone
                return true;
        }
    }
}

/// <summary>
/// Cloud files on Windows. OneDrive, iCloud Drive and others use the same mechanism
/// of the operating system (Files On-Demand), which marks files with attributes.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsCloudFileSystem : ICloudFileSystem
{
    private const int AttributePinned = 0x00080000;
    private const int AttributeUnpinned = 0x00100000;
    private const int AttributeRecallOnDataAccess = 0x00400000;

    public IEnumerable<string> FindOnlineOnly(string folder)
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = 0,
        };

        // Listing a folder only reads attributes, it does not download anything.
        return new FileSystemEnumerable<string>(folder, (ref FileSystemEntry entry) => entry.ToFullPath(), options)
        {
            ShouldIncludePredicate = (ref FileSystemEntry entry) =>
                !entry.IsDirectory && ((int)entry.Attributes & AttributeRecallOnDataAccess) != 0,
        };
    }

    public CloudFileState GetState(string path)
    {
        int attributes;
        try
        {
            attributes = (int)File.GetAttributes(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return CloudFileState.NotFound;
        }

        if ((attributes & AttributeRecallOnDataAccess) != 0)
        {
            return CloudFileState.OnlineOnly;
        }

        return (attributes & AttributePinned) != 0 ? CloudFileState.Pinned : CloudFileState.Local;
    }

    /// <summary>
    /// Marks the file as "free up space", the same as the command in the context menu
    /// of the Explorer. The cloud service then removes the local content shortly after.
    /// </summary>
    /// <remarks>
    /// Asking Windows to drop the content directly (CfDehydratePlaceholder) is not an
    /// option: with OneDrive the call does not return, and while it waits the open file
    /// keeps OneDrive from freeing the file itself. Setting the attributes needs no
    /// open file, so it cannot get in the way.
    /// </remarks>
    public bool FreeUp(string path)
    {
        try
        {
            var attributes = (int)File.GetAttributes(path);
            File.SetAttributes(path, (FileAttributes)((attributes | AttributeUnpinned) & ~AttributePinned));
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
