namespace Loregrove.Application.Chunking;

public sealed class ChunkCandidateValidationException(string message) : Exception(message);

public static class ChunkCandidateValidator
{
    public static void Validate(
        ChunkingDocument document,
        IReadOnlyList<ChunkCandidate> candidates,
        ChunkerDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(descriptor);

        var observations = new Dictionary<Loregrove.Domain.Sources.SourceAnchorId, ChunkingObservation>();
        for (var observationOrdinal = 0; observationOrdinal < document.Observations.Count; observationOrdinal++)
        {
            var observation = document.Observations[observationOrdinal];
            if (observation.AnchorOrdinal != observationOrdinal)
            {
                throw new ChunkCandidateValidationException("Chunking observations must have contiguous ordered ordinals.");
            }

            if (!observations.TryAdd(observation.SourceAnchorId, observation))
            {
                throw new ChunkCandidateValidationException("Chunking observations contain duplicate source-anchor identities.");
            }
        }

        var anchorRanges = observations.Keys.ToDictionary(
            anchorId => anchorId,
            _ => new List<(int Start, int End)>());
        var previousAnchorOrdinal = -1;
        var previousAnchorEnd = 0;
        for (var ordinal = 0; ordinal < candidates.Count; ordinal++)
        {
            var candidate = candidates[ordinal];
            if (candidate.Ordinal != ordinal)
            {
                throw new ChunkCandidateValidationException("Chunk ordinals must be contiguous and ordered.");
            }

            if (string.IsNullOrEmpty(candidate.Text) || candidate.ContextText is null || candidate.EvidenceSpans.Count == 0)
            {
                throw new ChunkCandidateValidationException("Every chunk must contain source-derived text and evidence spans.");
            }

            var canonicalContent = string.IsNullOrEmpty(candidate.ContextText)
                ? candidate.Text
                : $"{candidate.ContextText}\n\n{candidate.Text}";
            var expectedContentHash = ChunkerDescriptor.Hash(canonicalContent);
            if (!string.Equals(candidate.ContentHash, expectedContentHash, StringComparison.Ordinal))
            {
                throw new ChunkCandidateValidationException("A chunk content hash does not match its canonical content.");
            }

            var previousChunkEnd = 0;
            for (var spanOrdinal = 0; spanOrdinal < candidate.EvidenceSpans.Count; spanOrdinal++)
            {
                var span = candidate.EvidenceSpans[spanOrdinal];
                if (!observations.TryGetValue(span.SourceAnchorId, out var observation) ||
                    span.AnchorOrdinal != observation.AnchorOrdinal ||
                    !string.Equals(span.AnchorTextHash, observation.NormalizedTextHash, StringComparison.Ordinal) ||
                    !string.Equals(span.LocatorFingerprint, observation.LocatorFingerprint, StringComparison.Ordinal))
                {
                    throw new ChunkCandidateValidationException("A chunk evidence span does not identify an input observation exactly.");
                }

                var expectedContext = string.Join(
                    " › ",
                    observation.HeadingPath.Where(value => !string.IsNullOrWhiteSpace(value)));
                if (!string.Equals(candidate.ContextText, expectedContext, StringComparison.Ordinal))
                {
                    throw new ChunkCandidateValidationException("Chunk context does not match its source observation heading path.");
                }

                if (span.AnchorStart < 0 || span.AnchorEnd <= span.AnchorStart ||
                    span.AnchorEnd > observation.NormalizedText.Length ||
                    span.ChunkStart < 0 || span.ChunkEnd <= span.ChunkStart ||
                    span.ChunkEnd > candidate.Text.Length)
                {
                    throw new ChunkCandidateValidationException("A chunk evidence span is outside its anchor or chunk text.");
                }

                if (span.ChunkStart < previousChunkEnd)
                {
                    throw new ChunkCandidateValidationException("Chunk evidence spans must be ordered and non-overlapping.");
                }

                if ((spanOrdinal == 0 && span.ChunkStart != 0) ||
                    (spanOrdinal > 0 && !candidate.Text.AsSpan(previousChunkEnd, span.ChunkStart - previousChunkEnd)
                        .SequenceEqual("\n\n".AsSpan())))
                {
                    throw new ChunkCandidateValidationException("Only the canonical separator may be unmapped chunk text.");
                }

                if (!candidate.Text.AsSpan(span.ChunkStart, span.ChunkEnd - span.ChunkStart)
                    .SequenceEqual(observation.NormalizedText.AsSpan(
                        span.AnchorStart,
                        span.AnchorEnd - span.AnchorStart)))
                {
                    throw new ChunkCandidateValidationException("A chunk evidence span does not map identical source text.");
                }

                if (span.AnchorOrdinal == previousAnchorOrdinal)
                {
                    if (span.AnchorStart != previousAnchorEnd)
                    {
                        throw new ChunkCandidateValidationException("Source-anchor mappings contain a gap, overlap, or reorder.");
                    }
                }
                else
                {
                    if (span.AnchorOrdinal != previousAnchorOrdinal + 1 || span.AnchorStart != 0 ||
                        (previousAnchorOrdinal >= 0 &&
                         previousAnchorEnd != document.Observations[previousAnchorOrdinal].NormalizedText.Length))
                    {
                        throw new ChunkCandidateValidationException("Source anchors must be mapped completely in input order.");
                    }
                }

                previousChunkEnd = span.ChunkEnd;
                previousAnchorOrdinal = span.AnchorOrdinal;
                previousAnchorEnd = span.AnchorEnd;
                anchorRanges[span.SourceAnchorId].Add((span.AnchorStart, span.AnchorEnd));
            }

            if (previousChunkEnd != candidate.Text.Length)
            {
                throw new ChunkCandidateValidationException("Chunk text after its final evidence span is not mapped to evidence.");
            }

            var evidenceIdentity = string.Join(
                '\n',
                candidate.EvidenceSpans.Select(span => string.Join(
                    ':',
                    span.AnchorOrdinal,
                    span.AnchorTextHash,
                    span.LocatorFingerprint,
                    span.AnchorStart,
                    span.AnchorEnd,
                    span.ChunkStart,
                    span.ChunkEnd)));
            var expectedChunkKey = ChunkerDescriptor.Hash(string.Join(
                '\n',
                document.SourceContentHash,
                document.ParsedArtifactContentHash,
                descriptor.Fingerprint,
                ordinal,
                expectedContentHash,
                evidenceIdentity));
            if (!string.Equals(candidate.ChunkKey, expectedChunkKey, StringComparison.Ordinal))
            {
                throw new ChunkCandidateValidationException("A chunk key does not match its deterministic identity inputs.");
            }
        }

        if (document.Observations.Count > 0 &&
            (previousAnchorOrdinal != document.Observations.Count - 1 ||
             previousAnchorEnd != document.Observations[^1].NormalizedText.Length))
        {
            throw new ChunkCandidateValidationException("Chunk candidates do not preserve all source-anchor text.");
        }

        foreach (var observation in document.Observations)
        {
            var ranges = anchorRanges[observation.SourceAnchorId]
                .OrderBy(range => range.Start)
                .ThenBy(range => range.End)
                .ToArray();
            if (ranges.Length == 0 || ranges[0].Start != 0 || ranges[^1].End != observation.NormalizedText.Length)
            {
                throw new ChunkCandidateValidationException("Chunk candidates do not preserve all source-anchor text.");
            }

            for (var index = 1; index < ranges.Length; index++)
            {
                if (ranges[index].Start != ranges[index - 1].End)
                {
                    throw new ChunkCandidateValidationException("Source-anchor mappings contain a gap or overlap.");
                }
            }
        }
    }
}
