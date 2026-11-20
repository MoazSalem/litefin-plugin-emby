// <copyright file="AnimeMarkersService.cs" company="Litefin">
// Copyright (c) Litefin. All rights reserved.
// </copyright>

namespace Litefin.Emby.Plugin.RestApi.Services
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.Linq;
    using System.Net;
    using System.Threading.Tasks;
    using Litefin.Emby.Plugin.Common;
    using Litefin.Emby.Plugin.Models;
    using Litefin.Emby.Plugin.RestApi.Requests;
    using Litefin.Emby.Plugin.Services;
    using MediaBrowser.Controller.Api;
    using MediaBrowser.Controller.Entities;
    using MediaBrowser.Controller.Entities.TV;
    using MediaBrowser.Controller.Library;
    using MediaBrowser.Model.Entities;
    using MediaBrowser.Model.Net;
    using MediaBrowser.Model.Querying;

    /// <summary>
    /// REST API service that exposes anime episode classification markers (Filler, Mixed, Manga/Anime Canon)
    /// and audio stream verdicts (Subbed, Dubbed) for television series in Emby.
    /// </summary>
    public class AnimeMarkersService : BaseApiService
    {
        private readonly IMediaSourceManager mediaSourceManager;
        private readonly AnimeFillerListService fillerListService;

        /// <summary>
        /// Initializes a new instance of the <see cref="AnimeMarkersService"/> class.
        /// </summary>
        /// <param name="mediaSourceManager">Instance of the <see cref="IMediaSourceManager"/> interface.</param>
        public AnimeMarkersService(IMediaSourceManager mediaSourceManager)
        {
            this.mediaSourceManager = mediaSourceManager;

            // Instantiate the background filler list resolution and caching service
            this.fillerListService = new AnimeFillerListService(this.Logger);
        }

        /// <summary>
        /// Processes GET /Litefin/AnimeMarkers/Series to query markers for an anime series.
        /// </summary>
        /// <param name="request">The anime markers query request containing the series identifier.</param>
        /// <returns>An <see cref="AnimeMarkersResponse"/> object populated with episode and season verdicts.</returns>
        public async Task<object> Get(GetAnimeMarkersRequest request)
        {
            // Validate incoming parameter
            if (request == null || string.IsNullOrWhiteSpace(request.SeriesId))
            {
                throw new HttpException("A valid series identifier is required.")
                {
                    StatusCode = HttpStatusCode.BadRequest,
                };
            }

            var seriesIdStr = request.SeriesId!.Trim();
            BaseItem? targetItem = null;

            // Attempt resolution via 32-character GUID string
            if (Guid.TryParse(seriesIdStr, out var seriesGuid) && seriesGuid != Guid.Empty)
            {
                targetItem = this.LibraryManager.GetItemById(seriesGuid);
            }
            // Fall back to resolution via Emby numeric internal ID
            else if (long.TryParse(seriesIdStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seriesInternalId) && seriesInternalId > 0)
            {
                targetItem = this.LibraryManager.GetItemById(seriesInternalId);
            }

            // Verify that the requested item exists and represents a TV series
            if (targetItem is not Series series)
            {
                this.Logger.Warn("Requested item '{0}' is not a television series", seriesIdStr);
                throw new HttpException($"Series with id '{seriesIdStr}' was not found.")
                {
                    StatusCode = HttpStatusCode.NotFound,
                };
            }

            // -----------------------------------------------------------------
            // Anime Candidate Verification
            // -----------------------------------------------------------------
            // Inspect provider IDs, tags, genres, and directory paths.
            // If the series exhibits no anime traits, return early with matched = false.
            var isAnime = AnimeTitleMatcher.IsAnimeCandidate(series);
            if (!isAnime)
            {
                return new AnimeMarkersResponse
                {
                    Enabled = true,
                    Matched = false,
                    Slug = null,
                };
            }

            // Resolve canonical AnimeFillerList slug from series name and aliases
            var resolvedSlug = AnimeTitleMatcher.ResolveSlug(series.Name);

            // Retrieve scraped or cached marker definitions if a slug was resolved
            CachedShowData? showData = null;
            if (!string.IsNullOrEmpty(resolvedSlug))
            {
                showData = await this.fillerListService.GetShowDataAsync(resolvedSlug!, this.Request.CancellationToken).ConfigureAwait(false);
            }

            // -----------------------------------------------------------------
            // Episode Query
            // -----------------------------------------------------------------
            // Query all episodes belonging to this series across all seasons.
            var episodeQuery = new InternalItemsQuery
            {
                AncestorIds = new[] { series.InternalId },
                IncludeItemTypes = new[] { "Episode" },
                Recursive = true,
                OrderBy = new[] { (ItemSortBy.SortName, SortOrder.Ascending) },
            };

            var rawItems = this.LibraryManager.GetItemList(episodeQuery);
            var episodes = rawItems?.OfType<Episode>().ToList() ?? new List<Episode>();

            // -----------------------------------------------------------------
            // Stream Resolution & Canon Classification Mapping
            // -----------------------------------------------------------------
            // Pass explicit delegate to resolve media streams from the media source manager
            // so audio track languages (Subbed, Dubbed) are accurately detected.
            var (episodeVerdicts, seasonVerdicts) = AnimeEpisodeMapper.MapEpisodes(
                episodes,
                showData,
                ep =>
                {
                    try
                    {
                        if (this.mediaSourceManager != null)
                        {
                            return this.mediaSourceManager.GetMediaStreams(ep);
                        }

                        return ep.GetMediaStreams();
                    }
                    catch (Exception ex)
                    {
                        this.Logger.Debug("Failed to resolve streams for episode {0}: {1}", ep.Id, ex.Message);
                        return ep.GetMediaStreams();
                    }
                });

            // -----------------------------------------------------------------
            // Assemble Response Payload
            // -----------------------------------------------------------------
            var response = new AnimeMarkersResponse
            {
                Enabled = true,
                Matched = true,
                Slug = resolvedSlug,
            };

            foreach (var kvp in episodeVerdicts)
            {
                response.Episodes[kvp.Key] = kvp.Value;
            }

            foreach (var kvp in seasonVerdicts)
            {
                response.Seasons[kvp.Key] = kvp.Value;
            }

            return response;
        }
    }
}
