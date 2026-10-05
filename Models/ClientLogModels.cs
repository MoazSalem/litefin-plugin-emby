// <copyright file="ClientLogModels.cs" company="Litefin">
// Copyright (c) Litefin. All rights reserved.
// </copyright>

namespace Litefin.Emby.Plugin.Models
{
    using System;

    /// <summary>
    /// Metadata descriptor for a client diagnostic log file uploaded to the server.
    /// Provides file attributes, formatted sizing, and modification timestamps for dashboard rendering.
    /// </summary>
    public class ClientLogFileInfo
    {
        /// <summary>
        /// Gets or sets the file name of the log document.
        /// </summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the raw file size in bytes.
        /// </summary>
        public long Size { get; set; }

        /// <summary>
        /// Gets or sets the human-readable formatted file size string (e.g., "14.2 KB" or "1.5 MB").
        /// </summary>
        public string SizeFormatted { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the timestamp when the log file was created on the server.
        /// </summary>
        public DateTime DateCreated { get; set; }

        /// <summary>
        /// Gets or sets the timestamp when the log file was last modified or uploaded.
        /// </summary>
        public DateTime DateModified { get; set; }
    }
}
