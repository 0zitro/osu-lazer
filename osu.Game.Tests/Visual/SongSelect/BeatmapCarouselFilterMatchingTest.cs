// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using osu.Game.Beatmaps;
using osu.Game.Graphics.Carousel;
using osu.Game.Rulesets;
using osu.Game.Rulesets.Mods;
using osu.Game.Screens.Select;
using osu.Game.Screens.Select.Filter;
using osu.Game.Tests.Resources;

namespace osu.Game.Tests.Visual.SongSelect
{
    [TestFixture]
    public partial class BeatmapCarouselFilterMatchingTest
    {
        [Test]
        public async Task TestUserStarFilterUsesRecalculatedDifficultyWhenSortingByRecalculatedDifficulty()
        {
            var beatmap = TestResources.CreateTestBeatmapSetInfo(1).Beatmaps[0];
            beatmap.StarRating = 8;

            var criteria = createCriteria(SortMode.RecalculatedDifficulty, beatmap);

            var difficultyCache = new TestBeatmapDifficultyCache(new Dictionary<BeatmapInfo, double>
            {
                [beatmap] = 4,
            });

            var filter = new BeatmapCarouselFilterMatching(() => criteria, () => difficultyCache);
            var results = await filter.Run(new[] { new CarouselItem(beatmap) }, CancellationToken.None);

            Assert.That(results, Has.Count.EqualTo(1));
        }

        [Test]
        public async Task TestUserStarFilterStillUsesBaseDifficultyForOtherSortModes()
        {
            var beatmap = TestResources.CreateTestBeatmapSetInfo(1).Beatmaps[0];
            beatmap.StarRating = 8;

            var criteria = createCriteria(SortMode.Difficulty, beatmap);

            var difficultyCache = new TestBeatmapDifficultyCache(new Dictionary<BeatmapInfo, double>
            {
                [beatmap] = 4,
            });

            var filter = new BeatmapCarouselFilterMatching(() => criteria, () => difficultyCache);
            var results = await filter.Run(new[] { new CarouselItem(beatmap) }, CancellationToken.None);

            Assert.That(results, Has.Count.EqualTo(0));
        }

        private static FilterCriteria createCriteria(SortMode sort, BeatmapInfo beatmap)
        {
            var criteria = new FilterCriteria
            {
                Sort = sort,
                Ruleset = beatmap.Ruleset,
                Mods = Array.Empty<Mod>(),
            };

            criteria.UserStarDifficulty.Max = 5;
            return criteria;
        }

        private partial class TestBeatmapDifficultyCache : BeatmapDifficultyCache
        {
            private readonly IReadOnlyDictionary<BeatmapInfo, double> starsByBeatmap;

            public TestBeatmapDifficultyCache(IReadOnlyDictionary<BeatmapInfo, double> starsByBeatmap)
            {
                this.starsByBeatmap = starsByBeatmap;
            }

            public override Task<StarDifficulty?> GetDifficultyAsync(IBeatmapInfo beatmapInfo, IRulesetInfo? rulesetInfo = null, IEnumerable<Mod>? mods = null,
                                                                      CancellationToken cancellationToken = default, int computationDelay = 0)
            {
                if (beatmapInfo is BeatmapInfo beatmap && starsByBeatmap.TryGetValue(beatmap, out double stars))
                    return Task.FromResult<StarDifficulty?>(new StarDifficulty(stars, 0));

                return Task.FromResult<StarDifficulty?>(new StarDifficulty(beatmapInfo.StarRating, 0));
            }
        }
    }
}
