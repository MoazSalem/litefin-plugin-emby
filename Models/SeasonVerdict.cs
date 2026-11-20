// <copyright file="SeasonVerdict.cs" company="Litefin">
// Copyright (c) Litefin. All rights reserved.
// </copyright>

namespace Litefin.Emby.Plugin.Models
{
    using System.Text.Json.Serialization;

    /// <summary>
    /// Transport DTO representing the audio evaluation verdict for an entire season.
    /// Aggregates audio stream tracks across all episodes contained within the season.
    /// </summary>
    public class SeasonVerdict
    {
        /// <summary>
        /// Gets or sets the aggregated audio classification across all episodes in this season.
        /// </summary>
        [JsonPropertyName("audio")]
        public string? Audio { get; set; }
    }
}
