// <copyright file="HeroRequests.cs" company="Litefin">
// Copyright (c) Litefin. All rights reserved.
// </copyright>

namespace Litefin.Emby.Plugin.RestApi.Requests
{
    using System;
    using MediaBrowser.Controller.Net;
    using MediaBrowser.Model.Dto;
    using MediaBrowser.Model.Querying;
    using MediaBrowser.Model.Services;

    /// <summary>
    /// Request DTO for querying curated featured items (Movies and Series with Backdrops) for the Home Hero Carousel.
    /// </summary>
    [Route("/Litefin/Hero", "GET", Summary = "Gets curated hero carousel items")]
    [Authenticated]
    public class GetHeroItemsRequest : IReturn<QueryResult<BaseItemDto>>
    {
        /// <summary>
        /// Gets or sets the optional target user ID filter.
        /// When omitted, resolves automatically to the authenticated user.
        /// </summary>
        [ApiMember(Name = "UserId", Description = "User ID filter", IsRequired = false, DataType = "string", ParameterType = "query", Verb = "GET")]
        public Guid? UserId { get; set; }

        /// <summary>
        /// Gets or sets the maximum number of hero items to return (default: 5).
        /// </summary>
        [ApiMember(Name = "Limit", Description = "Maximum number of items to return", IsRequired = false, DataType = "int", ParameterType = "query", Verb = "GET")]
        public int? Limit { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether to filter out played movies and completed series.
        /// </summary>
        [ApiMember(Name = "IgnoreWatched", Description = "Filter out played items", IsRequired = false, DataType = "bool", ParameterType = "query", Verb = "GET")]
        public bool? IgnoreWatched { get; set; }
    }
}
