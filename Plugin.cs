// <copyright file="Plugin.cs" company="Litefin">
// Copyright (c) Litefin. All rights reserved.
// </copyright>

namespace Litefin.Emby.Plugin
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.IO;
    using Litefin.Emby.Plugin.Configuration;
    using MediaBrowser.Common.Configuration;
    using MediaBrowser.Common.Plugins;
    using MediaBrowser.Model.Drawing;
    using MediaBrowser.Model.Plugins;
    using MediaBrowser.Model.Serialization;

    /// <summary>
    /// The main entry point for the Litefin server-side integration plugin for Emby.
    /// Provides configuration management, admin web page registration, and custom REST API endpoints.
    /// </summary>
    public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages, IHasThumbImage
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="Plugin"/> class.
        /// </summary>
        /// <param name="applicationPaths">Instance of the <see cref="IApplicationPaths"/> interface.</param>
        /// <param name="xmlSerializer">Instance of the <see cref="IXmlSerializer"/> interface.</param>
        public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
            : base(applicationPaths, xmlSerializer)
        {
            // Store static singleton reference for service access
            Instance = this;
        }

        /// <summary>
        /// Gets the current active plugin instance.
        /// </summary>
        public static Plugin? Instance { get; private set; }

        /// <inheritdoc />
        public override string Name => "Litefin";

        /// <inheritdoc />
        public override string Description => "Litefin server-side integration for Emby Server: Home hero carousel, merged rows, client settings backups, and Seerr proxy.";

        /// <inheritdoc />
        public override Guid Id => Guid.Parse("f5c68360-ca47-4648-b47c-3da5f112aa8f");

        /// <summary>
        /// Gets the thumbnail image format for catalog and plugin listings.
        /// </summary>
        public ImageFormat ThumbImageFormat => ImageFormat.Png;

        /// <summary>
        /// Retrieves the embedded plugin thumbnail image stream.
        /// </summary>
        /// <returns>A readable stream of the embedded image resource, or null if missing.</returns>
        public Stream? GetThumbImage()
        {
            var type = this.GetType();
            return type.Assembly.GetManifestResourceStream(type.Namespace + ".ThumbImage.png");
        }

        /// <inheritdoc />
        public IEnumerable<PluginPageInfo> GetPages()
        {
            // Namespace prefix for accessing manifest embedded resources
            var ns = this.GetType().Namespace;

            // Return configuration page and associated JavaScript controller
            return new[]
            {
                // Main administrative HTML view fragment for Emby dashboard
                new PluginPageInfo
                {
                    Name = this.Name,
                    DisplayName = this.Name,
                    EmbeddedResourcePath = string.Format(CultureInfo.InvariantCulture, "{0}.Configuration.configPage.html", ns),
                    EnableInMainMenu = true,
                    MenuSection = "plugins",
                    MenuIcon = "tv",
                },

                // Controller module for the config page, referenced via data-controller="__plugin/litefinjs"
                new PluginPageInfo
                {
                    Name = "litefinjs",
                    EmbeddedResourcePath = string.Format(CultureInfo.InvariantCulture, "{0}.Configuration.litefin.js", ns),
                },
            };
        }
    }
}
