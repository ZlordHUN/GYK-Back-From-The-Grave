using System;
using System.Collections.Generic;
using UnityEngine;

namespace GraveyardKeeperCoop.Utils
{
    public static class MapCoordinateHelper
    {
        private const int MaxExitDepth = 6;
        private const float UnresolvedRetryDelay = 5f;

        // Interior rooms live on separate parts of the world canvas, so projecting their raw
        // coordinates puts a player marker off the visible map. Cache the exterior door reached
        // through that room's teleport links instead.
        private static readonly Dictionary<string, Vector3> exteriorAnchors =
            new Dictionary<string, Vector3>();
        private static readonly Dictionary<string, float> unresolvedRetryAt =
            new Dictionary<string, float>();

        // Coordinate conversion settings - calculated from actual zone data:
        // CALIBRATION DATA - Based on actual player positions:
        // Reference Point (Church door, correct on map):
        //   World: (1630.1, -1459.5) -> Map: approximately (-92, -78) [church entrance]
        //
        // The old zone-based calibration was incorrect because zone centers
        // don't correspond to visible landmarks. Using actual player position instead.
        
        private const float scaleX = 0.05f;
        private const float scaleY = 0.05f;  // REDUCED further to fix vertical drift (was 0.08)
        
        // Reference point: Church door position (where it should be correct)
        private static readonly Vector2 worldReference = new Vector2(1630f, -1459f);
        private static readonly Vector2 mapReference = new Vector2(-150f, -78f);  // Church entrance on map (adjusted further left)

        /// <summary>
        /// Convert 3D world coordinates to 2D map GUI coordinates
        /// </summary>
        public static Vector2 WorldToMap(Vector3 worldPos)
        {
            // Calculate offset from reference point
            float worldDeltaX = worldPos.x - worldReference.x;
            float worldDeltaY = worldPos.y - worldReference.y;
            
            // Apply scale and add map reference position
            float mapX = (worldDeltaX * scaleX) + mapReference.x;
            float mapY = (worldDeltaY * scaleY) + mapReference.y;
            
            return new Vector2(mapX, mapY);
        }

        /// <summary>
        /// Convert a player position to map coordinates. For an interior zone, place the marker at
        /// the building's exterior teleport point instead of projecting the off-map room position.
        /// </summary>
        public static Vector2 PlayerWorldToMap(Vector3 worldPos)
        {
            WorldZone zone = WorldZone.GetZoneOfPoint(new Vector2(worldPos.x, worldPos.y));
            if (zone == null)
            {
                return WorldToMap(worldPos);
            }

            string zoneKey = GetZoneKey(zone);
            if (exteriorAnchors.TryGetValue(zoneKey, out Vector3 cachedAnchor))
            {
                return WorldToMap(cachedAnchor);
            }

            if (unresolvedRetryAt.TryGetValue(zoneKey, out float retryAt) &&
                Time.realtimeSinceStartup < retryAt)
            {
                return WorldToMap(worldPos);
            }

            var visitedZones = new HashSet<string>();
            if (!TryResolveExteriorAnchor(zone, worldPos, visitedZones, 0, out Vector3 exteriorAnchor))
            {
                // Outdoor zones and rooms whose teleport objects are not loaded keep the existing
                // behavior. Retry later so streamed-in teleport data can become available without
                // rescanning every object in an outdoor zone on every map frame.
                unresolvedRetryAt[zoneKey] = Time.realtimeSinceStartup + UnresolvedRetryDelay;
                return WorldToMap(worldPos);
            }

            unresolvedRetryAt.Remove(zoneKey);
            CoopMod.Logger.LogInfo(
                $"[MapIndicator] Zone '{zone.id}' mapped to exterior door " +
                $"({exteriorAnchor.x:F1}, {exteriorAnchor.y:F1})");
            return WorldToMap(exteriorAnchor);
        }

