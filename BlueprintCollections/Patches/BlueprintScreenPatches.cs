using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using HarmonyLib;
using Sandbox;
using Sandbox.Engine.Networking;
using Sandbox.Game.GUI;
using Sandbox.Game.Gui;
using Sandbox.Game.Screens.Helpers;
using Sandbox.Graphics.GUI;
using VRage;
using VRage.GameServices;
using VRage.Utils;
using VRageMath;

namespace BlueprintCollections.Patches;

/// <summary>
/// Adds the collection filter controls to the blueprints screen.
/// </summary>
[HarmonyPatch(typeof(MyGuiBlueprintScreen_Reworked), "RecreateControls")]
internal static class BlueprintScreenRecreateControlsPatch
{
    private static void Postfix(MyGuiBlueprintScreen_Reworked __instance)
    {
        try
        {
            CollectionFilterUi.Attach(__instance);
        }
        catch (Exception e)
        {
            MyLog.Default.WriteLine($"{Plugin.Name}: Failed to create the collection filter controls: {e}");
        }
    }
}

/// <summary>
/// Hides the workshop entries that are not part of the selected collection.
/// The game itself only shows/hides list buttons in ApplyFiltering, so hiding here
/// composes cleanly with the search box, group selection and sorting.
/// </summary>
[HarmonyPatch(typeof(MyGuiBlueprintScreen_Reworked), "ApplyFiltering", new Type[0])]
internal static class BlueprintScreenApplyFilteringPatch
{
    private static void Postfix(MyGuiBlueprintScreen_Reworked __instance)
    {
        if (!WorkshopCollectionService.IsFiltering) return;
        if (!BlueprintScreenFilter.IsBlueprintScreen(__instance)) return;

        try
        {
            MyGuiControlList list = BlueprintScreenFilter.BpListRef(__instance);
            if (list == null) return;

            foreach (MyGuiControlBase control in list.Controls)
            {
                if (!(control is MyGuiControlContentButton button)) continue;
                if (!(button.UserData is MyBlueprintItemInfo info)) continue;

                // Shared blueprints (MyBlueprintTypeEnum.SHARED) are deliberately not filtered,
                // they are not part of the subscribed items list the collection filter applies to.
                if (info.Type != MyBlueprintTypeEnum.WORKSHOP || info.Item == null) continue;

                if (!WorkshopCollectionService.IsItemVisible(info.Item.Id))
                {
                    control.Visible = false;
                }
            }

            list.SetScrollBarPage();
        }
        catch (Exception e)
        {
            MyLog.Default.WriteLine($"{Plugin.Name}: Failed to apply the collection filter: {e}");
        }
    }
}

/// <summary>
/// OnSelectItem disables the detail action buttons and only refreshes them once an asynchronous
/// workshop details download finishes. Quickly moving through the list can leave that download
/// behind, which leaves buttons such as "Copy to clipboard" disabled. Refresh them immediately.
/// </summary>
[HarmonyPatch(typeof(MyGuiBlueprintScreen_Reworked), "OnSelectItem")]
internal static class BlueprintScreenSelectItemPatch
{
    private static void Postfix(MyGuiBlueprintScreen_Reworked __instance)
    {
        if (!BlueprintScreenFilter.IsBlueprintScreen(__instance)) return;

        try
        {
            __instance.UpdateDetailKeyEnable();
        }
        catch (Exception e)
        {
            MyLog.Default.WriteLine($"{Plugin.Name}: Failed to update the blueprint action buttons: {e}");
        }
    }
}

/// <summary>
/// Re-reads the collection list when the player uses the screen's refresh button, so new
/// subscriptions show up without restarting the game.
/// </summary>
[HarmonyPatch(typeof(MyGuiBlueprintScreen_Reworked), "OnButton_Refresh")]
internal static class BlueprintScreenRefreshPatch
{
    private static void Postfix(MyGuiBlueprintScreen_Reworked __instance)
    {
        if (!BlueprintScreenFilter.IsBlueprintScreen(__instance)) return;

        try
        {
            WorkshopCollectionService.Refresh();
        }
        catch (Exception e)
        {
            MyLog.Default.WriteLine($"{Plugin.Name}: Failed to refresh the workshop collections: {e}");
        }
    }
}

