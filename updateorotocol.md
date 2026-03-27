TWO MAIN LOCAL RELEASE COMMANDS

1. Only the custom bootstrap installer folder:
.\scripts\Build-BootstrapInstaller.ps1

Output:
artifacts\bootstrap-installer\X.Y\AudioBit-Setup

What it is for:
- share this folder directly with another laptop or PC
- install through the custom AudioBit.Setup UI
- still updater-friendly because the payload is the Velopack portable layout


2. New GitHub release folder with version bump:
.\scripts\Build-GitHubReleaseFolder.ps1

What it does:
- reads version.json
- bumps the version by 0.1
- writes the new version back into version.json
- creates:
  artifacts\github-release\NEW_VERSION\AudioBit-Setup
  artifacts\github-release\NEW_VERSION\GitHub-Upload
- keeps the custom installer folder separate from the GitHub upload assets
- keeps both outputs updater-friendly
