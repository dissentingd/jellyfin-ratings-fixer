using Jellyfin.Plugin.RatingsFixer.Configuration;
using Jellyfin.Plugin.RatingsFixer.Ledger;
using Jellyfin.Plugin.RatingsFixer.Rating;
using Jellyfin.Plugin.RatingsFixer.Service;

namespace Jellyfin.Plugin.RatingsFixer.Tests;

public class ReviewSuggesterTests
{
    private static ReviewSuggester Suggester(PluginConfiguration? config = null)
    {
        config ??= new PluginConfiguration { SuggestFromCollections = true };
        var scale = new FakeScale();
        return new ReviewSuggester(scale, new RatingEngine(scale, RatingsFixerService.ToEngineOptions(config)), RatingsFixerService.ToSuggesterOptions(config));
    }

    [Fact]
    public void MatureKeywords_SuggestR_AndSayWhich()
    {
        var s = Suggester().Suggest(new ReviewFacts { Genres = ["Drama"], Tags = ["prostitution", "new york city"] })!;
        Assert.Equal("R", s.Rating);
        Assert.Contains("prostitution", s.Reasons[0]);
    }

    [Theory]
    [InlineData("rape", "rape and revenge", true)]
    [InlineData("rape", "she was raped", true)]
    [InlineData("rape", "grapes of wrath", false)]
    [InlineData("heroin", "heroin", true)]
    [InlineData("heroin", "strong female heroine", false)] // found live: 24 "heroine" titles were suggested R
    [InlineData("sex", "sex comedy", true)]
    [InlineData("sex", "sexism", false)]
    [InlineData("stripper", "two strippers", true)]
    [InlineData("prostitut*", "prostitution", true)]
    [InlineData("porn*", "pornography", true)]
    [InlineData("porn*", "a sporn", false)]
    [InlineData("serial killer", "Serial Killers", true)]
    public void Keywords_MatchWholeWords_OrPrefixesWithAStar(string keyword, string text, bool expected)
    {
        Assert.Equal(expected, new KeywordMatcher([keyword]).Find([text]).Count == 1);
    }

    [Fact]
    public void ExplicitKeywords_InTagsOrTitle_SuggestNc17()
    {
        var byTag = Suggester().Suggest(new ReviewFacts { Genres = ["Comedy"], Tags = ["softcore", "sex"] })!;
        Assert.Equal("NC-17", byTag.Rating);
        Assert.Contains("softcore", byTag.Reasons[0]);
        Assert.Equal("NC-17", Suggester().Suggest(new ReviewFacts { Name = "Burlesque Nights" })!.Rating);
    }

    [Fact]
    public void ExplicitKeywords_OnlyInTheSummary_SuggestR()
    {
        // A summary mentioning a stripper is usually a drama about one, not a sex film.
        var s = Suggester().Suggest(new ReviewFacts { Genres = ["Drama"], Overview = "A stripper tries to start over." })!;
        Assert.Equal("R", s.Rating);
        Assert.Contains("stripper (in summary)", s.Reasons[0]);
    }

    [Fact]
    public void MatureKeywords_CountInTheSummaryAndTitle()
    {
        Assert.Equal("R", Suggester().Suggest(new ReviewFacts { Overview = "A sexy comedy about two roommates." })!.Rating);
        Assert.Equal("R", Suggester().Suggest(new ReviewFacts { Name = "Naked Lunch" })!.Rating);
        Assert.Null(Suggester().Suggest(new ReviewFacts { Overview = "Our heroine fights sexism at work." }));
    }

    [Fact]
    public void CodeEra_UsFilmWithNoOtherSignal_SuggestsG()
    {
        var s = Suggester().Suggest(new ReviewFacts { Year = 1944, ProductionLocations = ["United States of America"] })!;
        Assert.Equal("G", s.Rating);
        Assert.Contains("Production Code", s.Reasons[0]);
    }

    [Theory]
    [InlineData(1964, "Japan")] // the Code was American: Zatoichi isn't a G
    [InlineData(1972, "United States of America")]
    [InlineData(1930, "United States of America")]
    public void CodeEra_OnlyUsFilmsOfThoseYears(int year, string country)
    {
        Assert.Null(Suggester().Suggest(new ReviewFacts { Year = year, ProductionLocations = [country] }));
    }

