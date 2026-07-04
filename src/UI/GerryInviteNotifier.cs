using UnityEngine;
using BepInEx;

namespace GraveyardKeeperCoop.UI
{
    /// <summary>
    /// Manages Gerry the Skull appearing on the Join Game screen to notify players of invitations.
    /// Spawns its own Gerry WorldGameObject and makes him hop continuously like in the game.
    /// Gerry's obj_id is "talking_skull" and NPC ID is "crafting_skull_3".
    /// </summary>
    public class GerryInviteNotifier : MonoBehaviour
    {
        // Configuration
        private float hopDuration = 0.5f;        // Duration of each hop
        private float hopHeight = 30f;           // Height of hop arc in UI space
        
        // References
        private WorldGameObject gerryWGO;        // Our spawned Gerry instance
        private GameObject gerryContainer;       // Container in UI space (positioned beneath Join/Back buttons)
        private GameObject gerrySpritesContainer; // Inner container for just Gerry's sprites (this one hops)
        private Transform bubbleCornerTransform; // For speech bubbles (stays still)
        private Transform dialogueCornerTransform; // For dialogue options (positioned lower to match speech bubble location)
        private bool isShowing = false;
        private bool isHopping = false;
        private Coroutine hoppingCoroutine;
        
        // Invitation data
        private string currentInviterName;
        private System.Action onAccept;
        private System.Action onDecline;

        /// <summary>
        /// Initializes Gerry by spawning our own WorldGameObject.
        /// Gerry's obj_id is "talking_skull".
        /// </summary>
        public void Initialize(Transform parent)
        {
            CoopMod.Logger.LogInfo("Initializing GerryInviteNotifier - spawning Gerry...");

            // Create a container for Gerry - position it beneath the Join/Back buttons
            gerryContainer = new GameObject("GerryInviteContainer");
            gerryContainer.transform.SetParent(parent, false);
            gerryContainer.transform.localPosition = new Vector3(0, -130, 0); // Even lower on screen
            gerryContainer.transform.localScale = Vector3.one;
            gerryContainer.layer = 13; // UI layer

            // Create inner container for Gerry's sprites (this one will hop)
            gerrySpritesContainer = new GameObject("GerrySprites");
            gerrySpritesContainer.transform.SetParent(gerryContainer.transform, false);
            gerrySpritesContainer.transform.localPosition = Vector3.zero;
            gerrySpritesContainer.transform.localScale = Vector3.one;
            gerrySpritesContainer.layer = 13;

            // Spawn Gerry WGO as a child of the sprites container
            SpawnGerry();

            // Create bubble corner point for speech bubbles
            GameObject cornerPoint = new GameObject("BubbleCornerPoint");
            cornerPoint.transform.SetParent(gerryContainer.transform, false);
            cornerPoint.transform.localPosition = new Vector3(0, 60, 0); // Moderate distance above Gerry
            cornerPoint.AddComponent<BubbleCornerPoint>();
            bubbleCornerTransform = cornerPoint.transform;

            // Create a separate corner point for dialogue options (positioned lower to match speech bubble)
            // CRITICAL: MultiAnswerGUI uses world camera, so we need a world-space transform!
            // Convert the UI position to world space
            Vector3 screenPos = MainGame.me.gui_cam.WorldToScreenPoint(bubbleCornerTransform.position);
            // Use a fixed Z depth near the camera instead of letting ScreenToWorldPoint use the far clip plane
            screenPos.z = 10f;
            Vector3 worldPos = MainGame.me.world_cam.ScreenToWorldPoint(screenPos);
            
            GameObject dialogueCornerPoint = new GameObject("DialogueCornerPoint");
            // Don't parent it - let it be in world space
            dialogueCornerPoint.transform.position = worldPos;
            dialogueCornerPoint.AddComponent<BubbleCornerPoint>();
            dialogueCornerTransform = dialogueCornerPoint.transform;
            
            CoopMod.Logger.LogInfo($"Created world-space dialogue corner at world pos: {worldPos}, screen pos: {screenPos}");

            gerryContainer.SetActive(false);
            CoopMod.Logger.LogInfo("GerryInviteNotifier initialized successfully");
        }


