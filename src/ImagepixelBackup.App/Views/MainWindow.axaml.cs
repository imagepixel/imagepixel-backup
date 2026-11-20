using System;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using ImagepixelBackup.App.ViewModels;

namespace ImagepixelBackup.App.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is MainViewModel viewModel)
        {
            viewModel.PickFolder = title => FolderPicking.PickAsync(OpenFolderPickerAsync, title);
        }
    }

    // The dialog of the system. A place without a folder on the disk behind it comes back
    // as an exception, from Avalonia or from here, and FolderPicking asks again.
    private async Task<string?> OpenFolderPickerAsync(string title)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
        });

        if (folders.Count == 0)
        {
            return null;
        }

        return folders[0].TryGetLocalPath() ?? throw new DirectoryNotFoundException("The chosen place is no folder on the disk.");
    }
}
