using System;
using System.Collections.Generic;
using System.Reflection;
using GraveyardKeeperCoop.Network;
using GraveyardKeeperCoop.UI;
using HarmonyLib;
using Steamworks;
using UnityEngine;

namespace GraveyardKeeperCoop.Multiplayer
{
    /// <summary>
    /// Each fishing player publishes their presentation. Observers animate only that
    /// player's ghost and a passive copy of the minigame; FishingGUI gameplay remains
    /// exclusively on the initiating computer.
    /// </summary>
    [DefaultExecutionOrder(500)]
    public sealed class FishingPresentationSync : SyncBehaviour
    {
        public static FishingPresentationSync Instance => GetInstance<FishingPresentationSync>();
        private const float SendInterval = 0.05f;
        private const float RemoteTimeout = 3f;
        private static readonly FieldInfo DistanceField = AccessTools.Field(typeof(FishingGUI), "_throwing_distance_int");
        private static readonly FieldInfo SuccessField = AccessTools.Field(typeof(FishingGUI), "is_success_fishing");
        private static readonly FieldInfo FishField = AccessTools.Field(typeof(FishingGUI), "_fish");
        private static readonly FieldInfo AnimatorField = AccessTools.Field(typeof(CustomNetworkAnimatorSync), "_animator");

        private sealed class RemoteFishing
        {
            public FishingPresentationState State;
            public float ReceivedAt;
            public FishingSpectatorView View = new FishingSpectatorView();
            public FishingUiInterpolator Ui = new FishingUiInterpolator();
            public PlayerComponent Player;
            public uint AppliedSequence;
            public bool HasApplied;
            public float OriginalScaleX;
            public Animator ControlledAnimator;
            public float OriginalAnimatorSpeed;
            public string FishIcon = string.Empty;
            public float NextProgressLogAt;
        }

        private readonly Dictionary<ulong, RemoteFishing> remotes = new Dictionary<ulong, RemoteFishing>();
        private readonly Dictionary<ulong, uint> lastSequences = new Dictionary<ulong, uint>();
        private readonly List<ulong> expired = new List<ulong>();
        private FishingPresentationState lastLocal;
        private float nextSendAt;
        private uint localSequence;
        private float nextErrorLogAt;
        private float nextLocalProgressLogAt;
        protected override string LogPrefix => "[FishingPresentation]";

        protected override void SubscribeEvents()
        {
            if (SteamP2PManager.Instance == null) return;
            SteamP2PManager.Instance.OnFishingPresentationReceived -= Receive;
            SteamP2PManager.Instance.OnFishingPresentationReceived += Receive;
        }

        protected override void UnsubscribeEvents()
        {
            if (SteamP2PManager.Instance != null)
                SteamP2PManager.Instance.OnFishingPresentationReceived -= Receive;
            ClearRemotes();
        }

        protected override void OnSyncEnabled()
        {
            lastLocal = null;
            nextSendAt = 0f;
            nextLocalProgressLogAt = 0f;
            lastSequences.Clear();
            ClearRemotes();
        }

        protected override void OnSyncDisabled()
        {
            lastLocal = null;
            lastSequences.Clear();
            ClearRemotes();
        }

        internal bool IsRemoteFishing(CSteamID peer)
        {
            return IsSyncEnabled && IsOnline &&
                remotes.TryGetValue(peer.m_SteamID, out RemoteFishing remote) &&
                Time.realtimeSinceStartup - remote.ReceivedAt <= RemoteTimeout;
        }

        internal void OnPeerLeftLobby(CSteamID peer)
        {
            RemoveRemote(peer.m_SteamID, "peer left lobby");
            // A restarted peer begins a new sequence. Ordinary stops keep their
            // tombstone, but an authenticated lobby departure ends that session.
            lastSequences.Remove(peer.m_SteamID);
        }

