// <copyright file="CollectionsService.cs" company="Litefin">
// Copyright (c) Litefin. All rights reserved.
// </copyright>

namespace Litefin.Emby.Plugin.RestApi.Services
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Net;
    using Litefin.Emby.Plugin.Common;
    using Litefin.Emby.Plugin.RestApi.Requests;
    using MediaBrowser.Controller.Api;
    using MediaBrowser.Controller.Dto;
    using MediaBrowser.Controller.Entities;
    using MediaBrowser.Model.Dto;
    using MediaBrowser.Model.Net;
    using MediaBrowser.Model.Querying;

    /// <summary>
    /// REST API service providing reverse collection lookups for Emby Server.
    /// Finds all BoxSets containing a given movie or series for contextual navigation.
    /// </summary>
    public class CollectionsService : BaseApiService
    {
        private readonly IDtoService dtoService;

        /// <summary>
        /// Initializes a new instance of the <see cref="CollectionsService"/> class.
        /// </summary>
        /// <param name="dtoService">The server DTO mapping service.</param>
        public CollectionsService(IDtoService dtoService)
        {
            this.dtoService = dtoService;
        }

        /// <summary>
        /// Handles GET /Litefin/Items/{ItemId}/Collections requests.
        /// </summary>
        /// <param name="request">The item collection query request parameters.</param>
        /// <returns>A QueryResult of BaseItemDto BoxSets that contain the requested item.</returns>
        public object Get(GetItemCollectionsRequest request)
        {
            // Resolve target user
            var user = this.GetTargetUser(request.UserId);

            // Lookup the target media item
            var item = this.LibraryManager.GetItemById(request.ItemId);
            if (item == null)
            {
                throw new HttpException("Item not found.")
                {
                    StatusCode = HttpStatusCode.NotFound,
                };
            }

            this.Logger.Info("Querying collections containing item: '{0}' ({1})", item.Name, item.Id);

            // Parse optional fields
            ItemFields[]? parsedFields = null;
            if (!string.IsNullOrWhiteSpace(request.Fields))
            {
                parsedFields = request.Fields!
                    .Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(s =>
                    {
                        var trimmed = s.Trim();
                        return Enum.TryParse<ItemFields>(trimmed, true, out var f) ? (ItemFields?)f : null;
                    })
                    .Where(f => f.HasValue)
                    .Select(f => f!.Value)
                    .ToArray();
            }

            var dtoOptions = new DtoOptions(allFields: false)
            {
                Fields = parsedFields is { Length: > 0 } ? parsedFields : Array.Empty<ItemFields>(),
                EnableImages = true,
                EnableUserData = true,
            };

            // Query all BoxSet items visible to the user
            var visibleCollections = this.LibraryManager.GetItemList(new InternalItemsQuery(user)
            {
                IncludeItemTypes = new[] { "BoxSet" },
                Recursive = true,
            })
            .OfType<BoxSet>()
            .Where(boxSet =>
            {
                // Verify if this collection folder contains the item's internal ID
                var childrenIds = boxSet.GetChildrenIds(new InternalItemsQuery(user));
                return childrenIds != null && childrenIds.Contains(item.InternalId);
            })
            .OrderBy(i => i.SortName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

            // Paginate results if startIndex or limit are requested
            IEnumerable<BaseItem> pagedCollections = visibleCollections;
            if (request.StartIndex.HasValue)
            {
                pagedCollections = pagedCollections.Skip(request.StartIndex.Value);
            }

            if (request.Limit.HasValue)
            {
                pagedCollections = pagedCollections.Take(request.Limit.Value);
            }

            var finalArray = pagedCollections.ToArray();
            var dtos = this.dtoService.GetBaseItemDtos(finalArray, dtoOptions, user);

            return new QueryResult<BaseItemDto>
            {
                Items = dtos,
                TotalRecordCount = visibleCollections.Count,
            };
        }
    }
}
