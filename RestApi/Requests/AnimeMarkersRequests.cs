// <copyright file="AnimeMarkersRequests.cs" company="Litefin">
// Copyright (c) Litefin. All rights reserved.
// </copyright>

namespace Litefin.Emby.Plugin.RestApi.Requests
{
    using Litefin.Emby.Plugin.Models;
    using MediaBrowser.Model.Services;

    /// <summary>
    /// Request parameters for retrieving anime episode classification and audio markers.
    /// Exposes canon kinds (MangaCanon, AnimeCanon, Mixed, Filler) and audio stream verdicts.
    /// </summary>
    [Route("/Litefin/AnimeMarkers/Series", "GET", Summary = "Queries episode and season markers for an anime series")]
    [Authenticated]
    public class GetAnimeMarkersRequest : IReturn<AnimeMarkersResponse>
    {
        /// <summary>
        /// Gets or sets the television series identifier (GUID string or numeric internal ID).
        /// </summary>
        public string? SeriesId { get; set; }
    }
}
