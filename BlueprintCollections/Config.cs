using System;
using System.IO;
using System.Xml.Serialization;
using VRage.FileSystem;
using VRage.Utils;

namespace BlueprintCollections;

/// <summary>
/// Persisted plugin configuration.
/// </summary>
[XmlRoot("BlueprintCollectionsConfig")]
public class Config
{
    /// <summary>
    /// Steam Workshop collection currently used as the filter, 0 means no filtering.
    /// </summary>
    public ulong SelectedCollectionId { get; set; }

    /// <summary>
    /// Cached title of the selected collection, used while the collection list is still loading.
    /// </summary>
    public string SelectedCollectionTitle { get; set; } = string.Empty;

    /// <summary>
    /// Maximum size of the Steam preview image cache in megabytes. 0 means unlimited.
    /// </summary>
    public int PreviewCacheLimitMb { get; set; } = 10;

    public const int DefaultPreviewCacheLimitMb = 10;
    public const int MaxPreviewCacheLimitMb = 4096;

    private static string ConfigFilePath =>
        Path.Combine(MyFileSystem.UserDataPath, "Storage", Plugin.Name + ".cfg");

    public static Config Current { get; private set; } = new Config();

    public static void Load()
    {
        string path = null;
        try
        {
            path = ConfigFilePath;
            if (File.Exists(path))
            {
                using (var reader = File.OpenText(path))
                {
                    Current = (Config)new XmlSerializer(typeof(Config)).Deserialize(reader) ?? new Config();
                }
            }
            else
            {
                Current = new Config();
            }
        }
        catch (Exception e)
        {
            MyLog.Default.Warning($"{Plugin.Name}: Failed to read config file {path ?? "<unknown>"}: {e.Message}");
            Current = new Config();
        }

        if (Current.PreviewCacheLimitMb < 0) Current.PreviewCacheLimitMb = DefaultPreviewCacheLimitMb;
        if (Current.PreviewCacheLimitMb > MaxPreviewCacheLimitMb) Current.PreviewCacheLimitMb = MaxPreviewCacheLimitMb;
    }

    public static void Save()
    {
        string path = null;
        try
        {
            path = ConfigFilePath;
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            using (var writer = File.CreateText(path))
            {
                new XmlSerializer(typeof(Config)).Serialize(writer, Current);
            }
        }
        catch (Exception e)
        {
            MyLog.Default.Warning($"{Plugin.Name}: Failed to write config file {path ?? "<unknown>"}: {e.Message}");
        }
    }
}
