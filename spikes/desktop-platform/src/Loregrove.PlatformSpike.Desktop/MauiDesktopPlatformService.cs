using System.Diagnostics;
using Loregrove.PlatformSpike.Services;

namespace Loregrove.PlatformSpike.Desktop;

public sealed class MauiDesktopPlatformService : IDesktopPlatformService
{
    public string PlatformName => DeviceInfo.Platform.ToString();

    public async Task<IReadOnlyList<PickedFile>> PickFilesAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var results = await FilePicker.Default.PickMultipleAsync(new PickOptions { PickerTitle = "Select Loregrove spike files" });
        return (results ?? []).Select(file => new PickedFile(file.FileName, file.FullPath, TryGetSize(file.FullPath))).ToArray();
    }

    public async Task<PickedFolder?> PickFolderAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
#if WINDOWS
        var picker = new Windows.Storage.Pickers.FolderPicker();
        picker.FileTypeFilter.Add("*");
        var nativeWindow = Application.Current?.Windows.FirstOrDefault()?.Handler?.PlatformView as Microsoft.UI.Xaml.Window
            ?? throw new InvalidOperationException("The native WinUI window is not ready.");
        var windowHandle = WinRT.Interop.WindowNative.GetWindowHandle(nativeWindow);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, windowHandle);
        var folder = await picker.PickSingleFolderAsync();
        return folder is null ? null : new PickedFolder(folder.Name, folder.Path);
#elif MACCATALYST
        var picker = new UIKit.UIDocumentPickerViewController([UniformTypeIdentifiers.UTTypes.Folder], asCopy: false)
        {
            AllowsMultipleSelection = false
        };
        var completion = new TaskCompletionSource<PickedFolder?>(TaskCreationOptions.RunContinuationsAsynchronously);
        picker.DidPickDocumentAtUrls += (_, args) =>
        {
            var url = args.Urls.FirstOrDefault();
            completion.TrySetResult(url is null ? null : new PickedFolder(url.LastPathComponent ?? "Selected folder", url.Path ?? string.Empty));
        };
        picker.WasCancelled += (_, _) => completion.TrySetResult(null);
        var controller = Microsoft.Maui.ApplicationModel.Platform.GetCurrentUIViewController()
            ?? throw new InvalidOperationException("The native Mac Catalyst view controller is not ready.");
        await controller.PresentViewControllerAsync(picker, true);
        return await completion.Task.WaitAsync(cancellationToken);
#else
        throw new PlatformNotSupportedException();
#endif
    }

    public Task CopyTextAsync(string text, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Clipboard.Default.SetTextAsync(text);
    }

    public Task WriteSecretAsync(string key, string secret, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return SecureStorage.Default.SetAsync(key, secret);
    }

    public Task<string?> ReadSecretAsync(string key, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return SecureStorage.Default.GetAsync(key);
    }

    public Task DeleteSecretAsync(string key, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        SecureStorage.Default.Remove(key);
        return Task.CompletedTask;
    }

    public async Task OpenFileAsync(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await Launcher.Default.OpenAsync(new OpenFileRequest("Open source", new ReadOnlyFile(path)));
    }

    public Task RevealFileAsync(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
#if WINDOWS
        Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
#elif MACCATALYST
        Process.Start(new ProcessStartInfo("open", $"-R \"{path}\"") { UseShellExecute = false });
#else
        throw new PlatformNotSupportedException();
#endif
        return Task.CompletedTask;
    }

    private static long? TryGetSize(string path)
    {
        try { return File.Exists(path) ? new FileInfo(path).Length : null; }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }
}