        private void LateUpdate()
        {
            if (!IsSyncEnabled || !IsOnline) return;
            try
            {
                PublishLocal();
                float now = Time.realtimeSinceStartup;
                expired.Clear();
                foreach (var pair in remotes)
                {
                    if (!MainGame.game_started || now - pair.Value.ReceivedAt > RemoteTimeout ||
                        !OnlineCoopManager.Instance.IsRemotePlayer(new CSteamID(pair.Key)))
                    {
                        expired.Add(pair.Key);
                        continue;
                    }
                    RenderRemote(new CSteamID(pair.Key), pair.Value);
                }
                foreach (ulong id in expired)
                {
                    RemoteFishing remote = remotes[id];
                    float age = now - remote.ReceivedAt;
                    if (MainGame.game_started && age > RemoteTimeout)
                        CoopMod.Logger.LogWarning($"{LogPrefix} Remote {id} timed out after {age:F2}s without a snapshot (sequence={remote.State.Sequence}, phase={(FishingGUI.FishingState)remote.State.Phase})");
                    RemoveRemote(id, age > RemoteTimeout ? "snapshot timeout" : "world/peer unavailable");
                }
            }
            catch (Exception ex)
            {
                if (Time.realtimeSinceStartup >= nextErrorLogAt)
                {
                    nextErrorLogAt = Time.realtimeSinceStartup + 5f;
                    CoopMod.Logger.LogWarning($"{LogPrefix} Presentation update failed: {ex}");
                }
            }
        }

        private void PublishLocal()
        {
            FishingGUI gui = GUIElements.me?.fishing;
            bool active = MainGame.game_started && MainGame.me?.player != null &&
                gui != null && gui.is_shown && gui.gameObject.activeInHierarchy &&
                gui.state != FishingGUI.FishingState.None;
            if (!active)
            {
                if (lastLocal?.Active == true)
                    Send(new FishingPresentationState(), true);
                return;
            }

            var state = new FishingPresentationState
            {
                Active = true,
                Phase = (byte)gui.state,
                FacingRight = gui.is_to_right,
                // Only the committed cast distance is public. The live depth picker
                // and bait/depth selection widgets never leave the owner's GUI.
                Distance = gui.state >= FishingGUI.FishingState.WaitingForBite
                    ? (byte)Mathf.Clamp((int)(DistanceField?.GetValue(gui) ?? 0), 0, 3) : (byte)0,
                CanTakeOut = gui.can_take_out,
                Success = gui.state == FishingGUI.FishingState.TakingOut &&
                    (bool)(SuccessField?.GetValue(gui) ?? false)
            };
            if (state.Success)
                state.FishItemId = (FishField?.GetValue(gui) as Item)?.id ?? string.Empty;

            if (gui.state == FishingGUI.FishingState.Pulling && gui.process_back != null)
            {
                float height = Mathf.Max(1f, gui.process_back.height);
                state.RodPosition = gui.fishing_rod != null ? gui.fishing_rod.transform.localPosition.y / height : 0f;
                state.RodSize = gui.fishing_rod != null ? gui.fishing_rod.height / height : 0f;
                state.FishPosition = gui.fish_tf != null ? gui.fish_tf.localPosition.y / height : 0f;
                state.Progress = gui.progress_bar != null ? gui.progress_bar.value : 0f;
            }

            Animator animator = GetAnimator(MainGame.me.player);
            if (animator != null && animator.isActiveAndEnabled && animator.runtimeAnimatorController != null)
            {
                // Publish the pose actually playing, not a queued transition target.
                AnimatorStateInfo info = animator.GetCurrentAnimatorStateInfo(0);
                state.AnimationHash = info.fullPathHash;
                state.AnimationTime = Mathf.Max(0f, info.normalizedTime);
                state.AnimationLength = Mathf.Clamp(info.length, 0f, 120f);
                state.AnimationSpeed = Mathf.Clamp(animator.speed * info.speed * info.speedMultiplier, 0f, 10f);
            }

            bool changedPhase = lastLocal == null || !lastLocal.Active ||
                lastLocal.Phase != state.Phase || lastLocal.AnimationHash != state.AnimationHash ||
                lastLocal.CanTakeOut != state.CanTakeOut || lastLocal.Success != state.Success;
            if (changedPhase || Time.realtimeSinceStartup >= nextSendAt)
                Send(state, changedPhase);
        }

        private void Send(FishingPresentationState state, bool reliable)
        {
            state.Sequence = ++localSequence;
            byte[] payload = FishingPresentationCodec.Serialize(state);
            if (payload == null) return;
            SteamP2PManager.Instance?.BroadcastFishingPresentation(payload, reliable);
            if (lastLocal == null || lastLocal.Phase != state.Phase || lastLocal.Active != state.Active)
                CoopMod.Logger.LogInfo($"{LogPrefix} Local phase={(FishingGUI.FishingState)state.Phase}, active={state.Active}");
            if (state.Phase == (byte)FishingGUI.FishingState.Pulling &&
                (lastLocal?.Phase != state.Phase || Time.realtimeSinceStartup >= nextLocalProgressLogAt))
            {
                nextLocalProgressLogAt = Time.realtimeSinceStartup + 1f;
                LogProgress("Local", state);
            }
            lastLocal = state;
            // Advance the scheduled tick instead of adding an interval to each
            // late frame (which turns a 20 Hz target into roughly 16 Hz at 60 fps).
            nextSendAt += SendInterval;
            if (reliable || nextSendAt <= Time.realtimeSinceStartup)
                nextSendAt = Time.realtimeSinceStartup + SendInterval;
        }

