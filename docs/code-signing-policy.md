# Code Signing Policy

QNote currently ships **no code-signed artifacts**; download integrity is
attested by SHA256 checksums instead.

## Current state (v1.2.0+, portable-channel switch)

- **Microsoft Store (MSIX)**: the installed package carries Microsoft's
  Store certificate chain — Windows accepts it with no manual trust step,
  and updates are delivered by the Store.
- **GitHub portable zips**: **not signed**. Every release publishes a
  `<zip>.sha256` sidecar next to each zip plus a combined `SHA256SUMS.txt`:

  ```powershell
  Get-FileHash .\QNote_1.2.0.0_win-x64_self-contained.zip -Algorithm SHA256
  ```

The release workflow (`.github/workflows/release.yml`) contains no signing
steps and references no secrets — it only builds, zips, checksums and
publishes.

## History

- **Self-signed MSIX (v1.0.1 – v1.1.0, discontinued)**: GitHub releases
  shipped a `CN=QNote` self-signed MSIX plus a certificate-trust ritual
  (`install.ps1` importing `qnote.cer` into the machine root store). The
  channel was retired with the v1.2.0 switch to portable zips, and the old
  releases' MSIX assets are being removed so no new user installs a
  self-signed identity that can no longer receive updates.
- **SignPath.io**: the free open-source code-signing application was
  declined; there is no plan to reapply. If a publicly trusted certificate
  ever becomes available, signing the portable zips would not break either
  channel — unlike the old MSIX Publisher trap (the certificate subject was
  part of the package identity, so a subject change forced a
  uninstall/reinstall), zip signing has no identity implications.

## Key handling

- No private keys exist for this project; nothing signing-related is stored
  in this repository or in CI secrets.
- Checksums are computed by the release workflow on GitHub-hosted runners
  from the exact bytes it uploads, so they attest the build produced from
  this repository's tagged commit.
