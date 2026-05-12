using System.Collections.Generic;
using UnityEngine;

namespace MeteoriteSPH3D
{
    public sealed class SpatialHash3D
    {
        private readonly Dictionary<Vector3Int, List<int>> buckets = new Dictionary<Vector3Int, List<int>>(2048);
        private float cellSize = 1f;

        public void Rebuild(List<SPHParticle3D> particles, float smoothingRadius)
        {
            buckets.Clear();
            cellSize = Mathf.Max(0.001f, smoothingRadius);

            for (int i = 0; i < particles.Count; i++)
            {
                SPHParticle3D p = particles[i];
                if (p == null || !p.active) continue;

                Vector3Int cell = ToCell(p.position);
                List<int> list;
                if (!buckets.TryGetValue(cell, out list))
                {
                    list = new List<int>(16);
                    buckets.Add(cell, list);
                }
                list.Add(i);
            }
        }

        public Vector3Int ToCell(Vector3 p)
        {
            return new Vector3Int(
                Mathf.FloorToInt(p.x / cellSize),
                Mathf.FloorToInt(p.y / cellSize),
                Mathf.FloorToInt(p.z / cellSize));
        }

        public void Query(Vector3 position, List<int> result)
        {
            result.Clear();
            Vector3Int c0 = ToCell(position);
            for (int y = -1; y <= 1; y++)
            {
                for (int z = -1; z <= 1; z++)
                {
                    for (int x = -1; x <= 1; x++)
                    {
                        Vector3Int c = new Vector3Int(c0.x + x, c0.y + y, c0.z + z);
                        List<int> list;
                        if (!buckets.TryGetValue(c, out list)) continue;
                        result.AddRange(list);
                    }
                }
            }
        }
    }
}
