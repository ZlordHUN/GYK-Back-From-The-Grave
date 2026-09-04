using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using BepInEx;
using Steamworks;
using UnityEngine;
using GraveyardKeeperCoop.Network;

namespace GraveyardKeeperCoop.Multiplayer
{
    /// <summary>
    /// Persists the joiner's personal inventory, loadout, money, carried item,
    /// health, energy state, technology-point and refugee-gratitude balances,
    /// active effects, and personal story-grant receipts across host-save loads.
    /// The transferred host save remains authoritative for quests, technology, NPCs,
    /// world flags, and every other shared campaign field.
    /// </summary>
    public class JoinerProfileManager : MonoBehaviour
    {
        private const int FormatVersion = 11;
        private const float ApplyPollTimeoutSeconds = 120f;
        private const float ApplyPollIntervalSeconds = 0.25f;
        private const int MaxProfileItems = 1024;
        private const int MaxToolbarSlots = 64;
        private const int MaxProfileBuffs = 64;
        private const int MaxProfileStoryReceipts = 128;
        private const long MaxProfileBytes = 16L * 1024L * 1024L;

        private static JoinerProfileManager _instance;
        public static JoinerProfileManager Instance => _instance;

        private bool appliedThisSession;
        private bool wasJoinerSessionActive;
        private Coroutine applyCoroutine;
        private string activeSaveHash = string.Empty;

        private void Awake()
        {
            if (_instance != null && _instance != this)
            {
                Destroy(gameObject);
                return;
            }

            _instance = this;
            DontDestroyOnLoad(gameObject);
            CoopMod.Logger.LogInfo(
                "[JoinerProfile] Initialized " +
                "(personal inventory/loadout/money/carried item/health/energy/technology points/refugee gratitude/effects/story receipts)");
        }

        private void Update()
        {
            if (!IsEnabled())
                return;

            bool joinerActive = IsJoinerSessionActive();
            if (wasJoinerSessionActive && !joinerActive)
            {
                appliedThisSession = false;
                activeSaveHash = string.Empty;
            }

            wasJoinerSessionActive = joinerActive;
        }

        /// <summary>
        /// Called immediately before SaveSlotsMenuGUI starts replacing the current
        /// world with the downloaded host save.
        /// </summary>
        public void OnHostSaveLoadStartedAsJoiner(
            string hostSlot,
            string saveHash)
        {
            if (!IsEnabled())
                return;

            WorldGameObject playerBeforeLoad = MainGame.me?.player;
            if (!TryNormalizeSaveIdentity(
                    hostSlot,
                    saveHash,
                    out string normalizedSlot,
                    out string normalizedHash))
            {
                CoopMod.Logger.LogError(
                    $"[JoinerProfile] Refusing host-save load without a valid save " +
                    $"revision (slot='{hostSlot ?? string.Empty}', " +
                    $"hashLength={saveHash?.Length ?? 0})");
                normalizedSlot = string.Empty;
                normalizedHash = string.Empty;
            }

            activeSaveHash = normalizedHash;
            appliedThisSession = false;

            if (applyCoroutine != null)
                StopCoroutine(applyCoroutine);

            applyCoroutine = StartCoroutine(
                ApplyAfterFreshPlayerReady(
                    playerBeforeLoad,
                    normalizedSlot,
                    normalizedHash));
        }

        /// <summary>
        /// Host: publish the exact save revision after the .dat write completed.
        /// Joiners commit their live personal state only in response to this event.
        /// </summary>
        public static void NotifyHostSaveCompleted(SaveSlotData slot)
        {
            OnlineCoopManager coop = OnlineCoopManager.Instance;
            if (coop == null || !coop.IsOnlineCoopEnabled || !coop.IsHost ||
                !MainGame.game_started || slot == null ||
                string.IsNullOrEmpty(slot.filename_no_extension))
            {
                return;
            }

            try
            {
                string saveHash = SaveHashCache.ComputeSlotHash(
                    slot.filename_no_extension);
                if (!TryNormalizeSaveIdentity(
                        slot.filename_no_extension,
                        saveHash,
                        out string normalizedSlot,
                        out string normalizedHash))
                {
                    CoopMod.Logger.LogWarning(
                        $"[JoinerProfile] Host save '{slot.filename_no_extension}' " +
                        "completed but its fingerprint could not be read");
                    return;
                }

                using (var writer = new MsgWriter(Op.JoinerProfileCommit))
                {
                    writer.Write(normalizedSlot);
                    writer.Write(normalizedHash);
                    SteamP2PManager.Instance?.BroadcastBinary(writer.ToArray());
                }
                CoopMod.Logger.LogInfo(
                    $"[JoinerProfile] Published host save commit " +
                    $"slot='{normalizedSlot}', hash={ShortHash(normalizedHash)}");
            }
            catch (Exception ex)
            {
                CoopMod.Logger.LogWarning(
                    $"[JoinerProfile] Failed to publish host save commit: {ex.Message}");
            }
        }

