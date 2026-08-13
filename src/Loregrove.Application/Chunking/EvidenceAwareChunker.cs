using System.Text;
using Loregrove.Domain.Sources;

namespace Loregrove.Application.Chunking;

public sealed class EvidenceAwareChunker : IChunker
{
    private const string Separator = "\n\n";
    private readonly EvidenceAwareChunkerOptions _options;

    public EvidenceAwareChunker(EvidenceAwareChunkerOptions? options = null)
    {
        _options = options ?? new EvidenceAwareChunkerOptions();
        _options.Validate();
        Descriptor = ChunkerDescriptor.Create(
            "loregrove.evidence-aware",
            "1.0.0",
            1,
            _options.CanonicalConfiguration);
    }

    public ChunkerDescriptor Descriptor { get; }

    public IReadOnlyList<ChunkCandidate> Chunk(ChunkingDocument document, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(document);
        ValidateDocument(document);
        var fragments = document.Observations.SelectMany(SplitObservation).ToArray();
        var drafts = new List<Draft>();
        Draft? current = null;

        foreach (var fragment in fragments)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (current is null)
            {
                current = new Draft(fragment);
                continue;
            }

            if (CanCombine(current, fragment))
            {
                current.Append(fragment);
            }
            else
            {
                drafts.Add(current);
                current = new Draft(fragment);
            }
        }

        if (current is not null)
        {
            drafts.Add(current);
        }

