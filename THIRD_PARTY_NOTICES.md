# Third-party notices

SSGen uses the following third-party components. Only permissive licences are allowed. LGPL is acceptable only for components that are dynamically linked and replaceable.

## Shipped with the app: NuGet packages

| Component | Licence | Use |
|---|---|---|
| [CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet) | MIT | MVVM source generators |
| [Microsoft.Extensions.Hosting / DependencyInjection / Logging](https://github.com/dotnet/runtime) | MIT | DI and logging abstractions |
| [Microsoft.Data.Sqlite](https://github.com/dotnet/efcore) | MIT | SQLite access |
| [SQLitePCLRaw](https://github.com/ericsink/SQLitePCL.raw) (transitive) | Apache-2.0 | Native SQLite binding |
| [SQLite](https://sqlite.org/copyright.html) (via e_sqlite3) | Public domain | Database engine |
| [System.Security.Cryptography.ProtectedData](https://github.com/dotnet/runtime) | MIT | DPAPI secret encryption |
| [Serilog](https://github.com/serilog/serilog), Serilog.Extensions.Hosting, Serilog.Sinks.File | Apache-2.0 | File logging |
| [SkiaSharp](https://github.com/mono/SkiaSharp) (+ NativeAssets.Win32) | MIT (Skia: BSD-3-Clause) | Image decoding and encoding, icon compositing |
| [FFmpeg.AutoGen](https://github.com/Ruslan-B/FFmpeg.AutoGen) | MIT | C# bindings to FFmpeg (loaded dynamically) |
| [Grpc.Net.Client](https://github.com/grpc/grpc-dotnet) | Apache-2.0 | Emulator gRPC client (live preview) |
| [Google.Protobuf](https://github.com/protocolbuffers/protobuf) | BSD-3-Clause | Protobuf runtime |
| [Anthropic](https://github.com/anthropics/anthropic-sdk-csharp) (official C# SDK) | MIT | Claude provider adapter (used only when the user adds an Anthropic profile) |
| [Microsoft.Extensions.AI.Abstractions](https://github.com/dotnet/extensions), System.Net.ServerSentEvents (transitive, via Anthropic) | MIT | SDK dependencies |

## Shipped with the app: bundled binaries and sources (fetched by `tools/fetch-deps.ps1`)

| Component | Licence | Use |
|---|---|---|
| [scrcpy-server v5.0.1](https://github.com/Genymobile/scrcpy) (Genymobile) | Apache-2.0 | Pushed to USB phones to stream the screen (H.264) and inject input. Licence text: `tools/scrcpy/LICENSE` |
| [FFmpeg 9.0](https://ffmpeg.org/legal.html), **LGPL shared build** by [BtbN/FFmpeg-Builds](https://github.com/BtbN/FFmpeg-Builds): avcodec, avutil, swscale, swresample DLLs only | LGPL-2.1-or-later | H.264 decoding for the phone preview. **Dynamically loaded and unmodified.** Users may replace the DLLs in `tools/ffmpeg/` with their own compatible build. Build info: `tools/ffmpeg/BUILD.txt`; source: https://github.com/FFmpeg/FFmpeg |
| [Poppins](https://github.com/itfoundry/Poppins) (Regular, SemiBold, Bold, ExtraBold) | SIL Open Font License 1.1 | Text in rendered store images. Licence text: `fonts/Poppins-OFL.txt` |
| [DM Serif Display](https://github.com/googlefonts/dm-fonts) | SIL Open Font License 1.1 | Optional serif headlines. Licence text: `fonts/DMSerifDisplay-OFL.txt` |
| [SkiaSharp.HarfBuzz / HarfBuzzSharp](https://github.com/mono/SkiaSharp) (HarfBuzz) | MIT (HarfBuzz: Old MIT) | Text shaping for Arabic, Urdu, Hebrew, Indic and other scripts |
| [Velopack](https://github.com/velopack/velopack) | MIT | Installer and the startup hook (`vpk` builds the installer; not shipped) |
| `emulator_controller.proto` from the Android Emulator (`<sdk>/emulator/lib`) | Apache-2.0 (header retained) | Generated client for the emulator's gRPC API: `src/SSGen.Emulator/Protos/` |

## Development, build and test only (not shipped)

| Component | Licence |
|---|---|
| Grpc.Tools (protobuf code generation) | Apache-2.0 |
| xUnit, xunit.runner.visualstudio | Apache-2.0 |
| Microsoft.NET.Test.Sdk | MIT |
| coverlet.collector | MIT |
| Sample APK: [Privacy Friendly Notes 2.2.2](https://f-droid.org/packages/org.secuso.privacyfriendlynotes/) from F-Droid | GPL-3.0. Test data only: downloaded into `third_party/samples/`, never committed or shipped |

## Not bundled

- **Android SDK tools** (adb, emulator, aapt2, Android CLI, system images) are installed by the user under Google's [Android SDK License](https://developer.android.com/studio/terms). SSGen never redistributes them.
- **Segoe Fluent Icons / Segoe MDL2 Assets** are Windows system fonts. SSGen uses the installed copies.
- **Device frames** in rendered images are drawn by SSGen's own code (generic shapes); no device artwork from Apple, Google or others is included.
