using System;
using System.Collections.Generic;
using UnityEngine;
using Object = UnityEngine.Object;

namespace GraveyardKeeperCoop.UI
{
    /// <summary>
    /// A passive copy of one remote player's fishing display. It never opens or
    /// drives FishingGUI, so local fishing and other spectators remain independent.
    /// </summary>
    internal sealed class FishingSpectatorView : IDisposable
    {
        private GameObject _root;
        private UIPanel _panel;
        private GameObject _pulling;
        private UIWidget _rod;
        private UIWidget _processBack;
        private UIRect[] _rodVisuals;
        private Transform _fish;
        private UIProgressBar _progress;
        private Vector3 _trackCenter;
        private float _leftOffset = -90f;
        private float _rightOffset = 90f;
        private bool _disposed;
        private bool _loggedCloneFailure;

        public void Render(
            WorldGameObject actor,
            float rodPosition,
            float fishPosition,
            float rodSize,
            float progress,
            bool toRight)
        {
            if (!EnsureRoot() || !PositionAtActor(actor) || !EnsurePulling())
            {
                Hide();
                return;
            }

            float height = Mathf.Max(1, _processBack.height);
            _rod.height = Mathf.Max(1, Mathf.RoundToInt(Finite01(rodSize) * height));
            SetLocalY(_rod.transform, FinitePosition(rodPosition) * height);
            SetLocalY(_fish, FinitePosition(fishPosition) * height);
            foreach (UIRect visual in _rodVisuals)
            {
                if (visual != _rod)
                    visual.UpdateAnchors();
            }

            // The disabled progress component is only a native visual fill helper.
            // Neither its Start callbacks nor its input/update methods can run.
            _progress.Set(Finite01(progress), false);
            _progress.ForceUpdate();
            _pulling.transform.localPosition =
                new Vector3(toRight ? _rightOffset : _leftOffset, 0f, 0f) -
                Vector3.Scale(_trackCenter, _pulling.transform.localScale);

            _pulling.SetActive(true);
            _root.SetActive(true);
        }

        public void Hide()
        {
            if (_root != null)
                _root.SetActive(false);
        }

        public void Dispose()
        {
            _disposed = true;
            Hide();
            if (_root != null)
                Object.Destroy(_root);
            _root = null;
            _panel = null;
            _pulling = null;
            _rod = null;
            _processBack = null;
            _rodVisuals = null;
            _fish = null;
            _progress = null;
        }

        private bool EnsureRoot()
        {
            if (_disposed)
                return false;
            if (_root != null)
                return true;

            UIRoot uiRoot = MainGame.me?.ui_root;
            FishingGUI fishing = GUIElements.me?.fishing;
            if (uiRoot == null || !uiRoot.gameObject.activeInHierarchy || fishing == null)
                return false;

            _root = new GameObject("RemoteFishingSpectator");
            _root.SetActive(false);
            _root.layer = fishing.gameObject.layer;
            _root.transform.SetParent(uiRoot.transform, false);
            _panel = _root.AddComponent<UIPanel>();
            UIPanel sourcePanel = fishing.GetComponentInParent<UIPanel>();
            _panel.depth = sourcePanel != null ? sourcePanel.depth + 1 : 200;
            _panel.clipping = UIDrawCall.Clipping.None;
            _panel.alpha = 1f;
            return true;
        }

