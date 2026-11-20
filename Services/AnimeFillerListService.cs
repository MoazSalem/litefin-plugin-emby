// <copyright file="AnimeFillerListService.cs" company="Litefin">
// Copyright (c) Litefin. All rights reserved.
// </copyright>

namespace Litefin.Emby.Plugin.Services
{
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.Globalization;
    using System.IO;
    using System.Net.Http;
    using System.Text.Json;
    using System.Text.RegularExpressions;
    using System.Threading;
    using System.Threading.Tasks;
    using Litefin.Emby.Plugin.Models;
    using MediaBrowser.Model.Logging;

    /// <summary>
    /// Service responsible for scraping, caching, and querying episode canon classifications
    /// from the AnimeFillerList reference provider.
    /// Manages an in-memory hot cache as well as persistent disk storage.
    /// </summary>
    public class AnimeFillerListService
    {
        // Maximum cache age before refreshing show listings from the remote provider (7 days)
        private static readonly TimeSpan CacheTtl = TimeSpan.FromDays(7);

        // Negative cache duration for slugs that returned 404 or failed parsing (1 day)
        private static readonly TimeSpan NegativeCacheTtl = TimeSpan.FromDays(1);

        // Concurrency throttle to avoid overwhelming the upstream provider
        private static readonly SemaphoreSlim RequestThrottle = new SemaphoreSlim(2, 2);

        // Shared HTTP client configured for scraping operations
        private static readonly HttpClient HttpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(20),
        };

