// <copyright file="BackupService.cs" company="Litefin">
// Copyright (c) Litefin. All rights reserved.
// </copyright>

namespace Litefin.Emby.Plugin.RestApi.Services
{
    using System;
    using System.Linq;
    using System.Net;
    using Litefin.Emby.Plugin.Common;
    using Litefin.Emby.Plugin.Models;
    using Litefin.Emby.Plugin.RestApi.Requests;
    using MediaBrowser.Controller.Api;
    using MediaBrowser.Model.Net;

    /// <summary>
    /// REST API service providing settings backup and sync capabilities between Litefin clients.
    /// Manages per-user configurations while allowing cross-user layout sharing.
    /// </summary>
    public class BackupService : BaseApiService
    {
        /// <summary>
        /// Processes GET /Litefin/Backup requests to retrieve all available backup snapshots.
        /// </summary>
        /// <param name="request">The retrieval request parameter object.</param>
        /// <returns>A collection of all saved backup snapshots.</returns>
        public object Get(GetBackupsRequest request)
        {
            // Access backups list from the persistent plugin configuration singleton
            var backups = Plugin.Instance?.Configuration.Backups;
            if (backups == null)
            {
                return Array.Empty<object>();
            }

            this.Logger.Info("Retrieving all Litefin settings backups (count: {0})", backups.Count);

            // Project backups into a structured response object
            return backups.Select(b => new
            {
                Id = b.Id,
                UserId = b.UserId,
                Username = b.Username,
                DeviceId = b.DeviceId,
                DeviceName = b.DeviceName,
                Name = b.Name,
                DateCreated = b.DateCreated,
                Settings = b.Settings,
                AppVersion = b.AppVersion,
                Platform = b.Platform,
            }).ToArray();
        }

        /// <summary>
        /// Processes POST /Litefin/Backup requests to create or update a settings backup snapshot.
        /// </summary>
        /// <param name="request">The payload containing serialized settings and client metadata.</param>
        public void Post(SaveBackupRequest request)
        {
            // Validate that serialized settings payload is supplied
            if (request == null || string.IsNullOrWhiteSpace(request.Settings))
            {
                throw new HttpException("Settings data is required.")
                {
                    StatusCode = HttpStatusCode.BadRequest,
                };
            }

            // Resolve the authenticated user's ID and name for ownership attribution
            var userIdStr = this.GetAuthenticatedUserIdString();
            var username = this.GetAuthenticatedUsername();

            var backups = Plugin.Instance?.Configuration.Backups;
            if (backups == null)
            {
                throw new HttpException("Backup store unavailable.")
                {
                    StatusCode = HttpStatusCode.ServiceUnavailable,
                };
            }

            // Search for an existing backup snapshot matching the requested ID
            UserBackup? existing = null;
            if (!string.IsNullOrEmpty(request.Id))
            {
                existing = backups.FirstOrDefault(b => string.Equals(b.Id, request.Id, StringComparison.OrdinalIgnoreCase));
            }

            if (existing != null)
            {
                // Enforce ownership: only the user who created a backup may overwrite it
                if (!string.Equals(existing.UserId, userIdStr, StringComparison.OrdinalIgnoreCase))
                {
                    this.Logger.Warn("User {0} attempted to overwrite backup {1} owned by {2}", userIdStr, existing.Id, existing.UserId);
                    throw new HttpException("Forbidden.")
                    {
                        StatusCode = HttpStatusCode.Forbidden,
                    };
                }

                this.Logger.Info("Updating settings backup {0} for user {1} ({2})", existing.Id, username, userIdStr);

                // Update settings and tracking metadata
                existing.Settings = request.Settings;
                existing.DateCreated = DateTime.UtcNow;
                existing.Username = username;
                existing.DeviceId = request.DeviceId;
                existing.DeviceName = request.DeviceName;
                existing.AppVersion = request.AppVersion ?? string.Empty;
                existing.Platform = request.Platform ?? string.Empty;

                if (!string.IsNullOrEmpty(request.Name))
                {
                    existing.Name = request.Name!;
                }
            }
            else
            {
                // Assign a new GUID if no identifier was supplied
                var backupId = string.IsNullOrEmpty(request.Id) ? Guid.NewGuid().ToString() : request.Id!;
                var backupName = request.Name ?? string.Empty;

                this.Logger.Info("Creating settings backup {0} ('{1}') for user {2} ({3})", backupId, backupName, username, userIdStr);

                // Append new backup record to persistent configuration
                backups.Add(new UserBackup
                {
                    Id = backupId,
                    UserId = userIdStr,
                    Username = username,
                    DeviceId = request.DeviceId,
                    DeviceName = request.DeviceName,
                    Name = backupName,
                    DateCreated = DateTime.UtcNow,
                    Settings = request.Settings,
                    AppVersion = request.AppVersion ?? string.Empty,
                    Platform = request.Platform ?? string.Empty,
                });
            }

            // Persist the updated configuration to disk
            Plugin.Instance?.SaveConfiguration();
        }

        /// <summary>
        /// Processes DELETE /Litefin/Backup/{Id} requests to remove a backup snapshot.
        /// </summary>
        /// <param name="request">The request specifying the snapshot identifier to remove.</param>
        public void Delete(DeleteBackupRequest request)
        {
            // Resolve the authenticated user's ID
            var userIdStr = this.GetAuthenticatedUserIdString();

            var backups = Plugin.Instance?.Configuration.Backups;
            if (backups == null)
            {
                throw new HttpException("Backup store unavailable.")
                {
                    StatusCode = HttpStatusCode.ServiceUnavailable,
                };
            }

            // Locate the backup snapshot to delete
            var existing = backups.FirstOrDefault(b => string.Equals(b.Id, request.Id, StringComparison.OrdinalIgnoreCase));
            if (existing == null)
            {
                this.Logger.Warn("DeleteBackup failed: No backup found with ID {0}", request.Id);
                throw new HttpException("No backup found with that ID.")
                {
                    StatusCode = HttpStatusCode.NotFound,
                };
            }

            // Enforce ownership: only the snapshot owner can delete it
            if (!string.Equals(existing.UserId, userIdStr, StringComparison.OrdinalIgnoreCase))
            {
                this.Logger.Warn("User {0} attempted to delete backup {1} owned by {2}", userIdStr, existing.Id, existing.UserId);
                throw new HttpException("Forbidden.")
                {
                    StatusCode = HttpStatusCode.Forbidden,
                };
            }

            this.Logger.Info("Deleting settings backup {0} for user {1} ({2})", existing.Id, existing.Username, userIdStr);

            // Remove record and save configuration
            backups.Remove(existing);
            Plugin.Instance?.SaveConfiguration();
        }
    }
}
