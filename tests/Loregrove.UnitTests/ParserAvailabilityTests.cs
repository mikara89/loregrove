using Loregrove.Application.Parsing;
using Loregrove.Domain.Sources;

namespace Loregrove.UnitTests;

public sealed class ParserAvailabilityTests
{
    [Fact]
    public async Task AvailabilityIsCheckedBeforeDynamicDescriptorAndParse()
    {
        var parser = new DeferredParser();
        var descriptor = new ParseSourceDescriptor(
            SourceDocumentVersionId.New(),
            new string('a', 64),
            "document.pdf",
            "application/pdf");

        var availability = await parser.GetAvailabilityAsync(descriptor, CancellationToken.None);

        Assert.Equal(ParserAvailabilityState.Deferred, availability.State);
        Assert.Equal(ParserAvailabilityReason.DoclingDisabled, availability.Reason);
        Assert.False(parser.DescriptorRequested);
        Assert.False(parser.ParseRequested);
    }

    private sealed class DeferredParser : IDocumentParser, IDocumentParserAvailability, IDocumentParserDescriptorProvider
    {
        public bool DescriptorRequested { get; private set; }
        public bool ParseRequested { get; private set; }
        public ParserDescriptor Descriptor { get; } = ParserDescriptor.Create("deferred", "1", 1, "static");
        public bool CanParse(ParseSourceDescriptor source) => true;

        public Task<ParserAvailability> GetAvailabilityAsync(ParseSourceDescriptor source, CancellationToken cancellationToken) =>
            Task.FromResult(ParserAvailability.Deferred(ParserAvailabilityReason.DoclingDisabled));

        public Task<ParserDescriptor> GetDescriptorAsync(ParseSourceDescriptor source, CancellationToken cancellationToken)
        {
            DescriptorRequested = true;
            return Task.FromResult(Descriptor);
        }

        public Task<ParsedDocumentResult> ParseAsync(Stream source, ParseSourceDescriptor descriptor, CancellationToken cancellationToken)
        {
            ParseRequested = true;
            throw new InvalidOperationException();
        }
    }
}
