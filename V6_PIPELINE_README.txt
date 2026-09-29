ARSAN GAZ ERP RELEASE PIPELINE

REQUIREMENTS
- Git for Windows with access to the arsangaz01/arsan-gaz-erp-releases repository.
- GitHub Actions enabled for the repository.
- The application version is set in ArsanGazERP.csproj.

PUBLISH A NEW VERSION
1. Update the Version property in ArsanGazERP.csproj.
2. Run PUSH_SOURCE_AND_WORKFLOW.cmd to commit and push the source to main.
3. Run CREATE_RELEASE_TAG.cmd to push the matching v<version> tag.
4. GitHub Actions builds the .NET 10 x64 application, installer, and portable ZIP.
5. The workflow creates the release or replaces the assets on an existing release.

REBUILD AN EXISTING TAG
1. Open Actions in the GitHub repository.
2. Run Build and Release Arsan Gaz ERP.
3. Enter the tag, for example v5.1.0.

DOWNLOADS
Latest release: https://github.com/arsangaz01/arsan-gaz-erp-releases/releases/latest

The workflow is stored in .github/workflows/release.yml.
The installer definition is stored in installer/ArsanGazERP.iss.