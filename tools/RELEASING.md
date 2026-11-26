# Publishing releases and WinGet updates

ARS is listed as `JeffLepp.AgentsRideShotgun` in the public WinGet catalog.
Pushing source changes to this repository does not update that catalog. Each
new release needs a published installer and a new version manifest submitted
to [microsoft/winget-pkgs](https://github.com/microsoft/winget-pkgs/tree/master/manifests/j/JeffLepp/AgentsRideShotgun).

Check the two published channels before choosing a version:

```powershell
winget show --id JeffLepp.AgentsRideShotgun --exact --source winget --versions
gh release list --repo JeffLepp/AgentsRideShotgun --limit 5
```

## Build and verify the release

1. Finish reviewing and testing the intended changes. Update `Version` in
   `Directory.Build.props` to an unused three-part version. Local installer
   directories also reserve their version: never overwrite an existing package
   or replace the bytes at a published release URL. Commit and push the reviewed
   source with the user's existing human Git identity.
2. Load the existing Azure Artifact Signing credentials into the release shell.
   `build-installer.ps1` requires `AZURE_CLIENT_ID`, `AZURE_CLIENT_SECRET`,
   `AZURE_TENANT_ID`, `DESKWEAVE_SIGN_ACCOUNT`, `DESKWEAVE_SIGN_ENDPOINT`, and
   `DESKWEAVE_SIGN_PROFILE` when `-Sign` is used. Keep their values out of source
   control and logs. Put the .NET 10 SDK and the matching Velopack CLI on `PATH`;
   the script checks the CLI against the SDK version in `app/ARS.csproj`.
3. Save workspace work and quit the development controller before publishing.
   The publish script refuses to replace a running build. Build the signed
   package from the repository root:

   ```powershell
   $version = [string]([xml](Get-Content -Raw Directory.Build.props)).Project.PropertyGroup[0].Version
   .\tools\build-installer.ps1 -Version $version -Sign
   $release = Join-Path $PWD "artifacts/installer/$version"
   $installer = Join-Path $release 'ARS-Setup.exe'
   ```

4. Test the exact signed package, including a clean install and an upgrade from
   the currently published installer. Run the relevant engine/UI regression
   gates for the changes being shipped. These existing checks record package
   signatures, scan results, clean install, and upgrade behavior; use fresh
   output directories for each run:

   ```powershell
   .\tools\check-package-security.ps1 -Installer $installer -Sign `
       -OutputDirectory "artifacts/release-checks/$version/security"
   # Supply the previously published, verified installer; do not use an unrelated test build.
   $previousInstaller = (Resolve-Path 'artifacts/installer/0.3.2/ARS-Setup.exe').Path
   .\tools\check-clean-install.ps1 -Installer $installer -PreviousInstaller $previousInstaller `
       -OutputDirectory "artifacts/release-checks/$version/install"
   ```

   Run the source regression matrix from the repository root. This invocation
   applies the script policy only to the release-check process.

   ```powershell
   $checks = Join-Path $PWD ('artifacts/release-checks/source-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
   powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\tools\check-release.ps1 -OutputDirectory $checks
   ```

   The optional `-Browser` parameter selects a clean fixture Chromium executable.
   Reports verify the isolated process exit
   and the completed gate result independently. This does not certify a signed
   package.

   Replace the previous version path when a newer release has been published.
   The local clean install check needs Windows Sandbox. On Windows Home, use
   the **Release installation validation** GitHub Actions workflow on its
   disposable Windows runner. The workflow template is
   `tools/release-validation.workflow.yml`; install it as
   `.github/workflows/release-validation.yml` to enable it. GitHub OAuth pushes
   that add a workflow require the `workflow` scope. Its default run builds an unsigned test package
   and validates source packaging only. After uploading the signed package to
   a draft GitHub release, run it again with that `release_tag` to validate the
   actual release installer before making the release public. A successful build or an old
   probe report alone does not verify the signed package. See the
   [engine probe guidance](../engine-probe/README.md) and
   [lifecycle/update policy](../engine/LIFECYCLE.md) for remaining acceptance work.

## Publish the installer, then submit its WinGet manifest

1. Create a GitHub release for the tested source commit, tagged `v<version>`,
   with release notes describing the shipped fixes. Upload the tested
   `ARS-Setup.exe`, the full `.nupkg`, and `releases.win.json` from the same
   release directory. The application updater reads the GitHub Velopack feed;
   publishing only Setup would leave that update path incomplete. Confirm all
   assets are present before publishing the release. Never replace released
   assets with different bytes; issue a new version instead.
2. Generate the WinGet manifests from the published release:

   ```powershell
   .\tools\prepare-winget.ps1 -Version $version `
       -ReleaseRepository JeffLepp/AgentsRideShotgun
   $manifest = Join-Path $PWD "artifacts/winget/manifests/j/JeffLepp/AgentsRideShotgun/$version"
   winget validate --manifest $manifest
   ```

   The preparation script verifies the local package evidence and publisher
   signature, downloads the versioned public installer URL, and requires its
   SHA-256 to match the tested local installer. It generates three YAML files
   under the same package ID, with the silent switches and `ARSApp` product code.
   It does not publish a release or submit a pull request.
3. Review the generated YAML against the previous accepted manifest. Submit
   those three files as a new version in `microsoft/winget-pkgs`. For example,
   install [WinGet Create](https://github.com/microsoft/winget-create), sign in
   through its credential flow, then submit the prepared directory:

   ```powershell
   winget install --id Microsoft.WingetCreate --exact --source winget
   wingetcreate token -s
   wingetcreate submit --no-open $manifest
   ```

   Submission opens a pull request for Microsoft's installer validation and
   review. Track the PR until it merges and appears in the public catalog.
   [Microsoft's submission documentation](https://learn.microsoft.com/en-us/windows/package-manager/package/winget-validation)
   explains that repository merge and public catalog availability are separate
   steps. WinGet does not discover new ARS versions merely from a Git push or
   GitHub release.
4. Verify the delivered catalog independently:

   ```powershell
   winget source update --name winget
   winget show --id JeffLepp.AgentsRideShotgun --exact --source winget
   ```

   Confirm the expected version, versioned URL, and SHA-256. On a disposable
   machine with the previous version installed, verify an actual catalog
   upgrade with:

   ```powershell
   winget upgrade --id JeffLepp.AgentsRideShotgun --exact --source winget
   ```

   Record the resulting installed version and payload evidence. Use
   `verify-installed-payload.ps1 -Package <tested-full.nupkg>` to compare every
   installed file with the tested signed package. Its optional
   `-ExpectedPackageSha256` pins the package bytes; `-OutputPath` retains the
   verification report.
