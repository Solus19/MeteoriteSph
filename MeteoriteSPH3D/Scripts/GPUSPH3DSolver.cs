using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;

namespace MeteoriteSPH3D
{
    public sealed class GPUSPH3DSolver
    {
        [StructLayout(LayoutKind.Sequential)]
        public struct GPUParticle
        {
            public Vector3 position;
            public float age;
            public Vector3 velocity;
            public float temperature;
            public float density;
            public float pressure;
            public float nearDensity;
            public float recentGroundContact;
            public float mass;
            public float active;
            public float pad0;
            public float pad1;
        }

        public ComputeBuffer ParticleBuffer { get { return particleBuffer; } }
        public int ActiveCount { get; private set; }
        public bool IsReady { get { return compute != null && particleBuffer != null && terrainSolidBuffer != null; } }

        private ComputeShader compute;
        private ComputeBuffer particleBuffer;
        private ComputeBuffer terrainSolidBuffer;
        private ComputeBuffer cellCountsBuffer;
        private ComputeBuffer cellItemsBuffer;

        private GPUParticle[] particleCache;
        private int[] terrainSolidCache;
        private readonly uint[] clearArgs = new uint[3];

        private int maxParticles;
        private int maxPerCell;
        private int gridX;
        private int gridY;
        private int gridZ;
        private int gridCellCount;

        private int kClear;
        private int kBuild;
        private int kDensity;
        private int kIntegrate;

        public void Initialize(MeteoriteSPH3DController c, VoxelTerrain3D terrain)
        {
            Release();

            compute = Resources.Load<ComputeShader>("MeteoriteSPH3DParticles");
            if (compute == null)
            {
                Debug.LogError("MeteoriteSPH3DParticles.compute not found in Resources.");
                return;
            }

            maxParticles = Mathf.Max(256, c.maxParticles);
            maxPerCell = Mathf.Clamp(c.gpuGridMaxParticlesPerCell, 16, 256);

            float h = Mathf.Max(0.05f, c.smoothingRadius);
            gridX = Mathf.Max(1, Mathf.CeilToInt(terrain.WorldWidth / h) + 2);
            gridY = Mathf.Max(1, Mathf.CeilToInt((terrain.WorldHeight + c.extraWorldHeight) / h) + 2);
            gridZ = Mathf.Max(1, Mathf.CeilToInt(terrain.WorldDepth / h) + 2);
            gridCellCount = gridX * gridY * gridZ;

            particleCache = new GPUParticle[maxParticles];
            particleBuffer = new ComputeBuffer(maxParticles, Marshal.SizeOf(typeof(GPUParticle)), ComputeBufferType.Structured);
            terrainSolidBuffer = new ComputeBuffer(terrain.Width * terrain.Height * terrain.Depth, sizeof(int), ComputeBufferType.Structured);
            cellCountsBuffer = new ComputeBuffer(gridCellCount, sizeof(int), ComputeBufferType.Structured);
            cellItemsBuffer = new ComputeBuffer(gridCellCount * maxPerCell, sizeof(int), ComputeBufferType.Structured);

            kClear = compute.FindKernel("ClearGrid");
            kBuild = compute.FindKernel("BuildGrid");
            kDensity = compute.FindKernel("DensityPressure");
            kIntegrate = compute.FindKernel("Integrate");

            BindBuffers(kClear);
            BindBuffers(kBuild);
            BindBuffers(kDensity);
            BindBuffers(kIntegrate);

            compute.SetInts("_GridSize", gridX, gridY, gridZ);
            compute.SetInt("_GridCellCount", gridCellCount);
            compute.SetInt("_MaxParticlesPerCell", maxPerCell);
            compute.SetInts("_TerrainSize", terrain.Width, terrain.Height, terrain.Depth);
            compute.SetFloat("_TerrainCellSize", terrain.CellSize);

            UploadTerrain(terrain);
            UploadFromParticles(null);
        }

