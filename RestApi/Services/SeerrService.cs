// <copyright file="SeerrService.cs" company="Litefin">
// Copyright (c) Litefin. All rights reserved.
// </copyright>

namespace Litefin.Emby.Plugin.RestApi.Services
{
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.Globalization;
    using System.IO;
    using System.Linq;
    using System.Net;
    using System.Net.Http;
    using System.Net.Http.Headers;
    using System.Text;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;
    using Litefin.Emby.Plugin.Common;
    using Litefin.Emby.Plugin.Models;
    using Litefin.Emby.Plugin.RestApi.Requests;
    using MediaBrowser.Controller.Api;
    using MediaBrowser.Model.Net;

    /// <summary>
    /// REST API service providing an authenticated proxy bridge between Litefin clients and Seerr (Overseerr/Jellyseerr).
    /// Transparently forwards search, discovery, watchlist, and media request workflows with permission gating and blocklist enforcement.
    /// </summary>
    public class SeerrService : BaseApiService
    {
        private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);

        // Upper bound on image bodies GetImage buffers; larger responses fall back to the TMDB redirect
        private const long MaxImageBytes = 20 * 1024 * 1024;

        // TMDB image path segments accepted by GetImage; mirrors Seerr's own imageproxy validation
        private static readonly System.Text.RegularExpressions.Regex ImagePathSegmentRegex =
            new System.Text.RegularExpressions.Regex(@"^[A-Za-z0-9_.-]+\z", System.Text.RegularExpressions.RegexOptions.Compiled);

        // TMDB duotone filter segments such as w1280_filter(duotone,991B1B,FCA5A5)
        private static readonly System.Text.RegularExpressions.Regex ImageDuotoneSegmentRegex =
            new System.Text.RegularExpressions.Regex(@"^w[0-9]+_filter\(duotone,[0-9A-Fa-f]{6},[0-9A-Fa-f]{6}\)\z", System.Text.RegularExpressions.RegexOptions.Compiled);

        // Numeric TMDB identifier lists such as "123,456" or "8|337" (keywords, watch providers)
        private static readonly System.Text.RegularExpressions.Regex NumericIdListRegex =
            new System.Text.RegularExpressions.Regex(@"^[0-9]+([,|][0-9]+)*\z", System.Text.RegularExpressions.RegexOptions.Compiled);

        // TMDB original-language filters: ISO 639-1 codes, optionally pipe-separated ("ja|ko")
        private static readonly System.Text.RegularExpressions.Regex OriginalLanguageFilterRegex =
            new System.Text.RegularExpressions.Regex(@"^[a-z]{2}(\|[a-z]{2})*\z", System.Text.RegularExpressions.RegexOptions.Compiled);

        // ISO 3166-1 alpha-2 region codes used by TMDB watch provider filters
        private static readonly System.Text.RegularExpressions.Regex WatchRegionRegex =
            new System.Text.RegularExpressions.Regex(@"^[A-Z]{2}\z", System.Text.RegularExpressions.RegexOptions.Compiled);

