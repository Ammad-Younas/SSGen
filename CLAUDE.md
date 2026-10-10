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
src/StoreShot.Emulator/      # devices: no UI
  DeviceHub.cs               # entry point: resolves SDK from settings, providers, APK inspector, mirror selection
  Adb/                       # AdbClient (adb.exe wrapper), LongRunningProcess, output parsers, install-error mapping
  Apk/                       # ApkInspector (aapt2 + managed AXML fallback, icon extraction), ProjectApkStore
  Avd/AvdManager.cs          # reads/writes AVD .ini files directly; only deletes/wipes AVDs marked storeshot.managed=yes
  Devices/                   # IDevice (what the agent uses), AdbDevice, MockDevice; IEmulatorProvider: LocalAvd/ConnectedPhone/Mock
  Interop/JobObject.cs       # kill-on-close job: emulators die with StoreShot even on crash
  Mirror/                    # IScreenMirror: EmulatorGrpcMirror (emulators), ScrcpyMirror+H264Decoder (phones), ScreencapMirror (fallback)
  Logcat/                    # threadtime parser + batched streaming session
  Protos/                    # emulator_controller.proto (Apache-2.0) -> generated gRPC client
tests/StoreShot.Core.Tests/      # xUnit
tests/StoreShot.Emulator.Tests/  # xUnit; EmulatorIntegrationTests is opt-in (see Commands)
tools/fetch-deps.ps1         # downloads scrcpy-server, FFmpeg LGPL DLLs, sample APK into third_party/ (git-ignored)
```

Planned projects, each added in the milestone that needs it: `StoreShot.Ai` and `StoreShot.Agent` (M3), `StoreShot.Scene` (SkiaSharp scene doc + renderer) and `StoreShot.Templates` (M5).

## Device pipeline (M2): what is verified and why it's built this way

- **Live preview by device type** (`DeviceHub.StartMirrorAsync`):
  - **Emulators** use the emulator's gRPC `streamScreenshot`. StoreShot launches emulators with `-grpc <port> -grpc-use-token`; the port and token are read from `%LOCALAPPDATA%\Temp\avd\running\pid_*.ini`. Measured **33 fps** during scrolling.
  - **Why not scrcpy on emulators:** emulators only have *software* H.264 encoders, which manage about 5 fps. That was measured, with host-GPU rendering making no difference.
  - **Phones** use scrcpy. Its protocol is pinned to v5.0.1, which must match `ScrcpyProtocol.ServerVersion` and `fetch-deps.ps1`.
  - **Fallback** for everything is screencap polling.
- **Boot pitfalls handled in `LocalAvdProvider.WaitForBootAsync`:**
  1. *unauthorized*: a stale adb server holds another key. Fix: restart the adb server once.
  2. *offline for more than 60 s* after a quick boot: a corrupt snapshot. Fix: kill without saving, then cold boot (`-no-snapshot-load`).
- **Stopping**: always use `adb emu kill` and allow up to 60 s. Killing an emulator mid snapshot-save corrupts the snapshot.
- **Touches** are in *frame* pixels at the mirror level. The gRPC mirror scales to device pixels. The scrcpy server ignores touches whose stated size doesn't match the current video size.

## Commands

```powershell
dotnet build StoreShot.slnx
dotnet test StoreShot.slnx
dotnet run --project src/StoreShot.App
powershell -File tools/fetch-deps.ps1            # once after cloning: scrcpy-server, FFmpeg DLLs, sample APK
$env:STORESHOT_INTEGRATION='1'; dotnet test tests/StoreShot.Emulator.Tests   # + real emulator end-to-end (~1-2 min)
```

Diagnostics env vars: `STORESHOT_MOCK_DEVICE=1` shows a mock device in the app; `STORESHOT_SCRCPY_LOG=verbose` logs scrcpy server output (`ScrcpyMirror.ServerOutput`).

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
| M2 Emulator (AVD mgmt, APK parse/install, live preview, manual control, logcat) | **done**, awaiting owner review |
| M3 Provider layer + Agent v1 | next (after review) |
| M4 Agent v2 | |
| M5 Selection, templates, render, verified export presets | then **stop for review** |
| M6 Gallery + editor · M7 Export, i18n, installer · M8 Hardening | |

## Dev machine facts (owner's PC)

- Android SDK at `D:\App_Development_Setup\Android_SDK` (ANDROID_HOME). Emulator 36.6, WHPX acceleration.
- Installed images: API 36, 36.1 and 37.0 (ps16k), Google Play x86_64.
- JDK 21 at `D:\App_Development_Setup\Java_JDK`. .NET SDK 10.0.401.
- `StoreShot_Pixel_API36` exists (created by StoreShot from the API 36 Play image, marked managed). Its first cold boot is ~50 s; quick boot ~6 s.
- Exploration lesson: `android emulator create` with no arguments *creates* an AVD. Only call these CLIs with `-h` when exploring.