        private bool EnsurePulling()
        {
            if (_pulling != null)
                return true;

            FishingGUI source = GUIElements.me?.fishing;
            if (source?.pulling_go == null || source.process_back == null ||
                source.fishing_rod == null || source.fish_tf == null ||
                source.progress_bar == null)
                return false;

            // Clone under an inactive parent; strip input, animation and follow
            // scripts before anything in the clone gets an OnEnable/Start call.
            _root.SetActive(false);
            GameObject clone = Object.Instantiate(source.pulling_go, _root.transform, false);
            clone.name = "RemoteFishingPulling";
            clone.SetActive(false);
            Transform sourceRoot = source.pulling_go.transform;
            Transform cloneRoot = clone.transform;
            try
            {
                _rod = MapComponent(sourceRoot, cloneRoot, source.fishing_rod);
                _processBack = MapComponent(sourceRoot, cloneRoot, source.process_back);
                _fish = MapTransform(sourceRoot, cloneRoot, source.fish_tf);
                _progress = MapComponent(sourceRoot, cloneRoot, source.progress_bar);
                if (_rod == null || _processBack == null || _fish == null || _progress == null)
                    throw new InvalidOperationException("Native fishing widgets are outside pulling_go.");

                _progress.enabled = false;
                _progress.onChange = new List<EventDelegate>();
                _progress.onDragFinished = null;
                _progress.foregroundWidget = MapComponent(
                    sourceRoot, cloneRoot, source.progress_bar.foregroundWidget);
                _progress.backgroundWidget = MapComponent(
                    sourceRoot, cloneRoot, source.progress_bar.backgroundWidget);
                _progress.thumb = MapTransform(sourceRoot, cloneRoot, source.progress_bar.thumb);
                if (_progress.foregroundWidget == null)
                    throw new InvalidOperationException("Native fishing progress fill is outside pulling_go.");

                StripBehaviours(clone);
                _rodVisuals = _rod.GetComponentsInChildren<UIRect>(true);
                Vector3 sourceScale = sourceRoot.lossyScale;
                Vector3 parentScale = _root.transform.lossyScale;
                cloneRoot.localScale = new Vector3(
                    ScaleRatio(sourceScale.x, parentScale.x),
                    ScaleRatio(sourceScale.y, parentScale.y), 1f);
                cloneRoot.localRotation = Quaternion.identity;
                cloneRoot.localPosition = Vector3.zero;
                Vector3[] corners = _processBack.worldCorners;
                _trackCenter = cloneRoot.InverseTransformPoint((corners[0] + corners[2]) * 0.5f);
                _leftOffset = source.pull_pos_x_left;
                _rightOffset = source.pull_pos_x_right;

                // The local interaction hint belongs to the owner's controls.
                Transform hint = MapTransform(sourceRoot, cloneRoot, source.txt_pull_hint?.transform);
                if (hint != null)
                    hint.gameObject.SetActive(false);
                _pulling = clone;
                return true;
            }
            catch (Exception exception)
            {
                Object.Destroy(clone);
                _rod = null;
                _processBack = null;
                _rodVisuals = null;
                _fish = null;
                _progress = null;
                if (!_loggedCloneFailure)
                {
                    _loggedCloneFailure = true;
                    CoopMod.Logger.LogWarning($"[FishingSync] Could not create spectator minigame: {exception.Message}");
                }
                return false;
            }
        }

        private void StripBehaviours(GameObject clone)
        {
            foreach (Collider collider in clone.GetComponentsInChildren<Collider>(true))
            {
                collider.enabled = false;
                Object.DestroyImmediate(collider);
            }
            foreach (Collider2D collider in clone.GetComponentsInChildren<Collider2D>(true))
            {
                collider.enabled = false;
                Object.DestroyImmediate(collider);
            }
            foreach (Behaviour behaviour in clone.GetComponentsInChildren<Behaviour>(true))
            {
                if (behaviour == _progress || behaviour is UIWidget || behaviour is UIPanel)
                    continue;
                behaviour.enabled = false;
                Object.DestroyImmediate(behaviour);
            }
            foreach (UIRect rect in clone.GetComponentsInChildren<UIRect>(true))
            {
                // The native rod sprite is anchored to its rod-size widget.
                // Retain those cloned anchors so different rods resize correctly.
                ClearExternalAnchor(rect.leftAnchor, clone.transform);
                ClearExternalAnchor(rect.rightAnchor, clone.transform);
                ClearExternalAnchor(rect.topAnchor, clone.transform);
                ClearExternalAnchor(rect.bottomAnchor, clone.transform);
                rect.ResetAnchors();
            }
            foreach (UIWidget widget in clone.GetComponentsInChildren<UIWidget>(true))
            {
                widget.autoResizeBoxCollider = false;
                widget.onChange = null;
                widget.onPostFill = null;
                widget.onRender = null;
                widget.hitCheck = null;
            }
            foreach (Transform child in clone.GetComponentsInChildren<Transform>(true))
                child.gameObject.layer = _root.layer;

            // Keep any native clipping while moving child panels into this
            // independent spectator panel's sorting range.
            UIPanel[] panels = clone.GetComponentsInChildren<UIPanel>(true);
            int minDepth = int.MaxValue;
            foreach (UIPanel panel in panels)
                minDepth = Mathf.Min(minDepth, panel.depth);
            foreach (UIPanel panel in panels)
            {
                panel.depth = _panel.depth + 1 + panel.depth - minDepth;
                panel.alpha = 1f;
            }
        }

