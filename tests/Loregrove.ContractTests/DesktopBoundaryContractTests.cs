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
}
