// <copyright file="HeroService.cs" company="Litefin">
// Copyright (c) Litefin. All rights reserved.
// </copyright>

namespace Litefin.Emby.Plugin.RestApi.Services
{
    using System;
    using System.Linq;
    using Litefin.Emby.Plugin.Common;
    using Litefin.Emby.Plugin.RestApi.Requests;
    using MediaBrowser.Controller.Api;
    using MediaBrowser.Controller.Dto;
    using MediaBrowser.Controller.Entities;
    using MediaBrowser.Model.Dto;
    using MediaBrowser.Model.Entities;
    using MediaBrowser.Model.Querying;

    /// <summary>
    /// REST API service providing optimized query results for the Litefin Home Hero Carousel on Emby Server.
    /// Curates movies and series with backdrops in a single request and strips unneeded heavy fields.
    /// </summary>
    public class HeroService : BaseApiService
    {
        private readonly IDtoService dtoService;

        /// <summary>
        /// Initializes a new instance of the <see cref="HeroService"/> class.
        /// </summary>
        /// <param name="dtoService">The server DTO mapping service.</param>
        public HeroService(IDtoService dtoService)
        {
            this.dtoService = dtoService;
        }

        /// <summary>
        /// Processes GET /Litefin/Hero requests.
        /// </summary>
        /// <param name="request">The hero items query request parameters.</param>
        /// <returns>A QueryResult of BaseItemDto items tailored for the hero carousel.</returns>
        public object Get(GetHeroItemsRequest request)
        {
            // Resolve target user from request or authentication claims
            var user = this.GetTargetUser(request.UserId);

            // Establish limit and watched-filtering flags
            var itemLimit = request.Limit.GetValueOrDefault(5);
            var filterWatched = request.IgnoreWatched.GetValueOrDefault(false);

            this.Logger.Info("Processing GetHeroItems for user: {0}, limit: {1}, ignoreWatched: {2}", user.Name, itemLimit, filterWatched);

            // Prepare DTO options requesting only essential fields to minimize payload overhead
            var dtoOptions = new DtoOptions(allFields: false)
            {
                Fields = new[]
                {
                    ItemFields.Overview,
                    ItemFields.PrimaryImageAspectRatio,
                    ItemFields.ProviderIds,
                },
                EnableImages = true,
                EnableUserData = true,
                ImageTypeLimit = 1,
            };

            // Fetch a candidate pool to filter backdrops and played status
            var query = new InternalItemsQuery(user)
            {
                IncludeItemTypes = new[] { "Movie", "Series" },
                IsVirtualItem = false,
                OrderBy = new[] { ("Random", SortOrder.Ascending) },
                Limit = itemLimit * 4,
                Recursive = true,
                DtoOptions = dtoOptions,
            };

            if (filterWatched)
            {
                query.IsPlayed = false;
            }

            // Execute the candidate library query
            var candidatesResult = this.LibraryManager.GetItemsResult(query);

            // Filter down to items that actually have a backdrop image
            var candidateItems = candidatesResult.Items
                .Where(item => item.HasImage(ImageType.Backdrop, 0))
                .ToList();

            // Additional verification for played status if requested
            if (filterWatched)
            {
                candidateItems = candidateItems
                    .Where(item => !item.IsPlayed(user))
                    .ToList();
            }

            // Shuffle candidates and pick requested limit
            var selectedItems = candidateItems
                .OrderBy(_ => Guid.NewGuid())
                .Take(itemLimit)
                .ToList();

            if (selectedItems.Count == 0)
            {
                return new QueryResult<BaseItemDto>
                {
                    Items = Array.Empty<BaseItemDto>(),
                    TotalRecordCount = 0,
                };
            }

            // Map server entities to lightweight DTOs
            var dtos = this.dtoService.GetBaseItemDtos(selectedItems.ToArray(), dtoOptions, user);

            // Strip redundant heavy fields for faster JSON transport over TV network stacks
            for (int i = 0; i < dtos.Length; i++)
            {
                var dto = dtos[i];
                dto.PremiereDate = null;
                dto.EndDate = null;
                dto.Status = null;
                dto.AirDays = null;
                dto.ChildCount = null;
                dto.Genres = null;
                dto.Taglines = null;
                dto.ExternalUrls = null;
                dto.People = null;
                dto.Studios = null;
            }

            return new QueryResult<BaseItemDto>
            {
                Items = dtos,
                TotalRecordCount = dtos.Length,
            };
        }
    }
}
