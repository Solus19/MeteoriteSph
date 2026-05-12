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

        public VoxelTerrain3D(int width, int height, int depth, float cellSize)
        {
            Width = Mathf.Max(4, width);
            Height = Mathf.Max(4, height);
            Depth = Mathf.Max(4, depth);
            CellSize = Mathf.Max(0.05f, cellSize);
            Cells = new VoxelCell3D[Width * Height * Depth];
        }

        public int Index(int x, int y, int z)
        {
            return x + Width * (z + Depth * y);
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
            int i = Index(x, y, z);
            bool wasSolid = Cells[i].solid;
            Cells[i] = cell;
            if (!wasSolid && cell.solid) SolidCount++;
            if (wasSolid && !cell.solid) SolidCount--;
        }

        public void SetSolid(int x, int y, int z, bool solid, float temperature, float pressure, float damage, bool deposited = false)
        {
            if (!InBounds(x, y, z)) return;
            int i = Index(x, y, z);
            bool wasSolid = Cells[i].solid;
            Cells[i].solid = solid;
            Cells[i].temperature = temperature;
            Cells[i].pressure = pressure;
            Cells[i].damage = damage;
            Cells[i].deposited = deposited;

            if (!wasSolid && solid) SolidCount++;
            if (wasSolid && !solid) SolidCount--;
        }

        public void GenerateFlat(int baseHeight)
        {
            SolidCount = 0;
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
                        if (solid) SolidCount++;
                    }
                }
            }
        }

        public void GenerateRelief(int baseHeight, int amplitudeCells, float noiseScale, int seed)
        {
            SolidCount = 0;
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
                    dome = dome * dome * (3f - 2f * dome); // smoothstep-like
                    float cone = Mathf.Clamp01(1f - radial * 1.05f);
                    float peak = Mathf.Clamp01(1f - radial * 1.85f);
                    peak = peak * peak;
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

            // Light smoothing so the central mountain stays readable, but with a clear single peak.
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
                        if (solid) SolidCount++;
                    }
                }
            }
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

        public void CoolVoxels(float dt, float coolingRate)
        {
            float cool = Mathf.Max(0f, coolingRate) * dt;
            for (int i = 0; i < Cells.Length; i++)
            {
                if (!Cells[i].solid) continue;
                VoxelCell3D c = Cells[i];
                c.temperature = Mathf.Max(0f, c.temperature - cool);
                c.pressure = Mathf.Max(0f, c.pressure - cool * 1.5f);
                c.damage = Mathf.Clamp01(c.damage);
                Cells[i] = c;
            }
        }
    }
}
