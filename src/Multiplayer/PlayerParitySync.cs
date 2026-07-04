using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using Steamworks;
using UnityEngine;
using GraveyardKeeperCoop.Network;

namespace GraveyardKeeperCoop.Multiplayer
{
    /// <summary>
    /// Synchronizes the shared player state that is not covered by movement packets:
    /// raw stats/resources, active buffs, animator action state, and carried overhead item.
    /// </summary>
    public class PlayerParitySync : MonoBehaviour
    {
        private const byte LegacyPayloadVersion = 1;
        private const byte PayloadVersion = 2;
        private const float SyncIntervalSeconds = 1f;
        private const float EchoSuppressSeconds = 0.45f;
        private const int MaxPayloadBytes = 128 * 1024;
        private const int MaxBuffCount = 64;
        private static readonly string[] LocalOnlyPlayerParams =
        {
            "lock_tp",
            "lock_tp_param",
            "speed"
        };
        private static readonly BindingFlags RuntimeFieldFlags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

        private static PlayerParitySync _instance;
        public static PlayerParitySync Instance => _instance;

        private readonly Dictionary<ulong, uint> lastReceivedSequenceByOrigin = new Dictionary<ulong, uint>();
        private readonly Dictionary<ulong, string> lastAppliedActionFingerprintByOrigin = new Dictionary<ulong, string>();

        private bool isSyncEnabled;
        private float lastSendTime = -SyncIntervalSeconds;
        private float suppressSendUntil;
        private uint nextSequence;
        private string lastSentFingerprint = string.Empty;
        private string lastSentStatsFingerprint = string.Empty;
        private string lastSentActionFingerprint = string.Empty;
        private static FieldInfo toolTargetObjField;
        private static FieldInfo toolActionDelayField;
        private static FieldInfo toolTargetStateChangedField;
        private static FieldInfo toolDrivenByAnimEventField;
        private static FieldInfo toolPlayingAnimationField;
        private static FieldInfo toolIsUsingField;
        private static FieldInfo toolTriedToStopField;
        private static FieldInfo toolWasUsingField;
        private static FieldInfo toolActionStartTimeField;
        private static FieldInfo toolCurrentToolField;
        private static FieldInfo attackPerformingField;
        private static FieldInfo attackAnimBasedTimingField;
        private static FieldInfo attackSuccessedField;
        private static FieldInfo attackUsingItemField;
        private static FieldInfo attackStoppedTimeField;

        private void Awake()
        {
            if (_instance != null && _instance != this)
            {
                Destroy(gameObject);
                return;
            }

            _instance = this;
            DontDestroyOnLoad(gameObject);

            CoopMod.Logger.LogInfo("[PlayerParitySync] Initialized");
        }

        private void OnEnable()
        {
            if (SteamP2PManager.Instance != null)
            {
                SteamP2PManager.Instance.OnPlayerParityReceived -= OnPlayerParityReceived;
                SteamP2PManager.Instance.OnPlayerParityReceived += OnPlayerParityReceived;
            }
        }

        private void OnDisable()
        {
            if (SteamP2PManager.Instance != null)
            {
                SteamP2PManager.Instance.OnPlayerParityReceived -= OnPlayerParityReceived;
            }
        }

        public void EnableSync()
        {
            isSyncEnabled = true;
            lastSendTime = -SyncIntervalSeconds;
            suppressSendUntil = 0f;
            nextSequence = 0;
            lastSentFingerprint = string.Empty;
            lastSentStatsFingerprint = string.Empty;
            lastSentActionFingerprint = string.Empty;
            lastReceivedSequenceByOrigin.Clear();
            lastAppliedActionFingerprintByOrigin.Clear();

            CoopMod.Logger.LogInfo("[PlayerParitySync] Sync enabled");
            SendLocalSnapshot(force: true);
        }

        public void DisableSync()
        {
            isSyncEnabled = false;
            lastReceivedSequenceByOrigin.Clear();
            lastAppliedActionFingerprintByOrigin.Clear();
            CoopMod.Logger.LogInfo("[PlayerParitySync] Sync disabled");
        }

        private void Update()
        {
            if (!isSyncEnabled)
                return;

            var onlineCoop = OnlineCoopManager.Instance;
            if (onlineCoop == null || !onlineCoop.IsOnlineCoopEnabled)
                return;

            if (!CanUsePlayerState())
                return;

            float now = Time.realtimeSinceStartup;
            if (now - lastSendTime < SyncIntervalSeconds || now < suppressSendUntil)
                return;

            SendLocalSnapshot(force: false);
        }

