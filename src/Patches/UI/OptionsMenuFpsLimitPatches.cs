using HarmonyLib;
using UnityEngine;

namespace GraveyardKeeperCoop.Patches
{
    /// <summary>
    /// Adds the mod's frame-rate limiter to the game's native Options menu.
    /// </summary>
    [HarmonyPatch]
    public static class OptionsMenuFpsLimitPatches
    {
        private static readonly int[] FpsValues = { 0, 30, 60, 75, 90, 120, 144, 165, 240 };
        private static readonly string[] FpsLabels =
        {
            "Game default",
            "30 FPS",
            "60 FPS",
            "75 FPS",
            "90 FPS",
            "120 FPS",
            "144 FPS",
            "165 FPS",
            "240 FPS"
        };

        [HarmonyPatch(typeof(BaseMenuGUI), nameof(BaseMenuGUI.Init))]
        [HarmonyPostfix]
        private static void BaseMenuGUI_Init_Postfix(BaseMenuGUI __instance)
        {
            if (__instance is OptionsMenuGUI optionsMenu)
                EnsureInstalled(optionsMenu);
        }

        [HarmonyPatch(typeof(OptionsMenuGUI), nameof(OptionsMenuGUI.Open))]
        [HarmonyPostfix]
        private static void OptionsMenuGUI_Open_Postfix(OptionsMenuGUI __instance)
        {
            OptionsMenuFpsLimitController controller = EnsureInstalled(__instance);
            controller?.RefreshValue();
        }

        private static OptionsMenuFpsLimitController EnsureInstalled(OptionsMenuGUI menu)
        {
            if (menu == null)
                return null;

            OptionsMenuFpsLimitController controller =
                menu.GetComponent<OptionsMenuFpsLimitController>() ??
                menu.gameObject.AddComponent<OptionsMenuFpsLimitController>();
            controller.EnsureInstalled(menu, FpsValues, FpsLabels);
            return controller;
        }
    }

    internal sealed class OptionsMenuFpsLimitController : MonoBehaviour
    {
        private const int BackgroundHeightExtension = 42;

        private OptionsMenuGUI menu;
        private MenuItemGUI fpsLimitItem;
        private int[] fpsValues;
        private string[] fpsLabels;
        private bool installed;

        public void EnsureInstalled(OptionsMenuGUI optionsMenu, int[] values, string[] labels)
        {
            if (installed || optionsMenu == null || optionsMenu.cursor_mode_switcher == null)
                return;

            menu = optionsMenu;
            fpsValues = values;
            fpsLabels = labels;

            GameObject rowObject = Object.Instantiate(
                optionsMenu.cursor_mode_switcher.gameObject,
                optionsMenu.cursor_mode_switcher.transform.parent);
            rowObject.name = "fps_limit_switcher";
            rowObject.transform.localScale = Vector3.one;
            rowObject.transform.SetSiblingIndex(optionsMenu.cursor_mode_switcher.transform.GetSiblingIndex() + 1);

            foreach (LocalizedLabel localizedLabel in rowObject.GetComponentsInChildren<LocalizedLabel>(true))
            {
                localizedLabel.enabled = false;
                Object.DestroyImmediate(localizedLabel);
            }

            fpsLimitItem = rowObject.GetComponent<MenuItemGUI>();
            if (fpsLimitItem == null)
            {
                CoopMod.Logger.LogError("[OptionsFPS] Cloned row has no MenuItemGUI component");
                Object.Destroy(rowObject);
                return;
            }

            fpsLimitItem.Init(optionsMenu);
            SetLeftLabel("FPS limit");
            RefreshValue();
            fpsLimitItem.Show();

            RebuildMenuItems();
            ExtendBackground();
            RepositionMenu();
            installed = true;

            CoopMod.Logger.LogInfo("[OptionsFPS] Added FPS limit row below Mouse cursor");
        }

        public void RefreshValue()
        {
            if (fpsLimitItem == null || fpsLabels == null || fpsLabels.Length == 0)
                return;

            int selectedIndex = GetSelectedIndex();
            fpsLimitItem.SetupOptions(
                selectedIndex,
                fpsLabels.Length - 1,
                fpsLabels[selectedIndex],
                OnOptionChanged,
                false);
            SetLeftLabel("FPS limit");
        }