    [Fact]
    public void CodeEra_NeedsKnownUsOnlyProduction_AndYieldsToOtherSignals()
    {
        Assert.Null(Suggester().Suggest(new ReviewFacts { Year = 1950 }));
        Assert.Null(Suggester().Suggest(new ReviewFacts { Year = 1950, ProductionLocations = ["United States of America", "Italy"] }));
        Assert.Equal("PG-13", Suggester().Suggest(new ReviewFacts { Year = 1950, ProductionLocations = ["United States of America"], Genres = ["Crime"] })!.Rating);
    }

    [Fact]
    public void CodeEra_BlankTurnsItOff()
    {
        var config = new PluginConfiguration { CodeEraYears = string.Empty };
        Assert.Null(Suggester(config).Suggest(new ReviewFacts { Year = 1944, ProductionLocations = ["United States of America"] }));
    }

    [Fact]
    public void OldDefaultKeywords_AreMigrated_CustomOnesKept()
    {
        var old = new PluginConfiguration { MatureKeywords = PluginConfiguration.MatureKeywords13 };
        Assert.True(old.MigrateSuggestionDefaults());
        Assert.Equal(new PluginConfiguration().MatureKeywords, old.MatureKeywords);

        var custom = new PluginConfiguration { MatureKeywords = "gore" };
        Assert.False(custom.MigrateSuggestionDefaults());
        Assert.Equal("gore", custom.MatureKeywords);
    }

    [Fact]
    public void Genres_UseTheStrictestMatch()
    {
        Assert.Equal("PG", Suggester().Suggest(new ReviewFacts { Genres = ["Family", "Comedy"] })!.Rating);
        Assert.Equal("R", Suggester().Suggest(new ReviewFacts { Genres = ["Animation", "Horror"] })!.Rating);
    }

    [Fact]
    public void KeywordsOutrankALooserGenre()
    {
        var s = Suggester().Suggest(new ReviewFacts { Genres = ["Family"], Tags = ["gore"] })!;
        Assert.Equal("R", s.Rating);
        Assert.Equal(2, s.Reasons.Count); // both signals are shown, strictest first
    }

    [Fact]
    public void CollectionSiblings_SuggestTheStrictestSibling()
    {
        var s = Suggester().Suggest(new ReviewFacts { SiblingRatings = [("Saga", "PG"), ("Saga", "PG-13"), ("Saga", "NR")] })!;
        Assert.Equal("PG-13", s.Rating);
        Assert.Contains("Saga", s.Reasons[0]);
    }

    [Fact]
    public void CollectionSiblings_ThatAreAllUnrated_SayNothing()
    {
        // Found live: a collection whose other entries are all "NR" crashed the review list.
        Assert.Null(Suggester().Suggest(new ReviewFacts { SiblingRatings = [("Saga", "NR"), ("Saga", "Not Rated")] }));
    }

    [Fact]
    public void CollectionSiblings_AreOffByDefault()
    {
        Assert.False(new PluginConfiguration().SuggestFromCollections);
        Assert.Equal(4, new PluginConfiguration().SuggestionCollectionMaxSize);
    }

    [Fact]
    public void CollectionSiblings_CanBeTurnedOff()
    {
        var config = new PluginConfiguration { SuggestFromCollections = false };
        Assert.Null(Suggester(config).Suggest(new ReviewFacts { SiblingRatings = [("Saga", "R")] }));
    }

    [Fact]
    public void PreCodeTag_SuggestsPg13()
    {
        Assert.Equal("PG-13", Suggester().Suggest(new ReviewFacts { Year = 1932, Tags = ["pre-code"] })!.Rating);
    }

    [Fact]
    public void NothingKnown_NoSuggestion()
    {
        Assert.Null(Suggester().Suggest(new ReviewFacts { Genres = ["Comedy"], Tags = ["new york city"] }));
    }

