using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace GraveyardKeeperCoop.Utils
{
    internal static class VisualSyncHelpers
    {
        public static byte PackSpriteFlags(SpriteRenderer sr)
        {
            if (sr == null) return 0;
            return (byte)((sr.gameObject.activeSelf ? 1 : 0) |
                          (sr.enabled ? 2 : 0) |
                          (sr.flipX ? 4 : 0) |
                          (sr.flipY ? 8 : 0));
        }

        public static bool TryApplySpriteDelta(VisualHierarchyMap map, string entry, Dictionary<string, Sprite> sprites)
        {
            if (map == null || string.IsNullOrEmpty(entry)) return false;

            int first = entry.IndexOf(':');
            int last = entry.LastIndexOf(':');
            if (first <= 0 || last <= first) return false;

            if (!uint.TryParse(entry.Substring(0, first), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint hash)) return false;
            if (!byte.TryParse(entry.Substring(last + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out byte flags)) return false;
            if (!map.TryGetSprite(hash, out SpriteRenderer sr) || sr == null) return false;

            string spriteName = SpriteNameNormalizer.Normalize(entry.Substring(first + 1, last - first - 1));
            bool active = (flags & 1) != 0;
            bool enabled = (flags & 2) != 0;
            bool flipX = (flags & 4) != 0;
            bool flipY = (flags & 8) != 0;

            if (sr.gameObject.activeSelf != active) sr.gameObject.SetActive(active);
            sr.enabled = enabled;
            sr.flipX = flipX;
            sr.flipY = flipY;

            if (string.IsNullOrEmpty(spriteName)) sr.sprite = null;
            else if (sprites != null && sprites.TryGetValue(spriteName, out Sprite sprite)) sr.sprite = sprite;

            return true;
        }

        public static Dictionary<string, Sprite> BuildSpriteLibrary()
        {
            var spriteLibrary = new Dictionary<string, Sprite>();
            Sprite[] sprites = Resources.FindObjectsOfTypeAll<Sprite>();
            for (int i = 0; i < sprites.Length; i++)
            {
                Sprite sprite = sprites[i];
                if (sprite == null) continue;
                string normalizedName = SpriteNameNormalizer.Normalize(sprite.name);
                if (!spriteLibrary.ContainsKey(normalizedName))
                    spriteLibrary[normalizedName] = sprite;
            }
            return spriteLibrary;
        }

        public static Transform FindChildByName(Transform root, string name)
        {
            if (root == null) return null;
            Transform[] transforms = root.GetComponentsInChildren<Transform>(true);
            for (int i = 0; i < transforms.Length; i++)
            {
                if (transforms[i] != null && transforms[i].name == name) return transforms[i];
            }
            return null;
        }

        public sealed class VisualHierarchyMap
        {
            private readonly Dictionary<uint, SpriteRenderer> sprites = new Dictionary<uint, SpriteRenderer>();
            private readonly Dictionary<uint, Transform> transforms = new Dictionary<uint, Transform>();
            private readonly bool includeTransforms;

            private VisualHierarchyMap(bool includeTransforms)
            {
                this.includeTransforms = includeTransforms;
            }

            public IReadOnlyDictionary<uint, SpriteRenderer> Sprites => sprites;
            public IReadOnlyDictionary<uint, Transform> Transforms => transforms;
            public int SpriteCount => sprites.Count;
            public int TransformCount => transforms.Count;

            public static VisualHierarchyMap From(Transform root, bool includeTransforms = false)
            {
                var map = new VisualHierarchyMap(includeTransforms);
                if (root != null) map.Traverse(root, string.Empty);
                return map;
            }

            public bool TryGetSprite(uint hash, out SpriteRenderer spriteRenderer) => sprites.TryGetValue(hash, out spriteRenderer);
            public bool TryGetTransform(uint hash, out Transform transform) => transforms.TryGetValue(hash, out transform);

            private void Traverse(Transform node, string parentPath)
            {
                var duplicateCounts = new Dictionary<string, int>(node.childCount);
                for (int i = 0; i < node.childCount; i++)
                {
                    string childName = node.GetChild(i).name;
                    duplicateCounts[childName] = duplicateCounts.TryGetValue(childName, out int count) ? count + 1 : 1;
                }

                var duplicateIndexes = new Dictionary<string, int>(node.childCount);
                for (int i = 0; i < node.childCount; i++)
                {
                    Transform child = node.GetChild(i);
                    string childName = child.name;
                    string segment = childName;
                    if (duplicateCounts[childName] > 1)
                    {
                        int index = duplicateIndexes.TryGetValue(childName, out int existing) ? existing : 0;
                        segment = childName + "[" + index + "]";
                        duplicateIndexes[childName] = index + 1;
                    }

                    string path = parentPath.Length > 0 ? parentPath + "/" + segment : segment;
                    uint hash = HashPath(path);

                    if (includeTransforms && !transforms.ContainsKey(hash))
                        transforms[hash] = child;

                    SpriteRenderer sr = child.GetComponent<SpriteRenderer>();
                    if (sr != null && !sprites.ContainsKey(hash))
                        sprites[hash] = sr;

                    Traverse(child, path);
                }
            }

            private static uint HashPath(string path)
            {
                uint hash = 2166136261u;
                for (int i = 0; i < path.Length; i++)
                {
                    hash ^= (byte)path[i];
                    hash *= 16777619u;
                }
                return hash;
            }
        }
    }
}
