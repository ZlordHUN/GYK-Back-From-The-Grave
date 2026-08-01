using System;
using System.Text;
using HarmonyLib;

namespace GraveyardKeeperCoop.Patches
{
    [HarmonyPatch]
    internal static class PlayerParamSyncPatches
    {
        private static int lastRelationshipNotificationFrame = -1;
        private static string lastRelationshipNotificationKey;
        private static float lastRelationshipNotificationValue;

        private static void MarkDirty()
        {
            var sync = GraveyardKeeperCoop.Multiplayer.PlayerParamSync.Instance;
            if (sync == null) return;
            sync.MarkDirty();
        }

        private static void NotifyRelationshipMutation(string paramName)
        {
            if (string.IsNullOrEmpty(paramName) ||
                !paramName.StartsWith("_rel_", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            var player = MainGame.me?.player;
            ReconcileRelationshipAlias(paramName);
            float value = player?.GetParam(paramName, 0f) ?? 0f;
            int frame = UnityEngine.Time.frameCount;
            if (lastRelationshipNotificationFrame == frame &&
                string.Equals(
                    lastRelationshipNotificationKey,
                    paramName,
                    StringComparison.OrdinalIgnoreCase) &&
                Math.Abs(lastRelationshipNotificationValue - value) < 0.0001f)
            {
                return;
            }

            lastRelationshipNotificationFrame = frame;
            lastRelationshipNotificationKey = paramName;
            lastRelationshipNotificationValue = value;
            CoopMod.Logger.LogInfo(
                $"[PlayerParamSync] Local shared relationship changed {paramName}={value:F0}; sending immediately");

            // PlayerParamSync is host-canonical, so a client-owned FlowScript
            // must publish the mutation through the bidirectional parity path.
            // The host merges it monotonically and immediately echoes the
            // resulting canonical value to every peer.
            GraveyardKeeperCoop.Multiplayer.PlayerParitySync.Instance
                ?.SendLocalSnapshot(force: true);
        }

        private static void ReconcileRelationshipAlias(string changedKey)
        {
            const string prefix = "_rel_";
            if (string.IsNullOrEmpty(changedKey) ||
                !changedKey.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            WorldGameObject player = MainGame.me?.player;
            GameRes playerParams = player?.data?.GetParams();
            if (playerParams == null)
                return;

            string changedId = changedKey.Substring(prefix.Length);
            ReconcileRelationshipKeys(
                playerParams,
                changedKey,
                GetDisplayedRelationshipKey(changedId));

            var knownNpcs = MainGame.me?.save?.known_npcs?.npcs;
            if (knownNpcs == null)
                return;

            for (int i = 0; i < knownNpcs.Count; i++)
            {
                KnownNPC npc = knownNpcs[i];
                if (npc == null || string.IsNullOrEmpty(npc.npc_id))
                    continue;

                string rawKey = prefix + npc.npc_id;
                string displayedKey = GetDisplayedRelationshipKey(npc.npc_id);
                if (string.Equals(changedKey, rawKey, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(changedKey, displayedKey, StringComparison.OrdinalIgnoreCase))
                {
                    ReconcileRelationshipKeys(playerParams, rawKey, displayedKey);
                }
            }
        }

        private static void ReconcileKnownNpcRelationship(KnownNPC npc)
        {
            if (npc == null || string.IsNullOrEmpty(npc.npc_id))
                return;

            GameRes playerParams = MainGame.me?.player?.data?.GetParams();
            if (playerParams == null)
                return;

            string rawKey = "_rel_" + npc.npc_id;
            string displayedKey = GetDisplayedRelationshipKey(npc.npc_id);
            if (ReconcileRelationshipKeys(playerParams, rawKey, displayedKey))
            {
                MarkDirty();
                GraveyardKeeperCoop.Multiplayer.PlayerParitySync.Instance
                    ?.SendLocalSnapshot(force: true);
            }
        }

        private static string GetDisplayedRelationshipKey(string npcId)
        {
            ObjectDefinition definition =
                GameBalance.me?.GetDataOrNull<ObjectDefinition>(npcId);
            string displayedId = definition != null &&
                                 !string.IsNullOrEmpty(definition.npc_alias)
                ? definition.npc_alias
                : npcId;
            return "_rel_" + displayedId;
        }

        private static bool ReconcileRelationshipKeys(
            GameRes playerParams,
            string firstKey,
            string secondKey)
        {
            if (playerParams == null ||
                string.IsNullOrEmpty(firstKey) ||
                string.IsNullOrEmpty(secondKey) ||
                string.Equals(firstKey, secondKey, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            float first = playerParams.Get(firstKey, 0f);
            float second = playerParams.Get(secondKey, 0f);
            float canonical = Math.Max(first, second);
            bool changed = false;
            if (canonical > first + 0.0001f)
            {
                playerParams.Set(firstKey, canonical);
                changed = true;
            }
            if (canonical > second + 0.0001f)
            {
                playerParams.Set(secondKey, canonical);
                changed = true;
            }

            if (changed)
            {
                CoopMod.Logger.LogInfo(
                    $"[PlayerParamSync] Reconciled relationship aliases " +
                    $"{firstKey}={first:F0}, {secondKey}={second:F0} -> {canonical:F0}");
            }
            return changed;
        }

        private static void NotifyRelationshipMutations(GameRes gameRes)
        {
            if (gameRes == null)
                return;

            foreach (GameResAtom atom in gameRes.ToAtomList(1f))
                NotifyRelationshipMutation(atom?.type);
        }

        private static bool IsSyncEnabled()
        {
            var onlineCoop = GraveyardKeeperCoop.Network.OnlineCoopManager.Instance;
            return onlineCoop != null && onlineCoop.IsOnlineCoopEnabled;
        }

        [HarmonyPatch(typeof(WorldGameObject), "AddToParams", typeof(GameRes))]
        [HarmonyPostfix]
        internal static void AddToParams_GameRes_Postfix(
            WorldGameObject __instance,
            GameRes game_res)
        {
            if (__instance == null || !__instance.is_player) return;
            if (!IsSyncEnabled()) return;
            MarkDirty();
            NotifyRelationshipMutations(game_res);
        }

        [HarmonyPatch(typeof(WorldGameObject), "AddToParams", typeof(string), typeof(float))]
        [HarmonyPostfix]
        internal static void AddToParams_StringFloat_Postfix(
            WorldGameObject __instance,
            string param_name)
        {
            if (__instance == null || !__instance.is_player) return;
            if (!IsSyncEnabled()) return;
            MarkDirty();
            NotifyRelationshipMutation(param_name);
        }

        [HarmonyPatch(typeof(WorldGameObject), "SubParam", typeof(string), typeof(float))]
        [HarmonyPostfix]
        internal static void SubParam_Postfix(
            WorldGameObject __instance,
            string param_name)
        {
            if (__instance == null || !__instance.is_player) return;
            if (!IsSyncEnabled()) return;
            MarkDirty();
        }

        [HarmonyPatch(typeof(WorldGameObject), "SetParam", typeof(string), typeof(float))]
        [HarmonyPostfix]
        internal static void SetParam_StringFloat_Postfix(
            WorldGameObject __instance,
            string param_name)
        {
            if (__instance == null || !__instance.is_player) return;
            if (!IsSyncEnabled()) return;
            MarkDirty();
            NotifyRelationshipMutation(param_name);
        }

        [HarmonyPatch(typeof(WorldGameObject), "SetParam", typeof(GameRes))]
        [HarmonyPostfix]
        internal static void SetParam_GameRes_Postfix(
            WorldGameObject __instance,
            GameRes game_res)
        {
            if (__instance == null || !__instance.is_player) return;
            if (!IsSyncEnabled()) return;
            MarkDirty();
            NotifyRelationshipMutations(game_res);
        }

        // Every additive player resource eventually reaches this primitive.
        // Restrict by reference identity so inventories, NPCs and WGOs do not
        // turn this into a global GameRes hot-path hook.
        [HarmonyPatch(typeof(GameRes), "Add", typeof(string), typeof(float))]
        [HarmonyPostfix]
        internal static void GameRes_Add_Postfix(
            GameRes __instance,
            string stype)
        {
            WorldGameObject player = MainGame.me?.player;
            if (player?.data == null ||
                !ReferenceEquals(__instance, player.data.GetParams()))
            {
                return;
            }
            if (!IsSyncEnabled())
                return;

            MarkDirty();
            NotifyRelationshipMutation(stype);
        }

        // Dialogue answer rewards bypass WorldGameObject.AddToParams and write
        // through player.data directly. Gerry's beer reward is one such SmartRes
        // (_rel is expanded to _rel_<npc id> when the answer is presented).
        [HarmonyPatch(typeof(WorldGameObject), "ReceiveSmartRes")]
        [HarmonyPostfix]
        internal static void ReceiveSmartRes_Postfix(
            WorldGameObject __instance,
            SmartRes res)
        {
            if (__instance == null ||
                res == null ||
                !ReferenceEquals(__instance, MainGame.me?.player) ||
                !IsSyncEnabled())
            {
                return;
            }

            if (res.res_type != SmartRes.ResType.GameRes)
                return;

            NotifyRelationshipMutation(res.res?.type);
        }

        [HarmonyPatch(typeof(NPCItemGUI), "Draw")]
        [HarmonyPrefix]
        internal static void NpcItemGui_Draw_Prefix(KnownNPC npc)
        {
            ReconcileKnownNpcRelationship(npc);
        }

        [HarmonyPatch(typeof(NPCItemGUI), "Draw")]
        [HarmonyPostfix]
        internal static void NpcItemGui_Draw_Postfix(KnownNPC npc)
        {
            if (npc == null ||
                string.IsNullOrEmpty(npc.npc_id) ||
                npc.npc_id.IndexOf("skull", StringComparison.OrdinalIgnoreCase) < 0)
            {
                return;
            }

            ObjectDefinition definition =
                GameBalance.me?.GetDataOrNull<ObjectDefinition>(npc.npc_id);
            string displayedKey = GetDisplayedRelationshipKey(npc.npc_id);
            GameRes playerParams = MainGame.me?.player?.data?.GetParams();
            var observed = new StringBuilder();
            if (playerParams != null)
            {
                foreach (string type in playerParams.Types)
                {
                    if (string.IsNullOrEmpty(type) ||
                        !type.StartsWith("_rel_", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (observed.Length > 0)
                        observed.Append(", ");
                    observed.Append(type)
                        .Append('=')
                        .Append(playerParams.Get(type, 0f).ToString("F0"));
                }
            }

            CoopMod.Logger.LogInfo(
                $"[PlayerParamSync] Known NPC relationship read npc={npc.npc_id}, " +
                $"alias={definition?.npc_alias ?? ""}, key={displayedKey}, " +
                $"value={WorldGameObject.GetRelation(npc.npc_id)}, " +
                $"stored=[{observed}]");
        }

        [HarmonyPatch(typeof(GameLogics), "ForceExecute", new System.Type[] { typeof(string) })]
        [HarmonyPostfix]
        internal static void ForceExecute_Postfix()
        {
            if (!IsSyncEnabled()) return;
            MarkDirty();
        }

        [HarmonyPatch(typeof(GameLogics), "ForceExecuteCond")]
        [HarmonyPostfix]
        internal static void ForceExecuteCond_Postfix()
        {
            if (!IsSyncEnabled()) return;
            MarkDirty();
        }
    }
}
