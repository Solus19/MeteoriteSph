using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace MeteoriteSPH3D
{
    public sealed class ParticleRenderer3D : MonoBehaviour
    {
        private Mesh sphereMesh;
        private readonly Material[] materials = new Material[4];
        private readonly Matrix4x4[] matrices = new Matrix4x4[1023];
        private Material gpuMaterial;
        private ComputeBuffer argsBuffer;
        private readonly uint[] args = new uint[5];
        private float radius = 0.13f;

        public void Initialize(float particleRadius)
        {
            radius = particleRadius;
            GameObject temp = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            sphereMesh = temp.GetComponent<MeshFilter>().sharedMesh;
            Destroy(temp);

            Shader shader = Shader.Find("MeteoriteSPH3D/ParticleUnlit");
            if (shader == null) shader = Shader.Find("Sprites/Default");

            Color[] colors =
            {
                new Color(0.35f, 0.26f, 0.18f, 1f),
                new Color(0.95f, 0.22f, 0.06f, 1f),
                new Color(1.0f, 0.55f, 0.04f, 1f),
                new Color(1.0f, 0.95f, 0.38f, 1f)
            };

            for (int i = 0; i < materials.Length; i++)
            {
                materials[i] = new Material(shader);
                materials[i].enableInstancing = true;
                if (materials[i].HasProperty("_Color")) materials[i].SetColor("_Color", colors[i]);
            }

            Shader gpuShader = Shader.Find("MeteoriteSPH3D/ParticleGPUInstanced");
            if (gpuShader != null)
            {
                gpuMaterial = new Material(gpuShader);
                gpuMaterial.enableInstancing = true;
            }
            argsBuffer = new ComputeBuffer(1, args.Length * sizeof(uint), ComputeBufferType.IndirectArguments);
            args[0] = sphereMesh != null ? sphereMesh.GetIndexCount(0) : 0;
            args[1] = 0;
            args[2] = sphereMesh != null ? sphereMesh.GetIndexStart(0) : 0;
            args[3] = sphereMesh != null ? sphereMesh.GetBaseVertex(0) : 0;
            args[4] = 0;
            argsBuffer.SetData(args);
        }

        public void SetRadius(float r)
        {
            radius = Mathf.Max(0.02f, r);
        }

        private void OnDestroy()
        {
            if (argsBuffer != null) argsBuffer.Release();
            argsBuffer = null;
        }

        private void LateUpdate()
        {
            MeteoriteSPH3DController c = MeteoriteSPH3DController.Instance;
            if (c == null || !c.ShouldRenderParticles) return;
            if (c.UseGpuSimulation && c.GpuParticleBuffer != null)
            {
                DrawGpu(c);
                return;
            }
            if (c.Particles == null) return;
            Draw(c.Particles);
        }


        private void DrawGpu(MeteoriteSPH3DController c)
        {
            if (sphereMesh == null || gpuMaterial == null || argsBuffer == null || c.GpuParticleDrawCount <= 0) return;

            args[0] = sphereMesh.GetIndexCount(0);
            args[1] = (uint)c.GpuParticleDrawCount;
            args[2] = sphereMesh.GetIndexStart(0);
            args[3] = sphereMesh.GetBaseVertex(0);
            args[4] = 0;
            argsBuffer.SetData(args);

            gpuMaterial.SetBuffer("_Particles", c.GpuParticleBuffer);
            gpuMaterial.SetFloat("_Radius", radius);
            gpuMaterial.SetFloat("_CellSize", c.cellSize);
            gpuMaterial.SetInt("_LayerViewEnabled", c.layerViewEnabled ? 1 : 0);
            gpuMaterial.SetInt("_LayerViewAxis", (int)c.layerViewAxis);
            gpuMaterial.SetInt("_SingleLayerMode", c.singleLayerMode ? 1 : 0);
            gpuMaterial.SetInt("_VisibleLayerMin", c.visibleLayerMin);
            gpuMaterial.SetInt("_VisibleLayerMax", c.visibleLayerMax);
            gpuMaterial.SetInt("_SingleVisibleLayer", c.singleVisibleLayer);

            float w = c.terrainWidth * c.cellSize;
            float h = (c.terrainHeight * c.cellSize) + c.extraWorldHeight + 10f;
            float d = c.terrainDepth * c.cellSize;
            Bounds bounds = new Bounds(new Vector3(w * 0.5f, h * 0.5f, d * 0.5f), new Vector3(w + 20f, h + 20f, d + 20f));
            Graphics.DrawMeshInstancedIndirect(
                sphereMesh,
                0,
                gpuMaterial,
                bounds,
                argsBuffer,
                0,
                null,
                ShadowCastingMode.Off,
                false);
        }

        private void Draw(List<SPHParticle3D> particles)
        {
            if (sphereMesh == null) return;

            for (int m = 0; m < materials.Length; m++)
            {
                if (materials[m] != null && !materials[m].enableInstancing)
                    materials[m].enableInstancing = true;
            }

            for (int bucket = 0; bucket < 4; bucket++)
            {
                int count = 0;
                for (int i = 0; i < particles.Count; i++)
                {
                    SPHParticle3D p = particles[i];
                    if (p == null || !p.active) continue;
                    if (MeteoriteSPH3DController.Instance != null && !MeteoriteSPH3DController.Instance.IsPositionVisible(p.position)) continue;
                    if (TemperatureBucket(p.temperature) != bucket) continue;

                    matrices[count++] = Matrix4x4.TRS(p.position, Quaternion.identity, Vector3.one * (radius * 2f));
                    if (count == 1023)
                    {
                        Graphics.DrawMeshInstanced(sphereMesh, 0, materials[bucket], matrices, count, null, ShadowCastingMode.Off, false);
                        count = 0;
                    }
                }

                if (count > 0)
                {
                    Graphics.DrawMeshInstanced(sphereMesh, 0, materials[bucket], matrices, count, null, ShadowCastingMode.Off, false);
                }
            }
        }

        private int TemperatureBucket(float temp)
        {
            if (temp > 520f) return 3;
            if (temp > 300f) return 2;
            if (temp > 90f) return 1;
            return 0;
        }
    }
}
