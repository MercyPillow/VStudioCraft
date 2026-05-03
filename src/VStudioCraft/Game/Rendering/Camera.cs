using System;
using OpenTK;

namespace VStudioCraft.Game
{
    internal sealed class Camera
    {
        public Vector3 Position = new Vector3(8f, 4f, 24f);
        public float Yaw;                                // radians, 0 = facing -Z
        public float Pitch = -0.2f;                      // radians, negative = looking down
        public float Fov = MathHelper.DegreesToRadians(70f);
        public float Near = 0.1f;
        public float Far = 1000f;

        public Vector3 Forward
        {
            get
            {
                float cp = (float)Math.Cos(Pitch);
                return new Vector3(
                    (float)Math.Sin(Yaw) * cp,
                    (float)Math.Sin(Pitch),
                    -(float)Math.Cos(Yaw) * cp);
            }
        }

        public Vector3 Right => Vector3.Normalize(Vector3.Cross(Forward, Vector3.UnitY));

        public Matrix4 GetView() => Matrix4.LookAt(Position, Position + Forward, Vector3.UnitY);

        public Matrix4 GetProjection(int width, int height)
        {
            if (height <= 0) height = 1;
            return Matrix4.CreatePerspectiveFieldOfView(Fov, width / (float)height, Near, Far);
        }

        public void ClampPitch()
        {
            const float lim = 1.55334f;
            if (Pitch > lim) Pitch = lim;
            if (Pitch < -lim) Pitch = -lim;
        }
    }
}
