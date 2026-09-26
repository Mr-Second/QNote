# Code Signing Policy

QNote release packages (`.msix`) are Authenticode-signed so Windows can verify
their publisher and integrity.

## Current state: self-signed

Releases are currently signed with a self-signed certificate (`CN=QNote`),
which requires a one-time manual certificate trust step before installation —
see [msix-install.md](msix-install.md). The certificate thumbprint is
published in that document and in each release's notes.

## Planned: SignPath.io

Free code signing provided by [SignPath.io](https://signpath.io), certificate
by SignPath Foundation, has been applied for under their open-source program.
Once approved, release packages will be signed with a publicly trusted
certificate (chained to a GlobalSign root), so installation will no longer
require any manual certificate trust.

Because the certificate subject becomes part of the MSIX package identity,
switching from the self-signed certificate to the SignPath certificate is a
**one-time breaking change**: existing installs must be uninstalled and
reinstalled once (note data is unaffected). After the switch, the publisher
identity will never change again.

## Key handling

- Private keys are **never** stored in this repository.
- CI signing uses GitHub Actions secrets only; the signing workflow builds
  from this repository on GitHub-hosted runners.
- Signatures are timestamped, so installed packages remain valid after the
  certificate expires.
