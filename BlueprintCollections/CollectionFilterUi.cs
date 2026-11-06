using System;
using System.Collections.Generic;
using BlueprintCollections.Patches;
using Sandbox.Engine.Networking;
using Sandbox.Game.Gui;
using Sandbox.Game.Screens.Helpers;
using Sandbox.Graphics.GUI;
using VRage.Utils;
using VRageMath;
using VRageRender;
using VRageRender.Messages;

namespace BlueprintCollections;

/// <summary>
/// Adds the workshop collection filter combobox to the blueprints screen and keeps it in
/// sync with <see cref="WorkshopCollectionService"/>.
/// </summary>
internal static class CollectionFilterUi
{
    private const string ComboName = "BlueprintCollectionsFilter";
    private const float SearchShift = 0.045f;

    private sealed class Attachment
    {
        public WeakReference<MyGuiBlueprintScreen_Reworked> Screen;
        public WeakReference<MyGuiControlCombobox> Combo;
        public Vector2 SearchBasePosition;
        public float ListBaseY;
        public float ListBaseHeight;
        public bool PendingPopulate;
        public int LastFilterVersion;
    }

    private static readonly List<Attachment> Attachments = new List<Attachment>();

    /// <summary>Called from the RecreateControls postfix of the blueprint screen.</summary>
    public static void Attach(MyGuiBlueprintScreen_Reworked screen)
    {
        if (screen == null) return;
        if (!BlueprintScreenFilter.IsBlueprintScreen(screen)) return;

        RemoveAttachmentFor(screen);
        PruneDead();

        MyGuiControlSearchBox searchBox = BlueprintScreenFilter.SearchBoxRef(screen);
        Vector2 searchBase = searchBox != null
            ? searchBox.Position
            : new Vector2(-0.418f, -0.24f);
        float comboWidth = searchBox != null ? searchBox.Size.X : 0.203f;

        MyGuiControlList list = BlueprintScreenFilter.BpListRef(screen);
        float listBaseY = list != null ? list.Position.Y : searchBase.Y + 0.032f;
        float listBaseHeight = list != null ? list.Size.Y : 0.55f;

        var combo = new MyGuiControlCombobox(
            position: searchBase,
            size: new Vector2(comboWidth, 0.03f),
            openAreaItemsCount: 12,
            toolTip: "Show only the workshop blueprints that belong to the selected collection.",
            originAlign: MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_CENTER)
        {
            Name = ComboName
        };

        screen.Controls.Add(combo);

        Populate(combo);

        combo.ItemSelected += delegate
        {
            long key = combo.GetSelectedKey();
            ulong collectionId = key > 0 ? (ulong)key : 0UL;
            WorkshopCollectionService.Select(collectionId);

            if (collectionId != 0 && !IsMixedGroupSelected(screen))
            {
                // A collection only contains workshop items, so switch the screen from
                // Local/Cloud to the Steam workshop group. The All view keeps its local
                // and cloud items and just filters the workshop part.
                BlueprintScreenFilter.SelectSteamWorkshopGroup(screen);
            }
            else
            {
                BlueprintScreenFilter.Apply(screen);
            }
        };

        var attachment = new Attachment
        {
            Screen = new WeakReference<MyGuiBlueprintScreen_Reworked>(screen),
            Combo = new WeakReference<MyGuiControlCombobox>(combo),
            SearchBasePosition = searchBase,
            ListBaseY = listBaseY,
            ListBaseHeight = listBaseHeight,

            // Force one rebuild after the screen is created. When the screen is reopened the
            // game can skip the workshop part because a background task from the previous
            // visit is still running, and the filter version may not have changed since then.
            LastFilterVersion = -1
        };
        Attachments.Add(attachment);

        WorkshopCollectionService.Changed -= OnServiceChanged;
        WorkshopCollectionService.Changed += OnServiceChanged;

        WorkshopCollectionService.RequestData();

        if (WorkshopCollectionService.IsFiltering && !IsMixedGroupSelected(screen))
        {
            // The saved collection filter should show workshop items, even if the screen
            // remembered Local/Cloud from an earlier visit. The All view is kept as-is,
            // because it already shows the filtered workshop part.
            BlueprintScreenFilter.SelectSteamWorkshopGroup(screen);
        }

        BlueprintScreenFilter.Apply(screen);

        ApplyPickerLayout(screen, attachment, ShouldShowPicker(screen));
    }

