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

        // Persistent shared HttpClient instance configured for robust upstream communication
        private static readonly HttpClient HttpClient = new HttpClient
        {
            Timeout = RequestTimeout,
        };

        // In-memory cache holding non-admin blocklist visibility permissions
        // Keyed by user identifier string to avoid repeated upstream network roundtrips
        private static readonly ConcurrentDictionary<string, (bool CanView, DateTime ExpiresAt)> BlocklistPermissionCache =
            new ConcurrentDictionary<string, (bool CanView, DateTime ExpiresAt)>(StringComparer.OrdinalIgnoreCase);

        #region Status and Diagnostic Endpoints

        /// <summary>
        /// Processes GET /Litefin/Seerr/Status to verify if Seerr is configured and responsive.
        /// </summary>
        public async Task<object> Get(GetSeerrStatusRequest request)
        {
            if (!TryGetConfiguration(out _, out _))
            {
                return new { configured = false, available = false };
            }

            try
            {
                using var response = await this.SendAsync(HttpMethod.Get, "/auth/me", null, this.Request.CancellationToken).ConfigureAwait(false);
                return new { configured = true, available = response.IsSuccessStatusCode };
            }
            catch (Exception ex)
            {
                this.Logger.Warn("Unable to reach configured Seerr instance: {0}", ex.Message);
                return new { configured = true, available = false };
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
            => this.ProxyGetAsync("/discover/trending");

        public Task<object> Get(GetSeerrMoviesRequest request)
        {
            var queryParams = new List<string>();
            if (!string.IsNullOrWhiteSpace(request.Genre))
            {
                queryParams.Add($"genre={Uri.EscapeDataString(request.Genre)}");
            }

            if (!string.IsNullOrWhiteSpace(request.Language))
            {
                queryParams.Add($"language={Uri.EscapeDataString(request.Language)}");
            }

            if (!string.IsNullOrWhiteSpace(request.Certification))
            {
                var country = !string.IsNullOrWhiteSpace(request.CertificationCountry) ? request.CertificationCountry : "US";
                queryParams.Add($"certificationCountry={Uri.EscapeDataString(country)}");
                queryParams.Add($"certification={Uri.EscapeDataString(request.Certification)}");
            }

            if (request.Keywords.HasValue)
            {
                queryParams.Add($"keywords={request.Keywords.Value.ToString(CultureInfo.InvariantCulture)}");
            }

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
            return this.ProxyGetMerged5PagesAsync($"/discover/movies?genre={request.GenreId.ToString(CultureInfo.InvariantCulture)}{sortParam}", request.Page);
        }

        public Task<object> Get(GetSeerrMoviesByKeywordRequest request)
        {
            var sortParam = !string.IsNullOrWhiteSpace(request.SortBy) ? $"&sortBy={Uri.EscapeDataString(request.SortBy)}" : string.Empty;
            return this.ProxyGetMerged5PagesAsync($"/discover/movies?keywords={request.KeywordId.ToString(CultureInfo.InvariantCulture)}{sortParam}", request.Page);
        }

        public Task<object> Get(GetSeerrMoviesByStudioRequest request)
            => this.ProxyGetMerged5PagesAsync($"/discover/movies/studio/{request.StudioId.ToString(CultureInfo.InvariantCulture)}", request.Page);

        public Task<object> Get(GetSeerrTvRequest request)
        {
            var queryParams = new List<string>();
            if (!string.IsNullOrWhiteSpace(request.Genre))
            {
                queryParams.Add($"genre={Uri.EscapeDataString(request.Genre)}");
            }

            if (!string.IsNullOrWhiteSpace(request.Language))
            {
                queryParams.Add($"language={Uri.EscapeDataString(request.Language)}");
            }

            if (!string.IsNullOrWhiteSpace(request.Certification))
            {
                var country = !string.IsNullOrWhiteSpace(request.CertificationCountry) ? request.CertificationCountry : "US";
                queryParams.Add($"certificationCountry={Uri.EscapeDataString(country)}");
                queryParams.Add($"certification={Uri.EscapeDataString(request.Certification)}");
            }

            if (request.Keywords.HasValue)
            {
                queryParams.Add($"keywords={request.Keywords.Value.ToString(CultureInfo.InvariantCulture)}");
            }

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
            return this.ProxyGetMerged5PagesAsync($"/discover/tv?genre={request.GenreId.ToString(CultureInfo.InvariantCulture)}{sortParam}", request.Page);
        }

        public Task<object> Get(GetSeerrTvByKeywordRequest request)
        {
            var sortParam = !string.IsNullOrWhiteSpace(request.SortBy) ? $"&sortBy={Uri.EscapeDataString(request.SortBy)}" : string.Empty;
            return this.ProxyGetMerged5PagesAsync($"/discover/tv?keywords={request.KeywordId.ToString(CultureInfo.InvariantCulture)}{sortParam}", request.Page);
        }

        public Task<object> Get(GetSeerrTvByNetworkRequest request)
            => this.ProxyGetMerged5PagesAsync($"/discover/tv/network/{request.NetworkId.ToString(CultureInfo.InvariantCulture)}", request.Page);

        public Task<object> Get(GetSeerrUpcomingMoviesRequest request)
            => this.ProxyGetMerged5PagesAsync("/discover/movies/upcoming", request.Page);

        public Task<object> Get(GetSeerrUpcomingTvRequest request)
            => this.ProxyGetMerged5PagesAsync("/discover/tv/upcoming", request.Page);

        public Task<object> Get(GetSeerrGenreSliderMovieRequest request)
            => this.ProxyGetAsync("/discover/genreslider/movie");

        public Task<object> Get(GetSeerrGenreSliderTvRequest request)
            => this.ProxyGetAsync("/discover/genreslider/tv");

        #endregion

        #region Search, Details, and Filmography

        public Task<object> Get(SearchSeerrRequest request)
        {
            var path = $"/search?query={Uri.EscapeDataString(request.Query ?? string.Empty)}&page={Math.Max(1, request.Page).ToString(CultureInfo.InvariantCulture)}";
            return this.ProxyGetAsync(path);
        }

        public Task<object> Get(GetSeerrCollectionDetailsRequest request)
            => this.ProxyGetAsync($"/collection/{request.CollectionId.ToString(CultureInfo.InvariantCulture)}");

        public Task<object> Get(GetSeerrPersonDetailsRequest request)
            => this.ProxyGetAsync($"/person/{request.PersonId.ToString(CultureInfo.InvariantCulture)}");

        public Task<object> Get(GetSeerrPersonCombinedCreditsRequest request)
            => this.ProxyGetAsync($"/person/{request.PersonId.ToString(CultureInfo.InvariantCulture)}/combined_credits");

        public Task<object> Get(GetSeerrMovieDetailsRequest request)
            => this.ProxyGetAsync($"/movie/{request.TmdbId.ToString(CultureInfo.InvariantCulture)}");

        public Task<object> Get(GetSeerrMovieSimilarRequest request)
            => this.ProxyGetAsync($"/movie/{request.TmdbId.ToString(CultureInfo.InvariantCulture)}/similar?page={Math.Max(1, request.Page).ToString(CultureInfo.InvariantCulture)}");

        public Task<object> Get(GetSeerrMovieRecommendationsRequest request)
            => this.ProxyGetAsync($"/movie/{request.TmdbId.ToString(CultureInfo.InvariantCulture)}/recommendations?page={Math.Max(1, request.Page).ToString(CultureInfo.InvariantCulture)}");

        public Task<object> Get(GetSeerrTvDetailsRequest request)
            => this.ProxyGetAsync($"/tv/{request.TmdbId.ToString(CultureInfo.InvariantCulture)}");

        public Task<object> Get(GetSeerrTvSimilarRequest request)
            => this.ProxyGetAsync($"/tv/{request.TmdbId.ToString(CultureInfo.InvariantCulture)}/similar?page={Math.Max(1, request.Page).ToString(CultureInfo.InvariantCulture)}");

        public Task<object> Get(GetSeerrTvRecommendationsRequest request)
            => this.ProxyGetAsync($"/tv/{request.TmdbId.ToString(CultureInfo.InvariantCulture)}/recommendations?page={Math.Max(1, request.Page).ToString(CultureInfo.InvariantCulture)}");

        #endregion

        #region Requests, Media, Ratings, Services, Users, and Watchlist

        public async Task<object> Get(GetSeerrRequestsRequest request)
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
            var userId = await this.ResolveAuthenticatedSeerrUserIdAsync().ConfigureAwait(false);
            if (!userId.HasValue)
            {
                throw new HttpException("Forbidden.") { StatusCode = HttpStatusCode.Forbidden };
            }

            var path = $"/user/{userId.Value.ToString(CultureInfo.InvariantCulture)}/watchlist?page={Math.Max(1, request.Page).ToString(CultureInfo.InvariantCulture)}";
            return await this.ProxyAsync(HttpMethod.Get, path, null, userId.Value).ConfigureAwait(false);
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

        private Task<object> ProxyGetAsync(string path)
            => this.ProxyAsync(HttpMethod.Get, path, null);

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

            var fetchTasks = Enumerable.Range(0, 5).Select(async offset =>
            {
                var targetSeerrPage = startSeerrPage + offset;
                var targetPath = $"{pathWithoutPage}{separator}page={targetSeerrPage.ToString(CultureInfo.InvariantCulture)}";
                try
                {
                    using var response = await this.SendAsync(HttpMethod.Get, targetPath, null, this.Request.CancellationToken).ConfigureAwait(false);
                    if (!response.IsSuccessStatusCode)
                    {
                        return null;
                    }

                    using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
                    using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: this.Request.CancellationToken).ConfigureAwait(false);
                    return doc.RootElement.Clone();
                }
                catch
                {
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

            var canViewBlocklist = await this.CanUserViewBlocklistAsync(this.Request.CancellationToken).ConfigureAwait(false);
            var mergedResults = new List<JsonElement>();

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
            if (!TryGetConfiguration(out var baseUrl, out var apiKey))
            {
                throw new InvalidOperationException("Seerr is not configured.");
            }

            using var request = new HttpRequestMessage(method, $"{baseUrl}/api/v1{path}");
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Headers.Add("X-Api-Key", apiKey);

            if (seerrUserId.HasValue)
            {
                request.Headers.Add("X-Api-User", seerrUserId.Value.ToString(CultureInfo.InvariantCulture));
            }

            if (body != null)
            {
                request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
            }

            using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutSource.CancelAfter(RequestTimeout);
            return await HttpClient.SendAsync(request, timeoutSource.Token).ConfigureAwait(false);
        }

        private async Task<HttpResponseMessage> SendAsync(
            HttpMethod method,
            string path,
            object? body,
            string baseUrl,
            string apiKey,
            CancellationToken cancellationToken)
        {
            using var request = new HttpRequestMessage(method, $"{baseUrl}/api/v1{path}");
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Headers.Add("X-Api-Key", apiKey);

            if (body != null)
            {
                request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
            }

            using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutSource.CancelAfter(RequestTimeout);
            return await HttpClient.SendAsync(request, timeoutSource.Token).ConfigureAwait(false);
        }

        private async Task<int?> ResolveSeerrUserIdAsync(string userIdStr, string currentUsername, CancellationToken cancellationToken)
        {
            const int pageSize = 100;
            var normalizedUserId = userIdStr.Replace("-", string.Empty);

            for (var skip = 0; skip < 10000; skip += pageSize)
            {
                var path = $"/user?take={pageSize.ToString(CultureInfo.InvariantCulture)}&skip={skip.ToString(CultureInfo.InvariantCulture)}";
                using var response = await this.SendAsync(HttpMethod.Get, path, null, cancellationToken).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
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

                    // 1. Primary Match: embyUserId or jellyfinUserId
                    if (user.TryGetProperty("embyUserId", out var embyIdProp))
                    {
                        var candidate = embyIdProp.GetString()?.Replace("-", string.Empty);
                        if (candidate != null && string.Equals(candidate, normalizedUserId, StringComparison.OrdinalIgnoreCase))
                        {
                            return seerrUserId;
                        }
                    }

                    if (user.TryGetProperty("jellyfinUserId", out var jellyfinIdProperty))
                    {
                        var candidate = jellyfinIdProperty.GetString()?.Replace("-", string.Empty);
                        if (candidate != null && string.Equals(candidate, normalizedUserId, StringComparison.OrdinalIgnoreCase))
                        {
                            return seerrUserId;
                        }
                    }

                    // 2. Secondary Match: embyUsername, jellyfinUsername, or username
                    if (!string.IsNullOrWhiteSpace(currentUsername))
                    {
                        if (user.TryGetProperty("embyUsername", out var embyUserProp))
                        {
                            var u = embyUserProp.GetString();
                            if (!string.IsNullOrWhiteSpace(u) && string.Equals(u, currentUsername, StringComparison.OrdinalIgnoreCase))
                            {
                                return seerrUserId;
                            }
                        }

                        if (user.TryGetProperty("jellyfinUsername", out var jfUserProp))
                        {
                            var u = jfUserProp.GetString();
                            if (!string.IsNullOrWhiteSpace(u) && string.Equals(u, currentUsername, StringComparison.OrdinalIgnoreCase))
                            {
                                return seerrUserId;
                            }
                        }

                        if (user.TryGetProperty("username", out var userProp))
                        {
                            var u = userProp.GetString();
                            if (!string.IsNullOrWhiteSpace(u) && string.Equals(u, currentUsername, StringComparison.OrdinalIgnoreCase))
                            {
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

            return null;
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
            // Administrator check on Emby
            if (this.IsUserAdmin())
            {
                return true;
            }

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
            var seerrUserId = await this.ResolveAuthenticatedSeerrUserIdAsync().ConfigureAwait(false);
            if (seerrUserId.HasValue)
            {
                const int manageBlocklist = 268435456;
                const int viewBlocklist = 1073741824;
                canView = await this.HasSeerrPermissionAsync(seerrUserId.Value, manageBlocklist | viewBlocklist, cancellationToken).ConfigureAwait(false);
            }

            if (!string.IsNullOrEmpty(userIdStr))
            {
                BlocklistPermissionCache[userIdStr] = (canView, DateTime.UtcNow.AddMinutes(5));
            }

            return canView;
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
    }
}
