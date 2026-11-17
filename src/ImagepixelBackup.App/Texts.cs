using System;
using System.Globalization;
using ImagepixelBackup.Core;
using ImagepixelBackup.Core.Restic;

namespace ImagepixelBackup.App;

/// <summary>Turns values and errors into the German texts shown to the user.</summary>
public static class Texts
{
    public const string ProductName = "Imagepixel Backup";

    /// <summary>
    /// The version of the notes on use and liability, which is the date they were last
    /// changed. The full text is the file NUTZUNGSHINWEISE.md. The user confirms the
    /// notes once at the first start; changing this value asks everyone once more.
    /// </summary>
    public const string TermsVersion = "2026-11-17-2";

    /// <summary>The short form of the notes, shown once at the first start.</summary>
    public const string TermsSoftware =
        "Imagepixel Backup ist kostenlose, quelloffene Software und wird ohne Gewährleistung bereitgestellt. "
        + "Du nutzt das Programm auf eigene Gefahr. Für Fehler des Programms und für den Verlust von Daten haftet Imagepixel nur dort, "
        + "wo das Gesetz es zwingend vorschreibt, vor allem bei Vorsatz und grober Fahrlässigkeit. Die Fälle stehen in den Nutzungshinweisen.";

    public const string TermsStorage =
        "Kein Speicher ist vor jedem Ausfall sicher, auch Imagepixel Storage nicht. Verlass dich bei wichtigen Daten "
        + "nicht auf eine einzige Sicherung, und prüfe von Zeit zu Zeit, ob sich Dateien wiederherstellen lassen.";

    public const string TermsPassword =
        "Dein Passwort kennst nur du. Ohne das Passwort kommt niemand mehr an deine Sicherungen, auch Imagepixel nicht.";

    public const string TermsConfirm = "Ich bin mit den Nutzungshinweisen einverstanden.";

    /// <summary>What the info page says about the notes the user confirmed.</summary>
    public const string TermsReminder =
        "Imagepixel Backup ist kostenlose, quelloffene Software ohne Gewährleistung; du nutzt sie auf eigene Gefahr. Die Hinweise zur Nutzung und Haftung, "
        + "die du beim ersten Start bestätigt hast, kannst du hier nachlesen.";

    /// <summary>What the guide explains. Linux has no cloud storage to back up.</summary>
    public static string GuideHint => CloudFolderDetector.IsSupported
        ? "Schritt für Schritt erklärt: Backup einrichten, Daten wiederherstellen, Cloud-Speicher sichern und was die Meldungen des Programms bedeuten."
        : "Schritt für Schritt erklärt: Backup einrichten, Daten wiederherstellen und was die Meldungen des Programms bedeuten.";

    /// <summary>
    /// What happens to files of a cloud service that are not stored on the computer.
    /// Freeing them up again after the backup is only implemented for Windows.
    /// </summary>
    public static string CloudHint => OperatingSystem.IsWindows()
        ? "Dateien, die nur online liegen, werden zum Sichern heruntergeladen und danach wieder freigegeben. "
          + "Was du mit „Immer auf diesem Gerät behalten“ markiert hast, bleibt auf der Festplatte."
        : "Dateien, die nur online liegen, werden zum Sichern heruntergeladen und bleiben danach auf diesem Rechner. "
          + "Achte darauf, dass genug Speicherplatz frei ist.";

    /// <summary>An example of where a backup on an external disk could be.</summary>
    public static string LocalPathExample =>
        OperatingSystem.IsWindows() ? @"z. B. E:\Backup"
        : OperatingSystem.IsMacOS() ? "z. B. /Volumes/Backup"
        : $"z. B. {LinuxMediaFolder}/{Environment.UserName}/Backup";

    // where a USB disk shows up: Ubuntu uses /media, CachyOS and other Arch systems /run/media
    private static string LinuxMediaFolder => System.IO.Directory.Exists("/run/media") ? "/run/media" : "/media";

    /// <summary>An example of a folder to restore into, written the way this system writes paths.</summary>
    public static string RestorePathExample => OperatingSystem.IsWindows()
        ? @"z. B. C:\Wiederhergestellt"
        : "z. B. " + System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Wiederhergestellt");

