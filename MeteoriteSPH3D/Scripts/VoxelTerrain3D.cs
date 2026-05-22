using System.Collections.Generic;
using UnityEngine;

namespace MeteoriteSPH3D
{
    public sealed class VoxelTerrain3D
    {
        public readonly int Width;
        public readonly int Height;
        public readonly int Depth;
        public readonly float CellSize;
        public readonly VoxelCell3D[] Cells;

        public float WorldWidth { get { return Width * CellSize; } }
        public float WorldHeight { get { return Height * CellSize; } }
        public float WorldDepth { get { return Depth * CellSize; } }

        public int SolidCount { get; private set; }
        public bool HasDirtyBounds { get { return dirtyBoundsValid; } }
        public bool HasDirtyVoxels { get { return dirtyVoxelIndices.Count > 0; } }

        private readonly List<int> thermalIndices = new List<int>(4096);
        private readonly bool[] thermalQueued;
        private readonly int[] topSolidYByColumn;
        private readonly List<int> dirtyVoxelIndices = new List<int>(4096);
        private readonly bool[] dirtyVoxelQueued;

        private bool dirtyBoundsValid;
        private int dirtyMinX;
        private int dirtyMinY;
        private int dirtyMinZ;
        private int dirtyMaxX;
        private int dirtyMaxY;
        private int dirtyMaxZ;

        public VoxelTerrain3D(int width, int height, int depth, float cellSize)
        {
            Width = Mathf.Max(4, width);
            Height = Mathf.Max(4, height);
            Depth = Mathf.Max(4, depth);
            CellSize = Mathf.Max(0.05f, cellSize);
            Cells = new VoxelCell3D[Width * Height * Depth];
            thermalQueued = new bool[Cells.Length];
            topSolidYByColumn = new int[Width * Depth];
            dirtyVoxelQueued = new bool[Cells.Length];
            for (int i = 0; i < topSolidYByColumn.Length; i++) topSolidYByColumn[i] = -1;
        }

        public int Index(int x, int y, int z)
        {
            return x + Width * (z + Depth * y);
        }

        public int ColumnIndex(int x, int z)
        {
            return x + Width * z;
        }

        public void UnpackIndex(int index, out int x, out int y, out int z)
        {
            y = index / (Width * Depth);
            int rem = index - y * Width * Depth;
            z = rem / Width;
            x = rem - z * Width;
        }

        public int TopSolidY(int x, int z)
        {
            if (x < 0 || x >= Width || z < 0 || z >= Depth) return -1;
            return topSolidYByColumn[ColumnIndex(x, z)];
        }

        public float SurfaceHeightWorld(int x, int z)
        {
            int top = TopSolidY(x, z);
            return top >= 0 ? (top + 1) * CellSize : 0f;
        }

        private void UpdateTopSolidCacheAfterSet(int x, int y, int z, bool wasSolid, bool isSolid)
        {
            int ci = ColumnIndex(x, z);
            int oldTop = topSolidYByColumn[ci];
            if (isSolid)
            {
                if (y > oldTop) topSolidYByColumn[ci] = y;
                return;
            }

            if (wasSolid && y == oldTop)
            {
                int newTop = -1;
                for (int yy = y - 1; yy >= 0; yy--)
                {
                    if (Cells[Index(x, yy, z)].solid)
                    {
                        newTop = yy;
                        break;
                    }
                }
                topSolidYByColumn[ci] = newTop;
            }
        }

        public bool InBounds(int x, int y, int z)
        {
            return x >= 0 && x < Width && y >= 0 && y < Height && z >= 0 && z < Depth;
        }

        public bool IsSolid(int x, int y, int z)
        {
            return InBounds(x, y, z) && Cells[Index(x, y, z)].solid;
        }

        public VoxelCell3D Get(int x, int y, int z)
        {
            return Cells[Index(x, y, z)];
        }

