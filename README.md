# Litefin Plugin For Emby Server

A high-performance Emby Server companion extension built for the Litefin client ecosystem. Delivers server-accelerated hero carousels, batch item queries, chronological merged rows, Seerr request proxies, and secure client configuration sync.

---

## Features

* **Server-Side Hero Spotlight (`/Litefin/Items/Hero`)**: Pre-filters, deduplicates, and aggregates featured hero media across user libraries in one fast shot. No client-side waterfall queries.
* **Chronological Merged Rows (`/Litefin/MergedRows/ContinueAndNextUp`)**: Merges Continue Watching and Next Up rows on the server, deduplicates series episodes, and orders everything strictly by timestamp (Plex-style).
* **Batch Media & Thumbnails (`/Litefin/Items/Latest`, `/Litefin/Items/Thumbnails`)**: Pulls latest items and pre-calculated library card thumbnails across multiple parent libraries in a single round-trip.
* **Role-Aware Filmography (`/Litefin/Persons/{personId}/Items`)**: Queries a person's movies, series, and songs with their specific character role or contribution intact.
* **Reverse Collection Lookup (`/Litefin/Items/{itemId}/Collections`)**: Reverse-resolves which BoxSet collections contain a given movie or show without burning client cycles scanning every collection folder.
* **Multi-Snapshot Settings Backups**: Save unlimited client configuration snapshots. Tag them with custom names or let Litefin auto-tag by device metadata. Restores work across devices; overwrites and deletions are locked to the owning user.
* **Admin Dashboard Hub**: Manage backups directly inside Emby Dashboard (**Settings** &rarr; **Plugins** &rarr; **Litefin**). Export snapshots as JSON, import/merge existing backups, or prune stale records.
* **Seerr Integration & Proxy**: Holds your Seerr server URL and API key server-side. Exposes authenticated discovery, search, details, and request endpoints mapped to the caller's Emby user token. Includes three connection workflows:
  * **Quick Connect Pairing**: One-click pairing verified in your web client.
  * **Admin Login**: Authenticate with admin credentials once to pull the key automatically.
  * **Manual Key**: Good old-fashioned copy-paste.
* **Zero Privacy Leaks**: Sensitive access tokens, server addresses, and temporary session state are stripped client-side before any backup hits the wire.

---

## Installation

### Manual Install
1. Grab the latest `Litefin.Emby.Plugin.dll` from Releases (or build it yourself below).
2. Drop `Litefin.Emby.Plugin.dll` into your Emby Server plugins folder:
   * **Windows (Service / AppData)**: `%AppData%\Emby-Server\programdata\plugins\`
   * **Windows (Portable / Custom)**: `<Emby-Install-Dir>\programdata\plugins\`
   * **Linux / Docker**: `/var/lib/emby/programdata/plugins/` or `/config/plugins/`
   * **macOS**: `~/emby-server/programdata/plugins/` or `~/.config/emby-server/programdata/plugins/`
   * **Android**: `/storage/emulated/0/Android/data/com.emby.embyserver/files/programdata/plugins/`
3. Restart Emby Server. Done.

For custom data layouts, check the official [Emby Server Data Folder docs](https://emby.media/support/articles/Server-Data-Folder.html).

---

## Build and Development

### Prerequisites
* [.NET 8.0 or 9.0 SDK](https://dotnet.microsoft.com/download)

### Compilation
From the repo root:

```bash
dotnet build -c Release
```

Output binary lands at:
```
bin/Release/netstandard2.0/Litefin.Emby.Plugin.dll
```

On Windows, the `PostBuild` task automatically detects running Emby Server installations and deploys the DLL directly into your `programdata\plugins\` folder.

---

## Configuration

* **Client**: In Litefin, open **Settings** &rarr; **Backup & Restore** to create, preview, restore, or delete saved snapshots.
* **Admin**: In Emby Web, go to **Settings** &rarr; **Plugins** &rarr; **Litefin** to set up your Seerr server, manage backup snapshots, or run connection tests.

### Storage Location
Plugin options and backups are saved as XML in Emby's configuration directory:
* **Windows**: `<Emby-Data-Folder>\plugins\configurations\Litefin.Emby.Plugin.xml`
* **Linux / Docker**: `/config/plugins/configurations/Litefin.Emby.Plugin.xml`