        /// <summary>
        /// Spawns Gerry as a WorldGameObject so we get proper animations.
        /// Gerry's obj_id is "talking_skull" in the game files.
        /// Since WGOs don't render on menu screens, we'll also create UI sprite copies.
        /// </summary>
        private void SpawnGerry()
        {
            try
            {
                CoopMod.Logger.LogInfo("Spawning Gerry WorldGameObject...");

                // Try to spawn Gerry's WGO - obj_id could be "talking_skull" or "crafting_skull_3"
                string[] gerryIds = new string[] { "talking_skull", "crafting_skull_3", "crafting_skull" };
                
                foreach (string objId in gerryIds)
                {
                    try
                    {
                        gerryWGO = WorldMap.SpawnWGO(gerrySpritesContainer.transform, objId, Vector3.zero);
                        if (gerryWGO != null)
                        {
                            CoopMod.Logger.LogInfo($"Successfully spawned Gerry WGO with obj_id: '{objId}'");
                            
                            // Get the SpriteRenderers from WGO
                            var spriteRenderers = gerryWGO.GetComponentsInChildren<SpriteRenderer>(true);
                            CoopMod.Logger.LogInfo($"Found {spriteRenderers.Length} SpriteRenderers in WGO");
                            
                            // CRITICAL FIX: Create UI2DSprite copies of each SpriteRenderer
                            // This allows us to display Gerry in UI space on menu screens!
                            foreach (var sr in spriteRenderers)
                            {
                                if (sr.sprite != null)
                                {
                                    // Create a UI sprite gameobject as child of sprites container
                                    GameObject uiSpriteGO = new GameObject($"UI_{sr.gameObject.name}");
                                    uiSpriteGO.transform.SetParent(gerrySpritesContainer.transform, false);
                                    uiSpriteGO.layer = 13; // UI layer
                                    
                                    // Add UI2DSprite component
                                    var ui2dSprite = uiSpriteGO.AddComponent<UI2DSprite>();
                                    ui2dSprite.sprite2D = sr.sprite;
                                    ui2dSprite.depth = 1000;
                                    ui2dSprite.MakePixelPerfect();
                                    
                                    // Position relative to container (will need tweaking)
                                    uiSpriteGO.transform.localPosition = Vector3.zero;
                                    uiSpriteGO.transform.localScale = Vector3.one * 2f; // 2x bigger
                                    
                                    CoopMod.Logger.LogInfo($"Created UI2DSprite for {sr.gameObject.name}: sprite={sr.sprite.name}, depth={ui2dSprite.depth}");
                                }
                            }
                            
                            // Hide the WGO since it won't render on menu anyway
                            gerryWGO.gameObject.SetActive(false);
                            
                            CoopMod.Logger.LogInfo($"Created {spriteRenderers.Length} UI sprites from Gerry WGO");
                            break;
                        }
                    }
                    catch (System.Exception ex)
                    {
                        CoopMod.Logger.LogWarning($"Could not spawn WGO with obj_id '{objId}': {ex.Message}");
                    }
                }

                if (gerryWGO == null)
                {
                    CoopMod.Logger.LogError("Failed to spawn Gerry WGO with any obj_id!");
                }
            }
            catch (System.Exception ex)
            {
                CoopMod.Logger.LogError($"Error spawning Gerry WGO: {ex.Message}\n{ex.StackTrace}");
            }
        }



