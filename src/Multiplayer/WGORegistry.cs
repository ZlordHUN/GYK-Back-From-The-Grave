using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace GraveyardKeeperCoop.Multiplayer
{
    /// <summary>
    /// Shared runtime index for WorldGameObjects. This avoids each sync lane scanning
    /// the whole world repeatedly and gives remote apply paths one consistent resolver.
    /// </summary>
    public class WGORegistry : MonoBehaviour
    {
        private static WGORegistry _instance;
        public static WGORegistry Instance => _instance;

        private readonly Dictionary<long, WorldGameObject> byUniqueId = new Dictionary<long, WorldGameObject>();
        private readonly List<WorldGameObject> snapshot = new List<WorldGameObject>();

        private bool dirty = true;

        public int Count => byUniqueId.Count;

        private void Awake()
        {
            if (_instance != null && _instance != this)
            {
                Destroy(gameObject);
                return;
            }

            _instance = this;
            DontDestroyOnLoad(gameObject);
            CoopMod.Logger.LogInfo("[WGORegistry] Initialized");
        }

        public void MarkDirty()
        {
            dirty = true;
        }

        public void RebuildNow()
        {
            Rebuild();
        }

        public void Register(WorldGameObject wgo)
        {
            if (!IsUsable(wgo))
                return;

            if (!snapshot.Contains(wgo))
                snapshot.Add(wgo);

            if (wgo.unique_id != 0L)
                byUniqueId[wgo.unique_id] = wgo;

            dirty = false;
        }

        public void Unregister(long uniqueId)
        {
            if (uniqueId == 0L)
                return;

            byUniqueId.Remove(uniqueId);
            snapshot.RemoveAll(wgo => !IsUsable(wgo) || wgo.unique_id == uniqueId);
        }

        public bool TryGet(long uniqueId, out WorldGameObject wgo)
        {
            wgo = null;
            if (uniqueId == 0L)
                return false;

            if (dirty)
                Rebuild();

            if (byUniqueId.TryGetValue(uniqueId, out wgo) && IsUsable(wgo))
                return true;

            return false;
        }

        public List<WorldGameObject> SnapshotAll()
        {
            if (dirty)
                Rebuild();

            // Hot-path callers only enumerate this list. Returning the cached list avoids
            // allocating/copying tens of thousands of WGO references several times a second.
            return snapshot;
        }

        public WorldGameObject Resolve(long uniqueId, string objId, string customTag, Vector3 position, float maxDistance = 128f)
        {
            if (TryGet(uniqueId, out var byId))
                return byId;

            if (!string.IsNullOrEmpty(customTag))
            {
                try
                {
                    WorldGameObject byTag = WorldMap.GetWorldGameObjectByCustomTag(customTag, true);
                    if (IsUsable(byTag))
                        return byTag;
                }
                catch { }
            }

            return ResolveNearest(objId, position, maxDistance);
        }

        public WorldGameObject ResolveNearest(string objId, Vector3 position, float maxDistance = 128f)
        {
            if (string.IsNullOrEmpty(objId))
                return null;

            if (dirty)
                Rebuild();

            WorldGameObject nearest = null;
            float nearestSqr = maxDistance * maxDistance;
            for (int i = 0; i < snapshot.Count; i++)
            {
                WorldGameObject candidate = snapshot[i];
                if (!IsUsable(candidate) || candidate.obj_id != objId)
                    continue;

                float distance = (candidate.transform.position - position).sqrMagnitude;
                if (distance < nearestSqr)
                {
                    nearestSqr = distance;
                    nearest = candidate;
                }
            }

            return nearest;
        }

        private void Rebuild()
        {
            byUniqueId.Clear();
            snapshot.Clear();

            List<WorldGameObject> objects = null;
            try
            {
                objects = MainGame.me?.GetListOfWorldObjects();
            }
            catch { }

            if (objects == null && WorldMap.objs != null)
                objects = WorldMap.objs;

            if (objects != null)
            {
                for (int i = 0; i < objects.Count; i++)
                {
                    WorldGameObject wgo = objects[i];
                    if (!IsUsable(wgo))
                        continue;

                    snapshot.Add(wgo);
                    if (wgo.unique_id != 0L && !byUniqueId.ContainsKey(wgo.unique_id))
                        byUniqueId[wgo.unique_id] = wgo;
                }
            }

            dirty = false;
        }

        private static bool IsUsable(WorldGameObject wgo)
        {
            if (wgo == null || wgo.gameObject == null)
                return false;
            if (wgo.GetComponent<RemoteBuildingPreviewMarker>() != null)
                return false;

            Scene scene = wgo.gameObject.scene;
            return scene.IsValid() && !string.IsNullOrEmpty(scene.name);
        }
    }
}
