namespace ImagepixelBackup.Core.Tests;

public sealed class CloudFolderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ipb-cloud-" + Guid.NewGuid().ToString("N"));

    private string Profile => Path.Combine(_root, "Users", "anna");

    private string Volume => Path.Combine(_root, "G") + Path.DirectorySeparatorChar;

    public CloudFolderTests()
    {
        Directory.CreateDirectory(Profile);
    }

    private CloudFolderDetector Detector(
        Dictionary<string, string>? environment = null,
        params (string Root, string Label)[] volumes) =>
        new()
        {
            UserProfile = Profile,
            EnvironmentVariable = name => environment?.GetValueOrDefault(name),
            Volumes = () => volumes,
        };

    private string Create(params string[] parts)
    {
        var path = Path.Combine(parts);
        Directory.CreateDirectory(path);
        return path;
    }

    [Fact]
    public void Nothing_is_found_on_a_computer_without_cloud_storage()
    {
        Assert.Empty(Detector().Find());
    }

    [Fact]
    public void Personal_and_work_OneDrive_are_found_once_each()
    {
        var personal = Create(Profile, "OneDrive");
        var work = Create(Profile, "OneDrive - Muster GmbH");

        var found = Detector(new()
        {
            ["OneDriveConsumer"] = personal,
            ["OneDriveCommercial"] = work,
            ["OneDrive"] = work + Path.DirectorySeparatorChar,
        }).Find();

        Assert.Equal(
            [new CloudFolder(CloudService.OneDrive, "OneDrive", personal), new CloudFolder(CloudService.OneDrive, "OneDrive - Muster GmbH", work)],
            found);
    }

    [Fact]
    public void Variables_that_point_to_a_missing_folder_are_ignored()
    {
        var found = Detector(new() { ["OneDrive"] = Path.Combine(Profile, "OneDrive") }).Find();

        Assert.Empty(found);
    }

    [Theory]
    [InlineData("Meine Ablage")]
    [InlineData("My Drive")]
    public void Google_Drive_volume_is_found_with_its_personal_folder(string folderName)
    {
        var personal = Create(Volume, folderName);
        Create(Volume, "Geteilte Ablagen");

        var found = Detector(null, (Volume, "Google Drive"), (Path.Combine(_root, "C"), "Windows")).Find();

        Assert.Equal([new CloudFolder(CloudService.GoogleDrive, "Google Drive", personal)], found);
    }

    [Fact]
    public void ICloud_Drive_and_a_mirrored_Google_Drive_are_found_in_the_profile()
    {
        var google = Create(Profile, "Meine Ablage");
        var icloud = Create(Profile, "iCloudDrive");

        var found = Detector().Find();

        Assert.Equal(
            [new CloudFolder(CloudService.GoogleDrive, "Google Drive", google), new CloudFolder(CloudService.ICloudDrive, "iCloud Drive", icloud)],
            found);
    }

    [Fact]
    public void MacOS_locations_are_found()
    {
        var oneDrive = Create(Profile, "Library", "CloudStorage", "OneDrive-Personal");
        var google = Create(Profile, "Library", "CloudStorage", "GoogleDrive-anna@example.com");
        var icloud = Create(Profile, "Library", "Mobile Documents", "com~apple~CloudDocs");
        Create(Profile, "Library", "CloudStorage", "Dropbox");

        var found = Detector().Find();

        Assert.Equal(
            [
                new CloudFolder(CloudService.GoogleDrive, "Google Drive", google),
                new CloudFolder(CloudService.OneDrive, "OneDrive-Personal", oneDrive),
                new CloudFolder(CloudService.ICloudDrive, "iCloud Drive", icloud),
            ],
            found);
    }

    [Fact]
    public void MacOS_Google_Drive_is_found_with_its_personal_folder()
    {
        var account = Create(Profile, "Library", "CloudStorage", "GoogleDrive-anna@example.com");
        var personal = Create(account, "Meine Ablage");
        Create(account, "Geteilte Ablagen");

        var found = Detector().Find();

        Assert.Equal([new CloudFolder(CloudService.GoogleDrive, "Google Drive", personal)], found);
    }

    [Fact]
    public void Containing_matches_the_folder_and_everything_inside_but_not_its_neighbours()
    {
        var oneDrive = new CloudFolder(CloudService.OneDrive, "OneDrive", Create(Profile, "OneDrive"));
        CloudFolder[] folders = [oneDrive];

        Assert.Same(oneDrive, CloudFolderDetector.Containing(oneDrive.Path, folders));
        Assert.Same(oneDrive, CloudFolderDetector.Containing(Path.Combine(oneDrive.Path, "Bilder", "2026"), folders));
        Assert.Same(oneDrive, CloudFolderDetector.Containing(oneDrive.Path + Path.DirectorySeparatorChar, folders));

        // only Linux tells folders apart by upper and lower case
        Assert.Equal(
            !OperatingSystem.IsLinux(),
            CloudFolderDetector.Containing(oneDrive.Path.ToUpperInvariant(), folders) is not null);
        Assert.Null(CloudFolderDetector.Containing(oneDrive.Path + " alt", folders));
        Assert.Null(CloudFolderDetector.Containing(Profile, folders));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