        /// <summary>
        /// Shows Gerry with an invitation message and Accept/Decline options.
        /// </summary>
        public void ShowInvitation(string inviterName, System.Action onAcceptCallback, System.Action onDeclineCallback)
        {
            CoopMod.Logger.LogInfo("[GERRY] ========== GerryInviteNotifier.ShowInvitation() ==========");
            CoopMod.Logger.LogInfo($"[GERRY] Inviter: {inviterName}");
            CoopMod.Logger.LogInfo($"[GERRY] isShowing: {isShowing}");
            CoopMod.Logger.LogInfo($"[GERRY] gerryContainer null? {gerryContainer == null}");
            
            if (isShowing)
            {
                CoopMod.Logger.LogWarning("[GERRY] Gerry is already showing an invitation!");
                return;
            }

            currentInviterName = inviterName;
            onAccept = onAcceptCallback;
            onDecline = onDeclineCallback;
            isShowing = true;

            CoopMod.Logger.LogInfo($"[GERRY] Starting hop onto screen animation...");

            // Start Gerry off-screen to the left and hop him on
            StartCoroutine(HopOntoScreen());
        }

        /// <summary>
        /// Makes Gerry hop onto the screen from the left.
        /// </summary>
        private System.Collections.IEnumerator HopOntoScreen()
        {
            CoopMod.Logger.LogInfo("[GERRY] HopOntoScreen coroutine started");
            
            // Activate container but start off-screen
            gerryContainer.SetActive(true);
            CoopMod.Logger.LogInfo("[GERRY] gerryContainer activated");
            
            // Store the normal position
            Vector3 normalPosition = new Vector3(0, -130, 0);
            
            // Calculate starting position (off-screen left)
            // We need to check screen width in UI space
            Vector3 screenLeftEdge = MainGame.me.gui_cam.ScreenToWorldPoint(new Vector3(-100, Screen.height / 2, 0));
            Vector3 containerLeftEdge = gerryContainer.transform.parent.InverseTransformPoint(screenLeftEdge);
            
            // Start even further left to be safe
            Vector3 startPosition = new Vector3(containerLeftEdge.x - 100, -130, 0);
            gerryContainer.transform.localPosition = startPosition;
            
            CoopMod.Logger.LogInfo($"[GERRY] Starting hop from {startPosition} to {normalPosition}");
            
            // Hop onto screen
            float hopDistance = 80f; // Distance per hop
            Vector3 currentPos = startPosition;
            
            while (currentPos.x < normalPosition.x)
            {
                // Calculate next hop target
                Vector3 targetPos = currentPos + new Vector3(hopDistance, 0, 0);
                if (targetPos.x > normalPosition.x)
                {
                    targetPos.x = normalPosition.x; // Don't overshoot
                }
                
                // Perform one hop
                float elapsed = 0f;
                while (elapsed < hopDuration)
                {
                    elapsed += Time.deltaTime;
                    float t = elapsed / hopDuration;
                    
                    // Smooth horizontal movement
                    Vector3 pos = Vector3.Lerp(currentPos, targetPos, t);
                    
                    // Arc for vertical movement (parabola)
                    float heightOffset = hopHeight * Mathf.Sin(t * Mathf.PI);
                    pos.y += heightOffset;
                    
                    gerryContainer.transform.localPosition = pos;
                    yield return null;
                }
                
                currentPos = targetPos;
                gerryContainer.transform.localPosition = currentPos;
            }
            
            CoopMod.Logger.LogInfo("Gerry has arrived on screen!");
            
            // Now start hopping in place
            isHopping = true;
            hoppingCoroutine = StartCoroutine(HopInPlace());
            
            // Show speech bubble after arriving
            StartCoroutine(ShowSpeechBubble(0.3f));
        }

        /// <summary>
        /// Performs a single hop animation with arc.
        /// </summary>
        private System.Collections.IEnumerator SingleHop(Vector3 start, Vector3 end)
        {
            float elapsed = 0f;
            
            while (elapsed < hopDuration)
            {
                elapsed += UnityEngine.Time.deltaTime;
                float t = elapsed / hopDuration;
                
                // Smooth horizontal movement
                Vector3 pos = Vector3.Lerp(start, end, t);
                
                // Arc for vertical movement (parabola) - small hops
                float heightOffset = (hopHeight * 0.01f) * Mathf.Sin(t * Mathf.PI);
                pos.y += heightOffset;
                
                gerryWGO.transform.position = pos;
                yield return null;
            }
            
            gerryWGO.transform.position = end;
        }

