using System;
using System.Globalization;
using ImagepixelBackup.Core;
using ImagepixelBackup.Core.Restic;

namespace ImagepixelBackup.App;

/// <summary>Turns values and errors into the German texts shown to the user.</summary>
public static class Texts
{
    public const string ProductName = "Imagepixel Backup";

    /// <summary>What happens to files of a cloud service that are not stored on the computer.</summary>
    public const string CloudHint =
        "Dateien, die nur online liegen, werden zum Sichern heruntergeladen und danach wieder freigegeben. "
        + "Was du mit „Immer auf diesem Gerät behalten“ markiert hast, bleibt auf der Festplatte.";

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

        if (Has("cannot find the path") || Has("cannot find the file") || error is System.IO.DirectoryNotFoundException)
        {
            return "Der Ordner wurde nicht gefunden. Ist die Festplatte angeschlossen?";
        }

        if (Has("winfsp"))
        {
            return "Zum Öffnen als Laufwerk fehlt die Komponente WinFsp. Bitte installiere " + ProductName + " erneut.";
        }

        if (error is System.ComponentModel.Win32Exception || Has("restic") && Has("not found"))
        {
            return "Das Backup-Programm restic wurde nicht gefunden. Bitte installiere " + ProductName + " erneut.";
        }

        return message;
    }
}
