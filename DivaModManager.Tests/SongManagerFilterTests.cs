using DivaModManager.UI;
using Xunit;

namespace DivaModManager.Tests;

public sealed class SongManagerFilterTests
{
    [Fact]
    public void DifficultyAndLevelMustMatchTheSameChart()
    {
        var song = new SongEntry
        {
            Difficulties = new[]
            {
                new SongDifficulty { NormalizedName = "easy", NumericLevel = 3m },
                new SongDifficulty { NormalizedName = "hard", NumericLevel = 8m }
            }
        };

        Assert.True(SongManagerWindow.MatchesDifficultyFilters(song, "easy", 3m, false));
        Assert.True(SongManagerWindow.MatchesDifficultyFilters(song, "hard", 8m, false));
        Assert.False(SongManagerWindow.MatchesDifficultyFilters(song, "easy", 8m, false));
    }

    [Fact]
    public void UnknownLevelMatchesOnlyMalformedOrOutOfRangeChart()
    {
        var song = new SongEntry
        {
            Difficulties = new[]
            {
                new SongDifficulty { NormalizedName = "extreme", NumericLevel = 10m },
                new SongDifficulty { NormalizedName = "ex_extreme", NumericLevel = null }
            }
        };

        Assert.True(SongManagerWindow.MatchesDifficultyFilters(song, null, null, true));
        Assert.True(SongManagerWindow.MatchesDifficultyFilters(song, "ex_extreme", null, true));
        Assert.False(SongManagerWindow.MatchesDifficultyFilters(song, "extreme", null, true));
    }
}
