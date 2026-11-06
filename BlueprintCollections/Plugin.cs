using System;
using System.Reflection;
using System.Threading;
using HarmonyLib;
using Sandbox;
using Sandbox.Graphics.GUI;
using VRage.Plugins;
using VRage.Utils;

// Define assembly version when compiled by Pulsar
#if !LOCAL_BUILD
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]
#endif

namespace BlueprintCollections;

/// <summary>
/// Client plugin that filters the workshop blueprint list of the game's blueprints screen
/// down to the items of a Steam Workshop collection owned by, subscribed to, or favorited
/// by the player.
/// </summary>
public class Plugin : IPlugin
{
    public const string Name = "BlueprintCollections";

    public static Plugin Instance { get; private set; }

    private int m_retryCounter;
    private static int m_configDialogPending;
    private static volatile bool m_configDialogShown;

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    public void Init(object gameInstance)
    {
        Instance = this;

        Config.Load();
        WorkshopCollectionService.Initialize();

        var harmony = new Harmony(Name);
        harmony.PatchAll(Assembly.GetExecutingAssembly());
    }

    public void Dispose()
    {
        // IMPORTANT: Do NOT call harmony.UnpatchAll() here! It may break other plugins.
        Instance = null;
    }

    public void Update()
    {
        CollectionFilterUi.Update();

        // Retry loading while the player is browsing the blueprints screen and the
        // collection data is not available yet (for example before Steam is ready).
        if (++m_retryCounter < 120) return; // ~2 seconds at 60 FPS
        m_retryCounter = 0;

        if (!CollectionFilterUi.HasLiveScreens) return;
        WorkshopCollectionService.TryRetry();
    }

    /// <summary>
    /// Called by Pulsar or CringeLauncher when the player opens the plugin's configuration.
    /// CringeLauncher calls it from its render thread, so the game GUI must be touched on the
    /// main thread only.
    /// </summary>
    public void OpenConfigDialog()
    {
        if (m_configDialogShown) return;
        if (Interlocked.CompareExchange(ref m_configDialogPending, 1, 0) != 0) return;

        if (MySandboxGame.Static == null)
        {
            m_configDialogPending = 0;
            return;
        }

        try
        {
            MySandboxGame.Static.Invoke(delegate
            {
                m_configDialogPending = 0;
                if (m_configDialogShown) return;

                m_configDialogShown = true;

                try
                {
                    var screen = new ConfigScreen();
                    screen.Closed += delegate(MyGuiScreenBase source, bool isUnloading)
                    {
                        m_configDialogShown = false;
                    };

                    MyGuiSandbox.AddScreen(screen);
                }
                catch (Exception e)
                {
                    m_configDialogShown = false;
                    MyLog.Default.WriteLine($"{Plugin.Name}: Failed to open the settings dialog: {e}");
                }
            }, "BlueprintCollectionsOpenConfig");
        }
        catch (Exception e)
        {
            m_configDialogPending = 0;
            MyLog.Default.WriteLine($"{Plugin.Name}: Failed to queue the settings dialog: {e}");
        }
    }
}
