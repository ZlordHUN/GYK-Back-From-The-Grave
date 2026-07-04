using UnityEngine;

namespace GraveyardKeeperCoop.UI
{
    /// <summary>
    /// Adds the game's native save-slot gamepad frame to custom controller controls.
    /// </summary>
    internal static class ControllerFocusFrame
    {
        private static Sprite cachedFrameSprite;
        private static bool warnedMissingFrame;

        public static GameObject Attach(
            GameObject target,
            GamepadNavigationItem navigationItem,
            int requestedWidth = 0,
            int requestedHeight = 0,
            UIWidget boundsWidget = null,
            int horizontalPadding = 14,
            int verticalPadding = 8)
        {
            if (target == null || navigationItem == null)
                return null;

            Transform existing = target.transform.Find("gamepad frame");
            if (existing != null)
            {
                UIWidget existingWidget = existing.GetComponent<UIWidget>();
                if (existingWidget != null)
                {
                    navigationItem.focus_frame = existing.gameObject;
                    existing.gameObject.SetActive(false);
                    ClearAnchors(existingWidget);
                    AttachGeometryDriver(
                        target,
                        existingWidget,
                        requestedWidth,
                        requestedHeight,
                        boundsWidget,
                        horizontalPadding,
                        verticalPadding);
                    return existing.gameObject;
                }

                // Some dialog-button prefabs use a transform-only frame container.
                // Its child pieces retain the template's original fixed width and
                // cannot be resized as one widget. Replace it with the same native
                // frame sprite as a single controllable widget.
                FindFrameSprite();
                Object.DestroyImmediate(existing.gameObject);
            }

            Sprite frameSprite = FindFrameSprite();
            if (frameSprite == null)
            {
                if (!warnedMissingFrame)
                {
                    warnedMissingFrame = true;
                    CoopMod.Logger.LogWarning(
                        "[ControllerUI] Native gamepad selection frame was not available");
                }
                return null;
            }

            ResolveTargetBounds(
                target,
                boundsWidget,
                out Vector3 targetCenter,
                out int targetWidth,
                out int targetHeight);
            int width = requestedWidth > 0
                ? requestedWidth
                : targetWidth + horizontalPadding;
            int height = requestedHeight > 0
                ? requestedHeight
                : targetHeight + verticalPadding;

            var frameObject = new GameObject("gamepad frame");
            frameObject.layer = target.layer;
            frameObject.transform.SetParent(target.transform, false);
            frameObject.transform.localPosition = targetCenter;
            frameObject.transform.localScale = Vector3.one;

            var frame = frameObject.AddComponent<UI2DSprite>();
            frame.sprite2D = frameSprite;
            frame.width = Mathf.Max(24, width);
            frame.height = Mathf.Max(18, height);
            frame.depth = FindHighestWidgetDepth(target) + 1;
            frame.color = new Color(1f, 0.843f, 0f, 1f);

            navigationItem.focus_frame = frameObject;
            frameObject.SetActive(false);
            AttachGeometryDriver(
                target,
                frame,
                requestedWidth,
                requestedHeight,
                boundsWidget,
                horizontalPadding,
                verticalPadding);
            return frameObject;
        }

        private static void AttachGeometryDriver(
            GameObject target,
            UIWidget frame,
            int requestedWidth,
            int requestedHeight,
            UIWidget boundsWidget,
            int horizontalPadding,
            int verticalPadding)
        {
            var driver = target.GetComponent<ControllerFocusFrameDriver>() ??
                         target.AddComponent<ControllerFocusFrameDriver>();
            driver.Initialize(
                frame,
                requestedWidth,
                requestedHeight,
                boundsWidget,
                horizontalPadding,
                verticalPadding);
        }

