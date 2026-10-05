// <copyright file="SeerrRequests.cs" company="Litefin">
// Copyright (c) Litefin. All rights reserved.
// </copyright>

namespace Litefin.Emby.Plugin.RestApi.Requests
{
    using Litefin.Emby.Plugin.Models;
    using MediaBrowser.Controller.Net;
    using MediaBrowser.Model.Services;

    #region Status and Auth Requests

    /// <summary>
    /// Checks whether Seerr is configured and reachable.
    /// </summary>
    [Route("/Litefin/Seerr/Status", "GET", Summary = "Reports whether Seerr is configured and reachable")]
    [Authenticated]
    public class GetSeerrStatusRequest : IReturn<object>
    {
    }

    /// <summary>
    /// Tests temporary Seerr settings without persisting them.
    /// </summary>
    [Route("/Litefin/Seerr/Status/Test", "POST", Summary = "Tests temporary Seerr connection settings")]
    [Authenticated]
    public class TestSeerrConnectionRequest : SeerrConnectionTestRequest, IReturn<object>
    {
    }

    /// <summary>
    /// Pings the Seerr server to check reachability.
    /// </summary>
    [Route("/Litefin/Seerr/Status/Ping", "POST", Summary = "Pings the Seerr server for reachability")]
    [Authenticated]
    public class PingSeerrServerRequest : SeerrServerTestRequest, IReturn<object>
    {
    }



    /// <summary>
    /// Authenticates with Seerr using administrator credentials to obtain the API key.
    /// </summary>
    [Route("/Litefin/Seerr/Auth/Login", "POST", Summary = "Logs in to Seerr using credentials")]
    [Authenticated]
    public class SeerrAdminLoginRequestDto : SeerrAdminLoginRequest, IReturn<SeerrAdminLoginResult>
    {
    }

    #endregion

    #region Discover Requests

    /// <summary>
    /// Retrieves trending media from Seerr.
    /// </summary>
    [Route("/Litefin/Seerr/Discover/Trending", "GET", Summary = "Gets trending media from Seerr")]
    [Authenticated]
    public class GetSeerrTrendingRequest : IReturn<object>
    {
    }

    /// <summary>
    /// Retrieves popular movies from Seerr with optional filtering, merging 5 upstream pages.
    /// </summary>
    [Route("/Litefin/Seerr/Discover/Movies", "GET", Summary = "Gets popular movies from Seerr")]
    [Authenticated]
    public class GetSeerrMoviesRequest : IReturn<object>
    {
        public int Page { get; set; } = 1;

        public string? Genre { get; set; }

        public string? Language { get; set; }

        public string? Certification { get; set; }

        public string? CertificationCountry { get; set; }

        public int? Keywords { get; set; }

        public int? Studio { get; set; }

        public string? SortBy { get; set; }
    }

    /// <summary>
    /// Retrieves movies by genre from Seerr.
    /// </summary>
    [Route("/Litefin/Seerr/Discover/Movies/Genre/{GenreId}", "GET", Summary = "Gets movies by genre from Seerr")]
    [Authenticated]
    public class GetSeerrMoviesByGenreRequest : IReturn<object>
    {
        public int GenreId { get; set; }

        public int Page { get; set; } = 1;

        public string? SortBy { get; set; }
    }

    /// <summary>
    /// Retrieves movies by keyword from Seerr.
    /// </summary>
    [Route("/Litefin/Seerr/Discover/Movies/Keyword/{KeywordId}", "GET", Summary = "Gets movies by keyword from Seerr")]
    [Authenticated]
    public class GetSeerrMoviesByKeywordRequest : IReturn<object>
    {
        public int KeywordId { get; set; }

        public int Page { get; set; } = 1;

        public string? SortBy { get; set; }
    }

    /// <summary>
    /// Retrieves movies by studio from Seerr.
    /// </summary>
    [Route("/Litefin/Seerr/Discover/Movies/Studio/{StudioId}", "GET", Summary = "Gets movies by studio from Seerr")]
    [Authenticated]
    public class GetSeerrMoviesByStudioRequest : IReturn<object>
    {
        public int StudioId { get; set; }

        public int Page { get; set; } = 1;
    }

