using HarmonyLib;

namespace GraveyardKeeperCoop.Patches
{
    /// <summary>
    /// Makes the map shortcut behave as a toggle while the GameGUI is open.
    /// </summary>
    [HarmonyPatch(typeof(GameGUI), nameof(GameGUI.OpenOrSelectTab))]
    internal static class MapTogglePatches
    {
        [HarmonyPrefix]
        private static bool CloseSelectedMapOnMapShortcut(
            GameGUI __instance,
            GameGUI.TabType tab)
        {
            if (__instance == null || !__instance.is_shown ||
                tab != GameGUI.TabType.Map ||
                !LazyInput.GetKeyDown(GameKey.Map))
            {
                return true;
            }

            MapGUI map = GUIElements.me?.map;
            if (map == null || !map.is_shown)
                return true;

            __instance.Hide(true);
            LazyInput.ClearKeyDown(GameKey.Map);
            return false;
        }
    }
}
