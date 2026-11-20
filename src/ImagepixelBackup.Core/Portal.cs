using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ImagepixelBackup.Core;

/// <summary>
/// Where the customer portal of Imagepixel is reached: its address and the public key
/// every request carries. Neither is part of the application. They come from the
/// environment variables <see cref="UrlVariable"/> and <see cref="KeyVariable"/>, or
/// from a file <see cref="FileName"/> in the folder of the settings or next to the
/// program, in the form <c>{"url": "https://…", "apiKey": "…"}</c>. Without them the
/// application does not offer to pair with the portal.
/// </summary>
public sealed record PortalSettings(string Url, string ApiKey)
{
    public const string FileName = "portal.json";

    public const string UrlVariable = "IMAGEPIXEL_BACKUP_PORTAL_URL";

    public const string KeyVariable = "IMAGEPIXEL_BACKUP_PORTAL_KEY";

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    /// <summary>Finds the settings, or returns null if there are none that can be used.</summary>
    /// <param name="dataDirectory">The folder of the settings of the user; <see cref="ConfigStore.DefaultDirectory"/> if null.</param>
    /// <param name="programDirectory">The folder of the program; where the application runs from if null.</param>
    public static PortalSettings? Load(string? dataDirectory = null, string? programDirectory = null) =>
        Load(dataDirectory, programDirectory, Environment.GetEnvironmentVariable);

