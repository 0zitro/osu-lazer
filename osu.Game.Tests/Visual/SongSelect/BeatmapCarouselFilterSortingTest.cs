// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using osu.Framework.Extensions.IEnumerableExtensions;
using osu.Game.Beatmaps;
using osu.Game.Graphics.Carousel;
using osu.Game.Rulesets;
using osu.Game.Rulesets.Mods;
using osu.Game.Rulesets.Osu.Mods;
using osu.Game.Screens.Select;
using osu.Game.Screens.Select.Filter;
using osu.Game.Tests.Resources;

namespace osu.Game.Tests.Visual.SongSelect
{
    [TestFixture]
    public partial class BeatmapCarouselFilterSortingTest
    {
        [Test]
        public async Task TestSorting()
        {
            List<BeatmapSetInfo> beatmapSets = new List<BeatmapSetInfo>();

            const string zzz_lowercase = "zzzzz";
            const string zzz_uppercase = "ZZZZZ";
            const int diff_count = 5;

            for (int i = 0; i < 20; i++)
            {
                var set = TestResources.CreateTestBeatmapSetInfo(diff_count);

                if (i == 4)
                    set.Beatmaps.ForEach(b => b.Metadata.Artist = zzz_uppercase);

                if (i == 8)
                    set.Beatmaps.ForEach(b => b.Metadata.Artist = zzz_lowercase);

                if (i == 12)
                    set.Beatmaps.ForEach(b => b.Metadata.Author.Username = zzz_uppercase);

                if (i == 16)
                    set.Beatmaps.ForEach(b => b.Metadata.Author.Username = zzz_lowercase);

                beatmapSets.Add(set);
            }

            var results = await runSorting(SortMode.Author, beatmapSets);

            Assert.That(results.Last().Metadata.Author.Username, Is.EqualTo(zzz_uppercase));
            Assert.That(results.SkipLast(diff_count).Last().Metadata.Author.Username, Is.EqualTo(zzz_lowercase));

            results = await runSorting(SortMode.Artist, beatmapSets);

            Assert.That(results.Last().Metadata.Artist, Is.EqualTo(zzz_uppercase));
            Assert.That(results.SkipLast(diff_count).Last().Metadata.Artist, Is.EqualTo(zzz_lowercase));
        }

        [Test]
        public async Task TestSortingDateSubmitted()
        {
            List<BeatmapSetInfo> beatmapSets = new List<BeatmapSetInfo>();

            const string zzz_string = "zzzzz";

            for (int i = 0; i < 10; i++)
            {
                var set = TestResources.CreateTestBeatmapSetInfo(5);

                // A total of 6 sets have date submitted (4 don't)
                // A total of 5 sets have artist string (3 of which also have date submitted)

                if (i >= 2 && i < 8) // i = 2, 3, 4, 5, 6, 7 have submitted date
                    set.DateSubmitted = DateTimeOffset.Now.AddMinutes(i);
                if (i < 5) // i = 0, 1, 2, 3, 4 have matching string
                    set.Beatmaps.ForEach(b => b.Metadata.Artist = zzz_string);

                set.Beatmaps.ForEach(b => b.Metadata.Title = $"submitted: {set.DateSubmitted}");

                beatmapSets.Add(set);
            }

            var results = await runSorting(SortMode.DateSubmitted, beatmapSets);

            Assert.That(results.Count(), Is.EqualTo(50));

            Assert.That(results.Reverse().TakeWhile(b => b.BeatmapSet!.DateSubmitted == null).Count(), Is.EqualTo(20), () => "missing dates should be at the end");
            Assert.That(results.TakeWhile(b => b.BeatmapSet!.DateSubmitted != null).Count(), Is.EqualTo(30), () => "non-missing dates should be at the start");
        }

        [Test]
        public async Task TestSortByArtistUsesTitleAsTiebreaker()
        {
            List<BeatmapSetInfo> beatmapSets = new List<BeatmapSetInfo>();

            const int diff_count = 5;

            for (int i = 0; i < 20; i++)
            {
                var set = TestResources.CreateTestBeatmapSetInfo(diff_count);

                if (i == 4)
                {
                    set.Beatmaps.ForEach(b =>
                    {
                        b.Metadata.Artist = "ZZZ";
                        b.Metadata.Title = "AAA";
                    });
                }

                if (i == 8)
                {
                    set.Beatmaps.ForEach(b =>
                    {
                        b.Metadata.Artist = "ZZZ";
                        b.Metadata.Title = "ZZZ";
                    });
                }

                beatmapSets.Add(set);
            }

            var results = await runSorting(SortMode.Artist, beatmapSets);

            Assert.That(() =>
            {
                var lastItem = results.Last();
                return lastItem.Metadata.Artist == "ZZZ" && lastItem.Metadata.Title == "ZZZ";
            });

            Assert.That(() =>
            {
                var secondLastItem = results.SkipLast(diff_count).Last();
                return secondLastItem.Metadata.Artist == "ZZZ" && secondLastItem.Metadata.Title == "AAA";
            });
        }

