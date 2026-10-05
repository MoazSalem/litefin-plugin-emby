// <copyright file="ItemsService.cs" company="Litefin">
// Copyright (c) Litefin. All rights reserved.
// </copyright>

namespace Litefin.Emby.Plugin.RestApi.Services
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using Litefin.Emby.Plugin.Common;
    using Litefin.Emby.Plugin.Models;
    using Litefin.Emby.Plugin.RestApi.Requests;
    using MediaBrowser.Controller.Api;
    using MediaBrowser.Controller.Dto;
    using MediaBrowser.Controller.Entities;
    using MediaBrowser.Controller.Entities.Audio;
    using MediaBrowser.Controller.Entities.Movies;
    using MediaBrowser.Controller.Entities.TV;
    using MediaBrowser.Controller.Library;
    using MediaBrowser.Model.Dto;
    using MediaBrowser.Model.Entities;
    using MediaBrowser.Model.Querying;

    /// <summary>
    /// REST API service providing batch querying of items and library card thumbnails across multiple libraries.
    /// Eliminates multiple serial HTTP roundtrips from Litefin TV clients during home screen initialization.
    /// </summary>
    public class ItemsService : BaseApiService
    {
        private readonly IUserViewManager userViewManager;
        private readonly IDtoService dtoService;

        /// <summary>
        /// Initializes a new instance of the <see cref="ItemsService"/> class.
        /// </summary>
        /// <param name="userViewManager">The user view manager.</param>
        /// <param name="dtoService">The DTO mapping service.</param>
        public ItemsService(IUserViewManager userViewManager, IDtoService dtoService)
        {
            this.userViewManager = userViewManager;
            this.dtoService = dtoService;
        }

        /// <summary>
        /// Handles GET /Litefin/Items/Latest requests.
        /// </summary>
        /// <param name="request">The batch latest items request parameters.</param>
        /// <returns>A dictionary mapping each library parent ID to its list of latest items.</returns>
        public object Get(GetBatchLatestRequest request)
        {
            if (string.IsNullOrWhiteSpace(request.ParentIds))
            {
                return new Dictionary<Guid, IReadOnlyList<BaseItemDto>>();
            }

            // Resolve target user
            var user = this.GetTargetUser(request.UserId);

            // Parse incoming comma-separated parent GUIDs
            var parsedParentIds = request.ParentIds!
                .Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(s => Guid.TryParse(s.Trim(), out var g) ? g : Guid.Empty)
                .Where(g => g != Guid.Empty)
                .Distinct()
                .ToArray();

            if (parsedParentIds.Length == 0)
            {
                return new Dictionary<Guid, IReadOnlyList<BaseItemDto>>();
            }

            var itemLimit = request.Limit.GetValueOrDefault(12);

            // Parse optional ItemFields requested by the caller
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

            // =========================================================================
            // Dynamic DTO Serialization Options
            // =========================================================================
            // Parse optional caller fields and ensure ItemFields.Id and PrimaryImageAspectRatio
            // are always included. In Emby, DtoService requires ItemFields.Id to populate dto.Id
            // when allFields is false.
            var fieldsList = parsedFields != null ? new List<ItemFields>(parsedFields) : new List<ItemFields>();
            if (!fieldsList.Contains(ItemFields.Id))
            {
                fieldsList.Add(ItemFields.Id);
            }
            if (!fieldsList.Contains(ItemFields.PrimaryImageAspectRatio))
            {
                fieldsList.Add(ItemFields.PrimaryImageAspectRatio);
            }

            var dtoOptions = new DtoOptions(allFields: false)
            {
                Fields = fieldsList.ToArray(),
                EnableImages = true,
                EnableUserData = true,
                ImageTypeLimit = 1,
            };

            var result = new Dictionary<Guid, IReadOnlyList<BaseItemDto>>();

            // Iterate over each parent library ID to collect latest items
            foreach (var parentId in parsedParentIds)
            {
                var parentItem = this.LibraryManager.GetItemById(parentId);
                if (parentItem == null)
                {
                    result[parentId] = Array.Empty<BaseItemDto>();
                    continue;
                }

                // Determine target types based on folder collection type
                var collectionFolder = parentItem as ICollectionFolder;
                var collectionType = collectionFolder?.CollectionType;
                string[] includeItemTypes = Array.Empty<string>();

                if (string.Equals(collectionType, "movies", StringComparison.OrdinalIgnoreCase))
                {
                    includeItemTypes = new[] { "Movie" };
                }
                else if (string.Equals(collectionType, "tvshows", StringComparison.OrdinalIgnoreCase))
                {
                    includeItemTypes = new[] { "Episode" };
                }
                else if (string.Equals(collectionType, "musicvideos", StringComparison.OrdinalIgnoreCase))
                {
                    includeItemTypes = new[] { "MusicVideo" };
                }

                try
                {
                    // Query latest items for this specific parent library
                    var latestQuery = new LatestItemsQuery
                    {
                        GroupItems = true,
                        IncludeItemTypes = includeItemTypes,
                        IsPlayed = request.IsPlayed,
                        Limit = itemLimit,
                        ParentId = parentItem.InternalId,
                        UserId = user.InternalId,
                    };

                    var items = this.userViewManager.GetLatestItems(latestQuery, dtoOptions);

                    if (items == null || items.Length == 0)
                    {
                        result[parentId] = Array.Empty<BaseItemDto>();
                        continue;
                    }

                    // Convert entities to DTO format
                    var dtos = this.dtoService.GetBaseItemDtos(items, dtoOptions, user);

                    // Safeguard valid client IDs to avoid '0' or blank identity strings
                    for (int i = 0; i < dtos.Length; i++)
                    {
                        var dto = dtos[i];
                        var entity = items[i];
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

                    result[parentId] = dtos;
                }
                catch (Exception ex)
                {
                    this.Logger.ErrorException("Failed to retrieve latest items for parent library ID: {0}", ex, parentId);
                    result[parentId] = Array.Empty<BaseItemDto>();
                }
            }

            return result;
        }

        /// <summary>
        /// Handles GET /Litefin/Items/Thumbnails requests.
        /// </summary>
        /// <param name="request">The batch library card thumbnail request parameters.</param>
        /// <returns>A dictionary mapping parentId to LibraryThumbnailResult.</returns>
        public object Get(GetBatchThumbnailsRequest request)
        {
            var result = new Dictionary<string, LibraryThumbnailResult>(StringComparer.OrdinalIgnoreCase);

            if (string.IsNullOrWhiteSpace(request.ParentIds))
            {
                return result;
            }

            // Resolve target user
            var user = this.GetTargetUser(request.UserId);

            // Parse incoming comma-separated parent GUIDs
            var ids = request.ParentIds!
                .Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(s => Guid.TryParse(s.Trim(), out var g) ? (Guid?)g : null)
                .Where(g => g.HasValue)
                .Select(g => g!.Value)
                .ToArray();

            var dtoOptions = new DtoOptions(allFields: false)
            {
                Fields = new[] { ItemFields.Id, ItemFields.PrimaryImageAspectRatio },
                EnableImages = true,
                EnableUserData = false,
                ImageTypeLimit = 1,
            };

            // Resolve server base URL from the incoming request URI
            string serverUrl = string.Empty;
            if (!string.IsNullOrEmpty(this.Request.AbsoluteUri))
            {
                try
                {
                    var uri = new Uri(this.Request.AbsoluteUri);
                    serverUrl = uri.GetLeftPart(UriPartial.Authority);
                }
                catch
                {
                    serverUrl = string.Empty;
                }
            }

            // Iterate over each parent library to pick the best thumbnail candidate
            foreach (var parentId in ids)
            {
                try
                {
                    var parentItem = this.LibraryManager.GetItemById(parentId);
                    if (parentItem == null)
                    {
                        result[parentId.ToString("N")] = new LibraryThumbnailResult();
                        continue;
                    }

                    var collectionFolder = parentItem as ICollectionFolder;
                    var collectionType = collectionFolder?.CollectionType;

                    string[] includeItemTypes = collectionType?.ToLowerInvariant() switch
                    {
                        "music" => new[] { "MusicAlbum", "Audio" },
                        "movies" => new[] { "Movie" },
                        "tvshows" => new[] { "Series" },
                        "boxsets" => new[] { "BoxSet" },
                        "playlists" => new[] { "Playlist" },
                        _ => Array.Empty<string>(),
                    };

                    var query = new InternalItemsQuery(user)
                    {
                        IncludeItemTypes = includeItemTypes,
                        IsVirtualItem = false,
                        OrderBy = new[] { ("Random", SortOrder.Ascending) },
                        Limit = 5,
                        Recursive = true,
                    };

                    QueryResult<BaseItem> itemsResult;
                    if (parentItem is Folder folder)
                    {
                        itemsResult = folder.GetItems(query);
                    }
                    else
                    {
                        itemsResult = this.LibraryManager.GetItemsResult(query);
                    }

                    var rawItems = itemsResult.Items;
                    var (bestItem, matchedType) = FindBestItem(rawItems, collectionType);

                    BaseItemDto? itemDto = null;
                    string? resolvedUrl = null;

                    if (bestItem != null && matchedType.HasValue)
                    {
                        itemDto = this.dtoService.GetBaseItemDto(bestItem, dtoOptions, user);
                        itemDto.MediaSources = null;
                        itemDto.UserData = null;
                        itemDto.PremiereDate = null;
                        itemDto.EndDate = null;
                        itemDto.OfficialRating = null;
                        itemDto.CommunityRating = null;
                        itemDto.ChannelId = null;
                        itemDto.Status = null;
                        itemDto.AirDays = null;
                        itemDto.ChildCount = null;
                        itemDto.Overview = null;
                        itemDto.Genres = null;
                        itemDto.Taglines = null;
                        itemDto.ExternalUrls = null;
                        itemDto.People = null;
                        itemDto.Studios = null;

                        var tag = GetImageTag(itemDto, matchedType.Value);
                        if (tag != null)
                        {
                            resolvedUrl = $"{serverUrl}/emby/Items/{bestItem.Id:N}/Images/{matchedType.Value}?tag={tag}&maxWidth=512&quality=80";
                        }
                    }

                    result[parentId.ToString("N")] = new LibraryThumbnailResult
                    {
                        Item = itemDto,
                        ResolvedUrl = resolvedUrl,
                    };
                }
                catch (Exception ex)
                {
                    this.Logger.ErrorException("Error processing library thumbnail candidates for parentId: {0}", ex, parentId);
                    result[parentId.ToString("N")] = new LibraryThumbnailResult();
                }
            }

            return result;
        }

        private static (BaseItem? Item, ImageType? MatchedType) FindBestItem(IEnumerable<BaseItem> items, string? collectionType)
        {
            ImageType[] priorityOrder = collectionType?.ToLowerInvariant() switch
            {
                "music" => new[] { ImageType.Primary, ImageType.Thumb, ImageType.Backdrop },
                "photos" or "homevideos" or "musicvideos" or "livetv" => new[] { ImageType.Primary, ImageType.Thumb },
                _ => new[] { ImageType.Backdrop, ImageType.Thumb, ImageType.Primary },
            };

            foreach (var imageType in priorityOrder)
            {
                foreach (var item in items)
                {
                    if (item.HasImage(imageType, 0))
                    {
                        return (item, imageType);
                    }
                }
            }

            return (null, null);
        }

        private static string? GetImageTag(BaseItemDto dto, ImageType imageType)
        {
            if (imageType == ImageType.Backdrop)
            {
                if (dto.BackdropImageTags != null && dto.BackdropImageTags.Length > 0)
                {
                    return dto.BackdropImageTags[0];
                }

                return null;
            }

            if (dto.ImageTags != null && dto.ImageTags.TryGetValue(imageType, out var tag))
            {
                return tag;
            }

            return null;
        }
    }
}
