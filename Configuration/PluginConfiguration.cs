// <copyright file="PluginConfiguration.cs" company="Litefin">
// Copyright (c) Litefin. All rights reserved.
// </copyright>

namespace Litefin.Emby.Plugin.Configuration
{
    using System.Collections.ObjectModel;
    using Litefin.Emby.Plugin.Models;
    using MediaBrowser.Model.Plugins;

    /// <summary>
    /// Plugin configuration for Litefin on Emby Server.
    /// Holds system-wide integration settings as well as user client settings backups.
    /// </summary>
    public class PluginConfiguration : BasePluginConfiguration
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="PluginConfiguration"/> class.
        /// Sets default empty values and initializes collections.
        /// </summary>
        public PluginConfiguration()
        {
            this.Backups = new Collection<UserBackup>();
            this.SeerrUrl = string.Empty;
            this.SeerrApiKey = string.Empty;
        }

        /// <summary>
        /// Gets or sets the collection of user-specific client settings backups.
        /// Serialized automatically by Emby Server as part of the plugin XML configuration.
        /// </summary>
        public Collection<UserBackup> Backups { get; set; }

        /// <summary>
        /// Gets or sets the base URL of the configured Seerr instance.
        /// </summary>
        public string SeerrUrl { get; set; }

        /// <summary>
        /// Gets or sets the Seerr API key managed through the admin dashboard.
        /// </summary>
        public string SeerrApiKey { get; set; }
    }
}