        public void Set(int x, int y, int z, VoxelCell3D cell)
        {
            if (!InBounds(x, y, z)) return;
            int i = Index(x, y, z);
            bool wasSolid = Cells[i].solid;
            cell.damage = Mathf.Clamp01(cell.damage);
            Cells[i] = cell;
            if (!wasSolid && cell.solid) SolidCount++;
            if (wasSolid && !cell.solid) SolidCount--;
            UpdateTopSolidCacheAfterSet(x, y, z, wasSolid, cell.solid);
            MarkDirtyVoxel(x, y, z);
            QueueThermalIfNeeded(i, cell);
        }

        public void SetSolid(int x, int y, int z, bool solid, float temperature, float pressure, float damage, bool deposited = false)
        {
            if (!InBounds(x, y, z)) return;
            int i = Index(x, y, z);
            bool wasSolid = Cells[i].solid;
            Cells[i].solid = solid;
            Cells[i].temperature = Mathf.Max(0f, temperature);
            Cells[i].pressure = Mathf.Max(0f, pressure);
            Cells[i].damage = Mathf.Clamp01(damage);
            Cells[i].deposited = deposited;

            if (!wasSolid && solid) SolidCount++;
            if (wasSolid && !solid) SolidCount--;
            UpdateTopSolidCacheAfterSet(x, y, z, wasSolid, solid);
            MarkDirtyVoxel(x, y, z);
            QueueThermalIfNeeded(i, Cells[i]);
        }

        public void GenerateFlat(int baseHeight)
        {
            ClearThermalState();
            SolidCount = 0;
            for (int i = 0; i < topSolidYByColumn.Length; i++) topSolidYByColumn[i] = -1;
            baseHeight = Mathf.Clamp(baseHeight, 1, Height - 2);

            for (int y = 0; y < Height; y++)
            {
                for (int z = 0; z < Depth; z++)
                {
                    for (int x = 0; x < Width; x++)
                    {
                        bool solid = y <= baseHeight;
                        Cells[Index(x, y, z)] = new VoxelCell3D
                        {
                            solid = solid,
                            temperature = 0f,
                            pressure = 0f,
                            damage = 0f,
                            deposited = false
                        };
                        if (solid)
                        {
                            SolidCount++;
                            int ci = ColumnIndex(x, z);
                            if (y > topSolidYByColumn[ci]) topSolidYByColumn[ci] = y;
                        }
                    }
                }
            }

            MarkAllDirty();
        }

