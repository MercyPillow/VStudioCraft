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

        // Amanatides & Woo voxel traversal.
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

            int axis = -1;
            float t = 0f;

            while (t <= maxDist)
            {
                if (BlockData.IsSolid(world.GetBlock(ix, iy, iz)))
                {
                    hit.X = ix; hit.Y = iy; hit.Z = iz;
                    switch (axis)
                    {
                        case 0: hit.Nx = -stepX; hit.Ny = 0;      hit.Nz = 0;      break;
                        case 1: hit.Nx = 0;      hit.Ny = -stepY; hit.Nz = 0;      break;
                        case 2: hit.Nx = 0;      hit.Ny = 0;      hit.Nz = -stepZ; break;
                        default: hit.Nx = 0;     hit.Ny = 1;      hit.Nz = 0;      break;
                    }
                    return true;
                }

                if (tMaxX < tMaxY && tMaxX < tMaxZ)
                {
                    ix += stepX; t = tMaxX; tMaxX += tDeltaX; axis = 0;
                }
                else if (tMaxY < tMaxZ)
                {
                    iy += stepY; t = tMaxY; tMaxY += tDeltaY; axis = 1;
                }
                else
                {
                    iz += stepZ; t = tMaxZ; tMaxZ += tDeltaZ; axis = 2;
                }
            }
            return false;
        }
    }
}
