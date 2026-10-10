# StoreShot Studio

Turn an Android APK into upload-ready store listing screenshots for Google Play and the App Store. The app runs your APK in the Android Emulator, captures its screens (by hand or with an AI agent of your choice), renders them into designed listing images, and exports them at the correct store sizes.

> **Status: milestone M1 (foundation).** The shell, projects, settings and environment setup assistant work. The emulator, exploration, templates, editor and export arrive in M2–M7.

## Requirements

- Windows 10 or 11 (x64)
- [.NET 10 SDK](https://dotnet.microsoft.com/download) to build from source
- Android SDK with platform-tools, the emulator, build-tools and an x86_64 Google Play system image (API 30+). Installing [Android Studio](https://developer.android.com/studio) is the easiest way to get these.
- Hardware virtualization enabled, with the **Windows Hypervisor Platform** feature turned on
- Java 17+ is optional. Only the legacy SDK tools need it.

## Build and run

```powershell
dotnet build StoreShot.slnx
dotnet test StoreShot.slnx
dotnet run --project src/StoreShot.App
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

Each check explains why StoreShot needs it and how to fix it. When the SDK command-line tools are installed, missing SDK packages can be installed in one click. You're asked to accept Google's SDK licence first.

The SDK is found automatically through `ANDROID_HOME`, `ANDROID_SDK_ROOT` or `%LOCALAPPDATA%\Android\Sdk`. If yours is elsewhere, set the folder in **Settings**.

## Where things are stored

| What | Where |
|---|---|
| Settings, recent projects, encrypted API keys | `%LOCALAPPDATA%\StoreShotStudio\app.db` |
| Logs (14 days) | `%LOCALAPPDATA%\StoreShotStudio\logs` |
| Projects (default) | `Documents\StoreShot Studio\Projects\<name>` |

API keys are encrypted with Windows DPAPI and can only be read by your Windows account. StoreShot sends no telemetry.

## AI

StoreShot doesn't ship with, or default to, any AI provider. From M3 you can add your own:

- an Anthropic, OpenAI or Gemini API key;
- any OpenAI-compatible endpoint (OpenRouter, Groq, LM Studio and others);
- a local Ollama model.

Without AI, everything works in manual mode.

## Licence notices

See [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).