    /// <summary>The same, with the environment variables read through <paramref name="environment"/>.</summary>
    internal static PortalSettings? Load(string? dataDirectory, string? programDirectory, Func<string, string?> environment)
    {
        if (environment(UrlVariable) is { Length: > 0 } url && environment(KeyVariable) is { Length: > 0 } key)
        {
            return Create(url, key);
        }

        foreach (var directory in new[] { dataDirectory ?? ConfigStore.DefaultDirectory, programDirectory ?? AppContext.BaseDirectory })
        {
            var path = Path.Combine(directory, FileName);
            try
            {
                if (File.Exists(path) && Parse(File.ReadAllText(path)) is { } settings)
                {
                    return settings;
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // a file that cannot be read counts as none
            }
        }

        return null;
    }

    /// <summary>Reads the settings from the JSON of a <see cref="FileName"/> file.</summary>
    public static PortalSettings? Parse(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<SettingsFile>(json, JsonOptions) is { } file ? Create(file.Url, file.ApiKey) : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Names the portal without its key, so that no log or message shows the key.</summary>
    public override string ToString() => $"PortalSettings {{ Url = {Url} }}";

    // only an encrypted connection: the code and the secret of a pairing travel over it
    private static PortalSettings? Create(string? url, string? apiKey) =>
        Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps && !string.IsNullOrWhiteSpace(apiKey)
            ? new PortalSettings(uri.ToString().TrimEnd('/'), apiKey.Trim())
            : null;

    private sealed record SettingsFile(
        [property: JsonPropertyName("url")] string? Url,
        [property: JsonPropertyName("apiKey")] string? ApiKey);
}

/// <summary>The customer portal turned a request down or could not answer it.</summary>
/// <param name="statusCode">The HTTP status of the answer.</param>
/// <param name="message">What the portal said, meant to be shown to the customer for the statuses 400, 403 and 429.</param>
public sealed class PortalException(HttpStatusCode statusCode, string message) : Exception(message)
{
    public HttpStatusCode StatusCode { get; } = statusCode;

    /// <summary>Whether the message is a sentence for the customer, as the portal gives one for wrong input, a wrong secret and too many requests.</summary>
    public bool IsForTheCustomer => StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests;
}

/// <summary>A pairing the portal has started: the code to show, and until when it can be entered.</summary>
public sealed record PairingStart(string Code, DateTimeOffset ValidUntil);

/// <summary>What the portal says about a pairing when asked.</summary>
/// <param name="Stage">One of "wartet", "in-arbeit", "fertig", "fehler" and "abgelaufen".</param>
/// <param name="Access">The encrypted access data, with "fertig" only.</param>
/// <param name="Result">The sentence for the customer, with "fehler".</param>
public sealed record PairingAnswer(string Stage, string? Access, string? Result);

/// <summary>
/// The three functions of the customer portal that a computer calls while it is paired,
/// without anyone logged in. The code and the secret, which only this computer knows,
/// show that it is the one that started the pairing.
/// </summary>
public sealed class PortalClient(HttpClient http, PortalSettings settings)
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    /// <summary>Starts a pairing. The portal keeps the public key and the hash of the secret.</summary>
    /// <param name="publicKey">The public key of the computer as SubjectPublicKeyInfo.</param>
    /// <param name="secret">32 random bytes that only this computer knows.</param>
    /// <param name="system">"windows" or "macos".</param>
    public async Task<PairingStart> BeginAsync(byte[] publicKey, byte[] secret, string system, CancellationToken cancellationToken = default)
    {
        var answer = await CallAsync(
            "kopplung_beginnen",
            new Dictionary<string, string>
            {
                ["p_schluessel"] = Convert.ToBase64String(publicKey),
                ["p_geheimnis_hash"] = PairingCipher.SecretHash(secret),
                ["p_system"] = system,
            },
            cancellationToken);

        var code = Text(answer, "code");
        if (!PairingCode.IsValid(code)
            || !DateTimeOffset.TryParse(Text(answer, "gueltig_bis"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var validUntil))
        {
            throw new PortalException(HttpStatusCode.OK, "The portal answered without a usable code.");
        }

        return new PairingStart(PairingCode.Format(code), validUntil);
    }

    /// <summary>Asks how far the pairing has got.</summary>
    public async Task<PairingAnswer> FetchAsync(string code, byte[] secret, CancellationToken cancellationToken = default)
    {
        var answer = await CallAsync("kopplung_abholen", Proof(code, secret), cancellationToken);

        // the encrypted access data may come as an object or as the text of one
        var access = answer.TryGetProperty("zugang", out var value) ? value.ValueKind switch
        {
            JsonValueKind.Object => value.GetRawText(),
            JsonValueKind.String => value.GetString(),
            _ => null,
        } : null;

        return new PairingAnswer(Text(answer, "stand") ?? "", access, Text(answer, "ergebnis"));
    }

    /// <summary>Tells the portal that the access data have arrived, so that it deletes them.</summary>
    public Task ConfirmAsync(string code, byte[] secret, CancellationToken cancellationToken = default) =>
        CallAsync("kopplung_bestaetigen", Proof(code, secret), cancellationToken);

    private static Dictionary<string, string> Proof(string code, byte[] secret) => new()
    {
        ["p_code"] = PairingCode.Normalize(code),
        ["p_geheimnis"] = Convert.ToBase64String(secret),
    };

    private static string? Text(JsonElement answer, string name) =>
        answer.ValueKind == JsonValueKind.Object && answer.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private async Task<JsonElement> CallAsync(string function, Dictionary<string, string> parameters, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Timeout);

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{settings.Url}/rest/v1/rpc/{function}")
        {
            Content = new StringContent(JsonSerializer.Serialize(parameters), Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("apikey", settings.ApiKey);
        request.Headers.Accept.ParseAdd("application/json");
        request.Content.Headers.Add("Content-Profile", "portal");

        using var response = await http.SendAsync(request, timeout.Token);
        var body = await response.Content.ReadAsStringAsync(timeout.Token);

        JsonElement answer = default;
        try
        {
            if (body.Length > 0)
            {
                using var document = JsonDocument.Parse(body);
                answer = document.RootElement.Clone();
            }
        }
        catch (JsonException)
        {
            // said below, depending on the status
        }

        if (!response.IsSuccessStatusCode)
        {
            var message = answer.ValueKind == JsonValueKind.Object && answer.TryGetProperty("message", out var said) && said.ValueKind == JsonValueKind.String
                ? said.GetString()
                : null;
            throw new PortalException(response.StatusCode, string.IsNullOrWhiteSpace(message) ? $"The portal answered with status {(int)response.StatusCode}." : message);
        }

        // a function that returns a row may come as a list of one
        if (answer.ValueKind == JsonValueKind.Array && answer.GetArrayLength() == 1)
        {
            answer = answer[0];
        }

        return answer.ValueKind == JsonValueKind.Object
            ? answer
            : throw new PortalException(response.StatusCode, "The portal answered with something other than an object.");
    }
}

/// <summary>How far the pairing of this computer has got.</summary>
public enum PairingStage
{
    /// <summary>The code is shown and waits to be entered in the portal.</summary>
    Waiting,

    /// <summary>The code was entered; the access data of the computer are being made.</summary>
    InProgress,

    /// <summary>The access data have arrived and are kept on this computer.</summary>
    Done,

    /// <summary>The portal could not make the access data, or the pairing cannot go on. A new code is needed.</summary>
    Failed,

    /// <summary>The code was not entered in time. A new code is needed.</summary>
    Expired,
}

/// <summary>Where the pairing of this computer stands.</summary>
/// <param name="Stage">How far it has got.</param>
/// <param name="Code">The code, as it is shown.</param>
/// <param name="ValidUntil">Until when the code can be entered in the portal.</param>
/// <param name="Message">With <see cref="PairingStage.Failed"/>: the sentence of the portal for the customer, or null if the problem lies with this computer.</param>
/// <param name="Access">With <see cref="PairingStage.Done"/>: the access data.</param>
public sealed record PairingStatus(
    PairingStage Stage,
    string Code,
    DateTimeOffset ValidUntil,
    string? Message = null,
    DeviceAccess? Access = null);

/// <summary>
/// Pairs this computer with the customer portal. A key pair and a secret are made here
/// and kept with the other secrets of the application; the portal gets the public key
/// and a hash of the secret and gives back a code. Once the customer has entered the
/// code in the portal and Imagepixel has made the access data of the computer, the
/// portal hands them over encrypted for this computer only. They are decrypted and kept
/// here until the backup is set up with them, and the portal is told to delete its copy.
/// A pairing that is under way survives a restart of the application.
/// </summary>
public sealed class DevicePairing(PortalClient portal, ISecretStore secrets, string system)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>The name the portal knows the system of this computer by, or null where pairing is not offered.</summary>
    public static string? CurrentSystem => OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "macos" : null;

    /// <summary>Pairing as this computer can do it, or null if the portal is not set up or the system is not one the portal knows.</summary>
    public static DevicePairing? Create(HttpClient http, ISecretStore secrets, PortalSettings? settings) =>
        settings is not null && CurrentSystem is { } system ? new DevicePairing(new PortalClient(http, settings), secrets, system) : null;

    /// <summary>The current time. Replaceable for tests.</summary>
    public Func<DateTimeOffset> Clock { get; init; } = () => DateTimeOffset.UtcNow;

    /// <summary>
    /// The pairing begun earlier, or null if there is none. Whether its code has been
    /// entered meanwhile only <see cref="CheckAsync"/> finds out; until then it counts as waiting.
    /// </summary>
    public PairingStatus? Current()
    {
        if (Load() is not { } state)
        {
            return null;
        }

        try
        {
            return state.Access is { } access
                ? new PairingStatus(PairingStage.Done, state.Code, state.ValidUntil, Access: DeviceAccess.Parse(Encoding.UTF8.GetBytes(access)))
                : new PairingStatus(PairingStage.Waiting, state.Code, state.ValidUntil);
        }
        catch (PairingException)
        {
            // kept by a version that took other access data: as if there were none
            return null;
        }
    }

    /// <summary>Begins a new pairing. One begun earlier is given up.</summary>
    public async Task<PairingStatus> StartAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var (privateKey, publicKey) = PairingCipher.CreateDeviceKey();
            var secret = RandomNumberGenerator.GetBytes(32);
            var started = await portal.BeginAsync(publicKey, secret, system, cancellationToken);

            Save(new State
            {
                Code = started.Code,
                ValidUntil = started.ValidUntil,
                PrivateKey = Convert.ToBase64String(privateKey),
                Secret = Convert.ToBase64String(secret),
            });
            CryptographicOperations.ZeroMemory(privateKey);
            return new PairingStatus(PairingStage.Waiting, started.Code, started.ValidUntil);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Asks the portal how far the pairing has got. When the access data are there, they
    /// are decrypted and kept, and the portal is told to delete its copy. If the portal
    /// cannot be reached, the exception of the connection comes through and the pairing
    /// stays as it was, to be asked about again.
    /// </summary>
    public async Task<PairingStatus> CheckAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var state = Load() ?? throw new InvalidOperationException("No pairing has been begun.");
            if (state.Access is not null)
            {
                await ConfirmAsync(state, cancellationToken);
                return Current() ?? throw new InvalidOperationException("The kept access data cannot be read.");
            }

            var secret = Convert.FromBase64String(state.Secret!);
            PairingAnswer answer;
            try
            {
                answer = await portal.FetchAsync(state.Code, secret, cancellationToken);
            }
            catch (PortalException e) when (e.IsForTheCustomer)
            {
                // the code is unknown by now or used up: after its time it has simply run out
                return Clock() > state.ValidUntil
                    ? new PairingStatus(PairingStage.Expired, state.Code, state.ValidUntil)
                    : new PairingStatus(PairingStage.Failed, state.Code, state.ValidUntil, e.Message);
            }

            switch (answer.Stage)
            {
                case "in-arbeit":
                    return new PairingStatus(PairingStage.InProgress, state.Code, state.ValidUntil);
                case "fehler":
                    return new PairingStatus(PairingStage.Failed, state.Code, state.ValidUntil, answer.Result);
                case "abgelaufen":
                    return new PairingStatus(PairingStage.Expired, state.Code, state.ValidUntil);
                case "fertig":
                    break;
                default:
                    return new PairingStatus(PairingStage.Waiting, state.Code, state.ValidUntil);
            }

            DeviceAccess access;
            byte[] plain;
            try
            {
                var privateKey = Convert.FromBase64String(state.PrivateKey!);
                plain = PairingCipher.Decrypt(answer.Access ?? "", privateKey, state.Code);
                CryptographicOperations.ZeroMemory(privateKey);
                access = DeviceAccess.Parse(plain);
            }
            catch (PairingException)
            {
                // the portal cannot do better on another try: a new pairing is needed
                return new PairingStatus(PairingStage.Failed, state.Code, state.ValidUntil);
            }

            // kept before the portal is told to let go of its copy
            state.Access = Encoding.UTF8.GetString(plain);
            state.PrivateKey = null;
            Save(state);
            await ConfirmAsync(state, cancellationToken);
            return new PairingStatus(PairingStage.Done, state.Code, state.ValidUntil, Access: access);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Gives the pairing up, or forgets it once the backup has been set up with its access data.</summary>
    public void Forget() => secrets.Set(SecretNames.DevicePairing, null);

    // The portal deletes the access data an hour after they were made anyway. Telling
    // it sooner is worth a try every time the pairing is looked at, until it worked.
    private async Task ConfirmAsync(State state, CancellationToken cancellationToken)
    {
        if (state.Confirmed || state.Secret is null)
        {
            return;
        }

        try
        {
            await portal.ConfirmAsync(state.Code, Convert.FromBase64String(state.Secret), cancellationToken);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return;
        }
        catch (PortalException)
        {
            // the portal no longer knows the pairing: there is nothing left to delete
        }

        state.Confirmed = true;
        state.Secret = null;
        Save(state);
    }

    private State? Load()
    {
        if (secrets.Get(SecretNames.DevicePairing) is not { Length: > 0 } json)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<State>(json, JsonOptions) is { Code.Length: > 0 } state
                && (state.Access is not null || state is { Secret: not null, PrivateKey: not null })
                ? state
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private void Save(State state) => secrets.Set(SecretNames.DevicePairing, JsonSerializer.Serialize(state, JsonOptions));

    // What is kept of a pairing, with the other secrets of the application.
    private sealed class State
    {
        public string Code { get; set; } = "";

        public DateTimeOffset ValidUntil { get; set; }

        /// <summary>The private key as PKCS#8 in Base64, until the access data are decrypted.</summary>
        public string? PrivateKey { get; set; }

        /// <summary>The secret in Base64, until the portal has been told the access data arrived.</summary>
        public string? Secret { get; set; }

        /// <summary>The decrypted access data, once they are there.</summary>
        public string? Access { get; set; }

        public bool Confirmed { get; set; }
    }
}
