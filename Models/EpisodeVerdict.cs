// <copyright file="EpisodeVerdict.cs" company="Litefin">
// Copyright (c) Litefin. All rights reserved.
// </copyright>

namespace Litefin.Emby.Plugin.Models
{
    using System.Text.Json.Serialization;

    /// <summary>
    /// Transport DTO representing the evaluation verdict for an individual episode.
    /// Communicated to the client to render visual indicator badges on episode cards.
    /// </summary>
    public class EpisodeVerdict
    {
        /// <summary>
        /// Gets or sets the standardized kind string (Filler, Mixed, MangaCanon, AnimeCanon).
        /// </summary>
        [JsonPropertyName("kind")]
        public string? Kind { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether the episode is identified as a recap episode.
        /// </summary>
        [JsonPropertyName("recap")]
        public bool Recap { get; set; }

        /// <summary>
        /// Gets or sets the audio stream verdict (Subbed, Dubbed, Subbed/Dubbed, or null).
        /// </summary>
        [JsonPropertyName("audio")]
        public string? Audio { get; set; }
    }
}
