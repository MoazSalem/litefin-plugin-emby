// <copyright file="AnimeMarkersResponse.cs" company="Litefin">
// Copyright (c) Litefin. All rights reserved.
// </copyright>

namespace Litefin.Emby.Plugin.Models
{
    using System;
    using System.Collections.Generic;
    using System.Text.Json.Serialization;

    /// <summary>
    /// Response payload delivered to the client for an entire series query.
    /// Returns canon classification markers, recap indicators, and audio verdicts.
    /// </summary>
    public class AnimeMarkersResponse
    {
        /// <summary>
        /// Gets or sets a value indicating whether anime marker processing is enabled.
        /// Allows clients to globally check whether companion markers are operational.
        /// </summary>
        [JsonPropertyName("enabled")]
        public bool Enabled { get; set; } = true;

        /// <summary>
        /// Gets or sets a value indicating whether the series was positively identified as an anime title.
        /// </summary>
        [JsonPropertyName("matched")]
        public bool Matched { get; set; }

        /// <summary>
        /// Gets or sets the canonical provider slug resolved for this series.
        /// Matches the URL path token on AnimeFillerList.
        /// </summary>
        [JsonPropertyName("slug")]
        public string? Slug { get; set; }

        /// <summary>
        /// Gets or sets the episode classification entries keyed by Emby episode identifier.
        /// Keyed by normalized 32-character hex GUID string as well as numeric internal ID.
        /// </summary>
        [JsonPropertyName("episodes")]
        public Dictionary<string, EpisodeVerdict> Episodes { get; set; } = new Dictionary<string, EpisodeVerdict>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Gets or sets the aggregated season entries keyed by Emby season identifier.
        /// Allows season headers and row badges to present summarized audio track statuses.
        /// </summary>
        [JsonPropertyName("seasons")]
        public Dictionary<string, SeasonVerdict> Seasons { get; set; } = new Dictionary<string, SeasonVerdict>(StringComparer.OrdinalIgnoreCase);
    }
}
