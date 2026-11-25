# WinBloatFree

A small, portable Windows 10 and 11 tool that turns off telemetry, ads, suggestions and preinstalled apps you do not want. Every change is a checkbox, so you decide exactly what is removed or turned off.

- One `.exe`. No installer, nothing to set up.
- Works offline. The app makes no network calls of any kind.
- No telemetry, no accounts, no ads, no tracking.
- No scripts are downloaded or run. All logic is in C# and uses Windows APIs directly.
- Every change is saved before it is made and can be undone from Undo / History.
- Open source under the MIT license.

## Made by 21 FLUX

Windows used to be a good, simple system. Today it comes with apps you didn't ask for, ads, and constant data collection. With so much personal data being leaked and stolen, it's time to take back control. WinBloatFree lets you choose what Windows keeps, and it never collects anything itself.

> **Use at your own risk.** WinBloatFree changes system settings, services and installed apps. It is tested with care, but every PC is different. Prefer a System Restore point when the box is available, read the explanation of each change, and use Undo / History if something does not work the way you want. The software comes with no warranty (see [LICENSE](LICENSE)).

## How it works

1. Start `WinBloatFree.exe`. Windows asks for administrator rights, because most changes affect the whole PC.
2. The app checks this PC and shows the current status of every tweak: **Currently on**, **Already off**, **Partly on**, **Not installed** or **Not applicable**. When a tweak is partly on, the detail pane lists which parts are already done and which are still on.
3. Pick a preset or tick boxes yourself. The right pane shows what each tweak does, exactly which registry values, services, tasks or apps change, and how undo works.
4. Select **Review changes**. Nothing has changed yet. The review lists every change by category and risk.
5. Select **Apply**. A registry backup and an undo history file are always saved. If the restore point box is ticked, a System Restore point is made first. If System Restore is off, the box offers to turn it on for the system drive, and is unticked by default. If the restore point cannot be created, you can continue without it or cancel. Nothing is aborted automatically.

Each tweak has a risk label:

| Risk | Meaning |
|---|---|
| Safe | No effect on features most people use. |
| Moderate | Turns off something some people use. Read the explanation. |
| Advanced | Can break features or apps. Only for people who know they do not need them. |

### Presets

| Preset | What it selects |
|---|---|
| Recommended | Safe changes that most people want. Windows Update is left as is. |
| Privacy | Recommended, plus more privacy changes that may turn off features some people use. |
| Everything | Almost everything, including Advanced telemetry tasks. It never removes essential apps (Photos, Calculator, Notepad, Paint, Snipping Tool, Xbox) and never turns Windows Update fully off. |
| Custom | Your own selection. Changing any box switches to Custom. |

## Categories

**Telemetry & Privacy** (12). Diagnostic data level, the Connected User Experiences and Telemetry service (DiagTrack), the WAP Push Message Routing Service (dmwappushservice) as its own checkbox, advertising ID, activity history, tailored experiences, feedback requests, inking and typing personalization, online speech recognition, app launch tracking, Windows Error Reporting and telemetry scheduled tasks.

**Bloatware Apps** (38). Preinstalled apps, one checkbox each, removed for all users and for new accounts. The Microsoft Store, App Installer, Windows Security and other system parts are never listed. Calculator, Photos, Notepad, Paint and Snipping Tool are listed as Advanced and are not in any preset.

**Windows Update** (5 levels, 3 options). Pick one level:

- Leave as is
- Pause updates (1 to 5 weeks)
- Security updates only (holds feature updates for a year, no drivers, no previews)
- Block feature upgrades and drivers (stays on your current version, for example 25H2)
- Fully off (stops the update services, with a clear warning)

Options: no automatic restart while signed in, no sharing of update downloads with other PCs, and no early feature rollouts.

**OneDrive** (5). Stop it starting with Windows, hide it in File Explorer, stop "back up your folders" prompts, disable sync by policy, or uninstall it with Microsoft's own uninstaller.

**Copilot & AI** (7). Turn off Windows Copilot, remove the Copilot app, disable Recall and Click to Do, and turn off the AI features in Edge, Paint and Notepad through their official policies.

**Ads & Suggestions** (10). Start menu suggestions, lock screen tips, Settings suggestions, Windows tips and setup prompts, File Explorer ads, Bing in Start search, search highlights, Widgets and news (hidden on Windows 11 24H2 and later, because Windows blocks that policy), automatic installs of suggested apps, and suggestion notifications.

**Services** (16). Optional services such as Fax, Downloaded Maps Manager, Retail Demo, Remote Registry, Geolocation, SysMain and Xbox services, each with a clear risk label. Printing, Windows Search and biometrics are listed as Advanced and are not in any preset.

**Performance** (4). Turn off Windows variable refresh rate (fixes stutter and very low frame rates on the desktop and in windowed apps on some VRR monitors, any graphics card), turn off hardware-accelerated GPU scheduling (needs a restart), turn off NVIDIA G-SYNC in the driver (NVIDIA only, separate checkbox) and turn off background game recording.

**Undo / History**. Lists every run with what changed. Undo puts everything back.

## Safety and undo

