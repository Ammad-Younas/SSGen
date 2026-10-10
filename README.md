# SSGen

Turn an Android APK into upload-ready store listing screenshots for Google Play and the App Store. The app runs your APK in the Android Emulator, captures its screens (by hand or with an AI agent of your choice), renders them into designed listing images, and exports them at the correct store sizes.

> **Status: milestone M2 (emulator).** Projects, settings, the environment setup assistant and the full device pipeline work: virtual device management, APK import and install, a live preview you can control, screen capture and logcat. AI exploration, templates, the editor and export arrive in M3–M7.

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

## AI

SSGen doesn't ship with, or default to, any AI provider. From M3 you can add your own:

- an Anthropic, OpenAI or Gemini API key;
- any OpenAI-compatible endpoint (OpenRouter, Groq, LM Studio and others);
- a local Ollama model.

Without AI, everything works in manual mode.

## Licence notices

See [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).
