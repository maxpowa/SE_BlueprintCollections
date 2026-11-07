using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Threading.Tasks;
using Sandbox;
using Sandbox.Engine.Networking;
using Steamworks;
using VRage.FileSystem;
using VRage.GameServices;
using VRage.Utils;

namespace BlueprintCollections;

/// <summary>
/// A Steam Workshop collection that can be used as a filter.
/// </summary>
public sealed class WorkshopCollection
{
    public ulong Id { get; }
    public string Title { get; internal set; }
    public bool Owned { get; internal set; }
    public bool Subscribed { get; internal set; }
    public bool Favorited { get; internal set; }

    public WorkshopCollection(ulong id, string title)
    {
        Id = id;
        Title = title ?? string.Empty;
    }

    public string DisplayName
    {
        get
        {
            if (Owned && Subscribed && Favorited) return $"{Title} (owned, subscribed, favorited)";
            if (Owned && Subscribed) return $"{Title} (owned, subscribed)";
            if (Owned && Favorited) return $"{Title} (owned, favorited)";
            if (Subscribed && Favorited) return $"{Title} (subscribed, favorited)";
            if (Owned) return $"{Title} (owned)";
            if (Subscribed) return $"{Title} (subscribed)";
            if (Favorited) return $"{Title} (favorited)";
            return Title;
        }
    }

    public string ToolTip
    {
        get
        {
            var sources = new List<string>();
            if (Owned) sources.Add("owned by you");
            if (Subscribed) sources.Add("subscribed");
            if (Favorited) sources.Add("favorited");

            string source = sources.Count > 0 ? string.Join(", ", sources) : "not associated";
            return $"{Title}\nCollection {Id}, {source}.";
        }
    }
}

/// <summary>
/// A blueprint that belongs to the selected collection. Members that the player is not
/// subscribed to get a detached <see cref="MyWorkshopItem"/> so the game's own download,
/// extraction and preview code can handle them on demand.
/// </summary>
public sealed class WorkshopMember
{
    public ulong Id { get; }
    public string Title { get; internal set; }
    public string Description { get; internal set; }
    public ulong OwnerId { get; internal set; }
    public ulong Size { get; internal set; }
    public DateTime TimeCreated { get; internal set; }
    public DateTime TimeUpdated { get; internal set; }
    public bool Subscribed { get; internal set; }
    public MyWorkshopItem Item { get; internal set; }

    /// <summary>Steam UGC handle of the primary preview image.</summary>
    internal ulong PreviewHandle { get; set; }

    /// <summary>Steam CDN URL of the primary preview image, if available.</summary>
    internal string PreviewUrl { get; set; } = string.Empty;

    internal WorkshopMember(ulong id, string title)
    {
        Id = id;
        Title = title ?? string.Empty;
    }
}

/// <summary>
/// Loads the Steam Workshop collections owned by, subscribed to, or favorited by the local
/// player, the contents of the selected collection, and keeps track of the collection used
/// as a filter.
///
/// All Steam callbacks are delivered on the game's main thread by
/// <c>SteamAPI.RunCallbacks</c>, so every member of this class is main-thread only.
/// </summary>
public static class WorkshopCollectionService
{
    private const int ResultsPerPage = 50;      // kNumUGCResultsPerPage
    private const int DetailsBatchSize = 100;   // max IDs accepted by CreateQueryUGCDetailsRequest
    private const string SteamServiceName = "Steam";
    private const string IgnoreMarker = "[WCF-IGNORE]";

    private static readonly List<WorkshopCollection> CollectionsInternal = new List<WorkshopCollection>();
    private static readonly HashSet<ulong> SelectedMembers = new HashSet<ulong>();

    private static readonly Dictionary<ulong, WorkshopMember> Members = new Dictionary<ulong, WorkshopMember>();
    private static readonly List<WorkshopMember> BlueprintMembersInternal = new List<WorkshopMember>();
    private static readonly HashSet<ulong> UnsubscribedMemberIds = new HashSet<ulong>();

    /// <summary>Guards the member data, which is read from the game's prefab loader worker thread.</summary>
    private static readonly object MemberLock = new object();

    /// <summary>Preview downloads waiting for a free slot (main thread only).</summary>
    private static readonly Queue<WorkshopMember> PreviewQueue = new Queue<WorkshopMember>();
    private static readonly HashSet<ulong> PreviewQueued = new HashSet<ulong>();
    private static readonly HashSet<ulong> PreviewUrlFailed = new HashSet<ulong>();
    private const int MaxConcurrentPreviewDownloads = 4;
    private static int m_activePreviewDownloads;

