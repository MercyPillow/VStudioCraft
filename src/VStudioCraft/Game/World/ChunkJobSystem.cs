using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
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
            // Spawn intents computed by the worker. Render thread drains
            // these into World.Passives / World.Hostiles after a successful
            // freshly-installed chunk (skipping if the install was a
            // cached-modified swap — the historic guard
            // `freshlyInstalled && !r.Chunk.IsModified` in UpdateStreaming
            // still applies, so the intents are simply discarded in that
            // case). May be null if the chunk's spawn pass produced
            // nothing — saves a List allocation in the common "ocean
            // chunk with no eligible columns" case.
            public List<PassiveMob> PassiveSpawns;
            public List<HostileMob> HostileSpawns;
        }

        internal struct MeshResult
        {
            public int X, Z;
            public float[] Verts;
            public int VertFloatCount;
            public uint[] Indices;
            public int IndexCount;
            // Transparent stream (water today, glass/fancy-leaves tomorrow).
            public float[] TVerts;
            public int TVertFloatCount;
            public uint[] TIndices;
            public int TIndexCount;
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

        // Tier 6 — Read-only count for the loading-screen readiness
        // poll. ConcurrentDictionary.Count is approximate but cheap;
        // approximate is fine here because we're checking a "drain
        // settled" condition that reads "0 == 0" once the queue is
        // empty (any momentary count drift between Count and reality
        // resolves on the next frame's poll).
        public int MeshInFlight => _meshInFlight.Count;

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
                            // Stage 1: terrain (noise + caves + ravines + ores
                            // + flora + trees + fluid features). Worker-thread-
                            // safe because the chunk is brand-new and not
                            // installed in `_chunks` yet — no other thread can
                            // see it.
                            //
                            // Dispatches on the world's dimension — the Nether
                            // generator runs the 3D mountain mass + lava sea
                            // pass; the overworld generator runs the
                            // canonical noise + caves + ravines + ores +
                            // flora + trees pass. The Nether path skips the
                            // dungeon + passive-spawn passes (no overworld-
                            // style dungeons or passive mobs in the Nether)
                            // and only computes hostile spawns (pigman /
                            // ghast / blaze).
                            var c = new Chunk(job.X, job.Z);
                            if (_world.Dimension == Dimension.Nether)
                            {
                                NetherTerrainGenerator.Generate(c, _world.Seed, _world.AlphaSampler);
                                LightCalculator.RecomputeChunk(c);
                                var nHostiles = new List<HostileMob>();
                                World.ComputeNetherSpawnsForChunk(c, _world.Seed, nHostiles);
                                _genResults.Enqueue(new GenResult
                                {
                                    Chunk = c,
                                    PassiveSpawns = null,
                                    HostileSpawns = nHostiles.Count > 0 ? nHostiles : null,
                                });
                                continue;
                            }

                            TerrainGenerator.Generate(c, _world.Noise, _world.AlphaSampler);
                            // Stage 2: initial light pass — skylight column
                            // descent + emitter BFS. Same chunk-private read.
                            LightCalculator.RecomputeChunk(c);

                            // Stage 3 (P1 of chunk-streaming smoothness work):
                            // dungeon gen + relight + mob-spawn USED to live
                            // on the render thread inside UpdateStreaming,
                            // running once per just-installed chunk. With
                            // MaxInstallsPerFrame = 8 and ~3 ms per chunk
                            // that was a ~24 ms render-thread spike whenever
                            // the player crossed into a fresh ring. We move
                            // it here so the entire post-install cost
                            // becomes one List<>.AddRange on the render
                            // thread.
                            //
                            // Dungeon gen registers chest tile entities at
                            // world coords; `_chestEntities` is a
                            // ConcurrentDictionary so two workers carving
                            // dungeons in different chunks can register
                            // chests in parallel without a lock. Light
                            // recompute runs a second time because the
                            // dungeon carved a hollow interior that wasn't
                            // present during Stage 2.
                            //
                            // Mob spawns are computed into per-chunk
                            // throwaway lists and shipped on GenResult; the
                            // render-thread drain decides whether to keep
                            // them (matches the historic
                            // `freshlyInstalled && !r.Chunk.IsModified`
                            // guard — see GameRenderer.UpdateStreaming).
                            _world.GenerateDungeonsInChunk(c);
                            LightCalculator.RecomputeChunk(c);

                            var passives = new List<PassiveMob>();
                            var hostiles = new List<HostileMob>();
                            _world.ComputePassiveSpawnsForChunk(c, passives);
                            _world.ComputeHostileSpawnsForChunk(c, hostiles);

                            _genResults.Enqueue(new GenResult
                            {
                                Chunk = c,
                                PassiveSpawns = passives.Count > 0 ? passives : null,
                                HostileSpawns = hostiles.Count > 0 ? hostiles : null,
                            });
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
                                var tVerts = new float[mesher.TransparentVertexFloatCount];
                                Array.Copy(mesher.TransparentVertices, tVerts, mesher.TransparentVertexFloatCount);
                                var tIdxs = new uint[mesher.TransparentIndexCount];
                                Array.Copy(mesher.TransparentIndices, tIdxs, mesher.TransparentIndexCount);
                                _meshResults.Enqueue(new MeshResult
                                {
                                    X = job.X, Z = job.Z,
                                    Verts = verts, VertFloatCount = mesher.VertexFloatCount,
                                    Indices = idxs, IndexCount = mesher.IndexCount,
                                    TVerts = tVerts, TVertFloatCount = mesher.TransparentVertexFloatCount,
                                    TIndices = tIdxs, TIndexCount = mesher.TransparentIndexCount,
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
