# Workshop Collection Filter

A client plugin for Space Engineers (version 1) that filters the **workshop blueprints list**
of the in-game Blueprints screen down to the items of a single **Steam Workshop collection**.
It ships both as a [Pulsar](https://github.com/SpaceGT/Pulsar) plugin and as a
**CringeLauncher** plugin package.

The collection dropdown lists:

- collections **owned** by the player (published under their Steam account),
- collections the player is **subscribed** to, and
- collections the player has **favorited**.

Collections that appear in more than one of those lists are shown once; the sources are listed
in the item's tooltip. The dropdown shows owned collections first, then subscribed and favorited
collections, alphabetically within each group.

Collections whose title contains `[WCF-IGNORE]` are hidden from the picker. If one of them was
selected previously, the filter falls back to **All Subscribed Items**.

## Usage

1. Open the Blueprints screen (F10, or the blueprint button in the main menu).
2. Set the screen's group filter to **Steam Workshop** or **All** (the group button cycles
   Mixed → Local → Workshop → Cloud). The **Workshop collection** picker appears in the search
   box row in both views; the search box moves down one row while the picker is visible, and
   moves back up for the Local and Cloud views.
3. Pick a collection from that picker. From Local or Cloud the screen switches to the Steam
   Workshop group so the collection is visible; in the All view the local and cloud blueprints
   stay visible and only the workshop part of the list is filtered.
4. Only blueprints that belong to the selected collection remain visible. This includes
   blueprints of the collection that the player is **not subscribed to**; those are downloaded
   on demand the first time they are opened or copied.
   The normal search box, sorting and group selection keep working on top of the filter.
5. Pick **All Subscribed Items** to turn the filter off.

The screen's refresh button also reloads the collection list, so newly subscribed collections
show up without restarting the game.

The selection is stored in `%APPDATA%\SpaceEngineers\Storage\BlueprintCollections.cfg` and is
restored on the next game start.

## Options

Open the plugin's configuration dialog in Pulsar (plugin settings) or CringeLauncher (the plugin
entry). It shows the usage summary at the top and the **preview image cache limit** in MB at the
bottom. The default is 10 MB; 0 means unlimited. Changes are applied immediately.

The screen is provided through the loader's normal plugin settings entry; the blueprint screen
itself has no separate options button.

## How it works

- `BlueprintScreenRecreateControlsPatch` adds the picker at the search box position (the search
  box and list move down one row while it is visible, and move back when it is hidden) on the
  game's `MyGuiBlueprintScreen_Reworked` screen. The picker is visible while the screen's group
  filter is the Steam Workshop group or the All group.
- `WorkshopCollectionService` talks to Steamworks directly:
  - owned collections: `SteamUGC.CreateQueryUserUGCRequest` with
    `k_EUserUGCList_Published` + `k_EUGCMatchingUGCType_Collections`;
  - subscribed collections: the same call with `k_EUserUGCList_Subscribed`;
  - favorited collections: the same call with `k_EUserUGCList_Favorited`;
  - collection members and their details: `SteamUGC.CreateQueryUGCDetailsRequest`,
    `SetReturnChildren(true)` and `SteamUGC.GetQueryUGCChildren`.
- Members that are already subscribed are shown by the game's normal workshop path.
  Members that are **not subscribed** are exposed as detached workshop items for the duration
  of the list build (`BlueprintScreenWorkshopItemsPatch`), so the game still creates and sorts
  them normally.
- `BlueprintScreenGetImagePathPatch` prevents the screen from downloading a whole item just to
  get its thumbnail. Not subscribed members use a resized copy of the primary Steam Workshop
  image, fitted to the blueprint screen's 242×128 preview slot with the original aspect ratio
  (about 6 KB instead of 0.5–1 MB), downloaded once and cached locally.
- `LoadWorkshopPrefabPatch` lets the game's prefab loader resolve the metadata of a detached
  item. Without it, an on-demand blueprint would download but never load.
- `BlueprintScreenApplyFilteringPatch` runs after the game's own `ApplyFiltering()` and hides
  every workshop entry whose published file id is not in the selected collection. This composes
  with the existing search box instead of replacing the game's filtering.

### On-demand downloads

Only blueprints tagged `blueprint` are shown on the blueprint screen; mods, worlds and scripts
that a collection may also contain are ignored there.

Clicking a not subscribed member makes the game download it through
`SteamUGC.DownloadItem`, which keeps unsubscribed items in Steam's temporary cache. The plugin
never subscribes or unsubscribes anything. The same applies when double-clicking to copy the
blueprint: the game downloads it first and then loads it.

The plugin only reads Workshop data and downloads blueprints the player explicitly opens.

## Requirements