        /// <summary>Client: commit personal state against the host save that just succeeded.</summary>
        public void HandleHostSaveCommit(CSteamID senderID, ref MsgReader reader)
        {
            try
            {
                CSteamID host = GetCurrentHost();
                if (host == CSteamID.Nil || senderID != host)
                {
                    CoopMod.Logger.LogWarning(
                        $"[JoinerProfile] Ignored save commit from non-host {senderID}");
                    return;
                }

                string hostSlot = reader.ReadString();
                string saveHash = reader.ReadString();
                if (!TryNormalizeSaveIdentity(
                        hostSlot,
                        saveHash,
                        out string normalizedSlot,
                        out string normalizedHash))
                {
                    CoopMod.Logger.LogWarning(
                        "[JoinerProfile] Ignored malformed host save commit");
                    return;
                }

                if (!IsEnabled() || !IsJoinerSessionActive() ||
                    !appliedThisSession || !IsPlayerReady())
                {
                    activeSaveHash = normalizedHash;
                    CoopMod.Logger.LogInfo(
                        $"[JoinerProfile] Save commit {ShortHash(normalizedHash)} " +
                        "arrived before a personal profile was active; the loaded host " +
                        "save will establish its baseline");
                    return;
                }

                string parentSaveHash = activeSaveHash;
                activeSaveHash = normalizedHash;
                SaveLiveProfile(
                    normalizedSlot,
                    normalizedHash,
                    "host save commit",
                    parentSaveHash,
                    false);
            }
            catch (Exception ex)
            {
                CoopMod.Logger.LogWarning(
                    $"[JoinerProfile] Failed to process host save commit: {ex.Message}");
            }
        }

        private void SaveLiveProfile(
            string hostSlot,
            string saveHash,
            string reason,
            string parentSaveHash,
            bool isBaseline)
        {
            try
            {
                string path = ResolveProfilePath(hostSlot, saveHash);
                if (string.IsNullOrEmpty(path))
                {
                    CoopMod.Logger.LogWarning(
                        $"[JoinerProfile] Commit skipped ({reason}): profile identity unavailable");
                    return;
                }

                var profile = new JoinerProfile();
                if (!profile.CaptureFromLivePlayer())
                    return;

                profile.BindToRevision(
                    saveHash,
                    parentSaveHash,
                    isBaseline);
                profile.Save(path);
                CoopMod.Logger.LogInfo(
                    $"[JoinerProfile] Committed saved personal state ({reason}, " +
                    $"slot='{hostSlot}', hash={ShortHash(saveHash)}, " +
                    $"parent={ShortHash(parentSaveHash)}, " +
                    $"inventory={profile.InventoryCount}, toolbelt={profile.ToolbeltCount}, " +
                    $"money={profile.Money:F2}, overhead={profile.OverheadItemId}, " +
                    $"hp={profile.Health:F2}, energy={profile.Energy:F2}, " +
                    $"tech_points={profile.TechnologyPointsSummary}, " +
                    $"gratitude={profile.RefugeeGratitudeSummary}, " +
                    $"effects={profile.BuffCount}, " +
                    $"story_receipts={profile.PersonalGrantReceiptCount}, " +
                    $"file='{Path.GetFileName(path)}')");
            }
            catch (Exception ex)
            {
                CoopMod.Logger.LogError(
                    $"[JoinerProfile] Personal state commit failed ({reason}): {ex.Message}");
            }
        }

