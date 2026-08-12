using Loregrove.Application.Platform;
using Loregrove.Infrastructure.Desktop;

namespace Loregrove.ContractTests;

public sealed class DesktopBoundaryContractTests
{
    [Fact]
    public async Task BootstrapSecretStoreNeverPersistsASecret()
    {
        var store = new UnavailableSecretStore();

        Assert.Null(await store.GetAsync("provider-key", CancellationToken.None));
        await Assert.ThrowsAsync<NotSupportedException>(() =>
            store.SetAsync("provider-key", "secret", CancellationToken.None));
    }

    [Fact]
    public async Task BootstrapPickerReturnsNoSyntheticFiles()
    {
        var platform = new UnavailableDesktopPlatform();

        var files = await platform.PickFilesAsync(CancellationToken.None);

        Assert.Empty(files);
    }

    [Fact]
    public async Task PickedFileOwnsAPlatformNeutralStreamOpener()
    {
        var opened = false;
        var picked = new PickedFile(
            "Résumé notes.txt",
            "Résumé notes.txt",
            "text/plain",
            3,
            _ =>
            {
                opened = true;
                return Task.FromResult<Stream>(new MemoryStream([1, 2, 3], writable: false));
            });

        await using var stream = await picked.OpenReadAsync(CancellationToken.None);

        Assert.True(opened);
        Assert.True(stream.CanRead);
        Assert.Equal("Résumé notes.txt", picked.OriginalFileName);
    }
}
