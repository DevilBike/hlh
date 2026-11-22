# Setup

## Play with it (no coding)

1. In game, open `/xlsettings`, go to **Experimental**, and add this under **Custom Plugin Repositories**:
   `https://raw.githubusercontent.com/DevilBike/hlh/main/repo.json`
2. Save, then install **Hunting Log Helper** from `/xlplugins`.
3. Install the plugins it depends on: **vnavmesh**, **Lifestream**, **BossMod Reborn**, and **Rotation Solver Reborn**.
4. Open it with `/huntlog`. Updates arrive through the normal plugin updater.

## Build it yourself (optional)

1. Install the [.NET 9 SDK](https://dotnet.microsoft.com/download) and Visual Studio 2022 or JetBrains Rider.
2. Run XIVLauncher with Dalamud at least once so the dev files exist.
3. Open `HuntingLogHelper.sln` and build. The output DLL is in `HuntingLogHelper/bin/x64/Debug/`.
4. In `/xlsettings` under **Experimental**, add that folder's `HuntingLogHelper.dll` as a dev plugin and enable it in `/xlplugins`.

## Releasing a new version

Push a tag such as `v0.1.1`. The GitHub Action builds the plugin, attaches `latest.zip` to a release, and bumps `repo.json`, so the in-game updater sees it.