        private void Receive(CSteamID sender, byte[] payload)
        {
            if (!IsSyncEnabled || !IsOnline || !MainGame.game_started ||
                !OnlineCoopManager.Instance.IsRemotePlayer(sender) ||
                SteamLobbyManager.Instance?.IsPeerCompatibilityValidated(sender) != true ||
                !FishingPresentationCodec.TryDeserialize(payload, out FishingPresentationState state))
                return;
            if (lastSequences.TryGetValue(sender.m_SteamID, out uint previous) &&
                unchecked((int)(state.Sequence - previous)) <= 0)
                return;
            // Keep the stop sequence after removing a view: late unreliable frames
            // must never resurrect a minigame that has already ended.
            lastSequences[sender.m_SteamID] = state.Sequence;
            if (!state.Active)
            {
                RemoveRemote(sender.m_SteamID, "owner stopped");
                return;
            }
            if (!remotes.TryGetValue(sender.m_SteamID, out RemoteFishing remote))
            {
                remote = new RemoteFishing();
                remotes.Add(sender.m_SteamID, remote);
            }
            if (remote.State == null || remote.State.Phase != state.Phase)
            {
                CoopMod.Logger.LogInfo($"{LogPrefix} Remote {sender} phase={(FishingGUI.FishingState)state.Phase}");
                remote.NextProgressLogAt = 0f;
                remote.Ui.Reset();
            }
            remote.State = state;
            remote.ReceivedAt = Time.realtimeSinceStartup;
            if (state.Phase == (byte)FishingGUI.FishingState.Pulling)
                remote.Ui.Push(remote.ReceivedAt, GetUiFrame(state));
            if (state.Phase == (byte)FishingGUI.FishingState.Pulling &&
                remote.ReceivedAt >= remote.NextProgressLogAt)
            {
                remote.NextProgressLogAt = remote.ReceivedAt + 1f;
                LogProgress($"Remote {sender}", state);
            }
        }

        private static void LogProgress(string owner, FishingPresentationState state)
        {
            CoopMod.Logger.LogInfo($"[FishingPresentation] {owner} sample sequence={state.Sequence}, rod={state.RodPosition:F3}, fish={state.FishPosition:F3}, progress={state.Progress:F3}");
        }

        private static FishingUiFrame GetUiFrame(FishingPresentationState state)
        {
            return new FishingUiFrame
            {
                RodPosition = state.RodPosition,
                FishPosition = state.FishPosition,
                RodSize = state.RodSize,
                Progress = state.Progress
            };
        }

        private static Animator GetAnimator(WorldGameObject actor)
        {
            CustomNetworkAnimatorSync wrapper = actor?.components?.animator;
            return wrapper != null ? AnimatorField?.GetValue(wrapper) as Animator : null;
        }

