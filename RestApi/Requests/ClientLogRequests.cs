// <copyright file="ClientLogRequests.cs" company="Litefin">
// Copyright (c) Litefin. All rights reserved.
// </copyright>

namespace Litefin.Emby.Plugin.RestApi.Requests
{
    using System.Collections.Generic;
    using System.IO;
    using Litefin.Emby.Plugin.Models;
    using MediaBrowser.Controller.Net;
    using MediaBrowser.Model.Services;

    /// <summary>
    /// Request DTO for uploading a client diagnostic log file.
    /// Intercepts the canonical /ClientLog/Document route called natively by Litefin clients.
    /// Implements IRequiresRequestStream so ServiceStack passes the raw log text stream directly.
    /// </summary>
    [Route("/ClientLog/Document", "POST", Summary = "Uploads a client diagnostic log document")]
    [Route("/Litefin/ClientLog/Document", "POST", Summary = "Uploads a client diagnostic log document")]
    [Authenticated]
    public class UploadClientLogRequest : IRequiresRequestStream, IReturnVoid
    {
        /// <summary>
        /// Gets or sets the target file name requested by the client in the query string.
        /// When omitted, the server generates a timestamped file name.
        /// </summary>
        [ApiMember(Name = "Name", Description = "Target log file name", ParameterType = "query", DataType = "string", IsRequired = false)]
        public string? Name { get; set; }

        /// <summary>
        /// Gets or sets the raw HTTP request stream containing the unparsed log text payload.
        /// </summary>
        public Stream RequestStream { get; set; } = Stream.Null;
    }

    /// <summary>
    /// Request DTO for listing all stored client diagnostic log files on the server.
    /// Accessible by administrators to audit client diagnostics.
    /// </summary>
    [Route("/Litefin/ClientLogs", "GET", Summary = "Retrieves all uploaded client diagnostic log files")]
    [Authenticated]
    public class GetClientLogsRequest : IReturn<List<ClientLogFileInfo>>
    {
    }

    /// <summary>
    /// Request DTO for retrieving, streaming, or downloading a specific client diagnostic log file.
    /// Returns raw text/plain content with an inline Content-Disposition header.
    /// </summary>
    [Route("/Litefin/ClientLogs/{Name}", "GET", Summary = "Downloads or views a specific client diagnostic log file")]
    [Authenticated]
    public class GetClientLogFileRequest : IReturn<object>
    {
        /// <summary>
        /// Gets or sets the file name of the requested log document.
        /// </summary>
        [ApiMember(Name = "Name", Description = "File name of the log document", ParameterType = "path", DataType = "string", IsRequired = true)]
        public string Name { get; set; } = string.Empty;
    }

    /// <summary>
    /// Request DTO for deleting a single client diagnostic log file by name.
    /// Restricted to administrator sessions.
    /// </summary>
    [Route("/Litefin/ClientLogs/{Name}", "DELETE", Summary = "Deletes a specific client diagnostic log file")]
    [Authenticated]
    public class DeleteClientLogRequest : IReturnVoid
    {
        /// <summary>
        /// Gets or sets the file name of the log document to delete.
        /// </summary>
        [ApiMember(Name = "Name", Description = "File name of the log document", ParameterType = "path", DataType = "string", IsRequired = true)]
        public string Name { get; set; } = string.Empty;
    }

    /// <summary>
    /// Request DTO for purging all client diagnostic logs stored on the server.
    /// Restricted to administrator sessions.
    /// </summary>
    [Route("/Litefin/ClientLogs", "DELETE", Summary = "Deletes all client diagnostic log files")]
    [Authenticated]
    public class ClearClientLogsRequest : IReturnVoid
    {
    }
}
