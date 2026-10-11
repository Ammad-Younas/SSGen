# SSGen: guide for Claude Code

Windows desktop app: import an Android APK → run it in the emulator → (optionally AI-)explore it → pick the best screens → render store listing screenshots (Google Play / App Store) → edit → export.

## Decisions (from the owner; they override the original brief)

- **Product and code are named "SSGen"** (renamed from "StoreShot Studio" after M2): exe `SSGen.exe`, solution `SSGen.slnx`, projects and namespaces `SSGen.*`, data folder `%LOCALAPPDATA%\SSGen` (migrated automatically from `StoreShotStudio`), and the default projects folder `Documents\SSGen\Projects`. Legacy values are still read so existing data keeps working: the AVD marker `storeshot.managed` (new devices get `ssgen.managed`), the DPAPI entropy `StoreShotStudio.Secrets.v1`, and the AVD name `StoreShot_Pixel_API36`. New AVDs are named `SSGen_*`.
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
SSGen.slnx
Directory.Build.props        # net10, nullable, warnings-as-errors, analyzers (latest-recommended)
Directory.Packages.props     # central package versions: add versions here, not in csproj
src/SSGen.Core/          # no UI: data, settings, secrets, projects, process runner, setup checks
  AppPaths.cs                # %LOCALAPPDATA%\SSGen\{app.db,logs,cache}
  Data/                      # SqliteDatabase, MigrationRunner, Migrations/{App,Project}/NNNN_name.sql (embedded)
  Settings/                  # AppSettings record (JSON in settings table), SettingsService
  Security/SecretStore.cs    # ISecretStore + DpapiSecretStore
  Projects/ProjectService.cs # project = folder with project.db + screens/ renders/ export/
  Processes/ProcessRunner.cs # IProcessRunner: timeouts, cancellation, kill-tree, line callbacks, stdin
  Setup/                     # AndroidSdk(+Locator), JavaLocator, SdkPackages (list parser + installer), ISetupCheck impls, SetupService
src/SSGen.App/           # WPF shell (assembly name SSGen -> SSGen.exe)
  App.xaml(.cs)              # composition root, Serilog, global exception handlers
  Services/                  # ThemeService, DialogService (all modal UI), NavigationService (PageKey)
  ViewModels/ Views/         # page VMs are singletons; PageTemplates.xaml maps VM type -> View
  Resources/Styles.xaml      # shared styles; colours come from Fluent DynamicResource brushes
  Controls/                  # ScreenMapView (Explore diagram), ColorBox (swatch palette + hex)
src/SSGen.Emulator/      # devices: no UI
  DeviceHub.cs               # entry point: resolves SDK from settings, providers, APK inspector, mirror selection
  Adb/                       # AdbClient (adb.exe wrapper), LongRunningProcess, output parsers, install-error mapping
  Apk/                       # ApkInspector (aapt2 + managed AXML fallback, icon extraction), ProjectApkStore
  Avd/AvdManager.cs          # reads/writes AVD .ini files directly; only deletes/wipes AVDs marked ssgen.managed=yes (or legacy storeshot.managed=yes)
  Devices/                   # IDevice (what the agent uses), AdbDevice, MockDevice; IEmulatorProvider: LocalAvd/ConnectedPhone/Mock
  Interop/JobObject.cs       # kill-on-close job: emulators die with SSGen even on crash
  Mirror/                    # IScreenMirror: EmulatorGrpcMirror (emulators), ScrcpyMirror+H264Decoder (phones), ScreencapMirror (fallback)
  Logcat/                    # threadtime parser + batched streaming session
  Protos/                    # emulator_controller.proto (Apache-2.0) -> generated gRPC client
src/SSGen.Ai/            # provider-agnostic LLM layer: no UI
  LlmContracts.cs            # ILlmProvider (text, images, JSON schema, streaming), LlmException(kind, fix), usage
  Providers/                 # Anthropic (official SDK), OpenAI + OpenAI-compatible, Gemini, Ollama (raw HTTP via HttpProviderBase)
  StructuredOutput.cs        # AskAsync<T>: schema prompt + example, tolerant JSON extraction, validate-and-retry with feedback
  Profiles/                  # ProviderProfile + store (app.db; keys in DPAPI as "provider:{id}"), per-task models, price table
  AiService.cs               # create/resolve providers per AiTask, vision detection (cached), connection test, CostMeter