        public void GenerateRelief(int baseHeight, int amplitudeCells, float noiseScale, int seed)
        {
            ClearThermalState();
            SolidCount = 0;
            for (int i = 0; i < topSolidYByColumn.Length; i++) topSolidYByColumn[i] = -1;
            baseHeight = Mathf.Clamp(baseHeight, 1, Height - 2);
            amplitudeCells = Mathf.Clamp(amplitudeCells, 0, Mathf.Max(0, Height / 2));
            noiseScale = Mathf.Max(0.001f, noiseScale);

            float seedA = seed * 13.731f + 19.17f;
            float seedB = seed * 7.113f + 91.43f;
            float cx = (Width - 1) * 0.5f;
            float cz = (Depth - 1) * 0.5f;
            float invMax = 1f / Mathf.Max(1f, Mathf.Min(Width, Depth) * 0.5f);

            int[] heightMap = new int[Width * Depth];
            for (int z = 0; z < Depth; z++)
            {
                for (int x = 0; x < Width; x++)
                {
                    float nx = x * noiseScale;
                    float nz = z * noiseScale;

                    float dx = (x - cx) * invMax;
                    float dz = (z - cz) * invMax;
                    float radial = Mathf.Sqrt(dx * dx + dz * dz);

                    float dome = Mathf.Clamp01(1f - radial * 0.92f);
                    dome = dome * dome * (3f - 2f * dome);
                    float cone = Mathf.Clamp01(1f - radial * 1.05f);
                    float peak = Mathf.Clamp01(1f - radial * 1.85f);
                    peak *= peak;
                    float shoulder = Mathf.Clamp01(1f - radial * 0.62f);

                    float n1 = Mathf.PerlinNoise(nx + seedA, nz + seedB) - 0.5f;
                    float n2 = Mathf.PerlinNoise(nx * 1.9f + seedB * 0.37f, nz * 1.9f + seedA * 0.37f) - 0.5f;
                    float ridge = 1f - Mathf.Abs(Mathf.PerlinNoise(nx * 1.25f + seedA * 0.21f, nz * 1.25f + seedB * 0.21f) * 2f - 1f);
                    ridge *= ridge;

                    float slopeNoise = n1 * amplitudeCells * 0.28f + n2 * amplitudeCells * 0.12f;
                    float summitNoise = ridge * amplitudeCells * 0.22f * Mathf.Clamp01(1f - radial * 1.2f);

                    float h = baseHeight
                              + shoulder * amplitudeCells * 0.28f
                              + dome * amplitudeCells * 1.10f
                              + cone * amplitudeCells * 0.72f
                              + peak * amplitudeCells * 0.42f
                              + slopeNoise
                              + summitNoise;

                    heightMap[x + z * Width] = Mathf.Clamp(Mathf.RoundToInt(h), 2, Height - 3);
                }
            }

            int[] smooth = new int[heightMap.Length];
            for (int z = 0; z < Depth; z++)
            {
                for (int x = 0; x < Width; x++)
                {
                    int center = heightMap[x + z * Width] * 5;
                    int cross = 0;
                    int crossCount = 0;
                    int[] oxs = { 1, -1, 0, 0 };
                    int[] ozs = { 0, 0, 1, -1 };
                    for (int i = 0; i < 4; i++)
                    {
                        int sx = Mathf.Clamp(x + oxs[i], 0, Width - 1);
                        int sz = Mathf.Clamp(z + ozs[i], 0, Depth - 1);
                        cross += heightMap[sx + sz * Width];
                        crossCount++;
                    }
                    smooth[x + z * Width] = Mathf.RoundToInt((center + cross) / (float)(5 + crossCount));
                }
            }

            for (int y = 0; y < Height; y++)
            {
                for (int z = 0; z < Depth; z++)
                {
                    for (int x = 0; x < Width; x++)
                    {
                        bool solid = y <= smooth[x + z * Width];
                        Cells[Index(x, y, z)] = new VoxelCell3D
                        {
                            solid = solid,
                            temperature = 0f,
                            pressure = 0f,
                            damage = 0f,
                            deposited = false
                        };
                        if (solid)
                        {
                            SolidCount++;
                            int ci = ColumnIndex(x, z);
                            if (y > topSolidYByColumn[ci]) topSolidYByColumn[ci] = y;
                        }
                    }
                }
            }

            MarkAllDirty();
        }

        public Vector3 CellCenter(int x, int y, int z)
        {
            return new Vector3((x + 0.5f) * CellSize, (y + 0.5f) * CellSize, (z + 0.5f) * CellSize);
        }

        public Vector3Int WorldToCell(Vector3 world)
        {
            return new Vector3Int(
                Mathf.FloorToInt(world.x / CellSize),
                Mathf.FloorToInt(world.y / CellSize),
                Mathf.FloorToInt(world.z / CellSize));
        }

        public int SolidNeighbourCount6(int x, int y, int z)
        {
            int c = 0;
            if (IsSolid(x + 1, y, z)) c++;
            if (IsSolid(x - 1, y, z)) c++;
            if (IsSolid(x, y + 1, z)) c++;
            if (IsSolid(x, y - 1, z)) c++;
            if (IsSolid(x, y, z + 1)) c++;
            if (IsSolid(x, y, z - 1)) c++;
            return c;
        }

        public bool HasSupport(int x, int y, int z, int requiredNeighbours)
        {
            if (!InBounds(x, y, z) || IsSolid(x, y, z)) return false;
            if (IsSolid(x, y - 1, z)) return true;
            return SolidNeighbourCount6(x, y, z) >= requiredNeighbours;
        }

