# QNote Privacy Policy

**Last updated: 2026-10-24**

QNote ("the app") is a personal, non-commercial open-source project
(https://github.com/Mr-Second/QNote). This policy explains what data the app
handles. QNote is **local-first**: cloud synchronization is optional and uses a
storage service you choose. QNote does not operate its own account or cloud
storage service.

## Data the app stores

Content you create in QNote — notes, categories, task lists, tasks, steps,
images, attachments and backup archives — is stored **locally on your device**, in the app's
private data directory (next to `QNote.exe` in a `data\` folder for the
portable edition, under `%APPDATA%` otherwise, or the MSIX package's
virtualized equivalent). Uninstalling the app may delete this data.

## Data the app collects or transmits

QNote:

- requires **no QNote account**; Microsoft sign-in is optional for OneDrive sync
- collects **no telemetry, analytics, or usage statistics**
- sends your content to **your selected provider only when you configure sync**
- contains **no advertising or tracking SDKs**

## Optional cloud synchronization

When you configure synchronization, QNote uploads and downloads notes, categories,
task lists, tasks, steps, images, attachments and deletion records using either
your Microsoft OneDrive app folder or the WebDAV destination you provide.
Synchronization runs automatically while QNote is running, and can also be
triggered manually. Changes and deletions can propagate between devices.

OneDrive sign-in uses Microsoft's authentication services. The Windows account
broker manages authentication, with local account/cache information protected
for your Windows user. WebDAV configuration and credentials are stored locally
using Windows current-user DPAPI protection. QNote does not send these credentials
to a developer-operated server.

Cloud content is not additionally encrypted by QNote unless you enable the
optional end-to-end encryption feature. When enabled, AES-256-GCM protects note
and task content, images, attachments, category/list definitions and deletion
records. Cloud file names, entry identifiers, container clocks and encryption
configuration remain visible to the storage provider.

QNote remembers a derived encryption key locally, protected for your Windows
user; it does not persist the encryption password. This does not encrypt your
local database. A new device requires the same encryption password. QNote cannot
recover a lost password: without an unlocked device or usable backup, encrypted
cloud content cannot be recovered. Turning encryption off converts cloud content
back to unencrypted storage after confirmation.

HTTPS protects requests in transit. If you explicitly configure HTTP WebDAV,
account credentials and unencrypted content can be observed on the network;
content encryption does not protect HTTP account credentials.

Microsoft and your WebDAV provider process requests under their own policies.
Removing local configuration or signing out does not delete existing cloud
content. You can manage or delete it through your provider.

## Update checks

The app can check for new versions at startup (configurable in Settings), or when
you request a manual check. Portable update checks contact the GitHub Releases
API. The Store edition uses Microsoft Store update services; downloading updates
uses Microsoft or GitHub. These services can receive normal network request
information, such as your IP address and request headers, under their own policies.

## Crash diagnostics

If the app crashes, it writes a local crash dump file (memory snapshot and a
text summary) into the app's local `CrashDumps` directory. These files
**never leave your device automatically** — they exist solely for you to
share voluntarily when reporting a bug. You can delete them at any time.

## Backups

Manual backups (`.qns` files) are saved to a location you choose and can optionally
be AES-256 encrypted with a backup password. QNote also creates local automatic
backups before operations such as restore, replacement and cloud encryption
conversion. Backup archives are not uploaded by the synchronization feature.
Sync credentials and remembered encryption keys are not included in database
backup archives. You control any backup files you share or upload yourself.

## Third-party services

QNote integrates Microsoft sign-in and OneDrive for optional synchronization,
and connects to user-configured WebDAV services. It also uses Microsoft Store or
GitHub for updates. It does not integrate analytics, advertising or tracking
services.

## Changes to this policy

Any changes to this policy will be published in the project repository and
noted in release notes.

## Contact

Questions or privacy concerns: open an issue at
https://github.com/Mr-Second/QNote/issues.