        private IEnumerator ApplyAfterFreshPlayerReady(
            WorldGameObject playerBeforeLoad,
            string hostSlot,
            string saveHash)
        {
            float deadline = Time.realtimeSinceStartup + ApplyPollTimeoutSeconds;
            while (Time.realtimeSinceStartup < deadline &&
                   !IsFreshPlayerReady(playerBeforeLoad))
            {
                yield return new WaitForSecondsRealtime(ApplyPollIntervalSeconds);
            }

            if (!IsFreshPlayerReady(playerBeforeLoad))
            {
                CoopMod.Logger.LogWarning(
                    "[JoinerProfile] Fresh player was not ready before profile apply timeout");
                MarkAppliedForSession();
                applyCoroutine = null;
                yield break;
            }

            // Let the base loader finish populating the newly spawned player's data.
            yield return null;

            string path = ResolveProfilePath(hostSlot, saveHash);
            if (string.IsNullOrEmpty(path))
            {
                CoopMod.Logger.LogWarning(
                    "[JoinerProfile] No exact host-save identity was available; " +
                    "leaving the loaded inventory untouched until the next successful host save");
                MarkAppliedForSession();
                applyCoroutine = null;
                yield break;
            }

            JoinerProfile profile = JoinerProfile.LoadResilient(path);
            if (profile != null && !profile.IsBoundToRevision(saveHash))
            {
                PreserveUnboundProfile(path);
                CoopMod.Logger.LogWarning(
                    $"[JoinerProfile] Ignoring unbound or mismatched profile for " +
                    $"hash={ShortHash(saveHash)}. Its snapshots were mutable and " +
                    "cannot safely be assigned to this save revision.");
                profile = null;
            }

            if (profile != null && profile.ApplyToLivePlayer())
            {
                QuestSideEffectSync.ReconcileCurrentQuestEffects();
                QuestSideEffectSync.ReconcileSucceededQuestEffects();
                QuestSync.ReconcileCurrentTaskMilestones();
                QuestSideEffectSync.ReconcileCompletedCutscenePersonalItemGrants();
                CoopMod.Logger.LogInfo(
                    $"[JoinerProfile] Applied personal inventory/loadout/money/carried item/health/energy/technology points/refugee gratitude/effects/story receipts " +
                    $"(inventory={profile.InventoryCount}, toolbelt={profile.ToolbeltCount}, " +
                    $"money={profile.Money:F2}, overhead={profile.OverheadItemId}, " +
                    $"hp={profile.Health:F2}, energy={profile.Energy:F2}, " +
                    $"tech_points={profile.TechnologyPointsSummary}, " +
                    $"gratitude={profile.RefugeeGratitudeSummary}, " +
                    $"effects={profile.BuffCount}, " +
                    $"story_receipts={profile.PersonalGrantReceiptCount}, " +
                    $"items={DescribeLiveInventory()})");
                RefreshPersonalStateUi();
            }
            else if (profile == null)
            {
                // The downloaded save contains the host's local-player params.
                // A new joiner starts with no personal receipts, then receives
                // every catalogued reward whose shared quest is already complete.
                QuestSideEffectSync.ClearPersonalGrantReceipts();
                QuestSideEffectSync.ReconcileCurrentQuestEffects();
                QuestSideEffectSync.ReconcileSucceededQuestEffects();
                QuestSync.ReconcileCurrentTaskMilestones();
                QuestSideEffectSync.ReconcileCompletedCutscenePersonalItemGrants();
                CoopMod.Logger.LogInfo(
                    $"[JoinerProfile] No committed personal snapshot exists for this " +
                    $"host revision; establishing its loaded inventory baseline for " +
                    $"hash={ShortHash(saveHash)}");
                SaveLiveProfile(
                    hostSlot,
                    saveHash,
                    "initial save baseline",
                    null,
                    true);
            }

            MarkAppliedForSession();
            applyCoroutine = null;
        }

        private void MarkAppliedForSession()
        {
            appliedThisSession = true;
        }

        /// <summary>
        /// Personal inventory is committed only when the host successfully saves.
        /// Mutation callers still report changes through this compatibility hook,
        /// but writing here would corrupt rollback by changing the snapshot bound
        /// to the currently loaded host revision.
        /// </summary>
        public static void NotifyPersonalInventoryChanged(string reason)
        {
            // Deliberately no-op. HandleHostSaveCommit is the transaction boundary.
        }

        internal static bool IsOwnedByLocalPlayer(Item item)
        {
            Item playerData = MainGame.me?.player?.data;
            return playerData != null &&
                   ContainsItemReference(playerData, item, 0);
        }

        private static bool ContainsItemReference(
            Item root,
            Item target,
            int depth)
        {
            if (root == null || target == null || depth > 4)
                return false;
            if (object.ReferenceEquals(root, target))
                return true;

            return ContainsItemReference(
                       root.inventory,
                       target,
                       depth + 1) ||
                   ContainsItemReference(
                       root.secondary_inventory,
                       target,
                       depth + 1);
        }

        private static bool ContainsItemReference(
            List<Item> items,
            Item target,
            int depth)
        {
            if (items == null)
                return false;

            for (int i = 0; i < items.Count; i++)
            {
                if (ContainsItemReference(items[i], target, depth))
                    return true;
            }
            return false;
        }

        private static bool IsEnabled()
        {
            return ModConfig.EnableJoinerProfilePersistence == null ||
                   ModConfig.EnableJoinerProfilePersistence.Value;
        }

        private static bool IsJoinerSessionActive()
        {
            var onlineCoop = OnlineCoopManager.Instance;
            return onlineCoop != null && onlineCoop.IsOnlineCoopEnabled &&
                   !onlineCoop.IsHost;
        }

        private static bool IsPlayerReady()
        {
            return MainGame.me != null && MainGame.game_started &&
                   MainGame.me.player != null && MainGame.me.player.data != null &&
                   MainGame.me.save != null;
        }

        private static bool IsFreshPlayerReady(WorldGameObject playerBeforeLoad)
        {
            return IsPlayerReady() &&
                   (playerBeforeLoad == null ||
                    !object.ReferenceEquals(MainGame.me.player, playerBeforeLoad));
        }

