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

## Shipped with the app: bundled binaries and sources (fetched by `tools/fetch-deps.ps1`)

| Component | Licence | Use |
|---|---|---|
| [scrcpy-server v5.0.1](https://github.com/Genymobile/scrcpy) (Genymobile) | Apache-2.0 | Pushed to USB phones to stream the screen (H.264) and inject input. Licence text: `tools/scrcpy/LICENSE` |
| [FFmpeg 9.0](https://ffmpeg.org/legal.html), **LGPL shared build** by [BtbN/FFmpeg-Builds](https://github.com/BtbN/FFmpeg-Builds): avcodec, avutil, swscale, swresample DLLs only | LGPL-2.1-or-later | H.264 decoding for the phone preview. **Dynamically loaded and unmodified.** Users may replace the DLLs in `tools/ffmpeg/` with their own compatible build. Build info: `tools/ffmpeg/BUILD.txt`; source: https://github.com/FFmpeg/FFmpeg |
| `emulator_controller.proto` from the Android Emulator (`<sdk>/emulator/lib`) | Apache-2.0 (header retained) | Generated client for the emulator's gRPC API: `src/StoreShot.Emulator/Protos/` |

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

## Planned (to be added when introduced)

- Velopack (MIT) for the installer (M7)
- Fonts under the SIL Open Font License (M5/M6)