        /// <summary>
        /// Makes Gerry hop continuously in place while waiting for player response.
        /// Now animates the UI sprites container instead of the main container.
        /// </summary>
        private System.Collections.IEnumerator HopInPlace()
        {
            CoopMod.Logger.LogInfo("Gerry hopping in place...");
            
            Vector3 basePosition = gerrySpritesContainer.transform.localPosition;
            
            while (isHopping)
            {
                // Small hops in place
                float elapsed = 0f;
                while (elapsed < hopDuration && isHopping)
                {
                    elapsed += Time.deltaTime;
                    float t = elapsed / hopDuration;
                    
                    // Small vertical hop in UI space
                    float heightOffset = hopHeight * Mathf.Sin(t * Mathf.PI);
                    Vector3 pos = basePosition;
                    pos.y += heightOffset;
                    
                    gerrySpritesContainer.transform.localPosition = pos;
                    yield return null;
                }
                
                // Brief pause between hops
                gerrySpritesContainer.transform.localPosition = basePosition;
                yield return new WaitForSeconds(0.1f);
            }
        }

        /// <summary>
        /// Called when Gerry finishes hopping onto the screen.
        /// </summary>
        private void OnGerryAppeared()
        {
            CoopMod.Logger.LogInfo("Gerry appeared on screen - starting continuous hopping");

            // Start continuous hopping in place
            isHopping = true;
            hoppingCoroutine = StartCoroutine(HopInPlace());
        }

        /// <summary>
        /// Shows a speech bubble attached to Gerry after a delay, then hides it and shows
        /// the MultiAnswer dialogue options in the exact same location.
        /// </summary>
        private System.Collections.IEnumerator ShowSpeechBubble(float delay)
        {
            yield return new UnityEngine.WaitForSeconds(delay);

            string message = $"{currentInviterName} has invited you to their graveyard!";

            // Show the invitation message as a speech bubble with Gerry's voice (UI space)
            long speakerId = (long)gerryContainer.GetInstanceID();
            SpeechBubbleGUI.ShowMessage(
                speaker_id: speakerId,
                txt: message,
                link: bubbleCornerTransform,
                on_disappeared: null,
                show_to_left: false,
                use_world_cam: false,
                type: SpeechBubbleGUI.SpeechBubbleType.Talk,
                is_player: false,
                voice: SmartSpeechEngine.VoiceID.Skull  // Gerry's voice!
            );

            CoopMod.Logger.LogInfo($"Speech bubble shown: {message}");

            // Wait up to 1.5 seconds, but allow skipping with any click
            float elapsed = 0f;
            float maxWait = 1.5f;
            while (elapsed < maxWait)
            {
                // Check for any mouse click or key press to skip
                if (Input.GetMouseButtonDown(0) || Input.GetMouseButtonDown(1) || Input.anyKeyDown)
                {
                    CoopMod.Logger.LogInfo("Player clicked to skip speech bubble");
                    break;
                }
                elapsed += Time.deltaTime;
                yield return null;
            }

            // Hide the speech bubble (if present) before showing the dialogue options
            if (SpeechBubbleGUI.all.ContainsKey(speakerId))
            {
                SpeechBubbleGUI.all[speakerId].ForceHide(false);
                SpeechBubbleGUI.all.Remove(speakerId);
                CoopMod.Logger.LogInfo("Speech bubble hidden");
            }

            // Small delay to ensure the bubble object is removed from the UI
            yield return new UnityEngine.WaitForSeconds(0.1f);

            // Show the Accept/Decline options using the MultiAnswerGUI at the same link
            ShowDialogueOptions();
        }

