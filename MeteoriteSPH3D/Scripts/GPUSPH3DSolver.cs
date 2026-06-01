using System.Collections.Generic;
using System.Runtime.InteropServices;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;

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

        [StructLayout(LayoutKind.Sequential)]
        private struct DepositCandidate
        {
            public Vector4 positionAge;
            public Vector4 velocityTemperature;
            public Vector4 contactMassIndexFlags;
        }

        public ComputeBuffer ParticleBuffer { get { return particleBuffer; } }
        public int ActiveCount { get; private set; }
        public bool IsReady { get { return compute != null && particleBuffer != null && terrainSolidBuffer != null; } }
        public bool SupportsAsyncReadback { get { return SystemInfo.supportsAsyncGPUReadback; } }
        public bool IsReadbackPending { get { return readbackPending; } }
        public bool IsDepositCandidateReadbackPending { get { return depositCandidateReadbackPending; } }
        public int EstimatedInactiveCount { get { return Mathf.Clamp(estimatedInactiveCount, 0, ActiveCount); } }
        public float EstimatedInactiveRatio { get { return ActiveCount > 0 ? Mathf.Clamp01((float)EstimatedInactiveCount / (float)ActiveCount) : 0f; } }

        private ComputeShader compute;
        private ComputeBuffer particleBuffer;
        private ComputeBuffer terrainSolidBuffer;
        private ComputeBuffer cellCountsBuffer;
        private ComputeBuffer cellItemsBuffer;
        private ComputeBuffer deactivateIndexBuffer;
        private ComputeBuffer depositCandidateBuffer;
        private ComputeBuffer depositCandidateCounterBuffer;

        private GPUParticle[] particleCache;
        private DepositCandidate[] depositCandidateCache;
        private int[] terrainSolidCache;
        private int[] deactivateIndexCache;

        private bool readbackPending;
        private AsyncGPUReadbackRequest readbackRequest;
        private int readbackCount;
        private int readbackUploadVersion;
        private int estimatedInactiveCount;
        private bool depositCandidateReadbackPending;
        private AsyncGPUReadbackRequest depositCandidateReadbackRequest;
        private int depositCandidateReadbackUploadVersion;
        private int uploadVersion;

        private int maxParticles;
        private int maxPerCell;
        private int maxDepositCandidates;
        private int gridX;
        private int gridY;
        private int gridZ;
        private int gridCellCount;

        private int kClear;
        private int kBuild;
        private int kDensity;
        private int kIntegrate;
        private int kDeactivate;
        private int kClearDepositCandidates;
        private int kCollectDepositCandidates;

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
            maxDepositCandidates = Mathf.Clamp(c.gpuDepositCandidateCapacity, 256, maxParticles);

            float h = Mathf.Max(0.05f, c.smoothingRadius);
            gridX = Mathf.Max(1, Mathf.CeilToInt(terrain.WorldWidth / h) + 2);
            gridY = Mathf.Max(1, Mathf.CeilToInt((terrain.WorldHeight + c.extraWorldHeight) / h) + 2);
            gridZ = Mathf.Max(1, Mathf.CeilToInt(terrain.WorldDepth / h) + 2);
            gridCellCount = gridX * gridY * gridZ;

            particleCache = new GPUParticle[maxParticles];
            depositCandidateCache = new DepositCandidate[maxDepositCandidates];
            particleBuffer = new ComputeBuffer(maxParticles, Marshal.SizeOf(typeof(GPUParticle)), ComputeBufferType.Structured);
            terrainSolidBuffer = new ComputeBuffer(terrain.Width * terrain.Height * terrain.Depth, sizeof(int), ComputeBufferType.Structured);
            cellCountsBuffer = new ComputeBuffer(gridCellCount, sizeof(int), ComputeBufferType.Structured);
            cellItemsBuffer = new ComputeBuffer(gridCellCount * maxPerCell, sizeof(int), ComputeBufferType.Structured);
            deactivateIndexBuffer = new ComputeBuffer(maxParticles, sizeof(int), ComputeBufferType.Structured);
            depositCandidateBuffer = new ComputeBuffer(maxDepositCandidates, Marshal.SizeOf(typeof(DepositCandidate)), ComputeBufferType.Structured);
            depositCandidateCounterBuffer = new ComputeBuffer(1, sizeof(int), ComputeBufferType.Structured);
            deactivateIndexCache = new int[maxParticles];

            kClear = compute.FindKernel("ClearGrid");
            kBuild = compute.FindKernel("BuildGrid");
            kDensity = compute.FindKernel("DensityPressure");
            kIntegrate = compute.FindKernel("Integrate");
            kDeactivate = compute.FindKernel("DeactivateParticles");
            kClearDepositCandidates = compute.FindKernel("ClearDepositCandidates");
            kCollectDepositCandidates = compute.FindKernel("CollectDepositCandidates");

            BindBuffers(kClear);
            BindBuffers(kBuild);
            BindBuffers(kDensity);
            BindBuffers(kIntegrate);
            BindBuffers(kDeactivate);
            BindBuffers(kClearDepositCandidates);
            BindBuffers(kCollectDepositCandidates);
            compute.SetBuffer(kDeactivate, "_DeactivateIndices", deactivateIndexBuffer);
            compute.SetBuffer(kClearDepositCandidates, "_DepositCandidates", depositCandidateBuffer);
            compute.SetBuffer(kClearDepositCandidates, "_DepositCandidateCounter", depositCandidateCounterBuffer);
            compute.SetBuffer(kCollectDepositCandidates, "_DepositCandidates", depositCandidateBuffer);
            compute.SetBuffer(kCollectDepositCandidates, "_DepositCandidateCounter", depositCandidateCounterBuffer);

            compute.SetInts("_GridSize", gridX, gridY, gridZ);
            compute.SetInt("_GridCellCount", gridCellCount);
            compute.SetInt("_MaxParticlesPerCell", maxPerCell);
            compute.SetInt("_DepositCandidateCapacity", maxDepositCandidates);
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
            if (deactivateIndexBuffer != null) deactivateIndexBuffer.Release();
            if (depositCandidateBuffer != null) depositCandidateBuffer.Release();
            if (depositCandidateCounterBuffer != null) depositCandidateCounterBuffer.Release();
            particleBuffer = null;
            terrainSolidBuffer = null;
            cellCountsBuffer = null;
            cellItemsBuffer = null;
            deactivateIndexBuffer = null;
            depositCandidateBuffer = null;
            depositCandidateCounterBuffer = null;
            compute = null;
            particleCache = null;
            depositCandidateCache = null;
            terrainSolidCache = null;
            deactivateIndexCache = null;
            readbackPending = false;
            depositCandidateReadbackPending = false;
            readbackCount = 0;
            readbackUploadVersion = 0;
            depositCandidateReadbackUploadVersion = 0;
            uploadVersion++;
            estimatedInactiveCount = 0;
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
                    p.gpuIndex = ActiveCount;
                    particleCache[ActiveCount++] = g;
                }

                if (particles.Count > ActiveCount)
                {
                    particles.RemoveRange(ActiveCount, particles.Count - ActiveCount);
                }
            }

            particleBuffer.SetData(particleCache);
            readbackPending = false;
            depositCandidateReadbackPending = false;
            uploadVersion++;
            estimatedInactiveCount = 0;
        }

        public bool RequestParticleReadback()
        {
            if (!SupportsAsyncReadback || particleBuffer == null || ActiveCount <= 0 || readbackPending) return false;
            int stride = Marshal.SizeOf(typeof(GPUParticle));
            readbackCount = Mathf.Clamp(ActiveCount, 0, maxParticles);
            readbackUploadVersion = uploadVersion;
            readbackRequest = AsyncGPUReadback.Request(particleBuffer, readbackCount * stride, 0);
            readbackPending = true;
            return true;
        }

        public bool TryConsumeParticleReadback(List<SPHParticle3D> particles)
        {
            if (!readbackPending) return false;
            if (!readbackRequest.done) return false;

            readbackPending = false;
            if (particles == null) return false;

            if (readbackRequest.hasError || readbackUploadVersion != uploadVersion)
            {
                return false;
            }

            NativeArray<GPUParticle> data = readbackRequest.GetData<GPUParticle>();
            particles.Clear();
            int count = Mathf.Min(readbackCount, data.Length);
            for (int i = 0; i < count; i++)
            {
                GPUParticle g = data[i];
                if (g.active < 0.5f) continue;
                SPHParticle3D p = new SPHParticle3D(g.position, g.velocity, g.temperature, g.mass);
                p.age = g.age;
                p.density = g.density;
                p.pressure = g.pressure;
                p.recentGroundContact = g.recentGroundContact;
                p.gpuIndex = i;
                particles.Add(p);
            }

            // Keep ActiveCount equal to the GPU buffer prefix length. Async deactivation can leave inactive holes;
            // shrinking ActiveCount here would make later deactivation indices point at wrong slots.
            ActiveCount = Mathf.Min(maxParticles, readbackCount);
            return true;
        }

        public void DownloadToParticles(List<SPHParticle3D> particles)
        {
            if (particleBuffer == null || particleCache == null || particles == null) return;
            if (ActiveCount <= 0)
            {
                particles.Clear();
                readbackPending = false;
                depositCandidateReadbackPending = false;
                return;
            }

            readbackPending = false;
            depositCandidateReadbackPending = false;
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
                p.gpuIndex = i;
                particles.Add(p);
            }
            ActiveCount = Mathf.Min(maxParticles, particles.Count);
        }


        private void DispatchDepositCandidateCollection(MeteoriteSPH3DController c)
        {
            if (!IsReady || depositCandidateBuffer == null || depositCandidateCounterBuffer == null || ActiveCount <= 0) return;

            float maxTempBonus = Mathf.Max(0f, c.centerCaptureTemperatureBonus, c.rimCaptureTemperatureBonus, c.forceDepositTemperatureBonus);
            float maxDepositTemperature = c.solidifyTemperature + maxTempBonus;
            float maxDepositSpeed = Mathf.Max(c.solidifySpeed, c.centerCaptureMaxSpeed, c.rimCaptureMaxSpeed, c.forceDepositMaxSpeed * 2.5f);
            float forcedAge = c.forceDepositOldParticles ? c.forceDepositAge : 999999f;

            compute.SetInt("_ActiveCount", ActiveCount);
            compute.SetInt("_DepositCandidateCapacity", maxDepositCandidates);
            compute.SetFloat("_DepositMinAge", Mathf.Min(c.solidifyMinAge, c.centerCaptureMinAge, c.rimCaptureMinAge, forcedAge));
            compute.SetFloat("_DepositMaxTemperature", maxDepositTemperature);
            compute.SetFloat("_DepositMaxSpeed", Mathf.Max(0.01f, maxDepositSpeed));
            compute.SetFloat("_DepositForcedAge", forcedAge);

            int groupsCandidates = Mathf.CeilToInt(maxDepositCandidates / 128f);
            compute.Dispatch(kClearDepositCandidates, groupsCandidates, 1, 1);

            int groupsParticles = Mathf.CeilToInt(ActiveCount / 128f);
            compute.Dispatch(kCollectDepositCandidates, groupsParticles, 1, 1);
        }

        public bool RequestDepositCandidateReadback(MeteoriteSPH3DController c)
        {
            if (!SupportsAsyncReadback || !IsReady || ActiveCount <= 0 || depositCandidateReadbackPending) return false;
            DispatchDepositCandidateCollection(c);
            depositCandidateReadbackUploadVersion = uploadVersion;
            depositCandidateReadbackRequest = AsyncGPUReadback.Request(depositCandidateBuffer);
            depositCandidateReadbackPending = true;
            return true;
        }

        public bool TryConsumeDepositCandidateReadback(List<SPHParticle3D> particles)
        {
            if (!depositCandidateReadbackPending) return false;
            if (!depositCandidateReadbackRequest.done) return false;

            depositCandidateReadbackPending = false;
            if (particles == null) return false;
            particles.Clear();

            if (depositCandidateReadbackRequest.hasError || depositCandidateReadbackUploadVersion != uploadVersion)
            {
                return false;
            }

            NativeArray<DepositCandidate> data = depositCandidateReadbackRequest.GetData<DepositCandidate>();
            int count = Mathf.Min(maxDepositCandidates, data.Length);
            for (int i = 0; i < count; i++)
            {
                DepositCandidate c = data[i];
                if (c.contactMassIndexFlags.w < 0.5f) continue;

                int gpuIndex = Mathf.RoundToInt(c.contactMassIndexFlags.z);
                if (gpuIndex < 0 || gpuIndex >= ActiveCount) continue;

                Vector3 position = new Vector3(c.positionAge.x, c.positionAge.y, c.positionAge.z);
                Vector3 velocity = new Vector3(c.velocityTemperature.x, c.velocityTemperature.y, c.velocityTemperature.z);
                SPHParticle3D p = new SPHParticle3D(position, velocity, c.velocityTemperature.w, c.contactMassIndexFlags.y);
                p.age = c.positionAge.w;
                p.recentGroundContact = c.contactMassIndexFlags.x;
                p.gpuIndex = gpuIndex;
                particles.Add(p);
            }

            return true;
        }

        public void DownloadDepositCandidates(MeteoriteSPH3DController c, List<SPHParticle3D> particles)
        {
            if (!IsReady || depositCandidateBuffer == null || depositCandidateCache == null || particles == null) return;
            depositCandidateReadbackPending = false;
            DispatchDepositCandidateCollection(c);
            depositCandidateBuffer.GetData(depositCandidateCache, 0, 0, maxDepositCandidates);

            particles.Clear();
            for (int i = 0; i < maxDepositCandidates; i++)
            {
                DepositCandidate cnd = depositCandidateCache[i];
                if (cnd.contactMassIndexFlags.w < 0.5f) continue;

                int gpuIndex = Mathf.RoundToInt(cnd.contactMassIndexFlags.z);
                if (gpuIndex < 0 || gpuIndex >= ActiveCount) continue;

                Vector3 position = new Vector3(cnd.positionAge.x, cnd.positionAge.y, cnd.positionAge.z);
                Vector3 velocity = new Vector3(cnd.velocityTemperature.x, cnd.velocityTemperature.y, cnd.velocityTemperature.z);
                SPHParticle3D p = new SPHParticle3D(position, velocity, cnd.velocityTemperature.w, cnd.contactMassIndexFlags.y);
                p.age = cnd.positionAge.w;
                p.recentGroundContact = cnd.contactMassIndexFlags.x;
                p.gpuIndex = gpuIndex;
                particles.Add(p);
            }
        }


        public void CompactActiveParticles(List<SPHParticle3D> particles)
        {
            if (particleBuffer == null || particleCache == null || particles == null) return;
            if (ActiveCount <= 0)
            {
                particles.Clear();
                estimatedInactiveCount = 0;
                readbackPending = false;
                depositCandidateReadbackPending = false;
                return;
            }

            readbackPending = false;
            depositCandidateReadbackPending = false;
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
                p.gpuIndex = particles.Count;
                particles.Add(p);
            }

            UploadFromParticles(particles);
        }

        public void DeactivateParticles(List<int> gpuIndices)
        {
            if (!IsReady || deactivateIndexBuffer == null || deactivateIndexCache == null || gpuIndices == null || gpuIndices.Count == 0) return;

            int count = 0;
            for (int i = 0; i < gpuIndices.Count && count < maxParticles; i++)
            {
                int idx = gpuIndices[i];
                if (idx < 0 || idx >= ActiveCount) continue;
                deactivateIndexCache[count++] = idx;
            }

            if (count <= 0) return;

            deactivateIndexBuffer.SetData(deactivateIndexCache, 0, 0, count);
            compute.SetInt("_DeactivateCount", count);
            int groups = Mathf.CeilToInt(count / 128f);
            compute.Dispatch(kDeactivate, groups, 1, 1);
            estimatedInactiveCount = Mathf.Min(ActiveCount, estimatedInactiveCount + count);
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
            int desiredMaxDepositCandidates = Mathf.Clamp(c.gpuDepositCandidateCapacity, 256, desiredMaxParticles);
            return nx != gridX || ny != gridY || nz != gridZ || desiredMaxParticles != maxParticles || desiredMaxPerCell != maxPerCell || desiredMaxDepositCandidates != maxDepositCandidates;
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
            compute.SetFloat("_XsphVelocityBlend", c.enableXsphVelocityBlend ? c.xsphVelocityBlend : 0f);
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
