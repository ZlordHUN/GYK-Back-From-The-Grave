using UnityEngine;
using BepInEx;

namespace GraveyardKeeperCoop.UI
{
    /// <summary>
    /// Manages Gerry the Skull appearing on the Join Game screen to provide feedback on join attempts.
    /// Gerry hops in FROM THE RIGHT when the player types an IP address.
    /// Shows status messages for joining attempts (attempting, success, failure with reasons).
    /// </summary>
    public class GerryJoinNotifier : MonoBehaviour
    {
    // Configuration
    private float hopDuration = 0.5f;        // Duration of each hop
    private float hopHeight = 30f;           // Height of hop arc in UI space
    private float hopDistance = 80f;         // Horizontal distance per hop in UI space
        
        // References
        private WorldGameObject gerryWGO;        // Our spawned Gerry instance
        private GameObject gerryContainer;       // Container in UI space
        private GameObject gerrySpritesContainer; // Inner container for Gerry's sprites (this one hops)
        private Transform bubbleCornerTransform; // For speech bubbles (stays still)
        private bool isShowing = false;
        private bool isHopping = false;
        private Coroutine hoppingCoroutine;
        
        // State
        private bool hasAppearedOnce = false; // Track if Gerry has appeared at least once

        /// <summary>
        /// Initializes Gerry by spawning our own WorldGameObject.
        /// Gerry's obj_id is "talking_skull".
        /// </summary>
        public void Initialize(Transform parent)
        {
            CoopMod.Logger.LogInfo("Initializing GerryJoinNotifier - spawning Gerry...");

            // Create a container for Gerry - position it beneath the Join/Back buttons
            gerryContainer = new GameObject("GerryJoinContainer");
            gerryContainer.transform.SetParent(parent, false);
            gerryContainer.transform.localPosition = new Vector3(0, -130, 0); // Same position as invite notifier
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

            gerryContainer.SetActive(false);
            CoopMod.Logger.LogInfo("GerryJoinNotifier initialized successfully");
        }

