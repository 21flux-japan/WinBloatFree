# Code signing policy

Free code signing provided by [SignPath.io](https://signpath.io/), certificate by [SignPath Foundation](https://signpath.org/).

This page is the project's code signing policy, as required by the [SignPath Foundation conditions](https://signpath.org/terms.html).

## What is signed

Only `WinBloatFree.exe` from this repository is signed. It is a single self-contained Windows executable, built from the source in [github.com/21flux-japan/WinBloatFree](https://github.com/21flux-japan/WinBloatFree). Third-party or upstream binaries are not signed with this project's certificate.

Signed files show **SignPath Foundation** as the publisher, because the certificate is issued to the foundation, not to 21 FLUX.

Until SignPath accepts the project and the CI secrets are set, GitHub Release attachments stay unsigned. After that, every tagged release (`v*`) is signed before it is published.

## How a release is built and signed

1. Windows release artifacts are built from this public repository by GitHub Actions, on GitHub-hosted `windows-latest` runners, from the tagged commit. The workflow is [.github/workflows/build.yml](.github/workflows/build.yml).
2. The job tests the engine, publishes the portable exe, and checks that the file's product name is `WinBloatFree` and that a product version is present.
3. The unsigned exe is uploaded as a GitHub Actions artifact and submitted to SignPath with [`signpath/github-action-submit-signing-request`](https://github.com/SignPath/github-action-submit-signing-request). SignPath checks that the artifact came from this GitHub-hosted workflow.
4. The artifact configuration in [.signpath/artifact-configurations/default.xml](.signpath/artifact-configurations/default.xml) (copied into the SignPath project as the `default` configuration) Authenticode-signs `WinBloatFree.exe` and requires `ProductName=WinBloatFree` and a `ProductVersion` that matches the build.
5. [Michael (21 FLUX)](https://github.com/21flux-japan) reviews and manually approves each signing request in SignPath. The workflow waits for that approval, then replaces the unsigned exe with the signed file, recomputes the SHA256 checksum, and attaches both to the GitHub Release.

The private key never leaves SignPath's hardware security module. This project does not store a code signing certificate or private key.

Signing is skipped when the `SIGNPATH_API_TOKEN` secret is not set, so ordinary pushes and pull requests keep building as they do today.

## Team roles

This is a single-maintainer project. Michael (21 FLUX) holds every role:

- Authors (committers): [Michael (21 FLUX)](https://github.com/21flux-japan). Trusted to modify the source code, build scripts, and CI configuration without additional reviews.
- Reviewers: [Michael (21 FLUX)](https://github.com/21flux-japan). Every change proposed by someone who is not a committer, including pull requests, is reviewed before it is merged.
- Approvers: [Michael (21 FLUX)](https://github.com/21flux-japan). Approves each SignPath signing request and decides whether that release may be signed.

All team members must use multi-factor authentication for GitHub and for SignPath.

## Privacy policy

This program will not transfer any information to other networked systems unless specifically requested by the user or the person installing or operating it.

WinBloatFree contains no networking code. It does not collect personal data, does not check for updates, and does not send usage data or crash reports. The only time a browser is opened is when you click "Source code", "Report an issue", or "Get it from the Store". The test suite fails the build if networking APIs show up in the source. There are no third-party analytics or other online services whose policies would apply.

## Conditions this project follows

- Releases are built from the source in this repository, in a verifiable CI job. Build scripts and workflow files are reviewed with the same care as application code.
- Only this project's own binaries are submitted for signing.
- The app does not change the PC until the user reviews the selected items and confirms. Undo / History can reverse those settings. There is no installer, so there is nothing to uninstall beyond deleting the exe and, if you want, the local history under `C:\ProgramData\WinBloatFree`.
