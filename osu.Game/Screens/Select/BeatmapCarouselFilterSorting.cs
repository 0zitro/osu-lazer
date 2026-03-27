// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using osu.Framework.Extensions;
using osu.Game.Beatmaps;
using osu.Game.Graphics.Carousel;
using osu.Game.Rulesets;
using osu.Game.Rulesets.Mods;
using osu.Game.Screens.Select.Filter;
using osu.Game.Utils;

namespace osu.Game.Screens.Select
{
    public class BeatmapCarouselFilterSorting : ICarouselFilter
    {
        public int BeatmapItemsCount { get; private set; }

        private readonly Func<FilterCriteria> getCriteria;
        private readonly Func<BeatmapDifficultyCache>? getDifficultyCache;
        private readonly Action? requestResort;

        /// <summary>
        /// In-flight lookups are tracked here to avoid duplicate expensive calculations.
        /// Completed lookups are removed and resolved via <see cref="BeatmapDifficultyCache"/> caching.
        /// </summary>
        private readonly ConcurrentDictionary<BeatmapDifficultyCache.DifficultyCacheLookup, Task<StarDifficulty?>> inFlightDifficultyLookups = new ConcurrentDictionary<BeatmapDifficultyCache.DifficultyCacheLookup, Task<StarDifficulty?>>();

        private readonly object difficultyComputationStateLock = new object();
        private CancellationTokenSource? difficultyComputationCancellationSource;
        private DifficultyComputationCriteriaSnapshot? difficultyComputationCriteria;

        public BeatmapCarouselFilterSorting(Func<FilterCriteria> getCriteria, Func<BeatmapDifficultyCache>? getDifficultyCache = null, Action? requestResort = null)
        {
            this.getCriteria = getCriteria;
            this.getDifficultyCache = getDifficultyCache;
            this.requestResort = requestResort;
        }

