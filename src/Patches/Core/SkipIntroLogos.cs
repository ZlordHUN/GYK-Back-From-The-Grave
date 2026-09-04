using HarmonyLib;
using LazyBearGames.Preloader;
using UnityEngine.SceneManagement;

namespace GraveyardKeeperCoop.Patches
{
    /// <summary>
    /// Skips the Tiny Games and Lazy Bear Studios intro logos on startup.
    /// </summary>
    [HarmonyPatch(typeof(LogoScene), "Awake")]
    public class SkipIntroLogos
    {
        static bool Prefix()
        {
            CoopMod.Logger.LogInfo("Skipping intro logos, going straight to preloader...");
            SceneManager.LoadScene("preloader");
            return false; // Skip original method
        }
    }
}
