using System.Globalization;
using ImagepixelBackup.Core;

// Tool for whoever runs Imagepixel Storage. It creates the setup code a customer
// pastes into the application, and it writes the status file that tells the
// application how much storage is booked and used. Once the service of Imagepixel
// exists, it takes over writing the status file; until then this tool does.

const long BytesPerGb = 1024L * 1024 * 1024;

var culture = CultureInfo.GetCultureInfo("de-DE");

try
{
    if (args.Length == 0)
    {
        PrintUsage();
        return 1;
    }

    var options = ParseOptions(args.Skip(1));
    switch (args[0])
    {
        case "setup-code":
            return SetupCodeCommand(options);
        case "write-status":
            return await WriteStatusAsync(options);
        case "show-status":
            return await ShowStatusAsync(options);
        default:
            PrintUsage();
            return 1;
    }
}
catch (Exception e) when (e is ArgumentException or IOException or S3Exception or HttpRequestException)
{
    Console.Error.WriteLine("Fehler: " + e.Message);
    return 1;
}

void PrintUsage()
{
    Console.Error.WriteLine("""
        ipb-admin - Werkzeug für Imagepixel Storage

        Einrichtungscode für einen Kunden erzeugen:
          ipb-admin setup-code --bucket NAME --credentials DATEI.csv [--region REGION] [--api URL --token TOKEN]

        Belegung messen und die Statusdatei in den Bucket schreiben:
          ipb-admin write-status --bucket NAME --credentials DATEI.csv --booked-gb ZAHL [--region REGION] [--api URL --token TOKEN]

        Statusdatei eines Buckets anzeigen:
          ipb-admin show-status --bucket NAME --credentials DATEI.csv [--region REGION]

        DATEI.csv ist die Schlüsseldatei, die der Speicheranbieter beim Anlegen eines Zugangsschlüssels zum
        Herunterladen anbietet. Ohne --credentials werden IPB_ACCESS_KEY_ID und IPB_SECRET_ACCESS_KEY gelesen.
        Ohne --region gilt eu-central-2.
        """);
}

int SetupCodeCommand(Dictionary<string, string> options)
{
    var (accessKeyId, secretAccessKey) = ReadCredentials(options);
    var (apiUrl, apiToken) = ReadApi(options);
    var code = new SetupCode(Required(options, "bucket"), accessKeyId, secretAccessKey, Region(options), apiUrl, apiToken);

    // the code contains the access key: it is meant for the customer only
    Console.WriteLine(code.Encode());
    return 0;
}

async Task<int> WriteStatusAsync(Dictionary<string, string> options)
{
    var booked = options.TryGetValue("booked-gb", out var text) && long.TryParse(text, CultureInfo.InvariantCulture, out var gb) && gb > 0
        ? gb * BytesPerGb
        : throw new ArgumentException("--booked-gb muss eine ganze Zahl größer als 0 sein.");
    var (apiUrl, apiToken) = ReadApi(options);

    using var http = new HttpClient();
    var client = CreateClient(http, options);

    // everything in the bucket counts, except what Imagepixel itself keeps there
    long used = 0;
    long objects = 0;
    await foreach (var (key, size) in client.ListObjectsAsync())
    {
        if (!key.StartsWith("imagepixel/", StringComparison.Ordinal))
        {
            used += size;
            objects++;
        }
    }

    var document = new StorageStatusDocument
    {
        BookedBytes = booked,
        UsedBytes = used,
        MeasuredAt = DateTimeOffset.UtcNow,
        ApiUrl = apiUrl,
        ApiToken = apiToken,
    };
    await client.PutObjectAsync(StorageStatusDocument.ObjectKey, document.ToJson(), "application/json");

    Console.WriteLine($"Statusdatei geschrieben: {StorageStatusDocument.ObjectKey}");
    PrintStatus(document, objects);
    return 0;
}

async Task<int> ShowStatusAsync(Dictionary<string, string> options)
{
    using var http = new HttpClient();
    var bytes = await CreateClient(http, options).GetObjectAsync(StorageStatusDocument.ObjectKey);
    if (bytes is null || StorageStatusDocument.Parse(bytes) is not { } document)
    {
        Console.Error.WriteLine("In diesem Bucket liegt keine lesbare Statusdatei.");
        return 2;
    }

    PrintStatus(document, null);
    return 0;
}

