using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ImagepixelBackup.Core.Tests;

/// <summary>Pairing a computer with the customer portal: the code, the encryption and the access data.</summary>
public sealed class PairingTests
{
    // Made with the crypto module of Node.js, the way the portal side encrypts, from a key
    // pair that Node.js made as well: a check against an implementation of its own.
    private const string NodeDeviceKey =
        "MIGHAgEAMBMGByqGSM49AgEGCCqGSM49AwEHBG0wawIBAQQg8lC3qac0b85RfXm16P7xvdpJNdv5bePvA9Ic7F8YmlWhRANCAAQwEz7LPSiI/bKgzsKXqb3WhGwQD05nRjnwl/smRl+AFPBhaL+vJ0GtwjtisuCP9TFhNhtOixR9e6y21fJPwPjS";

    private const string NodeCode = "K7QM-4TZP";

    private const string NodeMessage =
        """{"v":1,"epk":"MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEa7T/DMLK4wpRxkqchvjpbrbp2vOLaU7b4Da9csPwqEV2g8osHMQCRzFCeeWhIjNcbU7FRePcqTmMxeyh1bgyxw==","salt":"zM5dvCqMv96qXE0Rb3+w8g==","nonce":"n5ACm+YFMvYHZe8B","ct":"CDaxCkNU0bPdt2kuSrKnt/GjpaFQqXD9Z8gMxBGuIGpVXq56Sj7mU06sLlQUNYMzTTugwPtZ6JdiMlvXomcuGu6cpV6s3eJ1Z3iYbsO9R/LBhUyLrTXBLXNtVNC3T3mB3WFVU06fYQqQCmvg8WoqhBTtFC7nkKkXDqynAt6lNjJkfIQocVtIk5OlUjfad+hvB0AY6oTnw8Qhc/0DsJIR+xdKVLra/WaLB61cYEOx15HOKA1NIKnW+rZjvkZKVBRniocn/ONiCSsOzzVs2LB2WJhjR5sz7PZ0QAg5tdNph9/50HBDGZv64sf+YkzUUjLEX6/tYFdSjW8r6pKVXi/BNTQWTpaONTRrYUUKtCOS60kDWl29D1aMCARzJXY1/WbrQVcccSW7MJcqp9vWDj9PDRKEj6QoFUyKSP3JusWpc9WRUwCS82urX4jSeqy247pjGQvxSPd6bQ6CjwpMTW8Ew4L4Hf/aoM3ggJyojnl3P59R2JWLGzCz1XWMWHTpApD/OOfv+w=="}""";

    private const string NodePlain =
        """{"v":1,"g":"3f6c2a1e-8d4b-4c7a-9e21-5b0f7d9c4a10","n":"Notebook Anna","b":"kunde-beispiel","r":"eu-central-2","k":"EXAMPLEACCESSKEY0001","s":"beispiel/Geheimnis+0123456789abcdefABCDEF","p":"geraete/3f6c2a1e-8d4b-4c7a-9e21-5b0f7d9c4a10/","a":"https://status.example.test/v1","t":"beispiel-token","l":{"k":"EXAMPLEREADKEY000001","s":"lesen-geheim","pfade":["server/web1/"]}}""";

    // The vector of the portal side, made by its reference implementation in Node.js with
    // a fixed salt and nonce. Key and values are made up for the test.
    private const string VectorDeviceKey =
        "MIGHAgEAMBMGByqGSM49AgEGCCqGSM49AwEHBG0wawIBAQQgl3YgKrbf5ummar90rR3e3/5qyE5vpps9yk5PT8IEOgehRANCAASohIDfUVy7+WcKzcSjhJ9AwFqxkl2aMXnWXsjLuZ2m3MDqD84PQgwamj2IFR452o0ONO7kP7h73enjqx6UXZEw";

    private const string VectorMessage =
        """
        {
          "v": 1,
          "epk": "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEZxQT/5P8eGPRK1OLwlEKU/yfC3eKgBW6bdAkhtqCqW4HOGDI1ztSmmF6SwcT+R1KLW6lPfkcvBwyZYreLe0TSg==",
          "salt": "ABEiM0RVZneImaq7zN3u/w==",
          "nonce": "AQIDBAUGBwgJCgsM",
          "ct": "/SYZHtKsfXSJohHFlbbYXXm49Fwp2Kl44Jhk9KE0dpeWgBhXWEVAgR5XuGiXeaBKgb/syOjP6KohO01rf+nIkp4nL0yEzoX12OqJsbFRbzHPb+O9QwQJU/wY4mIYDTJfOAAk+899UOFdNWtXIdj3J89B4cnRx/voNrvN/5irQnduGrhy1dhpyrWRbD3AeViAJYUhPvTVNci8gw29xSW0qvXPIpkeNDDHztPVBXIcR5XLFr65gj6ayxvozs2BeviRUHGoxVezbWMnTTl2GLwH5LC1Sp4TLRF99CFGSVk5CjDc36Sg+POd854Oh34bLKmWN1msQ0Edn3flu0A35NhIneZG0uCHbf+3emhsicVOAmAJYeQUaw=="
        }
        """;

