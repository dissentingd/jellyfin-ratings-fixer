using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.RatingsFixer.Rating;

/// <summary>What the suggester knows about a title on the review list.</summary>
public sealed record ReviewFacts
{
    /// <summary>Gets a value indicating whether the title is a series.</summary>
    public bool IsSeries { get; init; }

    /// <summary>Gets the title.</summary>
    public string? Name { get; init; }

    /// <summary>Gets the plot summary.</summary>
    public string? Overview { get; init; }

    /// <summary>Gets the production year.</summary>
    public int? Year { get; init; }

    /// <summary>Gets the production countries, as the metadata provider names them.</summary>
    public IReadOnlyList<string> ProductionLocations { get; init; } = [];

    /// <summary>Gets the genres.</summary>
    public IReadOnlyList<string> Genres { get; init; } = [];

    /// <summary>Gets the tags (TMDb keywords, for most libraries).</summary>
    public IReadOnlyList<string> Tags { get; init; } = [];

    /// <summary>Gets the effective ratings of other titles in the same collections, with the collection name.</summary>
    public IReadOnlyList<(string Collection, string Rating)> SiblingRatings { get; init; } = [];
}

/// <summary>A suggested rating and why.</summary>
/// <param name="Rating">The suggested rating, already on the title's ladder.</param>
/// <param name="Reasons">Every signal that contributed, strictest first.</param>
public sealed record ReviewSuggestion(string Rating, IReadOnlyList<string> Reasons);

/// <summary>Settings for <see cref="ReviewSuggester"/>.</summary>
public sealed record SuggesterOptions
{
    /// <summary>
    /// Gets keywords that suggest explicit sexual content. Found in a tag or the title they suggest
    /// <see cref="ExplicitRating"/>; found only in the plot summary, <see cref="MatureRating"/> — a summary
    /// mentioning a stripper or pornography is usually a drama or documentary, not a sex film.
    /// See <see cref="KeywordMatcher"/> for how keywords match.
    /// </summary>
    public IReadOnlyList<string> ExplicitKeywords { get; init; } = [];

    /// <summary>Gets the rating explicit keywords suggest.</summary>
    public string ExplicitRating { get; init; } = "NC-17";

    /// <summary>Gets keywords that suggest mature content, matched in tags, title and plot summary.</summary>
    public IReadOnlyList<string> MatureKeywords { get; init; } = [];

    /// <summary>Gets the rating mature keywords suggest.</summary>
    public string MatureRating { get; init; } = "R";

    /// <summary>Gets genre → rating defaults.</summary>
    public IReadOnlyDictionary<string, string> GenreRatings { get; init; } = new Dictionary<string, string>();

    /// <summary>Gets a value indicating whether ratings of other titles in the same collection count.</summary>
    public bool UseCollections { get; init; } = true;

    /// <summary>Gets tag → rating era hints (e.g. <c>pre-code</c> → <c>PG-13</c>).</summary>
    public IReadOnlyDictionary<string, string> EraTags { get; init; } = new Dictionary<string, string>();

    /// <summary>
    /// Gets the years of the Production Code era (inclusive), or null for no era hint. Studio films of
    /// those years had to pass the Code, so with no other signal they were mostly family viewing.
    /// </summary>
    public (int From, int To)? CodeEraYears { get; init; }

    /// <summary>Gets the rating the Code era suggests when nothing else does.</summary>
    public string CodeEraRating { get; init; } = "G";

    /// <summary>Gets the production countries the Code applied to; a title needs every country on this list.</summary>
    public IReadOnlyList<string> CodeEraCountries { get; init; } = ["United States of America", "United States", "USA", "US"];
}

/// <summary>
/// Matches keywords against text. A keyword matches whole words, allowing a plural or verb ending
/// (<c>rape</c> finds "raped", not "grapes"); a keyword ending in <c>*</c> matches any word starting with it
/// (<c>prostitut*</c> finds "prostitute" and "prostitution"). Case-insensitive.
/// </summary>
public sealed class KeywordMatcher
{
    private readonly (string Keyword, Regex Pattern)[] _patterns;

