# Windrunner Launcher

Windrunner Launcher is a small desktop app for playing **Windrunner WOW** on the Windrunner server. It is the one thing you need to run a full private server on your own machine.

Download a release, put the executable in a folder, and run it. On **Windows** it is a single portable `.exe`. On **Linux** it is an AppImage. There is no installer, no registry entries, and no telemetry.

> **License:** source-available, non-commercial, share-alike. See [`LICENSE`](LICENSE). This is *not* an OSI open source license. The WoW client, MariaDB, emulator binaries, and mods keep their own licenses and are not part of this repository.

## What it does

**Play.** One Play button walks you through whatever is still missing: first-time setup, downloading the client, installing a local server if you want one, then launching the game. Realmlist is written for you so the client talks to the realm you selected.

**Realms.** Connect to Windrunner (or any other remote realm you add), or run a **local** realm that lives entirely on your PC. Switch between them without copying folders by hand.

**Client and mods.** The launcher can download a clean 1.12 client, keep it up to date, and manage optional mods and VanillaTweaks. Same for addons.

**Local server (optional).** If you want a private world, the launcher can install and start MariaDB, login (`realmd`), and world (`mangosd`) for you, stop them cleanly, and roll back a bad server update from an automatic backup.

**Updates.** Client content comes from Windrunner’s signed website manifest. Server packages and launcher self-updates come from GitHub Releases. Downloads are checksum-verified; you can cancel and retry.

**News.** The home screen shows Windrunner news from the project RSS feed. The launcher does not collect usage analytics.

## Build it yourself

You need the [.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0). NativeAOT Windows builds also need Visual Studio’s **Desktop development with C++** workload.

```bash
# run tests
dotnet test

# run the app while developing
dotnet run --project src/WindrunnerLauncher.App

# Windows portable exe (from a Windows machine)
dotnet publish src/WindrunnerLauncher.App -c Release -r win-x64 -p:PublishAot=true
```

The published file is `src/WindrunnerLauncher.App/bin/Release/net9.0/win-x64/publish/WindrunnerLauncher.exe`. Copy that one file into an empty folder and run it.

CI publishes **Windows** and **Linux** builds on pushes to `main`. Linux AppImages: `chmod +x WindrunnerLauncher.AppImage` then run it. If FUSE is missing, use `APPIMAGE_EXTRACT_AND_RUN=1 ./WindrunnerLauncher.AppImage`. Linux data lives under `~/.local/share/windrunner-launcher`.

## Contributing

Don't