        public void SendLocalSnapshot(bool force)
        {
            var onlineCoop = OnlineCoopManager.Instance;
            if (onlineCoop == null || !onlineCoop.IsOnlineCoopEnabled || !CanUsePlayerState())
                return;

            PlayerParitySnapshot snapshot;
            try
            {
                snapshot = CaptureSnapshot();
            }
            catch (Exception ex)
            {
                CoopMod.Logger.LogWarning($"[PlayerParitySync] Failed to capture snapshot: {ex.Message}");
                return;
            }

            if (!force &&
                snapshot.StatsFingerprint == lastSentStatsFingerprint &&
                snapshot.ActionFingerprint == lastSentActionFingerprint)
            {
                lastSendTime = Time.realtimeSinceStartup;
                return;
            }

            snapshot.Sequence = ++nextSequence;

            byte[] payload;
            try
            {
                payload = SerializeSnapshot(snapshot);
            }
            catch (Exception ex)
            {
                CoopMod.Logger.LogWarning($"[PlayerParitySync] Failed to serialize snapshot: {ex.Message}");
                return;
            }

            if (payload.Length > MaxPayloadBytes)
            {
                CoopMod.Logger.LogWarning($"[PlayerParitySync] Snapshot too large ({payload.Length} bytes); not sending");
                return;
            }

            if (onlineCoop.IsHost)
            {
                SteamP2PManager.Instance?.BroadcastPlayerParity(payload);
            }
            else
            {
                SteamP2PManager.Instance?.SendPlayerParityToHost(payload);
            }

            lastSentFingerprint = snapshot.Fingerprint;
            lastSentStatsFingerprint = snapshot.StatsFingerprint;
            lastSentActionFingerprint = snapshot.ActionFingerprint;
            lastSendTime = Time.realtimeSinceStartup;
        }

        private void OnPlayerParityReceived(CSteamID senderID, byte[] payload)
        {
            if (!isSyncEnabled)
                return;

            var onlineCoop = OnlineCoopManager.Instance;
            if (onlineCoop == null || !onlineCoop.IsOnlineCoopEnabled || !CanUsePlayerState())
                return;

            if (payload == null || payload.Length == 0 || payload.Length > MaxPayloadBytes)
                return;

            PlayerParitySnapshot snapshot;
            try
            {
                snapshot = DeserializeSnapshot(payload);
            }
            catch (Exception ex)
            {
                CoopMod.Logger.LogWarning($"[PlayerParitySync] Failed to parse snapshot: {ex.Message}");
                return;
            }

            ulong localSteamId = GetLocalSteamId();
            if (snapshot.OriginSteamId == 0UL || snapshot.OriginSteamId == localSteamId)
                return;

            if (!onlineCoop.IsHost && !IsExpectedHost(senderID))
                return;

            if (IsStaleSnapshot(snapshot))
                return;

            try
            {
                if (!onlineCoop.IsHost && HasUnsentLocalStats())
                {
                    SendLocalSnapshot(force: true);
                    return;
                }

                ApplySnapshot(snapshot, onlineCoop);
                suppressSendUntil = Time.realtimeSinceStartup + EchoSuppressSeconds;
                SeedLastSentStatFingerprintFromCurrentState();

                if (onlineCoop.IsHost)
                {
                    SendLocalSnapshot(force: true);
                }
            }
            catch (Exception ex)
            {
                CoopMod.Logger.LogWarning($"[PlayerParitySync] Failed to apply snapshot: {ex.Message}");
            }
        }

        private bool HasUnsentLocalStats()
        {
            try
            {
                return CaptureSnapshot().StatsFingerprint != lastSentStatsFingerprint;
            }
            catch
            {
                return false;
            }
        }

        private bool IsStaleSnapshot(PlayerParitySnapshot snapshot)
        {
            uint lastSequence;
            if (lastReceivedSequenceByOrigin.TryGetValue(snapshot.OriginSteamId, out lastSequence) &&
                snapshot.Sequence <= lastSequence)
            {
                return true;
            }

            lastReceivedSequenceByOrigin[snapshot.OriginSteamId] = snapshot.Sequence;
            return false;
        }

        private void ApplySnapshot(PlayerParitySnapshot snapshot, OnlineCoopManager onlineCoop)
        {
            var player = MainGame.me.player;
            var save = MainGame.me.save;
            bool applySharedLocalState = !onlineCoop.IsHost;

            if (!string.IsNullOrEmpty(snapshot.PlayerParamsJson))
            {
                if (applySharedLocalState)
                {
                    ApplyHostPlayerParamsPreservingClientProgress(
                        player,
                        snapshot.PlayerParamsJson);
                }
                else
                {
                    MergeNewClientProgressParams(
                        player,
                        snapshot.PlayerParamsJson);
                }
            }

            if (applySharedLocalState)
            {
                save.max_hp = snapshot.MaxHp;
                save.max_energy = snapshot.MaxEnergy;
                save.max_sanity = snapshot.MaxSanity;

                var buffs = new List<PlayerBuff>(snapshot.BuffJsons.Count);
                for (int i = 0; i < snapshot.BuffJsons.Count; i++)
                {
                    string buffJson = snapshot.BuffJsons[i];
                    if (string.IsNullOrEmpty(buffJson))
                        continue;

                    PlayerBuff buff = JsonUtility.FromJson<PlayerBuff>(buffJson);
                    if (buff != null && !string.IsNullOrEmpty(buff.buff_id))
                    {
                        buffs.Add(buff);
                    }
                }

                save.buffs = buffs;
                RedrawStatAndBuffUi();
            }

            if (applySharedLocalState)
            {
                ApplyAuthoritativeLocalRuntimeContext(snapshot);
            }

            CSteamID origin = new CSteamID(snapshot.OriginSteamId);
            if (onlineCoop.IsRemotePlayer(origin) && ShouldApplyAction(snapshot))
            {
                onlineCoop.ApplyRemoteParityAction(
                    snapshot.AnimState,
                    snapshot.ItemType,
                    snapshot.GlobalState,
                    snapshot.Direction,
                    snapshot.HasOverhead,
                    snapshot.OverheadItemJson);

                if (snapshot.Version >= PayloadVersion)
                {
                    onlineCoop.ApplyRemoteRuntimeContext(
                        snapshot.ZoneId,
                        snapshot.SubZoneId,
                        snapshot.IsInDungeon,
                        snapshot.DungeonLevel,
                        snapshot.DungeonPresetName,
                        snapshot.ControlEnabled,
                        snapshot.ToolIsUsing,
                        snapshot.ToolPlayingAnimation,
                        snapshot.ToolDrivenByAnimEvent,
                        snapshot.ToolTargetStateChanged,
                        snapshot.ToolTriedToStop,
                        snapshot.ToolWasUsing,
                        snapshot.ToolActionDelay,
                        snapshot.ToolActionElapsed,
                        snapshot.ToolCurrentType,
                        snapshot.ToolTargetUniqueId,
                        snapshot.ToolTargetObjId,
                        snapshot.ToolTargetCustomTag,
                        snapshot.ToolTargetPosition,
                        snapshot.AttackPerforming,
                        snapshot.AttackType,
                        snapshot.AttackAnimBasedTiming,
                        snapshot.AttackUsingItem,
                        snapshot.AttackSuccessed,
                        snapshot.AttackStoppedElapsed);
                }
            }
        }

