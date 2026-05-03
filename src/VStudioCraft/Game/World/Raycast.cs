using System;
using OpenTK;

namespace VStudioCraft.Game
{
    internal static class Raycast
    {
        public struct Hit
        {
            public int X, Y, Z;
            public int Nx, Ny, Nz;
        }

        // Amanatides & Woo voxel traversal, with a per-cell ray-vs-
        // AABB refinement so partial-cube blocks (snow layer today,
        // slabs / stairs / torches in the future) are only hit when
        // the ray actually crosses their sub-cell AABB. Without the
        // refinement a snow layer at cellY would be clickable
        // through the entire 1m cell — the wireframe would be 1/8
        // tall but the click area would extend up to cellY+1, which
        // reads as "the click target doesn't match what I'm looking
        // at". Default cubes return (0,0,0,1,1,1) so the test
        // collapses to "ray entered the cell" → hit, matching the
        // original Amanatides & Woo behaviour for them.
        public static bool Cast(World world, Vector3 origin, Vector3 dir, float maxDist, out Hit hit)
        {
            hit = default;
            if (dir.LengthSquared < 1e-10f) return false;
            dir.Normalize();

            int ix = (int)Math.Floor(origin.X);
            int iy = (int)Math.Floor(origin.Y);
            int iz = (int)Math.Floor(origin.Z);

            int stepX = dir.X >= 0 ? 1 : -1;
            int stepY = dir.Y >= 0 ? 1 : -1;
            int stepZ = dir.Z >= 0 ? 1 : -1;

            const float inf = float.PositiveInfinity;
            float tDeltaX = dir.X != 0 ? Math.Abs(1f / dir.X) : inf;
            float tDeltaY = dir.Y != 0 ? Math.Abs(1f / dir.Y) : inf;
            float tDeltaZ = dir.Z != 0 ? Math.Abs(1f / dir.Z) : inf;

            float nextX = stepX > 0 ? ix + 1 : ix;
            float nextY = stepY > 0 ? iy + 1 : iy;
            float nextZ = stepZ > 0 ? iz + 1 : iz;

            float tMaxX = dir.X != 0 ? (nextX - origin.X) / dir.X : inf;
            float tMaxY = dir.Y != 0 ? (nextY - origin.Y) / dir.Y : inf;
            float tMaxZ = dir.Z != 0 ? (nextZ - origin.Z) / dir.Z : inf;

            float t = 0f;

            while (t <= maxDist)
            {
                var block = world.GetBlock(ix, iy, iz);
                if (BlockData.IsRaycastTarget(block))
                {
                    // Tier 8 #45 V2 — Stairs have a primary AABB
                    // (lower step) plus a secondary AABB (upper step
                    // on one side). Test the closer one first; both
                    // are valid hit candidates. We pick whichever
                    // yields a smaller tEnter since the ray hits
                    // that geometry first.
                    // Meta-aware AABB so the ladder's 1-pixel-thick
                    // hitbox follows the facing meta. Other blocks
                    // ignore meta in the overload and fall through to
                    // the legacy lookup.
                    byte rcMeta = world.GetMeta(ix, iy, iz);
                    var (b0x, b0y, b0z, b1x, b1y, b1z) = BlockData.GetCollisionAabb(block, rcMeta);
                    float minX = ix + b0x, maxX = ix + b1x;
                    float minY = iy + b0y, maxY = iy + b1y;
                    float minZ = iz + b0z, maxZ = iz + b1z;
                    bool hit1 = RayAabb(origin, dir, minX, minY, minZ, maxX, maxY, maxZ,
                        out float t1, out int axis1, out int neg1);

                    bool hit2 = false;
                    float t2 = float.PositiveInfinity;
                    int axis2 = 0, neg2 = 0;
                    if (BlockData.IsStair(block))
                    {
                        byte sMeta = world.GetMeta(ix, iy, iz);
                        if (BlockData.TryGetExtraCollisionAabb(block, sMeta, out var ex))
                        {
                            float exMinX = ix + ex.minX, exMaxX = ix + ex.maxX;
                            float exMinY = iy + ex.minY, exMaxY = iy + ex.maxY;
                            float exMinZ = iz + ex.minZ, exMaxZ = iz + ex.maxZ;
                            hit2 = RayAabb(origin, dir,
                                exMinX, exMinY, exMinZ, exMaxX, exMaxY, exMaxZ,
                                out t2, out axis2, out neg2);
                        }
                    }

                    bool anyHit = hit1 || hit2;
                    if (anyHit)
                    {
                        // Pick the closer hit (smaller t) — that's the
                        // face the ray actually strikes first.
                        bool useFirst = hit1 && (!hit2 || t1 <= t2);
                        int hitAxis    = useFirst ? axis1 : axis2;
                        int hitNegSign = useFirst ? neg1  : neg2;

                        hit.X = ix; hit.Y = iy; hit.Z = iz;
                        // Face normal — points OUT of the AABB on the
                        // entry face. hitAxis is the axis whose slab
                        // we entered last; hitNegSign is the sign of
                        // dir on that axis (we entered from the
                        // opposite side, so the outward normal is
                        // -sign(dir)).
                        switch (hitAxis)
                        {
                            case 0: hit.Nx = -hitNegSign; hit.Ny = 0;            hit.Nz = 0;            break;
                            case 1: hit.Nx = 0;           hit.Ny = -hitNegSign;  hit.Nz = 0;            break;
                            case 2: hit.Nx = 0;           hit.Ny = 0;            hit.Nz = -hitNegSign;  break;
                            default: hit.Nx = 0;          hit.Ny = 1;            hit.Nz = 0;            break;
                        }
                        return true;
                    }
                    // Cell is a target but the ray didn't actually
                    // intersect its partial AABB — fall through to
                    // the cell-step below so we keep traversing.
                }

                if (tMaxX < tMaxY && tMaxX < tMaxZ)
                {
                    ix += stepX; t = tMaxX; tMaxX += tDeltaX;
                }
                else if (tMaxY < tMaxZ)
                {
                    iy += stepY; t = tMaxY; tMaxY += tDeltaY;
                }
                else
                {
                    iz += stepZ; t = tMaxZ; tMaxZ += tDeltaZ;
                }
            }
            return false;
        }