        private static CSteamID GetCurrentHost()
        {
            CSteamID host = SteamLobbyManager.Instance?.GetLobbyOwner() ?? CSteamID.Nil;
            if (host != CSteamID.Nil)
                return host;

            OnlineCoopManager coop = OnlineCoopManager.Instance;
            return coop != null && !coop.IsHost
                ? coop.RemotePlayerSteamID
                : CSteamID.Nil;
        }

        private static string ResolveProfilePrefix()
        {
            if (MainGame.me?.save == null)
                return null;

            CSteamID host = GetCurrentHost();
            if (host == CSteamID.Nil)
                return null;

            ulong local = SteamManager.Initialized
                ? SteamUser.GetSteamID().m_SteamID
                : 0UL;
            int worldSeed = MainGame.me.save.dungeon_seed;
            Directory.CreateDirectory(Paths.ConfigPath);
            return Path.Combine(
                Paths.ConfigPath,
                $"gkcoop_joiner_inventory_{local}_{host.m_SteamID}_{worldSeed}");
        }

        private static string ResolveProfilePath(string hostSlot, string saveHash)
        {
            if (!TryNormalizeSaveIdentity(
                    hostSlot,
                    saveHash,
                    out string normalizedSlot,
                    out string normalizedHash))
            {
                return null;
            }

            string prefix = ResolveProfilePrefix();
            if (string.IsNullOrEmpty(prefix))
                return null;

            // The .info payload transferred by the game does not serialize
            // filename_no_extension, so a full download can legitimately have no
            // host slot name here. The SHA-256 is already the exact .dat revision;
            // the prefix additionally scopes it to this player, host, and world.
            string identityHash = SaveHashCache.ComputeHash(
                Encoding.UTF8.GetBytes("revision\n" + normalizedHash));
            return string.IsNullOrEmpty(identityHash)
                ? null
                : $"{prefix}_{identityHash}.bin";
        }

        private static bool TryNormalizeSaveIdentity(
            string hostSlot,
            string saveHash,
            out string normalizedSlot,
            out string normalizedHash)
        {
            normalizedSlot = (hostSlot ?? string.Empty).Trim();
            normalizedHash = (saveHash ?? string.Empty).Trim().ToLowerInvariant();
            if (normalizedSlot.Length > 256 ||
                normalizedHash.Length != 64)
            {
                return false;
            }

            for (int i = 0; i < normalizedHash.Length; i++)
            {
                char c = normalizedHash[i];
                if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f')))
                    return false;
            }
            return true;
        }

        private static void PreserveUnboundProfile(string path)
        {
            if (string.IsNullOrEmpty(path))
                return;

            try
            {
                PreserveFile(path, path + ".pre-v5");
                PreserveFile(path + ".backup", path + ".backup.pre-v5");
            }
            catch (Exception ex)
            {
                CoopMod.Logger.LogWarning(
                    $"[JoinerProfile] Could not preserve legacy profile: {ex.Message}");
            }
        }

        private static void PreserveFile(string source, string destination)
        {
            if (File.Exists(source) && !File.Exists(destination))
                File.Copy(source, destination);
        }

        private static string ShortHash(string hash)
        {
            return string.IsNullOrEmpty(hash)
                ? "missing"
                : hash.Substring(0, Math.Min(12, hash.Length)) + "...";
        }

        private static void RefreshPersonalStateUi()
        {
            try
            {
                if (GUIElements.me?.buffs != null)
                    GUIElements.me.buffs.Redraw();

                if (GUIElements.me?.hud != null)
                {
                    GUIElements.me.hud.Redraw();
                    GUIElements.me.hud.Update();
                }
            }
            catch (Exception ex)
            {
                CoopMod.Logger.LogWarning(
                    $"[JoinerProfile] Failed to redraw personal-state UI: {ex.Message}");
            }
        }

        private static string DescribeLiveInventory()
        {
            List<Item> inventory = MainGame.me?.player?.data?.inventory;
            if (inventory == null || inventory.Count == 0)
                return "empty";

            const int maximumEntries = 12;
            var entries = new List<string>(Math.Min(inventory.Count, maximumEntries));
            for (int i = 0; i < inventory.Count && entries.Count < maximumEntries; i++)
            {
                Item item = inventory[i];
                if (item == null || string.IsNullOrEmpty(item.id) || item.value <= 0)
                    continue;

                entries.Add($"{item.id}x{item.value}");
            }

            if (entries.Count == 0)
                return "empty";
            if (inventory.Count > maximumEntries)
                entries.Add("...");
            return string.Join(",", entries.ToArray());
        }

        private sealed class JoinerProfile
        {
            private const string RedTechnologyPointsParam = "r";
            private const string GreenTechnologyPointsParam = "g";
            private const string BlueTechnologyPointsParam = "b";
            private const string VioletTechnologyPointsParam = "v";
            private const string RefugeeGratitudeParam = "gratitude_points";
            private const byte RevisionKindBaseline = 1;
            private const byte RevisionKindHostCommit = 2;