        /// <summary>
        /// Ensures stability is maintained on different sort modes for items with equal properties.
        /// </summary>
        [Test]
        public async Task TestSortingStabilityDateAdded()
        {
            List<BeatmapSetInfo> beatmapSets = new List<BeatmapSetInfo>();

            for (int i = 0; i < 10; i++)
            {
                var set = TestResources.CreateTestBeatmapSetInfo();

                set.DateAdded = DateTimeOffset.FromUnixTimeSeconds(i);

                // only need to set the first as they are a shared reference.
                var beatmap = set.Beatmaps.First();

                beatmap.Metadata.Artist = "a";
                beatmap.Metadata.Title = "b";

                beatmapSets.Add(set);
            }

            var results = await runSorting(SortMode.Title, beatmapSets);

            Assert.That(results.Select(b => b.BeatmapSet!.DateAdded), Is.Ordered.Descending);

            results = await runSorting(SortMode.Artist, beatmapSets);

            Assert.That(results.Select(b => b.BeatmapSet!.DateAdded), Is.Ordered.Descending);
        }

        [Test]
        public async Task TestSortingByRecalculatedDifficulty()
        {
            var setA = TestResources.CreateTestBeatmapSetInfo(1);
            var setB = TestResources.CreateTestBeatmapSetInfo(1);

            var beatmapA = setA.Beatmaps.First();
            var beatmapB = setB.Beatmaps.First();

            // Static star ratings intentionally conflict with recalculated stars to validate the new mode's behaviour.
            beatmapA.StarRating = 8;
            beatmapB.StarRating = 2;

            var difficultyCache = new TestBeatmapDifficultyCache(new Dictionary<BeatmapInfo, double>
            {
                [beatmapA] = 1,
                [beatmapB] = 9,
            });

            var sorter = new BeatmapCarouselFilterSorting(() => new FilterCriteria { Sort = SortMode.RecalculatedDifficulty }, () => difficultyCache);
            var carouselItems = await sorter.Run(new[] { new CarouselItem(beatmapA), new CarouselItem(beatmapB) }, CancellationToken.None);
            var results = carouselItems.Select(ci => ci.Model).OfType<BeatmapInfo>().ToList();

            Assert.That(results.First(), Is.EqualTo(beatmapA));
            Assert.That(results.Last(), Is.EqualTo(beatmapB));
        }

        [Test]
        public async Task TestStaleLookupCompletionDoesNotTriggerResort()
        {
            var beatmap = TestResources.CreateTestBeatmapSetInfo(1).Beatmaps.Single();

            var oldMods = new Mod[] { new OsuModDoubleTime() };
            var newMods = new Mod[] { new OsuModHalfTime() };

            var criteria = new FilterCriteria
            {
                Sort = SortMode.RecalculatedDifficulty,
                Ruleset = beatmap.Ruleset,
                Mods = oldMods,
            };

            int resortRequests = 0;

            var cache = new DelayedTestBeatmapDifficultyCache();
            var sorter = new BeatmapCarouselFilterSorting(() => criteria, () => cache, () => Interlocked.Increment(ref resortRequests));

            await sorter.Run(new[] { new CarouselItem(beatmap) }, CancellationToken.None);

            criteria = new FilterCriteria
            {
                Sort = SortMode.RecalculatedDifficulty,
                Ruleset = beatmap.Ruleset,
                Mods = newMods,
            };

            await sorter.Run(new[] { new CarouselItem(beatmap) }, CancellationToken.None);

            cache.CompleteFor(beatmap, oldMods, 6);
            await Task.Delay(100);

            Assert.That(resortRequests, Is.EqualTo(0));

            cache.CompleteFor(beatmap, newMods, 4);
            await Task.Delay(100);

            Assert.That(resortRequests, Is.EqualTo(1));
        }

        private static async Task<IEnumerable<BeatmapInfo>> runSorting(SortMode sort, List<BeatmapSetInfo> beatmapSets)
        {
            var sorter = new BeatmapCarouselFilterSorting(() => new FilterCriteria { Sort = sort });

            var carouselItems = await sorter.Run(beatmapSets.SelectMany(s => s.Beatmaps.Select(b => new CarouselItem(b))), CancellationToken.None);
            return carouselItems.Select(ci => ci.Model).OfType<BeatmapInfo>();
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

        private partial class DelayedTestBeatmapDifficultyCache : BeatmapDifficultyCache
        {
            private readonly List<(DifficultyCacheLookup lookup, TaskCompletionSource<StarDifficulty?> task)> lookups = new List<(DifficultyCacheLookup lookup, TaskCompletionSource<StarDifficulty?> task)>();

            public override Task<StarDifficulty?> GetDifficultyAsync(IBeatmapInfo beatmapInfo, IRulesetInfo? rulesetInfo = null, IEnumerable<Mod>? mods = null,
                                                                      CancellationToken cancellationToken = default, int computationDelay = 0)
            {
                var lookup = new DifficultyCacheLookup((BeatmapInfo)beatmapInfo, (RulesetInfo?)rulesetInfo, mods);
                var task = new TaskCompletionSource<StarDifficulty?>(TaskCreationOptions.RunContinuationsAsynchronously);
                lookups.Add((lookup, task));
                return task.Task;
            }

            public void CompleteFor(BeatmapInfo beatmap, IReadOnlyList<Mod> mods, double stars)
            {
                var matching = lookups.Single(entry => entry.lookup.BeatmapInfo.Equals(beatmap) && entry.lookup.OrderedMods.SequenceEqual(mods.OrderBy(m => m.Acronym)));
                matching.task.SetResult(new StarDifficulty(stars, 0));
            }
        }
    }
}
