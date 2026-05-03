using OpenTK;

namespace VStudioCraft.Game
{
    // Phase 5f — snapshot-pair interpolator for replicated entities OTHER
    // than the local player.
    //
    // RemotePlayer.cs has bespoke snapshot interp baked in because it
    // also drives the Steve rig's walk-cycle phase from the displacement.
    // Mobs (Phase 5a/5b replicas) and drops/projectiles (Phase 5c/5d) need
    // smooth motion too but don't have rig-walk-cycle considerations: the
    // existing PassiveMob / HostileMob render paths read mob.Position
    // directly. So this class is the minimum extraction: hold prev/curr
    // snapshots, lerp on Tick, write back to a shared external field
    // (the entity's Position).
    //
    // Usage in GameRenderer:
    //   - On EntitySpawn: state = new EntityInterpState(pos, yaw, now);
    //                     entity.Position = state.RenderedPos;
    //   - On EntityRelMove / RelMoveLook / Teleport: state.Apply(...);
    //   - Per frame in DrainNetwork: state.Tick(now);
    //                                entity.Position = state.RenderedPos;
    //                                entity.Yaw = state.RenderedYawRadians;
    //
    // The render-behind delay (100 ms) matches RemotePlayer so a player
    // and a pig walking next to each other animate against the same
    // wall-clock window — a longer delay on one would visibly desync.
    internal sealed class EntityInterpState
    {
        public const double InterpDelaySeconds = 0.10; // 100 ms

        private Vector3 _prevPos, _currPos;
        private float   _prevYaw, _currYaw; // radians (mob convention)
        private double  _prevTime, _currTime;

        public Vector3 RenderedPos { get; private set; }
        public float   RenderedYawRadians { get; private set; }

        public EntityInterpState(Vector3 pos, float yawRadians, double now)
        {
            _prevPos = _currPos = pos;
            _prevYaw = _currYaw = yawRadians;
            _prevTime = _currTime = now;
            RenderedPos = pos;
            RenderedYawRadians = yawRadians;
        }

        public void ApplyTeleport(Vector3 pos, float yawRadians, double now)
        {
            _prevPos = _currPos;
            _prevYaw = _currYaw;
            _prevTime = _currTime;
            _currPos = pos;
            _currYaw = yawRadians;
            _currTime = now;
        }

        public void ApplyRelMove(Vector3 delta, double now)
        {
            ApplyTeleport(_currPos + delta, _currYaw, now);
        }

        public void ApplyRelMoveLook(Vector3 delta, float yawRadians, double now)
        {
            ApplyTeleport(_currPos + delta, yawRadians, now);
        }

        public void ApplyLook(float yawRadians, double now)
        {
            ApplyTeleport(_currPos, yawRadians, now);
        }

        // Compute the rendered position+yaw at wall-clock `now`. Sample
        // (now - InterpDelay) inside [_prevTime, _currTime]; clamp on
        // either end (don't extrapolate forward — extrapolated mobs
        // overshoot walls and look broken; they'll just hold their last
        // server-known pose until the next packet).
        public void Tick(double now)
        {
            double sampleTime = now - InterpDelaySeconds;
            float frac;
            double window = _currTime - _prevTime;
            if (window <= 1e-6)
            {
                frac = 1f;
            }
            else
            {
                frac = (float)((sampleTime - _prevTime) / window);
                if (frac < 0f) frac = 0f;
                if (frac > 1f) frac = 1f;
            }

            RenderedPos = Vector3.Lerp(_prevPos, _currPos, frac);
            RenderedYawRadians = LerpAngleRadians(_prevYaw, _currYaw, frac);
        }

        private static float LerpAngleRadians(float a, float b, float t)
        {
            const float twoPi = (float)(2.0 * System.Math.PI);
            float diff = b - a;
            while (diff >  System.Math.PI) diff -= twoPi;
            while (diff < -System.Math.PI) diff += twoPi;
            return a + diff * t;
        }
    }
}
