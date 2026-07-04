using UnityEngine;

namespace GraveyardKeeperCoop.Utils
{
    public static class MapCoordinateHelper
    {
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
    }
}
