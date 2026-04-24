using System;
using System.Collections.Generic;
using OpenTK;
using OpenTK.Graphics.OpenGL;

namespace VStudioCraft.Game
{
    internal sealed class Shader : IDisposable
    {
        public int ProgramHandle { get; }

        // Cache uniform locations. GL.GetUniformLocation is a string hash on the hot path;
        // at ~150 draw calls per frame × several uniforms that adds up.
        private readonly Dictionary<string, int> _uniforms = new Dictionary<string, int>();

        public Shader(string vertexSrc, string fragmentSrc)
        {
            int vs = Compile(ShaderType.VertexShader, vertexSrc);
            int fs = Compile(ShaderType.FragmentShader, fragmentSrc);

            ProgramHandle = GL.CreateProgram();
            GL.AttachShader(ProgramHandle, vs);
            GL.AttachShader(ProgramHandle, fs);
            GL.LinkProgram(ProgramHandle);

            GL.GetProgram(ProgramHandle, GetProgramParameterName.LinkStatus, out int ok);
            if (ok == 0)
            {
                var log = GL.GetProgramInfoLog(ProgramHandle);
                throw new InvalidOperationException("Shader link failed: " + log);
            }

            GL.DetachShader(ProgramHandle, vs);
            GL.DetachShader(ProgramHandle, fs);
            GL.DeleteShader(vs);
            GL.DeleteShader(fs);
        }

        private static int Compile(ShaderType type, string src)
        {
            int handle = GL.CreateShader(type);
            GL.ShaderSource(handle, src);
            GL.CompileShader(handle);
            GL.GetShader(handle, ShaderParameter.CompileStatus, out int ok);
            if (ok == 0)
            {
                var log = GL.GetShaderInfoLog(handle);
                GL.DeleteShader(handle);
                throw new InvalidOperationException($"Shader compile failed ({type}): {log}");
            }
            return handle;
        }

        public void Use() => GL.UseProgram(ProgramHandle);

        private int Loc(string name)
        {
            if (_uniforms.TryGetValue(name, out var loc)) return loc;
            loc = GL.GetUniformLocation(ProgramHandle, name);
            _uniforms[name] = loc;
            return loc;
        }

        public void SetMatrix4(string name, Matrix4 m)
        {
            int loc = Loc(name);
            GL.UniformMatrix4(loc, false, ref m);
        }

        public void SetVector3(string name, Vector3 v)
        {
            GL.Uniform3(Loc(name), v.X, v.Y, v.Z);
        }

        public void SetFloat(string name, float f)
        {
            GL.Uniform1(Loc(name), f);
        }

        public void SetInt(string name, int i)
        {
            GL.Uniform1(Loc(name), i);
        }

        public void Dispose()
        {
            GL.DeleteProgram(ProgramHandle);
        }
    }
}