    /// <summary>
    /// The picker occupies the search box row in the Steam Workshop and All views; the search
    /// box and the list move down one row. For other groups the picker is hidden and everything
    /// moves back up.
    /// </summary>
    private static void ApplyPickerLayout(
        MyGuiBlueprintScreen_Reworked screen,
        Attachment attachment,
        bool showPicker)
    {
        if (screen == null || attachment == null) return;

        MyGuiControlSearchBox searchBox = BlueprintScreenFilter.SearchBoxRef(screen);
        if (searchBox != null)
        {
            float y = attachment.SearchBasePosition.Y + (showPicker ? SearchShift : 0f);
            if (Math.Abs(searchBox.Position.Y - y) > 0.0001f)
            {
                searchBox.Position = new Vector2(attachment.SearchBasePosition.X, y);
            }
        }

        MyGuiControlList list = BlueprintScreenFilter.BpListRef(screen);
        if (list != null)
        {
            float y = attachment.ListBaseY + (showPicker ? SearchShift : 0f);
            float height = Math.Max(0.1f, attachment.ListBaseHeight - (showPicker ? SearchShift : 0f));

            if (Math.Abs(list.Position.Y - y) > 0.0001f || Math.Abs(list.Size.Y - height) > 0.0001f)
            {
                list.Position = new Vector2(list.Position.X, y);
                list.Size = new Vector2(list.Size.X, height);
                list.SetScrollBarPage();
            }
        }

        if (attachment.Combo.TryGetTarget(out MyGuiControlCombobox combo))
        {
            if (!showPicker && combo.IsOpen) combo.IsOpen = false;
            if (combo.Visible != showPicker) combo.Visible = showPicker;
        }
    }

