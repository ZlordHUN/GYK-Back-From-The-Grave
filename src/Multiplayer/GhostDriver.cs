using System.Collections.Generic;
using UnityEngine;

namespace GraveyardKeeperCoop.Multiplayer
{
    /// <summary>
    /// Handles smooth interpolation of remote player movement.
    /// Uses time-synchronized playback with interpolation delay for smooth visuals.
    /// Includes Hermite spline interpolation, adaptive jitter buffer,
    /// walk-speed-clamped extrapolation, and velocity-based smoothing.
    /// </summary>
    public class GhostDriver : MonoBehaviour
    {
        // Whether to use local position instead of world position
        private bool useLocalPosition = false;
        
        // Cached transform reference
        private Transform _transform;
        
        // Sample structure for position history
        private struct Sample
        {
            public float time;
            public Vector2 position;
            public Vector2 velocity;
        }
        
        // Configuration constants
        private const float MIN_INTERP_DELAY = 0.050f;  // 50ms minimum jitter buffer
        private const float MAX_INTERP_DELAY = 0.200f;  // 200ms maximum jitter buffer
        private const float DEFAULT_INTERP_DELAY = 0.100f; // 100ms initial value
        private const float MAX_EXTRAP_SEC = 0.15f;     // Maximum extrapolation time
        private const float SNAP_DIST = 6f;             // Distance threshold for instant teleport
        private const float SMOOTH_TIME = 0.06f;        // SmoothDamp time (matched to original GYKMP)
        private const int MAX_SAMPLES = 32;             // Maximum position samples to keep
        private const float MAX_WALK_SPEED = 5f;        // Maximum plausible walk speed (units/sec) for extrapolation clamping
        private const float JITTER_ADAPT_RATE = 0.05f;  // How fast the jitter buffer adapts (per packet)
        
        // Adaptive jitter buffer state
        private float _interpDelay = DEFAULT_INTERP_DELAY;
        private float _jitterEstimate;                   // Exponential moving average of inter-packet jitter
        private float _lastPacketArrivalTime;            // Local time of last packet arrival
        private float _lastPacketInterval;               // Previous inter-packet interval for jitter calc
        
        // Time offset tracking
        private bool _hasTimeOffset;
        private float _timeOffset;
        private float _offsetLerpRate = 0.1f;
        
        // Position history buffer
        private readonly List<Sample> _buffer = new List<Sample>(MAX_SAMPLES);
        
        // Smoothing velocity
        private Vector2 _smoothVelocity;
        
        // Flag to enable/disable the driver
        private bool _isEnabled = false;
        
        private void Awake()
        {
            _transform = transform;
        }
        
        /// <summary>
        /// Enable the ghost driver
        /// </summary>
        public void Enable()
        {
            _isEnabled = true;
            _hasTimeOffset = false;
            _buffer.Clear();
            _smoothVelocity = Vector2.zero;
            _interpDelay = DEFAULT_INTERP_DELAY;
            _jitterEstimate = 0f;
            _lastPacketArrivalTime = 0f;
            _lastPacketInterval = 0f;
            CoopMod.Logger.LogDebug("[GhostDriver] Enabled");
        }
        
        /// <summary>
        /// Disable the ghost driver
        /// </summary>
        public void Disable()
        {
            _isEnabled = false;
            _buffer.Clear();
            CoopMod.Logger.LogDebug("[GhostDriver] Disabled");
        }
        
        /// <summary>
        /// Check if driver is enabled
        /// </summary>
        public bool IsEnabled => _isEnabled;
        