        private int GetSelectedIndex()
        {
            if (ModConfig.OverrideFramePacing?.Value != true)
                return 0;

            int target = ModConfig.TargetFrameRate?.Value ?? 60;
            int bestIndex = 1;
            int bestDistance = int.MaxValue;
            for (int i = 1; i < fpsValues.Length; i++)
            {
                int distance = Mathf.Abs(fpsValues[i] - target);
                if (distance >= bestDistance)
                    continue;

                bestDistance = distance;
                bestIndex = i;
            }

            return bestIndex;
        }

        private void OnOptionChanged(int index, UILabel valueLabel)
        {
            if (index < 0 || index >= fpsValues.Length)
                return;

            int target = fpsValues[index];
            bool enabled = target > 0;
            ModConfig.OverrideFramePacing.Value = enabled;
            if (enabled)
                ModConfig.TargetFrameRate.Value = target;

            if (valueLabel != null)
            {
                valueLabel.text = fpsLabels[index];
                valueLabel.MarkAsChanged();
            }

            PerformanceSmoothingPatches.ApplyConfiguredFramePacing(forceLog: true);
            CoopMod.Logger.LogInfo(
                enabled
                    ? $"[OptionsFPS] FPS limit set to {target}"
                    : "[OptionsFPS] FPS limit set to Game default");
        }

        private void SetLeftLabel(string text)
        {
            if (fpsLimitItem == null)
                return;

            foreach (UILabel label in fpsLimitItem.GetComponentsInChildren<UILabel>(true))
            {
                string parentName = label.transform.parent != null
                    ? label.transform.parent.name
                    : string.Empty;
                if (label.name != "label" || parentName == "options switcher" ||
                    parentName == "dec" || parentName == "inc")
                {
                    continue;
                }

                label.text = text;
                label.MarkAsChanged();
                return;
            }
        }

        private void RebuildMenuItems()
        {
            var itemsField = AccessTools.Field(typeof(BaseMenuGUI), "items");
            if (itemsField == null)
            {
                CoopMod.Logger.LogWarning("[OptionsFPS] Could not update Options menu item list");
                return;
            }

            MenuItemGUI[] orderedItems = menu.GetComponentsInChildren<MenuItemGUI>(true);
            itemsField.SetValue(menu, orderedItems);
        }

        private void ExtendBackground()
        {
            UI2DSprite background = null;
            foreach (UI2DSprite sprite in menu.GetComponentsInChildren<UI2DSprite>(true))
            {
                if (!sprite.name.ToLowerInvariant().Contains("back") ||
                    sprite.width <= 200 || sprite.height <= 100)
                {
                    continue;
                }

                if (background == null || sprite.width * sprite.height > background.width * background.height)
                    background = sprite;
            }

            if (background == null)
            {
                CoopMod.Logger.LogWarning("[OptionsFPS] Could not find the Options background to extend");
                return;
            }

            int oldHeight = background.height;
            Vector3 oldPosition = background.transform.localPosition;
            background.height = oldHeight + BackgroundHeightExtension;
            background.transform.localPosition = KeepTopEdgeFixed(
                oldPosition,
                background.pivot,
                BackgroundHeightExtension);
            background.MarkAsChanged();

            CoopMod.Logger.LogInfo(
                $"[OptionsFPS] Extended Options background {oldHeight} -> {background.height}");
        }

        private static Vector3 KeepTopEdgeFixed(Vector3 position, UIWidget.Pivot pivot, int heightAdded)
        {
            switch (pivot)
            {
                case UIWidget.Pivot.BottomLeft:
                case UIWidget.Pivot.Bottom:
                case UIWidget.Pivot.BottomRight:
                    position.y -= heightAdded;
                    break;
                case UIWidget.Pivot.Left:
                case UIWidget.Pivot.Center:
                case UIWidget.Pivot.Right:
                    position.y -= heightAdded * 0.5f;
                    break;
            }

            return position;
        }

        private void RepositionMenu()
        {
            SimpleUITable simpleTable = menu.GetComponentInChildren<SimpleUITable>(true);
            simpleTable?.Reposition();

            UITable table = menu.GetComponentInChildren<UITable>(true);
            if (table != null)
            {
                table.repositionNow = true;
                table.Reposition();
            }

            GamepadNavigationController navigation = menu.GetComponent<GamepadNavigationController>();
            if (navigation != null && navigation.is_enabled)
                navigation.ReinitItems(false);
        }
    }
}