        public async Task<List<CarouselItem>> Run(IEnumerable<CarouselItem> items, CancellationToken cancellationToken) => await Task.Run(() =>
        {
            var criteria = getCriteria();

            bool groupedSets = BeatmapCarouselFilterGrouping.ShouldGroupBeatmapsTogether(criteria);

            IReadOnlyDictionary<BeatmapInfo, double>? recalculatedStars = null;

            if (criteria.Sort == SortMode.RecalculatedDifficulty)
                recalculatedStars = createRecalculatedStarsMap(items, criteria, cancellationToken, getDifficultyComputationCancellationToken(criteria));
            else
                clearDifficultyComputationToken();

            double getStarRatingForSort(BeatmapInfo beatmap)
                => recalculatedStars?.GetValueOrDefault(beatmap) ?? beatmap.StarRating;

            int compare(BeatmapInfo a, BeatmapInfo b, bool aggregate)
            {
                int comparison;

                switch (criteria.Sort)
                {
                    case SortMode.Artist:
                        comparison = OrdinalSortByCaseStringComparer.DEFAULT.Compare(a.BeatmapSet!.Metadata.Artist, b.BeatmapSet!.Metadata.Artist);
                        if (comparison == 0)
                            goto case SortMode.Title;
                        break;

                    case SortMode.Title:
                        comparison = OrdinalSortByCaseStringComparer.DEFAULT.Compare(a.BeatmapSet!.Metadata.Title, b.BeatmapSet!.Metadata.Title);
                        break;

                    case SortMode.Author:
                        comparison = OrdinalSortByCaseStringComparer.DEFAULT.Compare(a.BeatmapSet!.Metadata.Author.Username, b.BeatmapSet!.Metadata.Author.Username);
                        break;

                    case SortMode.Source:
                        comparison = OrdinalSortByCaseStringComparer.DEFAULT.Compare(a.BeatmapSet!.Metadata.Source, b.BeatmapSet!.Metadata.Source);
                        break;

                    case SortMode.Difficulty:
                        comparison = a.StarRating.CompareTo(b.StarRating);
                        break;

                    case SortMode.RecalculatedDifficulty:
                        if (aggregate)
                            comparison = compareUsingAggregateMax(a, b, getStarRatingForSort);
                        else
                            comparison = getStarRatingForSort(a).CompareTo(getStarRatingForSort(b));
                        break;

                    case SortMode.DateAdded:
                        comparison = b.BeatmapSet!.DateAdded.CompareTo(a.BeatmapSet!.DateAdded);
                        break;

                    case SortMode.DateRanked:
                        comparison = Nullable.Compare(b.BeatmapSet!.DateRanked, a.BeatmapSet!.DateRanked);
                        break;

                    case SortMode.DateSubmitted:
                        comparison = Nullable.Compare(b.BeatmapSet!.DateSubmitted, a.BeatmapSet!.DateSubmitted);
                        break;

                    case SortMode.LastPlayed:
                        if (aggregate)
                            comparison = compareUsingAggregateMax(b, a, static b => (b.LastPlayed ?? DateTimeOffset.MinValue).ToUnixTimeSeconds());
                        else
                            comparison = Nullable.Compare(b.LastPlayed, a.LastPlayed);
                        break;

                    case SortMode.BPM:
                        if (aggregate)
                            comparison = compareUsingAggregateMax(a, b, static b => b.BPM);
                        else
                            comparison = a.BPM.CompareTo(b.BPM);
                        break;

                    case SortMode.Length:
                        if (aggregate)
                            comparison = compareUsingAggregateMax(a, b, static b => b.Length);
                        else
                            comparison = a.Length.CompareTo(b.Length);
                        break;

                    default:
                        throw new ArgumentOutOfRangeException();
                }

                // If the initial sort could not differentiate, attempt to use DateAdded to order sets in a stable fashion.
                // The directionality of this matches the current SortMode.DateAdded, but we may want to reconsider if that becomes a user decision (ie. asc / desc).
                if (comparison == 0)
                    comparison = b.BeatmapSet!.DateAdded.CompareTo(a.BeatmapSet!.DateAdded);

                // If DateAdded fails to break the tie, fallback to our internal GUID for stability.
                // This basically means it's a stable random sort.
                if (comparison == 0)
                    comparison = b.BeatmapSet!.ID.CompareTo(a.BeatmapSet!.ID);

                return comparison;
            }

            int compareDifficulty(BeatmapInfo a, BeatmapInfo b)
            {
                int comparison = a.Ruleset.CompareTo(b.Ruleset);

                if (comparison == 0)
                {
                    if (criteria.Sort == SortMode.RecalculatedDifficulty)
                        comparison = getStarRatingForSort(a).CompareTo(getStarRatingForSort(b));
                    else
                        comparison = a.StarRating.CompareTo(b.StarRating);
                }

                return comparison;
            }

            BeatmapItemsCount = items.Count();

            return items.Order(Comparer<CarouselItem>.Create((a, b) =>
            {
                var ab = (BeatmapInfo)a.Model;
                var bb = (BeatmapInfo)b.Model;

                if (groupedSets)
                {
                    if (ab.BeatmapSet!.Equals(bb.BeatmapSet))
                        return compareDifficulty(ab, bb);

                    // If we're grouping by sets, all fallback sorts need to be aggregates for the set.
                    return compare(ab, bb, aggregate: true);
                }

                return compare(ab, bb, aggregate: false);
            })).ToList();
        }, cancellationToken).ConfigureAwait(false);

        private static int compareUsingAggregateMax(BeatmapInfo a, BeatmapInfo b, Func<BeatmapInfo, double> func)
        {
            var aMatchedBeatmaps = a.BeatmapSet!.Beatmaps.Where(bb => !bb.Hidden);
            var bMatchedBeatmaps = b.BeatmapSet!.Beatmaps.Where(bb => !bb.Hidden);

            bool aAny = aMatchedBeatmaps.Any();
            bool bAny = bMatchedBeatmaps.Any();

            if (!aAny && !bAny) return 0;
            if (!aAny) return -1;
            if (!bAny) return 1;

            return aMatchedBeatmaps.Max(func).CompareTo(bMatchedBeatmaps.Max(func));
        }
        private IReadOnlyDictionary<BeatmapInfo, double> createRecalculatedStarsMap(IEnumerable<CarouselItem> items, FilterCriteria criteria, CancellationToken runCancellationToken, CancellationToken difficultyComputationCancellationToken)
        {
            var starsByBeatmap = new Dictionary<BeatmapInfo, double>();

            foreach (BeatmapInfo beatmap in items.Select(i => (BeatmapInfo)i.Model).Distinct())
            {
                runCancellationToken.ThrowIfCancellationRequested();
                starsByBeatmap[beatmap] = getOrQueueRecalculatedStarRating(beatmap, criteria, difficultyComputationCancellationToken);
            }

            return starsByBeatmap;
        }