        /// <summary>
        /// Shows dialogue options (Accept/Decline) using the game's MultiAnswerGUI system.
        /// The invitation message is shown as part of the dialogue options.
        /// </summary>
        private void ShowDialogueOptions()
        {
            try
            {
                CoopMod.Logger.LogInfo("Showing dialogue options (Accept/Decline)...");

                // Update dialogue corner to match current bubble corner position
                // Convert from UI space to world space for MultiAnswerGUI
                Vector3 screenPos = MainGame.me.gui_cam.WorldToScreenPoint(bubbleCornerTransform.position);
                // Use a fixed Z depth near the camera instead of -4000
                screenPos.z = 10f; // Close to the camera
                Vector3 worldPos = MainGame.me.world_cam.ScreenToWorldPoint(screenPos);
                dialogueCornerTransform.position = worldPos;
                
                CoopMod.Logger.LogInfo($"Updated dialogue corner - screen: {screenPos}, world: {worldPos}");

                // Create answer options using the game's AnswerVisualData structure
                var answers = new System.Collections.Generic.List<AnswerVisualData>();
                
                // Create Accept option
                var acceptAnswer = new AnswerVisualData();
                acceptAnswer.id = "Accept";
                acceptAnswer.translation = "Accept";
                answers.Add(acceptAnswer);

                // Create Decline option
                var declineAnswer = new AnswerVisualData();
                declineAnswer.id = "Decline";
                declineAnswer.translation = "Decline";
                answers.Add(declineAnswer);

                // Show the multi-answer dialogue with cleanup callback
                MultiAnswerGUI.ShowAnswers(
                    answers: answers,
                    link: dialogueCornerTransform, // World-space transform
                    on_chosen: OnDialogueOptionChosen,
                    show_to_left: false,
                    on_disappeared: OnDialogueDisappeared, // Clean up when dialogue closes
                    talker: null
                );

                CoopMod.Logger.LogInfo("Dialogue options shown successfully");
            }
            catch (System.Exception ex)
            {
                CoopMod.Logger.LogError($"Error showing dialogue options: {ex.Message}\n{ex.StackTrace}");
            }
        }

        /// <summary>
        /// Called when the dialogue disappears (either by selection or being closed).
        /// </summary>
        private void OnDialogueDisappeared()
        {
            CoopMod.Logger.LogInfo("Dialogue disappeared");
        }

        /// <summary>
        /// Called when player chooses a dialogue option.
        /// </summary>
        private void OnDialogueOptionChosen(string chosenId)
        {
            CoopMod.Logger.LogInfo($"Player chose dialogue option: {chosenId}");

            if (chosenId == "Accept")
            {
                OnAcceptPressed();
            }
            else if (chosenId == "Decline")
            {
                OnDeclinePressed();
            }
        }

        /// <summary>
        /// Called when player accepts the invitation.
        /// </summary>
        public void OnAcceptPressed()
        {
            CoopMod.Logger.LogInfo($"Player accepted invitation from {currentInviterName}");
            
            // Hide the dialogue immediately
            MultiAnswerGUI.HideAnyctive();
            
            // Stop the in-place hopping
            isHopping = false;
            if (hoppingCoroutine != null)
            {
                StopCoroutine(hoppingCoroutine);
                hoppingCoroutine = null;
            }
            
            // Show "Joining game" message
            long speakerId = (long)gerryContainer.GetInstanceID();
            SpeechBubbleGUI.ShowMessage(
                speaker_id: speakerId,
                txt: "Joining game...",
                link: bubbleCornerTransform,
                on_disappeared: null,
                show_to_left: false,
                use_world_cam: false,
                type: SpeechBubbleGUI.SpeechBubbleType.Talk,
                is_player: false,
                voice: SmartSpeechEngine.VoiceID.Skull
            );
            
            CoopMod.Logger.LogInfo("Showing 'Joining game...' message");
            
            // Wait a moment for the message to display, then proceed
            StartCoroutine(JoinGameAfterDelay());
        }
        
        /// <summary>
        /// Waits for the "Joining game" message to display, then executes the join callback.
        /// </summary>
        private System.Collections.IEnumerator JoinGameAfterDelay()
        {
            // Let the player see the message for a moment
            yield return new UnityEngine.WaitForSeconds(1.5f);
            
            // Clean up Gerry
            HideGerry();
            
            // Execute the accept callback
            onAccept?.Invoke();
        }