        private static bool TryResolveExteriorAnchor(
            WorldZone zone,
            Vector3 referencePosition,
            HashSet<string> visitedZones,
            int depth,
            out Vector3 exteriorAnchor)
        {
            exteriorAnchor = Vector3.zero;
            if (zone == null || depth >= MaxExitDepth)
            {
                return false;
            }

            string zoneKey = GetZoneKey(zone);
            if (exteriorAnchors.TryGetValue(zoneKey, out exteriorAnchor))
            {
                return true;
            }

            if (!visitedZones.Add(zoneKey))
            {
                return false;
            }

            List<WorldGameObject> exits = FindInteriorExits(zone, referencePosition);
            for (int i = 0; i < exits.Count; i++)
            {
                WorldGameObject exit = exits[i];
                string destinationTag = exit.custom_tag.Trim('_');
                WorldGameObject destination =
                    WorldMap.GetWorldGameObjectByCustomTag(destinationTag, true);
                if (destination == null || destination.is_removed)
                {
                    continue;
                }

                Vector3 destinationPosition = destination.transform.position;
                WorldZone destinationZone = WorldZone.GetZoneOfPoint(
                    new Vector2(destinationPosition.x, destinationPosition.y));

                // Nested rooms (cellars, DLC rooms, and similar) can first exit into another
                // interior. Continue through that room when it has its own interior-exit link.
                if (destinationZone != null && destinationZone != zone &&
                    HasInteriorExit(destinationZone))
                {
                    if (TryResolveExteriorAnchor(
                        destinationZone,
                        destinationPosition,
                        visitedZones,
                        depth + 1,
                        out exteriorAnchor))
                    {
                        exteriorAnchors[zoneKey] = exteriorAnchor;
                        visitedZones.Remove(zoneKey);
                        return true;
                    }

                    continue;
                }

                exteriorAnchor = destinationPosition;
                exteriorAnchors[zoneKey] = exteriorAnchor;
                visitedZones.Remove(zoneKey);
                return true;
            }

            visitedZones.Remove(zoneKey);
            return false;
        }

        private static List<WorldGameObject> FindInteriorExits(
            WorldZone zone,
            Vector3 referencePosition)
        {
            var exits = new List<WorldGameObject>();
            List<WorldGameObject> zoneObjects = zone.GetZoneWGOs();
            if (zoneObjects == null)
            {
                return exits;
            }

            for (int i = 0; i < zoneObjects.Count; i++)
            {
                WorldGameObject wgo = zoneObjects[i];
                if (!IsInteriorExit(wgo))
                {
                    continue;
                }

                exits.Add(wgo);
            }

            exits.Sort((left, right) =>
            {
                int priority = GetExitPriority(left).CompareTo(GetExitPriority(right));
                if (priority != 0)
                {
                    return priority;
                }

                float leftDistance = (left.transform.position - referencePosition).sqrMagnitude;
                float rightDistance = (right.transform.position - referencePosition).sqrMagnitude;
                return leftDistance.CompareTo(rightDistance);
            });

            return exits;
        }

        private static bool HasInteriorExit(WorldZone zone)
        {
            List<WorldGameObject> zoneObjects = zone.GetZoneWGOs();
            if (zoneObjects == null)
            {
                return false;
            }

            for (int i = 0; i < zoneObjects.Count; i++)
            {
                if (IsInteriorExit(zoneObjects[i]))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool IsInteriorExit(WorldGameObject wgo)
        {
            if (wgo == null || wgo.is_removed ||
                string.IsNullOrEmpty(wgo.obj_id) || string.IsNullOrEmpty(wgo.custom_tag) ||
                !wgo.obj_id.StartsWith("teleport", StringComparison.OrdinalIgnoreCase) ||
                !wgo.custom_tag.EndsWith("_", StringComparison.Ordinal))
            {
                return false;
            }

            string destinationTag = wgo.custom_tag.Trim('_');
            if (destinationTag.Length == 0)
            {
                return false;
            }

            char direction = char.ToLowerInvariant(destinationTag[destinationTag.Length - 1]);
            return direction == 'a' ||
                   wgo.obj_id.StartsWith("teleport_inside", StringComparison.OrdinalIgnoreCase);
        }

        private static int GetExitPriority(WorldGameObject wgo)
        {
            string destinationTag = wgo.custom_tag.Trim('_');
            char direction = char.ToLowerInvariant(destinationTag[destinationTag.Length - 1]);
            return direction == 'a' ? 0 : 1;
        }

        private static string GetZoneKey(WorldZone zone)
        {
            return !string.IsNullOrEmpty(zone.id)
                ? zone.id
                : $"instance:{zone.GetInstanceID()}";
        }
    }
}