    /// <summary>
    /// Retrieves popular series from Seerr with optional filtering, merging 5 upstream pages.
    /// </summary>
    [Route("/Litefin/Seerr/Discover/Tv", "GET", Summary = "Gets popular TV series from Seerr")]
    [Authenticated]
    public class GetSeerrTvRequest : IReturn<object>
    {
        public int Page { get; set; } = 1;

        public string? Genre { get; set; }

        public string? Language { get; set; }

        public string? Certification { get; set; }

        public string? CertificationCountry { get; set; }

        public int? Keywords { get; set; }

        public int? Network { get; set; }

        public string? SortBy { get; set; }
    }

    /// <summary>
    /// Retrieves television series by genre from Seerr.
    /// </summary>
    [Route("/Litefin/Seerr/Discover/Tv/Genre/{GenreId}", "GET", Summary = "Gets TV series by genre from Seerr")]
    [Authenticated]
    public class GetSeerrTvByGenreRequest : IReturn<object>
    {
        public int GenreId { get; set; }

        public int Page { get; set; } = 1;

        public string? SortBy { get; set; }
    }

    /// <summary>
    /// Retrieves television series by keyword from Seerr.
    /// </summary>
    [Route("/Litefin/Seerr/Discover/Tv/Keyword/{KeywordId}", "GET", Summary = "Gets TV series by keyword from Seerr")]
    [Authenticated]
    public class GetSeerrTvByKeywordRequest : IReturn<object>
    {
        public int KeywordId { get; set; }

        public int Page { get; set; } = 1;

        public string? SortBy { get; set; }
    }

    /// <summary>
    /// Retrieves television series by network from Seerr.
    /// </summary>
    [Route("/Litefin/Seerr/Discover/Tv/Network/{NetworkId}", "GET", Summary = "Gets TV series by network from Seerr")]
    [Authenticated]
    public class GetSeerrTvByNetworkRequest : IReturn<object>
    {
        public int NetworkId { get; set; }

        public int Page { get; set; } = 1;
    }

    /// <summary>
    /// Retrieves upcoming movies from Seerr.
    /// </summary>
    [Route("/Litefin/Seerr/Discover/Movies/Upcoming", "GET", Summary = "Gets upcoming movies from Seerr")]
    [Authenticated]
    public class GetSeerrUpcomingMoviesRequest : IReturn<object>
    {
        public int Page { get; set; } = 1;
    }

    /// <summary>
    /// Retrieves upcoming television series from Seerr.
    /// </summary>
    [Route("/Litefin/Seerr/Discover/Tv/Upcoming", "GET", Summary = "Gets upcoming TV series from Seerr")]
    [Authenticated]
    public class GetSeerrUpcomingTvRequest : IReturn<object>
    {
        public int Page { get; set; } = 1;
    }

    /// <summary>
    /// Retrieves movie genre slider items from Seerr.
    /// </summary>
    [Route("/Litefin/Seerr/Discover/GenreSlider/Movie", "GET", Summary = "Gets movie genre slider items from Seerr")]
    [Authenticated]
    public class GetSeerrGenreSliderMovieRequest : IReturn<object>
    {
    }

    /// <summary>
    /// Retrieves TV genre slider items from Seerr.
    /// </summary>
    [Route("/Litefin/Seerr/Discover/GenreSlider/Tv", "GET", Summary = "Gets TV genre slider items from Seerr")]
    [Authenticated]
    public class GetSeerrGenreSliderTvRequest : IReturn<object>
    {
    }

    #endregion

    #region Search, Details, and Cast Requests

    /// <summary>
    /// Searches the Seerr media catalogue.
    /// </summary>
    [Route("/Litefin/Seerr/Search", "GET", Summary = "Searches the Seerr catalogue")]
    [Authenticated]
    public class SearchSeerrRequest : IReturn<object>
    {
        public string Query { get; set; } = string.Empty;

        public int Page { get; set; } = 1;
    }

    /// <summary>
    /// Retrieves collection details from Seerr.
    /// </summary>
    [Route("/Litefin/Seerr/Collection/{CollectionId}", "GET", Summary = "Gets details for a collection from Seerr")]
    [Authenticated]
    public class GetSeerrCollectionDetailsRequest : IReturn<object>
    {
        public int CollectionId { get; set; }
    }