        internal static void UpdateGeometry(
            GameObject target,
            UIWidget frame,
            int requestedWidth,
            int requestedHeight,
            UIWidget boundsWidget,
            int horizontalPadding,
            int verticalPadding)
        {
            if (target == null || frame == null)
                return;

            ResolveTargetBounds(
                target,
                boundsWidget,
                out Vector3 targetCenter,
                out int targetWidth,
                out int targetHeight);
            int width = requestedWidth > 0
                ? requestedWidth
                : targetWidth + horizontalPadding;
            int height = requestedHeight > 0
                ? requestedHeight
                : targetHeight + verticalPadding;

            frame.transform.localPosition = targetCenter;
            frame.width = Mathf.Max(24, width);
            frame.height = Mathf.Max(18, height);
        }

        private static void ClearAnchors(UIWidget widget)
        {
            if (widget.leftAnchor != null)
                widget.leftAnchor.target = null;
            if (widget.rightAnchor != null)
                widget.rightAnchor.target = null;
            if (widget.topAnchor != null)
                widget.topAnchor.target = null;
            if (widget.bottomAnchor != null)
                widget.bottomAnchor.target = null;
        }

        private static Sprite FindFrameSprite()
        {
            if (cachedFrameSprite != null)
                return cachedFrameSprite;

            // Main-menu and dialog actions use the compact frame. Prefer it over
            // the much larger save-slot frame, whose thick side caps look wrong
            // when compressed to action-button dimensions.
            UI2DSprite[] sprites = Resources.FindObjectsOfTypeAll<UI2DSprite>();
            for (int i = 0; i < sprites.Length; i++)
            {
                string spriteName = sprites[i]?.sprite2D?.name?.ToLowerInvariant() ??
                                    string.Empty;
                if (spriteName.Contains("frame_small_selection"))
                {
                    cachedFrameSprite = sprites[i].sprite2D;
                    return cachedFrameSprite;
                }
            }

            SaveSlotGUI[] saveSlots = Resources.FindObjectsOfTypeAll<SaveSlotGUI>();
            for (int i = 0; i < saveSlots.Length; i++)
            {
                UIWidget frameWidget = saveSlots[i]?.gamepad_frame;
                if (frameWidget == null)
                    continue;

                UI2DSprite frame = frameWidget as UI2DSprite ??
                                   frameWidget.GetComponent<UI2DSprite>();
                if (frame?.sprite2D != null)
                {
                    cachedFrameSprite = frame.sprite2D;
                    return cachedFrameSprite;
                }

                UI2DSprite[] childFrames = frameWidget.GetComponentsInChildren<UI2DSprite>(true);
                for (int j = 0; j < childFrames.Length; j++)
                {
                    if (childFrames[j]?.sprite2D == null)
                        continue;

                    cachedFrameSprite = childFrames[j].sprite2D;
                    return cachedFrameSprite;
                }
            }

            for (int i = 0; i < sprites.Length; i++)
            {
                UI2DSprite sprite = sprites[i];
                string objectName = sprite?.name?.ToLowerInvariant() ?? string.Empty;
                if (sprite?.sprite2D != null &&
                    objectName.Contains("gamepad") &&
                    objectName.Contains("frame"))
                {
                    cachedFrameSprite = sprite.sprite2D;
                    return cachedFrameSprite;
                }
            }

            return null;
        }

