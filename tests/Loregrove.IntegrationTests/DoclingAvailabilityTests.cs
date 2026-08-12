using Loregrove.Application.Docling;
using Loregrove.Application.Parsing;
using Loregrove.Application.Storage;
using Loregrove.Domain.Sources;
using Loregrove.Infrastructure.Docling;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Loregrove.IntegrationTests;

public sealed class DoclingAvailabilityTests
{
    public static TheoryData<string, string, bool> SupportedFormats => new()
    {
        { "document.pdf", "application/pdf", true },
        { "document.docx", "application/vnd.openxmlformats-officedocument.wordprocessingml.document", true },
        { "document.pptx", "application/vnd.openxmlformats-officedocument.presentationml.presentation", true },
        { "document.xlsx", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", true },
        { "image.png", "image/png", true },
        { "image.jpg", "image/jpeg", true },
        { "image.jpeg", "image/jpeg", true },
        { "image.tif", "image/tiff", true },
        { "image.tiff", "image/tiff", true },
        { "image.bmp", "image/bmp", true },
        { "image.webp", "image/webp", true },
        { "document.txt", "text/plain", false },
        { "document.md", "text/markdown", false },
    };

    [Theory]
    [MemberData(nameof(SupportedFormats))]
    public void ParserRecognizesOnlyComplexFormats(string fileName, string mediaType, bool expected)
    {
        using var provider = BuildProvider(configuration => configuration.Mode = DoclingMode.Disabled);
        var parser = provider.GetServices<IDocumentParser>().Single(item => item.Descriptor.Id == "loregrove.docling");

        Assert.Equal(expected, parser.CanParse(Source(fileName, mediaType)));
    }

    [Fact]
    public void SpecificUnsupportedMediaTypeDoesNotFallBackByExtension()
    {
        using var provider = BuildProvider(configuration => configuration.Mode = DoclingMode.Disabled);
        var parser = provider.GetServices<IDocumentParser>().Single(item => item.Descriptor.Id == "loregrove.docling");

        Assert.False(parser.CanParse(Source("looks-like.pdf", "text/plain")));
        Assert.True(parser.CanParse(Source("looks-like.pdf", "application/octet-stream")));
    }

    [Fact]
    public async Task DisabledOneShotAndRemotePrivacyConditionsAreTypedAndNonConverting()
    {
        await AssertDeferredAsync(
            configuration => configuration.Mode = DoclingMode.Disabled,
            ParserAvailabilityReason.DoclingDisabled);
        await AssertDeferredAsync(
            configuration => configuration.Mode = DoclingMode.OneShot,
            ParserAvailabilityReason.DoclingOneShotDeferred);
        await AssertDeferredAsync(
            configuration =>
            {
                configuration.Mode = DoclingMode.Remote;
                configuration.RemoteEndpoint = new Uri("http://127.0.0.1:7777/");
                configuration.AllowRemoteDocumentUpload = false;
            },
            ParserAvailabilityReason.RemoteConsentRequired);
        await AssertDeferredAsync(
            configuration =>
            {
                configuration.Mode = DoclingMode.Remote;
                configuration.AllowRemoteDocumentUpload = true;
            },
            ParserAvailabilityReason.RemoteEndpointMissing);
        await AssertDeferredAsync(
            configuration =>
            {
                configuration.Mode = DoclingMode.Remote;
                configuration.RemoteEndpoint = new Uri("http://192.168.1.5:7777/");
                configuration.AllowRemoteDocumentUpload = true;
            },
            ParserAvailabilityReason.RemoteEndpointInvalid);
        await AssertDeferredAsync(
            configuration =>
            {
                configuration.Mode = DoclingMode.Remote;
                configuration.RemoteEndpoint = new Uri("https://docling.example.test/");
                configuration.AllowRemoteDocumentUpload = true;
                configuration.RemoteCredentialKey = "missing-api-key";
            },
            ParserAvailabilityReason.RemoteCredentialUnavailable);
    }

    [Fact]
    public async Task ManagedMissingPackIsDeferredBeforeProcessAcquisition()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"loregrove-docling-availability-{Guid.NewGuid():N}");
        using var provider = BuildProvider(configuration =>
        {
            configuration.Mode = DoclingMode.ManagedLocal;
            configuration.DeveloperPackOverridePath = Path.Combine(directory, "missing");
        });
        var parser = Assert.IsAssignableFrom<IDocumentParserAvailability>(
            provider.GetServices<IDocumentParser>().Single(item => item.Descriptor.Id == "loregrove.docling"));

        var availability = await parser.GetAvailabilityAsync(Source("source.pdf", "application/pdf"), CancellationToken.None);

        Assert.Equal(ParserAvailabilityState.Deferred, availability.State);
        Assert.Equal(ParserAvailabilityReason.DoclingPackMissing, availability.Reason);
        Assert.Equal(DoclingProcessState.Stopped, provider.GetRequiredService<Loregrove.Application.Docling.IDoclingProcessManager>().GetSnapshot().State);
    }

    [Fact]
    public async Task RemoteHttpsAndLoopbackHttpAreAvailableOnlyWithConsent()
    {
        foreach (var endpoint in new[] { new Uri("https://docling.example.test/"), new Uri("http://127.0.0.1:5001/") })
        {
            using var provider = BuildProvider(configuration =>
            {
                configuration.Mode = DoclingMode.Remote;
                configuration.RemoteEndpoint = endpoint;
                configuration.AllowRemoteDocumentUpload = true;
            });
            var parser = Assert.IsAssignableFrom<IDocumentParserAvailability>(
                provider.GetServices<IDocumentParser>().Single(item => item.Descriptor.Id == "loregrove.docling"));
            Assert.Equal(ParserAvailabilityState.Available,
                (await parser.GetAvailabilityAsync(Source("source.pdf", "application/pdf"), CancellationToken.None)).State);
        }
    }

    private static async Task AssertDeferredAsync(
        Action<DoclingConfiguration> configure,
        ParserAvailabilityReason reason)
    {
        using var provider = BuildProvider(configure);
        var parser = Assert.IsAssignableFrom<IDocumentParserAvailability>(
            provider.GetServices<IDocumentParser>().Single(item => item.Descriptor.Id == "loregrove.docling"));
        var availability = await parser.GetAvailabilityAsync(Source("source.pdf", "application/pdf"), CancellationToken.None);
        Assert.Equal(ParserAvailabilityState.Deferred, availability.State);
        Assert.Equal(reason, availability.Reason);
    }

    private static ServiceProvider BuildProvider(Action<DoclingConfiguration> configure)
    {
        var services = new ServiceCollection();
        services.AddLoregroveParsing();
        services.AddLoregroveDocling(configure);
        services.Replace(ServiceDescriptor.Singleton<IObjectStore, ThrowingObjectStore>());
        return services.BuildServiceProvider();
    }

    private static ParseSourceDescriptor Source(string fileName, string mediaType) => new(
        SourceDocumentVersionId.New(),
        new string('a', 64),
        fileName,
        mediaType);

    private sealed class ThrowingObjectStore : IObjectStore
    {
        public Task<StoredObject> StoreAsync(Stream content, CancellationToken cancellationToken) => throw new InvalidOperationException();
        public Task<Stream> OpenReadAsync(string objectKey, CancellationToken cancellationToken) => throw new InvalidOperationException();
        public Task<bool> ExistsAsync(string objectKey, CancellationToken cancellationToken) => throw new InvalidOperationException();
    }
}