- Before each change, the previous state is written to `C:\ProgramData\WinBloatFree\history\run-<date>.json`: the old registry value (or the fact that it did not exist), the old service startup type and whether it was running, the old task state, and the old NVIDIA driver value. The file is saved after every tweak, so it is complete even if the app is closed halfway. If WinBloatFree turns on System Restore so it can create a restore point, that is recorded in the same history. Undo leaves System Restore on so existing restore points stay available.
- Undo restores those values, newest change first, and removes registry keys that WinBloatFree created.
- Removed apps cannot be put back offline. Undo tells you which ones to reinstall from the Microsoft Store.
- Uninstalled OneDrive is put back with the OneDrive installer that ships with Windows, if it is still there.
- Changes that are already in place are skipped and are not recorded, so undo never "restores" a value you set yourself.
- Things that do not exist on your Windows version (a service, a task, an app) are skipped.
- Windows protects a few things, such as the Windows Update Medic service. If Windows refuses a change, the others still apply and the result screen lists what failed.

Most changes are registry policy values under `HKLM\SOFTWARE\Policies` and settings under `HKCU`. A few are only honored by some editions; the detail pane says so where that matters.

Note: settings under `HKCU` apply to the account that runs the app. If you sign in with a standard account and elevate with a different administrator account, those settings go to the administrator account. Run the app from the account you use.

## No network, no telemetry

WinBloatFree contains no networking code. It does not check for updates, does not send usage data or crash reports, and does not download anything. The only time anything opens the internet is when you click "Source code" or "Report an issue", which opens the link in your browser. The test suite fails the build if networking APIs show up in the source.

## Tweak definitions

All tweaks are plain JSON files in [`src/WinBloatFree.Core/Catalog`](src/WinBloatFree.Core/Catalog), built into the exe. Each tweak has an id, name, one-line summary, description, risk, presets and a list of actions:

| Action | What it does |
|---|---|
| `Registry` | Sets a value (DWord, QWord, String, ExpandString). |
| `RegistryDelete` | Deletes a value, for example a startup entry. |
| `RegistryToken` | Sets one `Name=Value;` part inside a string value and keeps the rest. |
| `Service` | Changes the startup type and can stop the service. |
| `Task` | Disables a scheduled task. |
| `Appx` | Removes an app package for all users and for new accounts. |
| `OneDriveUninstall` | Runs Microsoft's OneDrive uninstaller. |
| `NvidiaDrs` | Sets a value in the NVIDIA driver's global profile. |

The detail pane has a "View tweak definition (JSON)" link that shows the exact definition. New tweaks can be added with a pull request that only touches these files. The test suite checks every file (required fields, protected apps, one option per preset in each group, and no dashes in the text).

## Build

You need the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0). It builds on Windows, and on Linux or macOS too (the project sets `EnableWindowsTargeting`).

```
dotnet run --project tests/WinBloatFree.Core.Tests -c Release
dotnet publish src/WinBloatFree -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -o artifacts/publish
```

The result is `artifacts/publish/WinBloatFree.exe`, a single self-contained file of about 70 MB that runs on Windows 10 (version 2004 or later) and Windows 11 without installing .NET. It is large because it carries its own copy of .NET and WPF, compressed.

GitHub Actions builds the same exe on every push and attaches it, with a SHA256 checksum, to every release tagged `v*`. Once SignPath code signing is enabled, the file attached to a tagged release is the signed exe, and the checksum is computed after signing. See [Code signing policy](#code-signing-policy).

### Project layout

| Path | What it is |
|---|---|
| `src/WinBloatFree.Core` | Pure logic: tweak model, catalog, presets, engine, undo history. No Windows calls, so it runs and is tested on any OS. |
| `src/WinBloatFree` | WPF app and the Windows layer: registry, Service Control Manager, Task Scheduler, PackageManager, NVAPI and System Restore. |
| `tests/WinBloatFree.Core.Tests` | Test runner with an in-memory fake Windows. No test framework packages needed. |

### Dependencies

No third-party packages. The app uses the .NET 8 base library and WPF. The only extra piece is Microsoft's Windows SDK projection (`Microsoft.Windows.SDK.NET.Ref`), which the .NET SDK adds automatically for the Windows 10 target and which gives access to the in-box `PackageManager` API for app removal. It adds about 6 MB to the exe.

## Code signing policy

Free code signing provided by [SignPath.io](https://signpath.io/), certificate by [SignPath Foundation](https://signpath.org/).

Windows release builds of `WinBloatFree.exe` are produced from this repository by GitHub Actions on GitHub-hosted runners. Once the project is accepted by SignPath, those builds are submitted for signing. Only artifacts built from this source are signed. Each signing request is approved by an approver listed below. Signed files show **SignPath Foundation** as the publisher, because the certificate belongs to the foundation.

The full policy, including how signing is wired in CI, is in [CODE_SIGNING.md](CODE_SIGNING.md).

### Team roles

- Authors (committers): [Michael (21 FLUX)](https://github.com/21flux-japan). Trusted to modify the source without additional reviews.
- Reviewers: [Michael (21 FLUX)](https://github.com/21flux-japan). Every pull request from someone who is not a committer is reviewed before it is merged.
- Approvers: [Michael (21 FLUX)](https://github.com/21flux-japan). Approves each SignPath signing request before a release is signed.

This is a single-maintainer project, so one person holds all three roles.

### Privacy policy

This program will not transfer any information to other networked systems unless specifically requested by the user or the person installing or operating it.

WinBloatFree makes no network connections and collects nothing. It does not check for updates, and it does not send usage data or crash reports. A browser is opened only when you click "Source code", "Report an issue", or "Get it from the Store".

## License

MIT. Copyright (c) 2026 Michael (21 FLUX). See [LICENSE](LICENSE).

WinBloatFree does not include code from other debloat projects. Public documentation and other tools were used only as references for which Windows settings exist.
