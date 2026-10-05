// <copyright file="BackupRequest.cs" company="Litefin">
// Copyright (c) Litefin. All rights reserved.
// </copyright>

namespace Litefin.Emby.Plugin.Models
{
    /// <summary>
    /// Data transfer object holding backup creation parameters sent by the client.
    /// </summary>
    public class BackupRequest
    {
        /// <summary>
        /// Gets or sets the unique identifier of the backup snapshot.
        /// If null or empty, this request represents a new backup creation.
        /// </summary>
        public string? Id { get; set; }

        /// <summary>
        /// Gets or sets the unique identifier of the client device.
        /// </summary>
        public string DeviceId { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the friendly model or display name of the client device.
        /// </summary>
        public string DeviceName { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the custom name given to the backup snapshot by the user.
        /// </summary>
        public string? Name { get; set; }

        /// <summary>
        /// Gets or sets the serialized settings JSON payload.
        /// </summary>
        public string Settings { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the Litefin application version creating this backup.
        /// </summary>
        public string? AppVersion { get; set; }

        /// <summary>
        /// Gets or sets the client platform identifier code (e.g., "tizen", "webos").
        /// </summary>
        public string? Platform { get; set; }
    }
}