        /// <summary>
        /// Push a new position snapshot received from the network.
        /// </summary>
        /// <param name="position">The position received</param>
        /// <param name="hostTime">The host's Time.time when this position was captured (optional, use local time if not available)</param>
        /// <summary>
        /// Push a new position snapshot received from the network.
        /// Uses realtimeSinceStartup for monotonic timing (unaffected by Time.timeScale/pauses).
        /// </summary>
        /// <param name="position">The position received</param>
        /// <param name="velocity">Velocity from the sender (if available). Fallback: computed locally.</param>
        /// <param name="hostTime">The host's Time.realtimeSinceStartup when captured (or -1 for local time)</param>
        public void PushSnapshot(Vector2 position, Vector2 velocity, float hostTime = -1f)
        {
            if (!_isEnabled) return;
            
            // Use realtimeSinceStartup — monotonic, unaffected by timeScale or pauses
            float snapshotTime = hostTime >= 0 ? hostTime : Time.realtimeSinceStartup;
            float localTime = Time.realtimeSinceStartup;
            
            // === Adaptive jitter buffer ===
            // Track inter-packet arrival timing to measure network jitter
            if (_lastPacketArrivalTime > 0f)
            {
                float interval = localTime - _lastPacketArrivalTime;
                if (_lastPacketInterval > 0f)
                {
                    // Jitter = absolute deviation from the previous interval
                    float jitter = Mathf.Abs(interval - _lastPacketInterval);
                    // Exponential moving average with fast rise, slow decay
                    float alpha = jitter > _jitterEstimate ? 0.3f : JITTER_ADAPT_RATE;
                    _jitterEstimate = Mathf.Lerp(_jitterEstimate, jitter, alpha);
                    
                    // Target delay = 2× jitter estimate, clamped to [50ms, 200ms]
                    float targetDelay = Mathf.Clamp(_jitterEstimate * 2f, MIN_INTERP_DELAY, MAX_INTERP_DELAY);
                    _interpDelay = Mathf.Lerp(_interpDelay, targetDelay, JITTER_ADAPT_RATE);
                }
                _lastPacketInterval = interval;
            }
            _lastPacketArrivalTime = localTime;
            
            // Calculate and smooth the time offset
            float desiredOffset = snapshotTime - localTime;
            if (!_hasTimeOffset)
            {
                _timeOffset = desiredOffset;
                _hasTimeOffset = true;
            }
            else
            {
                _timeOffset = Mathf.Lerp(_timeOffset, desiredOffset, _offsetLerpRate);
            }
            
            // If no velocity provided, compute from previous sample (less accurate fallback)
            if (velocity == Vector2.zero && _buffer.Count > 0)
            {
                var last = _buffer[_buffer.Count - 1];
                float dt = Mathf.Max(0.0001f, snapshotTime - last.time);
                velocity = (position - last.position) / dt;
            }
            
            // Add sample to buffer
            _buffer.Add(new Sample
            {
                time = snapshotTime,
                position = position,
                velocity = velocity
            });
            
            // Keep buffer from growing too large
            if (_buffer.Count > MAX_SAMPLES)
            {
                _buffer.RemoveAt(0);
            }
        }
        
        /// <summary>
        /// Push a snapshot without explicit velocity (computed from position deltas)
        /// </summary>
        public void PushSnapshot(Vector2 position, float hostTime = -1f)
        {
            PushSnapshot(position, Vector2.zero, hostTime);
        }
        
        /// <summary>
        /// Push a snapshot using Vector3 (z is ignored), with velocity
        /// </summary>
        public void PushSnapshot(Vector3 position, Vector2 velocity, float hostTime = -1f)
        {
            PushSnapshot(new Vector2(position.x, position.y), velocity, hostTime);
        }
        
        /// <summary>
        /// Push a snapshot using Vector3 (z is ignored), no velocity
        /// </summary>
        public void PushSnapshot(Vector3 position, float hostTime = -1f)
        {
            PushSnapshot(new Vector2(position.x, position.y), Vector2.zero, hostTime);
        }
        
