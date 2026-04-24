using System;
using System.Collections.Concurrent;
using System.Threading;

namespace VStudioCraft.Game
{
    // Background producer for chunk generation and meshing. The render thread
    // enqueues "please gen" / "please mesh" requests and then — on subsequent
    // frames — drains completed results and uploads them to the GPU. The
    // expensive CPU work (Perlin noise, greedy mesh packing, vertex buffer
    // construction) happens on worker threads, so streaming new chunks no
    // longer stalls the render thread.
    //
    // Each worker owns its own ChunkMesher so they don't contend on its
    // scratch buffers. Meshers read from the shared World via its
    // ConcurrentDictionary of chunks; block edits race harmlessly (bytes are
    // atomic, and edited chunks are marked dirty so they'll be remeshed).
    internal sealed class ChunkJobSystem : IDisposable
    {
        private enum JobKind : byte { Gen, Mesh }

        private struct Job
        {
            public JobKind Kind;
            public int X, Z;
        }

        internal struct GenResult
        {
            public Chunk Chunk;
        }

        internal struct MeshResult
        {
            public int X, Z;
            public float[] Verts;
            public int VertFloatCount;
            public uint[] Indices;
            public int IndexCount;
        }

        private readonly World _world;
        private readonly BlockingCollection<Job> _jobs;
        private readonly ConcurrentQueue<GenResult> _genResults = new ConcurrentQueue<GenResult>();
        private readonly ConcurrentQueue<MeshResult> _meshResults = new ConcurrentQueue<MeshResult>();

        // "In-flight" = queued or currently being processed. Prevents duplicate
        // jobs for the same chunk piling up when the render thread enqueues
        // every frame. Entries are removed when a result is emitted.
        private readonly ConcurrentDictionary<(int x, int z), byte> _genInFlight = new ConcurrentDictionary<(int x, int z), byte>();
        private readonly ConcurrentDictionary<(int x, int z), byte> _meshInFlight = new ConcurrentDictionary<(int x, int z), byte>();

        private readonly Thread[] _workers;
        private volatile bool _disposed;

        public ChunkJobSystem(World world, int workerCount)
        {
            _world = world;
            _jobs = new BlockingCollection<Job>(new ConcurrentQueue<Job>());

            int count = Math.Max(1, workerCount);
            _workers = new Thread[count];
            for (int i = 0; i < count; i++)
            {
                var mesher = new ChunkMesher();
                int idx = i;
                _workers[i] = new Thread(() => WorkerLoop(mesher))
                {
                    IsBackground = true,
                    Name = "VStudioCraft chunk worker " + idx,
                };
                _workers[i].Start();
            }
        }

        public bool TryEnqueueGen(int cx, int cz)
        {
            if (_disposed) return false;
            if (!_genInFlight.TryAdd((cx, cz), 0)) return false;
            try { _jobs.Add(new Job { Kind = JobKind.Gen, X = cx, Z = cz }); return true; }
            catch (InvalidOperationException) { _genInFlight.TryRemove((cx, cz), out _); return false; }
        }

        public bool TryEnqueueMesh(int cx, int cz)
        {
            if (_disposed) return false;
            if (!_meshInFlight.TryAdd((cx, cz), 0)) return false;
            try { _jobs.Add(new Job { Kind = JobKind.Mesh, X = cx, Z = cz }); return true; }
            catch (InvalidOperationException) { _meshInFlight.TryRemove((cx, cz), out _); return false; }
        }

        public bool TryDequeueGen(out GenResult r) => _genResults.TryDequeue(out r);
        public bool TryDequeueMesh(out MeshResult r) => _meshResults.TryDequeue(out r);

        private void WorkerLoop(ChunkMesher mesher)
        {
            try
            {
                foreach (var job in _jobs.GetConsumingEnumerable())
                {
                    try
                    {
                        if (job.Kind == JobKind.Gen)
                        {
                            var c = new Chunk(job.X, job.Z);
                            TerrainGenerator.Generate(c, _world.Noise);
                            _genResults.Enqueue(new GenResult { Chunk = c });
                        }
                        else
                        {
                            var chunk = _world.GetChunk(job.X, job.Z);
                            if (chunk != null)
                            {
                                mesher.Build(_world, chunk);
                                // Mesher reuses its scratch buffers — snapshot into
                                // right-sized arrays the render thread can hand off
                                // to GL.BufferData without worrying about aliasing.
                                var verts = new float[mesher.VertexFloatCount];
                                Array.Copy(mesher.Vertices, verts, mesher.VertexFloatCount);
                                var idxs = new uint[mesher.IndexCount];
                                Array.Copy(mesher.Indices, idxs, mesher.IndexCount);
                                _meshResults.Enqueue(new MeshResult
                                {
                                    X = job.X, Z = job.Z,
                                    Verts = verts, VertFloatCount = mesher.VertexFloatCount,
                                    Indices = idxs, IndexCount = mesher.IndexCount,
                                });
                            }
                        }
                    }
                    catch { /* swallow — chunk stays dirty, will be retried */ }
                    finally
                    {
                        if (job.Kind == JobKind.Gen) _genInFlight.TryRemove((job.X, job.Z), out _);
                        else _meshInFlight.TryRemove((job.X, job.Z), out _);
                    }
                }
            }
            catch (InvalidOperationException) { /* BlockingCollection completed */ }
            catch (OperationCanceledException) { }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try { _jobs.CompleteAdding(); } catch { }
            foreach (var w in _workers)
            {
                try { w.Join(1000); } catch { }
            }
            try { _jobs.Dispose(); } catch { }
        }
    }
}
