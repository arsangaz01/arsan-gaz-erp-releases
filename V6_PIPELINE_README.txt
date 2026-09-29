ARSAN GAZ ERP V6 CI / SETUP PIPELINE

1. Run PREPARE_V6_PIPELINE.cmd.
2. In the project folder, run PUSH_SOURCE_AND_WORKFLOW.cmd.
3. Complete GitHub authentication if requested.
4. Run CREATE_RELEASE_TAG.cmd.
5. GitHub Actions builds:
   - ArsanGazERPSetup.exe
   - ArsanGazERP-portable-win-x64.zip
6. Download the files from the GitHub Release page.

The workflow is stored in .github/workflows/release.yml.
The installer definition is stored in installer/ArsanGazERP.iss.
The project is backed up before files are added.