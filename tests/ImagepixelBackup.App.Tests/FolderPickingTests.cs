namespace ImagepixelBackup.App.Tests;

/// <summary>
/// Choosing a folder in the dialog of the system. The dialog also offers places that are no
/// folder on the disk, such as a library or "Dieser PC"; Avalonia turns them down with an
/// exception, which used to end the application.
/// </summary>
public sealed class FolderPickingTests
{
    private const string Title = "Ordner auswählen, der gesichert werden soll";

    [Fact]
    public async Task A_folder_on_the_disk_is_taken_as_it_is_chosen()
    {
        var titles = new List<string>();

        var path = await FolderPicking.PickAsync(Dialog(titles, () => @"C:\Users\anna\Dokumente"), Title);

        Assert.Equal(@"C:\Users\anna\Dokumente", path);
        Assert.Equal(new[] { Title }, titles);
    }

    [Fact]
    public async Task A_cancelled_dialog_gives_no_folder()
    {
        Assert.Null(await FolderPicking.PickAsync(Dialog([], () => null), Title));
    }

    [Fact]
    public async Task A_place_that_is_no_folder_opens_the_dialog_again_with_a_hint()
    {
        var titles = new List<string>();
        var answers = new Queue<Func<string?>>(new Func<string?>[]
        {
            // what Avalonia throws for a library or "Dieser PC" on Windows
            () => throw new ArgumentException("Directory must exist", "fileSystemInfo"),

            // a place the dialog has no local path for
            () => throw new DirectoryNotFoundException("The chosen place is no folder on the disk."),
            () => @"C:\Users\anna\Bilder",
        });

        var path = await FolderPicking.PickAsync(Dialog(titles, () => answers.Dequeue()()), Title);

        Assert.Equal(@"C:\Users\anna\Bilder", path);
        Assert.Equal(new[] { Title, Texts.NotAFolder(Title), Texts.NotAFolder(Title) }, titles);
        Assert.StartsWith("Das war kein Ordner auf diesem Rechner.", titles[1]);
        Assert.EndsWith(Title, titles[1]);
    }

    [Fact]
    public async Task Cancelling_after_a_place_that_is_no_folder_gives_no_folder()
    {
        var titles = new List<string>();
        var answers = new Queue<Func<string?>>(new Func<string?>[]
        {
            () => throw new ArgumentException("Directory must exist", "fileSystemInfo"),
            () => null,
        });

        Assert.Null(await FolderPicking.PickAsync(Dialog(titles, () => answers.Dequeue()()), Title));
        Assert.Equal(2, titles.Count);
    }

    [Fact]
    public async Task A_dialog_that_turns_down_every_place_stops_asking_at_some_point()
    {
        var titles = new List<string>();

        var path = await FolderPicking.PickAsync(Dialog(titles, () => throw new ArgumentException("Directory must exist")), Title);

        Assert.Null(path);
        Assert.Equal(FolderPicking.MaxAttempts, titles.Count);
    }

    [Fact]
    public async Task Other_errors_of_the_dialog_are_not_taken_for_a_wrong_choice()
    {
        var titles = new List<string>();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => FolderPicking.PickAsync(Dialog(titles, () => throw new InvalidOperationException("no window")), Title));
        Assert.Single(titles);
    }

    // A dialog that notes its title and then gives what answer returns, or throws what it throws.
    private static Func<string, Task<string?>> Dialog(List<string> titles, Func<string?> answer) => async title =>
    {
        titles.Add(title);
        await Task.Yield();
        return answer();
    };
}
