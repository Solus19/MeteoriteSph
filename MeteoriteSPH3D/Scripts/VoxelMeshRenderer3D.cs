using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace MeteoriteSPH3D
{
    [RequireComponent(typeof(MeshFilter), typeof(MeshRenderer), typeof(MeshCollider))]
    public sealed class VoxelMeshRenderer3D : MonoBehaviour
    {
        private MeshFilter meshFilter;
        private MeshCollider meshCollider;
        private Mesh mesh;
        private Material material;

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

        public void Initialize()
        {
            meshFilter = GetComponent<MeshFilter>();
            meshCollider = GetComponent<MeshCollider>();
            mesh = new Mesh();
            mesh.indexFormat = IndexFormat.UInt32;
            mesh.name = "Meteorite SPH 3D Voxel Surface";
            meshFilter.sharedMesh = mesh;

            Shader shader = Shader.Find("MeteoriteSPH3D/VertexColorUnlit");
            if (shader == null) shader = Shader.Find("Sprites/Default");
            material = new Material(shader);
            GetComponent<MeshRenderer>().sharedMaterial = material;
        }

        public void Rebuild(VoxelTerrain3D terrain)
        {
            if (mesh == null) Initialize();

            List<Vector3> vertices = new List<Vector3>(12000);
            List<int> triangles = new List<int>(18000);
            List<Color> colors = new List<Color>(12000);
            List<Vector2> uvs = new List<Vector2>(12000);
            float s = terrain.CellSize;

            for (int y = 0; y < terrain.Height; y++)
            {
                for (int z = 0; z < terrain.Depth; z++)
                {
                    for (int x = 0; x < terrain.Width; x++)
                    {
                        if (!terrain.IsSolid(x, y, z)) continue;
                        if (MeteoriteSPH3DController.Instance != null && !MeteoriteSPH3DController.Instance.IsVoxelVisible(x, y, z)) continue;

                        VoxelCell3D cell = terrain.Get(x, y, z);
                        Color col = ColorForCell(cell);

                        for (int f = 0; f < 6; f++)
                        {
                            Vector3Int d = dirs[f];
                            bool neighbourSolid = terrain.IsSolid(x + d.x, y + d.y, z + d.z);
                            bool neighbourVisible = MeteoriteSPH3DController.Instance == null || MeteoriteSPH3DController.Instance.IsVoxelVisible(x + d.x, y + d.y, z + d.z);
                            if (neighbourSolid && neighbourVisible) continue;

                            int start = vertices.Count;
                            Vector3 basePos = new Vector3(x * s, y * s, z * s);
                            Vector2[] faceUvs =
                            {
                                new Vector2(0f, 0f),
                                new Vector2(0f, 1f),
                                new Vector2(1f, 1f),
                                new Vector2(1f, 0f)
                            };

                            for (int k = 0; k < 4; k++)
                            {
                                vertices.Add(basePos + face[f, k] * s);
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
                    }
                }
            }

            mesh.Clear();
            mesh.SetVertices(vertices);
            mesh.SetTriangles(triangles, 0);
            mesh.SetColors(colors);
            mesh.SetUVs(0, uvs);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();

            meshCollider.sharedMesh = null;
            meshCollider.sharedMesh = mesh;
        }

        private Color ColorForCell(VoxelCell3D c)
        {
            if (!c.deposited)
            {
                Color emeraldGround = new Color(0.02f, 0.48f, 0.31f, 1f);
                Color damagedEmerald = new Color(0.03f, 0.32f, 0.22f, 1f);
                return Color.Lerp(emeraldGround, damagedEmerald, Mathf.Clamp01(c.damage * 0.35f));
            }

            float t = Mathf.Clamp01(c.temperature / 650f);
            Color coldDeposit = new Color(0.22f, 0.18f, 0.13f, 1f);
            Color warmDeposit = new Color(0.55f, 0.18f, 0.08f, 1f);
            Color hotDeposit = new Color(1.0f, 0.58f, 0.05f, 1f);
            Color col = t < 0.5f ? Color.Lerp(coldDeposit, warmDeposit, t * 2f) : Color.Lerp(warmDeposit, hotDeposit, (t - 0.5f) * 2f);
            if (c.damage > 0.65f) col = Color.Lerp(col, Color.red, 0.25f);
            return col;
        }
    }
}