            private int inventorySize;
            private readonly List<string> inventoryItems = new List<string>();
            private readonly List<string> toolbeltItems = new List<string>();
            private string[] equippedItems = new string[0];
            private string gameVersion;
            private float money;
            private bool hasMoney;
            private float health;
            private bool hasHealthState;
            private float energy;
            private float tiredness;
            private float tired;
            private bool hasEnergyState;
            private float redTechnologyPoints;
            private float greenTechnologyPoints;
            private float blueTechnologyPoints;
            private float violetTechnologyPoints;
            private bool hasTechnologyPointBalances;
            private float refugeeGratitudePoints;
            private bool hasRefugeeGratitude;
            private float buffsCapturedAtGameTime;
            private bool hasBuffState;
            private readonly List<string> buffJsons = new List<string>();
            private readonly List<string> personalGrantReceipts =
                new List<string>();
            private string overheadItemJson = string.Empty;
            private string overheadItemId = "none";
            private int sourceFormatVersion;
            private string boundSaveHash = string.Empty;
            private string parentSaveHash = string.Empty;
            private byte revisionKind;

            public int InventoryCount => inventoryItems.Count;
            public int ToolbeltCount => toolbeltItems.Count;
            public float Money => money;
            public float Health => health;
            public float Energy => energy;
            public string TechnologyPointsSummary =>
                hasTechnologyPointBalances
                    ? $"r={redTechnologyPoints:F0},g={greenTechnologyPoints:F0}," +
                      $"b={blueTechnologyPoints:F0},v={violetTechnologyPoints:F0}"
                    : "legacy-host-baseline";
            public string RefugeeGratitudeSummary =>
                hasRefugeeGratitude
                    ? refugeeGratitudePoints.ToString("F0")
                    : "legacy-host-baseline";
            public int BuffCount => buffJsons.Count;
            public int PersonalGrantReceiptCount =>
                personalGrantReceipts.Count;
            public string OverheadItemId => overheadItemId;

            public void BindToRevision(
                string saveHash,
                string parentHash,
                bool isBaseline)
            {
                sourceFormatVersion = FormatVersion;
                boundSaveHash = (saveHash ?? string.Empty)
                    .Trim()
                    .ToLowerInvariant();
                parentSaveHash = (parentHash ?? string.Empty)
                    .Trim()
                    .ToLowerInvariant();
                revisionKind = isBaseline
                    ? RevisionKindBaseline
                    : RevisionKindHostCommit;
            }

            public bool IsBoundToRevision(string saveHash)
            {
                string expected = (saveHash ?? string.Empty)
                    .Trim()
                    .ToLowerInvariant();
                return sourceFormatVersion >= 5 &&
                       sourceFormatVersion <= FormatVersion &&
                       (revisionKind == RevisionKindBaseline ||
                        revisionKind == RevisionKindHostCommit) &&
                       expected.Length == 64 &&
                       string.Equals(
                           boundSaveHash,
                           expected,
                           StringComparison.Ordinal);
            }

            public bool CaptureFromLivePlayer()
            {
                Item data = MainGame.me?.player?.data;
                GameSave save = MainGame.me?.save;
                if (data == null || save == null)
                    return false;

                inventorySize = data.inventory_size;
                money = data.money;
                hasMoney = true;
                health = data.hp;
                hasHealthState = true;
                energy = data.GetParam("energy", 0f);
                tiredness = data.GetParam("tiredness", 0f);
                tired = data.GetParam("tired", 0f);
                hasEnergyState = true;
                redTechnologyPoints =
                    data.GetParam(RedTechnologyPointsParam, 0f);
                greenTechnologyPoints =
                    data.GetParam(GreenTechnologyPointsParam, 0f);
                blueTechnologyPoints =
                    data.GetParam(BlueTechnologyPointsParam, 0f);
                violetTechnologyPoints =
                    data.GetParam(VioletTechnologyPointsParam, 0f);
                hasTechnologyPointBalances = true;
                refugeeGratitudePoints =
                    data.GetParam(RefugeeGratitudeParam, 0f);
                hasRefugeeGratitude = true;
                personalGrantReceipts.Clear();
                personalGrantReceipts.AddRange(
                    QuestSideEffectSync.CapturePersonalGrantReceipts());
                buffsCapturedAtGameTime = MainGame.game_time;
                hasBuffState = true;
                buffJsons.Clear();
                SerializeBuffs(save.buffs, buffJsons, buffsCapturedAtGameTime);
                inventoryItems.Clear();
                toolbeltItems.Clear();
                SerializeItems(data.inventory, inventoryItems);
                SerializeItems(data.secondary_inventory, toolbeltItems);
                BaseCharacterComponent character =
                    MainGame.me?.player?.components?.character;
                Item overhead = character != null && character.has_overhead
                    ? character.GetOverheadItem()
                    : null;
                overheadItemJson = overhead != null && !overhead.IsEmpty()
                    ? overhead.ToJSON(0)
                    : string.Empty;
                overheadItemId = string.IsNullOrEmpty(overhead?.id)
                    ? "none"
                    : overhead.id;

                string[] currentEquipped = save.equipped_items;
                equippedItems = currentEquipped != null
                    ? (string[])currentEquipped.Clone()
                    : new string[0];
                gameVersion = save.game_version.ToString();
                return true;
            }

