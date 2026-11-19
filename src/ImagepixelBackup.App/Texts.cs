using System;
using System.Globalization;
using System.Linq;
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

    /// <summary>The name the drive with the encrypted files shows.</summary>
    public const string EncryptedVolumeName = "Imagepixel Backup (verschlüsselt)";

    /// <summary>What opening the backup encrypted does.</summary>
    public const string EncryptedHint =
        "„Verschlüsselt öffnen“ zeigt dieselben Sicherungen so, wie sie im Speicher liegen: als verschlüsselte Dateien mit Prüfsummen als Namen. "
        + "Ohne dein Passwort kann niemand sie lesen.";

    public static string EncryptedDriveOpen(string mountpoint) =>
        OperatingSystem.IsWindows() ? $"Laufwerk {mountpoint} zeigt die verschlüsselten Dateien" : "Das Laufwerk zeigt die verschlüsselten Dateien";

    public static string EncryptedDriveReady(string mountpoint, string folder) =>
        (OperatingSystem.IsWindows() ? $"Laufwerk {mountpoint} zeigt deine Sicherungen so, wie sie im Speicher liegen" : $"Unter {folder} siehst du deine Sicherungen so, wie sie im Speicher liegen")
        + ": verschlüsselt und nur zum Lesen. Öffnest du eine Datei mit einem Editor, ist ihr Inhalt unlesbar.";

    public static string EncryptedFolderOpened(string folder) =>
        $"Der {FileManager} zeigt den Ordner {folder}, so wie die Dateien dort liegen: verschlüsselt, mit Prüfsummen als Namen.";

    /// <summary>What stands in front of the drive letter the backup will be opened under.</summary>
    public const string DriveLetterLabel = "Verbinden als Laufwerk";

    public const string NoDriveLetter = "Es ist kein Laufwerksbuchstabe frei. Trenne ein anderes Laufwerk, um das Backup zu öffnen.";

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

    /// <summary>Formats how fast a backup gets through the data, for example "4,2 MB/s".</summary>
    public static string Speed(double bytesPerSecond) => Bytes((long)Math.Max(0, bytesPerSecond)) + "/s";

    /// <summary>
    /// Shortens a long path in the middle, so that its beginning and the name of the file
    /// stay readable: "Code\…\src\Datei.cs".
    /// </summary>
    public static string ShortenPath(string path, int maxLength)
    {
        if (path.Length <= maxLength)
        {
            return path;
        }

        var separator = System.IO.Path.DirectorySeparatorChar;
        var first = path.IndexOf(separator);
        var head = first is > 0 and < 24 ? path[..(first + 1)] + "…" : "…";

        // as much of the end as fits, starting at a folder if there is one
        var tail = path[^Math.Max(1, maxLength - head.Length)..];
        var cut = tail.IndexOf(separator);
        return head + (cut >= 0 && cut < tail.Length - 1 ? tail[cut..] : tail);
    }

    /// <summary>What the button and the page are called that renew the access data or move to another storage.</summary>
    public const string ChangeStorage = "Einrichtungscode oder Speicher ändern";

    /// <summary>What the info page says about it.</summary>
    public const string ChangeStorageHint =
        "Das brauchst du nur, wenn du von Imagepixel einen neuen Einrichtungscode bekommen hast, zum Beispiel nach einem Tausch der Zugangsdaten, "
        + "oder wenn deine Sicherungen künftig an einem anderen Ort liegen sollen. Deine gesicherten Ordner und ihre Einstellungen bleiben dabei erhalten.";

    /// <summary>What the setup says when the customer came to renew the access data or move to another storage.</summary>
    public static string SwitchNote(string storage) =>
        $"Du bist mit „{storage}“ verbunden. Hier gibst du einen neuen Einrichtungscode ein, den du von Imagepixel bekommen hast, "
        + "oder richtest einen anderen Speicher ein. Solange du nichts speicherst, bleibt alles, wie es ist, und deine Sicherungen laufen weiter.";

    /// <summary>What step 1 says about the folders that are backed up already.</summary>
    public const string KeptFoldersNote =
        "Das sind deine bisherigen Ordner. Sie behalten ihre Einstellungen: Zeitplan und Ausnahmen. "
        + "Was du hier entfernst, wird nach dem Speichern nicht mehr gesichert; seine bisherigen Sicherungen bleiben erhalten.";

    /// <summary>What the setup says once it is clear that the storage stays the same.</summary>
    public const string SameStorageNote =
        "Das ist dein bisheriger Speicher. Es werden nur die Angaben zum Speicher erneuert: Deine Ordner, ihre Ziele und alle Sicherungen bleiben, wie sie sind. "
        + "Dein Verschlüsselungs-Passwort bleibt dasselbe.";

    /// <summary>What the setup says once it is clear that the backups will go to another storage.</summary>
    public const string OtherStorageNote =
        "Das ist ein anderer Speicher. Deine Ordner kommen mit ihren Einstellungen mit und werden dort von vorn gesichert. "
        + "Deine bisherigen Sicherungen bleiben im alten Speicher und werden nicht gelöscht; geplantes Löschen alter Sicherungen entfällt.";

    /// <summary>What the overview says when the storage turns down the access data.</summary>
    public const string AccessProblem =
        "Der Speicher lehnt die Zugangsdaten ab, deshalb schlagen die Sicherungen fehl. Hast du von Imagepixel einen neuen Einrichtungscode bekommen, "
        + "gib ihn hier ein. Deine Ordner und ihre Einstellungen bleiben dabei erhalten.";

    /// <summary>Whether the error of a backup means that the storage turns down the access data.</summary>
    public static bool IsAccessProblem(string? error) =>
        error is not null
        && new[] { "InvalidAccessKeyId", "SignatureDoesNotMatch", "AccessDenied", "Access Denied", "NoSuchBucket", "bucket does not exist" }
            .Any(text => error.Contains(text, StringComparison.OrdinalIgnoreCase));

    public const string FolderLabel = "Ordner im Speicher (optional)";

    public const string FolderExample = "z. B. Notebook";

    public const string FolderHintBackup =
        "Unter diesem Ordner legt das Programm deine Sicherungen im Speicher ab. So bleiben mehrere Rechner in einem Speicher getrennt. "
        + "Ohne Angabe liegen sie direkt im Speicher. Später kannst du das Ziel für jeden Ordner einzeln ändern.";

    public const string FolderHintRestore =
        "Trage den Ordner ein, wenn du beim Einrichten des Backups einen angegeben hast. Sonst lass das Feld leer.";

    /// <summary>How a backup without a folder is called where the found backups are offered.</summary>
    public const string NoFolder = "Ohne Ordner (direkt im Speicher)";

    public const string FoundBackupsBackup =
        "In deinem Speicher liegen schon Backups. Wähle eines aus, um es weiterzuführen, oder gib einen neuen Ordner an:";

    public const string FoundBackupsRestore = "In deinem Speicher liegen diese Backups. Wähle aus, welches du öffnen möchtest:";

    public const string NoBackupHereButElsewhere =
        "An diesem Ort wurde kein Backup gefunden. In deinem Speicher liegen aber Backups an anderer Stelle: Wähle oben eines davon aus.";

    /// <summary>Says what is wrong with the name of a folder in the storage, or null if nothing is.</summary>
    public static string? FolderProblem(StorageFolderProblem problem) => problem switch
    {
        StorageFolderProblem.Characters =>
            "Der Ordnername darf nur Buchstaben, Ziffern, Leerzeichen, Punkt, Bindestrich und Unterstrich enthalten. "
            + "Unterordner trennst du mit einem Schrägstrich.",
        StorageFolderProblem.Reserved => "Diesen Namen verwendet der Speicher selbst. Bitte wähle einen anderen.",
        StorageFolderProblem.TooLong => $"Der Ordnername ist zu lang. Er darf höchstens {StorageFolder.MaxLength} Zeichen haben.",
        _ => null,
    };

    public const string SetupCodeAccepted = "Einrichtungscode erkannt.";

    public const string SetupCodeProblem =
        "Das ist kein gültiger Einrichtungscode. Bitte füge ihn vollständig ein, so wie du ihn von Imagepixel bekommen hast.";

    public const string SetupCodeMissing = "Bitte füge den Einrichtungscode ein, den du von Imagepixel bekommen hast.";

    /// <summary>How the overview names what a folder leaves out, for example "Ausnahmen: 3 Regeln".</summary>
    public static string Exclusions(int count) => count == 1 ? "Ausnahmen: 1 Regel" : $"Ausnahmen: {count} Regeln";

    public const string ExclusionsNote =
        "Ohne Ausnahmen wird der Ordner vollständig gesichert. Was du hier auslässt, fehlt in den Sicherungen, die ab jetzt entstehen, "
        + "und lässt sich aus ihnen nicht wiederherstellen. Was schon gesichert ist, bleibt in den älteren Ständen erhalten.";

    public const string ExclusionsRulesHint =
        "Eine Regel gilt in allen Unterordnern, Groß- und Kleinschreibung spielt keine Rolle. "
        + "Ein Name trifft Ordner und Dateien, die so heißen.";

    public const string ExclusionsGitWarning =
        "Achtung: Damit wird auch der Ordner „.git“ ausgelassen. Er enthält den gesamten Verlauf eines Projekts.";

    public const string SuggestionIntro =
        "In diesem Ordner liegen Dinge, die sich meist neu erzeugen oder leicht wieder beschaffen lassen. "
        + "Wenn du sie auslässt, wird deine Sicherung kleiner und schneller. Die Sicherung startet, sobald du dich entschieden hast.";

    public const string SuggestionLater =
        "Die Größen sind die auf deinem Rechner; im Backup fällt die Ersparnis meist etwas kleiner aus. "
        + "Du kannst die Ausnahmen später jederzeit unter „Ausnahmen …“ ändern.";

    /// <summary>What a folder says while its first backup waits for the answer to the suggestion.</summary>
    public static string SuggestionWaiting(long bytes) =>
        $"Vor der ersten Sicherung: In diesem Ordner lassen sich etwa {Bytes(bytes)} auslassen. Die Sicherung startet, sobald du dich entschieden hast.";

    /// <summary>The plain words a ready-made suggestion is offered in.</summary>
    public static string PresetTitle(string id) => id switch
    {
        "temporary" => "Keine temporären Dateien",
        "build-output" => "Keine Ordner, die Entwicklungswerkzeuge neu erzeugen",
        "videos" => "Keine Videos",
        "installers" => "Keine Installationsdateien und Abbilder",
        _ => id,
    };

    /// <summary>A rule of the customer's own as a sentence.</summary>
    public static string Rule(ExclusionKind kind, string value) => kind switch
    {
        ExclusionKind.Extension => $"Dateien mit der Endung .{value}",
        ExclusionKind.NamePrefix => $"Namen, die mit „{value}“ beginnen",
        _ => $"Dateien und Ordner mit dem Namen „{value}“",
    };

    /// <summary>What a paused folder says about going on.</summary>
    public const string PauseHint = "Beim Fortsetzen wird nicht noch einmal übertragen, was schon im Backup liegt.";

    /// <summary>The button that shows what a running backup is working on, and the one that hides it again.</summary>
    public const string DetailsShow = "Im Detail anzeigen";

    public const string DetailsHide = "Details ausblenden";

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

    /// <summary>
    /// Where in the storage a destination is, to stand in a sentence: "im Ordner „Notebook“"
    /// or "direkt im Speicher".
    /// </summary>
    public static string DestinationPhrase(StorageSettings? storage, string destination) =>
        destination.Length > 0 ? $"im Ordner „{destination}“"
        : storage is { IsS3: false } ? "direkt im Backup-Ordner"
        : "direkt im Speicher";

    /// <summary>The same as a label, for a button or a list: "Notebook" or "Ohne Ordner (direkt im Speicher)".</summary>
    public static string DestinationLabel(StorageSettings? storage, string destination) =>
        destination.Length > 0 ? destination : $"Ohne Ordner ({DestinationPhrase(storage, destination)})";

    /// <summary>What a folder in the overview says about where its backups go.</summary>
    public static string DestinationLine(StorageSettings? storage, string destination) =>
        destination.Length > 0 ? $"Ziel: Ordner „{destination}“" : $"Ziel: {DestinationPhrase(storage, destination)}";

    public const string DestinationNote =
        "Hier legst du fest, in welchem Ordner deines Speichers die Sicherungen dieses Ordners liegen. "
        + "Ein Ordner, in dem noch nichts liegt, beginnt als neues Backup mit deinem Verschlüsselungs-Passwort.";

    public const string DestinationsFound = "In deinem Speicher gibt es diese Ziele:";

    /// <summary>The question about what becomes of the backups at the destination a folder leaves.</summary>
    public static string OldBackupsQuestion(string folder, string oldPlace) =>
        $"Was soll mit den bisherigen Sicherungen von „{folder}“ {oldPlace} passieren?";

    public const string OldBackupsHint =
        "Gelöscht werden nur die Sicherungen dieses Ordners. Bis dahin kannst du sie weiter wiederherstellen, und sie belegen weiter Speicher. "
        + "Gelöscht wird, wenn das Programm nach Ablauf der Frist läuft.";

    public const string OldBackupsUnanswered = "Bitte wähle aus, was mit den bisherigen Sicherungen passieren soll.";

    public const string DestinationWhileRunning = "Während dieser Ordner gesichert wird, lässt sich sein Ziel nicht ändern. Bitte warte, bis die Sicherung fertig ist.";

    public static string DestinationOtherPassword(string place) =>
        $"{Capitalize(place)} liegt schon ein Backup mit einem anderen Verschlüsselungs-Passwort. Bitte wähle ein anderes Ziel.";

    /// <summary>What the overview says once the destination of a folder has changed.</summary>
    public static string DestinationChanged(string folder, string place, int? deleteOldAfterDays) =>
        $"„{folder}“ wird ab jetzt {place} gesichert."
        + (deleteOldAfterDays is { } days ? $" Die bisherigen Sicherungen werden in {days} Tagen gelöscht." : " Die bisherigen Sicherungen bleiben erhalten.");

    /// <summary>What a folder says about its old backups that wait to be deleted.</summary>
    public static string CleanupPending(StorageSettings? storage, PendingCleanup cleanup, DateTimeOffset now)
    {
        var place = DestinationPhrase(storage, cleanup.Destination);
        if (cleanup.LastError is { } error)
        {
            return $"Die bisherigen Sicherungen {place} ließen sich noch nicht löschen; das Programm versucht es später noch einmal. ({error})";
        }

        return cleanup.DueAt <= now
            ? $"Die bisherigen Sicherungen {place} werden jetzt gelöscht."
            : $"Die bisherigen Sicherungen {place} werden ab dem {cleanup.DueAt.ToLocalTime().ToString("dd.MM.yyyy", German)} gelöscht.";
    }

    public static string OldBackupsKept(string folder) => $"Die bisherigen Sicherungen von „{folder}“ bleiben erhalten.";

    /// <summary>What the overview says after old backups were deleted, or why that failed.</summary>
    public static string CleanupDone(StorageSettings? storage, CleanupResult result, string folder)
    {
        var place = DestinationPhrase(storage, result.Destination);
        if (result.Error is not null)
        {
            return $"Die bisherigen Sicherungen von „{folder}“ {place} ließen sich nicht löschen. Das Programm versucht es später noch einmal.";
        }

        var done = result.SnapshotsDeleted == 1
            ? $"Die bisherige Sicherung von „{folder}“ {place} wurde gelöscht."
            : $"Die {result.SnapshotsDeleted} bisherigen Sicherungen von „{folder}“ {place} wurden gelöscht.";
        return result.DestinationRemoved ? done + " Dort liegt jetzt nichts mehr." : done;
    }

    private static string Capitalize(string text) => text.Length > 0 ? char.ToUpper(text[0], German) + text[1..] : text;

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

        if (error is DestinationPasswordException destination)
        {
            return DestinationOtherPassword($"im Ordner „{destination.Destination}“");
        }

        if (error is DriveLetterInUseException drive)
        {
            return $"Der Laufwerksbuchstabe {drive.Letter} ist inzwischen belegt. Bitte wähle einen anderen.";
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