        /// <summary>
        /// Spawns Gerry as a WorldGameObject and creates UI sprite copies.
        /// </summary>
        private void SpawnGerry()
        {
            try
            {
                CoopMod.Logger.LogInfo("Spawning Gerry WorldGameObject for join notifier...");

                // Try to spawn Gerry's WGO
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
                            
                            // Create UI2DSprite copies for each SpriteRenderer
                            foreach (var sr in spriteRenderers)
                            {
                                if (sr.sprite != null)
                                {
                                    // Create a UI sprite gameobject
                                    GameObject uiSpriteGO = new GameObject($"UI_{sr.gameObject.name}");
                                    uiSpriteGO.transform.SetParent(gerrySpritesContainer.transform, false);
                                    uiSpriteGO.layer = 13; // UI layer
                                    
                                    // Add UI2DSprite component
                                    var ui2dSprite = uiSpriteGO.AddComponent<UI2DSprite>();
                                    ui2dSprite.sprite2D = sr.sprite;
                                    ui2dSprite.depth = 1000;
                                    ui2dSprite.MakePixelPerfect();
                                    
                                    // Position and scale
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
        /// Called when the player starts typing in the IP input field.
        /// Makes Gerry hop onto the screen FROM THE RIGHT.
        /// </summary>
        public void OnIPInputStarted()
        {
            if (hasAppearedOnce)
            {
                // Gerry has already appeared once, hop him back in from the right
                if (!isShowing)
                {
                    CoopMod.Logger.LogInfo("Gerry reappearing - hopping in from right again");
                    StartCoroutine(HopOntoScreenFromRight());
                }
                return;
            }

            if (isShowing)
            {
                CoopMod.Logger.LogInfo("Gerry is already showing");
                return;
            }

            CoopMod.Logger.LogInfo("Player started typing IP - Gerry hopping in from RIGHT");
            hasAppearedOnce = true;
            StartCoroutine(HopOntoScreenFromRight());
        }

        /// <summary>
        /// Makes Gerry hop onto the screen FROM THE RIGHT (opposite of invite notifier).
        /// </summary>
        private System.Collections.IEnumerator HopOntoScreenFromRight()
        {
            CoopMod.Logger.LogInfo("Gerry hopping onto screen from the RIGHT...");
            
            // Activate container but start off-screen
            gerryContainer.SetActive(true);
            isShowing = true;
            
            // Store the normal position
            Vector3 normalPosition = new Vector3(0, -130, 0);
            
            // Calculate starting position (off-screen RIGHT)
            Vector3 screenRightEdge = MainGame.me.gui_cam.ScreenToWorldPoint(new Vector3(Screen.width + 100, Screen.height / 2, 0));
            Vector3 containerRightEdge = gerryContainer.transform.parent.InverseTransformPoint(screenRightEdge);
            
            // Start even further right to be safe
            Vector3 startPosition = new Vector3(containerRightEdge.x + 100, -130, 0);
            gerryContainer.transform.localPosition = startPosition;
            
            CoopMod.Logger.LogInfo($"Starting hop from RIGHT at {startPosition} to {normalPosition}");
            
            // Hop onto screen (moving LEFT)
            Vector3 currentPos = startPosition;
            
            while (currentPos.x > normalPosition.x)
            {
                // Calculate next hop target (moving LEFT)
                Vector3 targetPos = currentPos + new Vector3(-hopDistance, 0, 0);
                if (targetPos.x < normalPosition.x)
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
            
            CoopMod.Logger.LogInfo("Gerry has arrived on screen from the RIGHT!");
            
            // Now start hopping in place
            isHopping = true;
            hoppingCoroutine = StartCoroutine(HopInPlace());
        }

        /// <summary>
        /// Makes Gerry hop continuously in place.
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
        /// Shows a message that the join attempt is starting.
        /// </summary>
        public void ShowJoiningMessage(string ipAddress)
        {
            if (!isShowing)
            {
                CoopMod.Logger.LogWarning("Cannot show joining message - Gerry is not visible");
                return;
            }

            CoopMod.Logger.LogInfo($"Showing joining message for IP: {ipAddress}");

            string message = $"Attempting to join {ipAddress}...";
            
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
        }

        /// <summary>
        /// Shows a success message when join succeeds.
        /// </summary>
        public void ShowJoinSuccessMessage()
        {
            if (!isShowing)
            {
                CoopMod.Logger.LogWarning("Cannot show success message - Gerry is not visible");
                return;
            }

            CoopMod.Logger.LogInfo("Showing join success message");

            string message = "Connected successfully! Welcome!";
            
            long speakerId = (long)gerryContainer.GetInstanceID();
            
            // Hide any existing speech bubble first
            if (SpeechBubbleGUI.all.ContainsKey(speakerId))
            {
                SpeechBubbleGUI.all[speakerId].ForceHide(false);
                SpeechBubbleGUI.all.Remove(speakerId);
            }
            
            SpeechBubbleGUI.ShowMessage(
                speaker_id: speakerId,
                txt: message,
                link: bubbleCornerTransform,
                on_disappeared: null,
                show_to_left: false,
                use_world_cam: false,
                type: SpeechBubbleGUI.SpeechBubbleType.Talk,
                is_player: false,
                voice: SmartSpeechEngine.VoiceID.Skull
            );
            
            // Hide Gerry after showing success message
            StartCoroutine(HideAfterDelay(2f));
        }

        /// <summary>
        /// Shows a failure message when join fails.
        /// </summary>
        public void ShowJoinFailureMessage(string reason)
        {
            if (!isShowing)
            {
                CoopMod.Logger.LogWarning("Cannot show failure message - Gerry is not visible");
                return;
            }

            CoopMod.Logger.LogInfo($"Showing join failure message: {reason}");

            string message = $"Connection failed: {reason}";
            
            long speakerId = (long)gerryContainer.GetInstanceID();
            
            // Hide any existing speech bubble first
            if (SpeechBubbleGUI.all.ContainsKey(speakerId))
            {
                SpeechBubbleGUI.all[speakerId].ForceHide(false);
                SpeechBubbleGUI.all.Remove(speakerId);
            }
            
            SpeechBubbleGUI.ShowMessage(
                speaker_id: speakerId,
                txt: message,
                link: bubbleCornerTransform,
                on_disappeared: null,
                show_to_left: false,
                use_world_cam: false,
                type: SpeechBubbleGUI.SpeechBubbleType.Talk,
                is_player: false,
                voice: SmartSpeechEngine.VoiceID.Skull
            );
            
            // Keep Gerry visible after failure so player can read the message
            // Don't hide automatically - let them try again
        }

        /// <summary>
        /// Hides Gerry after a delay.
        /// </summary>
        private System.Collections.IEnumerator HideAfterDelay(float delay)
        {
            yield return new WaitForSeconds(delay);
            HideGerry();
        }

        /// <summary>
        /// Makes Gerry exit to the right (animated) when input is cleared.
        /// </summary>
        public void ExitRight()
        {
            if (!isShowing)
            {
                return;
            }

            CoopMod.Logger.LogInfo("Gerry exiting to the right...");
            
            // Stop continuous hopping
            isHopping = false;
            if (hoppingCoroutine != null)
            {
                StopCoroutine(hoppingCoroutine);
                hoppingCoroutine = null;
            }
            
            // Hide any speech bubbles
            long speakerId = (long)gerryContainer.GetInstanceID();
            if (SpeechBubbleGUI.all.ContainsKey(speakerId))
            {
                SpeechBubbleGUI.all[speakerId].ForceHide(false);
                SpeechBubbleGUI.all.Remove(speakerId);
            }
            
            // Start exit animation
            StartCoroutine(ExitRightAnimation());
        }

        /// <summary>
        /// Animates Gerry hopping off screen to the right.
        /// </summary>
        private System.Collections.IEnumerator ExitRightAnimation()
        {
            CoopMod.Logger.LogInfo("Starting exit right animation");
            
            Vector3 currentPos = gerryContainer.transform.localPosition;
            Vector3 offScreenPos = new Vector3(1500, currentPos.y, currentPos.z); // Far right off screen
            
            // Calculate number of hops needed
            float distanceToTravel = offScreenPos.x - currentPos.x;
            int numHops = Mathf.CeilToInt(distanceToTravel / hopDistance);
            
            for (int i = 0; i < numHops; i++)
            {
                // Calculate next hop target (moving RIGHT)
                Vector3 targetPos = currentPos + new Vector3(hopDistance, 0, 0);
                if (targetPos.x > offScreenPos.x)
                {
                    targetPos.x = offScreenPos.x; // Don't overshoot
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
            
            CoopMod.Logger.LogInfo("Gerry has left the screen to the right");
            isShowing = false;
            gerryContainer.SetActive(false);
        }

        /// <summary>
        /// Hides Gerry immediately without animation.
        /// </summary>
        public void HideGerry()
        {
            if (!isShowing)
            {
                return;
            }

            CoopMod.Logger.LogInfo("Hiding Gerry join notifier");
            
            // Stop continuous hopping
            isHopping = false;
            if (hoppingCoroutine != null)
            {
                StopCoroutine(hoppingCoroutine);
                hoppingCoroutine = null;
            }
            
            // Hide any speech bubbles
            long speakerId = (long)gerryContainer.GetInstanceID();
            if (SpeechBubbleGUI.all.ContainsKey(speakerId))
            {
                SpeechBubbleGUI.all[speakerId].ForceHide(false);
                SpeechBubbleGUI.all.Remove(speakerId);
            }
            
            isShowing = false;
            gerryContainer.SetActive(false);
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