/// <summary>
/// Temporarily adds the not subscribed blueprint members of the selected collection to the
/// game's subscribed items list while it builds the workshop part of the list. This way the
/// game creates, sorts and displays those items exactly like subscribed ones, and the list
/// never keeps them once the filter is off.
/// </summary>
[HarmonyPatch(typeof(MyGuiBlueprintScreen_Reworked), "GetWorkshopItemsSteam")]
internal static class BlueprintScreenWorkshopItemsPatch
{
    private static void Prefix(MyGuiBlueprintScreen_Reworked __instance, out List<MyWorkshopItem> __state)
    {
        __state = null;

        try
        {
            if (!BlueprintScreenFilter.IsBlueprintScreen(__instance)) return;
            if (!WorkshopCollectionService.IsFiltering) return;
            if (!WorkshopCollectionService.AreMembersLoaded) return;

            WorkshopMember[] members = WorkshopCollectionService.GetBlueprintMembersSnapshot();
            if (members.Length == 0) return;

            using (MyGuiBlueprintScreen_Reworked.SubscribedItemsLock.AcquireExclusiveUsing())
            {
                List<MyWorkshopItem> list = MyGuiBlueprintScreen_Reworked.GetSubscribedItemsList(Content.Blueprint);
                if (list == null) return;

                var knownIds = new HashSet<ulong>();
                foreach (MyWorkshopItem item in list)
                {
                    if (item != null && string.Equals(item.ServiceName, "Steam", StringComparison.OrdinalIgnoreCase))
                    {
                        knownIds.Add(item.Id);
                    }
                }

                foreach (WorkshopMember member in members)
                {
                    MyWorkshopItem item = member.Item;
                    if (item == null || knownIds.Contains(item.Id)) continue;

                    list.Add(item);
                    knownIds.Add(item.Id);
                    (__state ?? (__state = new List<MyWorkshopItem>())).Add(item);
                }
            }
        }
        catch (Exception e)
        {
            MyLog.Default.WriteLine($"{Plugin.Name}: Failed to inject on-demand workshop items: {e}");
        }
    }

    private static void Finalizer(List<MyWorkshopItem> __state)
    {
        if (__state == null) return;

        try
        {
            using (MyGuiBlueprintScreen_Reworked.SubscribedItemsLock.AcquireExclusiveUsing())
            {
                List<MyWorkshopItem> list = MyGuiBlueprintScreen_Reworked.GetSubscribedItemsList(Content.Blueprint);
                if (list == null) return;
                foreach (MyWorkshopItem item in __state) list.Remove(item);
            }
        }
        catch (Exception e)
        {
            MyLog.Default.WriteLine($"{Plugin.Name}: Failed to remove injected workshop items: {e}");
        }
    }
}

/// <summary>
/// Prevents the screen from downloading whole workshop items just to get their thumbnail.
/// Not subscribed collection members have no local folder until the player actually opens
/// them, in which case the thumbnail is read from the downloaded folder.
/// </summary>
[HarmonyPatch(typeof(MyGuiBlueprintScreen_Reworked), "GetImagePath")]
internal static class BlueprintScreenGetImagePathPatch
{
    private static bool Prefix(MyBlueprintItemInfo data, ref string __result)
    {
        if (data == null || data.Type != MyBlueprintTypeEnum.WORKSHOP || data.Item == null) return true;
        if (!WorkshopCollectionService.IsUnsubscribedMember(data.Item.Id)) return true;

        try
        {
            string folder = data.Item.Folder;
            if (!string.IsNullOrEmpty(folder) && Directory.Exists(folder))
            {
                string thumb = Path.Combine(folder, MyBlueprintUtils.THUMB_IMAGE_NAME);
                if (File.Exists(thumb))
                {
                    __result = thumb;
                    return false;
                }

                thumb = Path.Combine(folder, "thumb.jpg");
                if (File.Exists(thumb))
                {
                    __result = thumb;
                    return false;
                }
            }

            // Not installed yet: use the primary Steam Workshop image.
            if (WorkshopCollectionService.TryGetPreviewImage(data.Item.Id, out string preview))
            {
                __result = preview;
                return false;
            }
        }
        catch (Exception e)
        {
            MyLog.Default.WriteLine($"{Plugin.Name}: Failed to resolve an on-demand blueprint thumbnail: {e}");
        }

        __result = string.Empty;
        return false;
    }
}