        // Image types GetImage passes through, mapped to the type it responds with; anything else
        // falls back to the TMDB redirect. Seerr labels JPEGs "image/jpg" (image/<file extension>).
        private static readonly Dictionary<string, string> AllowedImageContentTypes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["image/jpeg"] = "image/jpeg",
            ["image/jpg"] = "image/jpeg",
            ["image/png"] = "image/png",
            ["image/webp"] = "image/webp",
            ["image/gif"] = "image/gif",
        };

        // Persistent shared HttpClient instance configured for robust upstream communication
        private static readonly HttpClient HttpClient = new HttpClient
        {
            Timeout = RequestTimeout,
        };

        // In-memory cache holding non-admin blocklist visibility permissions
        // Keyed by user identifier string to avoid repeated upstream network roundtrips
        private static readonly ConcurrentDictionary<string, (bool CanView, DateTime ExpiresAt)> BlocklistPermissionCache =
            new ConcurrentDictionary<string, (bool CanView, DateTime ExpiresAt)>(StringComparer.OrdinalIgnoreCase);

        // In-memory cache holding upstream Seerr CSRF tokens and secrets
        // Keyed by normalized base URL to support Seerr instances with CSRF protection enabled
        private static readonly ConcurrentDictionary<string, SeerrCsrfToken> CsrfTokenCache =
            new ConcurrentDictionary<string, SeerrCsrfToken>(StringComparer.OrdinalIgnoreCase);

        // In-memory cache holding resolved Seerr user IDs for Emby users
        // Keyed by Emby User ID to avoid repeated upstream network roundtrips on GET requests
        private static readonly ConcurrentDictionary<string, (int? UserId, DateTime ExpiresAt)> SeerrUserIdCache =
            new ConcurrentDictionary<string, (int? UserId, DateTime ExpiresAt)>(StringComparer.OrdinalIgnoreCase);

        private static readonly char[] LanguageDelimiters = new[] { '-', '_' };

        #region Status and Diagnostic Endpoints

        /// <summary>
        /// Processes GET /Litefin/Seerr/Status to verify if Seerr is configured and responsive.
        /// </summary>
        public async Task<object> Get(GetSeerrStatusRequest request)
        {
            if (!TryGetConfiguration(out _, out _))
            {
                return new { configured = false, available = false, imageProxy = true };
            }

            try
            {
                using var response = await this.SendAsync(HttpMethod.Get, "/auth/me", null, this.Request.CancellationToken).ConfigureAwait(false);
                return new { configured = true, available = response.IsSuccessStatusCode, imageProxy = true };
            }
            catch (Exception ex)
            {
                this.Logger.Warn("Unable to reach configured Seerr instance: {0}", ex.Message);
                return new { configured = true, available = false, imageProxy = true };
            }
        }

        /// <summary>
        /// Processes POST /Litefin/Seerr/Status/Test to test temporary connection credentials without persisting them.
        /// </summary>
        public async Task<object> Post(TestSeerrConnectionRequest request)
        {
            // Verify that the calling user has administrative rights on Emby
            if (!this.IsUserAdmin())
            {
                throw new HttpException("Elevation required.") { StatusCode = HttpStatusCode.Forbidden };
            }

            if (request == null || !TryNormalizeUrl(request.SeerrUrl, out var baseUrl))
            {
                throw new HttpException("A valid Seerr URL is required.") { StatusCode = HttpStatusCode.BadRequest };
            }

            var hasApiKey = !string.IsNullOrWhiteSpace(request.SeerrApiKey);

            try
            {
                if (hasApiKey)
                {
                    var apiKey = request.SeerrApiKey!.Trim();
                    using var response = await this.SendAsync(
                        HttpMethod.Get,
                        "/auth/me",
                        null,
                        baseUrl,
                        apiKey,
                        this.Request.CancellationToken).ConfigureAwait(false);

                    if (response.IsSuccessStatusCode)
                    {
                        return new { available = true, authenticated = true, message = "Connected and authenticated successfully." };
                    }

                    if ((int)response.StatusCode == 401 || (int)response.StatusCode == 403)
                    {
                        return new { available = false, authenticated = false, message = "Seerr reached, but the API key was rejected." };
                    }

                    return new { available = false, authenticated = false, message = $"Seerr responded with HTTP {(int)response.StatusCode}." };
                }

                // If no API key was provided, verify general reachability via public status endpoint
                using var httpRequest = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/api/v1/status");
                httpRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

                using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(this.Request.CancellationToken);
                timeoutSource.CancelAfter(TimeSpan.FromSeconds(10));

                using var pingResponse = await HttpClient.SendAsync(httpRequest, timeoutSource.Token).ConfigureAwait(false);
                if (pingResponse.IsSuccessStatusCode)
                {
                    return new { available = true, authenticated = false, message = "Server reached, but an API key is required." };
                }

                return new { available = false, authenticated = false, message = $"Server responded with HTTP {(int)pingResponse.StatusCode}." };
            }
            catch (Exception ex)
            {
                return new { available = false, authenticated = false, message = $"Connection failed: {ex.Message}" };
            }
        }

        /// <summary>
        /// Processes POST /Litefin/Seerr/Status/Ping to check if a remote server address is reachable.
        /// </summary>
        public async Task<object> Post(PingSeerrServerRequest request)
        {
            if (!this.IsUserAdmin())
            {
                throw new HttpException("Elevation required.") { StatusCode = HttpStatusCode.Forbidden };
            }

            if (request == null || !TryNormalizeUrl(request.SeerrUrl, out var baseUrl))
            {
                throw new HttpException("A valid Seerr URL is required.") { StatusCode = HttpStatusCode.BadRequest };
            }

            try
            {
                using var httpRequest = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/api/v1/status");
                httpRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

                using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(this.Request.CancellationToken);
                timeoutSource.CancelAfter(TimeSpan.FromSeconds(10));

                using var response = await HttpClient.SendAsync(httpRequest, timeoutSource.Token).ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                {
                    return new
                    {
                        reachable = true,
                        statusCode = (int)response.StatusCode,
                        message = "Seerr server is online and reachable.",
                    };
                }

                return new
                {
                    reachable = false,
                    statusCode = (int)response.StatusCode,
                    message = $"Server responded with HTTP {(int)response.StatusCode} ({response.ReasonPhrase}).",
                };
            }
            catch (Exception ex)
            {
                return new
                {
                    reachable = false,
                    message = $"Cannot reach server ({ex.Message}). Check IP, port, and network route.",
                };
            }
        }

        #endregion
        #region Admin Authentication


        /// <summary>
        /// Processes POST /Litefin/Seerr/Auth/Login to acquire the API key using administrator credentials.
        /// </summary>
        public async Task<object> Post(SeerrAdminLoginRequestDto request)
        {
            if (!this.IsUserAdmin())
            {
                throw new HttpException("Elevation required.") { StatusCode = HttpStatusCode.Forbidden };
            }

            if (request == null || !TryNormalizeUrl(request.SeerrUrl, out var baseUrl) || string.IsNullOrWhiteSpace(request.Username))
            {
                throw new HttpException("Seerr URL and Username are required.") { StatusCode = HttpStatusCode.BadRequest };
            }

            var password = request.Password ?? string.Empty;

            try
            {
                using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(this.Request.CancellationToken);
                timeoutSource.CancelAfter(RequestTimeout);

                // Acquire active CSRF credentials if CSRF protection is enabled on the target Seerr server
                var csrf = await this.EnsureCsrfTokenAsync(baseUrl, null, timeoutSource.Token).ConfigureAwait(false);

                HttpResponseMessage? authResponse = null;

                // 1. Try Jellyfin authentication endpoint if configured in Seerr
                try
                {
                    var jellyfinLoginPayload = JsonSerializer.Serialize(new
                    {
                        username = request.Username,
                        password = password,
                    });

                    using var jfRequest = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/api/v1/auth/jellyfin")
                    {
                        Content = new StringContent(jellyfinLoginPayload, Encoding.UTF8, "application/json"),
                    };
                    jfRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

                    // Attach CSRF cookies and verification tokens to prevent EBADCSRFTOKEN rejection
                    AttachCsrfHeaders(jfRequest, baseUrl, csrf);

                    var jfResponse = await HttpClient.SendAsync(jfRequest, timeoutSource.Token).ConfigureAwait(false);
                    if (jfResponse.IsSuccessStatusCode)
                    {
                        authResponse = jfResponse;
                    }
                }
                catch (Exception jfEx)
                {
                    this.Logger.Debug("Jellyfin auth attempt failed: {0}", jfEx.Message);
                }

                // 2. If Jellyfin auth did not succeed, fall back to local authentication endpoint
                if (authResponse == null)
                {
                    var localLoginPayload = JsonSerializer.Serialize(new
                    {
                        email = request.Username,
                        password = password,
                    });

                    using var localRequest = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/api/v1/auth/local")
                    {
                        Content = new StringContent(localLoginPayload, Encoding.UTF8, "application/json"),
                    };
                    localRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

                    // Attach CSRF cookies and verification tokens to prevent EBADCSRFTOKEN rejection
                    AttachCsrfHeaders(localRequest, baseUrl, csrf);

                    var localResponse = await HttpClient.SendAsync(localRequest, timeoutSource.Token).ConfigureAwait(false);
                    if (localResponse.IsSuccessStatusCode)
                    {
                        authResponse = localResponse;
                    }
                    else
                    {
                        return new SeerrAdminLoginResult
                        {
                            Success = false,
                            Message = "Invalid credentials. Verify your email or username and password.",
                        };
                    }
                }

                var cookieHeader = GetCookieHeader(authResponse);
                if (string.IsNullOrWhiteSpace(cookieHeader))
                {
                    return new SeerrAdminLoginResult
                    {
                        Success = false,
                        Message = "Authentication succeeded but session cookie was not returned.",
                    };
                }

                var apiKey = await this.FetchSeerrApiKeyAsync(baseUrl, cookieHeader!, timeoutSource.Token).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(apiKey))
                {
                    return new SeerrAdminLoginResult
                    {
                        Success = false,
                        Message = "Login successful, but this account lacks permission to read the server API key.",
                    };
                }

                SaveSeerrConfiguration(baseUrl, apiKey!);
                this.Logger.Info("Successfully authenticated and configured Seerr at {0}", baseUrl);

                return new SeerrAdminLoginResult
                {
                    Success = true,
                    Message = "Connected successfully! API key retrieved and saved.",
                };
            }
            catch (Exception ex)
            {
                this.Logger.Warn("Seerr login error: {0}", ex.Message);
                return new SeerrAdminLoginResult
                {
                    Success = false,
                    Message = $"Connection failed: {ex.Message}",
                };
            }
        }

        #endregion

        #region Discover Endpoints

        public Task<object> Get(GetSeerrTrendingRequest request)
        {
            var path = this.AppendEffectiveLanguage("/discover/trending", request?.Language);
            return this.ProxyGetAsync(path);
        }

        public Task<object> Get(GetSeerrMoviesRequest request)
        {
            var queryParams = new List<string>();
            if (!string.IsNullOrWhiteSpace(request.Genre))
            {
                queryParams.Add($"genre={Uri.EscapeDataString(request.Genre)}");
            }

            // Seerr reads `language` on /discover/movies|tv as the TMDB original-language filter, not the
            // display language. Forwarding the Accept-Language header or a UI locale such as "en-US" made
            // every result disappear, so only explicit ISO 639-1 filters ("ja", "ja|ko") pass through.
            var movieLangFilter = request.Language?.Trim();
            if (!string.IsNullOrWhiteSpace(movieLangFilter) && OriginalLanguageFilterRegex.IsMatch(movieLangFilter!))
            {
                queryParams.Add($"language={Uri.EscapeDataString(movieLangFilter!)}");
            }

            if (!string.IsNullOrWhiteSpace(request.Certification))
            {
                var country = !string.IsNullOrWhiteSpace(request.CertificationCountry) ? request.CertificationCountry : "US";
                queryParams.Add($"certificationCountry={Uri.EscapeDataString(country)}");
                queryParams.Add($"certification={Uri.EscapeDataString(request.Certification)}");
            }

            AppendCatalogFilters(queryParams, request.Keywords, request.KeywordIds, request.WatchProviders, request.WatchRegion);

            if (request.Studio.HasValue)
            {
                queryParams.Add($"studio={request.Studio.Value.ToString(CultureInfo.InvariantCulture)}");
            }

            if (!string.IsNullOrWhiteSpace(request.SortBy))
            {
                queryParams.Add($"sortBy={Uri.EscapeDataString(request.SortBy)}");
            }

            var basePath = queryParams.Count > 0
                ? $"/discover/movies?{string.Join("&", queryParams)}"
                : "/discover/movies";

            return this.ProxyGetMerged5PagesAsync(basePath, request.Page);
        }

        public Task<object> Get(GetSeerrMoviesByGenreRequest request)
        {
            var sortParam = !string.IsNullOrWhiteSpace(request.SortBy) ? $"&sortBy={Uri.EscapeDataString(request.SortBy)}" : string.Empty;
            var rawPath = $"/discover/movies?genre={request.GenreId.ToString(CultureInfo.InvariantCulture)}{sortParam}";
            var basePath = this.AppendEffectiveLanguage(rawPath, request.Language);
            return this.ProxyGetMerged5PagesAsync(basePath, request.Page);
        }

        public Task<object> Get(GetSeerrMoviesByKeywordRequest request)
        {
            var sortParam = !string.IsNullOrWhiteSpace(request.SortBy) ? $"&sortBy={Uri.EscapeDataString(request.SortBy)}" : string.Empty;
            var rawPath = $"/discover/movies?keywords={request.KeywordId.ToString(CultureInfo.InvariantCulture)}{sortParam}";
            var basePath = this.AppendEffectiveLanguage(rawPath, request.Language);
            return this.ProxyGetMerged5PagesAsync(basePath, request.Page);
        }

        public Task<object> Get(GetSeerrMoviesByStudioRequest request)
        {
            var rawPath = $"/discover/movies/studio/{request.StudioId.ToString(CultureInfo.InvariantCulture)}";
            var basePath = this.AppendEffectiveLanguage(rawPath, request.Language);
            return this.ProxyGetMerged5PagesAsync(basePath, request.Page);
        }

        public Task<object> Get(GetSeerrTvRequest request)
        {
            var queryParams = new List<string>();
            if (!string.IsNullOrWhiteSpace(request.Genre))
            {
                queryParams.Add($"genre={Uri.EscapeDataString(request.Genre)}");
            }

            // Seerr reads `language` on /discover/movies|tv as the TMDB original-language filter, not the
            // display language. Forwarding the Accept-Language header or a UI locale such as "en-US" made
            // every result disappear, so only explicit ISO 639-1 filters ("ja", "ja|ko") pass through.
            var tvLangFilter = request.Language?.Trim();
            if (!string.IsNullOrWhiteSpace(tvLangFilter) && OriginalLanguageFilterRegex.IsMatch(tvLangFilter!))
            {
                queryParams.Add($"language={Uri.EscapeDataString(tvLangFilter!)}");
            }

            if (!string.IsNullOrWhiteSpace(request.Certification))
            {
                var country = !string.IsNullOrWhiteSpace(request.CertificationCountry) ? request.CertificationCountry : "US";
                queryParams.Add($"certificationCountry={Uri.EscapeDataString(country)}");
                queryParams.Add($"certification={Uri.EscapeDataString(request.Certification)}");
            }

            AppendCatalogFilters(queryParams, request.Keywords, request.KeywordIds, request.WatchProviders, request.WatchRegion);

            if (request.Network.HasValue)
            {
                queryParams.Add($"network={request.Network.Value.ToString(CultureInfo.InvariantCulture)}");
            }

            if (!string.IsNullOrWhiteSpace(request.SortBy))
            {
                queryParams.Add($"sortBy={Uri.EscapeDataString(request.SortBy)}");
            }

            var basePath = queryParams.Count > 0
                ? $"/discover/tv?{string.Join("&", queryParams)}"
                : "/discover/tv";

            return this.ProxyGetMerged5PagesAsync(basePath, request.Page);
        }

        public Task<object> Get(GetSeerrTvByGenreRequest request)
        {
            var sortParam = !string.IsNullOrWhiteSpace(request.SortBy) ? $"&sortBy={Uri.EscapeDataString(request.SortBy)}" : string.Empty;
            var rawPath = $"/discover/tv?genre={request.GenreId.ToString(CultureInfo.InvariantCulture)}{sortParam}";
            var basePath = this.AppendEffectiveLanguage(rawPath, request.Language);
            return this.ProxyGetMerged5PagesAsync(basePath, request.Page);
        }

        public Task<object> Get(GetSeerrTvByKeywordRequest request)
        {
            var sortParam = !string.IsNullOrWhiteSpace(request.SortBy) ? $"&sortBy={Uri.EscapeDataString(request.SortBy)}" : string.Empty;
            var rawPath = $"/discover/tv?keywords={request.KeywordId.ToString(CultureInfo.InvariantCulture)}{sortParam}";
            var basePath = this.AppendEffectiveLanguage(rawPath, request.Language);
            return this.ProxyGetMerged5PagesAsync(basePath, request.Page);
        }

        public Task<object> Get(GetSeerrTvByNetworkRequest request)
        {
            var rawPath = $"/discover/tv/network/{request.NetworkId.ToString(CultureInfo.InvariantCulture)}";
            var basePath = this.AppendEffectiveLanguage(rawPath, request.Language);
            return this.ProxyGetMerged5PagesAsync(basePath, request.Page);
        }

        public Task<object> Get(GetSeerrUpcomingMoviesRequest request)
        {
            var basePath = this.AppendEffectiveLanguage("/discover/movies/upcoming", request.Language);
            return this.ProxyGetMerged5PagesAsync(basePath, request.Page);
        }

        public Task<object> Get(GetSeerrUpcomingTvRequest request)
        {
            var basePath = this.AppendEffectiveLanguage("/discover/tv/upcoming", request.Language);
            return this.ProxyGetMerged5PagesAsync(basePath, request.Page);
        }

        /// <summary>
        /// Gets the discover slider configuration (built-in and custom rows, order, enabled state) from Seerr.
        /// Seerr allows this for any authenticated user, so no admin permission is required.
        /// </summary>
        /// <param name="request">The discover settings request.</param>
        /// <returns>The Seerr response.</returns>
        public Task<object> Get(GetSeerrDiscoverSettingsRequest request)
            => this.ProxyGetAsync("/settings/discover");

        public Task<object> Get(GetSeerrGenreSliderMovieRequest request)
        {
            var path = this.AppendEffectiveLanguage("/discover/genreslider/movie", request?.Language);
            return this.ProxyGetAsync(path);
        }

        public Task<object> Get(GetSeerrGenreSliderTvRequest request)
        {
            var path = this.AppendEffectiveLanguage("/discover/genreslider/tv", request?.Language);
            return this.ProxyGetAsync(path);
        }

        #endregion

        #region Search, Details, and Filmography

        public Task<object> Get(SearchSeerrRequest request)
        {
            var rawPath = $"/search?query={Uri.EscapeDataString(request.Query ?? string.Empty)}&page={Math.Max(1, request.Page).ToString(CultureInfo.InvariantCulture)}";
            var path = this.AppendEffectiveLanguage(rawPath, request.Language);
            return this.ProxyGetAsync(path);
        }

        public Task<object> Get(GetSeerrCollectionDetailsRequest request)
        {
            var path = this.AppendEffectiveLanguage($"/collection/{request.CollectionId.ToString(CultureInfo.InvariantCulture)}", request.Language);
            return this.ProxyGetAsync(path);
        }

        public Task<object> Get(GetSeerrPersonDetailsRequest request)
            => this.ProxyGetAsync($"/person/{request.PersonId.ToString(CultureInfo.InvariantCulture)}");

        public Task<object> Get(GetSeerrPersonCombinedCreditsRequest request)
        {
            var path = this.AppendEffectiveLanguage($"/person/{request.PersonId.ToString(CultureInfo.InvariantCulture)}/combined_credits", request.Language);
            return this.ProxyGetAsync(path);
        }

        /// <summary>
        /// Serves a TMDB image through Seerr's image cache so clients without internet access can show Seerr artwork.
        /// Anonymous because image elements cannot send the Emby authorization header.
        /// </summary>
        /// <param name="request">The image request parameters.</param>
        /// <returns>The raw image stream result, or a redirect result to image.tmdb.org.</returns>
        public async Task<object> Get(GetSeerrImageRequest request)
        {
            if (!TryGetConfiguration(out var baseUrl, out _))
            {
                throw new HttpException("Seerr is not configured.") { StatusCode = HttpStatusCode.NotFound };
            }

            var rawPath = request?.Path;
            if (string.IsNullOrEmpty(rawPath))
            {
                var pathInfo = this.Request.PathInfo ?? string.Empty;
                const string prefix = "/Litefin/Seerr/Image/";
                var idx = pathInfo.IndexOf(prefix, StringComparison.OrdinalIgnoreCase);
                if (idx >= 0)
                {
                    rawPath = pathInfo.Substring(idx + prefix.Length);
                }
            }

            var segments = string.IsNullOrEmpty(rawPath) ? Array.Empty<string>() : rawPath!.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length == 0 || !segments.All(IsAllowedImagePathSegment))
            {
                throw new HttpException("Invalid image path.") { StatusCode = HttpStatusCode.BadRequest };
            }

            var imagePath = string.Join("/", segments);

            try
            {
                using var client = new HttpClient { Timeout = RequestTimeout, MaxResponseContentBufferSize = MaxImageBytes };
                using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(this.Request.CancellationToken);
                timeoutSource.CancelAfter(RequestTimeout);

                using var httpRequest = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/imageproxy/tmdb/{imagePath}");
                using var response = await client.SendAsync(httpRequest, timeoutSource.Token).ConfigureAwait(false);
                var contentType = response.Content.Headers.ContentType?.MediaType;

                if (response.IsSuccessStatusCode && contentType != null && AllowedImageContentTypes.TryGetValue(contentType, out var responseType))
                {
                    var bytes = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                    var responseHeaders = new Dictionary<string, string>
                    {
                        { "Cache-Control", response.Headers.CacheControl?.ToString() ?? "public, max-age=86400" },
                        { "X-Content-Type-Options", "nosniff" },
                    };
                    return this.ResultFactory.GetResult(this.Request, bytes, responseType, responseHeaders);
                }

                this.Logger.Debug(
                    "Seerr image proxy returned {0} ({1}) for {2}; redirecting to TMDB",
                    (int)response.StatusCode,
                    contentType,
                    imagePath);
            }
            catch (HttpRequestException ex)
            {
                this.Logger.Debug("Seerr image proxy request failed for {0}; redirecting to TMDB: {1}", imagePath, ex.Message);
            }
            catch (TaskCanceledException ex) when (!this.Request.CancellationToken.IsCancellationRequested)
            {
                this.Logger.Debug("Seerr image proxy request timed out for {0}; redirecting to TMDB: {1}", imagePath, ex.Message);
            }

            return this.ResultFactory.GetRedirectResult($"https://image.tmdb.org/{imagePath}");
        }

        public Task<object> Get(GetSeerrMovieDetailsRequest request)
        {
            var path = this.AppendEffectiveLanguage($"/movie/{request.TmdbId.ToString(CultureInfo.InvariantCulture)}", request.Language);
            return this.ProxyGetAsync(path);
        }

        public Task<object> Get(GetSeerrMovieSimilarRequest request)
        {
            var rawPath = $"/movie/{request.TmdbId.ToString(CultureInfo.InvariantCulture)}/similar?page={Math.Max(1, request.Page).ToString(CultureInfo.InvariantCulture)}";
            var path = this.AppendEffectiveLanguage(rawPath, request.Language);
            return this.ProxyGetAsync(path);
        }

        public Task<object> Get(GetSeerrMovieRecommendationsRequest request)
        {
            var rawPath = $"/movie/{request.TmdbId.ToString(CultureInfo.InvariantCulture)}/recommendations?page={Math.Max(1, request.Page).ToString(CultureInfo.InvariantCulture)}";
            var path = this.AppendEffectiveLanguage(rawPath, request.Language);
            return this.ProxyGetAsync(path);
        }

        public Task<object> Get(GetSeerrTvDetailsRequest request)
        {
            var path = this.AppendEffectiveLanguage($"/tv/{request.TmdbId.ToString(CultureInfo.InvariantCulture)}", request.Language);
            return this.ProxyGetAsync(path);
        }

        public Task<object> Get(GetSeerrTvSimilarRequest request)
        {
            var rawPath = $"/tv/{request.TmdbId.ToString(CultureInfo.InvariantCulture)}/similar?page={Math.Max(1, request.Page).ToString(CultureInfo.InvariantCulture)}";
            var path = this.AppendEffectiveLanguage(rawPath, request.Language);
            return this.ProxyGetAsync(path);
        }

        public Task<object> Get(GetSeerrTvRecommendationsRequest request)
        {
            var rawPath = $"/tv/{request.TmdbId.ToString(CultureInfo.InvariantCulture)}/recommendations?page={Math.Max(1, request.Page).ToString(CultureInfo.InvariantCulture)}";
            var path = this.AppendEffectiveLanguage(rawPath, request.Language);
            return this.ProxyGetAsync(path);
        }

        #endregion

        #region Requests, Media, Ratings, Services, Users, and Watchlist

        public async Task<object> Get(GetSeerrRequestsRequest request)
        {
            try
            {
                var userId = await this.ResolveAuthenticatedSeerrUserIdAsync().ConfigureAwait(false);
                if (!userId.HasValue)
                {
                    return new
                    {
                        page = 1,
                        totalPages = 1,
                        totalResults = 0,
                        results = Array.Empty<object>(),
                    };
                }

                var path = $"/request?take={Math.Max(1, request.Take).ToString(CultureInfo.InvariantCulture)}&skip={Math.Max(0, request.Skip).ToString(CultureInfo.InvariantCulture)}&filter={Uri.EscapeDataString(request.Filter)}&requestedBy={userId.Value.ToString(CultureInfo.InvariantCulture)}";
                return await this.ProxyAsync(HttpMethod.Get, path, null, userId.Value).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                this.Logger.Warn("Failed to retrieve Seerr requests for authenticated user: {0}", ex.Message);
                return new
                {
                    page = 1,
                    totalPages = 1,
                    totalResults = 0,
                    results = Array.Empty<object>(),
                };
            }
        }

        public async Task<object> Post(CreateSeerrRequestDto request)
        {
            if (request == null)
            {
                throw new HttpException("Request payload is required.") { StatusCode = HttpStatusCode.BadRequest };
            }

            var isTv = string.Equals(request.MediaType, "tv", StringComparison.OrdinalIgnoreCase);
            if (!string.Equals(request.MediaType, "movie", StringComparison.OrdinalIgnoreCase) && !isTv)
            {
                throw new HttpException("MediaType must be movie or tv.") { StatusCode = HttpStatusCode.BadRequest };
            }

            var seasons = request.Seasons?.Where(s => s > 0).Distinct().ToArray();
            if (isTv && (seasons == null || seasons.Length == 0))
            {
                throw new HttpException("At least one season is required for a television request.") { StatusCode = HttpStatusCode.BadRequest };
            }

            var seerrUserId = await this.ResolveAuthenticatedSeerrUserIdAsync().ConfigureAwait(false);
            if (!seerrUserId.HasValue)
            {
                throw new HttpException("The authenticated Emby user is not linked to a Seerr account.")
                {
                    StatusCode = HttpStatusCode.Forbidden,
                };
            }

            var payload = new Dictionary<string, object>
            {
                ["mediaType"] = isTv ? "tv" : "movie",
                ["mediaId"] = request.MediaId,
                ["is4k"] = request.Is4K,
            };

            if (request.ServerId.HasValue)
            {
                payload["serverId"] = request.ServerId.Value;
            }

            var usesAdvancedOptions = request.ServerId.HasValue
                || request.ProfileId.HasValue
                || !string.IsNullOrWhiteSpace(request.RootFolder)
                || request.LanguageProfileId.HasValue;

            if (usesAdvancedOptions
                && !await this.HasSeerrPermissionAsync(seerrUserId.Value, 8192, this.Request.CancellationToken).ConfigureAwait(false))
            {
                throw new HttpException("Advanced request permission is required.") { StatusCode = HttpStatusCode.Forbidden };
            }

            if (request.ProfileId.HasValue)
            {
                payload["profileId"] = request.ProfileId.Value;
            }

            if (!string.IsNullOrWhiteSpace(request.RootFolder))
            {
                payload["rootFolder"] = request.RootFolder!;
            }

            if (request.LanguageProfileId.HasValue)
            {
                payload["languageProfileId"] = request.LanguageProfileId.Value;
            }

            if (request.UserId.HasValue && request.UserId.Value != seerrUserId.Value)
            {
                payload["userId"] = request.UserId.Value;
            }

            if (isTv)
            {
                payload["seasons"] = seasons!;
            }

            return await this.ProxyAsync(HttpMethod.Post, "/request", payload, seerrUserId.Value).ConfigureAwait(false);
        }

        public async Task Delete(CancelSeerrRequestDto request)
        {
            var userId = await this.ResolveAuthenticatedSeerrUserIdAsync().ConfigureAwait(false);
            if (!userId.HasValue)
            {
                throw new HttpException("Forbidden.") { StatusCode = HttpStatusCode.Forbidden };
            }

            await this.ProxyAsync(HttpMethod.Delete, $"/request/{request.RequestId.ToString(CultureInfo.InvariantCulture)}", null, userId.Value).ConfigureAwait(false);
        }

        public Task<object> Get(GetSeerrRecentlyAddedRequest request)
            => this.ProxyGetAsync($"/media?filter=allavailable&sort=mediaAdded&take={Math.Max(1, request.Take).ToString(CultureInfo.InvariantCulture)}&skip={Math.Max(0, request.Skip).ToString(CultureInfo.InvariantCulture)}");

        public async Task<object> Get(GetSeerrRatingsRequest request)
        {
            var routeType = string.Equals(request.MediaType, "tv", StringComparison.OrdinalIgnoreCase) ? "tv" : "movie";
            var userId = await this.ResolveAuthenticatedSeerrUserIdAsync().ConfigureAwait(false);
            if (!userId.HasValue)
            {
                throw new HttpException("Forbidden.") { StatusCode = HttpStatusCode.Forbidden };
            }

            return await this.ProxyAsync(HttpMethod.Get, $"/{routeType}/{request.TmdbId.ToString(CultureInfo.InvariantCulture)}/ratingscombined", null, userId.Value).ConfigureAwait(false);
        }

        public async Task<object> Get(GetSeerrServicesRequest request)
        {
            var service = GetServiceName(request.MediaType);
            if (service == null)
            {
                throw new HttpException("MediaType must be movie or tv.") { StatusCode = HttpStatusCode.BadRequest };
            }

            var userId = await this.ResolveAuthenticatedSeerrUserIdAsync().ConfigureAwait(false);
            if (!userId.HasValue)
            {
                throw new HttpException("Forbidden.") { StatusCode = HttpStatusCode.Forbidden };
            }

            return await this.ProxyAsync(HttpMethod.Get, $"/service/{service}", null, userId.Value).ConfigureAwait(false);
        }

        public async Task<object> Get(GetSeerrServiceDetailsRequest request)
        {
            var service = GetServiceName(request.MediaType);
            if (service == null)
            {
                throw new HttpException("MediaType must be movie or tv.") { StatusCode = HttpStatusCode.BadRequest };
            }

            var userId = await this.ResolveAuthenticatedSeerrUserIdAsync().ConfigureAwait(false);
            if (!userId.HasValue)
            {
                throw new HttpException("Forbidden.") { StatusCode = HttpStatusCode.Forbidden };
            }

            return await this.ProxyAsync(HttpMethod.Get, $"/service/{service}/{request.ServerId.ToString(CultureInfo.InvariantCulture)}", null, userId.Value).ConfigureAwait(false);
        }

        public async Task<object> Get(GetSeerrUserCapabilitiesRequest request)
        {
            var userId = await this.ResolveAuthenticatedSeerrUserIdAsync().ConfigureAwait(false);
            if (!userId.HasValue)
            {
                throw new HttpException("Forbidden.") { StatusCode = HttpStatusCode.Forbidden };
            }

            using var response = await this.SendAsync(HttpMethod.Get, "/auth/me", null, this.Request.CancellationToken, userId.Value).ConfigureAwait(false);
            using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: this.Request.CancellationToken).ConfigureAwait(false);

            var permissions = document.RootElement.TryGetProperty("permissions", out var val) && val.TryGetInt32(out var mask) ? mask : 0;
            return new { permissions };
        }

        public Task<object> Get(GetSeerrUsersRequest request)
            => this.ProxyGetAsync($"/user?take={Math.Max(1, request.Take).ToString(CultureInfo.InvariantCulture)}&sort={Uri.EscapeDataString(request.Sort)}");

        public async Task<object> Get(GetSeerrWatchlistRequest request)
        {
            try
            {
                var userId = await this.ResolveAuthenticatedSeerrUserIdAsync().ConfigureAwait(false);
                if (!userId.HasValue)
                {
                    return new
                    {
                        page = 1,
                        totalPages = 1,
                        totalResults = 0,
                        results = Array.Empty<object>(),
                    };
                }

                var path = $"/user/{userId.Value.ToString(CultureInfo.InvariantCulture)}/watchlist?page={Math.Max(1, request.Page).ToString(CultureInfo.InvariantCulture)}";
                return await this.ProxyAsync(HttpMethod.Get, path, null, userId.Value).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                this.Logger.Warn("Failed to retrieve Seerr watchlist for authenticated user: {0}", ex.Message);
                return new
                {
                    page = 1,
                    totalPages = 1,
                    totalResults = 0,
                    results = Array.Empty<object>(),
                };
            }
        }

        public async Task<object> Post(AddToSeerrWatchlistRequest request)
        {
            var userId = await this.ResolveAuthenticatedSeerrUserIdAsync().ConfigureAwait(false);
            if (!userId.HasValue)
            {
                throw new HttpException("Forbidden.") { StatusCode = HttpStatusCode.Forbidden };
            }

            object? payload = null;
            if (!string.IsNullOrEmpty(request.MediaType) && (request.TmdbId.HasValue || request.MediaId.HasValue || request.TitleId.HasValue))
            {
                payload = new Dictionary<string, object>
                {
                    ["mediaType"] = request.MediaType!,
                    ["mediaId"] = request.MediaId ?? request.TmdbId ?? request.TitleId ?? 0,
                };
            }
            else
            {
                // Deserialize arbitrary JSON body if provided
                try
                {
                    using var reader = new StreamReader(this.Request.InputStream, Encoding.UTF8, true, 1024, true);
                    var raw = await reader.ReadToEndAsync().ConfigureAwait(false);
                    if (!string.IsNullOrWhiteSpace(raw))
                    {
                        payload = JsonSerializer.Deserialize<Dictionary<string, object>>(raw);
                    }
                }
                catch
                {
                    // Fall back to empty object
                }
            }

            return await this.ProxyAsync(HttpMethod.Post, "/watchlist", payload, userId.Value).ConfigureAwait(false);
        }

        public async Task Delete(RemoveFromSeerrWatchlistRequest request)
        {
            var normalizedMediaType = string.Equals(request.MediaType, "tv", StringComparison.OrdinalIgnoreCase) ? "tv" : "movie";
            if (GetServiceName(request.MediaType) == null)
            {
                throw new HttpException("MediaType must be movie or tv.") { StatusCode = HttpStatusCode.BadRequest };
            }

            var userId = await this.ResolveAuthenticatedSeerrUserIdAsync().ConfigureAwait(false);
            if (!userId.HasValue)
            {
                throw new HttpException("Forbidden.") { StatusCode = HttpStatusCode.Forbidden };
            }

            var path = $"/watchlist/{request.TmdbId.ToString(CultureInfo.InvariantCulture)}?mediaType={normalizedMediaType}";
            await this.ProxyAsync(HttpMethod.Delete, path, null, userId.Value).ConfigureAwait(false);
        }

        #endregion

        #region Core Proxy Infrastructure

        private async Task<object> ProxyGetAsync(string path)
        {
            // Resolve authenticated Seerr user identity to forward user locale and profile configuration
            var seerrUserId = await this.ResolveAuthenticatedSeerrUserIdAsync().ConfigureAwait(false);
            return await this.ProxyAsync(HttpMethod.Get, path, null, seerrUserId).ConfigureAwait(false);
        }

        private async Task<object> ProxyAsync(
            HttpMethod method,
            string path,
            object? body,
            int? seerrUserId = null)
        {
            if (!TryGetConfiguration(out _, out _))
            {
                throw new HttpException("Seerr is not configured.") { StatusCode = HttpStatusCode.ServiceUnavailable };
            }

            try
            {
                using var response = await this.SendAsync(method, path, body, this.Request.CancellationToken, seerrUserId).ConfigureAwait(false);
                var content = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                var contentType = response.Content.Headers.ContentType?.ToString() ?? "application/json";

                // Filter blocklisted content on successful GET JSON requests if non-admin
                if (response.IsSuccessStatusCode && method == HttpMethod.Get && contentType.Contains("json"))
                {
                    var canViewBlocklist = await this.CanUserViewBlocklistAsync(this.Request.CancellationToken).ConfigureAwait(false);
                    if (!canViewBlocklist)
                    {
                        var filtered = this.FilterBlocklistedContent(content);
                        if (filtered != null)
                        {
                            content = filtered;
                        }
                    }
                }

                this.Request.Response.ContentType = contentType;
                this.Request.Response.StatusCode = (int)response.StatusCode;
                return this.ResultFactory.GetResult(this.Request, new MemoryStream(Encoding.UTF8.GetBytes(content)), contentType);
            }
            catch (HttpException)
            {
                throw;
            }
            catch (Exception ex)
            {
                this.Logger.Warn("Seerr request failed for {0}: {1}", path, ex.Message);
                throw new HttpException("Unable to reach Seerr.") { StatusCode = HttpStatusCode.BadGateway };
            }
        }

        private async Task<object> ProxyGetMerged5PagesAsync(string pathWithoutPage, int litefinPage)
        {
            if (!TryGetConfiguration(out _, out _))
            {
                throw new HttpException("Seerr is not configured.") { StatusCode = HttpStatusCode.ServiceUnavailable };
            }

            var normalizedPage = Math.Max(1, litefinPage);
            var startSeerrPage = ((normalizedPage - 1) * 5) + 1;
            var separator = pathWithoutPage.Contains("?") ? "&" : "?";

            // Resolve authenticated Seerr user ID to ensure upstream queries preserve user locale and context
            var seerrUserId = await this.ResolveAuthenticatedSeerrUserIdAsync().ConfigureAwait(false);

            // Concurrently query 5 upstream Seerr pages to construct 100-item Litefin page
            var fetchTasks = Enumerable.Range(0, 5).Select(async offset =>
            {
                var targetSeerrPage = startSeerrPage + offset;
                var targetPath = $"{pathWithoutPage}{separator}page={targetSeerrPage.ToString(CultureInfo.InvariantCulture)}";
                try
                {
                    using var response = await this.SendAsync(HttpMethod.Get, targetPath, null, this.Request.CancellationToken, seerrUserId).ConfigureAwait(false);
                    if (!response.IsSuccessStatusCode)
                    {
                        return null;
                    }

                    using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
                    using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: this.Request.CancellationToken).ConfigureAwait(false);
                    return doc.RootElement.Clone();
                }
                catch (Exception ex)
                {
                    // Catch exceptions on individual page queries so transient upstream payload flaws do not abort the merged batch
                    this.Logger.Warn("Failed to fetch Seerr page {0} for path {1}: {2}", targetSeerrPage, pathWithoutPage, ex.Message);
                    return (JsonElement?)null;
                }
            });

            var results = await Task.WhenAll(fetchTasks).ConfigureAwait(false);
            var validDocs = results.Where(d => d.HasValue && d.Value.ValueKind == JsonValueKind.Object).Select(d => d!.Value).ToList();

            if (validDocs.Count == 0)
            {
                throw new HttpException("Unable to reach Seerr.") { StatusCode = HttpStatusCode.BadGateway };
            }

            var firstDoc = validDocs[0];
            var upstreamTotalPages = firstDoc.TryGetProperty("totalPages", out var tpProp) && tpProp.TryGetInt32(out var tpVal) ? tpVal : 1;
            var upstreamTotalResults = firstDoc.TryGetProperty("totalResults", out var trProp) && trProp.TryGetInt32(out var trVal) ? trVal : 0;

            // Determine if the current authenticated user has administrative blocklist visibility
            var canViewBlocklist = await this.CanUserViewBlocklistAsync(this.Request.CancellationToken).ConfigureAwait(false);
            var mergedResults = new List<JsonElement>();

            // Collect and merge results across upstream pages, filtering blocklisted titles if non-admin
            foreach (var doc in validDocs)
            {
                if (doc.TryGetProperty("results", out var resProp) && resProp.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in resProp.EnumerateArray())
                    {
                        if (canViewBlocklist || !IsBlocklisted(item))
                        {
                            mergedResults.Add(item.Clone());
                        }
                    }
                }
            }

            var mergedTotalPages = (int)Math.Ceiling(upstreamTotalPages / 5.0);

            var mergedPayload = new Dictionary<string, object>
            {
                ["page"] = normalizedPage,
                ["totalPages"] = Math.Max(1, mergedTotalPages),
                ["totalResults"] = upstreamTotalResults,
                ["results"] = mergedResults,
            };

            var jsonString = JsonSerializer.Serialize(mergedPayload);
            this.Request.Response.ContentType = "application/json";
            this.Request.Response.StatusCode = (int)HttpStatusCode.OK;
            return this.ResultFactory.GetResult(this.Request, new MemoryStream(Encoding.UTF8.GetBytes(jsonString)), "application/json");
        }

        private async Task<HttpResponseMessage> SendAsync(
            HttpMethod method,
            string path,
            object? body,
            CancellationToken cancellationToken,
            int? seerrUserId = null)
        {
            // Validate Plugin Configuration
            if (!TryGetConfiguration(out var baseUrl, out var apiKey))
            {
                throw new InvalidOperationException("Seerr is not configured.");
            }

            // Delegate to unified SendAsync implementation with resolved credentials
            return await this.SendAsync(method, path, body, baseUrl, apiKey, cancellationToken, seerrUserId).ConfigureAwait(false);
        }

        private async Task<HttpResponseMessage> SendAsync(
            HttpMethod method,
            string path,
            object? body,
            string baseUrl,
            string apiKey,
            CancellationToken cancellationToken,
            int? seerrUserId = null)
        {
            // -----------------------------------------------------------------
            // CSRF Protection Handling
            // -----------------------------------------------------------------
            // Mutating HTTP methods (POST, PUT, DELETE, PATCH) are inspected by Seerr's csurf middleware.
            // If "Enable CSRF Protection" is enabled in Seerr, these calls strictly require:
            // 1. The httpOnly secret cookie (_csrf)
            // 2. The readable CSRF verification token in header (X-CSRF-Token or X-XSRF-TOKEN)
            var isStateChanging = method != HttpMethod.Get && method != HttpMethod.Head && method != HttpMethod.Options;
            SeerrCsrfToken? csrf = null;

            if (isStateChanging)
            {
                // Ensure valid CSRF credentials exist before dispatching the mutating call
                csrf = await this.EnsureCsrfTokenAsync(baseUrl, apiKey, cancellationToken).ConfigureAwait(false);
            }

            // Local factory creating a clean HttpRequestMessage (and fresh content stream if retried)
            HttpRequestMessage CreateRequestMessage()
            {
                var req = new HttpRequestMessage(method, $"{baseUrl}/api/v1{path}");
                req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                req.Headers.Add("X-Api-Key", apiKey);

                // Forward targeted user identity if acting on behalf of a specific Seerr user account
                if (seerrUserId.HasValue)
                {
                    req.Headers.Add("X-Api-User", seerrUserId.Value.ToString(CultureInfo.InvariantCulture));
                }

                // Forward incoming client Accept-Language header to ensure upstream localization
                var rawAcceptLanguage = this.Request?.Headers?["Accept-Language"];
                if (!string.IsNullOrWhiteSpace(rawAcceptLanguage))
                {
                    req.Headers.TryAddWithoutValidation("Accept-Language", rawAcceptLanguage);
                }

                // Serialize JSON body payload when present
                if (body != null)
                {
                    req.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
                }

                // Attach CSRF cookies and token headers for mutating requests
                if (isStateChanging)
                {
                    AttachCsrfHeaders(req, baseUrl, csrf);
                }

                return req;
            }

            // Set up bounded execution timeout
            using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutSource.CancelAfter(RequestTimeout);

            // Execute initial HTTP request attempt
            using var request = CreateRequestMessage();
            var response = await HttpClient.SendAsync(request, timeoutSource.Token).ConfigureAwait(false);

            // Fully buffer response content into memory for inspection and subsequent stream consumption
            await response.Content.LoadIntoBufferAsync().ConfigureAwait(false);

            // -----------------------------------------------------------------
            // Handle CSRF Token Rejection & Automatic Retry
            // -----------------------------------------------------------------
            // If the upstream server returned 403 Forbidden with an invalid CSRF token message,
            // invalidate our cached credentials, fetch a fresh CSRF token, and retry the request once.
            if (isStateChanging && response.StatusCode == HttpStatusCode.Forbidden)
            {
                var responseContent = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                if (IsCsrfError(response.StatusCode, responseContent))
                {
                    this.Logger.Warn("Seerr rejected {0} {1} with invalid CSRF token. Refreshing CSRF credentials and retrying...", method, path);

                    // Force refresh CSRF credentials from upstream
                    csrf = await this.EnsureCsrfTokenAsync(baseUrl, apiKey, cancellationToken, forceRefresh: true).ConfigureAwait(false);

                    // Build a fresh request message with updated CSRF credentials
                    using var retryRequest = CreateRequestMessage();
                    var retryResponse = await HttpClient.SendAsync(retryRequest, timeoutSource.Token).ConfigureAwait(false);
                    await retryResponse.Content.LoadIntoBufferAsync().ConfigureAwait(false);

                    // Dispose the failed response and replace with the retry outcome
                    response.Dispose();
                    response = retryResponse;
                }
            }

            // Passively update our in-memory CSRF cache with any Set-Cookie headers returned
            UpdateCsrfCacheFromResponse(baseUrl, response);

            return response;
        }

        private async Task<int?> ResolveSeerrUserIdAsync(string userIdStr, string currentUsername, CancellationToken cancellationToken)
        {
            // -----------------------------------------------------------------
            // In-Memory Cache Lookup
            // -----------------------------------------------------------------
            // Return cached Seerr user id if resolved within the last 15 minutes
            if (!string.IsNullOrEmpty(userIdStr)
                && SeerrUserIdCache.TryGetValue(userIdStr, out var cached)
                && DateTime.UtcNow < cached.ExpiresAt)
            {
                return cached.UserId;
            }

            try
            {
                const int pageSize = 100;
                var normalizedUserId = userIdStr.Replace("-", string.Empty);

                for (var skip = 0; skip < 10000; skip += pageSize)
                {
                    var path = $"/user?take={pageSize.ToString(CultureInfo.InvariantCulture)}&skip={skip.ToString(CultureInfo.InvariantCulture)}";
                    using var response = await this.SendAsync(HttpMethod.Get, path, null, cancellationToken).ConfigureAwait(false);
                    if (!response.IsSuccessStatusCode)
                    {
                        this.Logger.Warn("Unable to resolve Seerr user: upstream returned {0}", (int)response.StatusCode);
                        return null;
                    }

                    using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
                    using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
                    var users = document.RootElement;
                    if (users.ValueKind == JsonValueKind.Object && users.TryGetProperty("results", out var results))
                    {
                        users = results;
                    }

                    if (users.ValueKind != JsonValueKind.Array)
                    {
                        return null;
                    }

                    foreach (var user in users.EnumerateArray())
                    {
                        if (!user.TryGetProperty("id", out var idProperty) || !idProperty.TryGetInt32(out var seerrUserId))
                        {
                            continue;
                        }

                        // ------------------------------------------------------------
                        // 1. Primary Match: embyUserId or jellyfinUserId
                        // ------------------------------------------------------------
                        if (user.TryGetProperty("embyUserId", out var embyIdProp)
                            && embyIdProp.ValueKind == JsonValueKind.String)
                        {
                            var candidate = embyIdProp.GetString()?.Replace("-", string.Empty);
                            if (candidate != null && string.Equals(candidate, normalizedUserId, StringComparison.OrdinalIgnoreCase))
                            {
                                if (!string.IsNullOrEmpty(userIdStr))
                                {
                                    SeerrUserIdCache[userIdStr] = (seerrUserId, DateTime.UtcNow.AddMinutes(15));
                                }

                                return seerrUserId;
                            }
                        }

                        if (user.TryGetProperty("jellyfinUserId", out var jellyfinIdProperty)
                            && jellyfinIdProperty.ValueKind == JsonValueKind.String)
                        {
                            var candidate = jellyfinIdProperty.GetString()?.Replace("-", string.Empty);
                            if (candidate != null && string.Equals(candidate, normalizedUserId, StringComparison.OrdinalIgnoreCase))
                            {
                                if (!string.IsNullOrEmpty(userIdStr))
                                {
                                    SeerrUserIdCache[userIdStr] = (seerrUserId, DateTime.UtcNow.AddMinutes(15));
                                }

                                return seerrUserId;
                            }
                        }

                        // ------------------------------------------------------------
                        // 2. Secondary Match: embyUsername, jellyfinUsername, or username
                        // ------------------------------------------------------------
                        if (!string.IsNullOrWhiteSpace(currentUsername))
                        {
                            if (user.TryGetProperty("embyUsername", out var embyUserProp)
                                && embyUserProp.ValueKind == JsonValueKind.String)
                            {
                                var u = embyUserProp.GetString();
                                if (!string.IsNullOrWhiteSpace(u) && string.Equals(u, currentUsername, StringComparison.OrdinalIgnoreCase))
                                {
                                    if (!string.IsNullOrEmpty(userIdStr))
                                    {
                                        SeerrUserIdCache[userIdStr] = (seerrUserId, DateTime.UtcNow.AddMinutes(15));
                                    }

                                    return seerrUserId;
                                }
                            }

                            if (user.TryGetProperty("jellyfinUsername", out var jfUserProp)
                                && jfUserProp.ValueKind == JsonValueKind.String)
                            {
                                var u = jfUserProp.GetString();
                                if (!string.IsNullOrWhiteSpace(u) && string.Equals(u, currentUsername, StringComparison.OrdinalIgnoreCase))
                                {
                                    if (!string.IsNullOrEmpty(userIdStr))
                                    {
                                        SeerrUserIdCache[userIdStr] = (seerrUserId, DateTime.UtcNow.AddMinutes(15));
                                    }

                                    return seerrUserId;
                                }
                            }

                            if (user.TryGetProperty("username", out var userProp)
                                && userProp.ValueKind == JsonValueKind.String)
                            {
                                var u = userProp.GetString();
                                if (!string.IsNullOrWhiteSpace(u) && string.Equals(u, currentUsername, StringComparison.OrdinalIgnoreCase))
                                {
                                    if (!string.IsNullOrEmpty(userIdStr))
                                    {
                                        SeerrUserIdCache[userIdStr] = (seerrUserId, DateTime.UtcNow.AddMinutes(15));
                                    }

                                    return seerrUserId;
                                }
                            }
                        }
                    }

                    if (users.GetArrayLength() < pageSize)
                    {
                        break;
                    }
                }

                // Cache negative lookup for 5 minutes so repeated misses don't flood upstream
                if (!string.IsNullOrEmpty(userIdStr))
                {
                    SeerrUserIdCache[userIdStr] = (null, DateTime.UtcNow.AddMinutes(5));
                }

                return null;
            }
            catch (Exception ex)
            {
                this.Logger.Warn("Failed to resolve Seerr user for user {0}: {1}", userIdStr, ex.Message);
                return null;
            }
        }

        private async Task<int?> ResolveAuthenticatedSeerrUserIdAsync()
        {
            try
            {
                var userIdStr = this.GetAuthenticatedUserIdString();
                var username = this.GetAuthenticatedUsername();
                return await this.ResolveSeerrUserIdAsync(userIdStr, username, this.Request.CancellationToken).ConfigureAwait(false);
            }
            catch
            {
                return null;
            }
        }

        private async Task<bool> HasSeerrPermissionAsync(int seerrUserId, int permission, CancellationToken cancellationToken)
        {
            using var response = await this.SendAsync(HttpMethod.Get, "/auth/me", null, cancellationToken, seerrUserId).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return false;
            }

            using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (!document.RootElement.TryGetProperty("permissions", out var value) || !value.TryGetInt32(out var permissions))
            {
                return false;
            }

            const int adminPermission = 2;
            return (permissions & adminPermission) != 0 || (permissions & permission) != 0;
        }

        private async Task<bool> CanUserViewBlocklistAsync(CancellationToken cancellationToken)
        {
            try
            {
                // -------------------------------------------------------------------------
                // 1. Emby Administrator Check
                // -------------------------------------------------------------------------
                if (this.IsUserAdmin())
                {
                    return true;
                }

                // -------------------------------------------------------------------------
                // 2. Memory Cache Lookup for Non-Admin Accounts
                // -------------------------------------------------------------------------
                var userIdStr = string.Empty;
                try
                {
                    userIdStr = this.GetAuthenticatedUserIdString();
                }
                catch
                {
                    return false;
                }

                if (!string.IsNullOrEmpty(userIdStr)
                    && BlocklistPermissionCache.TryGetValue(userIdStr, out var cached)
                    && DateTime.UtcNow < cached.ExpiresAt)
                {
                    return cached.CanView;
                }

                var canView = false;

                // -------------------------------------------------------------------------
                // 3. Upstream Seerr User Permission Evaluation
                // -------------------------------------------------------------------------
                var seerrUserId = await this.ResolveAuthenticatedSeerrUserIdAsync().ConfigureAwait(false);
                if (seerrUserId.HasValue)
                {
                    const int manageBlocklist = 268435456;
                    const int viewBlocklist = 1073741824;
                    canView = await this.HasSeerrPermissionAsync(seerrUserId.Value, manageBlocklist | viewBlocklist, cancellationToken).ConfigureAwait(false);
                }

                // -------------------------------------------------------------------------
                // 4. Cache Update
                // -------------------------------------------------------------------------
                if (!string.IsNullOrEmpty(userIdStr))
                {
                    BlocklistPermissionCache[userIdStr] = (canView, DateTime.UtcNow.AddMinutes(5));
                }

                return canView;
            }
            catch (Exception ex)
            {
                // Fail safely: non-admin mode hides blocklisted titles without throwing 500 error
                this.Logger.Warn("Failed to evaluate Seerr blocklist viewing permissions; defaulting to safe non-admin view: {0}", ex.Message);
                return false;
            }
        }

        private string? FilterBlocklistedContent(string content)
        {
            if (string.IsNullOrWhiteSpace(content))
            {
                return null;
            }

            try
            {
                using var doc = JsonDocument.Parse(content);
                var root = doc.RootElement;

                if (root.ValueKind == JsonValueKind.Object)
                {
                    // Single item direct detail check: if blocklisted, throw 404
                    if (IsBlocklisted(root))
                    {
                        throw new HttpException("Media item not found.") { StatusCode = HttpStatusCode.NotFound };
                    }

                    // Paged results array
                    if (root.TryGetProperty("results", out var resultsProp) && resultsProp.ValueKind == JsonValueKind.Array)
                    {
                        var filteredResults = new List<JsonElement>();
                        var modified = false;

                        foreach (var item in resultsProp.EnumerateArray())
                        {
                            if (IsBlocklisted(item))
                            {
                                modified = true;
                            }
                            else
                            {
                                filteredResults.Add(item.Clone());
                            }
                        }

                        if (modified)
                        {
                            var dict = new Dictionary<string, object>();
                            foreach (var prop in root.EnumerateObject())
                            {
                                if (prop.NameEquals("results"))
                                {
                                    dict[prop.Name] = filteredResults;
                                }
                                else
                                {
                                    dict[prop.Name] = prop.Value.Clone();
                                }
                            }

                            return JsonSerializer.Serialize(dict);
                        }
                    }

                    // Parts array in collections
                    if (root.TryGetProperty("parts", out var partsProp) && partsProp.ValueKind == JsonValueKind.Array)
                    {
                        var filteredParts = new List<JsonElement>();
                        var modified = false;

                        foreach (var part in partsProp.EnumerateArray())
                        {
                            if (IsBlocklisted(part))
                            {
                                modified = true;
                            }
                            else
                            {
                                filteredParts.Add(part.Clone());
                            }
                        }

                        if (modified)
                        {
                            var dict = new Dictionary<string, object>();
                            foreach (var prop in root.EnumerateObject())
                            {
                                if (prop.NameEquals("parts"))
                                {
                                    dict[prop.Name] = filteredParts;
                                }
                                else
                                {
                                    dict[prop.Name] = prop.Value.Clone();
                                }
                            }

                            return JsonSerializer.Serialize(dict);
                        }
                    }

                    // Cast and crew credits
                    if (root.TryGetProperty("cast", out var castProp) && castProp.ValueKind == JsonValueKind.Array)
                    {
                        var filteredCast = new List<JsonElement>();
                        var filteredCrew = new List<JsonElement>();
                        var modified = false;

                        foreach (var item in castProp.EnumerateArray())
                        {
                            if (IsBlocklisted(item))
                            {
                                modified = true;
                            }
                            else
                            {
                                filteredCast.Add(item.Clone());
                            }
                        }

                        if (root.TryGetProperty("crew", out var crewProp) && crewProp.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var item in crewProp.EnumerateArray())
                            {
                                if (IsBlocklisted(item))
                                {
                                    modified = true;
                                }
                                else
                                {
                                    filteredCrew.Add(item.Clone());
                                }
                            }
                        }

                        if (modified)
                        {
                            var dict = new Dictionary<string, object>();
                            foreach (var prop in root.EnumerateObject())
                            {
                                if (prop.NameEquals("cast"))
                                {
                                    dict[prop.Name] = filteredCast;
                                }
                                else if (prop.NameEquals("crew"))
                                {
                                    dict[prop.Name] = filteredCrew;
                                }
                                else
                                {
                                    dict[prop.Name] = prop.Value.Clone();
                                }
                            }

                            return JsonSerializer.Serialize(dict);
                        }
                    }

                    // Similar and recommendations sub-containers
                    var hasSimilar = false;
                    List<JsonElement>? filteredSimilar = null;
                    if (root.TryGetProperty("similar", out var similarProp))
                    {
                        hasSimilar = TryFilterResultsArray(similarProp, out filteredSimilar);
                    }

                    var hasRecs = false;
                    List<JsonElement>? filteredRecs = null;
                    if (root.TryGetProperty("recommendations", out var recsProp))
                    {
                        hasRecs = TryFilterResultsArray(recsProp, out filteredRecs);
                    }

                    if (hasSimilar || hasRecs)
                    {
                        var dict = new Dictionary<string, object>();
                        foreach (var prop in root.EnumerateObject())
                        {
                            if (prop.NameEquals("similar") && hasSimilar && filteredSimilar != null)
                            {
                                dict[prop.Name] = RebuildContainerWithResults(similarProp, filteredSimilar);
                            }
                            else if (prop.NameEquals("recommendations") && hasRecs && filteredRecs != null)
                            {
                                dict[prop.Name] = RebuildContainerWithResults(recsProp, filteredRecs);
                            }
                            else
                            {
                                dict[prop.Name] = prop.Value.Clone();
                            }
                        }

                        return JsonSerializer.Serialize(dict);
                    }
                }
                else if (root.ValueKind == JsonValueKind.Array)
                {
                    var filteredArray = new List<JsonElement>();
                    var modified = false;

                    foreach (var item in root.EnumerateArray())
                    {
                        if (IsBlocklisted(item))
                        {
                            modified = true;
                        }
                        else
                        {
                            filteredArray.Add(item.Clone());
                        }
                    }

                    if (modified)
                    {
                        return JsonSerializer.Serialize(filteredArray);
                    }
                }
            }
            catch (HttpException)
            {
                throw;
            }
            catch (Exception ex)
            {
                this.Logger.Warn("Error filtering blocklist content: {0}", ex.Message);
            }

            return null;
        }

        // =========================================================================
        // Catalog Filter Resolution Helper
        // =========================================================================
        // Appends keyword and watch provider filters shared by movie and tv discover routes.
        // The legacy single keywords identifier wins over keywordIds; malformed list values are
        // ignored so they never reach Seerr.
        private static void AppendCatalogFilters(
            List<string> queryParams,
            int? keywords,
            string? keywordIds,
            string? watchProviders,
            string? watchRegion)
        {
            // Prefer single keyword if explicitly supplied by legacy callers
            if (keywords.HasValue)
            {
                queryParams.Add($"keywords={keywords.Value.ToString(CultureInfo.InvariantCulture)}");
            }
            // Otherwise, validate comma- or pipe-separated keyword lists
            else if (!string.IsNullOrWhiteSpace(keywordIds) && NumericIdListRegex.IsMatch(keywordIds))
            {
                queryParams.Add($"keywords={Uri.EscapeDataString(keywordIds)}");
            }

            // TMDB only honours watch providers together with an ISO 3166-1 alpha-2 watch region
            if (!string.IsNullOrWhiteSpace(watchProviders)
                && NumericIdListRegex.IsMatch(watchProviders)
                && !string.IsNullOrWhiteSpace(watchRegion)
                && WatchRegionRegex.IsMatch(watchRegion))
            {
                queryParams.Add($"watchRegion={watchRegion}");
                queryParams.Add($"watchProviders={Uri.EscapeDataString(watchProviders)}");
            }
        }

        private static string? GetServiceName(string mediaType)
            => string.Equals(mediaType, "tv", StringComparison.OrdinalIgnoreCase) ? "sonarr" :
               string.Equals(mediaType, "movie", StringComparison.OrdinalIgnoreCase) ? "radarr" : null;

        private static bool TryGetConfiguration(out string baseUrl, out string apiKey)
        {
            var configuration = Plugin.Instance?.Configuration;
            return TryNormalizeConfiguration(configuration?.SeerrUrl, configuration?.SeerrApiKey, out baseUrl, out apiKey);
        }

        private static bool TryNormalizeUrl(string? configuredUrl, out string baseUrl)
        {
            baseUrl = configuredUrl?.Trim().TrimEnd('/') ?? string.Empty;
            return Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri)
                && (string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase));
        }

        private static bool TryNormalizeConfiguration(
            string? configuredUrl,
            string? configuredApiKey,
            out string baseUrl,
            out string apiKey)
        {
            apiKey = configuredApiKey?.Trim() ?? string.Empty;
            return TryNormalizeUrl(configuredUrl, out baseUrl) && !string.IsNullOrWhiteSpace(apiKey);
        }

        private static string? GetCookieHeader(HttpResponseMessage response)
        {
            if (response.Headers.TryGetValues("Set-Cookie", out var cookies))
            {
                var cookieList = cookies.Select(c => c.Split(';')[0].Trim()).Where(c => !string.IsNullOrWhiteSpace(c));
                return string.Join("; ", cookieList);
            }

            return null;
        }

        private static void SaveSeerrConfiguration(string baseUrl, string apiKey)
        {
            var plugin = Plugin.Instance;
            if (plugin != null)
            {
                plugin.Configuration.SeerrUrl = baseUrl;
                plugin.Configuration.SeerrApiKey = apiKey;
                plugin.SaveConfiguration();
            }
        }

        private static bool IsBlocklistedStatus(JsonElement prop)
        {
            if (prop.ValueKind == JsonValueKind.Number && prop.TryGetInt32(out var num))
            {
                return num == 6;
            }

            if (prop.ValueKind == JsonValueKind.String && int.TryParse(prop.GetString(), out var strNum))
            {
                return strNum == 6;
            }

            return false;
        }

        private static bool IsBlocklisted(JsonElement element)
        {
            if (element.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            if (element.TryGetProperty("mediaInfo", out var mediaInfo) && mediaInfo.ValueKind == JsonValueKind.Object)
            {
                if (mediaInfo.TryGetProperty("status", out var statusProp) && IsBlocklistedStatus(statusProp))
                {
                    return true;
                }

                if (mediaInfo.TryGetProperty("status4k", out var status4kProp) && IsBlocklistedStatus(status4kProp))
                {
                    return true;
                }
            }

            if (element.TryGetProperty("status", out var rootStatusProp) && IsBlocklistedStatus(rootStatusProp))
            {
                return true;
            }

            if (element.TryGetProperty("status4k", out var rootStatus4kProp) && IsBlocklistedStatus(rootStatus4kProp))
            {
                return true;
            }

            return false;
        }

        private static bool TryFilterResultsArray(JsonElement container, out List<JsonElement> filteredResults)
        {
            filteredResults = new List<JsonElement>();

            if (container.ValueKind != JsonValueKind.Object
                || !container.TryGetProperty("results", out var resProp)
                || resProp.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            var modified = false;
            foreach (var item in resProp.EnumerateArray())
            {
                if (IsBlocklisted(item))
                {
                    modified = true;
                }
                else
                {
                    filteredResults.Add(item.Clone());
                }
            }

            return modified;
        }

        private static Dictionary<string, object> RebuildContainerWithResults(JsonElement container, List<JsonElement> filteredResults)
        {
            var dict = new Dictionary<string, object>();
            foreach (var prop in container.EnumerateObject())
            {
                if (prop.NameEquals("results"))
                {
                    dict[prop.Name] = filteredResults;
                }
                else
                {
                    dict[prop.Name] = prop.Value.Clone();
                }
            }

            return dict;
        }

        private async Task<string?> FetchSeerrApiKeyAsync(string baseUrl, string cookieHeader, CancellationToken cancellationToken)
        {
            using var settingsRequest = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/api/v1/settings/main");
            settingsRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            settingsRequest.Headers.Add("Cookie", cookieHeader);

            using var settingsResponse = await HttpClient.SendAsync(settingsRequest, cancellationToken).ConfigureAwait(false);
            if (!settingsResponse.IsSuccessStatusCode)
            {
                return null;
            }

            using var stream = await settingsResponse.Content.ReadAsStreamAsync().ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);

            if (doc.RootElement.TryGetProperty("apiKey", out var apiKeyProp))
            {
                return apiKeyProp.GetString();
            }

            return null;
        }

        #endregion

        #region Seerr Helpers & CSRF Support

        /// <summary>
        /// Validates that an image path segment does not contain path traversal characters and matches allowed patterns.
        /// </summary>
        /// <param name="segment">The path segment to validate.</param>
        /// <returns>True if the segment is safe and valid; otherwise false.</returns>
        private static bool IsAllowedImagePathSegment(string segment)
            => segment != "." && segment != ".."
                && (ImagePathSegmentRegex.IsMatch(segment) || ImageDuotoneSegmentRegex.IsMatch(segment));

        /// <summary>
        /// Resolves the effective language tag to request from Seerr/TMDB.
        /// Prefers an explicit query parameter, falling back to the client's Accept-Language header.
        /// </summary>
        /// <param name="explicitLanguage">The explicit language parameter from query string.</param>
        /// <returns>A normalized language string (e.g. "de", "pt-BR", "es"), or null if unspecified.</returns>
        private string? ResolveEffectiveLanguage(string? explicitLanguage)
        {
            // 1. If explicit query parameter is provided, sanitize and return it
            if (!string.IsNullOrWhiteSpace(explicitLanguage))
            {
                return explicitLanguage!.Trim();
            }

            // 2. Fall back to client's Accept-Language HTTP header
            var rawAcceptLanguage = this.Request?.Headers?["Accept-Language"];
            if (!string.IsNullOrWhiteSpace(rawAcceptLanguage))
            {
                // Accept-Language can be a comma-separated list like "de,en;q=0.8" or "pt-BR,pt;q=0.9"
                var primary = rawAcceptLanguage!.Split(',')[0].Trim();

                // Strip any quality values (e.g. ";q=1.0")
                var clean = primary.Split(';')[0].Trim();
                if (!string.IsNullOrWhiteSpace(clean))
                {
                    var parts = clean.Split(LanguageDelimiters, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 2)
                    {
                        return $"{parts[0].ToLowerInvariant()}-{parts[1].ToUpperInvariant()}";
                    }

                    if (parts.Length == 1)
                    {
                        return parts[0].ToLowerInvariant();
                    }
                }
            }

            return null;
        }

        /// <summary>
        /// Appends the effective language parameter to an upstream Seerr path if not already present.
        /// </summary>
        /// <param name="path">The relative API path.</param>
        /// <param name="explicitLanguage">The optional explicit language parameter.</param>
        /// <returns>The path with the language parameter appended if resolved.</returns>
        private string AppendEffectiveLanguage(string path, string? explicitLanguage = null)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return path;
            }

            // Avoid adding duplicate language parameter if already in query string
            if (path.Contains("language=", StringComparison.OrdinalIgnoreCase))
            {
                return path;
            }

            var effectiveLanguage = this.ResolveEffectiveLanguage(explicitLanguage);
            if (string.IsNullOrWhiteSpace(effectiveLanguage))
            {
                return path;
            }

            var separator = path.Contains("?") ? "&" : "?";
            return $"{path}{separator}language={Uri.EscapeDataString(effectiveLanguage!)}";
        }

        /// <summary>
        /// Ensures that valid CSRF credentials exist for the specified Seerr instance.
        /// Probes Seerr's status endpoint if not cached or expired to seed the session cookies and token.
        /// </summary>
        /// <param name="baseUrl">The normalized base URL of the Seerr server.</param>
        /// <param name="apiKey">Optional API key to authenticate probe requests.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <param name="forceRefresh">Whether to bypass cache and force a new upstream probe.</param>
        /// <returns>The active CSRF credentials if CSRF protection is active; otherwise null.</returns>
        private async Task<SeerrCsrfToken?> EnsureCsrfTokenAsync(
            string baseUrl,
            string? apiKey,
            CancellationToken cancellationToken,
            bool forceRefresh = false)
        {
            // Check whether active unexpired credentials or a verified disabled status already exist in cache
            if (!forceRefresh && CsrfTokenCache.TryGetValue(baseUrl, out var cached) && cached != null && !cached.IsExpired)
            {
                // If CSRF was determined to be disabled on this instance, return null immediately without network calls
                return cached.IsEnabled ? cached : null;
            }

            try
            {
                // Probe Seerr's status endpoint to trigger the global csurf middleware
                using var probeRequest = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/api/v1/status");
                probeRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

                // Attach API key if available
                if (!string.IsNullOrWhiteSpace(apiKey))
                {
                    probeRequest.Headers.Add("X-Api-Key", apiKey);
                }

                // If an existing secret is known, pass it along so Seerr reuses the secret
                if (CsrfTokenCache.TryGetValue(baseUrl, out var existing) && existing != null && !string.IsNullOrWhiteSpace(existing.Secret))
                {
                    probeRequest.Headers.TryAddWithoutValidation("Cookie", $"_csrf={existing.Secret}");
                }

                // Bound timeout for quick response
                using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeoutSource.CancelAfter(TimeSpan.FromSeconds(10));

                // Send probe request
                using var response = await HttpClient.SendAsync(probeRequest, timeoutSource.Token).ConfigureAwait(false);

                // Extract CSRF secret and token from response headers
                if (TryExtractCsrfToken(response, out var secret, out var token))
                {
                    var tokenState = new SeerrCsrfToken(secret, token, DateTime.UtcNow.AddMinutes(30));
                    CsrfTokenCache[baseUrl] = tokenState;
                    return tokenState;
                }

                // Fallback: If status did not yield cookies and API key is present, probe /api/v1/auth/me
                if (!string.IsNullOrWhiteSpace(apiKey))
                {
                    using var authMeRequest = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/api/v1/auth/me");
                    authMeRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                    authMeRequest.Headers.Add("X-Api-Key", apiKey);

                    using var authMeTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    authMeTimeout.CancelAfter(TimeSpan.FromSeconds(10));

                    using var authMeResponse = await HttpClient.SendAsync(authMeRequest, authMeTimeout.Token).ConfigureAwait(false);

                    if (TryExtractCsrfToken(authMeResponse, out secret, out token))
                    {
                        var tokenState = new SeerrCsrfToken(secret, token, DateTime.UtcNow.AddMinutes(30));
                        CsrfTokenCache[baseUrl] = tokenState;
                        return tokenState;
                    }
                }
            }
            catch (Exception ex)
            {
                this.Logger.Debug("Failed to prefetch CSRF token from Seerr at {0}: {1}", baseUrl, ex.Message);
            }

            // If Seerr has CSRF protection disabled, no cookies will be returned.
            // Cache this disabled state for 30 minutes so subsequent calls bypass all probing with zero overhead.
            var disabledState = new SeerrCsrfToken(DateTime.UtcNow.AddMinutes(30));
            CsrfTokenCache[baseUrl] = disabledState;
            return null;
        }

        /// <summary>
        /// Parses a single cookie value from a Set-Cookie header string by cookie name.
        /// </summary>
        /// <param name="setCookieHeader">The raw Set-Cookie header string.</param>
        /// <param name="name">The name of the cookie to find.</param>
        /// <param name="value">The parsed cookie value if found.</param>
        /// <returns>True if the cookie was found and non-empty; otherwise false.</returns>
        private static bool TryReadSetCookie(string setCookieHeader, string name, out string value)
        {
            value = string.Empty;

            if (string.IsNullOrWhiteSpace(setCookieHeader) || string.IsNullOrWhiteSpace(name))
            {
                return false;
            }

            var prefix = name + "=";
            var startIndex = 0;

            if (!setCookieHeader.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                var idx = setCookieHeader.IndexOf(prefix, StringComparison.OrdinalIgnoreCase);
                if (idx < 0)
                {
                    return false;
                }

                if (idx > 0 && setCookieHeader[idx - 1] != ';' && setCookieHeader[idx - 1] != ' ' && setCookieHeader[idx - 1] != ',')
                {
                    return false;
                }

                startIndex = idx;
            }

            var raw = setCookieHeader.Substring(startIndex + prefix.Length);
            var semiIndex = raw.IndexOf(';');
            if (semiIndex >= 0)
            {
                raw = raw.Substring(0, semiIndex);
            }

            value = raw.Trim();
            return value.Length > 0;
        }

        /// <summary>
        /// Extracts the _csrf secret and XSRF-TOKEN verification token from an upstream HTTP response.
        /// </summary>
        /// <param name="response">The response message from Seerr.</param>
        /// <param name="secret">The extracted _csrf secret cookie value.</param>
        /// <param name="token">The extracted XSRF-TOKEN value.</param>
        /// <returns>True if both the secret and token were found; otherwise false.</returns>
        private static bool TryExtractCsrfToken(HttpResponseMessage response, out string secret, out string token)
        {
            secret = string.Empty;
            token = string.Empty;

            if (response.Headers.TryGetValues("Set-Cookie", out var setCookieHeaders))
            {
                foreach (var header in setCookieHeaders)
                {
                    if (TryReadSetCookie(header, "_csrf", out var s))
                    {
                        secret = s;
                    }
                    else if (TryReadSetCookie(header, "XSRF-TOKEN", out var t))
                    {
                        token = t;
                    }
                }
            }

            return !string.IsNullOrEmpty(secret) && !string.IsNullOrEmpty(token);
        }

        /// <summary>
        /// Passively updates the in-memory CSRF cache using any Set-Cookie headers present on incoming Seerr responses.
        /// </summary>
        /// <param name="baseUrl">The normalized base URL of the Seerr server.</param>
        /// <param name="response">The response received from Seerr.</param>
        private static void UpdateCsrfCacheFromResponse(string baseUrl, HttpResponseMessage response)
        {
            if (!response.Headers.TryGetValues("Set-Cookie", out var setCookieHeaders))
            {
                return;
            }

            string? newSecret = null;
            string? newToken = null;

            foreach (var header in setCookieHeaders)
            {
                if (TryReadSetCookie(header, "_csrf", out var s))
                {
                    newSecret = s;
                }
                else if (TryReadSetCookie(header, "XSRF-TOKEN", out var t))
                {
                    newToken = t;
                }
            }

            if (string.IsNullOrEmpty(newSecret) && string.IsNullOrEmpty(newToken))
            {
                return;
            }

            CsrfTokenCache.AddOrUpdate(
                baseUrl,
                _ =>
                {
                    if (!string.IsNullOrEmpty(newSecret) && !string.IsNullOrEmpty(newToken))
                    {
                        return new SeerrCsrfToken(newSecret!, newToken!, DateTime.UtcNow.AddMinutes(30));
                    }

                    return null!;
                },
                (_, existing) =>
                {
                    var effectiveSecret = !string.IsNullOrEmpty(newSecret) ? newSecret : existing?.Secret;
                    var effectiveToken = !string.IsNullOrEmpty(newToken) ? newToken : existing?.Token;

                    if (!string.IsNullOrEmpty(effectiveSecret) && !string.IsNullOrEmpty(effectiveToken))
                    {
                        return new SeerrCsrfToken(effectiveSecret!, effectiveToken!, DateTime.UtcNow.AddMinutes(30));
                    }

                    return existing!;
                });
        }

        /// <summary>
        /// Attaches the necessary CSRF cookie and headers to an outgoing request to satisfy Seerr's csurf middleware.
        /// </summary>
        /// <param name="request">The HTTP request message to enrich.</param>
        /// <param name="baseUrl">The base URL of the upstream Seerr server.</param>
        /// <param name="csrf">The active CSRF credentials, or null if CSRF protection is inactive.</param>
        private static void AttachCsrfHeaders(HttpRequestMessage request, string baseUrl, SeerrCsrfToken? csrf)
        {
            if (csrf == null || string.IsNullOrEmpty(csrf.Secret) || string.IsNullOrEmpty(csrf.Token))
            {
                return;
            }

            var existingCookie = request.Headers.Contains("Cookie")
                ? string.Join("; ", request.Headers.GetValues("Cookie"))
                : null;

            var csrfCookies = $"_csrf={csrf.Secret}; XSRF-TOKEN={csrf.Token}";
            var combinedCookie = string.IsNullOrEmpty(existingCookie)
                ? csrfCookies
                : $"{existingCookie}; {csrfCookies}";

            request.Headers.Remove("Cookie");
            request.Headers.TryAddWithoutValidation("Cookie", combinedCookie);

            request.Headers.TryAddWithoutValidation("X-CSRF-Token", csrf.Token);
            request.Headers.TryAddWithoutValidation("X-XSRF-TOKEN", csrf.Token);

            if (Uri.TryCreate(baseUrl, UriKind.Absolute, out var baseUri))
            {
                request.Headers.TryAddWithoutValidation("Origin", baseUri.GetLeftPart(UriPartial.Authority));
                request.Headers.TryAddWithoutValidation("Referer", baseUrl.TrimEnd('/') + "/");
            }
        }

        /// <summary>
        /// Evaluates whether an upstream HTTP response failed due to an invalid or missing CSRF token.
        /// </summary>
        /// <param name="statusCode">The HTTP response status code.</param>
        /// <param name="responseBody">The raw body string returned by the upstream server.</param>
        /// <returns>True if the failure is caused by CSRF rejection; otherwise false.</returns>
        private static bool IsCsrfError(HttpStatusCode statusCode, string? responseBody)
        {
            if (statusCode != HttpStatusCode.Forbidden)
            {
                return false;
            }

            if (string.IsNullOrWhiteSpace(responseBody))
            {
                return false;
            }

            return responseBody!.IndexOf("csrf", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>
        /// Holds cached CSRF credentials for communication with an upstream Seerr instance.
        /// </summary>
        private sealed class SeerrCsrfToken
        {
            /// <summary>
            /// Initializes a new instance of the <see cref="SeerrCsrfToken"/> class with active credentials.
            /// </summary>
            public SeerrCsrfToken(string secret, string token, DateTime expiresAt)
            {
                this.Secret = secret;
                this.Token = token;
                this.ExpiresAt = expiresAt;
                this.IsEnabled = true;
            }

            /// <summary>
            /// Initializes a new instance of the <see cref="SeerrCsrfToken"/> class indicating CSRF protection is disabled upstream.
            /// </summary>
            public SeerrCsrfToken(DateTime expiresAt)
            {
                this.Secret = string.Empty;
                this.Token = string.Empty;
                this.ExpiresAt = expiresAt;
                this.IsEnabled = false;
            }

            /// <summary>
            /// Gets a value indicating whether CSRF protection is active on upstream.
            /// </summary>
            public bool IsEnabled { get; }

            /// <summary>
            /// Gets the secret value stored in the _csrf cookie.
            /// </summary>
            public string Secret { get; }

            /// <summary>
            /// Gets the verification token passed via the X-CSRF-Token or X-XSRF-TOKEN header.
            /// </summary>
            public string Token { get; }

            /// <summary>
            /// Gets the cache expiration timestamp in UTC.
            /// </summary>
            public DateTime ExpiresAt { get; }

            /// <summary>
            /// Gets a value indicating whether this cached token is expired.
            /// </summary>
            public bool IsExpired => DateTime.UtcNow >= this.ExpiresAt;
        }

        #endregion
    }
}
