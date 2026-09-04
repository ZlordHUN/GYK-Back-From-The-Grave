using HarmonyLib;

namespace GraveyardKeeperCoop.Patches
{
    /// <summary>
    /// Blocks game keyboard input when a UI input field has focus.
    /// This prevents character movement/actions while typing in chat or other input fields.
    /// Works by intercepting KeyboardController.Update and skipping it when input is focused.
    /// </summary>
    [HarmonyPatch(typeof(KeyboardController), "Update")]
    public static class InputBlocker
    {
        /// <summary>
        /// Prefix that blocks KeyboardController.Update when UI input has focus.
        /// UICamera.inputHasFocus is set by NGUI when any UIInput is selected.
        /// UIInput.current tracks the currently active input field.
        /// </summary>
        [HarmonyPrefix]
        public static bool Prefix()
        {
            // Block keyboard input when:
            // 1. UICamera reports that input has focus (NGUI's standard way)
            // 2. There's an active UIInput field
            if (UICamera.inputHasFocus || UIInput.current != null)
            {
                return false; // Skip KeyboardController.Update
            }
            
            return true; // Allow normal keyboard processing
        }
    }
}