        private double getOrQueueRecalculatedStarRating(BeatmapInfo beatmap, FilterCriteria criteria, CancellationToken difficultyComputationCancellationToken)
        {
            if (getDifficultyCache == null)
                return beatmap.StarRating;

            var lookup = new BeatmapDifficultyCache.DifficultyCacheLookup(beatmap, criteria.Ruleset, criteria.Mods);

            Task<StarDifficulty?> task = inFlightDifficultyLookups.GetOrAdd(lookup, l =>
            {
                Task<StarDifficulty?> lookupTask = getDifficultyCache().GetDifficultyAsync(l.BeatmapInfo, l.Ruleset, l.OrderedMods, difficultyComputationCancellationToken);

                if (!lookupTask.IsCompleted)
                {
                    _ = lookupTask.ContinueWith(t =>
                    {
                        inFlightDifficultyLookups.TryRemove(l, out _);

                        if (t.IsCompletedSuccessfully && t.GetResultSafely() != null && matchesCurrentCriteria(l))
                            requestResort?.Invoke();
                    }, TaskScheduler.Default);
                }

                return lookupTask;
            });

            if (!task.IsCompleted)
                return beatmap.StarRating;

            // `inFlightDifficultyLookups` should only hold incomplete tasks for de-duplication.
            // Completed tasks are already handled by `BeatmapDifficultyCache` and should not be retained here.
            inFlightDifficultyLookups.TryRemove(lookup, out _);

            return task.GetResultSafely()?.Stars ?? beatmap.StarRating;
        }

        private CancellationToken getDifficultyComputationCancellationToken(FilterCriteria criteria)
        {
            lock (difficultyComputationStateLock)
            {
                if (difficultyComputationCriteria == null || !criteriaMatches(difficultyComputationCriteria.Value, criteria))
                {
                    difficultyComputationCancellationSource?.Cancel();
                    difficultyComputationCancellationSource?.Dispose();

                    difficultyComputationCriteria = new DifficultyComputationCriteriaSnapshot(criteria);
                    difficultyComputationCancellationSource = new CancellationTokenSource();
                }

                difficultyComputationCancellationSource ??= new CancellationTokenSource();

                return difficultyComputationCancellationSource.Token;
            }
        }

        private void clearDifficultyComputationToken()
        {
            lock (difficultyComputationStateLock)
            {
                difficultyComputationCancellationSource?.Cancel();
                difficultyComputationCancellationSource?.Dispose();
                difficultyComputationCancellationSource = null;
                difficultyComputationCriteria = null;
            }
        }

        private bool matchesCurrentCriteria(in BeatmapDifficultyCache.DifficultyCacheLookup lookup)
        {
            FilterCriteria criteria = getCriteria();

            if (criteria.Sort != SortMode.RecalculatedDifficulty)
                return false;

            RulesetInfo? criteriaRuleset = criteria.Ruleset ?? lookup.BeatmapInfo.Ruleset;

            if (criteriaRuleset == null || !criteriaRuleset.Equals(lookup.Ruleset))
                return false;

            return modsEqual(criteria.Mods, lookup.OrderedMods);
        }

        private static bool modsEqual(IReadOnlyList<Mod>? criteriaMods, IReadOnlyList<Mod> lookupMods)
        {
            if (criteriaMods == null || criteriaMods.Count == 0)
                return lookupMods.Count == 0;

            if (criteriaMods.Count != lookupMods.Count)
                return false;

            return lookupMods.SequenceEqual(criteriaMods.OrderBy(m => m.Acronym));
        }

        private static bool criteriaMatches(in DifficultyComputationCriteriaSnapshot snapshot, FilterCriteria criteria)
        {
            if (!EqualityComparer<RulesetInfo?>.Default.Equals(snapshot.Ruleset, criteria.Ruleset))
                return false;

            return modsEqual(criteria.Mods, snapshot.OrderedMods);
        }

        private readonly struct DifficultyComputationCriteriaSnapshot
        {
            public readonly RulesetInfo? Ruleset;
            public readonly Mod[] OrderedMods;

            public DifficultyComputationCriteriaSnapshot(FilterCriteria criteria)
            {
                Ruleset = criteria.Ruleset;
                OrderedMods = criteria.Mods?.OrderBy(m => m.Acronym).Select(mod => mod.DeepClone()).ToArray() ?? Array.Empty<Mod>();
            }
        }
    }
}
