// <copyright file="BackupRequests.cs" company="Litefin">
// Copyright (c) Litefin. All rights reserved.
// </copyright>

namespace Litefin.Emby.Plugin.RestApi.Requests
{
    using Litefin.Emby.Plugin.Models;
    using MediaBrowser.Controller.Net;
    using MediaBrowser.Model.Services;

    /// <summary>
    /// Request DTO for retrieving all configuration and settings backups stored on the server.
    /// </summary>
    [Route("/Litefin/Backup", "GET", Summary = "Gets all settings backups stored on the server")]
    [Authenticated]
    public class GetBackupsRequest : IReturn<object>
    {
    }

    /// <summary>
    /// Request DTO for creating a new settings backup snapshot or updating an existing snapshot.
    /// </summary>
    [Route("/Litefin/Backup", "POST", Summary = "Creates or updates a settings backup snapshot")]
    [Authenticated]
    public class SaveBackupRequest : BackupRequest, IReturnVoid
    {
    }

    /// <summary>
    /// Request DTO for deleting a specific settings backup snapshot by unique identifier.
    /// </summary>
    [Route("/Litefin/Backup/{Id}", "DELETE", Summary = "Deletes a specific settings backup snapshot")]
    [Authenticated]
    public class DeleteBackupRequest : IReturnVoid
    {
        /// <summary>
        /// Gets or sets the unique identifier of the backup snapshot to delete.
        /// </summary>
        [ApiMember(Name = "Id", Description = "The identifier of the backup to delete", IsRequired = true, DataType = "string", ParameterType = "path")]
        public string Id { get; set; } = string.Empty;
    }
}
