// <copyright file="MergedRowsRequests.cs" company="Litefin">
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
    /// Request DTO for retrieving a merged and deduplicated list containing both Continue Watching and Next Up rows.
    /// </summary>
    [Route("/Litefin/MergedRows/ContinueAndNextUp", "GET", Summary = "Gets merged Continue Watching and Next Up rows")]
    [Authenticated]
    public class GetContinueAndNextUpRequest : IReturn<QueryResult<BaseItemDto>>
    {
        /// <summary>
        /// Gets or sets the optional target user GUID.
        /// </summary>
        [ApiMember(Name = "UserId", Description = "Target user GUID", IsRequired = false, DataType = "string", ParameterType = "query", Verb = "GET")]
        public Guid? UserId { get; set; }

        /// <summary>
        /// Gets or sets the maximum number of records to return for each query before merging.
        /// </summary>
        [ApiMember(Name = "Limit", Description = "Maximum records to return", IsRequired = false, DataType = "int", ParameterType = "query", Verb = "GET")]
        public int? Limit { get; set; }

        /// <summary>
        /// Gets or sets optional comma-separated ItemFields.
        /// </summary>
        [ApiMember(Name = "Fields", Description = "Comma-separated ItemFields", IsRequired = false, DataType = "string", ParameterType = "query", Verb = "GET")]
        public string? Fields { get; set; }
    }
}
