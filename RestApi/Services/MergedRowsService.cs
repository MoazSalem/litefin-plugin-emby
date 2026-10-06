// <copyright file="MergedRowsService.cs" company="Litefin">
// Copyright (c) Litefin. All rights reserved.
// </copyright>

namespace Litefin.Emby.Plugin.RestApi.Services
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using Litefin.Emby.Plugin.Common;
    using Litefin.Emby.Plugin.RestApi.Requests;
    using MediaBrowser.Controller.Api;
    using MediaBrowser.Controller.Dto;
    using MediaBrowser.Controller.Entities;
    using MediaBrowser.Controller.Entities.TV;
    using MediaBrowser.Controller.Library;
    using MediaBrowser.Controller.TV;
    using MediaBrowser.Model.Dto;
    using MediaBrowser.Model.Entities;
    using MediaBrowser.Model.Querying;

    /// <summary>
    /// REST API service providing an aggregated, deduplicated list of Continue Watching and Next Up rows.
    /// Interweaves in-progress media and chronological television episodes into a single responsive carousel.
    /// </summary>
    public class MergedRowsService : BaseApiService
    {
        private readonly ITVSeriesManager tvSeriesManager;
        private readonly IDtoService dtoService;
        private readonly IUserDataManager userDataManager;

        /// <summary>
        /// Initializes a new instance of the <see cref="MergedRowsService"/> class.
        /// </summary>
        /// <param name="tvSeriesManager">The TV series manager.</param>
        /// <param name="dtoService">The DTO mapping service.</param>
        /// <param name="userDataManager">The user data manager for playback timestamps.</param>
        public MergedRowsService(
            ITVSeriesManager tvSeriesManager,
            IDtoService dtoService,
            IUserDataManager userDataManager)
        {
            this.tvSeriesManager = tvSeriesManager;
            this.dtoService = dtoService;
            this.userDataManager = userDataManager;
        }

        /// <summary>
        /// Handles GET /Litefin/MergedRows/ContinueAndNextUp requests.
        /// </summary>
        /// <param name="request">The merged rows request parameters.</param>
        /// <returns>A QueryResult of BaseItemDto items sorted chronologically by user activity.</returns>
        public object Get(GetContinueAndNextUpRequest request)
        {
            // Resolve target user from request or authentication claims
            var user = this.GetTargetUser(request.UserId);
            var rowLimit = request.Limit.GetValueOrDefault(12);

            this.Logger.Info("Processing GetContinueAndNextUp for user: {0}, limit: {1}", user.Name, rowLimit);

            // =========================================================================
            // Dynamic DTO Options Configuration
            // =========================================================================
            // Parse optional ItemFields requested by the caller.
            // In Emby, DtoService requires ItemFields.Id to populate dto.Id when
            // allFields is set to false. Without this explicitly requested, dto.Id
            // defaults to string "0", breaking all downstream client routes and image lookups.
            var fieldsList = new List<ItemFields>();
            if (!string.IsNullOrWhiteSpace(request.Fields))
            {
                var parsed = request.Fields!
                    .Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(s =>
                    {
                        var trimmed = s.Trim();
                        return Enum.TryParse<ItemFields>(trimmed, true, out var f) ? (ItemFields?)f : null;
                    })
                    .Where(f => f.HasValue)
                    .Select(f => f!.Value);
                fieldsList.AddRange(parsed);
            }

            // Guarantee that primary identity and aspect ratio fields are always present
            if (!fieldsList.Contains(ItemFields.Id))
            {
                fieldsList.Add(ItemFields.Id);
            }

            if (!fieldsList.Contains(ItemFields.PrimaryImageAspectRatio))
            {
                fieldsList.Add(ItemFields.PrimaryImageAspectRatio);
            }

            // Establish DTO options with tailored fields for minimal transport overhead
            var dtoOptions = new DtoOptions(allFields: false)
            {
                Fields = fieldsList.ToArray(),
                EnableImages = true,
                EnableUserData = true,
                ImageTypeLimit = 1,
            };

            // =========================================================================
            // Query Resumable Items (Continue Watching)
            // =========================================================================
            // Note: Never attach partial DtoOptions to the database InternalItemsQuery.
            // In Emby's SqliteItemRepository, passing DtoOptions forces selective column
            // projection where InternalId is not extracted, causing entity.InternalId to remain 0.
            var resumeItemsResult = this.LibraryManager.GetItemsResult(new InternalItemsQuery(user)
            {
                OrderBy = new[] { ("DatePlayed", SortOrder.Descending) },
                IsResumable = true,
                StartIndex = 0,
                Limit = rowLimit,
                Recursive = true,
                IsVirtualItem = false,
                IsFolder = false,
                IncludeItemTypes = new[] { "Movie", "Episode", "Video" },
            });

            // Query Next Up episodes using the TVSeriesManager
            var nextUpResult = this.tvSeriesManager.GetNextUp(
                new NextUpQuery
                {
                    Limit = rowLimit,
                },
                user,
                dtoOptions);

            // Collect items with activity timestamp to allow unified chronological sorting
            var itemsWithActivity = new List<(BaseItem Item, DateTimeOffset ActivityDate)>();

            // Process Resume items
            if (resumeItemsResult.Items != null)
            {
                foreach (var item in resumeItemsResult.Items)
                {
                    if (item == null || item.IsDisplayedAsFolder || item is Folder || item is Season || item is Series)
                    {
                        continue;
                    }

                    var userData = this.userDataManager.GetUserData(user, item);
                    var activityDate = userData?.LastPlayedDate ?? item.DateCreated;
                    itemsWithActivity.Add((item, activityDate));
                }
            }

            // Process Next Up items and interweave them
            if (nextUpResult.Items != null)
            {
                foreach (var item in nextUpResult.Items)
                {
                    if (item == null || item.IsDisplayedAsFolder || item is Folder || item is Season || item is Series)
                    {
                        continue;
                    }

                    // Avoid duplicate entries if already present in resume list
                    if (itemsWithActivity.Any(x => x.Item.Id == item.Id))
                    {
                        continue;
                    }

                    DateTimeOffset? activityDate = null;

                    // For episodes, determine when the parent series was last active
                    if (item is Episode episode)
                    {
                        var seriesId = episode.FindSeriesId();
                        if (seriesId != 0)
                        {
                            var lastPlayedEpisodes = this.LibraryManager.GetItemList(new InternalItemsQuery(user)
                            {
                                AncestorIds = new[] { seriesId },
                                IncludeItemTypes = new[] { "Episode" },
                                OrderBy = new[] { ("DatePlayed", SortOrder.Descending) },
                                Limit = 1,
                                Recursive = true,
                            });

                            var lastPlayedEpisode = lastPlayedEpisodes?.FirstOrDefault();
                            if (lastPlayedEpisode != null)
                            {
                                var lastPlayedUserData = this.userDataManager.GetUserData(user, lastPlayedEpisode);
                                activityDate = lastPlayedUserData?.LastPlayedDate;
                            }
                        }
                    }

                    // Fallback to item user data or creation date
                    if (!activityDate.HasValue)
                    {
                        var userData = this.userDataManager.GetUserData(user, item);
                        activityDate = userData?.LastPlayedDate ?? item.DateCreated;
                    }

                    // Filter out any next-up item whose series activity is older than the configured cutoff limit
                    if (request.NextUpDateCutoff.HasValue && activityDate.HasValue && activityDate.Value < request.NextUpDateCutoff.Value)
                    {
                        continue;
                    }

                    itemsWithActivity.Add((item, activityDate.Value));
                }
            }

            // Deduplicate items belonging to the same series, keeping the most recently active one
            var deduplicated = new List<(BaseItem Item, DateTimeOffset ActivityDate)>();
            var seenSeriesIds = new HashSet<long>();

            var sortedItems = itemsWithActivity.OrderByDescending(x => x.ActivityDate).ToList();

            foreach (var entry in sortedItems)
            {
                if (entry.Item is Episode ep)
                {
                    var seriesId = ep.FindSeriesId();
                    if (seriesId != 0)
                    {
                        if (seenSeriesIds.Contains(seriesId))
                        {
                            continue;
                        }

                        seenSeriesIds.Add(seriesId);
                    }
                }

                deduplicated.Add(entry);
            }

            // Take requested limit
            var finalItems = deduplicated
                .Take(rowLimit)
                .Select(x => x.Item)
                .ToArray();

            if (finalItems.Length == 0)
            {
                return new QueryResult<BaseItemDto>
                {
                    Items = Array.Empty<BaseItemDto>(),
                    TotalRecordCount = 0,
                };
            }

            // =========================================================================
            // Map Entities to DTOs & Validate Client Identifiers
            // =========================================================================
            var dtos = this.dtoService.GetBaseItemDtos(finalItems, dtoOptions, user);

            // Double check that every mapped DTO contains a valid non-zero ID
            for (int i = 0; i < dtos.Length; i++)
            {
                var dto = dtos[i];
                var entity = finalItems[i];

                // Recover entity identity if DTO serializer left ID blank or at default zero
                if (string.IsNullOrEmpty(dto.Id) || dto.Id == "0")
                {
                    if (entity.InternalId > 0)
                    {
                        dto.Id = entity.InternalId.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    }
                    else if (entity.Id != Guid.Empty)
                    {
                        dto.Id = entity.Id.ToString("N");
                    }
                }
            }

            return new QueryResult<BaseItemDto>
            {
                Items = dtos,
                TotalRecordCount = dtos.Length,
            };
        }
    }
}