        // Cached JSON serializer options to avoid re-allocation on read and write operations
        private static readonly JsonSerializerOptions IndentedJsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true,
        };

        // In-memory cache holding parsed show datasets keyed by normalized slug
        private static readonly ConcurrentDictionary<string, CachedShowData> MemoryCache =
            new ConcurrentDictionary<string, CachedShowData>(StringComparer.OrdinalIgnoreCase);

        // Negative cache storing timestamps for slugs that do not exist upstream
        private static readonly ConcurrentDictionary<string, DateTime> NegativeCache =
            new ConcurrentDictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);

        // Regex matching episode row elements with class and eps id
        private static readonly Regex EpisodeRowRegex = new Regex(
            @"<tr\s+class=""(?<class>[^""]+)""[^>]*id=""eps-(?<num>\d+)""",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        // Alternate regex matching episode row elements where id precedes class
        private static readonly Regex EpisodeRowAltRegex = new Regex(
            @"<tr[^>]*id=""eps-(?<num>\d+)""[^>]*class=""(?<class>[^""]+)""",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private readonly ILogger logger;
        private readonly string cacheDirectory;

        /// <summary>
        /// Initializes a new instance of the <see cref="AnimeFillerListService"/> class.
        /// </summary>
        /// <param name="logger">Instance of the <see cref="ILogger"/> interface.</param>
        public AnimeFillerListService(ILogger logger)
        {
            this.logger = logger;

            // Resolve the persistent storage directory for cached anime marker datasets
            var baseConfigPath = Plugin.Instance?.ApplicationPaths?.PluginConfigurationsPath;
            if (string.IsNullOrWhiteSpace(baseConfigPath))
            {
                baseConfigPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "Emby-Server",
                    "plugins",
                    "configurations");
            }

            this.cacheDirectory = Path.Combine(baseConfigPath, "litefin_anime_markers");

            // Guarantee directory structure exists for atomic file writes
            try
            {
                if (!Directory.Exists(this.cacheDirectory))
                {
                    Directory.CreateDirectory(this.cacheDirectory);
                }
            }
            catch (Exception ex)
            {
                this.logger.ErrorException("Failed to initialize anime markers cache directory at {0}", ex, this.cacheDirectory);
            }
        }

        /// <summary>
        /// Retrieves episode classification data for the given show slug.
        /// Checks in-memory cache, then local disk storage, and finally scrapes remote upstream.
        /// </summary>
        /// <param name="slug">Canonical show slug identifier.</param>
        /// <param name="cancellationToken">Cancellation token for async operations.</param>
        /// <returns>Cached show data if resolved; otherwise, null.</returns>
        public async Task<CachedShowData?> GetShowDataAsync(string slug, CancellationToken cancellationToken = default)
        {
            // Guard against empty or blank slugs
            if (string.IsNullOrWhiteSpace(slug))
            {
                return null;
            }

            var normalizedSlug = slug.Trim().ToLowerInvariant();

            // -----------------------------------------------------------------
            // Negative Cache Check
            // -----------------------------------------------------------------
            // If previous attempts returned 404 Not Found within the backoff window,
            // immediately bypass network queries to avoid hammering upstream.
            if (NegativeCache.TryGetValue(normalizedSlug, out var negativeTimestamp))
            {
                if (DateTime.UtcNow - negativeTimestamp < NegativeCacheTtl)
                {
                    return null;
                }

                NegativeCache.TryRemove(normalizedSlug, out _);
            }

            // -----------------------------------------------------------------
            // Layer 1: Fast In-Memory Concurrent Dictionary
            // -----------------------------------------------------------------
            // Delivers sub-millisecond lookups for repeated requests within the same server lifecycle.
            if (MemoryCache.TryGetValue(normalizedSlug, out var cachedData))
            {
                if (DateTime.UtcNow - cachedData.FetchedAt < CacheTtl)
                {
                    return cachedData;
                }
            }

            // -----------------------------------------------------------------
            // Layer 2: Persistent Local JSON Disk Storage
            // -----------------------------------------------------------------
            // Survives server restarts without triggering unnecessary remote scraping.
            var diskData = await this.LoadFromDiskAsync(normalizedSlug, cancellationToken).ConfigureAwait(false);
            if (diskData != null)
            {
                MemoryCache[normalizedSlug] = diskData;

                if (DateTime.UtcNow - diskData.FetchedAt < CacheTtl)
                {
                    return diskData;
                }
            }

            // -----------------------------------------------------------------
            // Layer 3: Remote HTML Scraping
            // -----------------------------------------------------------------
            // Pulls live classifications from AnimeFillerList and parses episode tables.
            return await this.FetchFromRemoteAsync(normalizedSlug, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// Parses table rows and extracts episode classification metadata from raw HTML.
        /// </summary>
        /// <param name="html">Raw HTML markup from AnimeFillerList.</param>
        /// <returns>Map of episode numbers to parsed classification instances.</returns>
        private static Dictionary<int, AnimeMarkerEpisode> ParseEpisodesFromHtml(string html)
        {
            var result = new Dictionary<int, AnimeMarkerEpisode>();

            // Try primary row pattern where class comes first
            var matches = EpisodeRowRegex.Matches(html);

            // Fall back to secondary pattern where id comes first
            if (matches.Count == 0)
            {
                matches = EpisodeRowAltRegex.Matches(html);
            }

            // Iterate through all matched episode elements
            foreach (Match match in matches)
            {
                if (!match.Success)
                {
                    continue;
                }

                var numStr = match.Groups["num"].Value;
                var classStr = match.Groups["class"].Value.ToLowerInvariant();

                // Parse episode number integer
                if (!int.TryParse(numStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out var episodeNum))
                {
                    continue;
                }

                // Determine episode kind from the class token
                var kind = AnimeEpisodeKind.MangaCanon;
                var isRecap = classStr.Contains("recap");

                if (classStr.Contains("filler") && !classStr.Contains("canon"))
                {
                    // Pure filler episode
                    kind = AnimeEpisodeKind.Filler;
                }
                else if (classStr.Contains("mixed"))
                {
                    // Mixed canon and filler content
                    kind = AnimeEpisodeKind.Mixed;
                }
                else if (classStr.Contains("anime_canon") || classStr.Contains("anime-canon"))
                {
                    // TV anime canonical content
                    kind = AnimeEpisodeKind.AnimeCanon;
                }
                else
                {
                    // Manga canon content
                    kind = AnimeEpisodeKind.MangaCanon;
                }

                // Record episode metadata
                result[episodeNum] = new AnimeMarkerEpisode
                {
                    Number = episodeNum,
                    Kind = kind,
                    Recap = isRecap,
                };
            }

            return result;
        }

        /// <summary>
        /// Scrapes remote HTML from AnimeFillerList and parses episode tables.
        /// </summary>
        /// <param name="slug">Normalized show slug.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>Parsed show data or null if request failed or was not found.</returns>
        private async Task<CachedShowData?> FetchFromRemoteAsync(string slug, CancellationToken cancellationToken)
        {
            // Acquire concurrency semaphore to enforce rate limits
            await RequestThrottle.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                // Re-check memory cache after acquiring throttle lock
                if (MemoryCache.TryGetValue(slug, out var existing) && DateTime.UtcNow - existing.FetchedAt < CacheTtl)
                {
                    return existing;
                }

                var url = $"https://www.animefillerlist.com/shows/{slug}";
                this.logger.Info("Fetching anime filler list from {0}", url);

                using (var httpRequest = new HttpRequestMessage(HttpMethod.Get, url))
                {
                    httpRequest.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) Litefin/1.0");

                    using (var response = await HttpClient.SendAsync(httpRequest, cancellationToken).ConfigureAwait(false))
                    {
                        // Handle not found status codes gracefully
                        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                        {
                            this.logger.Warn("Anime show slug '{0}' was not found on AnimeFillerList (404)", slug);
                            NegativeCache[slug] = DateTime.UtcNow;
                            return null;
                        }

                        response.EnsureSuccessStatusCode();

                        // Read raw HTML content
                        var html = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

                        // Parse episode rows from HTML markup
                        var episodes = ParseEpisodesFromHtml(html);

                        if (episodes.Count == 0)
                        {
                            this.logger.Warn("Found no episode markers for slug '{0}' in remote HTML", slug);
                            NegativeCache[slug] = DateTime.UtcNow;
                            return null;
                        }

                        // Assemble cached show data container
                        var showData = new CachedShowData
                        {
                            Slug = slug,
                            FetchedAt = DateTime.UtcNow,
                        };

                        foreach (var ep in episodes)
                        {
                            showData.Episodes[ep.Key] = ep.Value;
                        }

                        // Update in-memory cache
                        MemoryCache[slug] = showData;

                        // Persist to local disk cache asynchronously
                        await this.SaveToDiskAsync(showData, cancellationToken).ConfigureAwait(false);

                        this.logger.Info("Successfully parsed and cached {0} episodes for slug '{1}'", episodes.Count, slug);
                        return showData;
                    }
                }
            }
            catch (Exception ex)
            {
                this.logger.ErrorException("Failed to fetch anime filler markers for slug '{0}'", ex, slug);
                return null;
            }
            finally
            {
                // Always release the concurrency semaphore
                RequestThrottle.Release();
            }
        }

        /// <summary>
        /// Reads and deserializes cached show data from local disk storage.
        /// </summary>
        /// <param name="slug">Normalized show slug.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>Deserialized show data or null if absent or corrupt.</returns>
        private async Task<CachedShowData?> LoadFromDiskAsync(string slug, CancellationToken cancellationToken)
        {
            var filePath = Path.Combine(this.cacheDirectory, $"{slug}.json");

            // Verify that the cached JSON file exists on disk
            if (!File.Exists(filePath))
            {
                return null;
            }

            try
            {
                // Open read stream to deserialize cached show data
                using (var stream = File.OpenRead(filePath))
                {
                    var data = await JsonSerializer.DeserializeAsync<CachedShowData>(stream, IndentedJsonOptions, cancellationToken).ConfigureAwait(false);

                    // ---------------------------------------------------------
                    // Validate Cached Episode Dictionary
                    // ---------------------------------------------------------
                    // If the cache file is empty, missing episodes, or corrupted,
                    // purge the file so an upstream fetch is triggered cleanly.
                    if (data == null || data.Episodes == null || data.Episodes.Count == 0)
                    {
                        this.logger.Warn("Cached anime markers file at {0} contains no episodes; purging corrupted cache file", filePath);

                        try
                        {
                            File.Delete(filePath);
                        }
                        catch (Exception deleteEx)
                        {
                            this.logger.Debug("Failed to delete corrupted anime cache file {0}: {1}", filePath, deleteEx.Message);
                        }

                        return null;
                    }

                    return data;
                }
            }
            catch (Exception ex)
            {
                this.logger.Warn("Failed to load cached anime markers from file: {0} ({1})", filePath, ex.Message);
                return null;
            }
        }

        /// <summary>
        /// Serializes and writes show data to disk storage.
        /// </summary>
        /// <param name="data">The show data instance to serialize.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>Task tracking async completion.</returns>
        private async Task SaveToDiskAsync(CachedShowData data, CancellationToken cancellationToken)
        {
            var filePath = Path.Combine(this.cacheDirectory, $"{data.Slug}.json");

            try
            {
                using (var stream = File.Create(filePath))
                {
                    await JsonSerializer.SerializeAsync(stream, data, IndentedJsonOptions, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                this.logger.Warn("Failed to write anime markers cache to file: {0} ({1})", filePath, ex.Message);
            }
        }
    }
}
