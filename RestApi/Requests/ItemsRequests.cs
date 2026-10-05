// <copyright file="ItemsRequests.cs" company="Litefin">
// Copyright (c) Litefin. All rights reserved.
// </copyright>

namespace Litefin.Emby.Plugin.RestApi.Requests
{
    using System;
    using System.Collections.Generic;
    using Litefin.Emby.Plugin.Models;
    using MediaBrowser.Controller.Net;
    using MediaBrowser.Model.Dto;
    using MediaBrowser.Model.Services;

    /// <summary>
    /// Request DTO for batch retrieving latest items across multiple parent library IDs in a single round-trip.
    /// </summary>
    [Route("/Litefin/Items/Latest", "GET", Summary = "Batch retrieves latest items for multiple libraries")]
    [Authenticated]
    public class GetBatchLatestRequest : IReturn<Dictionary<Guid, IReadOnlyList<BaseItemDto>>>
    {
        /// <summary>
        /// Gets or sets the comma-separated parent library IDs.
        /// </summary>
        [ApiMember(Name = "ParentIds", Description = "Comma-separated parent library GUIDs", IsRequired = true, DataType = "string", ParameterType = "query", Verb = "GET")]
        public string? ParentIds { get; set; }

        /// <summary>
        /// Gets or sets the optional user filter.
        /// </summary>
        [ApiMember(Name = "UserId", Description = "Target user GUID", IsRequired = false, DataType = "string", ParameterType = "query", Verb = "GET")]
        public Guid? UserId { get; set; }

        /// <summary>
        /// Gets or sets the maximum number of items to return per library (default: 12).
        /// </summary>
        [ApiMember(Name = "Limit", Description = "Limit per library", IsRequired = false, DataType = "int", ParameterType = "query", Verb = "GET")]
        public int? Limit { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether to filter by played status.
        /// </summary>
        [ApiMember(Name = "IsPlayed", Description = "Filter by played status", IsRequired = false, DataType = "bool", ParameterType = "query", Verb = "GET")]
        public bool? IsPlayed { get; set; }

        /// <summary>
        /// Gets or sets optional comma-separated ItemFields.
        /// </summary>
        [ApiMember(Name = "Fields", Description = "Comma-separated ItemFields", IsRequired = false, DataType = "string", ParameterType = "query", Verb = "GET")]
        public string? Fields { get; set; }
    }

    /// <summary>
    /// Request DTO for batch retrieving curated library card thumbnails.
    /// </summary>
    [Route("/Litefin/Items/Thumbnails", "GET", Summary = "Batch retrieves library thumbnails with pre-resolved URLs")]
    [Authenticated]
    public class GetBatchThumbnailsRequest : IReturn<IReadOnlyDictionary<string, LibraryThumbnailResult>>
    {
        /// <summary>
        /// Gets or sets the comma-separated parent library IDs.
        /// </summary>
        [ApiMember(Name = "ParentIds", Description = "Comma-separated parent library GUIDs", IsRequired = true, DataType = "string", ParameterType = "query", Verb = "GET")]
        public string? ParentIds { get; set; }

        /// <summary>
        /// Gets or sets the optional user filter.
        /// </summary>
        [ApiMember(Name = "UserId", Description = "Target user GUID", IsRequired = false, DataType = "string", ParameterType = "query", Verb = "GET")]
        public Guid? UserId { get; set; }
    }
}
