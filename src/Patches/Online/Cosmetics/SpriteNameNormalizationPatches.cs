using HarmonyLib;
using GraveyardKeeperCoop.Utils;

namespace GraveyardKeeperCoop.Patches
{
    [HarmonyPatch]
    internal static class SpriteNameNormalizationPatches
    {
        [HarmonyPatch(typeof(EasySpritesCollection), "GetSprite")]
        [HarmonyPrefix]
        private static void EasySpritesCollection_GetSprite_Prefix(ref string sprite_name, ref string sprite_if_not_found)
        {
            sprite_name = SpriteNameNormalizer.Normalize(sprite_name);
            sprite_if_not_found = SpriteNameNormalizer.Normalize(sprite_if_not_found);
        }

        [HarmonyPatch(typeof(EasySpriteCollectionSub), "GetSprite")]
        [HarmonyPrefix]
        private static void EasySpriteCollectionSub_GetSprite_Prefix(ref string spr_name)
        {
            spr_name = SpriteNameNormalizer.Normalize(spr_name);
        }

        [HarmonyPatch(typeof(CharacterSkin), "ReplaceSpriteName")]
        [HarmonyPostfix]
        private static void CharacterSkin_ReplaceSpriteName_Postfix(ref string __result)
        {
            __result = SpriteNameNormalizer.Normalize(__result);
        }
    }
}