    private static readonly HttpClient HttpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };

    private static string PreviewCacheDirectory =>
        Path.Combine(MyFileSystem.UserDataPath, "Storage", Plugin.Name, "thumbs");

    private static bool m_initialized;
    private static bool m_loading;
    private static bool m_loaded;
    private static bool m_collectionLoadFailed;
    private static int m_initialQueriesPending;
    private static int m_loadGeneration;

    private static ulong m_selectedId;
    private static bool m_membersLoading;
    private static bool m_membersLoaded;
    private static ulong m_membersRequestId;

    private static int m_filterVersion;
    private static DateTime m_nextRetryUtc = DateTime.MinValue;
    private static DateTime m_loadStartedUtc;
    private static DateTime m_membersLoadStartedUtc;

    /// <summary>Raised on the main thread whenever the collection list or selection changes.</summary>
    public static event Action Changed;

    public static IReadOnlyList<WorkshopCollection> Collections => CollectionsInternal;

    /// <summary>Incremented when the set of items to show changes and the screen must rebuild.</summary>
    public static int FilterVersion => m_filterVersion;

    public static bool IsLoaded => m_loaded;
    public static bool IsLoading => m_loading;

    /// <summary>Collection currently used as a filter, 0 means no filtering.</summary>
    public static ulong SelectedId => m_selectedId;

    public static bool IsFiltering => m_selectedId != 0;

    public static string SelectedTitle
    {
        get
        {
            if (m_selectedId == 0) return string.Empty;
            WorkshopCollection collection = Find(m_selectedId);
            return collection?.Title ?? Config.Current.SelectedCollectionTitle ?? string.Empty;
        }
    }

    public static void Initialize()
    {
        if (m_initialized) return;
        m_initialized = true;
        m_selectedId = Config.Current.SelectedCollectionId;
        EnforcePreviewCacheLimit();
    }

    /// <summary>Called when a blueprint screen is opened. Kicks off loading if needed.</summary>
    public static void RequestData()
    {
        EnsureCollectionsLoaded();
        EnsureSelectedMembersLoaded();
    }

    /// <summary>
    /// Periodically retries loading data that could not be fetched yet, for example
    /// because Steam was not signed in when the screen was opened.
    /// </summary>
    public static void TryRetry()
    {
        DateTime now = DateTime.UtcNow;
        if (now < m_nextRetryUtc) return;

        // Never stay stuck on a query whose callback never arrived.
        if (m_loading && (now - m_loadStartedUtc).TotalSeconds > 45)
        {
            m_loading = false;
            m_initialQueriesPending = 0;
        }
        if (m_membersLoading && (now - m_membersLoadStartedUtc).TotalSeconds > 45)
        {
            m_membersLoading = false;
        }

        if (m_loaded && (m_selectedId == 0 || m_membersLoaded)) return;
        if (!MyGameService.IsActive) return;

        EnsureCollectionsLoaded();
        EnsureSelectedMembersLoaded();
    }

    /// <summary>
    /// Discards the cached collection list and loads it again. Called when the player presses
    /// the refresh button of the blueprints screen, so newly subscribed collections show up
    /// without restarting the game.
    /// </summary>
    public static void Refresh()
    {
        if (m_loading) return;

        CollectionsInternal.Clear();
        m_loaded = false;
        m_collectionLoadFailed = false;

        EnsureCollectionsLoaded();

        if (m_selectedId != 0)
        {
            ResetMemberData();
            m_filterVersion++;
            m_membersLoading = false;
            m_membersRequestId = 0;
            QueryCollectionMembers(m_selectedId);
        }

        RaiseChanged();
    }

    /// <summary>
    /// Starts loading owned, subscribed and favorited collections. Does nothing when already
    /// loaded, loading, or when the game service is not available.
    /// </summary>
    public static void EnsureCollectionsLoaded()
    {
        if (m_loaded || m_loading) return;
        if (!MyGameService.IsActive) return;

        m_loading = true;
        m_initialQueriesPending = 3;
        m_collectionLoadFailed = false;
        m_loadStartedUtc = DateTime.UtcNow;

        int generation = ++m_loadGeneration;
        QueryUserCollections(EUserUGCList.k_EUserUGCList_Published, CollectionSource.Owned, page: 1, generation: generation);
        QueryUserCollections(EUserUGCList.k_EUserUGCList_Subscribed, CollectionSource.Subscribed, page: 1, generation: generation);
        QueryUserCollections(EUserUGCList.k_EUserUGCList_Favorited, CollectionSource.Favorited, page: 1, generation: generation);
    }

    /// <summary>Ensures the children of the selected collection are loaded.</summary>
    public static void EnsureSelectedMembersLoaded()
    {
        if (m_selectedId == 0) return;
        if (m_membersLoaded || m_membersLoading) return;
        if (!MyGameService.IsActive) return;

        QueryCollectionMembers(m_selectedId);
    }

    public static void Select(ulong collectionId)
    {
        if (m_selectedId == collectionId) return;

        // Resolve the title before changing the selection; the old cached title belongs
        // to the previously selected collection and must not be carried over.
        WorkshopCollection collection = collectionId == 0 ? null : Find(collectionId);

        m_selectedId = collectionId;
        m_membersLoading = false;
        m_membersRequestId = 0;
        SelectedMembers.Clear();
        ResetMemberData();
        m_filterVersion++;

        Config.Current.SelectedCollectionId = collectionId;
        Config.Current.SelectedCollectionTitle = collection?.Title ?? string.Empty;
        Config.Save();

        RaiseChanged();

        if (collectionId != 0)
        {
            EnsureSelectedMembersLoaded();
        }
    }

    /// <summary>
    /// Collections with this marker in their title are hidden from the picker and are not
    /// used as a filter.
    /// </summary>
    public static bool IsIgnoredTitle(string title)
    {
        return !string.IsNullOrEmpty(title) &&
               title.IndexOf(IgnoreMarker, StringComparison.OrdinalIgnoreCase) >= 0;
    }

    /// <summary>Whether the collection is marked to be hidden from the picker.</summary>
    public static bool IsIgnoredCollection(ulong collectionId)
    {
        if (collectionId == 0) return false;

        WorkshopCollection collection = Find(collectionId);
        if (collection != null) return IsIgnoredTitle(collection.Title);

        // Ignored collections are not kept in the list, so the cached title is the only
        // remaining source for the currently selected one.
        return collectionId == m_selectedId &&
               IsIgnoredTitle(Config.Current.SelectedCollectionTitle);
    }

    /// <summary>
    /// Turns the filter off when the selected collection has been marked as ignored. Returns
    /// true when the selection was cleared.
    /// </summary>
    public static bool ClearIgnoredSelectionIfNeeded()
    {
        if (m_selectedId == 0 || !IsIgnoredCollection(m_selectedId)) return false;

        Select(0);
        return true;
    }

    /// <summary>
    /// Whether a workshop item should remain visible with the current filter.
    /// </summary>
    public static bool IsItemVisible(ulong itemId)
    {
        if (m_selectedId == 0) return true;
        if (!m_membersLoaded) return false;
        return SelectedMembers.Contains(itemId);
    }

    /// <summary>Whether the selected collection's member data is fully loaded.</summary>
    public static bool AreMembersLoaded
    {
        get
        {
            lock (MemberLock)
            {
                return m_membersLoaded;
            }
        }
    }

    /// <summary>Whether the item is a blueprint member the player is not subscribed to.</summary>
    public static bool IsUnsubscribedMember(ulong itemId)
    {
        lock (MemberLock)
        {
            return m_membersLoaded && UnsubscribedMemberIds.Contains(itemId);
        }
    }

    /// <summary>
    /// Gets the detached workshop item backing a collection member, so the game's own
    /// workshop code can download and load it.
    /// </summary>
    public static bool TryGetMemberItem(ulong itemId, out MyWorkshopItem item)
    {
        lock (MemberLock)
        {
            item = null;
            if (!m_membersLoaded) return false;
            if (!Members.TryGetValue(itemId, out WorkshopMember member)) return false;
            item = member.Item;
            return item != null;
        }
    }

    /// <summary>Snapshot of the blueprint members, safe to iterate from any thread.</summary>
    public static WorkshopMember[] GetBlueprintMembersSnapshot()
    {
        lock (MemberLock)
        {
            return BlueprintMembersInternal.ToArray();
        }
    }

    /// <summary>
    /// Gets the local Steam preview image of a member, if it has already been downloaded.
    /// </summary>
    public static bool TryGetPreviewImage(ulong itemId, out string path)
    {
        lock (MemberLock)
        {
            path = null;
            if (!Members.TryGetValue(itemId, out WorkshopMember member)) return false;
            return TryGetCachedPreviewPath(member.PreviewHandle, out path);
        }
    }

    // Size of the preview image control in MyGuiBlueprintScreen_Reworked (242x128 GUI units).
    private const int PreviewImageWidth = 242;
    private const int PreviewImageHeight = 128;

    private static string GetPreviewPath(ulong previewHandle)
    {
        return Path.Combine(
            PreviewCacheDirectory,
            previewHandle + "_" + PreviewImageWidth + "x" + PreviewImageHeight + ".jpg");
    }

    private static string GetFallbackPreviewPath(ulong previewHandle)
    {
        return Path.Combine(PreviewCacheDirectory, previewHandle + ".png");
    }

    /// <summary>Removes images cached by older builds with a different size or letterboxing.</summary>
    private static void DeleteLegacyPreviewFiles(ulong previewHandle)
    {
        string basePath = Path.Combine(PreviewCacheDirectory, previewHandle.ToString());
        DeleteFileIfExists(basePath + "_256.jpg");
        DeleteFileIfExists(basePath + "_256ar.jpg");
    }

    private static bool TryGetCachedPreviewPath(ulong previewHandle, out string path)
    {
        path = null;
        if (previewHandle == 0 || previewHandle == (ulong)UGCHandle_t.Invalid) return false;

        string resized = GetPreviewPath(previewHandle);
        if (IsUsableFile(resized))
        {
            path = resized;
            return true;
        }

        string fallback = GetFallbackPreviewPath(previewHandle);
        if (IsUsableFile(fallback))
        {
            path = fallback;
            return true;
        }

        return false;
    }

    private static bool IsUsableFile(string path)
    {
        try
        {
            return File.Exists(path) && new FileInfo(path).Length > 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static void DeleteFileIfExists(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception)
        {
            // Ignore; the cache limit will retry later.
        }
    }

    private static WorkshopCollection Find(ulong id)
    {
        foreach (WorkshopCollection collection in CollectionsInternal)
        {
            if (collection.Id == id) return collection;
        }
        return null;
    }

    private static void RaiseChanged()
    {
        try
        {
            Changed?.Invoke();
        }
        catch (Exception e)
        {
            MyLog.Default.WriteLine($"{Plugin.Name}: Collection change handler failed: {e}");
        }
    }

    private static void ResetMemberData()
    {
        lock (MemberLock)
        {
            m_membersLoaded = false;
            Members.Clear();
            BlueprintMembersInternal.Clear();
            UnsubscribedMemberIds.Clear();
        }
        SelectedMembers.Clear();
    }

    private static void ResetMemberDetails()
    {
        lock (MemberLock)
        {
            m_membersLoaded = false;
            Members.Clear();
            BlueprintMembersInternal.Clear();
            UnsubscribedMemberIds.Clear();
        }
    }

    #region Collection list query

    private enum CollectionSource
    {
        Owned,
        Subscribed,
        Favorited
    }

    /// <summary>
    /// Queries one page of the collections the local player has published, is subscribed to,
    /// or has favorited.
    /// </summary>
    private static void QueryUserCollections(EUserUGCList listType, CollectionSource source, uint page, int generation)
    {
        try
        {
            UGCQueryHandle_t handle = SteamUGC.CreateQueryUserUGCRequest(
                SteamUser.GetSteamID().GetAccountID(),
                listType,
                EUGCMatchingUGCType.k_EUGCMatchingUGCType_Collections,
                EUserUGCListSortOrder.k_EUserUGCListSortOrder_TitleAsc,
                AppId_t.Invalid,
                SteamUtils.GetAppID(),
                page);

            if (handle == UGCQueryHandle_t.Invalid)
            {
                MyLog.Default.WriteLine($"{Plugin.Name}: Steam refused the {Describe(listType)} collections query.");
                CollectionQueryFailed(generation);
                return;
            }

            SteamAPICall_t apiCall = SteamUGC.SendQueryUGCRequest(handle);
            if (apiCall == SteamAPICall_t.Invalid)
            {
                SteamUGC.ReleaseQueryUGCRequest(handle);
                CollectionQueryFailed(generation);
                return;
            }

            CallResult<SteamUGCQueryCompleted_t> callResult = null;
            callResult = CallResult<SteamUGCQueryCompleted_t>.Create();
            callResult.Set(apiCall, delegate(SteamUGCQueryCompleted_t result, bool ioFailure)
            {
                try
                {
                    OnUserCollectionsResult(result, ioFailure, handle, listType, source, page, generation);
                }
                finally
                {
                    CloseCallResult(callResult);
                }
            });
        }
        catch (Exception e)
        {
            MyLog.Default.WriteLine($"{Plugin.Name}: {Describe(listType)} collections query failed: {e}");
            CollectionQueryFailed(generation);
        }
    }

    private static void OnUserCollectionsResult(
        SteamUGCQueryCompleted_t result,
        bool ioFailure,
        UGCQueryHandle_t handle,
        EUserUGCList listType,
        CollectionSource source,
        uint page,
        int generation)
    {
        bool stale = generation != m_loadGeneration;
        bool failed = ioFailure || result.m_eResult != EResult.k_EResultOK;
        bool hasMorePages = false;

        try
        {
            if (!failed && !stale)
            {
                for (uint i = 0; i < result.m_unNumResultsReturned; i++)
                {
                    if (!SteamUGC.GetQueryUGCResult(handle, i, out SteamUGCDetails_t details)) continue;
                    if (details.m_eResult != EResult.k_EResultOK) continue;
                    if (details.m_eFileType != EWorkshopFileType.k_EWorkshopFileTypeCollection) continue;

                    AddOrUpdate((ulong)details.m_nPublishedFileId, details.m_rgchTitle, source);
                }

                hasMorePages = (long)page * ResultsPerPage < result.m_unTotalMatchingResults;
            }
        }
        finally
        {
            SteamUGC.ReleaseQueryUGCRequest(handle);
        }

        if (stale)
        {
            return;
        }

        if (failed)
        {
            MyLog.Default.WriteLine(
                $"{Plugin.Name}: {Describe(listType)} collections query returned {result.m_eResult} (IO failure: {ioFailure}).");
            CollectionQueryFailed(generation);
        }
        else if (hasMorePages)
        {
            QueryUserCollections(listType, source, page + 1, generation);
        }
        else
        {
            CollectionQueryFinished();
        }
    }

    private static string Describe(EUserUGCList listType)
    {
        switch (listType)
        {
            case EUserUGCList.k_EUserUGCList_Published: return "owned";
            case EUserUGCList.k_EUserUGCList_Favorited: return "favorited";
            default: return "subscribed";
        }
    }

    #endregion

    #region Collection members

    private static void QueryCollectionMembers(ulong collectionId)
    {
        if (!MyGameService.IsActive)
        {
            m_membersLoading = false;
            return;
        }

        m_membersLoading = true;
        m_membersRequestId = collectionId;
        m_membersLoadStartedUtc = DateTime.UtcNow;

        try
        {
            var batch = new[] { (PublishedFileId_t)collectionId };
            UGCQueryHandle_t handle = SteamUGC.CreateQueryUGCDetailsRequest(batch, 1);
            if (handle == UGCQueryHandle_t.Invalid)
            {
                MembersQueryFinished(collectionId, null);
                return;
            }

            SteamUGC.SetReturnChildren(handle, true);

            SteamAPICall_t apiCall = SteamUGC.SendQueryUGCRequest(handle);
            if (apiCall == SteamAPICall_t.Invalid)
            {
                SteamUGC.ReleaseQueryUGCRequest(handle);
                MembersQueryFinished(collectionId, null);
                return;
            }

            CallResult<SteamUGCQueryCompleted_t> callResult = null;
            callResult = CallResult<SteamUGCQueryCompleted_t>.Create();
            callResult.Set(apiCall, delegate(SteamUGCQueryCompleted_t result, bool ioFailure)
            {
                List<ulong> members = null;
                try
                {
                    members = ReadCollectionMembers(result, ioFailure, handle);
                }
                finally
                {
                    CloseCallResult(callResult);
                }
                MembersQueryFinished(collectionId, members);
            });
        }
        catch (Exception e)
        {
            MyLog.Default.WriteLine($"{Plugin.Name}: Collection members query failed: {e}");
            MembersQueryFinished(collectionId, null);
        }
    }

    private static List<ulong> ReadCollectionMembers(SteamUGCQueryCompleted_t result, bool ioFailure, UGCQueryHandle_t handle)
    {
        try
        {
            if (ioFailure || result.m_eResult != EResult.k_EResultOK)
            {
                MyLog.Default.WriteLine(
                    $"{Plugin.Name}: Collection members query returned {result.m_eResult} (IO failure: {ioFailure}).");
                return null;
            }

            if (result.m_unNumResultsReturned == 0) return new List<ulong>();

            if (!SteamUGC.GetQueryUGCResult(handle, 0, out SteamUGCDetails_t details))
            {
                return null;
            }

            var members = new List<ulong>();
            uint childCount = details.m_unNumChildren;
            if (childCount == 0) return members;

            var children = new PublishedFileId_t[childCount];
            if (!SteamUGC.GetQueryUGCChildren(handle, 0, children, childCount))
            {
                return null;
            }

            for (int i = 0; i < children.Length; i++)
            {
                members.Add((ulong)children[i]);
            }

            return members;
        }
        finally
        {
            SteamUGC.ReleaseQueryUGCRequest(handle);
        }
    }

    private static void MembersQueryFinished(ulong collectionId, List<ulong> members)
    {
        if (collectionId != m_selectedId || collectionId != m_membersRequestId) return;

        if (members == null)
        {
            FailMembers(collectionId);
            return;
        }

        SelectedMembers.Clear();
        SelectedMembers.UnionWith(members);

        ResetMemberDetails();

        if (members.Count == 0)
        {
            MemberDetailsFinished(collectionId);
            return;
        }

        QueryMemberDetails(collectionId, members, 0);
    }

    /// <summary>
    /// Fetches the details (title, tags, size, timestamps, owner) of every collection member
    /// so blueprints and the not subscribed ones can be created as detached workshop items.
    /// </summary>
    private static void QueryMemberDetails(ulong collectionId, List<ulong> memberIds, int start)
    {
        if (collectionId != m_selectedId || collectionId != m_membersRequestId) return;

        if (start >= memberIds.Count)
        {
            MemberDetailsFinished(collectionId);
            return;
        }

        int length = Math.Min(DetailsBatchSize, memberIds.Count - start);
        var batch = new PublishedFileId_t[length];
        for (int i = 0; i < length; i++)
        {
            batch[i] = (PublishedFileId_t)memberIds[start + i];
        }

        try
        {
            UGCQueryHandle_t handle = SteamUGC.CreateQueryUGCDetailsRequest(batch, (uint)length);
            if (handle == UGCQueryHandle_t.Invalid)
            {
                FailMembers(collectionId);
                return;
            }

            SteamAPICall_t apiCall = SteamUGC.SendQueryUGCRequest(handle);
            if (apiCall == SteamAPICall_t.Invalid)
            {
                SteamUGC.ReleaseQueryUGCRequest(handle);
                FailMembers(collectionId);
                return;
            }

            CallResult<SteamUGCQueryCompleted_t> callResult = null;
            callResult = CallResult<SteamUGCQueryCompleted_t>.Create();
            callResult.Set(apiCall, delegate(SteamUGCQueryCompleted_t result, bool ioFailure)
            {
                try
                {
                    OnMemberDetailsResult(result, ioFailure, handle, collectionId, memberIds, start, length);
                }
                finally
                {
                    CloseCallResult(callResult);
                }
            });
        }
        catch (Exception e)
        {
            MyLog.Default.WriteLine($"{Plugin.Name}: Collection member details query failed: {e}");
            FailMembers(collectionId);
        }
    }

    private static void OnMemberDetailsResult(
        SteamUGCQueryCompleted_t result,
        bool ioFailure,
        UGCQueryHandle_t handle,
        ulong collectionId,
        List<ulong> memberIds,
        int start,
        int length)
    {
        bool stale = collectionId != m_selectedId || collectionId != m_membersRequestId;
        bool failed = ioFailure || result.m_eResult != EResult.k_EResultOK;

        try
        {
            if (!failed && !stale)
            {
                for (uint i = 0; i < result.m_unNumResultsReturned; i++)
                {
                    if (!SteamUGC.GetQueryUGCResult(handle, i, out SteamUGCDetails_t details)) continue;
                    if (details.m_eResult != EResult.k_EResultOK) continue;

                    string previewUrl = string.Empty;
                    try
                    {
                        SteamUGC.GetQueryUGCPreviewURL(handle, i, out previewUrl, 1024);
                    }
                    catch (Exception)
                    {
                        // Fall back to the UGC handle download.
                    }

                    AddMember(details, previewUrl);
                }
            }
        }
        finally
        {
            SteamUGC.ReleaseQueryUGCRequest(handle);
        }

        if (stale) return;

        if (failed)
        {
            MyLog.Default.WriteLine(
                $"{Plugin.Name}: Collection member details query returned {result.m_eResult} (IO failure: {ioFailure}).");
            FailMemberDetails(collectionId);
            return;
        }

        QueryMemberDetails(collectionId, memberIds, start + length);
    }

    private static void AddMember(SteamUGCDetails_t details, string previewUrl)
    {
        ulong id = (ulong)details.m_nPublishedFileId;
        if (id == 0) return;

        // The game's own subscribed blueprint query uses this tag. Collections may contain
        // mods, worlds and scripts, which do not belong on the blueprint screen.
        if (!HasTag(details.m_rgchTags, "blueprint")) return;
        if (details.m_eFileType != EWorkshopFileType.k_EWorkshopFileTypeCommunity) return;

        var member = new WorkshopMember(id, details.m_rgchTitle)
        {
            Description = details.m_rgchDescription ?? string.Empty,
            OwnerId = details.m_ulSteamIDOwner,
            Size = details.m_nFileSize > 0 ? (ulong)details.m_nFileSize : 0UL,
            TimeCreated = FromUnixTime(details.m_rtimeCreated),
            TimeUpdated = FromUnixTime(details.m_rtimeUpdated),
            PreviewHandle = (ulong)details.m_hPreviewFile,
            PreviewUrl = previewUrl ?? string.Empty
        };

        MyWorkshopItem item = CreateDetachedItem(member);
        if (item == null)
        {
            // Without a workshop item the game cannot display or download this member.
            // Subscribed ones are still shown through the game's own subscribed list.
            return;
        }

        member.Item = item;
        member.Subscribed = item.State.HasFlag(MyWorkshopItemState.Subscribed);

        lock (MemberLock)
        {
            if (Members.ContainsKey(id)) return;

            if (!member.Subscribed) UnsubscribedMemberIds.Add(id);

            Members.Add(id, member);
            BlueprintMembersInternal.Add(member);
        }

        // Subscribed items get their thumbnail from the game's normal workshop flow.
        // Not subscribed items use the primary Steam Workshop image.
        if (!member.Subscribed)
        {
            EnsurePreviewImage(member);
        }
    }

    /// <summary>
    /// Downloads the primary Steam Workshop preview image of a not subscribed member into the
    /// plugin cache, then updates the matching blueprint button.
    /// </summary>
    private static void EnsurePreviewImage(WorkshopMember member)
    {
        ulong handle = member.PreviewHandle;
        if (handle == 0 || handle == (ulong)UGCHandle_t.Invalid) return;

        string resized = GetPreviewPath(handle);
        if (IsUsableFile(resized)) return;

        // Incomplete files from an interrupted download, and images cached at another size.
        DeleteFileIfExists(resized + ".tmp");
        DeleteLegacyPreviewFiles(handle);

        if (!string.IsNullOrEmpty(member.PreviewUrl) && !PreviewUrlFailed.Contains(handle))
        {
            // A raw fallback may be shown meanwhile, but a resized copy is still preferred.
            if (!PreviewQueued.Add(handle)) return;
            PreviewQueue.Enqueue(member);
            PumpPreviewQueue();
            return;
        }

        string fallback = GetFallbackPreviewPath(handle);
        if (IsUsableFile(fallback)) return;

        DeleteFileIfExists(fallback);

        if (!PreviewQueued.Add(handle)) return;
        PreviewQueue.Enqueue(member);
        PumpPreviewQueue();
    }

    /// <summary>
    /// Starts queued preview downloads, a few at a time. Requesting the preview images of a
    /// large collection all at once makes Steam time out and stalls the game.
    /// </summary>
    private static void PumpPreviewQueue()
    {
        while (m_activePreviewDownloads < MaxConcurrentPreviewDownloads && PreviewQueue.Count > 0)
        {
            StartPreviewDownload(PreviewQueue.Dequeue());
        }
    }

    private static void StartPreviewDownload(WorkshopMember member)
    {
        m_activePreviewDownloads++;

        if (!string.IsNullOrEmpty(member.PreviewUrl))
        {
            DownloadPreviewFromUrlAsync(member);
        }
        else
        {
            StartUgcPreviewDownload(member.PreviewHandle);
        }
    }

    /// <summary>
    /// Downloads a small resized copy of the primary image from Steam's image CDN. The original
    /// Workshop preview can be around 0.5-1 MB; an image fitted to the game's 242x128 preview
    /// control keeps the original aspect ratio (228x128 for a 16:9 preview) and is about 6 KB.
    /// </summary>
    private static async void DownloadPreviewFromUrlAsync(WorkshopMember member)
    {
        ulong handle = member.PreviewHandle;
        string path = GetPreviewPath(handle);
        string url = BuildResizedPreviewUrl(member.PreviewUrl);
        bool success = false;

        try
        {
            Directory.CreateDirectory(PreviewCacheDirectory);

            using (HttpResponseMessage response = await HttpClient.GetAsync(url).ConfigureAwait(false))
            {
                if (response.IsSuccessStatusCode)
                {
                    byte[] data = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                    if (LooksLikeImage(data))
                    {
                        string temporaryPath = path + ".tmp";
                        File.WriteAllBytes(temporaryPath, data);
                        DeleteFileIfExists(path);
                        File.Move(temporaryPath, path);
                        success = true;
                    }
                }
            }
        }
        catch (Exception e)
        {
            MyLog.Default.WriteLine($"{Plugin.Name}: Preview image URL download failed ({handle}): {e.Message}");
        }

        try
        {
            MySandboxGame.Static?.Invoke(
                delegate { CompletePreviewDownload(handle, path, success, allowUgcFallback: !success); },
                "BlueprintCollectionsPreview");
        }
        catch (Exception)
        {
            // The game is shutting down.
        }
    }

    private static void StartUgcPreviewDownload(ulong handle)
    {
        string path = GetFallbackPreviewPath(handle);

        try
        {
            Directory.CreateDirectory(PreviewCacheDirectory);

            SteamAPICall_t apiCall = SteamRemoteStorage.UGCDownloadToLocation((UGCHandle_t)handle, path, 0u);
            if (apiCall == SteamAPICall_t.Invalid)
            {
                CompletePreviewDownload(handle, path, false);
                return;
            }

            CallResult<RemoteStorageDownloadUGCResult_t> callResult = null;
            callResult = CallResult<RemoteStorageDownloadUGCResult_t>.Create();
            callResult.Set(apiCall, delegate(RemoteStorageDownloadUGCResult_t result, bool ioFailure)
            {
                bool success = !ioFailure && result.m_eResult == EResult.k_EResultOK && IsUsableFile(path);
                if (!success)
                {
                    MyLog.Default.WriteLine(
                        $"{Plugin.Name}: Could not download a workshop preview image ({handle}): " +
                        $"{result.m_eResult} (IO failure: {ioFailure}).");
                }

                try
                {
                    CompletePreviewDownload(handle, path, success);
                }
                finally
                {
                    try
                    {
                        callResult.Dispose();
                    }
                    catch (Exception)
                    {
                        // Ignore, the game is shutting down.
                    }
                }
            });
        }
        catch (Exception e)
        {
            MyLog.Default.WriteLine($"{Plugin.Name}: Preview image download failed ({handle}): {e}");
            CompletePreviewDownload(handle, path, false);
        }
    }

    private static void CompletePreviewDownload(ulong handle, string path, bool success, bool allowUgcFallback = false)
    {
        if (!success && allowUgcFallback &&
            handle != 0 && handle != (ulong)UGCHandle_t.Invalid)
        {
            // The Steam image CDN failed; fall back to the raw primary preview via Steam.
            // Do not retry the CDN for this image again in this session.
            PreviewUrlFailed.Add(handle);
            DeleteFileIfExists(path);
            DeleteFileIfExists(path + ".tmp");
            StartUgcPreviewDownload(handle);
            return;
        }

        m_activePreviewDownloads--;
        PreviewQueued.Remove(handle);

        try
        {
            if (!success)
            {
                DeleteFileIfExists(path);
                DeleteFileIfExists(path + ".tmp");
                return;
            }

            // The resized image supersedes the raw fallback that was shown meanwhile.
            if (string.Equals(path, GetPreviewPath(handle), StringComparison.OrdinalIgnoreCase))
            {
                DeleteFileIfExists(GetFallbackPreviewPath(handle));
            }

            // Already built buttons can show the image without rebuilding the list.
            // Several members may share the same preview image handle.
            ulong[] ids = GetMemberIdsForPreview(handle);
            for (int i = 0; i < ids.Length; i++)
            {
                CollectionFilterUi.RefreshThumbnail(ids[i], path);
            }

            EnforcePreviewCacheLimit();
        }
        finally
        {
            PumpPreviewQueue();
        }
    }

    private static bool LooksLikeImage(byte[] data)
    {
        if (data == null || data.Length < 4) return false;

        // JPEG
        if (data[0] == 0xFF && data[1] == 0xD8) return true;

        // PNG
        if (data[0] == 0x89 && data[1] == 0x50 && data[2] == 0x4E && data[3] == 0x47) return true;

        return false;
    }

    private static string BuildResizedPreviewUrl(string previewUrl)
    {
        string separator = previewUrl.IndexOf('?') >= 0 ? "&" : "?";

        // Without "letterbox=true" the CDN fits the image inside the box and keeps the
        // original aspect ratio, so a 16:9 preview becomes 228x128 instead of a padded square.
        return previewUrl + separator +
               "imw=" + PreviewImageWidth + "&imh=" + PreviewImageHeight +
               "&ima=fit&impolicy=Letterbox";
    }

    private static ulong[] GetMemberIdsForPreview(ulong previewHandle)
    {
        lock (MemberLock)
        {
            var ids = new List<ulong>();
            foreach (WorkshopMember member in Members.Values)
            {
                if (member.PreviewHandle == previewHandle) ids.Add(member.Id);
            }
            return ids.ToArray();
        }
    }

    /// <summary>
    /// Trims the preview image cache to the configured size (0 = unlimited). Files used by the
    /// current collection and by in-flight downloads are kept as long as possible.
    /// </summary>
    public static void EnforcePreviewCacheLimit()
    {
        int limitMb = Config.Current.PreviewCacheLimitMb;
        if (limitMb <= 0) return;

        try
        {
            string directory = PreviewCacheDirectory;
            if (!Directory.Exists(directory)) return;

            long limitBytes = (long)Math.Min(limitMb, Config.MaxPreviewCacheLimitMb) * 1024L * 1024L;
            var directoryInfo = new DirectoryInfo(directory);
            FileInfo[] jpgFiles = directoryInfo.GetFiles("*.jpg");
            FileInfo[] pngFiles = directoryInfo.GetFiles("*.png");

            long total = 0;
            for (int i = 0; i < jpgFiles.Length; i++) total += jpgFiles[i].Length;
            for (int i = 0; i < pngFiles.Length; i++) total += pngFiles[i].Length;
            if (total <= limitBytes) return;

            var protectedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            lock (MemberLock)
            {
                foreach (WorkshopMember member in Members.Values)
                {
                    if (member.PreviewHandle == 0 ||
                        member.PreviewHandle == (ulong)UGCHandle_t.Invalid) continue;

                    protectedPaths.Add(GetPreviewPath(member.PreviewHandle));
                    protectedPaths.Add(GetFallbackPreviewPath(member.PreviewHandle));
                }
            }

            foreach (ulong handle in PreviewQueued)
            {
                protectedPaths.Add(GetPreviewPath(handle));
                protectedPaths.Add(GetFallbackPreviewPath(handle));
            }

            var files = new FileInfo[jpgFiles.Length + pngFiles.Length];
            Array.Copy(jpgFiles, files, jpgFiles.Length);
            Array.Copy(pngFiles, 0, files, jpgFiles.Length, pngFiles.Length);
            Array.Sort(files, (a, b) => a.LastWriteTimeUtc.CompareTo(b.LastWriteTimeUtc));

            long removed = 0;

            // Delete unprotected files first.
            for (int i = 0; i < files.Length && total > limitBytes; i++)
            {
                FileInfo file = files[i];
                if (protectedPaths.Contains(file.FullName)) continue;

                if (TryDeleteFile(file))
                {
                    total -= file.Length;
                    removed++;
                }
            }

            // If the current collection alone is too large, trim its oldest files too.
            for (int i = 0; i < files.Length && total > limitBytes; i++)
            {
                FileInfo file = files[i];
                if (!File.Exists(file.FullName)) continue;

                if (TryDeleteFile(file))
                {
                    total -= file.Length;
                    removed++;
                }
            }

            if (removed > 0)
            {
                MyLog.Default.WriteLine(
                    $"{Plugin.Name}: Trimmed {removed} preview image(s); cache is now " +
                    $"{total / (1024 * 1024)} MB (limit {limitMb} MB).");
            }
        }
        catch (Exception e)
        {
            MyLog.Default.WriteLine($"{Plugin.Name}: Failed to enforce the preview cache limit: {e}");
        }
    }

    private static bool TryDeleteFile(FileInfo file)
    {
        try
        {
            file.Delete();
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static void MemberDetailsFinished(ulong collectionId)
    {
        if (collectionId != m_selectedId || collectionId != m_membersRequestId) return;

        m_membersLoading = false;
        m_filterVersion++;

        int memberCount;
        int blueprintCount;
        int unsubscribedCount;

        lock (MemberLock)
        {
            m_membersLoaded = true;

            BlueprintMembersInternal.Sort(
                (a, b) => string.Compare(a.Title, b.Title, StringComparison.CurrentCultureIgnoreCase));

            memberCount = SelectedMembers.Count;
            blueprintCount = BlueprintMembersInternal.Count;
            unsubscribedCount = UnsubscribedMemberIds.Count;
        }

        WorkshopCollection collection = Find(collectionId);
        if (collection != null && Config.Current.SelectedCollectionTitle != collection.Title)
        {
            Config.Current.SelectedCollectionTitle = collection.Title;
            Config.Save();
        }

        MyLog.Default.WriteLine(
            $"{Plugin.Name}: Collection {collectionId}: {memberCount} member(s), " +
            $"{blueprintCount} blueprint(s), {unsubscribedCount} not subscribed.");

        RaiseChanged();
    }

    private static void FailMembers(ulong collectionId)
    {
        if (collectionId != m_selectedId || collectionId != m_membersRequestId) return;

        m_membersLoading = false;
        ResetMemberData();
        m_filterVersion++;
        ScheduleRetry();
        MyLog.Default.WriteLine($"{Plugin.Name}: Could not load members of collection {collectionId}.");
        RaiseChanged();
    }

    /// <summary>
    /// The collection members are known, but their details (and thus the not subscribed
    /// items) could not be fetched. Keep filtering with the known ids so subscribed
    /// blueprints stay usable.
    /// </summary>
    private static void FailMemberDetails(ulong collectionId)
    {
        if (collectionId != m_selectedId || collectionId != m_membersRequestId) return;

        m_membersLoading = false;

        lock (MemberLock)
        {
            m_membersLoaded = true;
            Members.Clear();
            BlueprintMembersInternal.Clear();
            UnsubscribedMemberIds.Clear();
        }

        m_filterVersion++;
        MyLog.Default.WriteLine(
            $"{Plugin.Name}: Could not load the details of the members of collection {collectionId}. " +
            "Only subscribed blueprints are shown.");
        RaiseChanged();
    }

    /// <summary>
    /// Creates a workshop item object for a collection member that is not in the game's
    /// subscribed list. It carries enough metadata for the screen and download code, and all
    /// real state (installed/not installed, folder) is filled by <see cref="MyWorkshopItem.UpdateState"/>.
    /// </summary>
    private static MyWorkshopItem CreateDetachedItem(WorkshopMember member)
    {
        try
        {
            MyWorkshopItem item = MyGameService.CreateWorkshopItem(SteamServiceName);
            if (item == null) return null;

            item.Id = member.Id;
            item.Title = member.Title;
            SetProperty(item, "Description", member.Description);
            SetProperty(item, "OwnerId", member.OwnerId);
            SetProperty(item, "Size", member.Size);
            SetProperty(item, "TimeCreated", member.TimeCreated);
            SetProperty(item, "TimeUpdated", member.TimeUpdated);
            SetProperty(item, "ItemType", MyWorkshopItemType.Item);

            // Fills State and Folder for items Steam still has in its local cache.
            item.UpdateState();
            return item;
        }
        catch (Exception e)
        {
            MyLog.Default.WriteLine($"{Plugin.Name}: Failed to create a workshop item for {member.Id}: {e}");
            return null;
        }
    }

    private static readonly Dictionary<(Type, string), PropertyInfo> PropertyCache =
        new Dictionary<(Type, string), PropertyInfo>();

    private static void SetProperty(object instance, string name, object value)
    {
        try
        {
            var key = (instance.GetType(), name);
            if (!PropertyCache.TryGetValue(key, out PropertyInfo property))
            {
                property = instance.GetType().GetProperty(
                    name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                PropertyCache[key] = property;
            }

            property?.SetValue(instance, value);
        }
        catch (Exception e)
        {
            MyLog.Default.WriteLine($"{Plugin.Name}: Failed to set workshop item property {name}: {e.Message}");
        }
    }

    private static bool HasTag(string tags, string tag)
    {
        if (string.IsNullOrEmpty(tags)) return false;
        string[] parts = tags.Split(',');
        for (int i = 0; i < parts.Length; i++)
        {
            if (string.Equals(parts[i].Trim(), tag, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    private static DateTime FromUnixTime(uint seconds)
    {
        try
        {
            return DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime;
        }
        catch (ArgumentOutOfRangeException)
        {
            return DateTime.MinValue;
        }
    }

    #endregion

    #region Helpers

    private static void AddOrUpdate(ulong id, string title, CollectionSource source)
    {
        if (id == 0) return;

        if (IsIgnoredTitle(title))
        {
            // Keep ignored collections out of the picker. If one is selected, remember its
            // title so the UI can turn the filter off; the selection is cleared from the UI.
            WorkshopCollection ignored = Find(id);
            if (ignored != null) CollectionsInternal.Remove(ignored);

            if (id == m_selectedId)
            {
                Config.Current.SelectedCollectionTitle = title;
                Config.Save();
            }
            return;
        }

        WorkshopCollection collection = Find(id);
        if (collection == null)
        {
            collection = new WorkshopCollection(id, title);
            CollectionsInternal.Add(collection);
        }

        if (!string.IsNullOrWhiteSpace(title)) collection.Title = title;

        switch (source)
        {
            case CollectionSource.Owned:
                collection.Owned = true;
                break;
            case CollectionSource.Subscribed:
                collection.Subscribed = true;
                break;
            case CollectionSource.Favorited:
                collection.Favorited = true;
                break;
        }

        if (id == m_selectedId && Config.Current.SelectedCollectionTitle != collection.Title)
        {
            Config.Current.SelectedCollectionTitle = collection.Title;
            Config.Save();
        }
    }

    private static void CollectionQueryFailed(int generation)
    {
        if (generation != m_loadGeneration) return;
        m_collectionLoadFailed = true;
        ScheduleRetry();
        CollectionQueryFinished();
    }

    private static void CollectionQueryFinished()
    {
        if (m_initialQueriesPending > 0) m_initialQueriesPending--;
        if (m_initialQueriesPending > 0) return;

        m_loading = false;
        m_loaded = !m_collectionLoadFailed;

        CollectionsInternal.Sort((a, b) =>
        {
            int result = string.Compare(
                a.Title, b.Title, StringComparison.CurrentCultureIgnoreCase);
            return result != 0 ? result : a.Id.CompareTo(b.Id);
        });

        MyLog.Default.WriteLine($"{Plugin.Name}: Loaded {CollectionsInternal.Count} workshop collection(s).");

        // Select() raises Changed itself when it clears an ignored selection.
        if (!ClearIgnoredSelectionIfNeeded()) RaiseChanged();
    }

    private static void ScheduleRetry()
    {
        m_nextRetryUtc = DateTime.UtcNow.AddSeconds(10);
    }

    private static void CloseCallResult(CallResult<SteamUGCQueryCompleted_t> callResult)
    {
        try
        {
            callResult?.Dispose();
        }
        catch (Exception)
        {
            // Ignore, the game is shutting down.
        }
    }

    #endregion
}

