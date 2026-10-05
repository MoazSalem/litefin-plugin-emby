define(['baseView', 'loading', 'emby-input', 'emby-button', 'emby-checkbox', 'emby-select', 'emby-scroller'], function (BaseView, loading) {
    'use strict';

    // Unique plugin identifier for configuration persistence
    var PluginUniqueId = 'f5c68360-ca47-4648-b47c-3da5f112aa8f';

    // Sanitization utility for user-supplied string data rendered into the DOM
    function escapeHtml(unsafe) {
        return (unsafe || '')
            .replace(/&/g, '&amp;')
            .replace(/</g, '&lt;')
            .replace(/>/g, '&gt;')
            .replace(/"/g, '&quot;')
            .replace(/'/g, '&#039;');
    }



    /**
     * Updates the status banner for Seerr connection and authentication operations.
     *
     * @param {Element} view - The active page view element.
     * @param {string} msg - Message text to display to the user.
     * @param {boolean|null} isSuccess - True for success (green), false for error (red), null for info.
     * @param {boolean} isPending - True if an asynchronous request is in-flight (blue).
     */
    function setSeerrStatus(view, msg, isSuccess, isPending) {
        var statusText = view.querySelector('#seerr-status');
        var statusIcon = view.querySelector('#seerr-status-icon');
        if (!statusText) return;

        statusText.textContent = msg || '';
        if (isSuccess === true) {
            statusText.style.color = '#4ade80';
            if (statusIcon) {
                statusIcon.textContent = '[OK]';
                statusIcon.style.color = '#4ade80';
            }
        } else if (isSuccess === false) {
            statusText.style.color = '#f87171';
            if (statusIcon) {
                statusIcon.textContent = '[Error]';
                statusIcon.style.color = '#f87171';
            }
        } else if (isPending === true) {
            statusText.style.color = '#38bdf8';
            if (statusIcon) {
                statusIcon.textContent = '...';
                statusIcon.style.color = '#38bdf8';
            }
        } else {
            statusText.style.color = '#a1a1aa';
            if (statusIcon) {
                statusIcon.textContent = '';
            }
        }
    }

    /**
     * Initializes segmented tab button navigation across the Seerr authentication options.
     *
     * @param {Element} view - The active page view element.
     */
    function setupTabSwitching(view) {
        var tabButtons = view.querySelectorAll('.seerr-tab-btn');
        var tabPanes = view.querySelectorAll('.seerr-tab-pane');

        tabButtons.forEach(function (btn) {
            btn.addEventListener('click', function () {
                var targetTab = this.getAttribute('data-tab');

                tabButtons.forEach(function (b) {
                    var isActive = b === btn;
                    b.classList.toggle('active', isActive);
                    b.classList.toggle('is-active', isActive);
                    b.style.background = '';
                    b.style.color = '';
                });

                tabPanes.forEach(function (pane) {
                    pane.style.display = pane.id === 'tab-pane-' + targetTab ? 'block' : 'none';
                });
            });
        });
    }



    /**
     * Authenticates with Seerr using administrator credentials to retrieve the API key.
     *
     * @param {Element} view - The active page view element.
     */
    function loginWithSeerrCredentials(view) {
        var seerrUrlInput = view.querySelector('#txt-seerr-url');
        var seerrUrl = (seerrUrlInput ? seerrUrlInput.value : '').trim().replace(/\/+$/, '');
        var userInput = view.querySelector('#txt-seerr-email') || view.querySelector('#txt-seerr-user');
        var username = (userInput ? userInput.value : '').trim();
        var passInput = view.querySelector('#txt-seerr-password');
        var password = passInput ? passInput.value : '';

        if (!seerrUrl) {
            setSeerrStatus(view, 'Please enter the Seerr Server URL first.', false);
            return;
        }
        if (!username) {
            setSeerrStatus(view, 'Please enter your Seerr admin email or username.', false);
            return;
        }

        setSeerrStatus(view, 'Connecting to Seerr...', null, true);
        Dashboard.showLoadingMsg();

        ApiClient.ajax({
            type: 'POST',
            url: ApiClient.getUrl('Litefin/Seerr/Auth/Login'),
            contentType: 'application/json',
            dataType: 'json',
            data: JSON.stringify({
                SeerrUrl: seerrUrl,
                Username: username,
                Password: password
            })
        }).then(function (result) {
            Dashboard.hideLoadingMsg();
            if (result && result.Success) {
                var passField = view.querySelector('#txt-seerr-password');
                if (passField) passField.value = '';
                loadSeerrConfiguration(view);
                setSeerrStatus(view, 'Logged in successfully. Seerr API key acquired and saved.', true);
            } else {
                setSeerrStatus(view, result && result.Message ? result.Message : 'Authentication failed.', false);
            }
        }).catch(function (err) {
            Dashboard.hideLoadingMsg();
            var msg = err && err.responseJSON && (err.responseJSON.Message || err.responseJSON.message)
                ? (err.responseJSON.Message || err.responseJSON.message)
                : err && err.responseText
                    ? err.responseText
                    : 'Admin login failed. Check credentials.';
            setSeerrStatus(view, msg, false);
        });
    }

    /**
     * Toggles unsaved changes warning indicator for the Seerr Server URL.
     *
     * @param {Element} view - The active page view element.
     */
    function checkSeerrUrlChanged(view) {
        var state = view.__litefinState || {};
        var urlInput = view.querySelector('#txt-seerr-url');
        var currentUrl = urlInput ? urlInput.value.trim().replace(/\/+$/, '') : '';
        var warningEl = view.querySelector('#seerr-url-unsaved-warning');

        if (!warningEl) return;
        warningEl.style.display = (currentUrl !== (state.savedSeerrUrl || '')) ? 'block' : 'none';
    }

    /**
     * Toggles visibility between the configured API key card and the 3 connection methods.
     *
     * @param {Element} view - The active page view element.
     * @param {boolean} hasKey - True if an API key is currently saved.
     */
    function updateSeerrKeyVisibility(view, hasKey) {
        var methodsContainer = view.querySelector('#seerr-connection-methods');
        var configuredCard = view.querySelector('#seerr-key-configured-card');

        if (!methodsContainer || !configuredCard) return;

        if (hasKey) {
            methodsContainer.style.display = 'none';
            configuredCard.style.display = 'flex';
        } else {
            methodsContainer.style.display = 'block';
            configuredCard.style.display = 'none';
        }
    }

    /**
     * Resets the configured Seerr API key in plugin settings.
     *
     * @param {Element} view - The active page view element.
     */
    function resetSeerrKey(view) {
        if (!confirm('Are you sure you want to reset the Seerr API key? You will need to reconnect.')) {
            return;
        }

        Dashboard.showLoadingMsg();
        setSeerrStatus(view, 'Resetting API key...', null, true);

        ApiClient.getPluginConfiguration(PluginUniqueId).then(function (config) {
            config.SeerrApiKey = '';
            return ApiClient.updatePluginConfiguration(PluginUniqueId, config);
        }).then(function () {
            var keyInput = view.querySelector('#txt-seerr-api-key');
            if (keyInput) keyInput.value = '';

            updateSeerrKeyVisibility(view, false);
            setSeerrStatus(view, 'API key reset. Please select a connection method below to re-authenticate.', null);
            Dashboard.hideLoadingMsg();
        }).catch(function (err) {
            Dashboard.hideLoadingMsg();
            var errMsg = err && err.responseJSON && err.responseJSON.Message
                ? err.responseJSON.Message
                : 'Failed to reset API key.';
            setSeerrStatus(view, errMsg, false);
        });
    }

    /**
     * Loads the stored Seerr settings and populates the view.
     *
     * @param {Element} view - The active page view element.
     */
    function loadSeerrConfiguration(view) {
        var state = view.__litefinState || (view.__litefinState = {});

        ApiClient.getPluginConfiguration(PluginUniqueId).then(function (config) {
            state.savedSeerrUrl = (config.SeerrUrl || '').trim().replace(/\/+$/, '');
            var apiKey = (config.SeerrApiKey || '').trim();

            var urlInput = view.querySelector('#txt-seerr-url');
            var keyInput = view.querySelector('#txt-seerr-api-key');

            if (urlInput) urlInput.value = config.SeerrUrl || '';
            if (keyInput) keyInput.value = apiKey;

            checkSeerrUrlChanged(view);
            updateSeerrKeyVisibility(view, apiKey.length > 0);
        });
    }

    /**
     * Saves the Seerr Server URL independently to the plugin configuration.
     *
     * @param {Element} view - The active page view element.
     */
    function saveSeerrUrl(view) {
        var state = view.__litefinState || (view.__litefinState = {});
        var urlInput = view.querySelector('#txt-seerr-url');
        var seerrUrl = urlInput ? urlInput.value.trim().replace(/\/+$/, '') : '';

        if (!seerrUrl) {
            setSeerrStatus(view, 'Please enter a valid Seerr Server URL.', false);
            return;
        }

        urlInput.value = seerrUrl;
        setSeerrStatus(view, 'Saving Seerr URL...', null, true);
        Dashboard.showLoadingMsg();

        ApiClient.getPluginConfiguration(PluginUniqueId).then(function (config) {
            config.SeerrUrl = seerrUrl;
            return ApiClient.updatePluginConfiguration(PluginUniqueId, config);
        }).then(function () {
            state.savedSeerrUrl = seerrUrl;
            checkSeerrUrlChanged(view);
            setSeerrStatus(view, 'Seerr URL saved successfully.', true);
            Dashboard.hideLoadingMsg();
        }).catch(function (err) {
            Dashboard.hideLoadingMsg();
            var errMsg = err && err.responseJSON && err.responseJSON.Message
                ? err.responseJSON.Message
                : 'Unable to save Seerr URL.';
            setSeerrStatus(view, errMsg, false);
        });
    }

    /**
     * Saves manual Seerr settings (URL and API key).
     *
     * @param {Element} view - The active page view element.
     */
    function saveSeerrConfiguration(view) {
        var state = view.__litefinState || (view.__litefinState = {});
        Dashboard.showLoadingMsg();

        var seerrUrl = (view.querySelector('#txt-seerr-url').value || '').trim().replace(/\/+$/, '');
        var apiKey = (view.querySelector('#txt-seerr-api-key').value || '').trim();

        ApiClient.getPluginConfiguration(PluginUniqueId).then(function (config) {
            config.SeerrUrl = seerrUrl;
            config.SeerrApiKey = apiKey;
            return ApiClient.updatePluginConfiguration(PluginUniqueId, config);
        }).then(function () {
            state.savedSeerrUrl = seerrUrl;
            checkSeerrUrlChanged(view);
            updateSeerrKeyVisibility(view, apiKey.length > 0);
            setSeerrStatus(view, 'API key saved manually.', true);
            Dashboard.hideLoadingMsg();
        }).catch(function () {
            setSeerrStatus(view, 'Unable to save Seerr settings.', false);
            Dashboard.hideLoadingMsg();
        });
    }

    /**
     * Pings the Seerr server endpoint to verify reachability from the Emby host.
     *
     * @param {Element} view - The active page view element.
     */
    function testSeerrServer(view) {
        var urlInput = view.querySelector('#txt-seerr-url');
        var seerrUrl = urlInput ? urlInput.value.trim().replace(/\/+$/, '') : '';

        if (!seerrUrl) {
            setSeerrStatus(view, 'Please enter a Seerr Server URL to test.', false);
            return;
        }

        setSeerrStatus(view, 'Pinging Seerr server...', null, true);
        Dashboard.showLoadingMsg();

        ApiClient.ajax({
            type: 'POST',
            url: ApiClient.getUrl('Litefin/Seerr/Status/Ping'),
            contentType: 'application/json',
            dataType: 'json',
            data: JSON.stringify({ SeerrUrl: seerrUrl })
        }).then(function (result) {
            Dashboard.hideLoadingMsg();
            if (result && result.reachable) {
                setSeerrStatus(view, result.message || 'Seerr server is online and reachable.', true);
            } else {
                setSeerrStatus(view, result && result.message ? result.message : 'Seerr server is unreachable. Check URL and network.', false);
            }
        }).catch(function (err) {
            Dashboard.hideLoadingMsg();
            var errMsg = err && err.responseJSON && err.responseJSON.message
                ? err.responseJSON.message
                : 'Unable to reach Seerr server. Check host, port, and firewall.';
            setSeerrStatus(view, errMsg, false);
        });
    }

    /**
     * Performs a full integration test with the configured URL and API key.
     *
     * @param {Element} view - The active page view element.
     */
    function testSeerrConnection(view) {
        var urlInput = view.querySelector('#txt-seerr-url');
        var keyInput = view.querySelector('#txt-seerr-api-key');
        var seerrUrl = urlInput ? urlInput.value.trim().replace(/\/+$/, '') : '';
        var apiKey = keyInput ? keyInput.value.trim() : '';

        if (!seerrUrl) {
            setSeerrStatus(view, 'Please enter a Seerr Server URL to test.', false);
            return;
        }

        setSeerrStatus(view, 'Testing connection...', null, true);
        Dashboard.showLoadingMsg();

        ApiClient.ajax({
            type: 'POST',
            url: ApiClient.getUrl('Litefin/Seerr/Status/Test'),
            contentType: 'application/json',
            dataType: 'json',
            data: JSON.stringify({
                SeerrUrl: seerrUrl,
                SeerrApiKey: apiKey
            })
        }).then(function (result) {
            Dashboard.hideLoadingMsg();
            if (result && result.available) {
                setSeerrStatus(view, result.message || 'Connected and authenticated successfully.', true);
            } else {
                setSeerrStatus(view, result && result.message ? result.message : 'Seerr is not reachable or rejected the key.', false);
            }
        }).catch(function (err) {
            Dashboard.hideLoadingMsg();
            var errMsg = err && err.responseJSON && err.responseJSON.message
                ? err.responseJSON.message
                : 'Connection test failed. Check settings.';
            setSeerrStatus(view, errMsg, false);
        });
    }

    /**
     * Loads and renders the table of user settings backups stored on the server.
     *
     * @param {Element} view - The active page view element.
     */
    function loadBackupsList(view) {
        Dashboard.showLoadingMsg();

        ApiClient.getPluginConfiguration(PluginUniqueId).then(function (config) {
            var html = '';
            var backups = (config.Backups || []).map(function (b, index) {
                return { data: b, originalIndex: index };
            });

            backups.sort(function (a, b) {
                return new Date(b.data.DateCreated) - new Date(a.data.DateCreated);
            });

            if (backups.length === 0) {
                html = '<tr><td colspan="8" style="padding: 2.5em 0; text-align: center; opacity: 0.65;">No stored user backups found.</td></tr>';
            } else {
                backups.forEach(function (item) {
                    var backup = item.data;
                    var index = item.originalIndex;
                    var dateStr = new Date(backup.DateCreated).toLocaleString();
                    var backupName = backup.Name || '(Auto)';
                    var platformMap = { web: 'Web', tizen: 'Tizen', webos: 'WebOS' };
                    var platformDisplay = platformMap[backup.Platform] || backup.Platform || 'Unknown';
                    var appVersion = backup.AppVersion || '1.0.0';

                    html += '<tr class="backup-row">';
                    html += '  <td class="backup-cell-primary">' + escapeHtml(backupName) + '</td>';
                    html += '  <td class="backup-cell-muted">' + escapeHtml(backup.DeviceName || 'Unknown') + '</td>';
                    html += '  <td class="backup-cell-text">' + escapeHtml(backup.Username) + '</td>';
                    html += '  <td class="backup-cell-mono">' + escapeHtml(backup.DeviceId || 'Unknown') + '</td>';
                    html += '  <td class="backup-cell-text">' + escapeHtml(platformDisplay) + '</td>';
                    html += '  <td class="backup-cell-text">' + escapeHtml(appVersion) + '</td>';
                    html += '  <td class="backup-cell-muted">' + dateStr + '</td>';
                    html += '  <td class="backup-cell-actions">';
                    html += '    <button is="emby-button" type="button" class="raised btnExportBackup" data-index="' + index + '">Export</button>';
                    html += '    <button is="emby-button" type="button" class="raised btnDeleteBackup" data-index="' + index + '">Delete</button>';
                    html += '  </td>';
                    html += '</tr>';
                });
            }

            var body = view.querySelector('#backups-table-body');
            if (body) {
                body.innerHTML = html;

                var exportBtns = body.querySelectorAll('.btnExportBackup');
                exportBtns.forEach(function (btn) {
                    btn.addEventListener('click', function () {
                        var index = parseInt(this.getAttribute('data-index'));
                        exportBackupRecord(index);
                    });
                });

                var deleteBtns = body.querySelectorAll('.btnDeleteBackup');
                deleteBtns.forEach(function (btn) {
                    btn.addEventListener('click', function () {
                        var index = parseInt(this.getAttribute('data-index'));
                        deleteBackupRecord(view, index);
                    });
                });
            }

            Dashboard.hideLoadingMsg();
        });
    }

    /**
     * Deletes a user backup record after confirmation.
     *
     * @param {Element} view - The active page view element.
     * @param {number} index - The index of the backup record to delete.
     */
    function deleteBackupRecord(view, index) {
        if (!confirm("Are you sure you want to delete this user's backup record?")) {
            return;
        }

        Dashboard.showLoadingMsg();
        ApiClient.getPluginConfiguration(PluginUniqueId).then(function (config) {
            config.Backups.splice(index, 1);
            return ApiClient.updatePluginConfiguration(PluginUniqueId, config);
        }).then(function () {
            loadBackupsList(view);
        });
    }

    /**
     * Exports a backup record to a downloaded JSON file.
     *
     * @param {number} index - Index of the backup record in configuration.
     */
    function exportBackupRecord(index) {
        Dashboard.showLoadingMsg();
        ApiClient.getPluginConfiguration(PluginUniqueId).then(function (config) {
            var backup = config.Backups[index];
            if (!backup) {
                Dashboard.hideLoadingMsg();
                return;
            }
            var blob = new Blob([JSON.stringify([backup], null, 2)], { type: 'application/json' });
            var url = URL.createObjectURL(blob);
            var a = document.createElement('a');
            a.href = url;
            a.download = (backup.Name || backup.DeviceName || 'backup').replace(/[^a-zA-Z0-9 _-]/g, '') + '_' + Date.now() + '.json';
            document.body.appendChild(a);
            a.click();
            document.body.removeChild(a);
            URL.revokeObjectURL(url);
            Dashboard.hideLoadingMsg();
        });
    }

    // =========================================================================
    // CLIENT DIAGNOSTIC LOGS MANAGEMENT
    // =========================================================================

    /**
     * Retrieves the list of uploaded client diagnostic logs from the server and renders the table.
     *
     * @param {Element} view - The active page view element.
     */
    function loadClientLogsList(view) {
        var tableBody = view.querySelector('#client-logs-table-body');
        var countBadge = view.querySelector('#client-logs-count-badge');
        if (!tableBody) return;

        // Query the server for available diagnostic log documents
        ApiClient.ajax({
            type: 'GET',
            url: ApiClient.getUrl('Litefin/ClientLogs'),
            dataType: 'json'
        }).then(function (logs) {
            var logList = Array.isArray(logs) ? logs : [];

            // Update header count badge with total available entries
            if (countBadge) {
                countBadge.textContent = logList.length + (logList.length === 1 ? ' log' : ' logs');
            }

            // Handle empty state gracefully with a friendly callout row
            if (logList.length === 0) {
                tableBody.innerHTML = '<tr><td colspan="4" style="padding: 2.5em 0; text-align: center; opacity: 0.65;">No client diagnostic logs uploaded yet. When a Litefin client uploads logs, they will appear here.</td></tr>';
                return;
            }

            var html = '';
            logList.forEach(function (log) {
                var dateStr = log.DateModified ? new Date(log.DateModified).toLocaleString() : 'Unknown';
                var safeName = escapeHtml(log.Name);
                var safeSize = escapeHtml(log.SizeFormatted || (log.Size + ' B'));

                html += '<tr class="backup-row">';
                html += '  <td class="backup-cell-primary" style="font-family: monospace; font-size: 0.9em;">' + safeName + '</td>';
                html += '  <td class="backup-cell-mono">' + safeSize + '</td>';
                html += '  <td class="backup-cell-muted">' + dateStr + '</td>';
                html += '  <td class="backup-cell-actions">';
                html += '    <button is="emby-button" type="button" class="raised btnExportBackup btnViewLog" data-name="' + safeName + '" data-size="' + safeSize + '">View</button>';
                html += '    <button is="emby-button" type="button" class="raised btnExportBackup btnDownloadLog" data-name="' + safeName + '">Download</button>';
                html += '    <button is="emby-button" type="button" class="raised btnDeleteBackup btnDeleteLog" data-name="' + safeName + '">Delete</button>';
                html += '  </td>';
                html += '</tr>';
            });

            tableBody.innerHTML = html;

            // Bind click handlers for viewing logs in the modal
            tableBody.querySelectorAll('.btnViewLog').forEach(function (btn) {
                btn.addEventListener('click', function () {
                    var name = this.getAttribute('data-name');
                    var size = this.getAttribute('data-size');
                    viewClientLogInModal(view, name, size);
                });
            });

            // Bind click handlers for direct log downloads
            tableBody.querySelectorAll('.btnDownloadLog').forEach(function (btn) {
                btn.addEventListener('click', function () {
                    var name = this.getAttribute('data-name');
                    downloadClientLog(name);
                });
            });

            // Bind click handlers for individual log deletions
            tableBody.querySelectorAll('.btnDeleteLog').forEach(function (btn) {
                btn.addEventListener('click', function () {
                    var name = this.getAttribute('data-name');
                    deleteClientLog(view, name);
                });
            });
        }).catch(function (err) {
            tableBody.innerHTML = '<tr><td colspan="4" style="padding: 2.5em 0; text-align: center; color: #f87171;">Failed to load client diagnostic logs: ' + escapeHtml(err.message || 'Server error') + '</td></tr>';
        });
    }

    /**
     * Fetches log file content and displays it inside the frosted modal viewer.
     *
     * @param {Element} view - The active page view element.
     * @param {string} logName - Target file name.
     * @param {string} logSize - Human-readable size string.
     */
    function viewClientLogInModal(view, logName, logSize) {
        var modal = view.querySelector('#client-log-modal');
        var titleEl = view.querySelector('#modal-log-title');
        var sizeEl = view.querySelector('#modal-log-size-badge');
        var contentEl = view.querySelector('#modal-log-content');
        if (!modal || !contentEl) return;

        // Set pending presentation state
        if (titleEl) titleEl.textContent = logName;
        if (sizeEl) sizeEl.textContent = logSize ? '(' + logSize + ')' : '';
        contentEl.textContent = 'Loading log document from server...';
        modal.style.display = 'flex';

        // Stash active file name for copy and download actions in modal header
        modal.__activeLogName = logName;

        // Retrieve raw text document from server
        ApiClient.ajax({
            type: 'GET',
            url: ApiClient.getUrl('Litefin/ClientLogs/' + encodeURIComponent(logName)),
            dataType: 'text'
        }).then(function (text) {
            contentEl.textContent = text || '(Log file is empty)';
        }).catch(function (err) {
            contentEl.textContent = 'Error loading log file: ' + (err.message || 'HTTP error');
        });
    }

    /**
     * Closes the active client log viewer modal.
     *
     * @param {Element} view - The active page view element.
     */
    function closeClientLogModal(view) {
        var modal = view.querySelector('#client-log-modal');
        if (modal) {
            modal.style.display = 'none';
            modal.__activeLogName = null;
        }
    }

    /**
     * Initiates a browser download for a specific client diagnostic log file.
     *
     * @param {string} logName - Target file name.
     */
    function downloadClientLog(logName) {
        if (!logName) return;
        var url = ApiClient.getUrl('Litefin/ClientLogs/' + encodeURIComponent(logName));
        var a = document.createElement('a');
        a.href = url;
        a.download = logName;
        document.body.appendChild(a);
        a.click();
        document.body.removeChild(a);
    }

    /**
     * Deletes a specific client diagnostic log file after user confirmation.
     *
     * @param {Element} view - The active page view element.
     * @param {string} logName - Target file name to remove.
     */
    function deleteClientLog(view, logName) {
        if (!logName) return;
        if (!confirm('Are you sure you want to delete "' + logName + '"?')) {
            return;
        }

        Dashboard.showLoadingMsg();
        ApiClient.ajax({
            type: 'DELETE',
            url: ApiClient.getUrl('Litefin/ClientLogs/' + encodeURIComponent(logName))
        }).then(function () {
            Dashboard.hideLoadingMsg();
            loadClientLogsList(view);
        }).catch(function (err) {
            Dashboard.hideLoadingMsg();
            alert('Failed to delete log: ' + (err.message || 'Server error'));
        });
    }

    /**
     * Clears all stored client diagnostic log files after user confirmation.
     *
     * @param {Element} view - The active page view element.
     */
    function clearAllClientLogs(view) {
        if (!confirm('Are you sure you want to permanently delete ALL client diagnostic logs?')) {
            return;
        }

        Dashboard.showLoadingMsg();
        ApiClient.ajax({
            type: 'DELETE',
            url: ApiClient.getUrl('Litefin/ClientLogs')
        }).then(function () {
            Dashboard.hideLoadingMsg();
            loadClientLogsList(view);
        }).catch(function (err) {
            Dashboard.hideLoadingMsg();
            alert('Failed to clear logs: ' + (err.message || 'Server error'));
        });
    }

    /**
     * Binds all permanent UI event handlers once when the view element is constructed.
     *
     * @param {Element} view - The active page view element.
     */
    function bindOnce(view) {
        setupTabSwitching(view);

        var seerrUrlInput = view.querySelector('#txt-seerr-url');
        if (seerrUrlInput) {
            seerrUrlInput.oninput = function () { checkSeerrUrlChanged(view); };
            seerrUrlInput.onchange = function () { checkSeerrUrlChanged(view); };
        }

        var testServerBtn = view.querySelector('#btn-test-seerr-server');
        if (testServerBtn) testServerBtn.onclick = function () { testSeerrServer(view); };

        var resetKeyBtn = view.querySelector('#btn-reset-seerr-key');
        if (resetKeyBtn) resetKeyBtn.onclick = function () { resetSeerrKey(view); };

        var saveUrlBtn = view.querySelector('#btn-save-seerr-url');
        if (saveUrlBtn) saveUrlBtn.onclick = function () { saveSeerrUrl(view); };



        var loginSeerrBtn = view.querySelector('#btn-login-seerr');
        if (loginSeerrBtn) loginSeerrBtn.onclick = function () { loginWithSeerrCredentials(view); };

        var saveSeerrBtn = view.querySelector('#btn-save-seerr');
        if (saveSeerrBtn) saveSeerrBtn.onclick = function () { saveSeerrConfiguration(view); };

        var testSeerrBtn = view.querySelector('#btn-test-seerr');
        if (testSeerrBtn) testSeerrBtn.onclick = function () { testSeerrConnection(view); };

        var exportBtn = view.querySelector('#btn-export-backups');
        if (exportBtn) {
            exportBtn.onclick = function () {
                Dashboard.showLoadingMsg();
                ApiClient.getPluginConfiguration(PluginUniqueId).then(function (config) {
                    var backups = config.Backups || [];
                    var blob = new Blob([JSON.stringify(backups, null, 2)], { type: 'application/json' });
                    var url = URL.createObjectURL(blob);
                    var a = document.createElement('a');
                    a.href = url;
                    a.download = 'litefin_backups_export_' + Date.now() + '.json';
                    document.body.appendChild(a);
                    a.click();
                    document.body.removeChild(a);
                    URL.revokeObjectURL(url);
                    Dashboard.hideLoadingMsg();
                });
            };
        }

        var refreshBtn = view.querySelector('#btn-refresh-backups');
        if (refreshBtn) {
            refreshBtn.onclick = function () {
                loadBackupsList(view);
            };
        }

        var fileInput = view.querySelector('#import-backups-file');
        var importBtn = view.querySelector('#btn-import-backups');
        if (importBtn && fileInput) {
            importBtn.onclick = function () {
                fileInput.click();
            };

            fileInput.onchange = function (e) {
                var file = e.target.files[0];
                if (!file) return;
                var reader = new FileReader();
                reader.onload = function (evt) {
                    try {
                        var imported = JSON.parse(evt.target.result);
                        if (!Array.isArray(imported)) {
                            alert('Invalid backup file. Must be a JSON array of backup records.');
                            return;
                        }

                        Dashboard.showLoadingMsg();
                        ApiClient.getPluginConfiguration(PluginUniqueId).then(function (config) {
                            var currentBackups = config.Backups || [];

                            imported.forEach(function (imp) {
                                if (!imp.Id || !imp.UserId || !imp.Settings) return;

                                var idx = currentBackups.findIndex(function (b) {
                                    return b.Id === imp.Id;
                                });

                                if (idx !== -1) {
                                    currentBackups[idx] = imp;
                                } else {
                                    currentBackups.push(imp);
                                }
                            });

                            config.Backups = currentBackups;
                            ApiClient.updatePluginConfiguration(PluginUniqueId, config).then(function () {
                                loadBackupsList(view);
                                alert('Backups successfully merged and imported.');
                            });
                        });
                    } catch (err) {
                        alert('Error parsing file: ' + err.message);
                    }
                    fileInput.value = '';
                };
                reader.readAsText(file);
            };
        }

        // Client Diagnostic Logs Toolbar Actions
        var refreshLogsBtn = view.querySelector('#btn-refresh-client-logs');
        if (refreshLogsBtn) {
            refreshLogsBtn.onclick = function () {
                loadClientLogsList(view);
            };
        }

        var clearLogsBtn = view.querySelector('#btn-clear-client-logs');
        if (clearLogsBtn) {
            clearLogsBtn.onclick = function () {
                clearAllClientLogs(view);
            };
        }

        // Modal Action Buttons
        var closeModalBtn = view.querySelector('#btn-modal-close-log');
        if (closeModalBtn) {
            closeModalBtn.onclick = function () {
                closeClientLogModal(view);
            };
        }

        var modalOverlay = view.querySelector('#client-log-modal');
        if (modalOverlay) {
            modalOverlay.onclick = function (e) {
                if (e.target === modalOverlay) {
                    closeClientLogModal(view);
                }
            };
        }

        var copyLogBtn = view.querySelector('#btn-modal-copy-log');
        if (copyLogBtn) {
            copyLogBtn.onclick = function () {
                var contentEl = view.querySelector('#modal-log-content');
                if (contentEl && navigator.clipboard && navigator.clipboard.writeText) {
                    navigator.clipboard.writeText(contentEl.textContent).then(function () {
                        var span = copyLogBtn.querySelector('span');
                        if (span) span.textContent = 'Copied!';
                        setTimeout(function () {
                            if (span) span.textContent = 'Copy Log';
                        }, 2000);
                    }).catch(function () {});
                }
            };
        }

        var downloadModalLogBtn = view.querySelector('#btn-modal-download-log');
        if (downloadModalLogBtn) {
            downloadModalLogBtn.onclick = function () {
                var modal = view.querySelector('#client-log-modal');
                if (modal && modal.__activeLogName) {
                    downloadClientLog(modal.__activeLogName);
                }
            };
        }
    }

    /**
     * Emby Web view controller constructor for the Litefin plugin configuration page.
     *
     * @param {Element} view - The root HTML view element.
     * @param {Object} params - Route parameters.
     */
    function View(view, params) {
        BaseView.apply(this, arguments);
        bindOnce(view);
    }

    Object.assign(View.prototype, BaseView.prototype);

    View.prototype.onResume = function (options) {
        BaseView.prototype.onResume.apply(this, arguments);
        var view = this.view;
        loadSeerrConfiguration(view);
        loadBackupsList(view);
        loadClientLogsList(view);
    };

    View.prototype.onPause = function () {
        // Delegate cleanup to base implementation
        BaseView.prototype.onPause.apply(this, arguments);
    };

    return View;
});
