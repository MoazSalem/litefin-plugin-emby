// <copyright file="CollectionsRequests.cs" company="Litefin">
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
    /// Request DTO for querying collections (BoxSets) that contain a specific media item.
    /// </summary>
    [Route("/Litefin/Items/{ItemId}/Collections", "GET", Summary = "Gets the collections that include the specified item")]
    [Authenticated]
    public class GetItemCollectionsRequest : IReturn<QueryResult<BaseItemDto>>
    {
        /// <summary>
        /// Gets or sets the target item GUID.
        /// </summary>
        [ApiMember(Name = "ItemId", Description = "Target item GUID", IsRequired = true, DataType = "string", ParameterType = "path", Verb = "GET")]
        public Guid ItemId { get; set; }

        /// <summary>
        /// Gets or sets the optional target user GUID.
        /// </summary>
        [ApiMember(Name = "UserId", Description = "User ID filter", IsRequired = false, DataType = "string", ParameterType = "query", Verb = "GET")]
        public Guid? UserId { get; set; }

        /// <summary>
        /// Gets or sets the optional record start index for pagination.
        /// </summary>
        [ApiMember(Name = "StartIndex", Description = "Start index", IsRequired = false, DataType = "int", ParameterType = "query", Verb = "GET")]
        public int? StartIndex { get; set; }

        /// <summary>
        /// Gets or sets the maximum number of collections to return.
        /// </summary>
        [ApiMember(Name = "Limit", Description = "Maximum collections to return", IsRequired = false, DataType = "int", ParameterType = "query", Verb = "GET")]
        public int? Limit { get; set; }

        /// <summary>
        /// Gets or sets optional comma-separated ItemFields.
        /// </summary>
        [ApiMember(Name = "Fields", Description = "Comma-separated ItemFields", IsRequired = false, DataType = "string", ParameterType = "query", Verb = "GET")]
        public string? Fields { get; set; }
    }
}
