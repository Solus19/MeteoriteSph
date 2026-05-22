using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace MeteoriteSPH3D
{
    [RequireComponent(typeof(MeshFilter), typeof(MeshRenderer), typeof(MeshCollider))]
    public sealed class VoxelMeshRenderer3D : MonoBehaviour
    {
        [Header("Chunked voxel mesh")]
        public int chunkSize = 12;
        public int maxChunkRebuildsPerCall = 1;
        public int colliderUpdateIntervalFrames = 60;
        [Tooltip("Chunk MeshColliders are disabled by default. Mouse picking uses a custom voxel raycast, so collider cooking no longer stalls deposition.")]
        public bool enableChunkColliders = false;
        [Tooltip("Build only the visible heightfield surface instead of scanning every solid voxel. Much faster for this terrain-style crater prototype.")]
        public bool useHeightfieldMeshing = true;

        public bool HasPendingRebuilds { get { return dirtyChunkQueue.Count > 0; } }
        public int LastRebuiltChunks { get; private set; }
        public int PendingChunkRebuilds { get { return dirtyChunkQueue.Count; } }

        private sealed class Chunk
        {
            public GameObject go;
            public Mesh mesh;
            public MeshCollider collider;
            public MeshFilter filter;
            public MeshRenderer renderer;
            public int cx;
            public int cy;
            public int cz;
        }

        private MeshFilter rootMeshFilter;
        private MeshRenderer rootMeshRenderer;
        private MeshCollider rootMeshCollider;
        private Material material;
        private Chunk[] chunks;
        private bool[] queuedChunks;
        private readonly Queue<int> dirtyChunkQueue = new Queue<int>(512);
        private readonly List<int> dirtyVoxelScratch = new List<int>(4096);

        private int chunkGridX;
        private int chunkGridY;
        private int chunkGridZ;
        private int cachedWidth;
        private int cachedHeight;
        private int cachedDepth;
        private int cachedChunkSize;
        private bool builtAtLeastOnce;
        private int fullRebuildSerial;

        private readonly List<Vector3> vertices = new List<Vector3>(4096);
        private readonly List<Vector3> normals = new List<Vector3>(4096);
        private readonly List<int> triangles = new List<int>(6144);
        private readonly List<Color> colors = new List<Color>(4096);
        private readonly List<Vector2> uvs = new List<Vector2>(4096);

        private static readonly Vector3Int[] dirs =
        {
            new Vector3Int(1,0,0), new Vector3Int(-1,0,0),
            new Vector3Int(0,1,0), new Vector3Int(0,-1,0),
            new Vector3Int(0,0,1), new Vector3Int(0,0,-1)
        };

        private static readonly Vector3[,] face =
        {
            { new Vector3(1,0,0), new Vector3(1,1,0), new Vector3(1,1,1), new Vector3(1,0,1) },
            { new Vector3(0,0,1), new Vector3(0,1,1), new Vector3(0,1,0), new Vector3(0,0,0) },
            { new Vector3(0,1,1), new Vector3(1,1,1), new Vector3(1,1,0), new Vector3(0,1,0) },
            { new Vector3(0,0,0), new Vector3(1,0,0), new Vector3(1,0,1), new Vector3(0,0,1) },
            { new Vector3(1,0,1), new Vector3(1,1,1), new Vector3(0,1,1), new Vector3(0,0,1) },
            { new Vector3(0,0,0), new Vector3(0,1,0), new Vector3(1,1,0), new Vector3(1,0,0) }
        };

        private static readonly Vector2[] faceUvs =
        {
            new Vector2(0f, 0f),
            new Vector2(0f, 1f),
            new Vector2(1f, 1f),
            new Vector2(1f, 0f)
        };

        public void Initialize()
        {
            rootMeshFilter = GetComponent<MeshFilter>();
            rootMeshRenderer = GetComponent<MeshRenderer>();
            rootMeshCollider = GetComponent<MeshCollider>();

            Shader shader = Shader.Find("MeteoriteSPH3D/VertexColorUnlit");
            if (shader == null) shader = Shader.Find("Sprites/Default");
            material = new Material(shader);

            Mesh empty = new Mesh();
            empty.name = "Meteorite SPH 3D Voxel Root Empty";
            rootMeshFilter.sharedMesh = empty;
            rootMeshRenderer.sharedMaterial = material;
            rootMeshRenderer.enabled = false;
            rootMeshCollider.sharedMesh = null;
            rootMeshCollider.enabled = false;
        }

        public void Configure(int newChunkSize, int newMaxChunkRebuildsPerCall, int newColliderUpdateIntervalFrames)
        {
            chunkSize = Mathf.Clamp(newChunkSize, 4, 64);
            maxChunkRebuildsPerCall = Mathf.Max(1, newMaxChunkRebuildsPerCall);
            colliderUpdateIntervalFrames = Mathf.Max(1, newColliderUpdateIntervalFrames);
        }

        public void RebuildImmediate(VoxelTerrain3D terrain)
        {
            if (terrain == null) return;
            if (material == null) Initialize();
            EnsureChunks(terrain);
            ClearQueue();
            EnqueueAllChunks();
            BuildQueuedChunks(terrain, int.MaxValue, true);
            int minX;
            int minY;
            int minZ;
            int maxX;
            int maxY;
            int maxZ;
            terrain.ConsumeDirtyBounds(out minX, out minY, out minZ, out maxX, out maxY, out maxZ);
            builtAtLeastOnce = true;
        }

        public void Rebuild(VoxelTerrain3D terrain)
        {
            LastRebuiltChunks = 0;
            if (terrain == null) return;
            if (material == null) Initialize();
            EnsureChunks(terrain);

            bool hadDirty = false;
            if (terrain.ConsumeDirtyVoxelIndices(dirtyVoxelScratch))
            {
                hadDirty = true;
                for (int i = 0; i < dirtyVoxelScratch.Count; i++)
                {
                    int x;
                    int y;
                    int z;
                    terrain.UnpackIndex(dirtyVoxelScratch[i], out x, out y, out z);
                    EnqueueChunksForBounds(x - 1, y - 1, z - 1, x + 1, y + 1, z + 1);
                }
                dirtyVoxelScratch.Clear();
            }
            else
            {
                int minX;
                int minY;
                int minZ;
                int maxX;
                int maxY;
                int maxZ;
                if (terrain.ConsumeDirtyBounds(out minX, out minY, out minZ, out maxX, out maxY, out maxZ))
                {
                    hadDirty = true;
                    EnqueueChunksForBounds(minX - 1, minY - 1, minZ - 1, maxX + 1, maxY + 1, maxZ + 1);
                }
            }

            if (!builtAtLeastOnce || (dirtyChunkQueue.Count == 0 && !hadDirty))
            {
                EnqueueAllChunks();
            }

            bool forceCollider = !builtAtLeastOnce;
            BuildQueuedChunks(terrain, Mathf.Max(1, maxChunkRebuildsPerCall), forceCollider);
            if (dirtyChunkQueue.Count == 0) builtAtLeastOnce = true;
        }

        private void EnsureChunks(VoxelTerrain3D terrain)
        {
            int size = Mathf.Clamp(chunkSize, 4, 64);
            bool needsRecreate = chunks == null
                                 || cachedWidth != terrain.Width
                                 || cachedHeight != terrain.Height
                                 || cachedDepth != terrain.Depth
                                 || cachedChunkSize != size;
            if (!needsRecreate) return;

            DestroyChunks();

            cachedWidth = terrain.Width;
            cachedHeight = terrain.Height;
            cachedDepth = terrain.Depth;
            cachedChunkSize = size;
            chunkGridX = Mathf.CeilToInt(terrain.Width / (float)size);
            chunkGridY = Mathf.CeilToInt(terrain.Height / (float)size);
            chunkGridZ = Mathf.CeilToInt(terrain.Depth / (float)size);

            int count = chunkGridX * chunkGridY * chunkGridZ;
            chunks = new Chunk[count];
            queuedChunks = new bool[count];
            dirtyChunkQueue.Clear();
            builtAtLeastOnce = false;
            fullRebuildSerial = 0;

            for (int cy = 0; cy < chunkGridY; cy++)
            {
                for (int cz = 0; cz < chunkGridZ; cz++)
                {
                    for (int cx = 0; cx < chunkGridX; cx++)
                    {
                        int id = ChunkIndex(cx, cy, cz);
                        GameObject go = new GameObject("VoxelChunk_" + cx + "_" + cy + "_" + cz);
                        go.transform.SetParent(transform, false);

                        MeshFilter mf = go.AddComponent<MeshFilter>();
                        MeshRenderer mr = go.AddComponent<MeshRenderer>();
                        MeshCollider mc = go.AddComponent<MeshCollider>();
                        mc.enabled = enableChunkColliders;
                        Mesh m = new Mesh();
                        m.indexFormat = IndexFormat.UInt32;
                        m.name = go.name + " Mesh";
                        m.MarkDynamic();

                        mf.sharedMesh = m;
                        mr.sharedMaterial = material;
                        mc.sharedMesh = null;

                        chunks[id] = new Chunk
                        {
                            go = go,
                            mesh = m,
                            collider = mc,
                            filter = mf,
                            renderer = mr,
                            cx = cx,
                            cy = cy,
                            cz = cz
                        };
                    }
                }
            }
        }

        private void DestroyChunks()
        {
            if (chunks == null) return;
            for (int i = 0; i < chunks.Length; i++)
            {
                if (chunks[i] == null || chunks[i].go == null) continue;
                if (Application.isPlaying) Destroy(chunks[i].go);
                else DestroyImmediate(chunks[i].go);
            }
            chunks = null;
            queuedChunks = null;
            dirtyChunkQueue.Clear();
        }

        private int ChunkIndex(int cx, int cy, int cz)
        {
            return cx + chunkGridX * (cz + chunkGridZ * cy);
        }

        private void EnqueueAllChunks()
        {
            fullRebuildSerial++;
            for (int i = 0; i < chunks.Length; i++) EnqueueChunk(i);
        }

        private void EnqueueChunksForBounds(int minX, int minY, int minZ, int maxX, int maxY, int maxZ)
        {
            if (chunks == null || chunks.Length == 0) return;

            minX = Mathf.Clamp(minX, 0, cachedWidth - 1);
            minY = Mathf.Clamp(minY, 0, cachedHeight - 1);
            minZ = Mathf.Clamp(minZ, 0, cachedDepth - 1);
            maxX = Mathf.Clamp(maxX, 0, cachedWidth - 1);
            maxY = Mathf.Clamp(maxY, 0, cachedHeight - 1);
            maxZ = Mathf.Clamp(maxZ, 0, cachedDepth - 1);

            int cMinX = minX / cachedChunkSize;
            int cMinY = minY / cachedChunkSize;
            int cMinZ = minZ / cachedChunkSize;
            int cMaxX = maxX / cachedChunkSize;
            int cMaxY = maxY / cachedChunkSize;
            int cMaxZ = maxZ / cachedChunkSize;

            for (int cy = cMinY; cy <= cMaxY; cy++)
            {
                for (int cz = cMinZ; cz <= cMaxZ; cz++)
                {
                    for (int cx = cMinX; cx <= cMaxX; cx++)
                    {
                        EnqueueChunk(ChunkIndex(cx, cy, cz));
                    }
                }
            }
        }

        private void EnqueueChunk(int id)
        {
            if (id < 0 || id >= queuedChunks.Length || queuedChunks[id]) return;
            queuedChunks[id] = true;
            dirtyChunkQueue.Enqueue(id);
        }

        private void ClearQueue()
        {
            dirtyChunkQueue.Clear();
            if (queuedChunks != null) System.Array.Clear(queuedChunks, 0, queuedChunks.Length);
        }

        private void BuildQueuedChunks(VoxelTerrain3D terrain, int budget, bool forceCollider)
        {
            LastRebuiltChunks = 0;
            int rebuilt = 0;
            while (dirtyChunkQueue.Count > 0 && rebuilt < budget)
            {
                int id = dirtyChunkQueue.Dequeue();
                queuedChunks[id] = false;
                Chunk chunk = chunks[id];
                BuildChunk(terrain, chunk, forceCollider);
                rebuilt++;
            }
            LastRebuiltChunks = rebuilt;
        }

        private void AddFace(Vector3 basePos, float s, int faceIndex, Color col)
        {
            int start = vertices.Count;
            Vector3Int d = dirs[faceIndex];
            Vector3 normal = new Vector3(d.x, d.y, d.z);
            for (int k = 0; k < 4; k++)
            {
                vertices.Add(basePos + face[faceIndex, k] * s);
                normals.Add(normal);
                colors.Add(col);
                uvs.Add(faceUvs[k]);
            }

            triangles.Add(start + 0);
            triangles.Add(start + 1);
            triangles.Add(start + 2);
            triangles.Add(start + 0);
            triangles.Add(start + 2);
            triangles.Add(start + 3);
        }

        private bool TryBuildHeightfieldChunk(VoxelTerrain3D terrain, int x0, int y0, int z0, int x1, int y1, int z1, float s, MeteoriteSPH3DController controller)
        {
            if (!useHeightfieldMeshing) return false;
            if (controller != null && controller.layerViewEnabled) return false;

            for (int z = z0; z < z1; z++)
            {
                for (int x = x0; x < x1; x++)
                {
                    int top = terrain.TopSolidY(x, z);
                    if (top < 0) continue;

                    if (top >= y0 && top < y1)
                    {
                        VoxelCell3D topCell = terrain.Get(x, top, z);
                        AddFace(new Vector3(x * s, top * s, z * s), s, 2, ColorForCell(topCell));
                    }

                    AddHeightfieldSide(terrain, x, z, x + 1, z, top, y0, y1, s, 0);
                    AddHeightfieldSide(terrain, x, z, x - 1, z, top, y0, y1, s, 1);
                    AddHeightfieldSide(terrain, x, z, x, z + 1, top, y0, y1, s, 4);
                    AddHeightfieldSide(terrain, x, z, x, z - 1, top, y0, y1, s, 5);
                }
            }

            return true;
        }

        private void AddHeightfieldSide(VoxelTerrain3D terrain, int x, int z, int nx, int nz, int top, int y0, int y1, float s, int faceIndex)
        {
            int neighbourTop = terrain.TopSolidY(nx, nz);
            if (neighbourTop >= top) return;

            int fromY = Mathf.Max(neighbourTop + 1, y0);
            int toY = Mathf.Min(top, y1 - 1);
            for (int y = fromY; y <= toY; y++)
            {
                if (!terrain.InBounds(x, y, z)) continue;
                VoxelCell3D cell = terrain.Get(x, y, z);
                AddFace(new Vector3(x * s, y * s, z * s), s, faceIndex, ColorForCell(cell));
            }
        }

        private void BuildChunk(VoxelTerrain3D terrain, Chunk chunk, bool forceCollider)
        {
            vertices.Clear();
            normals.Clear();
            triangles.Clear();
            colors.Clear();
            uvs.Clear();

            int x0 = chunk.cx * cachedChunkSize;
            int y0 = chunk.cy * cachedChunkSize;
            int z0 = chunk.cz * cachedChunkSize;
            int x1 = Mathf.Min(x0 + cachedChunkSize, terrain.Width);
            int y1 = Mathf.Min(y0 + cachedChunkSize, terrain.Height);
            int z1 = Mathf.Min(z0 + cachedChunkSize, terrain.Depth);
            float s = terrain.CellSize;
            MeteoriteSPH3DController controller = MeteoriteSPH3DController.Instance;

            if (!TryBuildHeightfieldChunk(terrain, x0, y0, z0, x1, y1, z1, s, controller))
            {
                for (int y = y0; y < y1; y++)
                {
                    for (int z = z0; z < z1; z++)
                    {
                        for (int x = x0; x < x1; x++)
                        {
                            if (!terrain.IsSolid(x, y, z)) continue;
                            if (controller != null && !controller.IsVoxelVisible(x, y, z)) continue;

                            VoxelCell3D cell = terrain.Get(x, y, z);
                            Color col = ColorForCell(cell);
                            Vector3 basePos = new Vector3(x * s, y * s, z * s);

                            for (int f = 0; f < 6; f++)
                            {
                                Vector3Int d = dirs[f];
                                bool neighbourSolid = terrain.IsSolid(x + d.x, y + d.y, z + d.z);
                                bool neighbourVisible = controller == null || controller.IsVoxelVisible(x + d.x, y + d.y, z + d.z);
                                if (neighbourSolid && neighbourVisible) continue;

                                AddFace(basePos, s, f, col);
                            }
                        }
                    }
                }
            }

            Mesh mesh = chunk.mesh;
            mesh.Clear(false);
            if (vertices.Count > 0)
            {
                mesh.SetVertices(vertices);
                mesh.SetNormals(normals);
                mesh.SetTriangles(triangles, 0, false);
                mesh.SetColors(colors);
                mesh.SetUVs(0, uvs);
                mesh.RecalculateBounds();
            }

            if (enableChunkColliders && chunk.collider != null)
            {
                bool updateCollider = forceCollider
                                      || colliderUpdateIntervalFrames <= 1
                                      || Time.frameCount % Mathf.Max(1, colliderUpdateIntervalFrames) == 0
                                      || fullRebuildSerial <= 1;
                if (updateCollider)
                {
                    chunk.collider.enabled = true;
                    chunk.collider.sharedMesh = null;
                    chunk.collider.sharedMesh = vertices.Count > 0 ? mesh : null;
                }
            }
            else if (chunk.collider != null && chunk.collider.enabled)
            {
                chunk.collider.enabled = false;
                chunk.collider.sharedMesh = null;
            }
        }

        private Color ColorForCell(VoxelCell3D c)
        {
            if (!c.deposited)
            {
                Color baseGround = new Color(0.46f, 0.46f, 0.46f, 1f);
                Color damagedGround = new Color(0.26f, 0.25f, 0.24f, 1f);
                return Color.Lerp(baseGround, damagedGround, Mathf.Clamp01(c.damage * 0.45f));
            }

            float t = Mathf.Clamp01(c.temperature / 650f);
            Color coldDeposit = new Color(0.28f, 0.19f, 0.11f, 1f);
            Color warmDeposit = new Color(0.50f, 0.25f, 0.10f, 1f);
            Color hotDeposit = new Color(0.95f, 0.50f, 0.08f, 1f);
            Color col = t < 0.5f ? Color.Lerp(coldDeposit, warmDeposit, t * 2f) : Color.Lerp(warmDeposit, hotDeposit, (t - 0.5f) * 2f);
            if (c.damage > 0.65f) col = Color.Lerp(col, Color.red, 0.20f);
            return col;
        }
    }
}
