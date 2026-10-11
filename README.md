# SSGen

Turn an Android APK into upload-ready store listing screenshots for Google Play and the App Store. The app runs your APK in the Android Emulator, captures its screens (by hand or with an AI agent of your choice), renders them into designed listing images, and exports them at the correct store sizes.

> **Status: milestone M8 (all planned milestones done).** Projects, settings, the environment setup assistant, the full device pipeline (virtual devices, APK import and install, a live preview you can control, screen capture, logcat) and app exploration (with your own AI provider or the built-in systematic explorer) work, and so do designing, rendering, reviewing, editing, translating and exporting store-ready images, plus a Windows installer.

## Requirements

- Windows 10 or 11 (x64)
- [.NET 10 SDK](https://dotnet.microsoft.com/download) to build from source
- Android SDK with platform-tools, the emulator, build-tools and an x86_64 Google Play system image (API 30+). Installing [Android Studio](https://developer.android.com/studio) is the easiest way to get these.
- Hardware virtualization enabled, with the **Windows Hypervisor Platform** feature turned on
- Java 17+ is optional. Only the legacy SDK tools need it.

## Build and run

```powershell
powershell -File tools/fetch-deps.ps1   # once: downloads scrcpy-server, FFmpeg (LGPL DLLs) and a sample APK
dotnet build SSGen.slnx
dotnet test SSGen.slnx
dotnet run --project src/SSGen.App
```

## First run

The **Environment setup** assistant checks:

- the .NET runtime
- the Android SDK location
- adb
- the emulator
- build-tools (aapt2)
- the SDK command-line tools
- a system image
- hardware acceleration
- Java
- free disk space

Each check explains why SSGen needs it and how to fix it. When the SDK command-line tools are installed, missing SDK packages can be installed in one click. You're asked to accept Google's SDK licence first.

The SDK is found automatically through `ANDROID_HOME`, `ANDROID_SDK_ROOT` or `%LOCALAPPDATA%\Android\Sdk`. If yours is elsewhere, set the folder in **Settings**.

## Running your app

1. Create a project on **Home**.
2. Open **Emulator**. Create a virtual device if you have none (*Create a virtual device*: pick a screen size and an installed Android version), then press **Start**. The first boot takes about a minute; later boots take seconds.
3. **Import APK…** to read the app's name, icon, version and permissions. A copy is kept in the project.
4. **Install & open** installs the app with all runtime permissions granted and launches it.
5. Use the preview like a phone: click to tap, drag to swipe, scroll to scroll, type to send keys. Back, Home and Recents are below the preview, and **Capture** saves the current screen into the project at full resolution.
6. **Logcat** shows the app's log, filterable by level and text.

USB phones with USB debugging enabled appear in the same device list.

SSGen only resets or deletes virtual devices it created itself. Closing SSGen shuts down the emulators it started.

## Where things are stored

| What | Where |
|---|---|
| Settings, recent projects, encrypted API keys | `%LOCALAPPDATA%\SSGen\app.db` |
| Logs (14 days) | `%LOCALAPPDATA%\SSGen\logs` |
| Projects (default) | `Documents\SSGen\Projects\<name>` |

API keys are encrypted with Windows DPAPI and can only be read by your Windows account. SSGen sends no telemetry.

## Exploring your app

**Explore** walks through the app on the running device and saves every distinct screen to the project. Start a device and import the APK first.

- **With AI**, the model chooses each step from the screen's elements, and from a downscaled screenshot if the model supports images. It also names the screens.
- **Without AI**, the systematic explorer tries every button, tab and menu item once, scrolls each list once, and returns to screens that still have untried items. It stops when nothing is left.
- **Interruptions are handled automatically**: permission requests are allowed (while using the app), "isn't responding" dialogs are waited out, crashes are survived (the app is reopened; after 3 the run stops), ads and rating popups are closed, loading screens are waited for, and the keyboard is closed before screenshots.
- **Sign-in screens**: enter a test account in Explore (the password is encrypted on this PC; the agent types it itself and never sends it to an AI), or describe a guest option. Otherwise SSGen pauses and asks you to log in on the Emulator page, then continues.
- **Take over** pauses the agent so you can use the device yourself (for anything it can't do), then **Resume**.
- **Continue last run** picks up a run that was stopped, hit a limit, or was interrupted, without repeating what was already tried.
- Text fields get realistic sample text (a note title, a search term), so screens show content instead of empty states.
- **Limits**: maximum steps and minutes, the cost cap for paid providers, and an automatic stop when nothing new turns up for 25 steps. You can pause, resume or stop a run at any time.
- The **screen map** shows how the discovered screens connect, as a diagram or a list.
- **Safe mode** (on by default) never performs purchase, payment, subscribe, delete, send, post, share, call or sign-out actions. It covers several languages and also blocks the "OK" on a dialog about one of those. You can add your own words in **Settings**. With safe mode off, SSGen asks you before each such action.

## Designing your store images

**Design** turns the captured screens into store-ready images:

1. **Rank** the screens. Without AI, SSGen measures each screenshot and skips blank, loading, sign-in, keyboard-covered and duplicate screens; settings and onboarding go last. With AI, a vision model scores each screen for core functionality, visual appeal, information richness, uniqueness and install appeal, flags screens to avoid (including ones showing personal data), and writes a short headline and subheadline. AI results are cached, so ranking again costs nothing.
2. **Adjust** the set: tick screens in or out, reorder them, and edit any headline. The first screen is the hero.
3. **Pick a template** (12 designs: headline above or below, full screen with caption, dark with glow, tilted device, two screens, large device, diagonal, minimal, bold dark, panorama across the whole set, phone and tablet) and the **brand colours** (taken from the app icon; editable).
4. Choose the **store sizes**: Google Play phone, 7" and 10" tablet, feature graphic (1024 × 500) and app icon (512 × 512); App Store iPhone 6.9", 6.5", 6.1" and iPad 13". Sizes and rules come from the official Google Play and App Store pages (sources and the date they were checked are in the settings file; you can edit a copy at `%LOCALAPPDATA%\SSGen\store-presets.json`).
5. **Render all.** Images go to the project's `renders` folder, one folder per store and size, each checked against the store rules (size, format, no transparency, counts). Each image also gets an editable design file for the editor (M6).

App Store images show your Android screens in an iPhone-style frame. Apple expects screenshots of the actual iOS app, so replace them with real iOS captures if your iOS app looks different.

Exploration turns on a clean status bar (9:41, full battery, no notifications) while it runs, and restores the real one afterwards.

## Reviewing and editing

**Gallery** shows every rendered image by store and size, each checked against that store's rules (a tick, a warning or an error with the reason), plus count problems for the whole set (for example fewer than the minimum). **Listing preview** lines the set up with your app icon, name and tagline (and the feature graphic for Google Play), to check order and flow. Double-click an image, or use Edit, to open it in the editor.

**Editor** changes one image at a time:

- Click a layer on the canvas or in the Layers list. Drag to move (it snaps to the centre lines), drag a corner to resize, drag the round handle to rotate. Shift changes the behaviour: free proportions for pictures and devices, kept proportions for the rest, 15° steps for rotation.
- Add text, rectangles, ellipses and pictures (logos, badges; the file is copied into the project).
- Properties: text, font, weight, size, colour and alignment; fills (solid or gradient), outlines and soft edges; device frame and colour; brightness, contrast and saturation for screenshots and pictures; position, rotation, opacity and shadow. With nothing selected you edit the background.
- Undo and redo everything (Ctrl+Z, Ctrl+Y). Arrow keys nudge, Delete removes, Ctrl+D duplicates, Ctrl+S saves.
- **Save** renders the image file again with the same engine as Render all, and checks it against the store rules. **Apply style to all** gives the other images of the same size this image's look and layout; each keeps its own headline and screenshot.

Rendering again on the Design page replaces edited images with fresh designs; SSGen asks first.

## Languages

On the Design page, **Languages** sets the primary language (the captions you write) and any extra languages. For each extra language you can type the captions or click **Translate with AI** (text only: no screenshots are sent; have a native speaker check the result). **Render all** then renders every language; screens without a translation use the primary captions. Arabic, Urdu, Persian and Hebrew are shaped and laid out right to left; Chinese, Japanese and Thai wrap correctly; if the template's font lacks a script, SSGen uses a Windows font that has it.

## Exporting

**Export** lets you choose store sizes and languages, checks every file once more against the store rules, and saves an upload-ready folder: `Store/language/size/01_name.png`, numbered in listing order, with `manifest.json` (sizes, checksums, problems, and the official pages the rules come from). Tick **Also make a ZIP file** for a single file to share. Images that break a rule are left out unless you include them on purpose.

## Installer

`powershell -File tools/make-installer.ps1 -Version 0.8.0` builds `artifacts/releases/SSGen-Setup.exe` with Velopack. SSGen installs into `%LOCALAPPDATA%\SSGen.Desktop` (program) and keeps your data in `%LOCALAPPDATA%\SSGen`; uninstalling (Settings > Apps) removes the program only. It's unsigned, so Windows SmartScreen asks for confirmation on first run ("More info", then "Run anyway"). The installer adds the .NET 10 Desktop Runtime if it's missing. Run `tools/fetch-deps.ps1` once before building it.

## AI

SSGen doesn't ship with, or default to, any AI provider. Add your own in **Settings > AI providers**:

- an Anthropic, OpenAI or Gemini API key;
- any OpenAI-compatible endpoint (OpenRouter, Groq, Together, LM Studio, llama.cpp and others);
- a local Ollama model.

**Test connection** checks the key and the model, and whether the model accepts screenshots. You can pick a different model for each task: exploration, ranking, headlines and translation.

**Cost**: local providers are free. For paid providers, enter each model's price per million tokens yourself; SSGen doesn't assume prices. The Explore page shows live token use and the estimated cost, and stops a run at your spending cap. You are asked for a cap before the first paid run.

**Privacy**: before a cloud model first sees screenshots of your app, SSGen asks for consent. If you decline, the model works from the screen's text only. Local models never need consent.

Without AI, everything works in manual mode.

## Licence notices

See [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).
