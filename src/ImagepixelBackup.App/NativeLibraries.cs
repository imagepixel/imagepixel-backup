using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace ImagepixelBackup.App;

/// <summary>
/// Keeps the libraries for text and drawing that come with the application apart from
/// the ones of the system.
/// </summary>
/// <remarks>
/// Linux: the application brings its own HarfBuzz, and recent systems have one too,
/// which their FreeType loads for the whole process the first time it reads a font
/// file — when the caches of fontconfig are missing or a font was added. From then on
/// every function of our HarfBuzz that had not been called yet would be looked up in
/// the one of the system, and the mix of the two ends the application with
/// "free(): invalid pointer". Looking all of them up at the start, before any font is
/// read, keeps ours whole.
/// </remarks>
public static class NativeLibraries
{
    private const int LookUpEverythingNow = 2; // RTLD_NOW

    private static readonly string[] Libraries = ["libHarfBuzzSharp.so", "libSkiaSharp.so"];

    /// <summary>Must be called before anything draws or measures text.</summary>
    public static void BindEarly()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        foreach (var library in Libraries)
        {
            if (Find(library) is { } path)
            {
                Open(path);
            }
        }
    }

    // next to the application once it is published, in a folder for the kind of system before
    private static string? Find(string library)
    {
        var direct = Path.Combine(AppContext.BaseDirectory, library);
        if (File.Exists(direct))
        {
            return direct;
        }

        var native = Path.Combine(AppContext.BaseDirectory, "runtimes", RuntimeInformation.RuntimeIdentifier, "native", library);
        if (File.Exists(native))
        {
            return native;
        }

        var architecture = "linux-" + RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant();
        var runtimes = Path.Combine(AppContext.BaseDirectory, "runtimes");
        return Directory.Exists(runtimes)
            ? Directory.EnumerateFiles(runtimes, library, SearchOption.AllDirectories)
                .FirstOrDefault(file => file.Contains(Path.DirectorySeparatorChar + architecture + Path.DirectorySeparatorChar))
            : null;
    }

    private static void Open(string path)
    {
        try
        {
            dlopen(path, LookUpEverythingNow);
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            try
            {
                // systems before 2021 keep the function in a library of its own
                dlopen_old(path, LookUpEverythingNow);
            }
            catch (Exception inner) when (inner is DllNotFoundException or EntryPointNotFoundException)
            {
                // without it the application starts as it always did
            }
        }
    }

    [DllImport("libc.so.6", EntryPoint = "dlopen")]
    private static extern IntPtr dlopen(string path, int flags);

    [DllImport("libdl.so.2", EntryPoint = "dlopen")]
    private static extern IntPtr dlopen_old(string path, int flags);
}