    /// <summary>A number of files, for example "1 Datei" or "14 Dateien".</summary>
    public static string Files(long count) => count == 1 ? "1 Datei" : $"{count} Dateien";

    /// <summary>What a backup found to save, for example "14 neue oder geänderte Dateien".</summary>
    public static string ChangedFiles(long count) =>
        count == 1 ? "1 neue oder geänderte Datei" : $"{count} neue oder geänderte Dateien";

    /// <summary>What the program is called that shows folders and files.</summary>
    public static string FileManager => OperatingSystem.IsWindows() ? "Explorer" : "Dateimanager";

    /// <summary>Where the application waits while its window is closed.</summary>
    public static string TrayPlace => OperatingSystem.IsMacOS() ? "in der Menüleiste" : "im Infobereich";

    /// <summary>What the restore page says about getting single files.</summary>
    public static string RestoreHint => BackupManager.IsMountSupported
        ? $"Wähle den Stand aus, den du als Ganzes zurückholen möchtest. Brauchst du nur einzelne Dateien, öffne das Backup als Laufwerk und kopiere sie im {FileManager} heraus."
        : "Wähle den Stand aus, den du zurückholen möchtest, und einen Ordner, in den er gelegt wird.";

    /// <summary>What opening the backup as a drive does.</summary>
    public static string DriveHint =>
        $"Zeigt alle gesicherten Stände wie eine Festplatte im {FileManager}. Du kannst Dateien ansehen und herauskopieren. "
        + "Das Laufwerk ist schreibgeschützt, das Backup kann dabei nicht verändert werden.";

    public static string ShowDrive => $"Im {FileManager} anzeigen";

    /// <summary>Says that the drive is open. Only Windows gives it a letter worth naming.</summary>
    public static string DriveOpen(string mountpoint) =>
        OperatingSystem.IsWindows() ? $"Laufwerk {mountpoint} ist geöffnet" : "Das Laufwerk ist geöffnet";

    public static string DriveReady(string mountpoint, string folder) => OperatingSystem.IsWindows()
        ? $"Deine Sicherungen stehen als Laufwerk {mountpoint} bereit – nur zum Lesen."
        : $"Deine Sicherungen stehen als Laufwerk bereit – nur zum Lesen. Du findest sie im {FileManager} unter {folder}.";

    /// <summary>What starting at login means on this system.</summary>
    public static string StartAtLoginHint =>
        $"Damit deine Sicherungen ohne dein Zutun laufen, kann das Programm bei jeder Anmeldung starten. Es wartet dann {TrayPlace}.";

    public static string StartsAtLoginNow =>
        $"Imagepixel Backup startet jetzt bei jeder Anmeldung von selbst und wartet {TrayPlace}.";

    /// <summary>macOS: why the application cannot start at login from where it currently lies.</summary>
    public const string MoveToApplications =
        "Bitte ziehe Imagepixel Backup zuerst in den Ordner „Programme“ und öffne es von dort. "
        + "Erst dann kann es bei der Anmeldung von selbst starten.";

    /// <summary>The same for a program that was only unpacked to try it.</summary>
    public const string InstallFirst =
        "Bitte installiere Imagepixel Backup zuerst. Von seinem jetzigen Ort aus kann es bei der Anmeldung nicht von selbst starten.";

    private static readonly CultureInfo German = CultureInfo.GetCultureInfo("de-DE");