        private static void RenderRemote(CSteamID sender, RemoteFishing remote)
        {
            PlayerComponent player = OnlineCoopManager.Instance.GetRemotePlayerComponent(sender);
            WorldGameObject actor = player?.wgo;
            if (actor == null || !actor.gameObject.activeInHierarchy)
            {
                remote.View.Hide();
                RestoreAnimator(remote);
                return;
            }
            if (remote.Player != player)
            {
                RestoreAnimator(remote);
                remote.Player = player;
                remote.OriginalScaleX = actor.transform.localScale.x;
                remote.HasApplied = false;
                remote.Ui.Reset();
                if (remote.State.Phase == (byte)FishingGUI.FishingState.Pulling)
                    remote.Ui.Push(remote.ReceivedAt, GetUiFrame(remote.State));
            }

            FishingPresentationState state = remote.State;
            BaseCharacterComponent character = actor.components?.character;
            if (character != null && character.anim_state != CharAnimState.Fishing)
                character.SetAnimationState(CharAnimState.Fishing, ItemDefinition.ItemType.None);
            Vector3 scale = actor.transform.localScale;
            scale.x = Mathf.Abs(remote.OriginalScaleX) * (state.FacingRight ? -1f : 1f);
            actor.transform.localScale = scale;

            Animator animator = GetAnimator(actor);
            bool newSample = !remote.HasApplied || remote.AppliedSequence != state.Sequence;
            if (animator != null && animator.isActiveAndEnabled && animator.runtimeAnimatorController != null)
            {
                actor.components.animator.SetInteger("global_state", (int)CharAnimState.Fishing);
                actor.components.animator.SetInteger("fishing_state", state.Phase);
                actor.components.animator.SetFloat("fishing_distance", state.Distance);
                if (state.AnimationHash != 0 && animator.HasState(0, state.AnimationHash))
                {
                    if (remote.ControlledAnimator != animator)
                    {
                        RestoreAnimator(remote);
                        remote.ControlledAnimator = animator;
                        remote.OriginalAnimatorSpeed = animator.speed;
                        // This ghost uses owner poses rather than local input triggers.
                        animator.ResetTrigger("start_fishing");
                        animator.ResetTrigger("on_fishing_throw");
                    }

                    // Pause autonomous state-machine time, then evaluate the owner's
                    // pose on EVERY render frame after generic player visual writes.
                    // Native Throwing/swing states can auto-exit even when marked
                    // looping, so prediction must not cross a cycle boundary.
                    animator.speed = 0f;
                    float time = FishingAnimationPlayback.ProjectNormalizedTime(
                        state.AnimationTime, state.AnimationLength, state.AnimationSpeed,
                        Time.realtimeSinceStartup - remote.ReceivedAt);
                    animator.Play(state.AnimationHash, 0, time);
                    animator.Update(0f);
                }
                else
                    RestoreAnimator(remote);
            }
            else
                RestoreAnimator(remote);

            string fishIcon = state.Success && !string.IsNullOrEmpty(state.FishItemId)
                ? state.FishItemId : string.Empty;
            if (newSample || remote.FishIcon != fishIcon)
            {
                ItemDefinition definition = string.IsNullOrEmpty(fishIcon) ? null :
                    GameBalance.me?.GetDataOrNull<ItemDefinition>(fishIcon);
                if (player.fish != null)
                    player.fish.sprite = definition == null ? null :
                        EasySpritesCollection.GetSprite(new Item(fishIcon, 1).GetIcon(), false, string.Empty);
                if (player.fishadow != null)
                    player.fishadow.sprite = definition == null ? null :
                        EasySpritesCollection.GetSprite("fish_shadow", false, string.Empty);
                remote.FishIcon = fishIcon;
            }
            remote.AppliedSequence = state.Sequence;
            remote.HasApplied = true;

            if (state.Phase == (byte)FishingGUI.FishingState.Pulling)
            {
                // Only continuous UI values are delayed; entering/exiting fishing
                // and catch outcomes always follow the newest authoritative phase.
                if (!remote.Ui.TrySample(Time.realtimeSinceStartup, out FishingUiFrame frame))
                    frame = GetUiFrame(state);
                remote.View.Render(actor, frame.RodPosition, frame.FishPosition,
                    frame.RodSize, frame.Progress, state.FacingRight);
            }
            else
                remote.View.Hide();
        }

        private void RemoveRemote(ulong id, string reason = "session reset")
        {
            if (!remotes.TryGetValue(id, out RemoteFishing remote)) return;
            remotes.Remove(id);
            remote.View.Dispose();
            RestoreAnimator(remote);
            WorldGameObject actor = remote.Player?.wgo;
            if (actor == null) return;
            Vector3 scale = actor.transform.localScale;
            scale.x = remote.OriginalScaleX;
            actor.transform.localScale = scale;
            if (remote.Player.fish != null) remote.Player.fish.sprite = null;
            if (remote.Player.fishadow != null) remote.Player.fishadow.sprite = null;
            if (actor.components?.character?.anim_state == CharAnimState.Fishing)
                OnlineCoopManager.Instance?.ClearRemoteWorkAnimation(new CSteamID(id));
            CoopMod.Logger.LogInfo($"{LogPrefix} Remote {id} stopped ({reason})");
        }

        private static void RestoreAnimator(RemoteFishing remote)
        {
            if (remote.ControlledAnimator != null)
                remote.ControlledAnimator.speed = remote.OriginalAnimatorSpeed;
            remote.ControlledAnimator = null;
        }

        private void ClearRemotes()
        {
            expired.Clear();
            expired.AddRange(remotes.Keys);
            foreach (ulong id in expired) RemoveRemote(id);
            expired.Clear();
        }
    }
}