            public bool ApplyToLivePlayer()
            {
                Item data = MainGame.me?.player?.data;
                GameSave save = MainGame.me?.save;
                if (data == null || save == null)
                    return false;

                // Replace only classified personal ownership state. Do not overwrite
                // the Item root: it also contains HP and progression/world parameters.
                data.inventory = DeserializeItems(inventoryItems);
                data.secondary_inventory = DeserializeItems(toolbeltItems);
                if (hasMoney)
                    data.money = money;
                save.equipped_items = (string[])equippedItems.Clone();
                data.SetInventorySize(
                    Math.Max(inventorySize, data.inventory.Count));

                // The downloaded world contains the host's local-player params.
                // Strip those personal receipts before restoring this Steam-ID-
                // keyed profile so the reconciliation pass can deliver only the
                // rewards this character has not already received.
                QuestSideEffectSync.ClearPersonalGrantReceipts();
                if (sourceFormatVersion >= 10)
                {
                    QuestSideEffectSync.ApplyPersonalGrantReceipts(
                        personalGrantReceipts);
                }
                // The marker, not the profile schema, owns this migration. Format-11
                // profiles written before a later catalog expansion do not contain
                // the marker even though their binary schema is current.
                QuestSideEffectSync.AdoptLegacyStoryGrantReceipts();

                if (hasBuffState)
                    ApplyPersonalBuffs(data, save);
                if (hasEnergyState)
                {
                    // Apply after swapping buff contributions. Energy-affecting
                    // effects are already reflected in the captured final value.
                    data.SetParam("energy", energy);
                    data.SetParam("tiredness", tiredness);
                    data.SetParam("tired", tired);
                }
                if (hasHealthState)
                {
                    // Apply after swapping effects so an HP-affecting buff is not
                    // added twice to the captured final health value.
                    data.hp = health;
                }
                if (hasTechnologyPointBalances)
                {
                    // Red, green, blue, and violet points are personal XP and
                    // technology-currency balances. Shared technology unlocks
                    // live in GameSave, but downloading the host save must not
                    // replace the joiner's unspent points.
                    data.SetParam(
                        RedTechnologyPointsParam,
                        redTechnologyPoints);
                    data.SetParam(
                        GreenTechnologyPointsParam,
                        greenTechnologyPoints);
                    data.SetParam(
                        BlueTechnologyPointsParam,
                        blueTechnologyPoints);
                    data.SetParam(
                        VioletTechnologyPointsParam,
                        violetTechnologyPoints);
                }
                if (hasRefugeeGratitude)
                {
                    // Refugee gratitude pays for work queued by this character.
                    // It must follow the player rather than the downloaded host
                    // save's local-player balance.
                    data.SetParam(
                        RefugeeGratitudeParam,
                        refugeeGratitudePoints);
                }

                ApplyOverheadItem();
                return true;
            }

            private void ApplyPersonalBuffs(Item data, GameSave save)
            {
                List<PlayerBuff> loadedHostBuffs = save.buffs ??
                    new List<PlayerBuff>();
                PersonalBuffState.SubtractContributions(
                    data.GetParams(),
                    loadedHostBuffs);

                var personalBuffs = new List<PlayerBuff>(buffJsons.Count);
                for (int i = 0; i < buffJsons.Count; i++)
                {
                    string json = buffJsons[i];
                    if (string.IsNullOrEmpty(json))
                        continue;

                    try
                    {
                        PlayerBuff buff = JsonUtility.FromJson<PlayerBuff>(json);
                        if (buff == null || string.IsNullOrEmpty(buff.buff_id) ||
                            buff.definition == null)
                        {
                            continue;
                        }

                        float remaining = buff.end_time - buffsCapturedAtGameTime;
                        if (remaining <= 0f)
                            continue;

                        buff.end_time = MainGame.game_time + remaining;
                        personalBuffs.Add(buff);
                    }
                    catch (Exception ex)
                    {
                        CoopMod.Logger.LogWarning(
                            $"[JoinerProfile] Ignored invalid personal effect: {ex.Message}");
                    }
                }

                save.buffs = personalBuffs;
                PersonalBuffState.AddContributions(
                    data.GetParams(),
                    personalBuffs);
            }

