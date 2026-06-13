# BepInEx Faster Load AssetBundles - Proton/Wine Patch

This is a compatibility patch of the popular [BepInEx Faster Load AssetBundles](https://thunderstore.io/c/lethal-company/p/DiFFoZ/BepInEx_Faster_Load_AssetBundles_Patcher/) patcher. It resolves a critical drive space detection issue that occurs under Linux, Proton, Wine, and Steam Deck environments.

## The Issue

The original mod uses the standard C# `DriveInfo` class combined with `Path.GetPathRoot(Path.GetFullPath(path))` to check if the target drive has at least 10GB of free space before caching decompressed asset bundles.

Under Proton/Wine:
1. Secondary steam libraries (e.g., those mounted under `/run/media/...`) are mapped inside the Wine prefix to the virtual drive letter `Z:\`.
2. `Path.GetPathRoot` extracts the root as `Z:\`, which Wine maps directly to the host's Linux root directory (`/`).
3. Under Wine-Mono, querying `Z:\` as a drive root often returns `0` free bytes or queries the wrong system partition.
4. The mod falsely concludes there is no space, disables decompression caching, and logs: `Ignoring request of decompressing, because the free drive space is less than 10GB`.
5. This forces heavy modpacks (200+ mods) to decompress assets in-memory on-the-fly, leading to severe RAM bloat and Out-Of-Memory (OOM) system crashes on 8GB RAM machines.

## The Fix

This patch replaces the root-based `DriveInfo` check with a direct Win32 `GetDiskFreeSpaceEx` API call using the **full subdirectory path** instead of stripping it down to the drive root.
* By passing the full directory path (e.g., `Z:\run\media\user\drive\...`), Wine successfully translates the Windows path back to the native Linux mount path and queries the actual filesystem mount.
* This correctly retrieves the free disk space of your secondary ext4/btrfs drive (e.g., 300+ GB) instead of failing.
* Added a robust fallback mechanism: if any query fails or returns a buggy type, the method defaults to `true` (enabling decompression) instead of silently disabling the mod.

## Installation

Since this is a preloader patcher, it must be installed into the BepInEx `patchers` folder:
1. Create a folder named `BepInExFasterLoadAssetBundles_ProtonPatch` in `Lethal Company/BepInEx/patchers/`.
2. Place the compiled `BepInExFasterLoadAssetBundles_ProtonPatch.dll` inside that folder.

## Build Requirements

To compile the project from source, reference the following libraries from your game files:
* `BepInEx.dll`
* `0Harmony.dll`
* `Mono.Cecil.dll`
* `UnityEngine.dll`
* `UnityEngine.CoreModule.dll`
* `UnityEngine.AssetBundleModule.dll`
* `Unity.Collections.dll`
* `Newtonsoft.Json` (available via NuGet)