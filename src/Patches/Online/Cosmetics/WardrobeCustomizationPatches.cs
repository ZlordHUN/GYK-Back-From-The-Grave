using System;
using System.Collections.Generic;
using System.Text;
using GraveyardKeeperCoop.UI;
using HarmonyLib;
using UnityEngine;

namespace GraveyardKeeperCoop.Patches
{
    internal static class KeeperWardrobeInteraction
    {
        // The keeper's bedroom is centered around the vanilla new-game spawn.
        // Spatial scoping protects the fallback name match from other cupboards.
        private static readonly Vector2 KeeperHouseCenter = new Vector2(2496f, -6336f);
        private const float KeeperHouseRadius = 560f;

        private static readonly string[] WardrobeTokens =
        {
            "wardrobe",
            "dresser",
            "closet",
            "cupboard",
            "clothes_cupboard",
        };

        private static readonly HashSet<int> ConfirmedWardrobes = new HashSet<int>();

        public static bool IsKeeperWardrobe(WorldGameObject wgo)
        {
            if (wgo == null || wgo.gameObject == null || wgo.obj_def == null)
                return false;

            int instanceId = wgo.GetInstanceID();
            if (ConfirmedWardrobes.Contains(instanceId))
                return true;

            // GameSave migrations identify this object explicitly. Prefer these
            // stable identifiers so visual prefab names and save age do not matter.
            if (string.Equals(wgo.obj_id, "cupboard_home", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(wgo.custom_tag, "home_cupboard", StringComparison.OrdinalIgnoreCase))
            {
                ConfirmWardrobe(wgo, instanceId);
                return true;
            }

            Vector2 offset = (Vector2)wgo.transform.position - KeeperHouseCenter;
            if (offset.sqrMagnitude > KeeperHouseRadius * KeeperHouseRadius)
                return false;

            string identity = BuildIdentity(wgo);
            if (!ContainsWardrobeToken(identity) ||
                identity.IndexOf("euric", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return false;
            }

            ConfirmWardrobe(wgo, instanceId);
            return true;
        }

        private static void ConfirmWardrobe(WorldGameObject wgo, int instanceId)
        {
            ConfirmedWardrobes.Add(instanceId);
            CoopMod.Logger.LogInfo(
                $"[CharacterCustomization] Keeper wardrobe detected: " +
                $"obj_id='{wgo.obj_id}', custom_tag='{wgo.custom_tag}', " +
                $"name='{wgo.name}', pos={wgo.transform.position}");
        }

        private static bool ContainsWardrobeToken(string identity)
        {
            for (int i = 0; i < WardrobeTokens.Length; i++)
            {
                if (identity.IndexOf(WardrobeTokens[i], StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            }

            return false;
        }

        private static string BuildIdentity(WorldGameObject wgo)
        {
            StringBuilder result = new StringBuilder(256);
            Append(result, wgo.name);
            Append(result, wgo.obj_id);
            Append(result, wgo.custom_tag);
            Append(result, wgo.obj_def?.id);

            if (wgo.wop != null)
            {
                Append(result, wgo.wop.name);
                Append(result, wgo.wop.gameObject?.name);
            }

            Transform[] transforms = wgo.GetComponentsInChildren<Transform>(true);
            for (int i = 0; i < transforms.Length; i++)
                Append(result, transforms[i]?.name);

            SpriteRenderer[] renderers = wgo.GetComponentsInChildren<SpriteRenderer>(true);
            for (int i = 0; i < renderers.Length; i++)
            {
                Append(result, renderers[i]?.name);
                Append(result, renderers[i]?.sprite?.name);
            }

            return result.ToString();
        }

        private static void Append(StringBuilder target, string value)
        {
            if (string.IsNullOrEmpty(value))
                return;

            if (target.Length > 0)
                target.Append('|');
            target.Append(value);
        }
    }

    [HarmonyPatch(typeof(ObjectDefinition), nameof(ObjectDefinition.IsNotInteractive))]
    internal static class KeeperWardrobeInteractivePatch
    {
        [HarmonyPostfix]
        private static void Postfix(WorldGameObject wgo, ref bool __result)
        {
            if (__result && KeeperWardrobeInteraction.IsKeeperWardrobe(wgo))
                __result = false;
        }
    }

    [HarmonyPatch(typeof(ComponentsManager), nameof(ComponentsManager.RefreshBubblesData))]
    internal static class KeeperWardrobeHintPatch
    {
        [HarmonyPostfix]
        private static void Postfix(ComponentsManager __instance)
        {
            WorldGameObject wgo = __instance?.wgo;
            if (wgo == null || !wgo.prepared_for_interaction ||
                !KeeperWardrobeInteraction.IsKeeperWardrobe(wgo))
            {
                return;
            }

            string hint = GameKeyTip.Get(
                GameKey.Interaction,
                "Customize",
                true,
                false,
                false,
                false);
            wgo.SetBubbleWidgetData(hint, BubbleWidgetData.WidgetID.Interaction);
        }
    }

    [HarmonyPatch(typeof(WorldGameObject), nameof(WorldGameObject.Interact))]
    internal static class KeeperWardrobeOpenPatch
    {
        [HarmonyPrefix]
        [HarmonyPriority(Priority.First)]
        private static bool Prefix(
            WorldGameObject __instance,
            WorldGameObject other_obj,
            bool interaction_start)
        {
            if (!KeeperWardrobeInteraction.IsKeeperWardrobe(__instance))
                return true;

            if (!interaction_start)
                return false;

            BaseCharacterComponent character = other_obj?.components?.character;
            WorldGameObject localPlayer = MainGame.me?.player;
            bool isLocalPlayer = other_obj != null && other_obj.is_player &&
                                 (other_obj == localPlayer ||
                                  (character != null && character.can_be_locally_controlled));
            if (!isLocalPlayer)
            {
                CoopMod.Logger.LogWarning(
                    $"[CharacterCustomization] Ignored wardrobe interaction from non-local actor " +
                    $"'{other_obj?.name ?? "null"}' (local='{localPlayer?.name ?? "null"}', " +
                    $"is_player={other_obj?.is_player}, local_control={character?.can_be_locally_controlled})");
                return false;
            }

            TryOpen(__instance, "WorldGameObject.Interact");
            return false;
        }

        internal static bool TryOpen(WorldGameObject wardrobe, string source)
        {
            try
            {
                CharacterCustomizationGUI gui = CharacterCustomizationGUI.Instance ??
                                                   CharacterCustomizationGUI.Create();
                if (gui == null)
                {
                    CoopMod.Logger.LogError(
                        $"[CharacterCustomization] Could not create wardrobe UI from {source}");
                    return false;
                }

                if (!gui.is_shown)
                    gui.OpenForWardrobe(wardrobe);
                return gui.is_shown;
            }
            catch (Exception ex)
            {
                CoopMod.Logger.LogError(
                    $"[CharacterCustomization] Failed to open wardrobe UI from {source}: {ex}");
                return false;
            }
        }
    }

    /// <summary>
    /// Current multiplayer builds have several WorldGameObject.Interact prefixes.
    /// If one of those gates the call before the wardrobe prefix can open the UI,
    /// recover at the local player's interaction component after it has resolved
    /// the target. This is deliberately limited to the interaction key-down frame.
    /// </summary>
    [HarmonyPatch(typeof(InteractionComponent), nameof(InteractionComponent.Interact))]
    internal static class KeeperWardrobeInteractionFallbackPatch
    {
        private static readonly System.Reflection.FieldInfo TargetField =
            AccessTools.Field(typeof(InteractionComponent), "_target_obj");
        private static bool _loggedMissingTargetField;

        [HarmonyPostfix]
        private static void Postfix(
            InteractionComponent __instance,
            bool interaction_start,
            bool __result)
        {
            if (!__result || !interaction_start || __instance?.wgo == null ||
                __instance.wgo != MainGame.me?.player)
            {
                return;
            }

            if (TargetField == null)
            {
                if (!_loggedMissingTargetField)
                {
                    _loggedMissingTargetField = true;
                    CoopMod.Logger.LogError(
                        "[CharacterCustomization] InteractionComponent._target_obj was not found");
                }
                return;
            }

            WorldGameObject target = TargetField.GetValue(__instance) as WorldGameObject;
            if (!KeeperWardrobeInteraction.IsKeeperWardrobe(target) ||
                CharacterCustomizationGUI.Instance?.is_shown == true)
            {
                return;
            }

            KeeperWardrobeOpenPatch.TryOpen(target, "InteractionComponent.Interact fallback");
        }
    }
}
