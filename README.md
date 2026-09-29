# Arsan Gaz ERP

Windows x64 desktop application. Download the latest installer or portable ZIP from [GitHub Releases](https://github.com/arsangaz01/arsan-gaz-erp-releases/releases/latest).

## Build

Requires the .NET 10 SDK on Windows.

```powershell
dotnet restore ArsanGazERP.csproj -r win-x64
dotnet publish ArsanGazERP.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish
```

## Release

Push a `v*` tag to build the installer and portable ZIP and attach both to the matching GitHub Release. To rebuild an existing tag, run **Build and Release Arsan Gaz ERP** from GitHub Actions and enter the tag, such as `v5.1.0`.