    /// <summary>
    /// Retrieves person details from Seerr.
    /// </summary>
    [Route("/Litefin/Seerr/Person/{PersonId}", "GET", Summary = "Gets details for a person from Seerr")]
    [Authenticated]
    public class GetSeerrPersonDetailsRequest : IReturn<object>
    {
        public int PersonId { get; set; }
    }

    /// <summary>
    /// Retrieves combined filmography credits for a person from Seerr.
    /// </summary>
    [Route("/Litefin/Seerr/Person/{PersonId}/CombinedCredits", "GET", Summary = "Gets combined credits for a person from Seerr")]
    [Route("/Litefin/Seerr/Person/{PersonId}/combined_credits", "GET", Summary = "Gets combined credits for a person from Seerr")]
    [Authenticated]
    public class GetSeerrPersonCombinedCreditsRequest : IReturn<object>
    {
        public int PersonId { get; set; }
    }

    /// <summary>
    /// Retrieves details for a specific movie from Seerr.
    /// </summary>
    [Route("/Litefin/Seerr/Movie/{TmdbId}", "GET", Summary = "Gets details for a movie from Seerr")]
    [Authenticated]
    public class GetSeerrMovieDetailsRequest : IReturn<object>
    {
        public int TmdbId { get; set; }
    }

    /// <summary>
    /// Retrieves similar movies from Seerr.
    /// </summary>
    [Route("/Litefin/Seerr/Movie/{TmdbId}/Similar", "GET", Summary = "Gets similar movies from Seerr")]
    [Authenticated]
    public class GetSeerrMovieSimilarRequest : IReturn<object>
    {
        public int TmdbId { get; set; }

        public int Page { get; set; } = 1;
    }

    /// <summary>
    /// Retrieves recommended movies from Seerr.
    /// </summary>
    [Route("/Litefin/Seerr/Movie/{TmdbId}/Recommendations", "GET", Summary = "Gets recommended movies from Seerr")]
    [Authenticated]
    public class GetSeerrMovieRecommendationsRequest : IReturn<object>
    {
        public int TmdbId { get; set; }

        public int Page { get; set; } = 1;
    }

    /// <summary>
    /// Retrieves details for a specific television series from Seerr.
    /// </summary>
    [Route("/Litefin/Seerr/Tv/{TmdbId}", "GET", Summary = "Gets details for a TV series from Seerr")]
    [Authenticated]
    public class GetSeerrTvDetailsRequest : IReturn<object>
    {
        public int TmdbId { get; set; }
    }

    /// <summary>
    /// Retrieves similar television series from Seerr.
    /// </summary>
    [Route("/Litefin/Seerr/Tv/{TmdbId}/Similar", "GET", Summary = "Gets similar TV series from Seerr")]
    [Authenticated]
    public class GetSeerrTvSimilarRequest : IReturn<object>
    {
        public int TmdbId { get; set; }

        public int Page { get; set; } = 1;
    }

    /// <summary>
    /// Retrieves recommended television series from Seerr.
    /// </summary>
    [Route("/Litefin/Seerr/Tv/{TmdbId}/Recommendations", "GET", Summary = "Gets recommended TV series from Seerr")]
    [Authenticated]
    public class GetSeerrTvRecommendationsRequest : IReturn<object>
    {
        public int TmdbId { get; set; }

        public int Page { get; set; } = 1;
    }

    #endregion

    #region Requests, Media, Ratings, Services, Users, and Watchlist

    /// <summary>
    /// Retrieves the Seerr requests list scoped to the authenticated user.
    /// </summary>
    [Route("/Litefin/Seerr/Requests", "GET", Summary = "Gets user Seerr requests")]
    [Route("/Litefin/Seerr/request", "GET", Summary = "Gets user Seerr requests")]
    [Authenticated]
    public class GetSeerrRequestsRequest : IReturn<object>
    {
        public int Take { get; set; } = 20;

        public int Skip { get; set; } = 0;

        public string Filter { get; set; } = "all";
    }

    /// <summary>
    /// Creates a media request on Seerr.
    /// </summary>
    [Route("/Litefin/Seerr/Requests", "POST", Summary = "Creates a media request on Seerr")]
    [Authenticated]
    public class CreateSeerrRequestDto : SeerrRequest, IReturn<object>
    {
    }