    /// <summary>Initializes a new instance of the <see cref="KeywordMatcher"/> class.</summary>
    /// <param name="keywords">The keywords.</param>
    public KeywordMatcher(IEnumerable<string> keywords)
    {
        _patterns = [.. keywords
            .Select(k => k.Trim())
            .Where(k => k.TrimEnd('*').Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(k => (k, new Regex(
                k.EndsWith('*') ? $@"(?<!\w){Regex.Escape(k.TrimEnd('*'))}" : $@"(?<!\w){Regex.Escape(k)}(?:s|es|d|ed|ing)?(?!\w)",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled)))];
    }

    /// <summary>Gets the words found in any of the texts (e.g. "prostitution" for <c>prostitut*</c>), lower-cased, one per keyword.</summary>
    /// <param name="texts">The texts.</param>
    /// <returns>The words found, in keyword order.</returns>
    public List<string> Find(IEnumerable<string?> texts)
    {
        var list = texts.Where(t => !string.IsNullOrEmpty(t)).ToList();
        var found = new List<string>();
        foreach (var (keyword, pattern) in _patterns)
        {
            foreach (var text in list)
            {
                if (pattern.Match(text!) is { Success: true } m)
                {
                    // A prefix keyword reports the whole word it started.
                    var end = m.Index + m.Length;
                    while (keyword.EndsWith('*') && end < text!.Length && char.IsLetterOrDigit(text[end]))
                    {
                        end++;
                    }

                    found.Add(text![m.Index..end].ToLowerInvariant());
                    break;
                }
            }
        }

        return [.. found.Distinct()];
    }
}

/// <summary>
/// Suggests ratings for review-list titles from their other metadata. Every signal proposes a rating; the
/// strictest wins, so a suggestion errs on the safe side. Suggestions are only ever shown for the admin to
/// accept — never written on their own. Pure (no Jellyfin types), so it's unit-tested directly.
/// </summary>
public sealed class ReviewSuggester(IRatingScale scale, RatingEngine engine, SuggesterOptions options)
{
    private readonly KeywordMatcher _explicit = new(options.ExplicitKeywords);
    private readonly KeywordMatcher _mature = new(options.MatureKeywords);

    /// <summary>Suggests a rating, or returns null when no signal applies.</summary>
    /// <param name="facts">What's known about the title.</param>
    /// <returns>The suggestion, or null.</returns>
    public ReviewSuggestion? Suggest(ReviewFacts facts)
    {
        var signals = new List<(RatingScore Score, string Rating, string Reason)>();

        void Add(string rating, string reason)
        {
            if (scale.Score(rating) is { } score)
            {
                signals.Add((score, rating, reason));
            }
        }

        // Tags and the title are deliberate labels; a plot summary just mentions things.
        string?[] labels = [.. facts.Tags, facts.Name];
        string?[] summary = [facts.Overview];
        var explicitHits = _explicit.Find(labels);
        if (explicitHits.Count > 0)
        {
            Add(options.ExplicitRating, $"explicit keywords: {List(explicitHits)}");
        }

        var matureHits = _mature.Find([.. labels, .. summary]);
        matureHits.AddRange(_explicit.Find(summary).Except(explicitHits, StringComparer.OrdinalIgnoreCase).Select(k => k + " (in summary)"));
        if (matureHits.Count > 0)
        {
            Add(options.MatureRating, $"keywords: {List(matureHits)}");
        }

        foreach (var genre in facts.Genres)
        {
            if (Lookup(options.GenreRatings, genre) is { } rating)
            {
                Add(rating, $"genre {genre} → {rating}");
            }
        }

        foreach (var tag in facts.Tags)
        {
            if (Lookup(options.EraTags, tag) is { } rating)
            {
                Add(rating, $"{tag} → {rating}");
            }
        }

        if (options.UseCollections)
        {
            // The strictest sibling in each collection: sequels and series entries usually share a rating.
            foreach (var group in facts.SiblingRatings.GroupBy(s => s.Collection, StringComparer.OrdinalIgnoreCase))
            {
                // Siblings can all be unscorable too (e.g. every entry "NR"): then this collection says nothing.
                var scored = group
                    .Select(s => (s.Rating, Score: scale.Score(s.Rating)))
                    .Where(s => s.Score is not null)
                    .ToList();
                if (scored.Count > 0)
                {
                    var strictest = scored.MaxBy(s => s.Score!.Value);
                    Add(strictest.Rating, $"collection “{group.Key}” has {strictest.Rating}");
                }
            }
        }

        if (signals.Count == 0
            && options.CodeEraYears is var (from, to)
            && facts.Year >= from && facts.Year <= to
            && facts.ProductionLocations.Count > 0
            && facts.ProductionLocations.All(l => options.CodeEraCountries.Contains(l, StringComparer.OrdinalIgnoreCase)))
        {
            Add(options.CodeEraRating, $"US film from the Production Code era ({facts.Year})");
        }

        if (signals.Count == 0)
        {
            return null;
        }

        // A rating that's already on the ladder is kept as is: mapping by score would fold NC-17 into R.
        var ordered = signals.OrderByDescending(s => s.Score).ToList();
        return (engine.OnLadder(ordered[0].Rating, facts.IsSeries) ?? engine.RatingFor(ordered[0].Score, facts.IsSeries)) is { } onLadder
            ? new ReviewSuggestion(onLadder, [.. ordered.Select(s => s.Reason)])
            : null;
    }

    private static string List(List<string> items) =>
        string.Join(", ", items.Take(4)) + (items.Count > 4 ? ", …" : string.Empty);

    private static string? Lookup(IReadOnlyDictionary<string, string> map, string key)
    {
        foreach (var (k, v) in map)
        {
            if (string.Equals(k, key, StringComparison.OrdinalIgnoreCase))
            {
                return v;
            }
        }

        return null;
    }
}