/// <summary>
/// The game's prefab loader resolves workshop metadata (title, description, DLCs) from the
/// subscribed items list, so a not subscribed member is added there for the duration of the
/// call. Without this, on-demand blueprints would download but never load.
/// </summary>
[HarmonyPatch(typeof(MyBlueprintUtils), "LoadWorkshopPrefab")]
internal static class LoadWorkshopPrefabPatch
{
    private static void Prefix(ulong? publishedItemId, string publishedServiceName, out MyWorkshopItem __state)
    {
        __state = null;

        try
        {
            if (!publishedItemId.HasValue) return;
            if (!string.Equals(publishedServiceName, "Steam", StringComparison.OrdinalIgnoreCase)) return;
            if (!WorkshopCollectionService.TryGetMemberItem(publishedItemId.Value, out MyWorkshopItem item) || item == null) return;

            using (MyGuiBlueprintScreen_Reworked.SubscribedItemsLock.AcquireExclusiveUsing())
            {
                List<MyWorkshopItem> list = MyGuiBlueprintScreen_Reworked.GetSubscribedItemsList(Content.Blueprint);
                if (list == null) return;

                foreach (MyWorkshopItem existing in list)
                {
                    if (existing != null && existing.Id == item.Id && existing.ServiceName == item.ServiceName) return;
                }

                list.Add(item);
                __state = item;
            }
        }
        catch (Exception e)
        {
            MyLog.Default.WriteLine($"{Plugin.Name}: Failed to expose an on-demand workshop item for loading: {e}");
        }
    }

    private static void Finalizer(MyWorkshopItem __state)
    {
        if (__state == null) return;

        try
        {
            using (MyGuiBlueprintScreen_Reworked.SubscribedItemsLock.AcquireExclusiveUsing())
            {
                List<MyWorkshopItem> list = MyGuiBlueprintScreen_Reworked.GetSubscribedItemsList(Content.Blueprint);
                list?.Remove(__state);
            }
        }
        catch (Exception e)
        {
            MyLog.Default.WriteLine($"{Plugin.Name}: Failed to clean up an on-demand workshop item: {e}");
        }
    }
}

/// <summary>
/// Tells the player when a not subscribed collection member cannot be downloaded, for example
/// because the author removed it or made it private.
/// </summary>
[HarmonyPatch(typeof(MyGuiBlueprintScreen_Reworked), "DownloadBlueprintFromSteam")]
internal static class DownloadBlueprintFromSteamPatch
{
    private static void Postfix(MyWorkshopItem item)
    {
        if (item == null) return;
        if (!WorkshopCollectionService.IsUnsubscribedMember(item.Id)) return;

        try
        {
            item.UpdateState();
            if (item.State.HasFlag(MyWorkshopItemState.Installed)) return;

            string title = item.Title;
            MySandboxGame.Static.Invoke(delegate
            {
                MyGuiSandbox.AddScreen(MyGuiSandbox.CreateMessageBox(
                    MyMessageBoxStyleEnum.Error,
                    buttonType: MyMessageBoxButtonsType.OK,
                    messageCaption: new StringBuilder("Workshop download failed"),
                    messageText: new StringBuilder(
                        $"Could not download \"{title}\" from the Steam Workshop.\n\n" +
                        "The item may have been removed, made private, or Steam may be offline."),
                    size: new Vector2(0.65f, 0.35f)));
            }, "BlueprintCollectionsDownloadFailed");
        }
        catch (Exception e)
        {
            MyLog.Default.WriteLine($"{Plugin.Name}: Failed to report an on-demand download failure: {e}");
        }
    }
}

/// <summary>
/// The game disables the blueprint list before a background thumbnail download but never
/// re-enables it when the download finishes, which leaves the list unclickable. Restore it.
/// </summary>
[HarmonyPatch(typeof(MyGuiBlueprintScreen_Reworked), "OnBlueprintDownloadedThumbnail")]
internal static class BlueprintScreenThumbnailDownloadedPatch
{
    private static void Postfix(MyGuiBlueprintScreen_Reworked __instance)
    {
        try
        {
            MyGuiControlList list = BlueprintScreenFilter.BpListRef(__instance);
            if (list != null && !list.Enabled) list.Enabled = true;
        }
        catch (Exception e)
        {
            MyLog.Default.WriteLine($"{Plugin.Name}: Failed to re-enable the blueprint list: {e}");
        }
    }
}

/// <summary>
/// Shared helpers to drive the screen's own filtering and selection logic.
/// </summary>
internal static class BlueprintScreenFilter
{
    private static readonly MethodInfo ApplyFilteringMethod =
        AccessTools.Method(typeof(MyGuiBlueprintScreen_Reworked), "ApplyFiltering", Type.EmptyTypes);

    private static readonly MethodInfo TrySelectFirstMethod =
        AccessTools.Method(typeof(MyGuiBlueprintScreen_Reworked), "TrySelectFirstBlueprint");

