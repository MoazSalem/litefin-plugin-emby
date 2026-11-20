// <copyright file="CachedShowData.cs" company="Litefin">
// Copyright (c) Litefin. All rights reserved.
// </copyright>

namespace Litefin.Emby.Plugin.Models
{
    using System;
    using System.Collections.Generic;

    /// <summary>
    /// In-memory and on-disk payload representing cached metadata for a specific anime series slug.
    /// Stores the remote fetch timestamp and parsed mapping of episode numbers to their classifications.
    /// </summary>
    public class CachedShowData
    {
        /// <summary>
        /// Gets or sets the normalized show slug identifier used by the canon provider.
        /// </summary>
        public string Slug { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the UTC timestamp when this data was fetched from the remote provider.
        /// Used to invalidate entries older than the configured time-to-live threshold.
        /// </summary>
        public DateTime FetchedAt { get; set; }

        /// <summary>
        /// Gets or sets the map of absolute episode numbers to their parsed classification metadata.
        /// Keyed by positive integer episode index corresponding to canonical numbering.
        /// </summary>
        public Dictionary<int, AnimeMarkerEpisode> Episodes { get; set; } = new Dictionary<int, AnimeMarkerEpisode>();
    }
}