        public void MarkDirtyVoxel(int x, int y, int z)
        {
            if (!InBounds(x, y, z)) return;
            if (!dirtyBoundsValid)
            {
                dirtyBoundsValid = true;
                dirtyMinX = dirtyMaxX = x;
                dirtyMinY = dirtyMaxY = y;
                dirtyMinZ = dirtyMaxZ = z;
            }
            else
            {
                if (x < dirtyMinX) dirtyMinX = x;
                if (y < dirtyMinY) dirtyMinY = y;
                if (z < dirtyMinZ) dirtyMinZ = z;
                if (x > dirtyMaxX) dirtyMaxX = x;
                if (y > dirtyMaxY) dirtyMaxY = y;
                if (z > dirtyMaxZ) dirtyMaxZ = z;
            }

            int index = Index(x, y, z);
            if (!dirtyVoxelQueued[index])
            {
                dirtyVoxelQueued[index] = true;
                dirtyVoxelIndices.Add(index);
            }
        }

        public void MarkAllDirty()
        {
            ClearDirtyVoxelQueue();
            dirtyBoundsValid = true;
            dirtyMinX = 0;
            dirtyMinY = 0;
            dirtyMinZ = 0;
            dirtyMaxX = Width - 1;
            dirtyMaxY = Height - 1;
            dirtyMaxZ = Depth - 1;
        }

        public bool ConsumeDirtyVoxelIndices(List<int> target)
        {
            if (target == null) return false;
            target.Clear();
            if (dirtyVoxelIndices.Count == 0) return false;

            for (int i = 0; i < dirtyVoxelIndices.Count; i++)
            {
                int index = dirtyVoxelIndices[i];
                target.Add(index);
                dirtyVoxelQueued[index] = false;
            }
            dirtyVoxelIndices.Clear();
            dirtyBoundsValid = false;
            return target.Count > 0;
        }

        private void ClearDirtyVoxelQueue()
        {
            for (int i = 0; i < dirtyVoxelIndices.Count; i++)
            {
                dirtyVoxelQueued[dirtyVoxelIndices[i]] = false;
            }
            dirtyVoxelIndices.Clear();
        }

        public bool ConsumeDirtyBounds(out int minX, out int minY, out int minZ, out int maxX, out int maxY, out int maxZ)
        {
            if (!dirtyBoundsValid)
            {
                minX = minY = minZ = maxX = maxY = maxZ = 0;
                return false;
            }

            minX = dirtyMinX;
            minY = dirtyMinY;
            minZ = dirtyMinZ;
            maxX = dirtyMaxX;
            maxY = dirtyMaxY;
            maxZ = dirtyMaxZ;
            dirtyBoundsValid = false;
            return true;
        }

        public void CoolVoxels(float dt, float coolingRate)
        {
            float cool = Mathf.Max(0f, coolingRate) * dt;
            if (cool <= 0f || thermalIndices.Count == 0) return;

            for (int t = thermalIndices.Count - 1; t >= 0; t--)
            {
                int i = thermalIndices[t];
                VoxelCell3D c = Cells[i];

                if (!c.solid && c.temperature <= 0f && c.pressure <= 0f)
                {
                    thermalQueued[i] = false;
                    thermalIndices.RemoveAt(t);
                    continue;
                }

                c.temperature = Mathf.Max(0f, c.temperature - cool);
                c.pressure = Mathf.Max(0f, c.pressure - cool * 1.5f);
                c.damage = Mathf.Clamp01(c.damage);
                Cells[i] = c;

                if (c.temperature <= 0f && c.pressure <= 0f)
                {
                    thermalQueued[i] = false;
                    thermalIndices.RemoveAt(t);
                }
            }
        }

        private void QueueThermalIfNeeded(int index, VoxelCell3D cell)
        {
            if (cell.temperature <= 0f && cell.pressure <= 0f) return;
            if (thermalQueued[index]) return;
            thermalQueued[index] = true;
            thermalIndices.Add(index);
        }

        private void ClearThermalState()
        {
            thermalIndices.Clear();
            System.Array.Clear(thermalQueued, 0, thermalQueued.Length);
            dirtyBoundsValid = false;
            ClearDirtyVoxelQueue();
        }
    }
}