        /// <summary>
        /// Host snapshots remain authoritative for existing player values, but a
        /// client-owned FlowScript can introduce a new story Ppar while that
        /// client is driving a cutscene. Preserve those new, non-zero parameters
        /// until the host has merged them instead of deleting them on the next
        /// one-second parity update.
        /// </summary>
        private static void ApplyHostPlayerParamsPreservingClientProgress(
            WorldGameObject player,
            string hostParamsJson)
        {
            GameRes localParams = player?.data?.GetParams();
            if (localParams == null)
                return;

            GameRes localBefore = localParams.Clone();
            GameRes hostParams = JsonUtility.FromJson<GameRes>(hostParamsJson);
            float localMovementSpeed =
                player.data.GetParam("speed", LazyConsts.PLAYER_SPEED);

            JsonUtility.FromJsonOverwrite(hostParamsJson, localParams);
            int preserved = MergeMissingNonZeroParams(localParams, localBefore, hostParams);
            ScrubLocalOnlyPlayerParams(localParams);
            player.data.SetParam("speed", localMovementSpeed);

            if (preserved > 0)
            {
                CoopMod.Logger.LogInfo(
                    $"[PlayerParitySync] Preserved {preserved} client-created " +
                    "story parameter(s) until host acknowledgement");
            }
        }

        /// <summary>
        /// The host does not accept client replacements for existing stats, but
        /// it must accept parameters that only exist because a client drove a
        /// story FlowScript. Otherwise the host immediately echoes an older
        /// parameter set back and breaks the next quest trigger (for example,
        /// item_flesh after the autopsy introduction).
        /// </summary>
        private static void MergeNewClientProgressParams(
            WorldGameObject player,
            string clientParamsJson)
        {
            GameRes hostParams = player?.data?.GetParams();
            if (hostParams == null)
                return;

            GameRes clientParams = JsonUtility.FromJson<GameRes>(clientParamsJson);
            int merged = MergeMissingNonZeroParams(hostParams, clientParams, hostParams);
            if (merged > 0)
            {
                MainGame.me?.save?.quests?.CheckQuestsState();
                CoopMod.Logger.LogInfo(
                    $"[PlayerParitySync] Merged {merged} new client story " +
                    "parameter(s) into host progression");
            }
        }

        private static int MergeMissingNonZeroParams(
            GameRes destination,
            GameRes source,
            GameRes authoritative)
        {
            if (destination == null || source == null)
                return 0;

            int merged = 0;
            List<string> types = source.Types;
            for (int i = 0; i < types.Count; i++)
            {
                string type = types[i];
                if (string.IsNullOrEmpty(type) ||
                    IsLocalOnlyPlayerParam(type) ||
                    (authoritative != null && authoritative.Has(type)))
                {
                    continue;
                }

                float value = source.Get(type);
                if (Mathf.Abs(value) < 0.0001f)
                    continue;

                destination.Set(type, value);
                merged++;
            }

            return merged;
        }