    /// <summary>
    /// The picker is shown for the Steam workshop group and for the All group, where the
    /// collection filter already applies to the workshop part of the list.
    /// </summary>
    private static bool ShouldShowPicker(MyGuiBlueprintScreen_Reworked screen)
    {
        if (screen == null) return false;

        try
        {
            MyBlueprintTypeEnum type = screen.GetSelectedBlueprintType();
            if (type == MyBlueprintTypeEnum.MIXED) return true;
            if (type != MyBlueprintTypeEnum.WORKSHOP) return false;

            int steamIndex = MyGameService.GetUGCIndex("Steam");
            return steamIndex >= 0 && BlueprintScreenFilter.GetWorkshopIndex(screen) == steamIndex;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool IsMixedGroupSelected(MyGuiBlueprintScreen_Reworked screen)
    {
        if (screen == null) return false;

        try
        {
            return screen.GetSelectedBlueprintType() == MyBlueprintTypeEnum.MIXED;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Whether at least one live blueprint screen carries the filter controls.</summary>
    public static bool HasLiveScreens
    {
        get
        {
            PruneDead();
            return Attachments.Count > 0;
        }
    }

    /// <summary>
    /// Called every frame. Applies deferred work: repopulating the combobox once the player
    /// closes the dropdown, and rebuilding the blueprint list when the filtered item set changes
    /// (selection changed, members loaded, collection refreshed).
    /// </summary>
    public static void Update()
    {
        if (Attachments.Count == 0) return;
        PruneDead();

        for (int i = Attachments.Count - 1; i >= 0; i--)
        {
            Attachment attachment = Attachments[i];
            if (!attachment.Screen.TryGetTarget(out MyGuiBlueprintScreen_Reworked screen)) continue;
            if (!attachment.Combo.TryGetTarget(out MyGuiControlCombobox combo)) continue;

            bool showPicker = ShouldShowPicker(screen);
            ApplyPickerLayout(screen, attachment, showPicker);

            if (attachment.PendingPopulate && !combo.IsOpen)
            {
                attachment.PendingPopulate = false;
                Populate(combo);
            }

            if (attachment.LastFilterVersion == WorkshopCollectionService.FilterVersion) continue;
            if (!screen.IsOpened) continue;

            try
            {
                // Rebuilds the workshop part of the list, including the on-demand members of
                // the selected collection, and re-applies the filter afterwards.
                screen.RefreshBlueprintList();

                // The game skips building the workshop list while one of its background
                // tasks is running (downloads, prefab loads). Without this fallback the
                // list stays empty until the task finishes, and if no further rebuild is
                // triggered it never recovers after changing the collection.
                if (!BlueprintScreenFilter.HasWorkshopItems(screen))
                {
                    BlueprintScreenFilter.AddWorkshopItems(screen);
                    BlueprintScreenFilter.Apply(screen);
                    MyLog.Default.WriteLine(
                        $"{Plugin.Name}: The list rebuild left no workshop items; " +
                        "added them directly.");
                }

                attachment.LastFilterVersion = WorkshopCollectionService.FilterVersion;
            }
            catch (Exception e)
            {
                MyLog.Default.WriteLine($"{Plugin.Name}: Failed to rebuild the blueprint list: {e}");
            }
        }
    }

    /// <summary>
    /// Updates the preview of an already built blueprint button when its Steam Workshop image
    /// finishes downloading. Buttons built later get the image through GetImagePath.
    /// </summary>
    public static void RefreshThumbnail(ulong itemId, string imagePath)
    {
        if (string.IsNullOrEmpty(imagePath) || Attachments.Count == 0) return;
        PruneDead();

        for (int i = Attachments.Count - 1; i >= 0; i--)
        {
            Attachment attachment = Attachments[i];
            if (!attachment.Screen.TryGetTarget(out MyGuiBlueprintScreen_Reworked screen)) continue;
            if (!screen.IsOpened) continue;

            MyGuiControlList list = BlueprintScreenFilter.BpListRef(screen);
            if (list == null) continue;

            foreach (MyGuiControlBase control in list.Controls)
            {
                if (!(control is MyGuiControlContentButton button)) continue;
                if (!(button.UserData is MyBlueprintItemInfo info)) continue;
                if (info.Item == null || info.Item.Id != itemId) continue;

                try
                {
                    MyRenderProxy.PreloadTextures(
                        new[] { imagePath }, TextureType.GUIWithoutPremultiplyAlpha);
                    button.CreatePreview(
                        imagePath, screen.GetThumbnailVisibility(), preserveIndexOnRecreation: true);
                }
                catch (Exception e)
                {
                    MyLog.Default.WriteLine($"{Plugin.Name}: Failed to update a workshop preview image: {e}");
                }
            }
        }
    }

    private static void OnServiceChanged()
    {
        PruneDead();

        for (int i = Attachments.Count - 1; i >= 0; i--)
        {
            Attachment attachment = Attachments[i];
            if (!attachment.Screen.TryGetTarget(out _))
            {
                Attachments.RemoveAt(i);
                continue;
            }
            if (!attachment.Combo.TryGetTarget(out MyGuiControlCombobox combo))
            {
                Attachments.RemoveAt(i);
                continue;
            }

            // Never rebuild the item list while the player has the dropdown open.
            if (combo.IsOpen)
            {
                attachment.PendingPopulate = true;
            }
            else
            {
                Populate(combo);
            }
        }
    }

    private static void Populate(MyGuiControlCombobox combo)
    {
        ulong selectedId = WorkshopCollectionService.SelectedId;

        combo.ClearItems();
        combo.AddItem(0L, "All Subscribed Items", 0, "Show every subscribed workshop blueprint, without a collection filter.", sort: false);

        int order = 1;
        foreach (WorkshopCollection collection in WorkshopCollectionService.Collections)
        {
            combo.AddItem(unchecked((long)collection.Id), collection.DisplayName, order++, collection.ToolTip, sort: false);
        }

        // Keep the selected collection selectable even if it is not in the list yet
        // (still loading) or no longer owned/subscribed/favorited (stale config).
        long selectedKey = unchecked((long)selectedId);
        if (selectedId != 0 && combo.TryGetItemByKey(selectedKey) == null)
        {
            string title = WorkshopCollectionService.SelectedTitle;
            if (string.IsNullOrEmpty(title)) title = "Collection " + selectedId;
            combo.AddItem(
                selectedKey,
                WorkshopCollectionService.IsLoading || !WorkshopCollectionService.IsLoaded
                    ? title + " (loading...)"
                    : title + " (not available)",
                order,
                "The selected collection is not currently owned, subscribed to, or favorited by this Steam account.",
                sort: false);
        }

        // The service is the source of truth for the active filter.
        combo.SelectItemByKey(selectedKey, sendEvent: false);
        if (combo.GetSelectedKey() < 0) combo.SelectItemByKey(0L, sendEvent: false);
    }

    private static void RemoveAttachmentFor(MyGuiBlueprintScreen_Reworked screen)
    {
        for (int i = Attachments.Count - 1; i >= 0; i--)
        {
            if (!Attachments[i].Screen.TryGetTarget(out MyGuiBlueprintScreen_Reworked existing)) continue;
            if (ReferenceEquals(existing, screen)) Attachments.RemoveAt(i);
        }
    }

    private static void PruneDead()
    {
        for (int i = Attachments.Count - 1; i >= 0; i--)
        {
            if (!Attachments[i].Screen.TryGetTarget(out _) ||
                !Attachments[i].Combo.TryGetTarget(out _))
            {
                Attachments.RemoveAt(i);
            }
        }
    }
}