        /// <summary>
        /// Called when player declines the invitation.
        /// </summary>
        public void OnDeclinePressed()
        {
            CoopMod.Logger.LogInfo($"Player declined invitation from {currentInviterName}");
            
            // Hide the dialogue immediately
            MultiAnswerGUI.HideAnyctive();
            
            // Make Gerry hop off-screen to the left
            StartCoroutine(HopOffScreen());
            
            onDecline?.Invoke();
        }

        /// <summary>
        /// Makes Gerry hop off-screen to the left, then hides him.
        /// </summary>
        private System.Collections.IEnumerator HopOffScreen()
        {
            CoopMod.Logger.LogInfo("Gerry hopping off-screen...");
            
            // Stop the in-place hopping
            isHopping = false;
            if (hoppingCoroutine != null)
            {
                StopCoroutine(hoppingCoroutine);
                hoppingCoroutine = null;
            }
            
            // Get screen dimensions to know when Gerry is fully off-screen
            // We need to account for UI scaling - convert screen space to UI space
            Vector3 screenPos = MainGame.me.gui_cam.WorldToScreenPoint(gerryContainer.transform.position);
            float currentScreenX = screenPos.x;
            
            // Hop off to the left until X position is negative (off-screen left edge)
            // Each hop moves Gerry left by hopHeight distance
            float hopDistance = 80f; // Distance to move left per hop
            
            while (currentScreenX > -100f) // Keep hopping until well off-screen
            {
                // Calculate new target position
                Vector3 startPos = gerryContainer.transform.localPosition;
                Vector3 endPos = startPos + new Vector3(-hopDistance, 0, 0);
                
                // Perform one hop to the left
                float elapsed = 0f;
                while (elapsed < hopDuration)
                {
                    elapsed += Time.deltaTime;
                    float t = elapsed / hopDuration;
                    
                    // Smooth horizontal movement
                    Vector3 pos = Vector3.Lerp(startPos, endPos, t);
                    
                    // Arc for vertical movement (parabola)
                    float heightOffset = hopHeight * Mathf.Sin(t * Mathf.PI);
                    pos.y += heightOffset;
                    
                    gerryContainer.transform.localPosition = pos;
                    
                    // Update screen position for exit check
                    screenPos = MainGame.me.gui_cam.WorldToScreenPoint(gerryContainer.transform.position);
                    currentScreenX = screenPos.x;
                    
                    yield return null;
                }
                
                gerryContainer.transform.localPosition = endPos;
            }
            
            // Gerry is now off-screen, hide him
            CoopMod.Logger.LogInfo("Gerry has left the screen");
            isShowing = false;
            gerryContainer.SetActive(false);
            
            // Restore server browser visibility
            if (JoinGameGUI.Instance != null)
            {
                JoinGameGUI.Instance.SetServerBrowserVisible(true);
            }
        }

        /// <summary>
        /// Hides Gerry immediately.
        /// </summary>
        public void HideGerry()
        {
            if (!isShowing)
            {
                return;
            }

            CoopMod.Logger.LogInfo("Hiding Gerry and cleaning up dialogue");
            
            // Force hide any active MultiAnswerGUI dialogue
            MultiAnswerGUI.HideAnyctive();
            
            // Stop continuous hopping
            isHopping = false;
            if (hoppingCoroutine != null)
            {
                StopCoroutine(hoppingCoroutine);
                hoppingCoroutine = null;
            }
            
            isShowing = false;
            gerryContainer.SetActive(false);
            
            // Restore server browser visibility
            if (JoinGameGUI.Instance != null)
            {
                JoinGameGUI.Instance.SetServerBrowserVisible(true);
            }
            
            CoopMod.Logger.LogInfo("Gerry hidden");
        }

        private void OnDestroy()
        {
            if (gerryContainer != null)
            {
                Destroy(gerryContainer);
            }
        }
    }
}
