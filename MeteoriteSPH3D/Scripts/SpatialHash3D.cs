using System;
using System.Collections.Generic;
using UnityEngine;

namespace MeteoriteSPH3D
{
    public sealed class SpatialHash3D
    {
        private struct CellKey : IEquatable<CellKey>
        {
            public int x;
            public int y;
            public int z;

            public CellKey(int x, int y, int z)
            {
                this.x = x;
                this.y = y;
                this.z = z;
            }

            public bool Equals(CellKey other)
            {
                return x == other.x && y == other.y && z == other.z;
            }

            public override bool Equals(object obj)
            {
                return obj is CellKey other && Equals(other);
            }

            public override int GetHashCode()
            {
                unchecked
                {
                    int h = x * 73856093;
                    h ^= y * 19349663;
                    h ^= z * 83492791;
                    return h;
                }
            }
        }

        private readonly Dictionary<CellKey, List<int>> buckets = new Dictionary<CellKey, List<int>>(2048);
        private readonly List<List<int>> usedBuckets = new List<List<int>>(2048);
        private float cellSize = 1f;

        public void Rebuild(List<SPHParticle3D> particles, float smoothingRadius)
        {
            for (int i = 0; i < usedBuckets.Count; i++)
            {
                usedBuckets[i].Clear();
            }
            usedBuckets.Clear();

            cellSize = Mathf.Max(0.001f, smoothingRadius);

            for (int i = 0; i < particles.Count; i++)
            {
                SPHParticle3D p = particles[i];
                if (p == null || !p.active) continue;

                CellKey cell = ToCellKey(p.position);
                List<int> list;
                if (!buckets.TryGetValue(cell, out list))
                {
                    list = new List<int>(16);
                    buckets.Add(cell, list);
                }
                if (list.Count == 0) usedBuckets.Add(list);
                list.Add(i);
            }
        }

        public Vector3Int ToCell(Vector3 p)
        {
            CellKey key = ToCellKey(p);
            return new Vector3Int(key.x, key.y, key.z);
        }

        private CellKey ToCellKey(Vector3 p)
        {
            return new CellKey(
                Mathf.FloorToInt(p.x / cellSize),
                Mathf.FloorToInt(p.y / cellSize),
                Mathf.FloorToInt(p.z / cellSize));
        }

        public void Query(Vector3 position, List<int> result)
        {
            result.Clear();
            CellKey c0 = ToCellKey(position);
            for (int y = -1; y <= 1; y++)
            {
                for (int z = -1; z <= 1; z++)
                {
                    for (int x = -1; x <= 1; x++)
                    {
                        CellKey c = new CellKey(c0.x + x, c0.y + y, c0.z + z);
                        List<int> list;
                        if (!buckets.TryGetValue(c, out list) || list.Count == 0) continue;
                        result.AddRange(list);
                    }
                }
            }
        }
    }
}