        return drafts.Select((draft, ordinal) => Finalize(document, draft, ordinal)).ToArray();
    }

    private bool CanCombine(Draft current, Fragment next)
    {
        if (!current.HeadingPath.SequenceEqual(next.HeadingPath, StringComparer.Ordinal) ||
            IsAtomic(current.LastKind) || IsAtomic(next.Kind) ||
            current.Length >= _options.TargetCharacters)
        {
            return false;
        }

        return current.Length + Separator.Length + next.Length <= _options.MaximumCharacters;
    }

    private IEnumerable<Fragment> SplitObservation(ChunkingObservation observation)
    {
        var text = observation.NormalizedText;
        var offset = 0;
        while (offset < text.Length)
        {
            var remaining = text.Length - offset;
            var length = remaining <= _options.MaximumCharacters
                ? remaining
                : FindBreak(text, offset, _options.MaximumCharacters, observation.Kind);
            yield return new Fragment(observation, offset, offset + length);
            offset += length;
        }
    }

    private static int FindBreak(string text, int offset, int maximum, ParsedBlockKind kind)
    {
        var end = offset + maximum;
        var floor = offset + Math.Max(1, maximum / 2);
        if (kind is ParsedBlockKind.Table or ParsedBlockKind.Code)
        {
            var lineBreak = text.LastIndexOf('\n', end - 1, maximum);
            if (lineBreak >= floor)
            {
                return lineBreak - offset + 1;
            }
        }

        var paragraphBreak = text.LastIndexOf("\n\n", end - 1, maximum, StringComparison.Ordinal);
        if (paragraphBreak >= floor)
        {
            return paragraphBreak - offset + 2;
        }

        for (var index = end - 1; index >= floor; index--)
        {
            if (text[index] is '.' or '!' or '?' && index + 1 < text.Length && char.IsWhiteSpace(text[index + 1]))
            {
                return index - offset + 1;
            }
        }

        for (var index = end - 1; index >= floor; index--)
        {
            if (char.IsWhiteSpace(text[index]))
            {
                return index - offset + 1;
            }
        }

        var boundary = offset + maximum;
        if (boundary < text.Length &&
            char.IsHighSurrogate(text[boundary - 1]) &&
            char.IsLowSurrogate(text[boundary]))
        {
            // A UTF-16 surrogate pair is indivisible. Profiles with a one-character maximum must
            // allow this documented two-code-unit exception rather than persist invalid Unicode.
            return maximum == 1 ? 2 : maximum - 1;
        }

        return maximum;
    }

    private ChunkCandidate Finalize(ChunkingDocument document, Draft draft, int ordinal)
    {
        var text = draft.Text.ToString();
        var context = string.Join(" › ", draft.HeadingPath.Where(value => !string.IsNullOrWhiteSpace(value)));
        var canonicalContent = string.IsNullOrEmpty(context) ? text : $"{context}\n\n{text}";
        var contentHash = ChunkerDescriptor.Hash(canonicalContent);
        var evidenceIdentity = string.Join(
            '\n',
            draft.Spans.Select(span => string.Join(
                ':',
                span.AnchorOrdinal,
                span.AnchorTextHash,
                span.LocatorFingerprint,
                span.AnchorStart,
                span.AnchorEnd,
                span.ChunkStart,
                span.ChunkEnd)));
        var key = ChunkerDescriptor.Hash(string.Join(
            '\n',
            document.SourceContentHash,
            document.ParsedArtifactContentHash,
            Descriptor.Fingerprint,
            ordinal,
            contentHash,
            evidenceIdentity));
        return new ChunkCandidate(ordinal, key, text, context, contentHash, draft.Spans.ToArray());
    }

    private static void ValidateDocument(ChunkingDocument document)
    {
        if (document.DocumentVersionId.Value == Guid.Empty || document.ParsedArtifactId.Value == Guid.Empty)
        {
            throw new ArgumentException("Chunking document identities are required.", nameof(document));
        }

        ValidateHash(document.SourceContentHash, nameof(document.SourceContentHash));
        ValidateHash(document.ParsedArtifactContentHash, nameof(document.ParsedArtifactContentHash));
        ArgumentNullException.ThrowIfNull(document.Observations);
        for (var ordinal = 0; ordinal < document.Observations.Count; ordinal++)
        {
            var observation = document.Observations[ordinal];
            if (observation.AnchorOrdinal != ordinal || observation.SourceAnchorId.Value == Guid.Empty ||
                string.IsNullOrEmpty(observation.NormalizedText))
            {
                throw new InvalidDataException("Chunking observations must be non-empty and contiguously ordered.");
            }

            ValidateHash(observation.NormalizedTextHash, nameof(observation.NormalizedTextHash));
            ValidateHash(observation.LocatorFingerprint, nameof(observation.LocatorFingerprint));
        }
    }

    private static void ValidateHash(string value, string name)
    {
        if (value.Length != 64 || !value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f'))
        {
            throw new ArgumentException("A lowercase SHA-256 value is required.", name);
        }
    }

    private static bool IsAtomic(ParsedBlockKind kind) =>
        kind is ParsedBlockKind.Heading or ParsedBlockKind.Table or ParsedBlockKind.Code or ParsedBlockKind.Formula;

    private sealed record Fragment(ChunkingObservation Observation, int Start, int End)
    {
        public int Length => End - Start;
        public ParsedBlockKind Kind => Observation.Kind;
        public IReadOnlyList<string> HeadingPath => Observation.HeadingPath;
    }

    private sealed class Draft
    {
        public Draft(Fragment fragment)
        {
            HeadingPath = fragment.HeadingPath.ToArray();
            AppendCore(fragment);
        }

        public StringBuilder Text { get; } = new();
        public List<ChunkEvidenceCandidate> Spans { get; } = [];
        public IReadOnlyList<string> HeadingPath { get; }
        public ParsedBlockKind LastKind { get; private set; }
        public int Length => Text.Length;

        public void Append(Fragment fragment)
        {
            Text.Append(Separator);
            AppendCore(fragment);
        }

        private void AppendCore(Fragment fragment)
        {
            var chunkStart = Text.Length;
            Text.Append(fragment.Observation.NormalizedText, fragment.Start, fragment.Length);
            Spans.Add(new ChunkEvidenceCandidate(
                fragment.Observation.SourceAnchorId,
                fragment.Observation.AnchorOrdinal,
                fragment.Observation.NormalizedTextHash,
                fragment.Observation.LocatorFingerprint,
                fragment.Start,
                fragment.End,
                chunkStart,
                chunkStart + fragment.Length));
            LastKind = fragment.Kind;
        }
    }
}