        private static bool IsLocalOnlyPlayerParam(string type)
        {
            for (int i = 0; i < LocalOnlyPlayerParams.Length; i++)
            {
                if (string.Equals(
                        LocalOnlyPlayerParams[i],
                        type,
                        StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        private static void ApplyAuthoritativeLocalRuntimeContext(PlayerParitySnapshot snapshot)
        {
            if (snapshot.Version < PayloadVersion || MainGame.me == null || MainGame.me.dungeon_root == null)
                return;

            try
            {
                bool localDungeonLoaded = MainGame.me.dungeon_root.dungeon_is_loaded_now;
                int localDungeonLevel = MainGame.me.dungeon_root.cur_dungeon_preset != null
                    ? MainGame.me.dungeon_root.cur_dungeon_preset.dungeon_level
                    : -1;

                if (snapshot.IsInDungeon)
                {
                    if (snapshot.DungeonLevel >= 0 && (!localDungeonLoaded || localDungeonLevel != snapshot.DungeonLevel))
                    {
                        CoopMod.Logger.LogInfo($"[PlayerParitySync] Loading host dungeon context level {snapshot.DungeonLevel}");
                        MainGame.me.TeleportToDungeonLevel(snapshot.DungeonLevel);
                    }
                }
                else if (localDungeonLoaded)
                {
                    CoopMod.Logger.LogInfo("[PlayerParitySync] Leaving local dungeon context to match host");
                    if (MainGame.me.dungeon_root.TrySaveDungeon())
                    {
                        CoopMod.Logger.LogInfo("[PlayerParitySync] Saved local dungeon before authoritative unload");
                    }

                    MainGame.me.dungeon_root.DestroyTiles();
                    MainGame.me.OnExitDungeon();
                }

                if (MainGame.me.player != null)
                {
                    MainGame.me.player.cur_zone = snapshot.ZoneId ?? string.Empty;
                }
            }
            catch (Exception ex)
            {
                CoopMod.Logger.LogWarning($"[PlayerParitySync] Failed to apply authoritative runtime context: {ex.Message}");
            }
        }

        private bool ShouldApplyAction(PlayerParitySnapshot snapshot)
        {
            string lastActionFingerprint;
            if (lastAppliedActionFingerprintByOrigin.TryGetValue(snapshot.OriginSteamId, out lastActionFingerprint) &&
                lastActionFingerprint == snapshot.ActionFingerprint)
            {
                return false;
            }

            lastAppliedActionFingerprintByOrigin[snapshot.OriginSteamId] = snapshot.ActionFingerprint;
            return true;
        }

        private static void RedrawStatAndBuffUi()
        {
            try
            {
                GUIElements gui = GUIElements.me;
                if (gui != null)
                {
                    if (gui.buffs != null)
                    {
                        gui.buffs.Redraw();
                    }

                    if (gui.hud != null)
                    {
                        gui.hud.Update();
                    }
                }
            }
            catch (Exception ex)
            {
                CoopMod.Logger.LogWarning($"[PlayerParitySync] Failed to redraw stat UI: {ex.Message}");
            }
        }

        private void SeedLastSentStatFingerprintFromCurrentState()
        {
            try
            {
                PlayerParitySnapshot current = CaptureSnapshot();
                lastSentFingerprint = current.Fingerprint;
                lastSentStatsFingerprint = current.StatsFingerprint;
            }
            catch
            {
                lastSentFingerprint = string.Empty;
                lastSentStatsFingerprint = string.Empty;
            }
        }

        private static bool CanUsePlayerState()
        {
            return MainGame.game_started
                && MainGame.me != null
                && MainGame.me.save != null
                && MainGame.me.player != null
                && MainGame.me.player.data != null
                && MainGame.me.player.data.GetParams() != null;
        }

        private static bool IsExpectedHost(CSteamID senderID)
        {
            var lobbyID = SteamLobbyManager.Instance?.CurrentLobbyID ?? CSteamID.Nil;
            if (lobbyID == CSteamID.Nil)
                return true;

            return SteamMatchmaking.GetLobbyOwner(lobbyID) == senderID;
        }

        private static ulong GetLocalSteamId()
        {
            return SteamManager.Initialized ? SteamUser.GetSteamID().m_SteamID : 0UL;
        }

        private static PlayerParitySnapshot CaptureSnapshot()
        {
            WorldGameObject player = MainGame.me.player;
            GameSave save = MainGame.me.save;
            BaseCharacterComponent character = player.components?.character;

            var snapshot = new PlayerParitySnapshot
            {
                Version = PayloadVersion,
                OriginSteamId = GetLocalSteamId(),
                GameTime = MainGame.game_time,
                PlayerParamsJson = JsonUtility.ToJson(GetSharedPlayerParamsForSnapshot(player)),
                MaxHp = save.max_hp,
                MaxEnergy = save.max_energy,
                MaxSanity = save.max_sanity,
                BuffJsons = new List<string>(),
                AnimState = character != null ? (int)character.anim_state : (int)CharAnimState.Idle,
                GlobalState = GetGlobalState(character),
                Direction = character != null ? character.direction : Vector2.down,
                HasOverhead = character != null && character.has_overhead,
                OverheadItemJson = string.Empty,
                ZoneId = SafeGetZoneId(player),
                SubZoneId = SafeGetSubZoneId(player.transform.position),
                ControlEnabled = character != null && character.control_enabled
            };

            snapshot.ItemType = GetActionItemType(character, snapshot.GlobalState);
            CaptureDungeonContext(snapshot);
            CaptureToolContext(snapshot, player);
            CaptureAttackContext(snapshot, character);

            if (save.buffs != null)
            {
                for (int i = 0; i < save.buffs.Count; i++)
                {
                    PlayerBuff buff = save.buffs[i];
                    if (buff != null && !string.IsNullOrEmpty(buff.buff_id))
                    {
                        snapshot.BuffJsons.Add(JsonUtility.ToJson(buff));
                    }
                }
            }

            if (snapshot.HasOverhead && character != null)
            {
                Item overhead = character.GetOverheadItem();
                snapshot.OverheadItemJson = overhead != null ? overhead.ToJSON() : string.Empty;
                snapshot.HasOverhead = !string.IsNullOrEmpty(snapshot.OverheadItemJson);
            }

            BuildFingerprints(snapshot);
            return snapshot;
        }

        private static GameRes GetSharedPlayerParamsForSnapshot(WorldGameObject player)
        {
            GameRes sharedParams = player.data.GetParams().Clone();
            ScrubLocalOnlyPlayerParams(sharedParams);
            return sharedParams;
        }

        private static void ScrubLocalOnlyPlayerParams(GameRes playerParams)
        {
            if (playerParams == null)
                return;

            for (int i = 0; i < LocalOnlyPlayerParams.Length; i++)
            {
                playerParams.Set(LocalOnlyPlayerParams[i], 0f);
            }
        }

        private static int GetGlobalState(BaseCharacterComponent character)
        {
            if (character == null)
                return (int)CharAnimState.Idle;

            try
            {
                if (character.wgo?.components?.animator != null &&
                    character.wgo.components.animator.ParamExists("global_state"))
                {
                    return character.wgo.components.animator.GetInteger("global_state");
                }
            }
            catch
            {
            }

            if (character.anim_state == CharAnimState.Tool)
            {
                int itemType = GetActionItemType(character, -1);
                return 100 + itemType;
            }

            return (int)character.anim_state;
        }

        private static int GetActionItemType(BaseCharacterComponent character, int globalState)
        {
            if (globalState >= 100)
                return globalState - 100;

            if (character == null)
                return (int)ItemDefinition.ItemType.None;

            if (character.anim_state != CharAnimState.Tool)
                return (int)ItemDefinition.ItemType.None;

            try
            {
                return character.wgo != null
                    ? (int)character.wgo.GetCurrentItemType()
                    : (int)ItemDefinition.ItemType.None;
            }
            catch
            {
                return (int)ItemDefinition.ItemType.None;
            }
        }

        private static string SafeGetZoneId(WorldGameObject player)
        {
            try
            {
                return player != null ? player.GetMyWorldZoneId() ?? string.Empty : string.Empty;
            }
            catch
            {
                return player?.cur_zone ?? string.Empty;
            }
        }

        private static string SafeGetSubZoneId(Vector3 position)
        {
            try
            {
                Collider2D[] colliders = Physics2D.OverlapPointAll(position);
                if (colliders == null)
                    return string.Empty;

                for (int i = 0; i < colliders.Length; i++)
                {
                    WorldSubZone subZone = colliders[i]?.GetComponentInParent<WorldSubZone>();
                    if (subZone != null && !string.IsNullOrEmpty(subZone.sub_zone_id))
                    {
                        return subZone.sub_zone_id;
                    }
                }
            }
            catch
            {
            }

            return string.Empty;
        }

        private static void CaptureDungeonContext(PlayerParitySnapshot snapshot)
        {
            try
            {
                var dungeonRoot = MainGame.me?.dungeon_root;
                snapshot.IsInDungeon = dungeonRoot != null && dungeonRoot.dungeon_is_loaded_now;
                snapshot.DungeonLevel = dungeonRoot?.cur_dungeon_preset != null
                    ? dungeonRoot.cur_dungeon_preset.dungeon_level
                    : -1;
                snapshot.DungeonPresetName = dungeonRoot?.cur_dungeon_preset != null
                    ? dungeonRoot.cur_dungeon_preset.name ?? string.Empty
                    : string.Empty;
            }
            catch
            {
                snapshot.IsInDungeon = false;
                snapshot.DungeonLevel = -1;
                snapshot.DungeonPresetName = string.Empty;
            }
        }

        private static void CaptureToolContext(PlayerParitySnapshot snapshot, WorldGameObject player)
        {
            ToolComponent tool = player?.components?.tool;
            if (tool == null)
                return;

            WorldGameObject target = GetFieldValue<WorldGameObject>(tool, GetToolTargetObjField(), null);
            snapshot.ToolIsUsing = GetFieldValue(tool, GetToolIsUsingField(), false);
            snapshot.ToolPlayingAnimation = GetFieldValue(tool, GetToolPlayingAnimationField(), false);
            snapshot.ToolDrivenByAnimEvent = GetFieldValue(tool, GetToolDrivenByAnimEventField(), false);
            snapshot.ToolTargetStateChanged = GetFieldValue(tool, GetToolTargetStateChangedField(), false);
            snapshot.ToolTriedToStop = GetFieldValue(tool, GetToolTriedToStopField(), false);
            snapshot.ToolWasUsing = GetFieldValue(tool, GetToolWasUsingField(), false);
            snapshot.ToolActionDelay = GetFieldValue(tool, GetToolActionDelayField(), 0);
            snapshot.ToolCurrentType = (int)GetFieldValue(tool, GetToolCurrentToolField(), ItemDefinition.ItemType.None);

            float actionStart = GetFieldValue(tool, GetToolActionStartTimeField(), -1f);
            snapshot.ToolActionElapsed = actionStart > 0f ? Mathf.Max(0f, Time.time - actionStart) : -1f;

            if (target != null)
            {
                snapshot.ToolTargetUniqueId = target.unique_id;
                snapshot.ToolTargetObjId = target.obj_id ?? string.Empty;
                snapshot.ToolTargetCustomTag = target.custom_tag ?? string.Empty;
                snapshot.ToolTargetPosition = target.transform.position;
            }
        }

        private static void CaptureAttackContext(PlayerParitySnapshot snapshot, BaseCharacterComponent character)
        {
            BaseCharacterAttack attack = character?.attack;
            if (attack == null)
                return;

            snapshot.AttackPerforming = attack.performing_attack;
            snapshot.AttackType = attack.cur_attack_type;
            snapshot.AttackAnimBasedTiming = GetFieldValue(attack, GetAttackAnimBasedTimingField(), false);
            snapshot.AttackUsingItem = GetFieldValue(attack, GetAttackUsingItemField(), false);
            snapshot.AttackSuccessed = GetFieldValue(attack, GetAttackSuccessedField(), false);

            float stoppedTime = GetFieldValue(attack, GetAttackStoppedTimeField(), -1f);
            snapshot.AttackStoppedElapsed = snapshot.AttackPerforming && stoppedTime > 0f
                ? Mathf.Max(0f, Time.time - stoppedTime)
                : -1f;
        }

        private static T GetFieldValue<T>(object target, FieldInfo field, T fallback)
        {
            if (target == null || field == null)
                return fallback;

            try
            {
                object value = field.GetValue(target);
                if (value is T)
                {
                    return (T)value;
                }
            }
            catch
            {
            }

            return fallback;
        }

        private static FieldInfo GetToolTargetObjField() => toolTargetObjField ?? (toolTargetObjField = typeof(ToolComponent).GetField("_target_obj", RuntimeFieldFlags));
        private static FieldInfo GetToolActionDelayField() => toolActionDelayField ?? (toolActionDelayField = typeof(ToolComponent).GetField("_action_delay", RuntimeFieldFlags));
        private static FieldInfo GetToolTargetStateChangedField() => toolTargetStateChangedField ?? (toolTargetStateChangedField = typeof(ToolComponent).GetField("_target_state_changed", RuntimeFieldFlags));
        private static FieldInfo GetToolDrivenByAnimEventField() => toolDrivenByAnimEventField ?? (toolDrivenByAnimEventField = typeof(ToolComponent).GetField("_is_driven_by_anim_event", RuntimeFieldFlags));
        private static FieldInfo GetToolPlayingAnimationField() => toolPlayingAnimationField ?? (toolPlayingAnimationField = typeof(ToolComponent).GetField("_playing_animation", RuntimeFieldFlags));
        private static FieldInfo GetToolIsUsingField() => toolIsUsingField ?? (toolIsUsingField = typeof(ToolComponent).GetField("_is_using_tool", RuntimeFieldFlags));
        private static FieldInfo GetToolTriedToStopField() => toolTriedToStopField ?? (toolTriedToStopField = typeof(ToolComponent).GetField("_tried_to_stop", RuntimeFieldFlags));
        private static FieldInfo GetToolWasUsingField() => toolWasUsingField ?? (toolWasUsingField = typeof(ToolComponent).GetField("_was_using_tool", RuntimeFieldFlags));
        private static FieldInfo GetToolActionStartTimeField() => toolActionStartTimeField ?? (toolActionStartTimeField = typeof(ToolComponent).GetField("_action_start_time", RuntimeFieldFlags));
        private static FieldInfo GetToolCurrentToolField() => toolCurrentToolField ?? (toolCurrentToolField = typeof(ToolComponent).GetField("_current_tool", RuntimeFieldFlags));
        private static FieldInfo GetAttackPerformingField() => attackPerformingField ?? (attackPerformingField = typeof(BaseCharacterAttack).GetField("performing", RuntimeFieldFlags));
        private static FieldInfo GetAttackAnimBasedTimingField() => attackAnimBasedTimingField ?? (attackAnimBasedTimingField = typeof(BaseCharacterAttack).GetField("anim_based_timing", RuntimeFieldFlags));
        private static FieldInfo GetAttackSuccessedField() => attackSuccessedField ?? (attackSuccessedField = typeof(BaseCharacterAttack).GetField("successed", RuntimeFieldFlags));
        private static FieldInfo GetAttackUsingItemField() => attackUsingItemField ?? (attackUsingItemField = typeof(BaseCharacterAttack).GetField("using_item", RuntimeFieldFlags));
        private static FieldInfo GetAttackStoppedTimeField() => attackStoppedTimeField ?? (attackStoppedTimeField = typeof(BaseCharacterAttack).GetField("_stopped_time", RuntimeFieldFlags));

        private static void BuildFingerprints(PlayerParitySnapshot snapshot)
        {
            snapshot.StatsFingerprint = BuildStatsFingerprint(snapshot);
            snapshot.ActionFingerprint = BuildActionFingerprint(snapshot);
            snapshot.Fingerprint = snapshot.StatsFingerprint + "\nACTION\n" + snapshot.ActionFingerprint;
        }

        private static string BuildStatsFingerprint(PlayerParitySnapshot snapshot)
        {
            var builder = new StringBuilder(1024);
            builder.Append(snapshot.PlayerParamsJson).Append('|');
            builder.Append(snapshot.MaxHp).Append('|');
            builder.Append(snapshot.MaxEnergy).Append('|');
            builder.Append(snapshot.MaxSanity).Append('|');
            builder.Append(snapshot.BuffJsons.Count).Append('|');

            for (int i = 0; i < snapshot.BuffJsons.Count; i++)
            {
                builder.Append(snapshot.BuffJsons[i]).Append('|');
            }

            return builder.ToString();
        }

        private static string BuildActionFingerprint(PlayerParitySnapshot snapshot)
        {
            var builder = new StringBuilder(512);
            builder.Append(snapshot.AnimState).Append('|');
            builder.Append(snapshot.ItemType).Append('|');
            builder.Append(snapshot.GlobalState).Append('|');

            bool directionMatters = snapshot.AnimState != (int)CharAnimState.Idle &&
                                    snapshot.AnimState != (int)CharAnimState.Walking;
            directionMatters = directionMatters || snapshot.GlobalState < 0 || snapshot.GlobalState >= 100;
            if (directionMatters)
            {
                builder.Append(snapshot.Direction.x).Append('|');
                builder.Append(snapshot.Direction.y).Append('|');
            }

            builder.Append(snapshot.HasOverhead).Append('|');
            builder.Append(snapshot.OverheadItemJson).Append('|');
            builder.Append(snapshot.ZoneId).Append('|');
            builder.Append(snapshot.SubZoneId).Append('|');
            builder.Append(snapshot.IsInDungeon).Append('|');
            builder.Append(snapshot.DungeonLevel).Append('|');
            builder.Append(snapshot.DungeonPresetName).Append('|');
            builder.Append(snapshot.ControlEnabled).Append('|');
            builder.Append(snapshot.ToolIsUsing).Append('|');
            builder.Append(snapshot.ToolPlayingAnimation).Append('|');
            builder.Append(snapshot.ToolDrivenByAnimEvent).Append('|');
            builder.Append(snapshot.ToolTargetStateChanged).Append('|');
            builder.Append(snapshot.ToolTriedToStop).Append('|');
            builder.Append(snapshot.ToolWasUsing).Append('|');
            builder.Append(snapshot.ToolActionDelay).Append('|');
            builder.Append(Mathf.Round(snapshot.ToolActionElapsed * 20f) / 20f).Append('|');
            builder.Append(snapshot.ToolCurrentType).Append('|');
            builder.Append(snapshot.ToolTargetUniqueId).Append('|');
            builder.Append(snapshot.ToolTargetObjId).Append('|');
            builder.Append(snapshot.ToolTargetCustomTag).Append('|');
            builder.Append(Mathf.Round(snapshot.ToolTargetPosition.x * 10f) / 10f).Append('|');
            builder.Append(Mathf.Round(snapshot.ToolTargetPosition.y * 10f) / 10f).Append('|');
            builder.Append(snapshot.AttackPerforming).Append('|');
            builder.Append(snapshot.AttackType).Append('|');
            builder.Append(snapshot.AttackAnimBasedTiming).Append('|');
            builder.Append(snapshot.AttackUsingItem).Append('|');
            builder.Append(snapshot.AttackSuccessed).Append('|');
            builder.Append(Mathf.Round(snapshot.AttackStoppedElapsed * 20f) / 20f);
            return builder.ToString();
        }

        private static byte[] SerializeSnapshot(PlayerParitySnapshot snapshot)
        {
            using (var stream = new MemoryStream(2048))
            using (var writer = new BinaryWriter(stream, Encoding.UTF8))
            {
                writer.Write(snapshot.Version);
                writer.Write(snapshot.OriginSteamId);
                writer.Write(snapshot.Sequence);
                writer.Write(snapshot.GameTime);
                writer.Write(snapshot.PlayerParamsJson ?? string.Empty);
                writer.Write(snapshot.MaxHp);
                writer.Write(snapshot.MaxEnergy);
                writer.Write(snapshot.MaxSanity);

                writer.Write(snapshot.BuffJsons.Count);
                for (int i = 0; i < snapshot.BuffJsons.Count; i++)
                {
                    writer.Write(snapshot.BuffJsons[i] ?? string.Empty);
                }

                writer.Write(snapshot.AnimState);
                writer.Write(snapshot.ItemType);
                writer.Write(snapshot.GlobalState);
                writer.Write(snapshot.Direction.x);
                writer.Write(snapshot.Direction.y);
                writer.Write(snapshot.HasOverhead);
                writer.Write(snapshot.OverheadItemJson ?? string.Empty);
                writer.Write(snapshot.ZoneId ?? string.Empty);
                writer.Write(snapshot.SubZoneId ?? string.Empty);
                writer.Write(snapshot.IsInDungeon);
                writer.Write(snapshot.DungeonLevel);
                writer.Write(snapshot.DungeonPresetName ?? string.Empty);
                writer.Write(snapshot.ControlEnabled);
                writer.Write(snapshot.ToolIsUsing);
                writer.Write(snapshot.ToolPlayingAnimation);
                writer.Write(snapshot.ToolDrivenByAnimEvent);
                writer.Write(snapshot.ToolTargetStateChanged);
                writer.Write(snapshot.ToolTriedToStop);
                writer.Write(snapshot.ToolWasUsing);
                writer.Write(snapshot.ToolActionDelay);
                writer.Write(snapshot.ToolActionElapsed);
                writer.Write(snapshot.ToolCurrentType);
                writer.Write(snapshot.ToolTargetUniqueId);
                writer.Write(snapshot.ToolTargetObjId ?? string.Empty);
                writer.Write(snapshot.ToolTargetCustomTag ?? string.Empty);
                WriteVector3(writer, snapshot.ToolTargetPosition);
                writer.Write(snapshot.AttackPerforming);
                writer.Write(snapshot.AttackType);
                writer.Write(snapshot.AttackAnimBasedTiming);
                writer.Write(snapshot.AttackUsingItem);
                writer.Write(snapshot.AttackSuccessed);
                writer.Write(snapshot.AttackStoppedElapsed);

                writer.Flush();
                return stream.ToArray();
            }
        }

        private static PlayerParitySnapshot DeserializeSnapshot(byte[] payload)
        {
            using (var stream = new MemoryStream(payload, writable: false))
            using (var reader = new BinaryReader(stream, Encoding.UTF8))
            {
                var snapshot = new PlayerParitySnapshot
                {
                    Version = reader.ReadByte()
                };

                if (snapshot.Version != LegacyPayloadVersion && snapshot.Version != PayloadVersion)
                {
                    throw new InvalidDataException($"Unsupported player parity payload version {snapshot.Version}");
                }

                snapshot.OriginSteamId = reader.ReadUInt64();
                snapshot.Sequence = reader.ReadUInt32();
                snapshot.GameTime = reader.ReadSingle();
                snapshot.PlayerParamsJson = reader.ReadString();
                snapshot.MaxHp = reader.ReadInt32();
                snapshot.MaxEnergy = reader.ReadInt32();
                snapshot.MaxSanity = reader.ReadInt32();

                int buffCount = reader.ReadInt32();
                if (buffCount < 0 || buffCount > MaxBuffCount)
                {
                    throw new InvalidDataException($"Invalid buff count {buffCount}");
                }

                snapshot.BuffJsons = new List<string>(buffCount);
                for (int i = 0; i < buffCount; i++)
                {
                    snapshot.BuffJsons.Add(reader.ReadString());
                }

                snapshot.AnimState = reader.ReadInt32();
                snapshot.ItemType = reader.ReadInt32();
                snapshot.GlobalState = reader.ReadInt32();
                snapshot.Direction = new Vector2(reader.ReadSingle(), reader.ReadSingle());
                snapshot.HasOverhead = reader.ReadBoolean();
                snapshot.OverheadItemJson = reader.ReadString();

                if (snapshot.Version >= PayloadVersion)
                {
                    snapshot.ZoneId = reader.ReadString();
                    snapshot.SubZoneId = reader.ReadString();
                    snapshot.IsInDungeon = reader.ReadBoolean();
                    snapshot.DungeonLevel = reader.ReadInt32();
                    snapshot.DungeonPresetName = reader.ReadString();
                    snapshot.ControlEnabled = reader.ReadBoolean();
                    snapshot.ToolIsUsing = reader.ReadBoolean();
                    snapshot.ToolPlayingAnimation = reader.ReadBoolean();
                    snapshot.ToolDrivenByAnimEvent = reader.ReadBoolean();
                    snapshot.ToolTargetStateChanged = reader.ReadBoolean();
                    snapshot.ToolTriedToStop = reader.ReadBoolean();
                    snapshot.ToolWasUsing = reader.ReadBoolean();
                    snapshot.ToolActionDelay = reader.ReadInt32();
                    snapshot.ToolActionElapsed = reader.ReadSingle();
                    snapshot.ToolCurrentType = reader.ReadInt32();
                    snapshot.ToolTargetUniqueId = reader.ReadInt64();
                    snapshot.ToolTargetObjId = reader.ReadString();
                    snapshot.ToolTargetCustomTag = reader.ReadString();
                    snapshot.ToolTargetPosition = ReadVector3(reader);
                    snapshot.AttackPerforming = reader.ReadBoolean();
                    snapshot.AttackType = reader.ReadInt32();
                    snapshot.AttackAnimBasedTiming = reader.ReadBoolean();
                    snapshot.AttackUsingItem = reader.ReadBoolean();
                    snapshot.AttackSuccessed = reader.ReadBoolean();
                    snapshot.AttackStoppedElapsed = reader.ReadSingle();
                }

                BuildFingerprints(snapshot);
                return snapshot;
            }
        }

        private static void WriteVector3(BinaryWriter writer, Vector3 value)
        {
            writer.Write(value.x);
            writer.Write(value.y);
            writer.Write(value.z);
        }

        private static Vector3 ReadVector3(BinaryReader reader)
        {
            return new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
        }

        private sealed class PlayerParitySnapshot
        {
            public byte Version;
            public ulong OriginSteamId;
            public uint Sequence;
            public float GameTime;
            public string PlayerParamsJson;
            public int MaxHp;
            public int MaxEnergy;
            public int MaxSanity;
            public List<string> BuffJsons;
            public int AnimState;
            public int ItemType;
            public int GlobalState;
            public Vector2 Direction;
            public bool HasOverhead;
            public string OverheadItemJson;
            public string ZoneId = string.Empty;
            public string SubZoneId = string.Empty;
            public bool IsInDungeon;
            public int DungeonLevel = -1;
            public string DungeonPresetName = string.Empty;
            public bool ControlEnabled = true;
            public bool ToolIsUsing;
            public bool ToolPlayingAnimation;
            public bool ToolDrivenByAnimEvent;
            public bool ToolTargetStateChanged;
            public bool ToolTriedToStop;
            public bool ToolWasUsing;
            public int ToolActionDelay;
            public float ToolActionElapsed = -1f;
            public int ToolCurrentType;
            public long ToolTargetUniqueId = -1L;
            public string ToolTargetObjId = string.Empty;
            public string ToolTargetCustomTag = string.Empty;
            public Vector3 ToolTargetPosition;
            public bool AttackPerforming;
            public int AttackType = -1;
            public bool AttackAnimBasedTiming;
            public bool AttackUsingItem;
            public bool AttackSuccessed;
            public float AttackStoppedElapsed = -1f;
            public string StatsFingerprint;
            public string ActionFingerprint;
            public string Fingerprint;
        }
    }
}