        private static void ResolveTargetBounds(
            GameObject target,
            UIWidget boundsWidget,
            out Vector3 center,
            out int width,
            out int height)
        {
            if (boundsWidget != null)
            {
                ResolveWidgetBounds(
                    target,
                    boundsWidget,
                    out center,
                    out width,
                    out height);
                return;
            }

            float minX = float.MaxValue;
            float maxX = float.MinValue;
            float minY = float.MaxValue;
            float maxY = float.MinValue;
            bool found = false;

            UIWidget[] widgets = target.GetComponentsInChildren<UIWidget>(true);
            for (int i = 0; i < widgets.Length; i++)
            {
                UIWidget widget = widgets[i];
                if (widget == null ||
                    !widget.gameObject.activeInHierarchy ||
                    string.Equals(
                        widget.gameObject.name,
                        "gamepad frame",
                        System.StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                Vector3[] corners = widget.worldCorners;
                for (int j = 0; j < corners.Length; j++)
                {
                    Vector3 local = target.transform.InverseTransformPoint(corners[j]);
                    minX = Mathf.Min(minX, local.x);
                    maxX = Mathf.Max(maxX, local.x);
                    minY = Mathf.Min(minY, local.y);
                    maxY = Mathf.Max(maxY, local.y);
                }
                found = true;
            }

            if (found && maxX > minX && maxY > minY)
            {
                center = new Vector3(
                    (minX + maxX) * 0.5f,
                    (minY + maxY) * 0.5f,
                    0f);
                width = Mathf.RoundToInt(maxX - minX);
                height = Mathf.RoundToInt(maxY - minY);
                return;
            }

            BoxCollider collider = target.GetComponent<BoxCollider>() ??
                                   target.GetComponentInChildren<BoxCollider>(true);
            if (collider != null)
            {
                center = target.transform.InverseTransformPoint(
                    collider.transform.TransformPoint(collider.center));
                width = Mathf.RoundToInt(collider.size.x);
                height = Mathf.RoundToInt(collider.size.y);
                return;
            }

            UILabel label = target.GetComponentInChildren<UILabel>(true);
            if (label != null)
            {
                center = target.transform.InverseTransformPoint(label.transform.position);
                width = Mathf.Max(90, Mathf.RoundToInt(label.printedSize.x) + 24);
                height = Mathf.Max(28, Mathf.RoundToInt(label.printedSize.y) + 10);
                return;
            }

            center = Vector3.zero;
            width = 110;
            height = 32;
        }

        private static void ResolveWidgetBounds(
            GameObject target,
            UIWidget widget,
            out Vector3 center,
            out int width,
            out int height)
        {
            Vector3[] corners = widget.worldCorners;
            Vector3 bottomLeft = target.transform.InverseTransformPoint(corners[0]);
            Vector3 topRight = target.transform.InverseTransformPoint(corners[2]);
            center = (bottomLeft + topRight) * 0.5f;
            center.z = 0f;
            width = Mathf.RoundToInt(Mathf.Abs(topRight.x - bottomLeft.x));
            height = Mathf.RoundToInt(Mathf.Abs(topRight.y - bottomLeft.y));
        }

        private static int FindHighestWidgetDepth(GameObject target)
        {
            int depth = 0;
            UIWidget[] widgets = target.GetComponentsInChildren<UIWidget>(true);
            for (int i = 0; i < widgets.Length; i++)
            {
                if (widgets[i] != null)
                    depth = Mathf.Max(depth, widgets[i].depth);
            }
            return depth;
        }
    }

    internal sealed class ControllerFocusFrameDriver : MonoBehaviour
    {
        private UIWidget frame;
        private int requestedWidth;
        private int requestedHeight;
        private UIWidget boundsWidget;
        private int horizontalPadding;
        private int verticalPadding;

        public void Initialize(
            UIWidget targetFrame,
            int width,
            int height,
            UIWidget geometryWidget,
            int widthPadding,
            int heightPadding)
        {
            frame = targetFrame;
            requestedWidth = width;
            requestedHeight = height;
            boundsWidget = geometryWidget;
            horizontalPadding = widthPadding;
            verticalPadding = heightPadding;
            ControllerFocusFrame.UpdateGeometry(
                gameObject,
                frame,
                requestedWidth,
                requestedHeight,
                boundsWidget,
                horizontalPadding,
                verticalPadding);
        }

        private void LateUpdate()
        {
            ControllerFocusFrame.UpdateGeometry(
                gameObject,
                frame,
                requestedWidth,
                requestedHeight,
                boundsWidget,
                horizontalPadding,
                verticalPadding);
        }
    }
}
