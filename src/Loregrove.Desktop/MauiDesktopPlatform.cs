using Loregrove.Application.Platform;
using Microsoft.Maui.Storage;

namespace Loregrove.Desktop;

/// <summary>
/// MAUI-backed desktop capabilities. Only file selection is enabled by Prompt 04.
/// </summary>
public sealed class MauiDesktopPlatform : IDesktopPlatform
{
    public async Task<IReadOnlyList<PickedFile>> PickFilesAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var selected = await FilePicker.Default.PickMultipleAsync(new PickOptions
        {
            PickerTitle = "Import files into Loregrove",
        });
        cancellationToken.ThrowIfCancellationRequested();

        return selected?.Select(ToPickedFile).ToArray() ?? [];
    }

    public Task<PickedFolder?> PickFolderAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<PickedFolder?>(null);
    }

    public Task OpenExternalFileAsync(string path, CancellationToken cancellationToken) =>
        UnsupportedAsync(cancellationToken);

    public Task RevealFileAsync(string path, CancellationToken cancellationToken) =>
        UnsupportedAsync(cancellationToken);

    public Task SetClipboardTextAsync(string text, CancellationToken cancellationToken) =>
        UnsupportedAsync(cancellationToken);

    private static PickedFile ToPickedFile(FileResult file) =>
        new(
            file.FileName,
            file.FileName,
            file.ContentType,
            Size: null,
            cancellationToken => OpenReadAsync(file, cancellationToken));

    private static async Task<Stream> OpenReadAsync(
        FileResult file,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var stream = await file.OpenReadAsync();
        if (cancellationToken.IsCancellationRequested)
        {
            await stream.DisposeAsync();
            cancellationToken.ThrowIfCancellationRequested();
        }

        return stream;
    }

    private static Task UnsupportedAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromException(
            new NotSupportedException("This desktop capability has not been enabled for the current host."));
    }
}