    /// <summary>
    /// Cancels a media request on Seerr.
    /// </summary>
    [Route("/Litefin/Seerr/Requests/{RequestId}", "DELETE", Summary = "Cancels a media request on Seerr")]
    [Authenticated]
    public class CancelSeerrRequestDto : IReturnVoid
    {
        public int RequestId { get; set; }
    }

    /// <summary>
    /// Retrieves recently added media items from Seerr.
    /// </summary>
    [Route("/Litefin/Seerr/Media", "GET", Summary = "Gets recently added media items from Seerr")]
    [Route("/Litefin/Seerr/RecentlyAdded", "GET", Summary = "Gets recently added media items from Seerr")]
    [Authenticated]
    public class GetSeerrRecentlyAddedRequest : IReturn<object>
    {
        public int Take { get; set; } = 20;

        public int Skip { get; set; } = 0;
    }

    /// <summary>
    /// Retrieves combined ratings for a media title from Seerr.
    /// </summary>
    [Route("/Litefin/Seerr/Ratings/{MediaType}/{TmdbId}", "GET", Summary = "Gets ratings for a media title from Seerr")]
    [Authenticated]
    public class GetSeerrRatingsRequest : IReturn<object>
    {
        public string MediaType { get; set; } = string.Empty;

        public int TmdbId { get; set; }
    }

    /// <summary>
    /// Retrieves services configured in Seerr.
    /// </summary>
    [Route("/Litefin/Seerr/Services/{MediaType}", "GET", Summary = "Gets services configured in Seerr")]
    [Authenticated]
    public class GetSeerrServicesRequest : IReturn<object>
    {
        public string MediaType { get; set; } = string.Empty;
    }

    /// <summary>
    /// Retrieves service profiles and root folders from Seerr.
    /// </summary>
    [Route("/Litefin/Seerr/Services/{MediaType}/{ServerId}", "GET", Summary = "Gets service profiles and root folders from Seerr")]
    [Authenticated]
    public class GetSeerrServiceDetailsRequest : IReturn<object>
    {
        public string MediaType { get; set; } = string.Empty;

        public int ServerId { get; set; }
    }

    /// <summary>
    /// Retrieves user capabilities and permission bitmask from Seerr.
    /// </summary>
    [Route("/Litefin/Seerr/User", "GET", Summary = "Gets the user permission bitmask from Seerr")]
    [Authenticated]
    public class GetSeerrUserCapabilitiesRequest : IReturn<object>
    {
    }

    /// <summary>
    /// Retrieves the list of users from Seerr.
    /// </summary>
    [Route("/Litefin/Seerr/Users", "GET", Summary = "Gets users list from Seerr")]
    [Authenticated]
    public class GetSeerrUsersRequest : IReturn<object>
    {
        public int Take { get; set; } = 1000;

        public string Sort { get; set; } = "displayname";
    }

    /// <summary>
    /// Retrieves the authenticated user's Seerr watchlist.
    /// </summary>
    [Route("/Litefin/Seerr/Watchlist", "GET", Summary = "Gets the user Seerr watchlist")]
    [Authenticated]
    public class GetSeerrWatchlistRequest : IReturn<object>
    {
        public int Page { get; set; } = 1;
    }

    /// <summary>
    /// Adds a title to the authenticated user's Seerr watchlist.
    /// </summary>
    [Route("/Litefin/Seerr/Watchlist", "POST", Summary = "Adds a title to the Seerr watchlist")]
    [Authenticated]
    public class AddToSeerrWatchlistRequest : IReturn<object>
    {
        public string? MediaType { get; set; }

        public int? TitleId { get; set; }

        public int? MediaId { get; set; }

        public int? TmdbId { get; set; }
    }

    /// <summary>
    /// Removes a title from the authenticated user's Seerr watchlist.
    /// </summary>
    [Route("/Litefin/Seerr/Watchlist/{MediaType}/{TmdbId}", "DELETE", Summary = "Removes a title from the Seerr watchlist")]
    [Authenticated]
    public class RemoveFromSeerrWatchlistRequest : IReturnVoid
    {
        public string MediaType { get; set; } = string.Empty;

        public int TmdbId { get; set; }
    }

    #endregion
}
