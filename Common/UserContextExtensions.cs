// <copyright file="UserContextExtensions.cs" company="Litefin">
// Copyright (c) Litefin. All rights reserved.
// </copyright>

namespace Litefin.Emby.Plugin.Common
{
    using System;
    using System.Net;
    using MediaBrowser.Controller.Api;
    using MediaBrowser.Controller.Entities;
    using MediaBrowser.Controller.Net;
    using MediaBrowser.Model.Net;

    /// <summary>
    /// Extension methods for resolving authenticated user context within Emby API services.
    /// </summary>
    public static class UserContextExtensions
    {
        /// <summary>
        /// Resolves the effective target user for the current request.
        /// Prioritizes the explicit requestedUserId parameter if provided, falling back to the authenticated user.
        /// </summary>
        /// <param name="service">The API service processing the request.</param>
        /// <param name="requestedUserId">Optional explicit user GUID from query or route parameters.</param>
        /// <returns>The resolved User entity.</returns>
        /// <exception cref="HttpException">Thrown when user context cannot be determined or user is not found.</exception>
        public static User GetTargetUser(this BaseApiService service, Guid? requestedUserId)
        {
            // If caller explicitly specified a non-empty user GUID, try to find that user
            if (requestedUserId.HasValue && requestedUserId.Value != Guid.Empty)
            {
                var userById = service.UserManager.GetUserById(requestedUserId.Value);
                if (userById != null)
                {
                    return userById;
                }
            }

            // Extract authentication info from the incoming HTTP request context
            var auth = service.AuthorizationContext?.GetAuthorizationInfo(service.Request);
            if (auth?.User != null)
            {
                return auth.User;
            }

            // If auth has an internal long user ID, attempt lookup by long ID
            if (auth != null && auth.UserId != 0)
            {
                var userByInternalId = service.UserManager.GetUserById(auth.UserId);
                if (userByInternalId != null)
                {
                    return userByInternalId;
                }
            }

            // User context is missing or unauthenticated
            throw new HttpException("User context missing.")
            {
                StatusCode = HttpStatusCode.Unauthorized,
            };
        }

        /// <summary>
        /// Gets the authenticated user's ID as a string, suitable for ownership comparison.
        /// </summary>
        /// <param name="service">The API service processing the request.</param>
        /// <returns>A string representation of the authenticated user's GUID or internal ID.</returns>
        public static string GetAuthenticatedUserIdString(this BaseApiService service)
        {
            var auth = service.AuthorizationContext?.GetAuthorizationInfo(service.Request);
            if (auth?.User != null)
            {
                return auth.User.Id.ToString();
            }

            if (auth != null && auth.UserId != 0)
            {
                var user = service.UserManager.GetUserById(auth.UserId);
                if (user != null)
                {
                    return user.Id.ToString();
                }

                return auth.UserId.ToString();
            }

            throw new HttpException("User context missing.")
            {
                StatusCode = HttpStatusCode.Unauthorized,
            };
        }

        /// <summary>
        /// Gets the username of the authenticated user.
        /// </summary>
        /// <param name="service">The API service processing the request.</param>
        /// <returns>The username, or "Unknown" if not resolvable.</returns>
        public static string GetAuthenticatedUsername(this BaseApiService service)
        {
            var auth = service.AuthorizationContext?.GetAuthorizationInfo(service.Request);
            if (auth?.User != null)
            {
                return auth.User.Name;
            }

            if (auth != null && auth.UserId != 0)
            {
                var user = service.UserManager.GetUserById(auth.UserId);
                if (user != null)
                {
                    return user.Name;
                }
            }

            return "Unknown";
        }

        /// <summary>
        /// Checks whether the authenticated user has administrative privileges on the server.
        /// </summary>
        /// <param name="service">The API service processing the request.</param>
        /// <returns>True if the user is an administrator, false otherwise.</returns>
        public static bool IsUserAdmin(this BaseApiService service)
        {
            try
            {
                var user = service.GetTargetUser(null);
                return user?.Policy?.IsAdministrator ?? false;
            }
            catch
            {
                return false;
            }
        }
    }
}
