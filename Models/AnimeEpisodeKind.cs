// <copyright file="AnimeEpisodeKind.cs" company="Litefin">
// Copyright (c) Litefin. All rights reserved.
// </copyright>

namespace Litefin.Emby.Plugin.Models
{
    /// <summary>
    /// Categorization kinds for anime episodes derived from canon references.
    /// Distinguishes manga adaptations, anime-original canon, mixed arcs, and filler episodes.
    /// </summary>
    public enum AnimeEpisodeKind
    {
        /// <summary>
        /// Content directly adapting the original manga source material.
        /// </summary>
        MangaCanon = 0,

        /// <summary>
        /// Content created specifically for the television adaptation that is considered canonical.
        /// </summary>
        AnimeCanon = 1,

        /// <summary>
        /// Content that mixes canonical storyline events with non-canonical embellishments or filler.
        /// </summary>
        Mixed = 2,

        /// <summary>
        /// Non-canonical filler storyline created independently of the original manga.
        /// </summary>
        Filler = 3,
    }
}