        // Slab-method ray-AABB intersection. Returns the entry t and
        // the axis of the face the ray entered (used by the caller
        // to compute the outward face normal). hitNegSign is the
        // direction sign on that axis — the caller negates it to
        // point outward from the AABB.
        private static bool RayAabb(
            Vector3 origin, Vector3 dir,
            float minX, float minY, float minZ,
            float maxX, float maxY, float maxZ,
            out float tEnter, out int hitAxis, out int hitNegSign)
        {
            tEnter = 0f;
            hitAxis = -1;
            hitNegSign = 0;
            float tNear = float.NegativeInfinity;
            float tFar  = float.PositiveInfinity;

            for (int a = 0; a < 3; a++)
            {
                float o = a == 0 ? origin.X : (a == 1 ? origin.Y : origin.Z);
                float d = a == 0 ? dir.X    : (a == 1 ? dir.Y    : dir.Z);
                float lo = a == 0 ? minX : (a == 1 ? minY : minZ);
                float hi = a == 0 ? maxX : (a == 1 ? maxY : maxZ);

                if (Math.Abs(d) < 1e-10f)
                {
                    if (o < lo || o > hi) return false;
                    continue;
                }

                float t1 = (lo - o) / d;
                float t2 = (hi - o) / d;
                int sign = 1;       // dir crosses lo first (d>0)
                if (t1 > t2) { var tmp = t1; t1 = t2; t2 = tmp; sign = -1; }

                if (t1 > tNear)
                {
                    tNear = t1;
                    hitAxis = a;
                    hitNegSign = sign;
                }
                if (t2 < tFar) tFar = t2;
                if (tNear > tFar) return false;
            }

            if (tNear < 0f)
            {
                // Ray origin is INSIDE the AABB. Treat as immediate
                // hit at t=0 with no meaningful entry-face axis —
                // caller falls through to the +Y default normal.
                tEnter = 0f;
                hitAxis = -1;
                hitNegSign = 0;
                return true;
            }
            tEnter = tNear;
            return true;
        }
    }
}