src/SSGen.Agent/         # exploration agent: no UI
  Observation/               # uiautomator XML -> UiSnapshot (numbered elements); ScreenFingerprint (dHash + structure + selection)
  Graph/ScreenGraph.cs       # unique screens + edges (with element keys, so routes can be replayed); FirstHopToUnfinished
  Safety/Guardrails.cs       # deterministic blocklist (multilingual, ids, confirm-dialog context, card numbers, AI risk flag)
  Strategies/                # IAgentStrategy: SystematicStrategy (no AI) and AiStrategy (falls back to systematic per step)
  Runs/                      # ExplorationAgent loop (observe/decide/guard/act/settle), AgentRunStore (project.db 0003)
src/SSGen.Scene/         # the one scene-document format (JSON) + SkiaSharp renderer: no UI
  SceneDocument.cs           # layers: shape, image, device (procedural frames), text; fills incl. panorama gradients
  SceneRenderer.cs           # opaque 24-bit output (stores forbid alpha), PNG/JPEG, shadows, rotation
  TextLayout.cs, Fonts.cs    # shrink-to-fit wrapping; bundled OFL fonts (third_party/fonts) then Windows fonts
  DeviceGeometry.cs          # original frame proportions (punch-hole phone, island phone, tablet)
  Palette.cs, Colors.cs      # brand palette from the app icon; WCAG contrast for text colour
  Editing/                   # SceneEditor (all edits + gestures), commands + EditHistory (undo/redo, merge, dirty), SceneGeometry (hit test, resize, rotate), StyleTransfer
