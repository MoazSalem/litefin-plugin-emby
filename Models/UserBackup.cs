// <copyright file="UserBackup.cs" company="Litefin">
// Copyright (c) Litefin. All rights reserved.
// </copyright>

namespace Litefin.Emby.Plugin.Models
{
    using System;

    /// <summary>
    /// Represents a user-specific settings backup snapshot stored on the server.
    /// Distinguishes between multiple backups created by the same user or different devices.
    /// </summary>
    public class UserBackup
    {
        /// <summary>
        /// Gets or sets the unique identifier (GUID string) of this specific backup snapshot.
        /// This allows distinguishing between multiple backups created by the same user.
        /// </summary>
        public string Id { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the unique identifier of the user who owns this backup.
        /// Stored as a string to support both GUID and long identifier formats.
        /// </summary>
        public string UserId { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the username at the time the backup was created.
        /// Stored primarily for friendly display purposes on the client UI.
        /// </summary>
        public string Username { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the device ID from which the backup was created.
        /// Used to distinguish backups coming from different client devices.
        /// </summary>
        public string DeviceId { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the friendly display name of the device (e.g. "Tizen TV", "WebOS").
        /// Helps users identify which device is associated with this backup.
        /// </summary>
        public string DeviceName { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the custom name given to this backup by the user.
        /// If empty, a default fallback name is generated.
        /// </summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the timestamp (UTC) when this backup was saved.
        /// Used by the client to determine if a newer local configuration exists.
        /// </summary>
        public DateTime DateCreated { get; set; }

        /// <summary>
        /// Gets or sets the serialized JSON payload containing the client settings key-value pairs.
        /// </summary>
        public string Settings { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the Litefin application version at the time of creation.
        /// </summary>
        public string AppVersion { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the platform/OS this backup was created on.
        /// </summary>
        public string Platform { get; set; } = string.Empty;
    }
}
