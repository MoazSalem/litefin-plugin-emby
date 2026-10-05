using System;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Querying;
using MediaBrowser.Model.Services;

namespace Litefin.Emby.Plugin.RestApi.Requests
{
    /// <summary>
    /// Request DTO for querying filmography and discography items associated with a person,
    /// returning items populated with their specific character role or contribution.
    /// </summary>
    [Route("/Litefin/Persons/{PersonId}/Items", "GET", Summary = "Gets items for a person with their role attached")]
    [Authenticated]
    public class GetPersonItemsRequest : IReturn<QueryResult<BaseItemDto>>
    {
        /// <summary>
        /// Gets or sets the target person ID (Guid or string representation).
        /// </summary>
        [ApiMember(Name = "PersonId", Description = "The identifier of the person", IsRequired = true, DataType = "string", ParameterType = "path")]
        public string PersonId { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the optional user ID to scope library visibility and playstates.
        /// </summary>
        [ApiMember(Name = "UserId", Description = "Optional user ID to filter by", IsRequired = false, DataType = "string", ParameterType = "query")]
        public Guid? UserId { get; set; }

        /// <summary>
        /// Gets or sets the maximum number of items to return per media kind.
        /// </summary>
        [ApiMember(Name = "Limit", Description = "Optional record limit per category", IsRequired = false, DataType = "int", ParameterType = "query")]
        public int? Limit { get; set; }
    }
}
