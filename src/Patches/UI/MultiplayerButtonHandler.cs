using UnityEngine;

namespace GraveyardKeeperCoop.Patches
{
    /// <summary>
    /// MonoBehaviour that handles clicks on the multiplayer button
    /// Attached to the button GameObject to handle OnClick events
    /// </summary>
    public class MultiplayerButtonHandler : MonoBehaviour
    {
        public MainMenuGUI mainMenu;
        
        /// <summary>
        /// Called when the button is clicked (via UIButton's OnClick event)
        /// </summary>
        public void OnClick()
        {
            CoopMod.Logger.LogInfo("MultiplayerButtonHandler.OnClick called!");
            
            if (mainMenu != null)
            {
                MainMenuPatches.OnMultiplayerButtonPressed(mainMenu);
            }
            else
            {
                CoopMod.Logger.LogError("MainMenuGUI reference is null!");
            }
        }
        
        /// <summary>
        /// Called when the object is clicked (Unity's OnMouseDown event)
        /// </summary>
        private void OnMouseDown()
        {
            CoopMod.Logger.LogInfo("MultiplayerButtonHandler.OnMouseDown called!");
            OnClick();
        }
    }
}
