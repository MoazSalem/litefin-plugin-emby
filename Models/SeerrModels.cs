// <copyright file="SeerrModels.cs" company="Litefin">
// Copyright (c) Litefin. All rights reserved.
// </copyright>

namespace Litefin.Emby.Plugin.Models
{
    using System.Collections.Generic;

    /// <summary>
    /// A media request submitted by an authenticated Litefin user for upstream processing by Seerr.
    /// </summary>
    public class SeerrRequest
    {
        /// <summary>
        /// Gets or sets the media type. Supported values are movie and tv.
        /// </summary>
        public string MediaType { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the TMDB media identifier.
        /// </summary>
        public int MediaId { get; set; }

        /// <summary>
        /// Gets or sets the requested season numbers for a television series.
        /// </summary>
        public IReadOnlyList<int>? Seasons { get; set; }

        /// <summary>Gets or sets the selected Seerr service identifier.</summary>
        public int? ServerId { get; set; }

        /// <summary>Gets or sets the selected quality profile identifier.</summary>
        public int? ProfileId { get; set; }

        /// <summary>Gets or sets the selected root folder.</summary>
        public string? RootFolder { get; set; }

        /// <summary>Gets or sets the optional Sonarr language profile identifier.</summary>
        public int? LanguageProfileId { get; set; }

        /// <summary>Gets or sets a value indicating whether the 4K service is requested.</summary>
        public bool Is4K { get; set; }

        /// <summary>Gets or sets the optional requested user identifier.</summary>
        public int? UserId { get; set; }
    }

    /// <summary>
    /// Request payload to authenticate directly using Seerr admin credentials.
    /// </summary>
    public class SeerrAdminLoginRequest
    {
        /// <summary>
        /// Gets or sets the target Seerr base URL.
        /// </summary>
        public string SeerrUrl { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the Seerr admin username or email.
        /// </summary>
        public string Username { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the Seerr admin password.
        /// </summary>
        public string Password { get; set; } = string.Empty;
    }

    /// <summary>
    /// Result of authenticating via admin credentials and saving the Seerr API key.
    /// </summary>
    public class SeerrAdminLoginResult
    {
        /// <summary>
        /// Gets or sets a value indicating whether login succeeded and the API key was saved.
        /// </summary>
        public bool Success { get; set; }

        /// <summary>
        /// Gets or sets an optional user-facing message or error explanation.
        /// </summary>
        public string? Message { get; set; }
    }

    /// <summary>
    /// Temporary Seerr credentials supplied by an administrator for a connection test.
    /// </summary>
    public class SeerrConnectionTestRequest
    {
        /// <summary>
        /// Gets or sets the Seerr base URL.
        /// </summary>
        public string SeerrUrl { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the Seerr API key. Optional when testing reachability.
        /// </summary>
        public string? SeerrApiKey { get; set; } = string.Empty;
    }


    /// <summary>
    /// Request payload to test whether a Seerr server is reachable without requiring an API key.
    /// </summary>
    public class SeerrServerTestRequest
    {
        /// <summary>
        /// Gets or sets the target Seerr base URL.
        /// </summary>
        public string SeerrUrl { get; set; } = string.Empty;
    }
}