src/SSGen.Templates/     # templates and store specs: no UI
  Definitions/*.json         # 12 screenshot templates + feature graphic (fractions of the canvas, colour tokens, roles)
  Presets/store-presets.json # VERIFIED store specs with source URLs and date; %LOCALAPPDATA%\SSGen\store-presets.json overrides
  TemplateEngine.cs          # template + inputs -> pixel-exact SceneDocument for a preset
  RenderService.cs           # renders the set per preset into renders/<Store>/<Size>/NN_name.png (+ scene JSON), validates
  RenderStore.cs             # renders table: list, load/save edited scenes (re-renders the image), validate files on disk
src/SSGen.Selection/     # choosing screens: no UI
  ScreenCandidate.cs         # image metrics (richness, colourfulness, blank, keyboard, dHash)
  Ranking.cs                 # HeuristicRanker (no AI), StoryPicker (hero first, settings last)
  AiRanker.cs                # batched vision ranking, cached (vision_cache), caption polish
  SelectionStore.cs          # selection table (included, order, captions)
tests/SSGen.Core.Tests/      # xUnit
tests/SSGen.Emulator.Tests/  # xUnit; EmulatorIntegrationTests is opt-in (see Commands)
tests/SSGen.Agent.Tests/     # providers via fake HttpMessageHandler, structured output, guardrails, full runs on MockDevice; real-emulator run opt-in
tests/SSGen.Design.Tests/    # every template x every preset renders and validates; ranking, AI ranking, selection, render pipeline; editor core
tools/fetch-deps.ps1         # downloads scrcpy-server, FFmpeg LGPL DLLs, OFL fonts (Poppins, DM Serif Display), sample APK into third_party/ (git-ignored)
tools/make-icon.ps1          # regenerates src/SSGen.App/Assets/ssgen.ico (each size drawn natively); every Window sets Icon to it
```

## Gallery and editor (M6): rules and lessons

- **Every edit is a command** (`SSGen.Scene.Editing`). Layer changes store the layer as JSON before/after, so the live object can change freely. Field edits carry a merge key (`{layer}:{field}`): typing or dragging a slider within 1.5 s is one undo step. Canvas drags are gestures: previewed live, recorded once on mouse up. A click that only selects must change nothing (a drag that rounded fractional coordinates once recorded an invisible "Move" and made the image look unsaved).
- **Canvas**: the stage is a Grid in document pixels scaled by a `LayoutTransform`, so `GetPosition(Stage)` is already in document coordinates. SkiaSharp renders the document (longest side 1600 px) on a background loop where the newest state wins, into a `WriteableBitmap`. Handles are WPF shapes sized `1/scale` so they stay the same on screen.
- **Saving** writes the scene JSON and renders the image again through the export renderer (temp file + move, so readers never see half a file), and sets `renders.edited_utc` (project.db 0006). The Design page asks before re-rendering sizes with edited images. Scene image sources inside the project are stored relative to it.
- **Apply style to all** (same size only) matches layers by id (template roles): copies look and position, keeps each image's text and screenshot, copies added layers, removes deleted ones, and keeps panorama offsets.
- The Gallery validates files on disk (`ImageFileInfo` reads size, format and alpha). The listing preview is a generic approximation, not a copy of either store's page.
- UI Automation notes: `Grid`/`Canvas` aren't in the UIA tree (name a `ScrollViewer` instead); `RadioButton` needs `SelectionItemPattern`, not Invoke; MessageBox buttons show as panes, so click them by position.

## Selection, templates, render (M5): rules and lessons

- **Store specs are never typed from memory.** `store-presets.json` holds what the official pages said on its `verified` date, with the URLs. Re-check them before changing anything. Store-wide rules (e.g. Google Play's "long side at most 2x short side") apply to screenshots only; feature graphic (1024x500) and icon (512x512, alpha allowed, 1 MB) have their own.
- **Scene documents are the contract** between templates, renderer and the M6 editor. Coordinates are canvas pixels; templates use fractions so one template fits every size. Renders are opaque RGB (PNG colour type 2); a test checks every template x preset.
- **Device frames are drawn in code** (no third-party artwork). App Store sets use the "island" phone; the App Store warning (Android UI in an iPhone-style frame) shows whenever an App Store size is selected.
- **Ranking without AI** uses image metrics and the screen's own class name + app bar title. Never match keywords against the package name ("privacyfriendlynotes" made every screen "settings-like"). Duplicates = same activity and near-identical pixels; similar layouts are not duplicates.
- **AI ranking** sends at most 6 screenshots per call, downscaled to 640 px JPEG, and caches results by image hash + model + prompt version. Screens already rejected as blank/keyboard aren't sent. A text-only polish pass makes the chosen set's headlines distinct. Consent, cap and price warnings go through `AiRunGate` (shared with Explore).
- Agent captures now store the app bar title and visible text per screen (screens.title/ui_text), so headlines work without AI.
- Status bar demo mode (API 36 verified): `sysui_demo_allowed 1`, then `com.android.systemui.demo` broadcasts (clock 0941, battery 100, network `-e fully true` to drop the "!" marks, notifications hidden); `exit` restores it. The agent turns it on for a run and off afterwards.

## AI and agent (M3): rules and lessons

- **No default provider, ever.** No profile exists until the user adds one; with none, exploration uses `SystematicStrategy` and everything works manually. Never hard-code prices: `model_prices` is user-edited, and an unknown price shows "cost unknown" (and the cap can't be enforced; the user is warned).
- **Safety is deterministic and model-independent.** Every action passes `Guardrails.Check` before it runs; the model's `risk` flag can only add blocks. Safe mode on = blocked, off = `IAgentObserver.ConfirmAsync`. Tests prove MockDevice's "Delete account" and "Buy premium" are never tapped (systematic and a reckless scripted AI).
- **Consent** is asked before the first screenshot goes to a paid (non-loopback) provider; declining switches to UI-text-only for that run. Local providers (loopback URL or Ollama) never ask and show "Free (local)".
- **Weak models**: `StructuredOutput` sends the schema + one example, extracts JSON from prose/fences/trailing commas, validates (e.g. element ids must exist) and retries with the error. If still unusable, `AiStrategy` falls back to the systematic choice for that step instead of failing the run.
- **Back on the launch screen closes the app.** The agent marks the screen after a (re)launch, and any screen whose Back left the app, as `IsRoot`; the systematic strategy stops there instead of pressing Back, and the AI prompt says so. Before this, a real run wasted 30 of 45 steps in a back/relaunch loop.
- **Revisiting**: edges store the tapped element's key, so the systematic strategy can travel to a screen that still has untried elements (`FirstHopToUnfinished`, capped at 3 re-taps per element).
- **Speed**: `SettleAsync` returns the last stable screenshot, which becomes the observation (about 3.5 s per step on the emulator; `uiautomator dump` is most of it).
- Real sample-app run (systematic, M3): 6 unique screens in 25 steps, 1 min 41 s, completed.

## Agent v2 (M4): interruptions, sign-in, recovery, resume

- **Order in each step** (`ExplorationAgent`): observe (settle, UI dump, close the keyboard if a text field has it open, verify the screenshot still matches the dump) → `ScreenClassifier` interruptions (permission dialog → allow while using; "not responding" → wait; crash dialog → close + count; ad/promo popup → close/"Not now"; spinner-only → wait) → outside-app handling (Back exited → root; pid gone → crash) → graph → sign-in handling → stuck/circle checks → strategy → guardrails → act.
- **Sign-in walls**: test account typed by the agent itself (`LoginOptions`; password in DPAPI as `project-login:{project id}`); never in an AI prompt (`AgentContext.Secrets` masked) or any log. Then a guest route (user hint, then guest/skip/later wording). A sign-in panel deeper in the app is a dead end (Back). On the start screen with no way in: hand over to the user once (`AgentControl.TakeOver` + `IAgentObserver.OnTakeoverRequested`), else finish with advice. A sign-in screen already got past counts as explored.
- **Progress** = a new screen or a first try of an element. The no-progress limit (25) and the going-in-circles check use it; using only "new screen" ended runs early on element-rich screens (formatting toolbars). Same for "stuck": in-place actions that try something new aren't stuck.
- **Systematic order on a screen**: onboarding skip, then elements specific to this screen before ones many screens share (so a popup's own items come before the menu button of the screen behind it), then reading order; then fill text fields once (`DemoText`; keyed by position because the text changes); then leaving buttons (Navigate up, Back, Close, Save). Sample text must not contain guardrail words (a saved "Buy milk" note became untappable); a test enforces it.
- **Screenshot integrity**: after an action, wait for the screen to change (bounded 1.5 s) before settling, and re-observe if the screen changed during the UI dump. Without this a slow transition saved the tutorial image under the main screen's activity.
- **Resume**: every node's full state (structure, selection, known/tried, revisits, flags) is saved each step (project.db 0004); a new run with `ResumeRunId` loads the graph and continues. Runs left "running" (SSGen crashed) become "interrupted" at the next start.
- **Popups are screens of their own**: near-identical pixels only match when the element sets are similar too (Jaccard ≥ 0.6).
- Keyboard on API 36: `dumpsys input_method` → `mInputShown=true`; the Escape key hides it; the keyboard never appears in the UI dump.
- Known: about 8 s per step on the emulator (two screencaps + UI dump + activity + verification); a gRPC screenshot fast path is an M8 item.

## Device pipeline (M2): what is verified and why it's built this way

- **Live preview by device type** (`DeviceHub.StartMirrorAsync`):
  - **Emulators** use the emulator's gRPC `streamScreenshot`. SSGen launches emulators with `-grpc <port> -grpc-use-token`; the port and token are read from `%LOCALAPPDATA%\Temp\avd\running\pid_*.ini`. Measured **33 fps** during scrolling.
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
dotnet build SSGen.slnx
dotnet test SSGen.slnx
dotnet run --project src/SSGen.App
powershell -File tools/fetch-deps.ps1            # once after cloning: scrcpy-server, FFmpeg DLLs, sample APK
$env:SSGEN_INTEGRATION='1'; dotnet test tests/SSGen.Emulator.Tests   # + real emulator end-to-end (~1-2 min)
$env:SSGEN_INTEGRATION='1'; dotnet test tests/SSGen.Agent.Tests      # + real systematic exploration of the sample app (~2 min)
```

Diagnostics env vars: `SSGEN_DATA_DIR=<folder>` runs the app on a separate data folder (clean first run, UI tests) instead of `%LOCALAPPDATA%\SSGen`; `SSGEN_MOCK_DEVICE=1` shows a mock device in the app; `SSGEN_SCRCPY_LOG=verbose` logs scrcpy server output (`ScrcpyMirror.ServerOutput`).

To reset first-run state, delete `%LOCALAPPDATA%\SSGen` (it holds app.db, logs and cache).

## Conventions

- File-scoped namespaces; `_camelCase` private fields; one public concept per file (small related records may share a file).
- The build treats warnings as errors. Fix analyzer warnings rather than suppressing them. Project-wide suppressions live in `.editorconfig` (`SSGen overrides`) and need a reason.
- **Errors the user sees** are thrown as `UserFacingException(message, suggestedFix)`. `DialogService.Describe` formats them. Never fail silently: the setup assistant converts any exception into a Fail result.
- **Never block the UI thread.** Process and file-heavy work runs via `Task.Run` or async; VMs report progress through `IProgress<T>`.
- All external tools run through `IProcessRunner` (mockable). Pass args via `ArgumentList` and never build command strings.
- New settings are added as `AppSettings` properties with safe defaults. JSON tolerates missing or unknown fields.
- Schema changes go in a new numbered SQL file (`0002_….sql`). Never edit an applied migration.
- Package ids use the slash form (`system-images/android-36/google_apis_playstore/x86_64`). The legacy `sdkmanager` uses `;`, and `SdkPackageManager` converts.
- Android SDK: prefer the new native **Android CLI** (`cmdline-tools/latest/bin/android.exe`, `android sdk list|install`, no Java needed). `sdkmanager.bat` is deprecated and is only a fallback.
- Theme colours: use Fluent brush keys via `DynamicResource` (e.g. `TextFillColorPrimaryBrush`, `CardBackgroundFillColorDefaultBrush`, `SystemFillColorCriticalBrush`).
- Accessibility: buttons whose content is not plain text need `AutomationProperties.Name`. UI Automation drives the app in manual tests.
- Don't use the Fluent `TabControl` for panels that need to scroll. It hosts content without a height constraint (lists grow unbounded and never scroll). Use the `TabStripButton` RadioButton strip with a `*` Grid row instead.
- Selected nav items, tab underlines and splitter highlights use `{DynamicResource NavSelectedBrush}`. `ThemeService` sets it per effective theme: light uses `AccentColorDark1` (the same red as accent buttons); dark uses `AccentColorDark2`. `DynamicResource` on a brush's `Color` inside a resource dictionary throws at runtime, so set brushes from code.
- Resizable panes use star columns plus `PaneSplitter`. Normalise **all** star columns to their pixel widths on `DragStarted`, because `GridSplitter` only rescales the two adjacent columns and the third collapses to its MinWidth. Persist proportions, never pixels.
- WPF has no horizontal-wheel event. `HorizontalScrollSupport` (attached to MainWindow) handles `WM_MOUSEHWHEEL` (two-finger sideways swipe, tilt wheel) and Shift+wheel for any ScrollViewer under the pointer.
- A `ScrollViewer` around wrapping content needs `HorizontalScrollBarVisibility="Disabled"`. Otherwise text never wraps and gets clipped.
- Store specs (dimensions, limits) are never invented. They go in config with the source URL and the verified date (M5).
- Button labels never end with "…" or "..." (owner's request), even when they open a dialog ("Browse", "Import APK", "Edit"). Status text may use "…".
- No em or en dashes in any text (UI strings, prompts, docs, comments): the owner asked for none. Use a colon, comma, parentheses or "to" instead.
- Scripted edits through the shell tool turn a backslash-n escape inside strings into a real newline (twice broke a C# string). Use the Edit tool for text that contains escapes. Some files are CRLF: check before matching multi-line text.
- When writing files from PowerShell, use UTF-8 explicitly. `Get-Content -Raw` without an encoding has corrupted `…` before.
- Every new third-party dependency or bundled asset gets a line in `THIRD_PARTY_NOTICES.md`. Permissive licences only (LGPL only if dynamically linked).

## Milestones

| | Status |
|---|---|
| M1 Foundation (shell, themes, SQLite, settings, DPAPI secrets, setup assistant) | **done** |
| M2 Emulator (AVD mgmt, APK parse/install, live preview, manual control, logcat) | **done** (reviewed) |
| M3 Provider layer + Agent v1 (providers, settings UI, first-run AI choice, guardrails, systematic + AI explorer, Explore page) | **done** |
| M4 Agent v2 (interruptions, sign-in + takeover, crash/circle/no-progress recovery, resume, form filling, popups, screen map) | **done** |
| M5 Selection, templates, render, verified export presets (Design page) | **done** (reviewed) |
| M6 Gallery + editor (layers, text, shapes, pictures, filters, undo/redo, apply style to all) | **done** |
| M7 Export, i18n, installer · M8 Hardening | |

## Dev machine facts (owner's PC)

- Android SDK at `D:\App_Development_Setup\Android_SDK` (ANDROID_HOME). Emulator 36.6, WHPX acceleration.
- Installed images: API 36, 36.1 and 37.0 (ps16k), Google Play x86_64.
- JDK 21 at `D:\App_Development_Setup\Java_JDK`. .NET SDK 10.0.401.
- `StoreShot_Pixel_API36` exists (created by SSGen from the API 36 Play image, marked managed). Its first cold boot is ~50 s; quick boot ~6 s.
- Exploration lesson: `android emulator create` with no arguments *creates* an AVD. Only call these CLIs with `-h` when exploring.
