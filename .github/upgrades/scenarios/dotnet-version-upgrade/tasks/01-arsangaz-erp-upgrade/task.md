# 01-arsangaz-erp-upgrade: Upgrade and validate the WPF application

Verify that the .NET 10 SDK and repository SDK configuration support `net10.0-windows`, then upgrade the single SDK-style WPF project atomically. Update the target framework and the three assessed Entity Framework Core package references (`Microsoft.EntityFrameworkCore.Design`, `Microsoft.EntityFrameworkCore.Sqlite`, and `Microsoft.EntityFrameworkCore.Tools`) to the compatible versions identified by the assessment. The remaining compatible package set should stay unchanged unless restore or compilation demonstrates a concrete requirement.

Apply the assessed inline compatibility fixes across the project, with particular attention to the 317 binary-incompatible WPF findings involving routed events, controls, dialogs, application startup, and file dialogs, plus the 17 behavioral-change findings. Restore dependencies and perform one bounded build-and-fix pass so the project compiles cleanly. No test-baseline work is included because test coverage was skipped.

**Done when**: `ArsanGazERP.csproj` targets `net10.0-windows`, the required EF Core packages resolve compatibly, the assessed API issues are addressed inline, restore succeeds, and the complete project builds with zero errors.

## Research Findings

- Scope is one SDK-style WPF project: `ArsanGazERP.csproj`; it currently targets `net8.0-windows`, uses `<UseWPF>true</UseWPF>`, and has no project references or dependants.
- The installed SDKs are `8.0.425` and `10.0.401`; no `global.json`, `Directory.Build.props`, or `Directory.Packages.props` is present, so SDK resolution and package versions are controlled by the project and installed SDK.
- Assessment data reports 3 package updates, 1 TFM issue, and 334 API incidents. The API incidents are reported against generated `obj\Debug\net8.0-windows\win-x64\*.g.cs` files; source inspection found normal WPF event/control/dialog usage in `App.xaml.cs`, `MainWindow.xaml.cs`, `Views/DatabaseManagementWindow.xaml.cs`, and `Views/OperationsWindow.xaml.cs`, with no stubs. These generated findings must be revalidated after retargeting and not edited directly.
- Required package actions from the assessment: `Microsoft.EntityFrameworkCore.Design`, `Microsoft.EntityFrameworkCore.Sqlite`, and `Microsoft.EntityFrameworkCore.Tools`, each `8.0.21` to `10.0.12`. The remaining 48 assessed packages stay unchanged unless restore/build reports a concrete dependency conflict.
- The assessment also reports 17 behavioral-change incidents, primarily `System.Uri`, `HttpContent`, and `JsonDocument`; these usages are limited to existing service code and will be validated by compilation. No test project exists and test coverage is explicitly skipped.

## Decomposition Assessment

- Loaded execution guidance: `dotnet-version-upgrade/execution.md`.
- Loaded breakdown hints: `dotnet-version-upgrade/breakdown-hints/common.md`.
- Stub scan: no `// STUB:` markers found in project C# sources.
- Verdict: atomic. The task affects one project only, has no dependency tiers, no package replacement research, no multi-targeting, and no test baseline task.

## Validation Decision

- Because this is a WPF/XAML project, use Visual Studio `msbuild.exe` with `/restore` for the final build per the building-projects skill. First use restore/build output to determine whether any source-level compatibility fix is actually required; do not modify generated `obj` files.