        private static void ClearExternalAnchor(UIRect.AnchorPoint anchor, Transform cloneRoot)
        {
            Transform target = anchor.target;
            if (target != null && target != cloneRoot && !target.IsChildOf(cloneRoot))
                anchor.target = null;
        }

        private bool PositionAtActor(WorldGameObject actor)
        {
            Camera worldCamera = MainGame.me?.world_cam;
            Camera uiCamera = MainGame.me?.gui_cam;
            if (actor == null || !actor.gameObject.activeInHierarchy ||
                worldCamera == null || uiCamera == null || _root.transform.parent == null)
                return false;

            Vector3 viewport = worldCamera.WorldToViewportPoint(actor.transform.position);
            if (viewport.z <= 0f || viewport.x < 0f || viewport.x > 1f ||
                viewport.y < 0f || viewport.y > 1f)
                return false;

            Vector3 screenPosition = worldCamera.WorldToScreenPoint(actor.bubble_pos);
            Transform parent = _root.transform.parent;
            Plane uiPlane = new Plane(parent.forward, parent.position);
            Ray uiRay = uiCamera.ScreenPointToRay(screenPosition);
            if (!uiPlane.Raycast(uiRay, out float distance))
                return false;

            Vector3 position = parent.InverseTransformPoint(uiRay.GetPoint(distance));
            position.z = 0f;
            _root.transform.localPosition = position;
            return true;
        }

        private static T MapComponent<T>(Transform sourceRoot, Transform cloneRoot, T source)
            where T : Component
        {
            Transform mapped = MapTransform(sourceRoot, cloneRoot, source != null ? source.transform : null);
            return mapped != null ? mapped.GetComponent<T>() : null;
        }

        private static Transform MapTransform(Transform sourceRoot, Transform cloneRoot, Transform source)
        {
            if (source == null)
                return null;
            if (source == sourceRoot)
                return cloneRoot;
            var parts = new List<string>();
            Transform current = source;
            while (current != null && current != sourceRoot)
            {
                parts.Add(current.name);
                current = current.parent;
            }
            if (current != sourceRoot)
                return null;
            parts.Reverse();
            return cloneRoot.Find(string.Join("/", parts.ToArray()));
        }

        private static void SetLocalY(Transform target, float y)
        {
            Vector3 position = target.localPosition;
            position.y = y;
            target.localPosition = position;
        }

        private static float Finite01(float value) =>
            float.IsNaN(value) || float.IsInfinity(value) ? 0f : Mathf.Clamp01(value);

        private static float FinitePosition(float value) =>
            float.IsNaN(value) || float.IsInfinity(value) ? 0f : Mathf.Clamp(value, -1f, 2f);

        private static float ScaleRatio(float value, float divisor) =>
            Mathf.Abs(divisor) < 0.00001f ? 1f : Mathf.Abs(value / divisor);
    }
}