    [Fact]
    public void SeriesSuggestions_UseTheSeriesLadder()
    {
        Assert.Equal("TV-MA", Suggester().Suggest(new ReviewFacts { IsSeries = true, Genres = ["Horror"] })!.Rating);
    }
}

[Collection(JellyfinStaticsCollection.Name)]
public class ReviewPanelTests : ServiceTestBase
{
    [Fact]
    public async Task ReviewList_ShowsMetadata_AndSuggestionsIncludingCollectionSiblings()
    {
        Context.Configuration.SuggestFromCollections = true;
        var sequel = Library.AddMovie("Sequel", tmdb: "1");
        sequel.Overview = "The story continues.";
        var original = Library.AddMovie("Original", official: "PG-13", tmdb: "2");
        var horror = Library.AddMovie("Scary", tmdb: "3");
        horror.Genres = ["Horror"];
        Library.AddCollection("Saga", original, sequel);
        await PreviewAsync();

        var items = Service.GetReviewItems();

        Assert.Equal(2, items.Count);
        var s = items.Single(i => i.ItemId == sequel.Id);
        Assert.Equal(("PG-13", "The story continues."), (s.Suggestion, s.Overview));
        Assert.Equal("R", items.Single(i => i.ItemId == horror.Id).Suggestion);
    }

    [Fact]
    public async Task BigCollections_AreIgnoredForSuggestions()
    {
        Context.Configuration.SuggestFromCollections = true;
        var unrated = Library.AddMovie("Unrated", tmdb: "1");
        var strict = Library.AddMovie("Strict", official: "R", tmdb: "2");
        var list = Library.AddCollection("Top Rated", [unrated, strict, .. Enumerable.Range(0, 5).Select(i => Library.AddMovie($"Filler {i}", official: "G", tmdb: $"f{i}"))]);
        Context.Configuration.SuggestionCollectionMaxSize = 5;
        await PreviewAsync();

        Assert.Null(Service.GetReviewItems().Single(i => i.ItemId == unrated.Id).Suggestion);

        Context.Configuration.SuggestionCollectionMaxSize = 30;
        Assert.Equal("R", Service.GetReviewItems().Single(i => i.ItemId == unrated.Id).Suggestion);
    }

    [Fact]
    public async Task Assign_WritesThroughTheNormalPath_AndLeavesTheReviewList()
    {
        var movie = Library.AddMovie("Unrated", tmdb: "1");
        await PreviewAsync();

        var result = await Service.AssignAsync([new ReviewAssignment { ItemId = movie.Id, Rating = "R", AcceptedSuggestion = true }], CancellationToken.None);

        Assert.Equal(1, result.Written);
        Assert.Equal(("R", 17), (movie.CustomRating, movie.InheritedParentalRatingValue));
        var entry = Assert.Single(RatingLedger.Load(Service.LedgerPath).Entries);
        Assert.Contains("accepted suggestion", entry.Reason);
        var report = Service.LoadReport()!;
        Assert.Equal((0, 1), (report.Counts["Review"], report.Counts["Raise"]));
        Assert.Empty(Service.GetReviewItems());

        // ...and like any written rating, it can be reverted.
        await Service.RevertAsync([movie.Id], apply: true, exclude: false, CancellationToken.None);
        Assert.Null(movie.CustomRating);
    }

    [Fact]
    public async Task Assign_ToASeries_ReachesItsEpisodes()
    {
        var series = Library.AddSeries("Show");
        var episode = Library.AddEpisode(series, "Pilot");
        await PreviewAsync();

        var result = await Service.AssignAsync([new ReviewAssignment { ItemId = series.Id, Rating = "TV-14" }], CancellationToken.None);

        Assert.Equal(1, result.ChildrenUpdated);
        Assert.Equal("TV-14", episode.CustomRating);
    }

    [Fact]
    public async Task Assign_RejectsARatingTheServerCannotScore()
    {
        var movie = Library.AddMovie("Unrated", tmdb: "1");

        var result = await Service.AssignAsync([new ReviewAssignment { ItemId = movie.Id, Rating = "Banana" }], CancellationToken.None);

        Assert.Equal(0, result.Written);
        Assert.Single(result.Errors);
        Assert.Null(movie.CustomRating);
    }
}
