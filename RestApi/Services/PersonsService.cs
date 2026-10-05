// <copyright file="PersonsService.cs" company="Litefin">
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
    using MediaBrowser.Controller.Library;
    using MediaBrowser.Model.Dto;
    using MediaBrowser.Model.Entities;
    using MediaBrowser.Model.Net;
    using MediaBrowser.Model.Querying;

    /// <summary>
    /// REST API service providing person filmography and discography items with attached character roles.
    /// Enables the Litefin UI to present actors, directors, and artists with their specific performance credentials.
    /// </summary>
    public class PersonsService : BaseApiService
    {
        private readonly IDtoService dtoService;

        /// <summary>
        /// Initializes a new instance of the <see cref="PersonsService"/> class.
        /// </summary>
        /// <param name="dtoService">The server DTO serialization service.</param>
        public PersonsService(IDtoService dtoService)
        {
            this.dtoService = dtoService;
        }

        /// <summary>
        /// Processes GET /Litefin/Persons/{PersonId}/Items requests.
        /// </summary>
        /// <param name="request">The query parameters defining person and pagination.</param>
        /// <returns>A QueryResult of BaseItemDto populated with role details.</returns>
        public object Get(GetPersonItemsRequest request)
        {
            // Resolve the current target user context
            var user = this.GetTargetUser(request.UserId);

            this.Logger.Info("Processing GetPersonItems for person identifier: {0}, user: {1}", request.PersonId, user.Name);

            // Locate the person entity in the Emby database across Guid, Int64, or string identifier formats
            BaseItem? targetPerson = null;
            if (Guid.TryParse(request.PersonId, out var personGuid))
            {
                targetPerson = this.LibraryManager.GetItemById(personGuid);
            }
            else if (long.TryParse(request.PersonId, out var personLongId))
            {
                targetPerson = this.LibraryManager.GetItemById(personLongId);
            }
            else if (!string.IsNullOrEmpty(request.PersonId))
            {
                targetPerson = this.LibraryManager.GetItemById(request.PersonId);
            }

            // Reject the request if the person entity does not exist on the server
            if (targetPerson == null)
            {
                this.Logger.Warn("Person item not found for identifier: {0}", request.PersonId);
                throw new HttpException("Person not found.")
                {
                    StatusCode = HttpStatusCode.NotFound,
                };
            }

            var targetPersonName = targetPerson.Name;
            var targetInternalId = targetPerson.InternalId;
            var queryLimit = request.Limit.GetValueOrDefault(100);

            // Query Movies associated with this person (sorted newest first)
            var movies = this.LibraryManager.GetItemList(new InternalItemsQuery(user)
            {
                PersonIds = new[] { targetInternalId },
                IncludeItemTypes = new[] { "Movie" },
                OrderBy = new[] { ("PremiereDate", SortOrder.Descending) },
                Limit = queryLimit,
                Recursive = true,
            });

            // Query Series associated with this person
            var series = this.LibraryManager.GetItemList(new InternalItemsQuery(user)
            {
                PersonIds = new[] { targetInternalId },
                IncludeItemTypes = new[] { "Series" },
                OrderBy = new[] { ("PremiereDate", SortOrder.Descending) },
                Limit = queryLimit,
                Recursive = true,
            });

            // Query specific Episodes (limited to 15 items to provide display cards plus overflow buffer)
            var episodes = this.LibraryManager.GetItemList(new InternalItemsQuery(user)
            {
                PersonIds = new[] { targetInternalId },
                IncludeItemTypes = new[] { "Episode" },
                OrderBy = new[] { ("PremiereDate", SortOrder.Descending) },
                Limit = 15,
                Recursive = true,
            });

            // Query Music Albums where the person is credited as artist
            var albums = this.LibraryManager.GetItemList(new InternalItemsQuery(user)
            {
                ArtistIds = new[] { targetInternalId },
                IncludeItemTypes = new[] { "MusicAlbum" },
                OrderBy = new[] { ("ProductionYear", SortOrder.Descending), ("SortName", SortOrder.Ascending) },
                Limit = 12,
                Recursive = true,
            });

            // Fallback to PersonIds filter if ArtistIds yielded no music album entries
            if (albums.Length == 0)
            {
                albums = this.LibraryManager.GetItemList(new InternalItemsQuery(user)
                {
                    PersonIds = new[] { targetInternalId },
                    IncludeItemTypes = new[] { "MusicAlbum" },
                    OrderBy = new[] { ("ProductionYear", SortOrder.Descending), ("SortName", SortOrder.Ascending) },
                    Limit = 12,
                    Recursive = true,
                });
            }

            // Query Audio Songs where the person is credited
            var songs = this.LibraryManager.GetItemList(new InternalItemsQuery(user)
            {
                ArtistIds = new[] { targetInternalId },
                IncludeItemTypes = new[] { "Audio" },
                OrderBy = new[] { ("SortName", SortOrder.Ascending) },
                Limit = 12,
                Recursive = true,
            });

            // Fallback to PersonIds for audio songs if ArtistIds query returned empty
            if (songs.Length == 0)
            {
                songs = this.LibraryManager.GetItemList(new InternalItemsQuery(user)
                {
                    PersonIds = new[] { targetInternalId },
                    IncludeItemTypes = new[] { "Audio" },
                    OrderBy = new[] { ("SortName", SortOrder.Ascending) },
                    Limit = 12,
                    Recursive = true,
                });
            }

            // Consolidate all category items into a continuous sequence for serialization
            var combinedItems = new List<BaseItem>(movies.Length + series.Length + episodes.Length + albums.Length + songs.Length);
            combinedItems.AddRange(movies);
            combinedItems.AddRange(series);
            combinedItems.AddRange(episodes);
            combinedItems.AddRange(albums);
            combinedItems.AddRange(songs);

            // Establish serialization options with minimal payload overhead
            var dtoOptions = new DtoOptions(allFields: false)
            {
                Fields = new[]
                {
                    ItemFields.PrimaryImageAspectRatio,
                    ItemFields.SeriesStudio,
                    ItemFields.MediaSources,
                },
                EnableImages = true,
                EnableUserData = true,
                ImageTypeLimit = 1,
            };

            // Map all database items into consumer DTO models
            var dtos = this.dtoService.GetBaseItemDtos(combinedItems.ToArray(), dtoOptions, user);

            // Populate the specific person's character role or title credit onto each resulting DTO
            for (var i = 0; i < combinedItems.Count && i < dtos.Length; i++)
            {
                var item = combinedItems[i];
                var itemDto = dtos[i];

                // Fetch people associated with this media item
                var itemPeople = this.LibraryManager.GetItemPeople(item);
                PersonInfo? matchingPerson = null;

                // Match against person name if available, else take the leading credit
                if (!string.IsNullOrEmpty(targetPersonName))
                {
                    matchingPerson = itemPeople.FirstOrDefault(p => string.Equals(p.Name, targetPersonName, StringComparison.OrdinalIgnoreCase));
                }
                else if (itemPeople.Count > 0)
                {
                    matchingPerson = itemPeople[0];
                }

                // If a matching contribution was resolved, assign it to the DTO People collection
                if (matchingPerson != null)
                {
                    itemDto.People = new[]
                    {
                        new BaseItemPerson
                        {
                            Id = targetPerson.Id.ToString(),
                            Name = matchingPerson.Name,
                            Role = matchingPerson.Role ?? string.Empty,
                            Type = matchingPerson.Type,
                        },
                    };
                }
            }

            // Return wrapped query result containing all populated DTOs
            return new QueryResult<BaseItemDto>
            {
                Items = dtos,
                TotalRecordCount = dtos.Length,
            };
        }
    }
}
