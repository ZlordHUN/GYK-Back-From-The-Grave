using System;
using UnityEngine;

namespace GraveyardKeeperCoop.UI
{
    /// <summary>
    /// Shows the game's built-in DialogGUI (same window used for host-disconnect popups)
    /// with label_2 repurposed as a live text input. Pre-populates the buffer with a
    /// default save name and returns the edited name via <see cref="Action{string}"/>.
    /// </summary>
    public class RenameSaveDialog : MonoBehaviour
    {
        private const int MAX_LENGTH = 48;
        private const float CARET_BLINK_INTERVAL = 0.5f;
        private const float BACKSPACE_REPEAT_DELAY = 0.5f;
        private const float BACKSPACE_REPEAT_RATE = 0.05f;

        // Marker passed to DialogGUI so label_2 becomes active. Overwritten on first Update.
        private const string INPUT_PLACEHOLDER = "\u00A0";

        private static RenameSaveDialog activeInstance;

        private DialogGUI dialog;
        private UILabel inputLabel;
        private string buffer = string.Empty;
        private bool confirmed;
        private Action<string> onConfirm;
        private Action onCancel;

        // Caret blink
        private float caretTimer;
        private bool showCaret = true;

        // Backspace key repeat
        private float backspaceHoldTime;
        private float nextBackspaceTime;

        /// <summary>
        /// Open the rename dialog. Only one instance is allowed at a time.
        /// </summary>
        public static void Show(string defaultName, Action<string> onConfirm, Action onCancel = null)
        {
            if (activeInstance != null)
            {
                CoopMod.Logger.LogWarning("[RenameSaveDialog] Another rename dialog is already open - ignoring new request");
                return;
            }

            var dialog = GUIElements.me?.dialog;
            if (dialog == null)
            {
                CoopMod.Logger.LogWarning("[RenameSaveDialog] DialogGUI not available - using default name");
                onConfirm?.Invoke(defaultName ?? string.Empty);
                return;
            }

            var controller = dialog.gameObject.AddComponent<RenameSaveDialog>();
            controller.dialog = dialog;
            controller.buffer = defaultName ?? string.Empty;
            controller.onConfirm = onConfirm;
            controller.onCancel = onCancel;
            controller.OpenDialog();
            activeInstance = controller;
        }

        private void OpenDialog()
        {
            try
            {
                string title = GJL.L("Name your save");
                if (string.IsNullOrEmpty(title) || title == "Name your save")
                    title = "Name your save";

                string okLabel = GJL.L("ok");
                if (string.IsNullOrEmpty(okLabel)) okLabel = "OK";
                string cancelLabel = GJL.L("Cancel");
                if (string.IsNullOrEmpty(cancelLabel) || cancelLabel == "Cancel") cancelLabel = "Cancel";

                dialog.Open(
                    title,
                    okLabel,
                    new GJCommons.VoidDelegate(OnOk),
                    cancelLabel,
                    new GJCommons.VoidDelegate(OnCancel),
                    null,
                    GameKey.Select,
                    GameKey.Back,
                    INPUT_PLACEHOLDER,
                    false,
                    string.Empty);

                inputLabel = dialog.label_2;
                if (inputLabel != null)
                {
                    inputLabel.gameObject.SetActive(true);
                    inputLabel.color = Color.white;
                }
                RefreshLabel();
            }
            catch (Exception ex)
            {
                CoopMod.Logger.LogError($"[RenameSaveDialog] Failed to open dialog: {ex}");
                Cleanup();
                onConfirm?.Invoke(buffer);
            }
        }

        private void OnOk()
        {
            if (confirmed) return; // Guard against double-fire
            confirmed = true;
            try
            {
                string finalName = buffer.Trim();
                if (string.IsNullOrEmpty(finalName))
                    finalName = "Manual Save";
                CoopMod.Logger.LogInfo($"[RenameSaveDialog] Confirmed with name: '{finalName}'");
                onConfirm?.Invoke(finalName);
            }
            catch (Exception ex)
            {
                CoopMod.Logger.LogError($"[RenameSaveDialog] Confirm callback threw: {ex}");
            }
            finally
            {
                Cleanup();
            }
        }

        private void OnCancel()
        {
            if (confirmed) return;
            confirmed = true;
            try
            {
                CoopMod.Logger.LogInfo("[RenameSaveDialog] Cancelled");
                onCancel?.Invoke();
            }
            catch (Exception ex)
            {
                CoopMod.Logger.LogError($"[RenameSaveDialog] Cancel callback threw: {ex}");
            }
            finally
            {
                Cleanup();
            }
        }

        private void Cleanup()
        {
            if (activeInstance == this) activeInstance = null;
            Destroy(this);
        }

        private void Update()
        {
            if (dialog == null || !dialog.is_shown)
                return;

            HandleTextInput();

            caretTimer += Time.unscaledDeltaTime;
            if (caretTimer >= CARET_BLINK_INTERVAL)
            {
                caretTimer = 0f;
                showCaret = !showCaret;
                RefreshLabel();
            }
        }

        private void HandleTextInput()
        {
            bool changed = false;

            // Printable characters from Input.inputString
            string input = Input.inputString;
            if (!string.IsNullOrEmpty(input))
            {
                for (int i = 0; i < input.Length; i++)
                {
                    char c = input[i];
                    // Only accept printable ASCII; ignore backspace/return/escape/tab
                    // (those are handled via Input.GetKey below or by DialogGUI's hotkeys).
                    if (c >= 32 && c <= 126 && buffer.Length < MAX_LENGTH)
                    {
                        buffer += c;
                        changed = true;
                    }
                }
            }

            // Backspace with key-repeat (hold to delete)
            if (Input.GetKey(KeyCode.Backspace))
            {
                if (backspaceHoldTime == 0f)
                {
                    // Initial press - delete one char immediately
                    if (buffer.Length > 0)
                    {
                        buffer = buffer.Substring(0, buffer.Length - 1);
                        changed = true;
                    }
                    nextBackspaceTime = Time.unscaledTime + BACKSPACE_REPEAT_DELAY;
                }
                else if (Time.unscaledTime >= nextBackspaceTime)
                {
                    if (buffer.Length > 0)
                    {
                        buffer = buffer.Substring(0, buffer.Length - 1);
                        changed = true;
                    }
                    nextBackspaceTime = Time.unscaledTime + BACKSPACE_REPEAT_RATE;
                }
                backspaceHoldTime += Time.unscaledDeltaTime;
            }
            else
            {
                backspaceHoldTime = 0f;
                nextBackspaceTime = 0f;
            }

            if (changed)
            {
                // Reset caret to visible on every edit so the user sees responsiveness
                showCaret = true;
                caretTimer = 0f;
                RefreshLabel();
            }
        }

        private void RefreshLabel()
        {
            if (inputLabel == null) return;
            string caret = showCaret ? "|" : " ";
            // Use [c][/c] NGUI color tags? Skip - keep it simple.
            inputLabel.text = buffer + caret;
        }

        private void OnDestroy()
        {
            if (activeInstance == this) activeInstance = null;
        }
    }
}