    private const string VectorPlain =
        """{"v":1,"g":"3f2b8c1e-9d4a-4e6b-8a1f-0c5d7e9b2a46","n":"Notebook Buchhaltung","b":"imagepixel-backup-kunde-beispiel","r":"eu-central-2","k":"BEISPIELZUGANG0001","s":"beispiel-geheimnis-nur-fuer-den-testvektor","p":"geraete/3f2b8c1e-9d4a-4e6b-8a1f-0c5d7e9b2a46/"}""";

    private const string DeviceId = "3f6c2a1e-8d4b-4c7a-9e21-5b0f7d9c4a10";

    private static byte[] Access(string? root = "geraete/" + DeviceId + "/", string? region = "eu-central-2") =>
        JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object?>
        {
            ["v"] = 1,
            ["g"] = DeviceId,
            ["n"] = "Notebook Anna",
            ["b"] = "kunde-beispiel",
            ["r"] = region,
            ["k"] = "EXAMPLEACCESSKEY0001",
            ["s"] = "beispiel-geheim",
            ["p"] = root,
        });

    [Theory]
    [InlineData("K7QM-4TZP", "K7QM4TZP", "K7QM-4TZP", true)]
    [InlineData(" k7qm 4tzp ", "K7QM4TZP", "K7QM-4TZP", true)]
    [InlineData("k7qm-4-t-z-p", "K7QM4TZP", "K7QM-4TZP", true)]
    [InlineData("K7QM-4TZ", "K7QM4TZ", "K7QM4TZ", false)]
    [InlineData("K7QM-4TZ0", "K7QM4TZ0", "K7QM-4TZ0", false)] // a zero looks like the letter O, which is left out as well
    [InlineData("I1QM-4TZP", "I1QM4TZP", "I1QM-4TZP", false)]
    [InlineData(null, "", "", false)]
    public void The_code_is_read_without_regard_to_case_spaces_and_hyphens(string? typed, string normalized, string shown, bool valid)
    {
        Assert.Equal(normalized, PairingCode.Normalize(typed));
        Assert.Equal(shown, PairingCode.Format(typed));
        Assert.Equal(valid, PairingCode.IsValid(typed));
    }

    [Fact]
    public void The_key_of_the_computer_is_a_P256_pair_and_the_secret_is_known_to_the_portal_by_its_hash()
    {
        var (privateKey, publicKey) = PairingCipher.CreateDeviceKey();

        // the form the portal expects: SubjectPublicKeyInfo of P-256 in DER, 91 bytes
        Assert.Equal(91, publicKey.Length);
        Assert.NotEmpty(privateKey);
        Assert.NotEqual(publicKey, PairingCipher.CreateDeviceKey().PublicKey);

        var hash = PairingCipher.SecretHash(new byte[32]);
        Assert.Equal("66687aadf862bd776c8fc18b8e9f8e20089714856ee233b3902a591d0d5f2925", hash);
    }

    [Fact]
    public void Access_data_encrypted_by_Node_js_are_decrypted()
    {
        var plain = PairingCipher.Decrypt(NodeMessage, Convert.FromBase64String(NodeDeviceKey), NodeCode);
        Assert.Equal(NodePlain, Encoding.UTF8.GetString(plain));

        // the code may come in any of the forms the customer can type it in
        Assert.Equal(NodePlain, Encoding.UTF8.GetString(PairingCipher.Decrypt(NodeMessage, Convert.FromBase64String(NodeDeviceKey), "k7qm4tzp")));

        var access = DeviceAccess.Parse(plain);
        Assert.Equal(
            new DeviceAccess(DeviceId, "Notebook Anna", "kunde-beispiel", "eu-central-2", "EXAMPLEACCESSKEY0001",
                "beispiel/Geheimnis+0123456789abcdefABCDEF", "geraete/" + DeviceId, "https://status.example.test/v1", "beispiel-token"),
            access);
    }

    [Fact]
    public void The_test_vector_of_the_portal_is_decrypted_exactly()
    {
        var key = Convert.FromBase64String(VectorDeviceKey);
        Assert.Equal(VectorPlain, Encoding.UTF8.GetString(PairingCipher.Decrypt(VectorMessage, key, "K7QM-4TZP")));

        // with another code it must fail
        Assert.Throws<PairingException>(() => PairingCipher.Decrypt(VectorMessage, key, "K7QM-4TZQ"));

        var access = DeviceAccess.Parse(Encoding.UTF8.GetBytes(VectorPlain));
        Assert.Equal("geraete/3f2b8c1e-9d4a-4e6b-8a1f-0c5d7e9b2a46", access.Root);
        Assert.Equal(("Notebook Buchhaltung", "BEISPIELZUGANG0001"), (access.DeviceName, access.AccessKeyId));
        Assert.Null(access.ApiUrl);

        // the folder is taken as it comes, whatever form it has
        Assert.Equal(
            "geraete/imagepixel-backup-geraet-3f2b8c1e-9d4a-4e6b-8a1f-0c5d7e9b2a46",
            DeviceAccess.Parse(Encoding.UTF8.GetBytes(VectorPlain.Replace("geraete/", "geraete/imagepixel-backup-geraet-"))).Root);
    }

    [Fact]
    public void What_is_encrypted_for_a_computer_only_opens_with_its_key_and_its_code()
    {
        var (privateKey, publicKey) = PairingCipher.CreateDeviceKey();
        var message = PairingCipher.Encrypt(Access(), publicKey, "ABCD-EFGH");

        Assert.Equal(Access(), PairingCipher.Decrypt(message, privateKey, "abcdefgh"));

        // a fresh key pair of the sender, salt and nonce each time
        Assert.NotEqual(message, PairingCipher.Encrypt(Access(), publicKey, "ABCD-EFGH"));

        Assert.Throws<PairingException>(() => PairingCipher.Decrypt(message, privateKey, "ABCD-EFGJ"));
        Assert.Throws<PairingException>(() => PairingCipher.Decrypt(message, PairingCipher.CreateDeviceKey().PrivateKey, "ABCD-EFGH"));
    }

    [Theory]
    [InlineData("ct")]
    [InlineData("nonce")]
    [InlineData("salt")]
    [InlineData("epk")]
    public void A_changed_message_is_turned_down(string part)
    {
        var (privateKey, publicKey) = PairingCipher.CreateDeviceKey();
        var message = JsonNode.Parse(PairingCipher.Encrypt(Access(), publicKey, "ABCD-EFGH"))!.AsObject();

        var bytes = Convert.FromBase64String((string)message[part]!);
        bytes[^1] ^= 0x01;
        message[part] = Convert.ToBase64String(bytes);

        Assert.Throws<PairingException>(() => PairingCipher.Decrypt(message.ToJsonString(), privateKey, "ABCD-EFGH"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("kein JSON")]
    [InlineData("{}")]
    [InlineData("""{"v":2,"epk":"","salt":"","nonce":"","ct":""}""")]
    [InlineData("""{"v":1,"epk":"%%%","salt":"AA==","nonce":"AAAAAAAAAAAAAAAA","ct":"AAAAAAAAAAAAAAAAAAAAAA=="}""")]
    [InlineData("""{"v":1,"epk":"AA==","salt":"AA==","nonce":"AAAAAAAAAAAAAAAA","ct":"AAAAAAAAAAAAAAAAAAAAAA=="}""")]
    public void A_message_that_is_not_one_is_turned_down(string message)
    {
        Assert.Throws<PairingException>(() => PairingCipher.Decrypt(message, PairingCipher.CreateDeviceKey().PrivateKey, "ABCD-EFGH"));
    }

    [Fact]
    public void Access_data_without_a_folder_of_their_own_are_turned_down()
    {
        Assert.Throws<PairingException>(() => DeviceAccess.Parse(Access(root: null)));
        Assert.Throws<PairingException>(() => DeviceAccess.Parse(Access(root: "/")));
        Assert.Throws<PairingException>(() => DeviceAccess.Parse(Access(root: "geraete/../server")));
        Assert.Throws<PairingException>(() => DeviceAccess.Parse(Encoding.UTF8.GetBytes("""{"v":1,"b":"kunde","k":"KEY","p":"geraete/1/"}""")));
        Assert.Throws<PairingException>(() => DeviceAccess.Parse(Encoding.UTF8.GetBytes("[]")));

        // the region of the setup code applies when none is named
        Assert.Equal(ImagepixelStorage.DefaultRegion, DeviceAccess.Parse(Access(region: null)).Region);
    }

    [Fact]
    public void Every_backup_of_a_paired_computer_lies_in_its_own_folder()
    {
        var storage = DeviceAccess.Parse(Access()).ToStorage();
        var root = "geraete/" + DeviceId;

        Assert.Equal(root, storage.Root);
        Assert.Equal("Notebook Anna", storage.DeviceName);
        Assert.Equal(DeviceId, storage.DeviceId);
        Assert.Equal($"s3:https://s3.eu-central-2.wasabisys.com/kunde-beispiel/{root}", storage.RepositoryUrl);
        Assert.Equal(root, storage.PathOf(""));

        // destinations lie inside it, and so does everything that reaches the storage itself
        var notebook = storage.WithFolder("Büro/Notebook");
        Assert.Equal($"s3:https://s3.eu-central-2.wasabisys.com/kunde-beispiel/{root}/Büro/Notebook", notebook.RepositoryUrl);
        Assert.Equal((root, "Notebook Anna", DeviceId), (notebook.Root, notebook.DeviceName, notebook.DeviceId));
        Assert.Equal($"{root}/Fotos", notebook.PathOf("Fotos"));

        var arguments = EncryptedView.CreateStartInfo("rclone", storage, "geheim", "", "S:", "Backup").ArgumentList.ToList();
        Assert.Contains($"imagepixel:kunde-beispiel/{root}", arguments);
        Assert.DoesNotContain("--exclude", arguments);
        Assert.Contains($"imagepixel:kunde-beispiel/{root}/Fotos",
            EncryptedView.CreateStartInfo("rclone", storage, "geheim", "Fotos", "S:", "Backup").ArgumentList);

        // the settings come back as they were saved
        var config = new AppConfig { Storage = notebook };
        var saved = JsonSerializer.Deserialize<AppConfig>(JsonSerializer.Serialize(config))!.Storage!;
        Assert.Equal(notebook.RepositoryUrl, saved.RepositoryUrl);
    }

    [Fact]
    public void Another_folder_of_the_bucket_is_another_storage()
    {
        var paired = DeviceAccess.Parse(Access()).ToStorage();
        var wholeBucket = new StorageSettings { Kind = StorageKind.ImagepixelStorage, Bucket = "kunde-beispiel", AccessKeyId = "OTHER" };

        Assert.False(BackupManager.IsSameStorage(wholeBucket, paired));
        Assert.False(BackupManager.IsSameStorage(paired, DeviceAccess.Parse(Access(root: "geraete/anderer/")).ToStorage()));

        // the same computer with a new key and another destination
        var renewed = paired.WithFolder("Fotos");
        renewed.AccessKeyId = "NEW";
        Assert.True(BackupManager.IsSameStorage(paired, renewed));

        // a folder on a disk has no such part
        var disk = new StorageSettings { Kind = StorageKind.LocalFolder, LocalPath = Path.GetTempPath(), Root = "geraete/x" };
        Assert.Equal("", disk.PathOf(""));
    }

    [Fact]
    public async Task The_backups_of_a_paired_computer_are_looked_for_in_its_own_folder()
    {
        var root = "geraete/" + DeviceId;
        var asked = new List<string>();
        var network = new FakeNetwork(request =>
        {
            var path = Uri.UnescapeDataString(request.RequestUri!.AbsolutePath);
            asked.Add(path + Uri.UnescapeDataString(request.RequestUri.Query));
            if (request.RequestUri.Query.Contains("list-type"))
            {
                return Xml($"""
                    <ListBucketResult xmlns="http://s3.amazonaws.com/doc/2006-03-01/">
                      <IsTruncated>false</IsTruncated>
                      <CommonPrefixes><Prefix>{root}/Fotos/</Prefix></CommonPrefixes>
                      <CommonPrefixes><Prefix>{root}/Notebook/</Prefix></CommonPrefixes>
                      <CommonPrefixes><Prefix>{root}/data/</Prefix></CommonPrefixes>
                    </ListBucketResult>
                    """);
            }

            return path == $"/kunde-beispiel/{root}/Notebook/config" || path == $"/kunde-beispiel/{root}/config"
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("config") }
                : new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var found = await new BackupFinder(new HttpClient(network)).FindAsync(DeviceAccess.Parse(Access()).ToStorage(), "geheim");

        Assert.Equal(new[] { "", "Notebook" }, found);

        // nothing outside the folder of the computer is asked for
        var listing = Assert.Single(asked, request => request.Contains("list-type"));
        Assert.Contains($"prefix={root}/", listing);
        Assert.Contains("delimiter=/", listing);
        Assert.All(asked.Where(request => !request.Contains("list-type")), request => Assert.StartsWith($"/kunde-beispiel/{root}/", request));
    }

    [Fact]
    public async Task A_paired_computer_measures_only_its_own_folder()
    {
        var root = "geraete/" + DeviceId;
        var listings = new List<string>();
        var network = new FakeNetwork(request =>
        {
            if (request.RequestUri!.Query.Contains("list-type"))
            {
                listings.Add(Uri.UnescapeDataString(request.RequestUri.Query));
                return Xml($"""
                    <ListBucketResult xmlns="http://s3.amazonaws.com/doc/2006-03-01/">
                      <IsTruncated>false</IsTruncated>
                      <Contents><Key>{root}/data/aa</Key><Size>5000</Size></Contents>
                    </ListBucketResult>
                    """);
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(new StorageStatusDocument { BookedBytes = 10_000, UsedBytes = 1000, MeasuredAt = DateTimeOffset.UtcNow }.ToJson()),
            };
        });

        var result = await new StorageStatusService(new HttpClient(network)).GetAsync(DeviceAccess.Parse(Access()).ToStorage(), "geheim", null);

        Assert.Equal(5000, result.Status!.UsedBytes);
        Assert.Contains($"prefix={root}/", Assert.Single(listings));
    }

    [Fact]
    public void The_portal_is_set_up_outside_the_application()
    {
        var folder = Path.Combine(Path.GetTempPath(), "ipb-portal-" + Guid.NewGuid().ToString("N"));
        var program = Path.Combine(folder, "Programm");
        var data = Path.Combine(folder, "Einstellungen");
        Directory.CreateDirectory(program);
        Directory.CreateDirectory(data);
        try
        {
            Assert.Null(PortalSettings.Load(data, program, NoEnvironment));

            File.WriteAllText(Path.Combine(program, PortalSettings.FileName), """{"url": "https://portal.example.test/", "apiKey": "oeffentlich"}""");
            Assert.Equal(new PortalSettings("https://portal.example.test", "oeffentlich"), PortalSettings.Load(data, program, NoEnvironment));

            // the folder of the settings comes first, so that one computer can be pointed elsewhere
            File.WriteAllText(Path.Combine(data, PortalSettings.FileName), """{"url": "https://test.example.test", "apiKey": "anders"}""");
            Assert.Equal(new PortalSettings("https://test.example.test", "anders"), PortalSettings.Load(data, program, NoEnvironment));

            // only over an encrypted connection, and only with a key
            Assert.Null(PortalSettings.Parse("""{"url": "http://portal.example.test", "apiKey": "k"}"""));
            Assert.Null(PortalSettings.Parse("""{"url": "https://portal.example.test", "apiKey": " "}"""));
            Assert.Null(PortalSettings.Parse("""{"url": "portal", "apiKey": "k"}"""));
            Assert.Null(PortalSettings.Parse("kein JSON"));

            // the environment comes before both files
            var environment = new Dictionary<string, string?>
            {
                [PortalSettings.UrlVariable] = "https://umgebung.example.test",
                [PortalSettings.KeyVariable] = "aus-der-umgebung",
            };
            Assert.Equal(
                new PortalSettings("https://umgebung.example.test", "aus-der-umgebung"),
                PortalSettings.Load(data, program, name => environment.GetValueOrDefault(name)));

            // the key never shows when the settings are written out
            Assert.DoesNotContain("aus-der-umgebung", PortalSettings.Load(data, program, name => environment.GetValueOrDefault(name))!.ToString());
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    private static string? NoEnvironment(string name) => null;

    [Fact]
    public void Pairing_is_offered_on_Windows_and_macOS_only()
    {
        var settings = new PortalSettings("https://portal.example.test", "oeffentlich");
        var pairing = DevicePairing.Create(new HttpClient(), new MemorySecretStore(), settings);

        // the portal knows these two systems only; the tests run on all three
        if (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS())
        {
            Assert.NotNull(pairing);
            Assert.Equal(OperatingSystem.IsWindows() ? "windows" : "macos", DevicePairing.CurrentSystem);
        }
        else
        {
            Assert.Null(pairing);
            Assert.Null(DevicePairing.CurrentSystem);
        }

        // without the address of the portal there is nothing to pair with
        Assert.Null(DevicePairing.Create(new HttpClient(), new MemorySecretStore(), null));
    }

    [Fact]
    public void The_secret_of_the_access_data_never_shows_when_they_are_written_out()
    {
        var access = DeviceAccess.Parse(Encoding.UTF8.GetBytes(NodePlain));
        var status = new PairingStatus(PairingStage.Done, NodeCode, DateTimeOffset.UtcNow, Access: access);

        foreach (var written in new[] { access.ToString(), status.ToString() })
        {
            Assert.Contains(access.AccessKeyId, written);
            Assert.DoesNotContain(access.SecretAccessKey, written);
            Assert.DoesNotContain(access.ApiToken!, written);
        }
    }

    [Fact]
    public async Task The_portal_is_called_the_way_its_functions_expect()
    {
        var portal = new FakePortal();
        var client = new PortalClient(new HttpClient(portal), new PortalSettings("https://portal.example.test", "oeffentlich"));

        var secret = new byte[32];
        secret[0] = 7;
        var (_, publicKey) = PairingCipher.CreateDeviceKey();
        var started = await client.BeginAsync(publicKey, secret, "windows");

        Assert.Equal("K7QM-4TZP", started.Code);
        var begin = Assert.Single(portal.Calls);
        Assert.Equal("https://portal.example.test/rest/v1/rpc/kopplung_beginnen", begin.Url);
        Assert.Equal("oeffentlich", begin.ApiKey);
        Assert.Equal("portal", begin.Profile);
        Assert.Equal(Convert.ToBase64String(publicKey), begin.Body["p_schluessel"]);
        Assert.Equal(PairingCipher.SecretHash(secret), begin.Body["p_geheimnis_hash"]);
        Assert.Equal("windows", begin.Body["p_system"]);

        var answer = await client.FetchAsync("k7qm 4tzp", secret);
        Assert.Equal("wartet", answer.Stage);
        Assert.Equal("K7QM4TZP", portal.Calls[^1].Body["p_code"]);
        Assert.Equal(Convert.ToBase64String(secret), portal.Calls[^1].Body["p_geheimnis"]);

        // the encrypted access data come as an object or as its text
        portal.Stage = "fertig";
        portal.Access = """{"v":1,"ct":"x"}""";
        Assert.Equal("""{"v":1,"ct":"x"}""", (await client.FetchAsync("K7QM-4TZP", secret)).Access);
        portal.AccessAsText = true;
        Assert.Equal("""{"v":1,"ct":"x"}""", (await client.FetchAsync("K7QM-4TZP", secret)).Access);

        await client.ConfirmAsync("K7QM-4TZP", secret);
        Assert.Equal("https://portal.example.test/rest/v1/rpc/kopplung_bestaetigen", portal.Calls[^1].Url);

        // what the portal says about a wrong secret is meant for the customer
        var wrong = await Assert.ThrowsAsync<PortalException>(() => client.FetchAsync("K7QM-4TZP", new byte[32]));
        Assert.Equal(HttpStatusCode.Forbidden, wrong.StatusCode);
        Assert.True(wrong.IsForTheCustomer);
        Assert.Equal("Der Code passt nicht.", wrong.Message);

        portal.Answer = _ => new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("""{"message":"Could not find the function"}""") };
        Assert.False((await Assert.ThrowsAsync<PortalException>(() => client.FetchAsync("K7QM-4TZP", secret))).IsForTheCustomer);
    }

    [Fact]
    public async Task A_pairing_goes_from_the_code_to_the_access_data_and_survives_a_restart()
    {
        var portal = new FakePortal();
        var secrets = new MemorySecretStore();
        var pairing = Pairing(portal, secrets);

        Assert.Null(pairing.Current());
        var started = await pairing.StartAsync();
        Assert.Equal(PairingStage.Waiting, started.Stage);
        Assert.Equal("K7QM-4TZP", started.Code);

        // the key and the secret are kept with the other secrets, not in the settings
        Assert.NotNull(secrets.Get(SecretNames.DevicePairing));
        Assert.Equal(PairingStage.Waiting, (await pairing.CheckAsync()).Stage);

        portal.Stage = "in-arbeit";
        Assert.Equal(PairingStage.InProgress, (await pairing.CheckAsync()).Stage);

        // the application was closed meanwhile: the next start goes on with the same code
        pairing = Pairing(portal, secrets);
        Assert.Equal(new PairingStatus(PairingStage.Waiting, "K7QM-4TZP", started.ValidUntil), pairing.Current());

        portal.Stage = "fertig";
        portal.Access = PairingCipher.Encrypt(Access(), portal.PublicKey!, "K7QM4TZP");
        var done = await pairing.CheckAsync();

        Assert.Equal(PairingStage.Done, done.Stage);
        Assert.Equal("geraete/" + DeviceId, done.Access!.Root);
        Assert.Equal("beispiel-geheim", done.Access.SecretAccessKey);
        Assert.Equal(1, portal.Confirmed);

        // once decrypted and confirmed, neither the private key nor the secret is kept any longer
        var kept = JsonNode.Parse(secrets.Get(SecretNames.DevicePairing)!)!.AsObject();
        Assert.False(kept.ContainsKey("PrivateKey"));
        Assert.False(kept.ContainsKey("Secret"));

        // kept until the backup is set up with it, without asking the portal again
        pairing = Pairing(portal, secrets);
        Assert.Equal(done.Access, pairing.Current()!.Access);
        var calls = portal.Calls.Count;
        Assert.Equal(done.Access, (await pairing.CheckAsync()).Access);
        Assert.Equal(calls, portal.Calls.Count);

        pairing.Forget();
        Assert.Null(pairing.Current());
        Assert.Null(secrets.Get(SecretNames.DevicePairing));
    }

    [Fact]
    public async Task Telling_the_portal_is_tried_again_until_it_worked()
    {
        var portal = new FakePortal { Stage = "fertig" };
        var pairing = Pairing(portal, new MemorySecretStore());
        await pairing.StartAsync();
        portal.Access = PairingCipher.Encrypt(Access(), portal.PublicKey!, "K7QM-4TZP");
        portal.ConfirmFails = true;

        Assert.Equal(PairingStage.Done, (await pairing.CheckAsync()).Stage);
        Assert.Equal(0, portal.Confirmed);

        portal.ConfirmFails = false;
        Assert.Equal(PairingStage.Done, (await pairing.CheckAsync()).Stage);
        Assert.Equal(1, portal.Confirmed);

        await pairing.CheckAsync();
        Assert.Equal(1, portal.Confirmed);
    }

    [Fact]
    public async Task A_pairing_that_cannot_go_on_says_why()
    {
        var now = DateTimeOffset.UtcNow;
        var portal = new FakePortal();
        var pairing = Pairing(portal, new MemorySecretStore(), () => now);
        await pairing.StartAsync();

        portal.Stage = "fehler";
        portal.Result = "Für deine Firma ist noch kein Speicher eingerichtet.";
        var failed = await pairing.CheckAsync();
        Assert.Equal((PairingStage.Failed, "Für deine Firma ist noch kein Speicher eingerichtet."), (failed.Stage, failed.Message));

        portal.Stage = "abgelaufen";
        Assert.Equal(PairingStage.Expired, (await pairing.CheckAsync()).Stage);

        // access data this computer cannot open: the portal would send the same again
        portal.Stage = "fertig";
        portal.Access = PairingCipher.Encrypt(Access(), PairingCipher.CreateDeviceKey().PublicKey, "K7QM-4TZP");
        var undecryptable = await pairing.CheckAsync();
        Assert.Equal((PairingStage.Failed, null), (undecryptable.Stage, undecryptable.Message));
        Assert.Equal(0, portal.Confirmed);

        // the portal turns the code down: before its time that is a failure, afterwards it ran out
        portal.Answer = _ => new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("""{"message":"Diesen Code gibt es nicht."}""") };
        var turnedDown = await pairing.CheckAsync();
        Assert.Equal((PairingStage.Failed, "Diesen Code gibt es nicht."), (turnedDown.Stage, turnedDown.Message));
        now = now.AddHours(1);
        Assert.Equal(PairingStage.Expired, (await pairing.CheckAsync()).Stage);

        // a portal that cannot be reached says nothing about the pairing
        portal.Answer = _ => throw new HttpRequestException("connection refused");
        await Assert.ThrowsAsync<HttpRequestException>(() => pairing.CheckAsync());
        Assert.NotNull(pairing.Current());
    }

    /// <summary>
    /// Begins a pairing with a real portal and asks about it once. Runs only where
    /// IMAGEPIXEL_BACKUP_PORTAL_URL and IMAGEPIXEL_BACKUP_PORTAL_KEY name one; the code it
    /// leaves there runs out after half an hour.
    /// </summary>
    [SkippableFact]
    public async Task A_pairing_begins_with_a_real_portal()
    {
        var url = Environment.GetEnvironmentVariable(PortalSettings.UrlVariable);
        var key = Environment.GetEnvironmentVariable(PortalSettings.KeyVariable);
        Skip.If(string.IsNullOrEmpty(url) || string.IsNullOrEmpty(key), "no portal is named");

        var settings = PortalSettings.Load(Path.GetTempPath(), Path.GetTempPath())!;
        var secrets = new MemorySecretStore();
        var pairing = new DevicePairing(new PortalClient(new HttpClient(), settings), secrets, "windows");

        var started = await pairing.StartAsync();
        Assert.True(PairingCode.IsValid(started.Code), started.Code);
        Assert.Equal(started.Code, PairingCode.Format(started.Code));
        Assert.InRange(started.ValidUntil, DateTimeOffset.UtcNow.AddMinutes(20), DateTimeOffset.UtcNow.AddMinutes(40));

        Assert.Equal(PairingStage.Waiting, (await pairing.CheckAsync()).Stage);

        // a wrong secret is turned down with a sentence for the customer
        var wrong = await Assert.ThrowsAsync<PortalException>(
            () => new PortalClient(new HttpClient(), settings).FetchAsync(started.Code, new byte[32]));
        Assert.True(wrong.IsForTheCustomer, $"{(int)wrong.StatusCode} {wrong.Message}");
    }

    private static DevicePairing Pairing(FakePortal portal, ISecretStore secrets, Func<DateTimeOffset>? clock = null) =>
        new(new PortalClient(new HttpClient(portal), new PortalSettings("https://portal.example.test", "oeffentlich")), secrets, "windows")
        {
            Clock = clock ?? (() => DateTimeOffset.UtcNow),
        };

    private static HttpResponseMessage Xml(string xml) =>
        new(HttpStatusCode.OK) { Content = new StringContent(xml, Encoding.UTF8, "application/xml") };

    private sealed class FakeNetwork(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(answer(request));
    }

    /// <summary>The three functions of the portal for pairing, as the test sets them.</summary>
    private sealed class FakePortal : HttpMessageHandler
    {
        public sealed record Call(string Url, string? ApiKey, string? Profile, Dictionary<string, string> Body);

        public List<Call> Calls { get; } = [];

        public string Stage { get; set; } = "wartet";

        public string? Access { get; set; }

        public bool AccessAsText { get; set; }

        public string? Result { get; set; }

        public byte[]? PublicKey { get; private set; }

        public int Confirmed { get; private set; }

        public bool ConfirmFails { get; set; }

        private string? _secretHash;

        /// <summary>Takes the place of the answers below when set.</summary>
        public Func<HttpRequestMessage, HttpResponseMessage>? Answer { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = JsonSerializer.Deserialize<Dictionary<string, string>>(await request.Content!.ReadAsStringAsync(cancellationToken))!;
            Calls.Add(new Call(
                request.RequestUri!.ToString(),
                request.Headers.TryGetValues("apikey", out var key) ? key.Single() : null,
                request.Content.Headers.TryGetValues("Content-Profile", out var profile) ? profile.Single() : null,
                body));

            if (Answer is { } answer)
            {
                return answer(request);
            }

            var function = request.RequestUri.Segments[^1];
            if (function == "kopplung_beginnen")
            {
                PublicKey = Convert.FromBase64String(body["p_schluessel"]);
                _secretHash = body["p_geheimnis_hash"];
                return Json(new { code = "K7QM-4TZP", gueltig_bis = DateTimeOffset.UtcNow.AddMinutes(30).ToString("O") });
            }

            if (body["p_code"] != "K7QM4TZP" || PairingCipher.SecretHash(Convert.FromBase64String(body["p_geheimnis"])) != _secretHash)
            {
                return new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent("""{"message":"Der Code passt nicht."}""") };
            }

            if (function == "kopplung_bestaetigen")
            {
                if (ConfirmFails)
                {
                    throw new HttpRequestException("connection reset");
                }

                Confirmed++;
                return Json(new { stand = "bestaetigt" });
            }

            object? access = Stage == "fertig" && Access is not null
                ? AccessAsText ? Access : JsonNode.Parse(Access)
                : null;
            return Json(new { stand = Stage, zugang = access, ergebnis = Stage == "fehler" ? Result : null });
        }

        private static HttpResponseMessage Json(object value) =>
            new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };
    }
}
