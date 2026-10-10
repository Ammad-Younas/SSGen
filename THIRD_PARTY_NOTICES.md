# Third-party notices

StoreShot Studio uses the following third-party components. Only permissive licences are allowed. LGPL is acceptable only for components that are dynamically linked.

## Shipped with the app

| Component | Licence | Use |
|---|---|---|
| [CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet) | MIT | MVVM source generators |
| [Microsoft.Extensions.Hosting / DependencyInjection / Logging](https://github.com/dotnet/runtime) | MIT | DI and logging abstractions |
| [Microsoft.Data.Sqlite](https://github.com/dotnet/efcore) | MIT | SQLite access |
| [SQLitePCLRaw](https://github.com/ericsink/SQLitePCL.raw) (transitive) | Apache-2.0 | Native SQLite binding |
| [SQLite](https://sqlite.org/copyright.html) (via e_sqlite3) | Public domain | Database engine |
| [System.Security.Cryptography.ProtectedData](https://github.com/dotnet/runtime) | MIT | DPAPI secret encryption |
| [Serilog](https://github.com/serilog/serilog), Serilog.Extensions.Hosting, Serilog.Sinks.File | Apache-2.0 | File logging |

## Development and test only (not shipped)

| Component | Licence |
|---|---|
| xUnit, xunit.runner.visualstudio | Apache-2.0 |
| Microsoft.NET.Test.Sdk | MIT |
| coverlet.collector | MIT |

## Not bundled

- **Android SDK tools** (adb, emulator, aapt2, Android CLI, system images) are installed by the user under Google's [Android SDK License](https://developer.android.com/studio/terms). StoreShot never redistributes them.
- **Segoe Fluent Icons / Segoe MDL2 Assets** are Windows system fonts. StoreShot uses the installed copies and doesn't bundle them.

## Planned (to be added when introduced)

- scrcpy-server (Apache-2.0) and an FFmpeg LGPL shared build (dynamically loaded) for the M2 live preview
- SkiaSharp (MIT) for M5/M6
- Velopack (MIT) for M7
- Fonts under the SIL Open Font License
