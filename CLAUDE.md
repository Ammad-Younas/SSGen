# StoreShot Studio — guide for Claude Code

Windows desktop app: import an Android APK → run it in the emulator → (optionally AI-)explore it → pick the best screens → render store listing screenshots (Google Play / App Store) → edit → export.

## Decisions (from the owner; they override the original brief)

- **Windows 10/11 only.** C# / .NET 10 (LTS) / **WPF** with the built-in Fluent theme (`Application.ThemeMode`). No Electron, macOS, Linux, Docker mode or CI.
- **MVVM** with CommunityToolkit.Mvvm (partial-property `[ObservableProperty]`, `[RelayCommand]`), DI via Microsoft.Extensions.DependencyInjection.
- **SkiaSharp** for both the editor canvas and the export renderer, driven by **one JSON scene-document format** shared by templates, editor and export. Undo/redo uses the command pattern.
- **SQLite** via Microsoft.Data.Sqlite with versioned migrations. **Secrets** are encrypted with DPAPI (CurrentUser). Never plaintext, never in the repo.
- **No default AI provider.** A provider-agnostic `ILlmProvider` covers Anthropic, OpenAI, Gemini, any OpenAI-compatible endpoint, and Ollama. Manual mode (no AI) must always work. Cost caps apply only to paid cloud providers and come from a user-editable price table. There is no preset cap; prices are never hard-coded.
- Emulator access goes through the `EmulatorProvider` abstraction (LocalAVD, ConnectedPhone, Mock). The dedicated AVD is `StoreShot_Pixel_API36`. **Never touch the owner's existing AVD `MADI`.**
- Installer: Windows only and unsigned (Velopack planned). Local git only, no remote.
- Agent safety guardrails (no purchases, deletions, sends or posts) are mandatory. Safe mode is on by default.
- **Ask the owner before any further major deviation.** Stop for review after **M2** and **M5**.

## Layout

```
StoreShot.slnx
Directory.Build.props        # net10, nullable, warnings-as-errors, analyzers (latest-recommended)
Directory.Packages.props     # central package versions — add versions here, not in csproj
src/StoreShot.Core/          # no UI: data, settings, secrets, projects, process runner, setup checks
  AppPaths.cs                # %LOCALAPPDATA%\StoreShotStudio\{app.db,logs,cache}
  Data/                      # SqliteDatabase, MigrationRunner, Migrations/{App,Project}/NNNN_name.sql (embedded)
  Settings/                  # AppSettings record (JSON in settings table), SettingsService
  Security/SecretStore.cs    # ISecretStore + DpapiSecretStore
  Projects/ProjectService.cs # project = folder with project.db + screens/ renders/ export/
  Processes/ProcessRunner.cs # IProcessRunner: timeouts, cancellation, kill-tree, line callbacks, stdin
  Setup/                     # AndroidSdk(+Locator), JavaLocator, SdkPackages (list parser + installer), ISetupCheck impls, SetupService
src/StoreShot.App/           # WPF shell (assembly name StoreShotStudio)
  App.xaml(.cs)              # composition root, Serilog, global exception handlers
  Services/                  # ThemeService, DialogService (all modal UI), NavigationService (PageKey)
  ViewModels/ Views/         # page VMs are singletons; PageTemplates.xaml maps VM type -> View
  Resources/Styles.xaml      # shared styles; colours come from Fluent DynamicResource brushes
tests/StoreShot.Core.Tests/  # xUnit
```

Planned projects, each added in the milestone that needs it: `StoreShot.Emulator` (M2), `StoreShot.Ai` and `StoreShot.Agent` (M3), `StoreShot.Scene` (SkiaSharp scene doc + renderer) and `StoreShot.Templates` (M5).

## Commands

```powershell
dotnet build StoreShot.slnx
dotnet test StoreShot.slnx
dotnet run --project src/StoreShot.App
```

To reset first-run state, delete `%LOCALAPPDATA%\StoreShotStudio` (it holds app.db, logs and cache).

## Conventions

- File-scoped namespaces; `_camelCase` private fields; one public concept per file (small related records may share a file).
- The build treats warnings as errors. Fix analyzer warnings rather than suppressing them. Project-wide suppressions live in `.editorconfig` (`StoreShot Studio overrides`) and need a reason.
- **Errors the user sees** are thrown as `UserFacingException(message, suggestedFix)`. `DialogService.Describe` formats them. Never fail silently: the setup assistant converts any exception into a Fail result.
- **Never block the UI thread.** Process and file-heavy work runs via `Task.Run` or async; VMs report progress through `IProgress<T>`.
- All external tools run through `IProcessRunner` (mockable). Pass args via `ArgumentList` and never build command strings.
- New settings are added as `AppSettings` properties with safe defaults. JSON tolerates missing or unknown fields.
- Schema changes go in a new numbered SQL file (`0002_….sql`). Never edit an applied migration.
- Package ids use the slash form (`system-images/android-36/google_apis_playstore/x86_64`). The legacy `sdkmanager` uses `;`, and `SdkPackageManager` converts.
- Android SDK: prefer the new native **Android CLI** (`cmdline-tools/latest/bin/android.exe`, `android sdk list|install`, no Java needed). `sdkmanager.bat` is deprecated and is only a fallback.
- Theme colours: use Fluent brush keys via `DynamicResource` (e.g. `TextFillColorPrimaryBrush`, `CardBackgroundFillColorDefaultBrush`, `SystemFillColorCriticalBrush`).
- Accessibility: buttons whose content is not plain text need `AutomationProperties.Name`. UI Automation drives the app in manual tests.
- Store specs (dimensions, limits) are never invented. They go in config with the source URL and the verified date (M5).
- When writing files from PowerShell, use UTF-8 explicitly. `Get-Content -Raw` without an encoding has corrupted `…` before.
- Every new third-party dependency or bundled asset gets a line in `THIRD_PARTY_NOTICES.md`. Permissive licences only (LGPL only if dynamically linked).

## Milestones

| | Status |
|---|---|
| M1 Foundation (shell, themes, SQLite, settings, DPAPI secrets, setup assistant) | **done** |
| M2 Emulator (AVD mgmt, APK parse/install, live preview, manual control, logcat) | next, then **stop for review** |
| M3 Provider layer + Agent v1 | |
| M4 Agent v2 | |
| M5 Selection, templates, render, verified export presets | then **stop for review** |
| M6 Gallery + editor · M7 Export, i18n, installer · M8 Hardening | |

## Dev machine facts (owner's PC)

- Android SDK at `D:\App_Development_Setup\Android_SDK` (ANDROID_HOME). Emulator 36.6, WHPX acceleration.
- Installed images: API 36, 36.1 and 37.0 (ps16k), Google Play x86_64.
- JDK 21 at `D:\App_Development_Setup\Java_JDK`. .NET SDK 10.0.401.