        public void Release()
        {
            if (particleBuffer != null) particleBuffer.Release();
            if (terrainSolidBuffer != null) terrainSolidBuffer.Release();
            if (cellCountsBuffer != null) cellCountsBuffer.Release();
            if (cellItemsBuffer != null) cellItemsBuffer.Release();
            particleBuffer = null;
            terrainSolidBuffer = null;
            cellCountsBuffer = null;
            cellItemsBuffer = null;
            compute = null;
            particleCache = null;
            terrainSolidCache = null;
            ActiveCount = 0;
        }

        private void BindBuffers(int kernel)
        {
            compute.SetBuffer(kernel, "_Particles", particleBuffer);
            compute.SetBuffer(kernel, "_TerrainSolid", terrainSolidBuffer);
            compute.SetBuffer(kernel, "_CellCounts", cellCountsBuffer);
            compute.SetBuffer(kernel, "_CellItems", cellItemsBuffer);
        }

        public void UploadTerrain(VoxelTerrain3D terrain)
        {
            if (terrainSolidBuffer == null || terrain == null) return;
            int n = terrain.Width * terrain.Height * terrain.Depth;
            if (terrainSolidCache == null || terrainSolidCache.Length != n) terrainSolidCache = new int[n];

            int i = 0;
            for (int z = 0; z < terrain.Depth; z++)
            {
                for (int y = 0; y < terrain.Height; y++)
                {
                    for (int x = 0; x < terrain.Width; x++)
                    {
                        terrainSolidCache[i++] = terrain.IsSolid(x, y, z) ? 1 : 0;
                    }
                }
            }
            terrainSolidBuffer.SetData(terrainSolidCache);
        }

        public void UploadFromParticles(List<SPHParticle3D> particles)
        {
            if (particleBuffer == null || particleCache == null) return;
            System.Array.Clear(particleCache, 0, particleCache.Length);
            ActiveCount = 0;

            if (particles != null)
            {
                int count = Mathf.Min(maxParticles, particles.Count);
                for (int i = 0; i < count; i++)
                {
                    SPHParticle3D p = particles[i];
                    if (p == null || !p.active) continue;
                    GPUParticle g = new GPUParticle();
                    g.position = p.position;
                    g.age = p.age;
                    g.velocity = p.velocity;
                    g.temperature = p.temperature;
                    g.density = p.density;
                    g.pressure = p.pressure;
                    g.nearDensity = 0f;
                    g.recentGroundContact = p.recentGroundContact;
                    g.mass = p.mass;
                    g.active = 1f;
                    particleCache[ActiveCount++] = g;
                }

                if (particles.Count > ActiveCount)
                {
                    particles.RemoveRange(ActiveCount, particles.Count - ActiveCount);
                }
            }

            particleBuffer.SetData(particleCache);
        }

        public void DownloadToParticles(List<SPHParticle3D> particles)
        {
            if (particleBuffer == null || particleCache == null || particles == null) return;
            if (ActiveCount <= 0)
            {
                particles.Clear();
                return;
            }

            particleBuffer.GetData(particleCache, 0, 0, ActiveCount);
            particles.Clear();
            for (int i = 0; i < ActiveCount; i++)
            {
                GPUParticle g = particleCache[i];
                if (g.active < 0.5f) continue;
                SPHParticle3D p = new SPHParticle3D(g.position, g.velocity, g.temperature, g.mass);
                p.age = g.age;
                p.density = g.density;
                p.pressure = g.pressure;
                p.recentGroundContact = g.recentGroundContact;
                particles.Add(p);
            }
            ActiveCount = Mathf.Min(maxParticles, particles.Count);
        }

        public void Step(MeteoriteSPH3DController c, VoxelTerrain3D terrain, float dt)
        {
            if (!IsReady || ActiveCount <= 0) return;

            if (NeedGridResize(c, terrain))
            {
                // Radius changed enough to alter grid dimensions. Download current GPU state first,
                // then rebuild buffers and reupload the same active particles.
                List<SPHParticle3D> temp = c.Particles;
                DownloadToParticles(temp);
                Initialize(c, terrain);
                UploadFromParticles(temp);
                if (ActiveCount <= 0) return;
            }

            SetParameters(c, terrain, dt);

            int groupsGrid = Mathf.CeilToInt(gridCellCount / 128f);
            compute.Dispatch(kClear, groupsGrid, 1, 1);

            int groupsParticles = Mathf.CeilToInt(ActiveCount / 128f);
            compute.Dispatch(kBuild, groupsParticles, 1, 1);
            compute.Dispatch(kDensity, groupsParticles, 1, 1);
            compute.Dispatch(kIntegrate, groupsParticles, 1, 1);
        }

