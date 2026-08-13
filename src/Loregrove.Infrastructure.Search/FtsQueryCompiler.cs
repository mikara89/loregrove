using System.Text.RegularExpressions;

namespace Loregrove.Infrastructure.Search;

public static partial class FtsQueryCompiler
{
    public const int MaximumTerms = 32;

    public static string? Compile(string userText)
    {
        ArgumentNullException.ThrowIfNull(userText);
        var terms = LiteralTermRegex().Matches(userText)
            .Select(match => match.Value)
            .Take(MaximumTerms)
            .ToArray();
        return terms.Length == 0
            ? null
            : string.Join(" AND ", terms.Select(term => $"\"{term}\""));
    }

    [GeneratedRegex(@"[\p{L}\p{M}\p{Nd}_]+", RegexOptions.CultureInvariant)]
    private static partial Regex LiteralTermRegex();
}