- Space Engineers (Steam version), client side.
- [Pulsar](https://github.com/SpaceGT/Pulsar) or CringeLauncher.
- Steam must be running and signed in, otherwise no collections can be listed.

## Building

### Pulsar

The Pulsar project (`BlueprintCollections/BlueprintCollections.csproj`) targets `net48` and
`net10.0` (Windows) like the
[client plugin template](https://github.com/CometWorks/client-plugin-template).

```bash
dotnet build BlueprintCollections.sln -c Release
```

`Directory.Build.props` auto-detects the game's `Bin64` folder from the Steam registry. If the
game is installed in a non-standard location, create `Directory.Build.props.user` with:

```xml
<Project>
  <PropertyGroup>
    <Bin64>C:\Path\To\SpaceEngineers\Bin64</Bin64>
    <!-- Optional: deploy each build into Pulsar's local plugin folder -->
    <Pulsar>$(APPDATA)\Pulsar</Pulsar>
  </PropertyGroup>
</Project>
```

`Directory.Build.props.user` is ignored by Git.

### CringeLauncher

`BlueprintCollections.Cringe/BlueprintCollections.Cringe.csproj` compiles the same sources from
`BlueprintCollections/` and packs them as a CringeLauncher plugin (`PackageType=CringePlugin`,
package id `Plugin.SE_BlueprintCollections`).

```bash
dotnet pack BlueprintCollections.Cringe/BlueprintCollections.Cringe.csproj -c Release -o artifacts
```

The resulting `Plugin.SE_BlueprintCollections.<version>.nupkg` can be installed through
CringeLauncher's plugin source. The project uses the CringePlugins NuGet feed
(`https://ng.zznty.ru/v3/index.json`) and gets the game reference assemblies and Harmony
transitively from `CringePlugins`.

For local development, open `BlueprintCollections.Cringe` and run the
`BlueprintCollections.Cringe Dev Client` launch profile. It starts `CringeBootstrap.exe` with the
game; `CringePlugins.MSBuild` generates `Properties/launchSettings.json` automatically. The
plugin is loaded from the project output directory (`DOTNET_USERDEV_PLUGINDIR`).

Every build also deploys the plugin and its companion files (`.dll`, `.pdb`, `.deps.json`,
`.runtimeconfig.json`, plus any copied dependency files) to:

```
%APPDATA%\CringeLauncher\plugins\Plugin.SE_BlueprintCollections
```

Override the destination with `-p:CringeLauncherPluginsDir=...`, or disable the deployment by
passing `-p:CringeLauncherPluginDir=`. If the game is running and holds the DLL, the copy is
reported as a warning and the build still succeeds.

### Local testing with Pulsar

Load the repository as a Pulsar development folder (start Pulsar with `-sources`), or set
`Pulsar` in `Directory.Build.props.user` and build to deploy the plugin into
`Pulsar\Legacy\Local\BlueprintCollections`.

## Limitations

- Not subscribed collection members show a resized copy of the primary Steam Workshop image
  (fitted to the game's 242×128 preview, original aspect ratio) while the blueprint itself is not downloaded. The image is cached in
  `%APPDATA%\SpaceEngineers\Storage\BlueprintCollections\thumbs`; once the blueprint is downloaded,
  its local thumbnail is used instead. The cache is trimmed to the configured limit (default
  10 MB, 0 = unlimited).
- The first download of a not subscribed item is done by Steam synchronously through the game's
  existing download path, so the details panel stays empty until it finishes.
- Blueprints shared directly between players (`MyBlueprintTypeEnum.SHARED`) are not filtered.
- The collection filter requires the Steam Workshop. Other UGC services are not supported.
- DLC metadata of not subscribed items is not populated, so missing-DLC handling falls back to
  the game's regular missing-block warning.

## Publishing to PluginHub

1. Push the repository to GitHub.
2. Fill in `RepoId`, `Author` and `Commit` in `BlueprintCollections.xml`.
3. Submit the plugin to [PluginHub](https://github.com/StarCpt/PluginHub).

## Troubleshooting

- Messages from the plugin are written to `%APPDATA%\SpaceEngineers\SpaceEngineers.log` and are
  prefixed with `BlueprintCollections:`. They include the number of collections found and any
  Steam API errors.
- If the dropdown only shows **All Subscribed Items**, make sure Steam is running, the player is
  signed in, and the game is not in offline mode. The plugin retries automatically while the
  Blueprints screen is open (about every 10 seconds after a failed query).
- If a collection is missing, verify that it is a **Space Engineers** Workshop collection and
  that the player owns it, is subscribed to it, or has favorited it.
- If only subscribed members of a collection appear, Steam did not return the member details.
  Press the screen's Refresh button to retry; the rest of the collection still filters normally.
- Blueprints downloaded on demand go to Steam's normal Workshop cache
  (`steamapps/workshop/content/244850/<id>`), the same place subscribed content lives.

## License

MIT, see [LICENSE](LICENSE).