    private static readonly MethodInfo GetWorkshopItemsSteamMethod =
        AccessTools.Method(typeof(MyGuiBlueprintScreen_Reworked), "GetWorkshopItemsSteam");

    private static readonly MethodInfo SetGroupSelectionMethod =
        AccessTools.Method(typeof(MyGuiBlueprintScreen_Reworked), "SetGroupSelection", new[] { typeof(MyBlueprintTypeEnum) });

    private static readonly AccessTools.FieldRef<MyGuiBlueprintScreen_Reworked, int> WorkshopIndexRef =
        AccessTools.FieldRefAccess<MyGuiBlueprintScreen_Reworked, int>("m_workshopIndex");

    public static readonly AccessTools.FieldRef<MyGuiBlueprintScreen_Reworked, MyGuiControlSearchBox> SearchBoxRef =
        AccessTools.FieldRefAccess<MyGuiBlueprintScreen_Reworked, MyGuiControlSearchBox>("m_searchBox");

    public static int GetWorkshopIndex(MyGuiBlueprintScreen_Reworked screen)
    {
        return screen == null ? -1 : WorkshopIndexRef(screen);
    }

    public static void SetWorkshopIndex(MyGuiBlueprintScreen_Reworked screen, int index)
    {
        if (screen != null) WorkshopIndexRef(screen) = index;
    }

    public static readonly AccessTools.FieldRef<MyGuiBlueprintScreen_Reworked, MyGuiControlList> BpListRef =
        AccessTools.FieldRefAccess<MyGuiBlueprintScreen_Reworked, MyGuiControlList>("m_BPList");

    private static readonly AccessTools.FieldRef<MyGuiBlueprintScreen_Reworked, Content> ContentRef =
        AccessTools.FieldRefAccess<MyGuiBlueprintScreen_Reworked, Content>("m_content");

    /// <summary>True for the blueprints screen, false for the script screen using the same class.</summary>
    public static bool IsBlueprintScreen(MyGuiBlueprintScreen_Reworked screen)
    {
        return screen != null && ContentRef(screen) == Content.Blueprint;
    }

    /// <summary>True when the list already contains at least one workshop button.</summary>
    public static bool HasWorkshopItems(MyGuiBlueprintScreen_Reworked screen)
    {
        MyGuiControlList list = BpListRef(screen);
        if (list == null) return false;

        foreach (MyGuiControlBase control in list.Controls)
        {
            if (!(control is MyGuiControlContentButton button)) continue;
            if (button.UserData is MyBlueprintItemInfo info && info.Type == MyBlueprintTypeEnum.WORKSHOP)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Adds the workshop part of the list when the game skipped it because a background task
    /// was running. Uses the cached subscribed items plus the injected collection members.
    /// </summary>
    public static void AddWorkshopItems(MyGuiBlueprintScreen_Reworked screen)
    {
        try
        {
            GetWorkshopItemsSteamMethod?.Invoke(screen, null);
        }
        catch (Exception e)
        {
            MyLog.Default.WriteLine($"{Plugin.Name}: Failed to add workshop items after a task: {e}");
        }
    }

    /// <summary>
    /// Switches the screen to the Steam workshop group. Selecting a collection from Local or
    /// Cloud should show workshop items; the All view is kept as-is and filtered in place.
    /// </summary>
    public static void SelectSteamWorkshopGroup(MyGuiBlueprintScreen_Reworked screen)
    {
        if (screen == null) return;

        try
        {
            int steamIndex = MyGameService.GetUGCIndex("Steam");
            if (steamIndex < 0) return;

            SetWorkshopIndex(screen, steamIndex);
            SetGroupSelectionMethod?.Invoke(screen, new object[] { MyBlueprintTypeEnum.WORKSHOP });
        }
        catch (Exception e)
        {
            MyLog.Default.WriteLine($"{Plugin.Name}: Failed to switch to the Steam workshop group: {e}");
        }
    }

    public static void Apply(MyGuiBlueprintScreen_Reworked screen)
    {
        if (screen == null) return;

        try
        {
            ApplyFilteringMethod?.Invoke(screen, null);
        }
        catch (Exception e)
        {
            MyLog.Default.WriteLine($"{Plugin.Name}: Failed to refresh the blueprint list: {e}");
        }

        try
        {
            TrySelectFirstMethod?.Invoke(screen, null);
        }
        catch (Exception e)
        {
            MyLog.Default.WriteLine($"{Plugin.Name}: Failed to select a blueprint after filtering: {e}");
        }
    }
}
