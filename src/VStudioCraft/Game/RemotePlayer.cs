using OpenTK;

namespace VStudioCraft.Game
{
    // One other-player replica on the client. Phase 4 of the multiplayer
    // feature.
    //
    // The wire protocol delivers a chain of EntitySpawn / EntityRelMove /
    // EntityLook / EntityRelMoveLook / EntityTeleport / EntityDespawn
    // packets. We don't apply each one to a single "current position"
    // field — that would jitter every render frame because the renderer
    // can run at 1500 fps but the server only ships at 20 Hz. Instead we
    // keep TWO snapshots:
    //
    //   `_prev`  — the position we were at when the last delta landed
    //   `_curr`  — the position the most recent delta moved us to
    //
    // and on render we lerp between them using the wall-clock time inside
    // the [_prevTime, _currTime] window, with a fixed render-behind delay
    // so a momentary packet gap doesn't stutter remote players.
    //
    // The yaw/pitch is interpolated the same way (with shortest-arc wrap
    // for yaw) so other players turn smoothly rather than snapping every
    // 50 ms.
    //
    // No physics is run client-side for remote players — the server is
    // authoritative, and the snapshot stream is the only source of truth.
    // Walk-cycle phase is integrated from horizontal speed of the
    // interpolated motion so legs/arms swing at a rate that matches the
    // displayed gait.
    internal sealed class RemotePlayer
    {
        public int EntityId { get; }
        public string DisplayName { get; set; }

        // Two snapshots that the renderer interpolates between. Time is
        // in wall-clock seconds (Stopwatch-elapsed at the host); both
        // sides agree because both are sampled here, not on the wire.
        private Vector3 _prevPos;
        private Vector3 _currPos;
        private float   _prevYaw, _currYaw;
        private float   _prevPitch, _currPitch;
        private double  _prevTime, _currTime;

        // Render-behind delay — see InterpDelaySeconds. Sized at half the
        // server tick interval so even on a worst-case "packet arrived
        // late" we still have non-zero room to interpolate between the
        // two snapshots without snapping past the live edge.
        public const double InterpDelaySeconds = 0.10; // 100 ms

        public Vector3 RenderedPos { get; private set; }
        public float RenderedYaw { get; private set; }
        public float RenderedPitch { get; private set; }

        // Walk-cycle phase, integrated from horizontal speed of the
        // rendered motion. Read by the Steve-rig drawer in GameRenderer
        // to swing arms/legs.
        public float WalkCyclePhase { get; private set; }

        public RemotePlayer(int entityId, string displayName, Vector3 spawnPos, float yaw, float pitch, double now)
        {
            EntityId = entityId;
            DisplayName = displayName ?? "?";
            // Seed both snapshots with the spawn pose so the very first
            // render frame has something sane to lerp to (lerp(a, a, t)
            // is just a, regardless of t — no flicker).
            _prevPos = _currPos = spawnPos;
            _prevYaw = _currYaw = yaw;
            _prevPitch = _currPitch = pitch;
            _prevTime = _currTime = now;
            RenderedPos = spawnPos;
            RenderedYaw = yaw;
            RenderedPitch = pitch;
        }

        // Apply an absolute position update (EntitySpawn first frame,
        // EntityTeleport, drift correction). Promotes current snapshot
        // to previous and overwrites current. The prev = curr-dup
        // pattern means an immediately-following lerp targets the new
        // value smoothly across one tick rather than snapping.
        public void ApplyTeleport(Vector3 pos, float yaw, float pitch, double now)
        {
            _prevPos = _currPos;
            _prevYaw = _currYaw;
            _prevPitch = _currPitch;
            _prevTime = _currTime;
            _currPos = pos;
            _currYaw = yaw;
            _currPitch = pitch;
            _currTime = now;
        }

        public void ApplyRelMove(Vector3 delta, double now)
        {
            ApplyTeleport(_currPos + delta, _currYaw, _currPitch, now);
        }

        public void ApplyRelMoveLook(Vector3 delta, float yaw, float pitch, double now)
        {
            ApplyTeleport(_currPos + delta, yaw, pitch, now);
        }

        public void ApplyLook(float yaw, float pitch, double now)
        {
            ApplyTeleport(_currPos, yaw, pitch, now);
        }

        // Compute the rendered position+look at wall-clock `now`. We sample
        // (now - InterpDelay) inside [_prevTime, _currTime]. If the gap
        // since _currTime exceeded the buffer we extrapolate flat (hold
        // _curr) rather than running ahead of the server — the only thing
        // worse than a slightly-stale remote player is a wildly-wrong one.
        public void Tick(double now, float dt)
        {
            double sampleTime = now - InterpDelaySeconds;
            float frac;
            double window = _currTime - _prevTime;
            if (window <= 1e-6)
            {
                frac = 1f; // both snapshots are the same instant; just hold curr
            }
            else
            {
                frac = (float)((sampleTime - _prevTime) / window);
                if (frac < 0f) frac = 0f;
                if (frac > 1f) frac = 1f;
            }

            var prev = RenderedPos;
            RenderedPos   = Vector3.Lerp(_prevPos, _currPos, frac);
            // Yaw needs shortest-arc interpolation: 359° → 1° should
            // visually rotate +2°, not sweep -358° around the long way.
            RenderedYaw   = LerpAngle(_prevYaw, _currYaw, frac);
            RenderedPitch = _prevPitch + (_currPitch - _prevPitch) * frac;

            // Walk-cycle from horizontal speed of the rendered motion.
            // Same constant the local third-person rig uses so a remote
            // player's stride matches the local player's at the same
            // speed. ~4.3 m/s walk → ~2 strides/sec.
            float ddx = RenderedPos.X - prev.X;
            float ddz = RenderedPos.Z - prev.Z;
            float horiz = (float)System.Math.Sqrt(ddx * ddx + ddz * ddz);
            // Speed-divide-dt would be the per-second velocity; we want
            // a phase increment per frame, so multiply by dt is wrong —
            // use horiz directly (already a per-frame distance).
            float strideRate = 1.6f; // empirical match to local Steve
            WalkCyclePhase += horiz * strideRate;
            // Decay the phase when stationary so the next stride
            // resumes from a neutral pose rather than mid-step.
            if (horiz < 1e-4f) WalkCyclePhase *= 0.85f;
        }

        private static float LerpAngle(float a, float b, float t)
        {
            float diff = b - a;
            while (diff >  180f) diff -= 360f;
            while (diff < -180f) diff += 360f;
            return a + diff * t;
        }
    }
}
