namespace ImagepixelBackup.Core.Tests;

/// <summary>The names of a bucket and a region, as they are typed in for an own Wasabi account.</summary>
public sealed class StorageNameTests
{
    [Theory]
    [InlineData("sicherung-buero", true)]
    [InlineData("kunde.4711", true)]
    [InlineData("abc", true)]
    [InlineData("ab", false)]
    [InlineData("Sicherung", false)]
    [InlineData("meine sicherung", false)]
    [InlineData("-sicherung", false)]
    [InlineData("sicherung-", false)]
    [InlineData("sicherung_buero", false)]
    public void A_bucket_name_has_the_form_the_storage_takes(string name, bool valid)
    {
        Assert.Equal(valid, ImagepixelStorage.IsBucketName(name));
        Assert.False(ImagepixelStorage.IsBucketName(new string('a', 64)));
    }

    [Theory]
    [InlineData("eu-central-2", true)]
    [InlineData("us-east-1", true)]
    [InlineData("ap-northeast-1", true)]
    [InlineData("europa", false)]
    [InlineData("EU-central-2", false)]
    [InlineData("eu-central", false)]
    [InlineData("eu--2", false)]
    [InlineData("eu-central-2.wasabisys.com", false)]
    public void A_region_has_the_form_of_one(string name, bool valid)
    {
        Assert.Equal(valid, ImagepixelStorage.IsRegionName(name));
    }
}
