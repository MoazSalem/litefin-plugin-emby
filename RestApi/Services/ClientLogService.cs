// <copyright file="ClientLogService.cs" company="Litefin">
// Copyright (c) Litefin. All rights reserved.
// </copyright>

namespace Litefin.Emby.Plugin.RestApi.Services
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.IO;
    using System.Linq;
    using System.Net;
    using System.Threading.Tasks;
    using Litefin.Emby.Plugin.Common;
    using Litefin.Emby.Plugin.Models;
    using Litefin.Emby.Plugin.RestApi.Requests;
    using MediaBrowser.Controller.Api;
    using MediaBrowser.Model.Net;

    /// <summary>
    /// REST API service providing client diagnostic log ingestion, storage, retrieval, and lifecycle management.
    /// Intercepts native Litefin /ClientLog/Document uploads and makes them inspectable from the Emby Dashboard.
    /// </summary>
    public class ClientLogService : BaseApiService
    {
        // Maximum allowed file size for a single client log upload: 10 megabytes
        private const long MaxLogFileSizeBytes = 10 * 1024 * 1024;

        // Maximum count of retained diagnostic logs before older entries are pruned
        private const int MaxRetainedLogFiles = 50;

        /// <summary>
        /// Processes POST /ClientLog/Document and POST /Litefin/ClientLog/Document to save client log streams.
        /// </summary>
        /// <param name="request">The incoming request containing the stream and optional file name.</param>
        /// <returns>A completed asynchronous task.</returns>
        public async Task Post(UploadClientLogRequest request)
        {
            // Resolve the active request stream from DTO or HTTP context
            var inputStream = request?.RequestStream ?? this.Request.InputStream;
            if (inputStream == null || inputStream == Stream.Null)
            {
                throw new HttpException("Request payload contains no log data.")
                {
                    StatusCode = HttpStatusCode.BadRequest,
                };
            }

            // Fallback to timestamped file name when name query parameter is omitted
            var rawName = string.IsNullOrWhiteSpace(request?.Name)
                ? $"Litefin_Log_{DateTime.UtcNow:yyyy-MM-dd_HH-mm-ss}.txt"
                : request!.Name!;

            // Sanitize file name to block path traversal attacks
            var safeName = SanitizeFileName(rawName);
            var logDir = GetLogDirectory();
            var targetFilePath = Path.Combine(logDir, safeName);

            this.Logger.Info("Receiving Litefin client diagnostic log: {0}", safeName);

            // Buffer and write the incoming log stream safely to disk
            using (var fileStream = new FileStream(targetFilePath, FileMode.Create, FileAccess.Write, FileShare.None, 8192, useAsync: true))
            {
                var buffer = new byte[81920];
                long totalBytesWritten = 0;
                int bytesRead;

                // Read chunks asynchronously while checking against the maximum size threshold
                while ((bytesRead = await inputStream.ReadAsync(buffer, 0, buffer.Length, this.Request.CancellationToken).ConfigureAwait(false)) > 0)
                {
                    totalBytesWritten += bytesRead;
                    if (totalBytesWritten > MaxLogFileSizeBytes)
                    {
                        // Abort immediately and delete partial file if quota is breached
                        fileStream.Close();
                        if (File.Exists(targetFilePath))
                        {
                            File.Delete(targetFilePath);
                        }

                        this.Logger.Warn("Client log {0} rejected: exceeded size limit of {1} bytes", safeName, MaxLogFileSizeBytes);
                        throw new HttpException("Log file exceeds maximum allowed upload size (10 MB).")
                        {
                            StatusCode = HttpStatusCode.RequestEntityTooLarge,
                        };
                    }

                    await fileStream.WriteAsync(buffer, 0, bytesRead, this.Request.CancellationToken).ConfigureAwait(false);
                }
            }

            this.Logger.Info("Successfully stored Litefin client log '{0}'", safeName);

            // Automatically purge oldest logs if the threshold of retained files is exceeded
            PruneOldLogs(logDir, MaxRetainedLogFiles);
        }

        /// <summary>
        /// Processes GET /Litefin/ClientLogs to list all uploaded client diagnostic log files.
        /// </summary>
        /// <param name="request">The listing request object.</param>
        /// <returns>A list of log file metadata objects ordered by last modified date descending.</returns>
        public object Get(GetClientLogsRequest request)
        {
            // Restrict diagnostic log audits to server administrators
            if (!this.IsUserAdmin())
            {
                throw new HttpException("Elevation required. Only administrators can view client diagnostic logs.")
                {
                    StatusCode = HttpStatusCode.Forbidden,
                };
            }

            var logDir = GetLogDirectory();
            var directoryInfo = new DirectoryInfo(logDir);
            if (!directoryInfo.Exists)
            {
                return Array.Empty<ClientLogFileInfo>();
            }

            // Query log directory for all text and log files
            var files = directoryInfo.GetFiles("*.*")
                .Where(f => f.Extension.Equals(".txt", StringComparison.OrdinalIgnoreCase) ||
                            f.Extension.Equals(".log", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .Select(f => new ClientLogFileInfo
                {
                    Name = f.Name,
                    Size = f.Length,
                    SizeFormatted = FormatFileSize(f.Length),
                    DateCreated = f.CreationTimeUtc,
                    DateModified = f.LastWriteTimeUtc,
                })
                .ToList();

            return files;
        }

        /// <summary>
        /// Processes GET /Litefin/ClientLogs/{Name} to view or download a specific client log file.
        /// </summary>
        /// <param name="request">The retrieval request specifying the target log file name.</param>
        /// <returns>A streamed HTTP response containing the plain text log document.</returns>
        public object Get(GetClientLogFileRequest request)
        {
            // Restrict diagnostic log access to administrators
            if (!this.IsUserAdmin())
            {
                throw new HttpException("Elevation required.")
                {
                    StatusCode = HttpStatusCode.Forbidden,
                };
            }

            if (request == null || string.IsNullOrWhiteSpace(request.Name))
            {
                throw new HttpException("A valid log file name is required.")
                {
                    StatusCode = HttpStatusCode.BadRequest,
                };
            }

            // Strict sanitization prevents directory traversal
            var safeName = SanitizeFileName(request.Name);
            var filePath = Path.Combine(GetLogDirectory(), safeName);

            if (!File.Exists(filePath))
            {
                throw new HttpException("Diagnostic log file not found.")
                {
                    StatusCode = HttpStatusCode.NotFound,
                };
            }

            // Open stream with shared read access so concurrent downloads succeed
            var fileStream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var responseHeaders = new Dictionary<string, string>
            {
                { "Content-Disposition", $"inline; filename=\"{safeName}\"" },
            };

            // Stream raw text directly to caller using Emby's HTTP result factory
            return this.ResultFactory.GetResult(this.Request, fileStream, "text/plain; charset=utf-8", responseHeaders);
        }

        /// <summary>
        /// Processes DELETE /Litefin/ClientLogs/{Name} to remove a specific client diagnostic log.
        /// </summary>
        /// <param name="request">The deletion request identifying the log file to remove.</param>
        public void Delete(DeleteClientLogRequest request)
        {
            // Restrict log deletion to administrators
            if (!this.IsUserAdmin())
            {
                throw new HttpException("Elevation required.")
                {
                    StatusCode = HttpStatusCode.Forbidden,
                };
            }

            if (request == null || string.IsNullOrWhiteSpace(request.Name))
            {
                throw new HttpException("A valid log file name is required.")
                {
                    StatusCode = HttpStatusCode.BadRequest,
                };
            }

            var safeName = SanitizeFileName(request.Name);
            var filePath = Path.Combine(GetLogDirectory(), safeName);

            if (File.Exists(filePath))
            {
                this.Logger.Info("Deleting Litefin client diagnostic log: {0}", safeName);
                File.Delete(filePath);
            }
        }

        /// <summary>
        /// Processes DELETE /Litefin/ClientLogs to purge all stored client diagnostic log files.
        /// </summary>
        /// <param name="request">The clear request object.</param>
        public void Delete(ClearClientLogsRequest request)
        {
            // Restrict bulk log clearing to administrators
            if (!this.IsUserAdmin())
            {
                throw new HttpException("Elevation required.")
                {
                    StatusCode = HttpStatusCode.Forbidden,
                };
            }

            var logDir = GetLogDirectory();
            var directoryInfo = new DirectoryInfo(logDir);
            if (!directoryInfo.Exists)
            {
                return;
            }

            // Remove all diagnostic log files
            var files = directoryInfo.GetFiles("*.*")
                .Where(f => f.Extension.Equals(".txt", StringComparison.OrdinalIgnoreCase) ||
                            f.Extension.Equals(".log", StringComparison.OrdinalIgnoreCase));

            int deletedCount = 0;
            foreach (var file in files)
            {
                try
                {
                    file.Delete();
                    deletedCount++;
                }
                catch (Exception ex)
                {
                    this.Logger.Warn("Unable to delete log file {0}: {1}", file.Name, ex.Message);
                }
            }

            this.Logger.Info("Purged {0} Litefin client diagnostic logs", deletedCount);
        }

        /// <summary>
        /// Resolves the dedicated local directory path used to store client diagnostic logs.
        /// Defaults to the "Litefin" subdirectory inside Emby's native log folder.
        /// </summary>
        /// <returns>The verified directory path on disk.</returns>
        private string GetLogDirectory()
        {
            // Retrieve Emby's official log directory from server configuration manager or plugin instance
            var baseLogDir = this.ConfigurationManager?.ApplicationPaths?.LogDirectoryPath
                ?? Plugin.Instance?.ApplicationPaths?.LogDirectoryPath;

            // Fallback for custom or portable environments where host path may be unbound
            if (string.IsNullOrWhiteSpace(baseLogDir))
            {
                baseLogDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "Emby-Server",
                    "programdata",
                    "logs");
            }

            // Subdivide into dedicated Litefin namespace to keep Emby server logs distinct
            var targetDir = Path.Combine(baseLogDir!, "Litefin");
            if (!Directory.Exists(targetDir))
            {
                Directory.CreateDirectory(targetDir);
            }

            return targetDir;
        }

        /// <summary>
        /// Strips malicious path traversal characters and enforces safe file naming standards.
        /// </summary>
        /// <param name="rawName">The untrusted client-provided file name.</param>
        /// <returns>A sanitized safe file name.</returns>
        private static string SanitizeFileName(string rawName)
        {
            // Strip any leading directory components (e.g. ../ or /etc/)
            var safe = Path.GetFileName(rawName);

            // Replace illegal file system characters with underscores
            foreach (var invalidChar in Path.GetInvalidFileNameChars())
            {
                safe = safe.Replace(invalidChar, '_');
            }

            // Enforce .txt extension if neither .txt nor .log was provided
            if (!safe.EndsWith(".txt", StringComparison.OrdinalIgnoreCase) &&
                !safe.EndsWith(".log", StringComparison.OrdinalIgnoreCase))
            {
                safe += ".txt";
            }

            return safe;
        }

        /// <summary>
        /// Enforces storage retention limits by pruning oldest log files when total count exceeds the limit.
        /// </summary>
        /// <param name="logDir">The directory path containing log documents.</param>
        /// <param name="maxRetainedFiles">Maximum count of log files to preserve.</param>
        private static void PruneOldLogs(string logDir, int maxRetainedFiles)
        {
            try
            {
                var directoryInfo = new DirectoryInfo(logDir);
                if (!directoryInfo.Exists)
                {
                    return;
                }

                // Query and sort all logs chronologically (oldest first)
                var logFiles = directoryInfo.GetFiles("*.*")
                    .Where(f => f.Extension.Equals(".txt", StringComparison.OrdinalIgnoreCase) ||
                                f.Extension.Equals(".log", StringComparison.OrdinalIgnoreCase))
                    .OrderBy(f => f.LastWriteTimeUtc)
                    .ToList();

                // Delete excess files until total count matches the retention ceiling
                var excessCount = logFiles.Count - maxRetainedFiles;
                for (int i = 0; i < excessCount; i++)
                {
                    try
                    {
                        logFiles[i].Delete();
                    }
                    catch
                    {
                        // File may be locked; continue pruning remaining candidates
                    }
                }
            }
            catch
            {
                // Silently ignore directory inspection errors during background cleanup
            }
        }

        /// <summary>
        /// Formats raw byte counts into clean human-readable binary size strings.
        /// </summary>
        /// <param name="bytes">Raw size in bytes.</param>
        /// <returns>A string representation formatted with binary unit suffixes.</returns>
        private static string FormatFileSize(long bytes)
        {
            if (bytes < 1024)
            {
                return string.Format(CultureInfo.InvariantCulture, "{0} B", bytes);
            }

            if (bytes < 1024 * 1024)
            {
                return string.Format(CultureInfo.InvariantCulture, "{0:F1} KB", bytes / 1024.0);
            }

            return string.Format(CultureInfo.InvariantCulture, "{0:F2} MB", bytes / (1024.0 * 1024.0));
        }
    }
}
