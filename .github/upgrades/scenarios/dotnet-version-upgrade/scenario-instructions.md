# .NET Version Upgrade

## Preferences
- **Flow Mode**: Automatic
- **Target Framework**: .NET 10 (LTS) (`net10.0-windows`)

## Upgrade Options
- **Upgrade Strategy**: All-at-Once
- **Unsupported API Handling**: Fix Inline
- **Test Coverage**: Skip

## Strategy
**Selected**: All-at-Once
**Rationale**: A single SDK-style WPF project is moving from modern .NET 8 to .NET 10 with no project dependencies, so the upgrade can be completed as one atomic operation.

### Execution Constraints
- Upgrade the project, packages, and compatibility fixes as one atomic operation.
- Verify the .NET 10 SDK and global.json compatibility before changing the project.
- Restore dependencies and resolve the assessed API compatibility issues in the same bounded upgrade pass.
- Validate the complete project build after the atomic upgrade; do not create a separate test-baseline task because test coverage was explicitly skipped.