            private static void SerializeBuffs(
                List<PlayerBuff> source,
                List<string> destination,
                float capturedAtGameTime)
            {
                if (source == null)
                    return;

                for (int i = 0;
                     i < source.Count && destination.Count < MaxProfileBuffs;
                     i++)
                {
                    PlayerBuff buff = source[i];
                    if (buff == null || string.IsNullOrEmpty(buff.buff_id) ||
                        buff.end_time <= capturedAtGameTime)
                    {
                        continue;
                    }

                    destination.Add(JsonUtility.ToJson(buff));
                }
            }

            private void ApplyOverheadItem()
            {
                BaseCharacterComponent character =
                    MainGame.me?.player?.components?.character;
                if (character == null)
                    return;

                // The downloaded host save may have restored the host's carried
                // item onto this local player clone. Clear it without dropping a
                // world copy, then restore only this joiner's committed item.
                if (character.has_overhead)
                    character.SetOverheadItem(null);

                Item overhead = DeserializeItem(overheadItemJson);
                if (overhead == null || overhead.IsEmpty())
                {
                    overheadItemId = "none";
                    return;
                }

                character.SetOverheadItem(overhead);
                overheadItemId = overhead.id ?? "unknown";
            }

            public void Save(string path)
            {
                if (string.IsNullOrEmpty(path))
                    return;

                string tempPath = path + ".new";
                string backupPath = path + ".backup";
                try
                {
                    if (File.Exists(tempPath))
                        File.Delete(tempPath);

                    using (var stream = new FileStream(
                        tempPath,
                        FileMode.CreateNew,
                        FileAccess.Write,
                        FileShare.None,
                        4096,
                        FileOptions.SequentialScan))
                    using (var writer = new BinaryWriter(stream))
                    {
                        writer.Write(FormatVersion);
                        writer.Write(DateTime.UtcNow.Ticks);
                        writer.Write(gameVersion ?? string.Empty);
                        writer.Write(boundSaveHash ?? string.Empty);
                        writer.Write(parentSaveHash ?? string.Empty);
                        writer.Write(revisionKind);
                        writer.Write(inventorySize);
                        writer.Write(money);
                        writer.Write(overheadItemJson ?? string.Empty);
                        writer.Write(energy);
                        writer.Write(tiredness);
                        writer.Write(tired);
                        writer.Write(buffsCapturedAtGameTime);
                        WriteStrings(writer, buffJsons);
                        writer.Write(health);
                        writer.Write(redTechnologyPoints);
                        writer.Write(greenTechnologyPoints);
                        writer.Write(blueTechnologyPoints);
                        writer.Write(violetTechnologyPoints);
                        writer.Write(refugeeGratitudePoints);
                        WriteStrings(writer, personalGrantReceipts);
                        WriteStrings(writer, inventoryItems);
                        WriteStrings(writer, toolbeltItems);
                        WriteStrings(writer, equippedItems);
                        writer.Flush();
                        stream.Flush(true);
                    }

                    if (File.Exists(path))
                    {
                        try
                        {
                            File.Replace(tempPath, path, backupPath, true);
                        }
                        catch (PlatformNotSupportedException)
                        {
                            ReplaceWithBackupFallback(tempPath, path, backupPath);
                        }
                        catch (IOException)
                        {
                            ReplaceWithBackupFallback(tempPath, path, backupPath);
                        }
                    }
                    else
                    {
                        File.Move(tempPath, path);
                    }
                }
                finally
                {
                    try
                    {
                        if (File.Exists(tempPath))
                            File.Delete(tempPath);
                    }
                    catch
                    {
                        // A stale .new is harmless and is replaced on the next commit.
                    }
                }
            }

            public static JoinerProfile LoadResilient(string path)
            {
                JoinerProfile profile = Load(path);
                if (profile != null || string.IsNullOrEmpty(path))
                    return profile;

                string backupPath = path + ".backup";
                profile = Load(backupPath);
                if (profile != null)
                {
                    CoopMod.Logger.LogWarning(
                        $"[JoinerProfile] Recovered profile from backup " +
                        $"'{Path.GetFileName(backupPath)}'");
                }
                return profile;
            }

            private static void ReplaceWithBackupFallback(
                string tempPath,
                string path,
                string backupPath)
            {
                File.Copy(path, backupPath, true);
                File.Delete(path);
                File.Move(tempPath, path);
            }

            public static JoinerProfile Load(string path)
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path))
                    return null;