        private bool NeedGridResize(MeteoriteSPH3DController c, VoxelTerrain3D terrain)
        {
            float h = Mathf.Max(0.05f, c.smoothingRadius);
            int nx = Mathf.Max(1, Mathf.CeilToInt(terrain.WorldWidth / h) + 2);
            int ny = Mathf.Max(1, Mathf.CeilToInt((terrain.WorldHeight + c.extraWorldHeight) / h) + 2);
            int nz = Mathf.Max(1, Mathf.CeilToInt(terrain.WorldDepth / h) + 2);
            int desiredMaxParticles = Mathf.Max(256, c.maxParticles);
            int desiredMaxPerCell = Mathf.Clamp(c.gpuGridMaxParticlesPerCell, 16, 256);
            return nx != gridX || ny != gridY || nz != gridZ || desiredMaxParticles != maxParticles || desiredMaxPerCell != maxPerCell;
        }

        private void SetParameters(MeteoriteSPH3DController c, VoxelTerrain3D terrain, float dt)
        {
            compute.SetInt("_ActiveCount", ActiveCount);
            compute.SetFloat("_Dt", dt);
            compute.SetFloat("_SmoothingRadius", Mathf.Max(0.05f, c.smoothingRadius));
            compute.SetFloat("_ParticleRadius", Mathf.Max(0.02f, c.particleRadius));
            compute.SetFloat("_ParticleSpacing", Mathf.Max(0.02f, c.particleSpacing));
            compute.SetFloat("_RestDensity", c.restDensity);
            compute.SetFloat("_MinDensity", c.minDensity);
            compute.SetFloat("_MaxDensity", c.maxDensity);
            compute.SetFloat("_GasConstant", c.gasConstant);
            compute.SetFloat("_PressureStrength", c.pressureStrength);
            compute.SetFloat("_NearPressureStrength", c.nearPressureStrength);
            compute.SetFloat("_CohesionStrength", c.cohesionStrength);
            compute.SetFloat("_XsphVelocityBlend", c.xsphVelocityBlend);
            compute.SetFloat("_Viscosity", c.viscosity);
            compute.SetFloat("_ColdViscosityMultiplier", c.coldViscosityMultiplier);
            compute.SetFloat("_ColdViscosityTemperature", c.coldViscosityTemperature);
            compute.SetFloat("_HotViscosityTemperature", Mathf.Max(c.hotViscosityTemperature, c.coldViscosityTemperature + 0.01f));
            compute.SetFloat("_Gravity", c.gravity);
            compute.SetFloat("_Damping", c.damping);
            compute.SetFloat("_CollisionFriction", c.collisionFriction);
            compute.SetFloat("_MaxVelocity", c.maxVelocity);
            compute.SetFloat("_MaxAcceleration", c.maxAcceleration);
            compute.SetFloat("_CoolingRate", c.coolingRate);
            compute.SetFloat("_GroundCoolingRate", c.groundCoolingRate);
            compute.SetFloat("_WorldWidth", terrain.WorldWidth);
            compute.SetFloat("_WorldHeight", terrain.WorldHeight + c.extraWorldHeight);
            compute.SetFloat("_WorldDepth", terrain.WorldDepth);
            compute.SetFloat("_SemiSolidTemperature", c.useViscoPlasticEjecta ? c.semiSolidTemperature : -10000f);
            compute.SetFloat("_SemiSolidViscosityMultiplier", c.semiSolidViscosityMultiplier);
            compute.SetFloat("_SemiSolidVelocityDamping", c.semiSolidVelocityDamping);
            compute.SetFloat("_SemiSolidGravityMultiplier", c.semiSolidGravityMultiplier);
            compute.SetFloat("_GroundTangentialDamping", c.groundTangentialDamping);
            compute.SetFloat("_GroundNormalDamping", c.groundNormalDamping);
            compute.SetFloat("_GroundContactCoolingBoost", c.groundContactCoolingBoost);
        }
    }
}