void PrintStatus(StorageStatusDocument document, long? objects)
{
    var status = document.ToStatus(StorageStatusSource.StatusFile);
    Console.WriteLine(string.Create(culture, $"  gebucht:  {document.BookedBytes / (double)BytesPerGb:0.##} GB"));
    Console.WriteLine(string.Create(culture, $"  belegt:   {document.UsedBytes / (double)BytesPerGb:0.##} GB ({status.Fraction * 100:0.#} %)") + (objects is null ? "" : $" in {objects} Objekten"));
    Console.WriteLine($"  gemessen: {document.MeasuredAt.ToLocalTime():dd.MM.yyyy HH:mm}");
    Console.WriteLine($"  API:      {document.ApiUrl ?? "(keine eingetragen)"}");
    if (status.IsWarning)
    {
        Console.WriteLine("  Hinweis:  Die Anwendung warnt den Kunden, weil mindestens 80 % belegt sind.");
    }
}

S3Client CreateClient(HttpClient http, Dictionary<string, string> options)
{
    var (accessKeyId, secretAccessKey) = ReadCredentials(options);
    var region = Region(options);
    return new S3Client(http, ImagepixelStorage.Endpoint(region), region, Required(options, "bucket"), accessKeyId, secretAccessKey);
}

string Region(Dictionary<string, string> options) =>
    options.GetValueOrDefault("region", ImagepixelStorage.DefaultRegion);

(string? Url, string? Token) ReadApi(Dictionary<string, string> options)
{
    var url = options.GetValueOrDefault("api");
    var token = options.GetValueOrDefault("token");
    if (url is null != token is null)
    {
        throw new ArgumentException("--api und --token gehören zusammen.");
    }

    if (url is not null && !StorageStatusService.IsUsableApiUrl(url))
    {
        throw new ArgumentException("--api muss eine Adresse sein, die mit https:// beginnt.");
    }

    return (url, token);
}

(string AccessKeyId, string SecretAccessKey) ReadCredentials(Dictionary<string, string> options)
{
    if (!options.TryGetValue("credentials", out var path))
    {
        var id = Environment.GetEnvironmentVariable("IPB_ACCESS_KEY_ID");
        var secret = Environment.GetEnvironmentVariable("IPB_SECRET_ACCESS_KEY");
        return string.IsNullOrEmpty(id) || string.IsNullOrEmpty(secret)
            ? throw new ArgumentException("Bitte --credentials angeben oder IPB_ACCESS_KEY_ID und IPB_SECRET_ACCESS_KEY setzen.")
            : (id, secret);
    }

    // the file has a header line such as "User Name,Access Key Id,Secret Access Key"
    var lines = File.ReadAllLines(path).Where(l => l.Trim().Length > 0).ToArray();
    if (lines.Length < 2)
    {
        throw new ArgumentException($"{path} enthält keine Schlüssel.");
    }

    var header = lines[0].Trim('﻿').Split(',').Select(h => h.Trim()).ToArray();
    var values = lines[1].Split(',').Select(v => v.Trim()).ToArray();
    var idColumn = Array.FindIndex(header, h => h.Contains("Access Key Id", StringComparison.OrdinalIgnoreCase));
    var secretColumn = Array.FindIndex(header, h => h.Contains("Secret", StringComparison.OrdinalIgnoreCase));
    if (idColumn < 0 || secretColumn < 0 || values.Length <= Math.Max(idColumn, secretColumn))
    {
        throw new ArgumentException($"{path} hat nicht die erwarteten Spalten \"Access Key Id\" und \"Secret Access Key\".");
    }

    return (values[idColumn], values[secretColumn]);
}

string Required(Dictionary<string, string> options, string name) =>
    options.TryGetValue(name, out var value) && value.Length > 0
        ? value
        : throw new ArgumentException($"--{name} fehlt.");

Dictionary<string, string> ParseOptions(IEnumerable<string> arguments)
{
    var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    string? name = null;
    foreach (var argument in arguments)
    {
        if (argument.StartsWith("--", StringComparison.Ordinal))
        {
            name = argument[2..];
            options[name] = "";
        }
        else if (name is not null)
        {
            options[name] = argument;
            name = null;
        }
        else
        {
            throw new ArgumentException($"Unerwartete Angabe: {argument}");
        }
    }

    return options;
}