    public static string Bytes(long bytes)
    {
        string[] units = ["Bytes", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0
            ? $"{bytes} {units[0]}"
            : string.Create(German, $"{value:0.#} {units[unit]}");
    }

    /// <summary>Formats a share as a whole percentage, for example "86 %".</summary>
    public static string Percent(double fraction) =>
        string.Create(German, $"{Math.Round(fraction * 100):0} %");

    /// <summary>Formats a point in time relative to today, for example "heute, 15:30".</summary>
    public static string Day(DateTimeOffset time, DateTimeOffset now)
    {
        var local = time.ToLocalTime();
        var days = (now.ToLocalTime().Date - local.Date).Days;
        var clock = local.ToString("HH:mm", German);
        return days switch
        {
            0 => $"heute, {clock}",
            1 => $"gestern, {clock}",
            -1 => $"morgen, {clock}",
            _ => local.ToString("dd.MM.yyyy, HH:mm", German),
        };
    }

    public static string StorageName(StorageSettings storage) => storage.Kind switch
    {
        StorageKind.ImagepixelStorage => $"Imagepixel Storage · {storage.Bucket}",
        _ => $"Ordner · {storage.LocalPath}",
    };

    /// <summary>Why a folder to back up and the folder of the backup must not lie inside each other.</summary>
    public static string Overlap(string folder, string storage) =>
        $"Das Backup kann nicht in einem Ordner liegen, der selbst gesichert wird – es würde sich bei jedem Lauf mitsichern. "
        + $"„{folder}“ und der Backup-Ordner „{storage}“ liegen ineinander. Bitte wähle für eines von beiden einen anderen Ort.";

    /// <summary>What to know before storing the backup in the folder of a cloud service.</summary>
    public static string CloudDestination(string service) =>
        $"Dieser Ordner gehört zu {service}. Das Backup belegt dann zuerst Platz auf deiner Festplatte und ist erst in der Cloud, "
        + $"wenn {service} es hochgeladen hat. Sichere außerdem nie in denselben Cloud-Speicher, den du sichern möchtest.";

    /// <summary>Explains an error in words a customer can act on.</summary>
    public static string Error(Exception error)
    {
        var message = error.Message;
        bool Has(string text) => message.Contains(text, StringComparison.OrdinalIgnoreCase);

        if (error is FolderOverlapException overlap)
        {
            return Overlap(overlap.Folder, overlap.Storage);
        }

        if (error is ResticException { ExitCode: ResticException.ExitWrongPassword })
        {
            return "Das Passwort passt nicht zu diesem Backup.";
        }

        if (error is ResticException { ExitCode: ResticException.ExitRepositoryLocked })
        {
            return "Das Backup wird gerade von einem anderen Vorgang benutzt. Bitte versuche es in ein paar Minuten noch einmal.";
        }

        if (Has("InvalidAccessKeyId") || Has("SignatureDoesNotMatch") || Has("access key"))
        {
            return "Der Zugangsschlüssel oder der geheime Schlüssel stimmt nicht.";
        }

        if (Has("Access Denied") || Has("AccessDenied"))
        {
            return "Der Zugriff wurde verweigert. Bitte prüfe Bucket-Name und Zugangsschlüssel.";
        }

        if (Has("NoSuchBucket") || Has("bucket does not exist"))
        {
            return "Diesen Bucket gibt es nicht. Bitte prüfe den Bucket-Namen.";
        }

        if (Has("no such host") || Has("dial tcp") || Has("i/o timeout") || Has("connection refused"))
        {
            return "Der Speicher ist nicht erreichbar. Besteht eine Internetverbindung?";
        }

        if (Has("cannot find the path") || Has("cannot find the file") || Has("no such file or directory")
            || error is System.IO.DirectoryNotFoundException)
        {
            return "Der Ordner wurde nicht gefunden. Ist die Festplatte angeschlossen?";
        }

        if (Has("winfsp"))
        {
            return "Zum Öffnen als Laufwerk fehlt die Komponente WinFsp. Bitte installiere " + ProductName + " erneut.";
        }

        if (Has("fusermount") || Has("/dev/fuse"))
        {
            return "Zum Öffnen als Laufwerk fehlt die Komponente FUSE (Paket „fuse3“). Bitte installiere " + ProductName + " erneut.";
        }

        if (error is System.Security.SecurityException)
        {
            return "Der Schlüsselbund dieses Rechners ist nicht erreichbar, deshalb kann das Passwort nicht sicher abgelegt werden. " + message;
        }

        if (error is System.ComponentModel.Win32Exception || Has("restic") && Has("not found"))
        {
            return "Das Backup-Programm restic wurde nicht gefunden. Bitte installiere " + ProductName + " erneut.";
        }

        return message;
    }
}
