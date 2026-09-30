# .NET Version Upgrade Plan

## Overview

**Target**: Upgrade `ArsanGazERP.csproj` from `net8.0-windows` to `net10.0-windows`.
**Scope**: One small SDK-style WPF project with 29 code files and no project dependencies.

### Selected Strategy
**All-At-Once** — All projects upgraded simultaneously in a single operation.
**Rationale**: One project, already SDK-style and targeting modern .NET, with no project dependencies or dependency tiers.

## Upgrade Options

| Option | Selected | Why |
|--------|----------|-----|
| Upgrade Strategy | All-at-Once | The solution contains one modern .NET project with no dependency tiers, making a single atomic upgrade appropriate. |
| Unsupported API Handling | Fix Inline | Resolve the assessed compatibility findings directly in the affected project during the upgrade pass. |
| Test Coverage | Skip | Test-baseline generation was explicitly declined for this planning run. |

## Tasks

### 01-arsangaz-erp-upgrade: Upgrade and validate the WPF application

Verify that the .NET 10 SDK and repository SDK configuration support `net10.0-windows`, then upgrade the single SDK-style WPF project atomically. Update the target framework and the three assessed Entity Framework Core package references (`Microsoft.EntityFrameworkCore.Design`, `Microsoft.EntityFrameworkCore.Sqlite`, and `Microsoft.EntityFrameworkCore.Tools`) to the compatible versions identified by the assessment. The remaining compatible package set should stay unchanged unless restore or compilation demonstrates a concrete requirement.

Apply the assessed inline compatibility fixes across the project, with particular attention to the 317 binary-incompatible WPF findings involving routed events, controls, dialogs, application startup, and file dialogs, plus the 17 behavioral-change findings. Restore dependencies and perform one bounded build-and-fix pass so the project compiles cleanly. No test-baseline work is included because test coverage was skipped.

**Done when**: `ArsanGazERP.csproj` targets `net10.0-windows`, the required EF Core packages resolve compatibly, the assessed API issues are addressed inline, restore succeeds, and the complete project builds with zero errors.
