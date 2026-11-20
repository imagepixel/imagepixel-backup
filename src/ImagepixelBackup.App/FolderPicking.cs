using System;
using System.IO;
using System.Threading.Tasks;

namespace ImagepixelBackup.App;

/// <summary>
/// Asks for a folder on this computer. The dialog of the system also lets one choose places
/// that are no folder on the disk, such as a library or "Dieser PC". Choosing one of those
/// opens the dialog again with a hint in its title, instead of ending the application.
/// </summary>
public static class FolderPicking
{
    /// <summary>How often the dialog is opened again before the choice counts as cancelled.</summary>
    public const int MaxAttempts = 5;

    /// <summary>Lets the user choose a folder: its local path, or null if the dialog was cancelled.</summary>
    /// <param name="open">
    /// Opens the dialog of the system with the given title and returns the local path of the
    /// folder chosen, or null if it was cancelled. Throws an <see cref="ArgumentException"/>
    /// or an <see cref="IOException"/> for a place that is no folder on the disk.
    /// </param>
    /// <param name="title">The title of the dialog.</param>
    public static async Task<string?> PickAsync(Func<string, Task<string?>> open, string title)
    {
        var shown = title;
        for (var attempt = 0; attempt < MaxAttempts; attempt++)
        {
            try
            {
                return await open(shown);
            }
            catch (Exception e) when (e is ArgumentException or IOException)
            {
                // Avalonia turns down such a place with "Directory must exist"
                shown = Texts.NotAFolder(title);
            }
        }

        return null;
    }
}