                try
                {
                    var file = new FileInfo(path);
                    if (file.Length <= 0 || file.Length > MaxProfileBytes)
                        throw new InvalidDataException("profile file size is invalid");

                    using (var stream = File.OpenRead(path))
                    using (var reader = new BinaryReader(stream))
                    {
                        int version = reader.ReadInt32();
                        if (version != 2 && version != 3 &&
                            version != 4 && version != 5 && version != 6 &&
                            version != 7 && version != 8 &&
                            version != 9 && version != 10 &&
                            version != FormatVersion)
                        {
                            CoopMod.Logger.LogWarning(
                                $"[JoinerProfile] Version mismatch file={version} " +
                                $"expected={FormatVersion}; ignoring profile");
                            return null;
                        }

                        reader.ReadInt64();
                        var profile = new JoinerProfile
                        {
                            sourceFormatVersion = version,
                            gameVersion = reader.ReadString()
                        };
                        if (version >= 5)
                        {
                            profile.boundSaveHash = reader.ReadString();
                            profile.parentSaveHash = reader.ReadString();
                            profile.revisionKind = reader.ReadByte();
                        }
                        profile.inventorySize = reader.ReadInt32();
                        if (version >= 3)
                        {
                            profile.money = reader.ReadSingle();
                            profile.hasMoney = true;
                        }
                        if (version >= 4)
                        {
                            profile.overheadItemJson = reader.ReadString();
                            Item overhead =
                                DeserializeItem(profile.overheadItemJson);
                            profile.overheadItemId =
                                string.IsNullOrEmpty(overhead?.id)
                                    ? "none"
                                    : overhead.id;
                        }
                        if (version >= 6)
                        {
                            profile.energy = reader.ReadSingle();
                            profile.tiredness = reader.ReadSingle();
                            profile.tired = reader.ReadSingle();
                            profile.hasEnergyState = true;
                            profile.buffsCapturedAtGameTime =
                                reader.ReadSingle();
                            profile.buffJsons.AddRange(
                                ReadStrings(reader, MaxProfileBuffs));
                            profile.hasBuffState = true;
                        }
                        if (version >= 7)
                        {
                            profile.health = reader.ReadSingle();
                            profile.hasHealthState = true;
                        }
                        if (version >= 8)
                        {
                            profile.redTechnologyPoints = reader.ReadSingle();
                            profile.greenTechnologyPoints = reader.ReadSingle();
                            profile.blueTechnologyPoints = reader.ReadSingle();
                            profile.violetTechnologyPoints = reader.ReadSingle();
                            profile.hasTechnologyPointBalances = true;
                        }
                        if (version >= 9)
                        {
                            profile.refugeeGratitudePoints = reader.ReadSingle();
                            profile.hasRefugeeGratitude = true;
                        }
                        if (version >= 10)
                        {
                            profile.personalGrantReceipts.AddRange(
                                ReadStrings(
                                    reader,
                                    MaxProfileStoryReceipts));
                        }
                        profile.inventoryItems.AddRange(
                            ReadStrings(reader, MaxProfileItems));
                        profile.toolbeltItems.AddRange(
                            ReadStrings(reader, MaxProfileItems));
                        profile.equippedItems =
                            ReadStrings(reader, MaxToolbarSlots).ToArray();
                        return profile;
                    }
                }
                catch (Exception ex)
                {
                    CoopMod.Logger.LogWarning(
                        $"[JoinerProfile] Load failed: {ex.Message}");
                    return null;
                }
            }

            private static void SerializeItems(
                List<Item> source,
                List<string> destination)
            {
                if (source == null)
                    return;

                for (int i = 0; i < source.Count; i++)
                    destination.Add(source[i] != null ? source[i].ToJSON(0) : string.Empty);
            }

            private static List<Item> DeserializeItems(List<string> source)
            {
                var result = new List<Item>(source.Count);
                for (int i = 0; i < source.Count; i++)
                {
                    Item item = DeserializeItem(source[i]);
                    result.Add(item ?? new Item());
                }
                return result;
            }

            private static Item DeserializeItem(string json)
            {
                Item item = string.IsNullOrEmpty(json)
                    ? new Item()
                    : JsonUtility.FromJson<Item>(json);
                EnsureItemLists(item);
                return item;
            }

            private static void EnsureItemLists(Item item)
            {
                if (item == null)
                    return;

                if (item.inventory == null)
                    item.inventory = new List<Item>();
                if (item.secondary_inventory == null)
                    item.secondary_inventory = new List<Item>();

                for (int i = 0; i < item.inventory.Count; i++)
                    EnsureItemLists(item.inventory[i]);
                for (int i = 0; i < item.secondary_inventory.Count; i++)
                    EnsureItemLists(item.secondary_inventory[i]);
            }

            private static void WriteStrings(
                BinaryWriter writer,
                IList<string> values)
            {
                int count = values?.Count ?? 0;
                writer.Write(count);
                for (int i = 0; i < count; i++)
                    writer.Write(values[i] ?? string.Empty);
            }

            private static List<string> ReadStrings(
                BinaryReader reader,
                int maximum)
            {
                int count = reader.ReadInt32();
                if (count < 0 || count > maximum)
                    throw new InvalidDataException($"invalid profile list count {count}");

                var result = new List<string>(count);
                for (int i = 0; i < count; i++)
                    result.Add(reader.ReadString());
                return result;
            }
        }
    }
}