        private void Update()
        {
            if (!_isEnabled || _transform == null || !_hasTimeOffset || _buffer.Count == 0)
                return;
            
            // Calculate the playback time using realtimeSinceStartup (monotonic, pause-safe)
            // Uses adaptive jitter buffer delay instead of fixed 100ms
            float playbackTime = Time.realtimeSinceStartup + _timeOffset - _interpDelay;
            
            // Remove old samples that are too far in the past
            while (_buffer.Count >= 2 && _buffer[1].time < playbackTime - 1.0f)
            {
                _buffer.RemoveAt(0);
            }
            
            // Find the target position through interpolation or extrapolation
            Vector2 targetPos;
            
            // Find the first sample that's at or after playback time
            int indexAfter = -1;
            for (int i = 0; i < _buffer.Count; i++)
            {
                if (_buffer[i].time >= playbackTime)
                {
                    indexAfter = i;
                    break;
                }
            }
            
            if (indexAfter <= 0)
            {
                // No future sample found - extrapolate from the last sample
                // Clamp velocity to max walk speed to prevent overshoot from bad data
                var lastSample = _buffer[_buffer.Count - 1];
                float dt = Mathf.Clamp(playbackTime - lastSample.time, 0f, MAX_EXTRAP_SEC);
                Vector2 clampedVelocity = Vector2.ClampMagnitude(lastSample.velocity, MAX_WALK_SPEED);
                targetPos = lastSample.position + clampedVelocity * dt;
            }
            else
            {
                // Interpolate between two samples using cubic Hermite spline
                // This uses both position AND velocity at each keyframe for C1-continuous curves
                var s0 = _buffer[indexAfter - 1];
                var s1 = _buffer[indexAfter];
                float span = Mathf.Max(0.0001f, s1.time - s0.time);
                float t = Mathf.Clamp01((playbackTime - s0.time) / span);
                targetPos = HermiteInterpolate(s0.position, s0.velocity * span, s1.position, s1.velocity * span, t);
            }
            
            // Get current position
            Vector3 currentPos = useLocalPosition ? _transform.localPosition : _transform.position;
            Vector3 targetPos3D = new Vector3(targetPos.x, targetPos.y, currentPos.z);
            
            // If too far away, snap immediately
            if ((currentPos - targetPos3D).sqrMagnitude > SNAP_DIST * SNAP_DIST)
            {
                if (useLocalPosition)
                    _transform.localPosition = targetPos3D;
                else
                    _transform.position = targetPos3D;
                
                _smoothVelocity = Vector2.zero;
                return;
            }
            
            // Distance-adaptive SmoothDamp: use shorter smooth time when far behind
            // to recover quickly from jitter spikes, normal smooth time for close tracking
            float distToTarget = Vector2.Distance(new Vector2(currentPos.x, currentPos.y), targetPos);
            float adaptiveSmoothTime = distToTarget > 1f 
                ? Mathf.Lerp(SMOOTH_TIME, 0.01f, Mathf.Clamp01((distToTarget - 1f) / 2f))
                : SMOOTH_TIME;
            
            // Smooth movement using SmoothDamp with unscaledDeltaTime
            // (unscaled so ghost keeps moving during timeScale changes / pauses)
            Vector2 smoothedPos = Vector2.SmoothDamp(
                new Vector2(currentPos.x, currentPos.y),
                targetPos,
                ref _smoothVelocity,
                adaptiveSmoothTime,
                Mathf.Infinity,
                Time.unscaledDeltaTime
            );
            
            Vector3 finalPos = new Vector3(smoothedPos.x, smoothedPos.y, currentPos.z);
            
            if (useLocalPosition)
                _transform.localPosition = finalPos;
            else
                _transform.position = finalPos;
        }
        
        /// <summary>
        /// Cubic Hermite spline interpolation between two points with tangents.
        /// Produces C1-continuous curves (smooth position AND velocity).
        /// p0/p1 = positions, m0/m1 = tangents (velocity × span), t = [0,1]
        /// </summary>
        private static Vector2 HermiteInterpolate(Vector2 p0, Vector2 m0, Vector2 p1, Vector2 m1, float t)
        {
            float t2 = t * t;
            float t3 = t2 * t;
            
            // Hermite basis functions
            float h00 = 2f * t3 - 3f * t2 + 1f;    // value at p0
            float h10 = t3 - 2f * t2 + t;           // tangent at p0
            float h01 = -2f * t3 + 3f * t2;         // value at p1
            float h11 = t3 - t2;                     // tangent at p1
            
            return h00 * p0 + h10 * m0 + h01 * p1 + h11 * m1;
        }
        
        /// <summary>
        /// Force an immediate position update (bypasses interpolation)
        /// </summary>
        public void ForcePosition(Vector3 position)
        {
            if (_transform != null)
            {
                if (useLocalPosition)
                    _transform.localPosition = position;
                else
                    _transform.position = position;
            }
            
            // Clear buffer and reset all state including adaptive jitter buffer
            _buffer.Clear();
            _hasTimeOffset = false;
            _smoothVelocity = Vector2.zero;
            _interpDelay = DEFAULT_INTERP_DELAY;
            _jitterEstimate = 0f;
            _lastPacketArrivalTime = 0f;
            _lastPacketInterval = 0f;
        }
        
        /// <summary>
        /// Get current target position (for debugging)
        /// </summary>
        public Vector3 GetTargetPosition()
        {
            if (_buffer.Count == 0)
                return _transform != null ? _transform.position : Vector3.zero;
            
            return new Vector3(_buffer[_buffer.Count - 1].position.x, _buffer[_buffer.Count - 1].position.y, 0);
        }
    }
}
