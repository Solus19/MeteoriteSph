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

        [StructLayout(LayoutKind.Sequential)]
        private struct GpuDepositResult
        {
            public int x;
            public int y;
            public int z;
            public int particleIndex;
            public Vector4 temperatureFlags;
        }

        public struct DepositedVoxel
        {
            public int x;
            public int y;
            public int z;
            public float temperature;
            public int particleIndex;
        }

        public ComputeBuffer ParticleBuffer { get { return particleBuffer; } }
        public int ActiveCount { get; private set; }
        public bool IsReady { get { return compute != null && particleBuffer != null && terrainSolidBuffer != null && terrainTopSolidYBuffer != null; } }
        public bool SupportsAsyncReadback { get { return SystemInfo.supportsAsyncGPUReadback; } }
        public bool IsReadbackPending { get { return readbackPending; } }
        public bool IsDepositCandidateReadbackPending { get { return depositCandidateReadbackPending; } }
        public bool IsGpuDepositReadbackPending { get { return gpuDepositReadbackPending; } }
        public int EstimatedInactiveCount { get { return Mathf.Clamp(estimatedInactiveCount, 0, ActiveCount); } }
        public int LiveActiveEstimate { get { return Mathf.Max(0, ActiveCount - EstimatedInactiveCount); } }
        public float EstimatedInactiveRatio { get { return ActiveCount > 0 ? Mathf.Clamp01((float)EstimatedInactiveCount / (float)ActiveCount) : 0f; } }

        private ComputeShader compute;
        private ComputeBuffer particleBuffer;
        private ComputeBuffer terrainSolidBuffer;
        private ComputeBuffer terrainTopSolidYBuffer;
        private ComputeBuffer cellCountsBuffer;
        private ComputeBuffer cellItemsBuffer;
        private ComputeBuffer deactivateIndexBuffer;
        private ComputeBuffer depositCandidateBuffer;
        private ComputeBuffer depositCandidateCounterBuffer;
        private ComputeBuffer gpuDepositResultBuffer;
        private ComputeBuffer gpuDepositResultCounterBuffer;
        private ComputeBuffer gpuDepositColumnClaimsBuffer;

        private GPUParticle[] particleCache;
        private DepositCandidate[] depositCandidateCache;
        private GpuDepositResult[] gpuDepositResultCache;
        private int[] terrainSolidCache;
        private int[] terrainTopSolidCache;
        private int[] terrainDirtyColumnUploadIndices;
        private int[] terrainDirtyUploadIndices;
        private readonly List<int> terrainDirtyScratch = new List<int>(4096);
        private int[] deactivateIndexCache;

        private bool readbackPending;
        private AsyncGPUReadbackRequest readbackRequest;
        private int readbackCount;
        private int readbackUploadVersion;
        private int estimatedInactiveCount;
        private bool depositCandidateReadbackPending;
        private AsyncGPUReadbackRequest depositCandidateReadbackRequest;
        private int depositCandidateReadbackUploadVersion;
        private bool gpuDepositReadbackPending;
        private AsyncGPUReadbackRequest gpuDepositReadbackRequest;
        private int gpuDepositReadbackUploadVersion;
        private int uploadVersion;

        private int maxParticles;
        private int maxPerCell;
        private int maxDepositCandidates;
        private int gridX;
        private int gridY;
        private int gridZ;
        private int gridCellCount;
        private int terrainColumnCount;
        private int gpuDepositClaimStamp;

        private int kClear;
        private int kBuild;
        private int kDensity;
        private int kIntegrate;
        private int kDeactivate;
        private int kClearDepositCandidates;
        private int kCollectDepositCandidates;
        private int kClearGpuDeposits;
        private int kApplyGpuDeposits;

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
            terrainColumnCount = Mathf.Max(1, terrain.Width * terrain.Depth);

            particleCache = new GPUParticle[maxParticles];
            depositCandidateCache = new DepositCandidate[maxDepositCandidates];
            gpuDepositResultCache = new GpuDepositResult[maxDepositCandidates];
            particleBuffer = new ComputeBuffer(maxParticles, Marshal.SizeOf(typeof(GPUParticle)), ComputeBufferType.Structured);
            terrainSolidBuffer = new ComputeBuffer(terrain.Width * terrain.Height * terrain.Depth, sizeof(int), ComputeBufferType.Structured);
            terrainTopSolidYBuffer = new ComputeBuffer(terrain.Width * terrain.Depth, sizeof(int), ComputeBufferType.Structured);
            cellCountsBuffer = new ComputeBuffer(gridCellCount, sizeof(int), ComputeBufferType.Structured);
            cellItemsBuffer = new ComputeBuffer(gridCellCount * maxPerCell, sizeof(int), ComputeBufferType.Structured);
            deactivateIndexBuffer = new ComputeBuffer(maxParticles, sizeof(int), ComputeBufferType.Structured);
            depositCandidateBuffer = new ComputeBuffer(maxDepositCandidates, Marshal.SizeOf(typeof(DepositCandidate)), ComputeBufferType.Structured);
            depositCandidateCounterBuffer = new ComputeBuffer(1, sizeof(int), ComputeBufferType.Structured);
            gpuDepositResultBuffer = new ComputeBuffer(maxDepositCandidates, Marshal.SizeOf(typeof(GpuDepositResult)), ComputeBufferType.Structured);
            gpuDepositResultCounterBuffer = new ComputeBuffer(1, sizeof(int), ComputeBufferType.Structured);
            gpuDepositColumnClaimsBuffer = new ComputeBuffer(terrainColumnCount, sizeof(int), ComputeBufferType.Structured);
            gpuDepositColumnClaimsBuffer.SetData(new int[terrainColumnCount]);
            gpuDepositClaimStamp = 1;
            deactivateIndexCache = new int[maxParticles];

            kClear = compute.FindKernel("ClearGrid");
            kBuild = compute.FindKernel("BuildGrid");
            kDensity = compute.FindKernel("DensityPressure");
            kIntegrate = compute.FindKernel("Integrate");
            kDeactivate = compute.FindKernel("DeactivateParticles");
            kClearDepositCandidates = compute.FindKernel("ClearDepositCandidates");
            kCollectDepositCandidates = compute.FindKernel("CollectDepositCandidates");
            kClearGpuDeposits = compute.FindKernel("ClearGpuDeposits");
            kApplyGpuDeposits = compute.FindKernel("ApplyGpuDeposits");

            BindBuffers(kClear);
            BindBuffers(kBuild);
            BindBuffers(kDensity);
            BindBuffers(kIntegrate);
            BindBuffers(kDeactivate);
            BindBuffers(kClearDepositCandidates);
            BindBuffers(kCollectDepositCandidates);
            BindBuffers(kClearGpuDeposits);
            BindBuffers(kApplyGpuDeposits);
            compute.SetBuffer(kDeactivate, "_DeactivateIndices", deactivateIndexBuffer);
            compute.SetBuffer(kClearDepositCandidates, "_DepositCandidates", depositCandidateBuffer);
            compute.SetBuffer(kClearDepositCandidates, "_DepositCandidateCounter", depositCandidateCounterBuffer);
            compute.SetBuffer(kCollectDepositCandidates, "_DepositCandidates", depositCandidateBuffer);
            compute.SetBuffer(kCollectDepositCandidates, "_DepositCandidateCounter", depositCandidateCounterBuffer);
            compute.SetBuffer(kClearGpuDeposits, "_GpuDepositResults", gpuDepositResultBuffer);
            compute.SetBuffer(kClearGpuDeposits, "_GpuDepositResultCounter", gpuDepositResultCounterBuffer);
            compute.SetBuffer(kClearGpuDeposits, "_GpuDepositColumnClaims", gpuDepositColumnClaimsBuffer);
            compute.SetBuffer(kApplyGpuDeposits, "_GpuDepositResults", gpuDepositResultBuffer);
            compute.SetBuffer(kApplyGpuDeposits, "_GpuDepositResultCounter", gpuDepositResultCounterBuffer);
            compute.SetBuffer(kApplyGpuDeposits, "_GpuDepositColumnClaims", gpuDepositColumnClaimsBuffer);

            compute.SetInts("_GridSize", gridX, gridY, gridZ);
            compute.SetInt("_GridCellCount", gridCellCount);
            compute.SetInt("_MaxParticlesPerCell", maxPerCell);
            compute.SetInt("_DepositCandidateCapacity", maxDepositCandidates);
            compute.SetInt("_GpuDepositTerrainColumnCount", terrainColumnCount);
            compute.SetInts("_TerrainSize", terrain.Width, terrain.Height, terrain.Depth);
            compute.SetFloat("_TerrainCellSize", terrain.CellSize);

            UploadTerrain(terrain);
            UploadFromParticles(null);
        }

        public void Release()
        {
            if (particleBuffer != null) particleBuffer.Release();
            if (terrainSolidBuffer != null) terrainSolidBuffer.Release();
            if (terrainTopSolidYBuffer != null) terrainTopSolidYBuffer.Release();
            if (cellCountsBuffer != null) cellCountsBuffer.Release();
            if (cellItemsBuffer != null) cellItemsBuffer.Release();
            if (deactivateIndexBuffer != null) deactivateIndexBuffer.Release();
            if (depositCandidateBuffer != null) depositCandidateBuffer.Release();
            if (depositCandidateCounterBuffer != null) depositCandidateCounterBuffer.Release();
            if (gpuDepositResultBuffer != null) gpuDepositResultBuffer.Release();
            if (gpuDepositResultCounterBuffer != null) gpuDepositResultCounterBuffer.Release();
            if (gpuDepositColumnClaimsBuffer != null) gpuDepositColumnClaimsBuffer.Release();
            particleBuffer = null;
            terrainSolidBuffer = null;
            terrainTopSolidYBuffer = null;
            cellCountsBuffer = null;
            cellItemsBuffer = null;
            deactivateIndexBuffer = null;
            depositCandidateBuffer = null;
            depositCandidateCounterBuffer = null;
            gpuDepositResultBuffer = null;
            gpuDepositResultCounterBuffer = null;
            gpuDepositColumnClaimsBuffer = null;
            compute = null;
            particleCache = null;
            depositCandidateCache = null;
            gpuDepositResultCache = null;
            terrainSolidCache = null;
            terrainTopSolidCache = null;
            terrainDirtyColumnUploadIndices = null;
            terrainDirtyUploadIndices = null;
            terrainDirtyScratch.Clear();
            deactivateIndexCache = null;
            readbackPending = false;
            depositCandidateReadbackPending = false;
            gpuDepositReadbackPending = false;
            readbackCount = 0;
            readbackUploadVersion = 0;
            depositCandidateReadbackUploadVersion = 0;
            gpuDepositReadbackUploadVersion = 0;
            uploadVersion++;
            estimatedInactiveCount = 0;
            ActiveCount = 0;
            terrainColumnCount = 0;
            gpuDepositClaimStamp = 0;
        }

        private void BindBuffers(int kernel)
        {
            compute.SetBuffer(kernel, "_Particles", particleBuffer);
            compute.SetBuffer(kernel, "_TerrainSolid", terrainSolidBuffer);
            compute.SetBuffer(kernel, "_TerrainTopSolidY", terrainTopSolidYBuffer);
            compute.SetBuffer(kernel, "_CellCounts", cellCountsBuffer);
            compute.SetBuffer(kernel, "_CellItems", cellItemsBuffer);
        }

        public void UploadTerrain(VoxelTerrain3D terrain)
        {
            if (terrainSolidBuffer == null || terrain == null) return;
            int n = terrain.Width * terrain.Height * terrain.Depth;
            EnsureTerrainUploadCache(n);

            int i = 0;
            // Keep this order identical to TerrainIndex() in the compute shader:
            // x + y * width + z * width * height.
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

            EnsureTerrainTopSolidUploadCache(terrain.Width * terrain.Depth);
            for (int z = 0; z < terrain.Depth; z++)
            {
                for (int x = 0; x < terrain.Width; x++)
                {
                    terrainTopSolidCache[x + z * terrain.Width] = terrain.TopSolidY(x, z);
                }
            }
            if (terrainTopSolidYBuffer != null) terrainTopSolidYBuffer.SetData(terrainTopSolidCache);

            terrain.ClearGpuDirtyVoxels();
        }

        public void UploadDirtyTerrain(VoxelTerrain3D terrain)
        {
            if (terrainSolidBuffer == null || terrain == null) return;

            int n = terrain.Width * terrain.Height * terrain.Depth;
            EnsureTerrainUploadCache(n);

            bool fullUploadRequired;
            bool hasDirty = terrain.ConsumeGpuDirtyVoxelIndices(terrainDirtyScratch, out fullUploadRequired);
            if (fullUploadRequired || terrainSolidCache == null)
            {
                UploadTerrain(terrain);
                return;
            }

            if (!hasDirty || terrainDirtyScratch.Count == 0) return;

            // If too much of the terrain changed, one full upload is cheaper than thousands of tiny SetData calls.
            if (terrainDirtyScratch.Count > Mathf.Max(4096, n / 6))
            {
                UploadTerrain(terrain);
                return;
            }

            if (terrainDirtyUploadIndices == null || terrainDirtyUploadIndices.Length < terrainDirtyScratch.Count)
                terrainDirtyUploadIndices = new int[Mathf.NextPowerOfTwo(terrainDirtyScratch.Count)];
            if (terrainDirtyColumnUploadIndices == null || terrainDirtyColumnUploadIndices.Length < terrainDirtyScratch.Count)
                terrainDirtyColumnUploadIndices = new int[Mathf.NextPowerOfTwo(terrainDirtyScratch.Count)];

            EnsureTerrainTopSolidUploadCache(terrain.Width * terrain.Depth);

            int count = 0;
            int columnCount = 0;
            for (int i = 0; i < terrainDirtyScratch.Count; i++)
            {
                int terrainIndex = terrainDirtyScratch[i];
                int x;
                int y;
                int z;
                terrain.UnpackIndex(terrainIndex, out x, out y, out z);
                int computeIndex = TerrainComputeIndex(terrain, x, y, z);
                if (computeIndex < 0 || computeIndex >= n) continue;

                terrainSolidCache[computeIndex] = terrain.IsSolid(x, y, z) ? 1 : 0;
                terrainDirtyUploadIndices[count++] = computeIndex;

                int column = x + z * terrain.Width;
                terrainTopSolidCache[column] = terrain.TopSolidY(x, z);
                terrainDirtyColumnUploadIndices[columnCount++] = column;
            }

            terrainDirtyScratch.Clear();

            if (count > 0)
            {
                System.Array.Sort(terrainDirtyUploadIndices, 0, count);

                int runStart = terrainDirtyUploadIndices[0];
                int runEnd = runStart;
                for (int i = 1; i < count; i++)
                {
                    int index = terrainDirtyUploadIndices[i];
                    if (index <= runEnd) continue; // ignore accidental duplicates
                    // Merge short gaps too: uploading a few unchanged ints is cheaper than many tiny GPU SetData calls.
                    if (index <= runEnd + 32)
                    {
                        runEnd = index;
                        continue;
                    }

                    UploadTerrainRun(runStart, runEnd);
                    runStart = runEnd = index;
                }
                UploadTerrainRun(runStart, runEnd);
            }

            if (columnCount > 0)
            {
                System.Array.Sort(terrainDirtyColumnUploadIndices, 0, columnCount);
                int runStart = terrainDirtyColumnUploadIndices[0];
                int runEnd = runStart;
                for (int i = 1; i < columnCount; i++)
                {
                    int index = terrainDirtyColumnUploadIndices[i];
                    if (index <= runEnd) continue;
                    if (index <= runEnd + 16)
                    {
                        runEnd = index;
                        continue;
                    }
                    UploadTerrainTopSolidRun(runStart, runEnd);
                    runStart = runEnd = index;
                }
                UploadTerrainTopSolidRun(runStart, runEnd);
            }
        }

        private void EnsureTerrainUploadCache(int n)
        {
            if (terrainSolidCache == null || terrainSolidCache.Length != n) terrainSolidCache = new int[n];
        }

        private void EnsureTerrainTopSolidUploadCache(int n)
        {
            if (terrainTopSolidCache == null || terrainTopSolidCache.Length != n) terrainTopSolidCache = new int[n];
        }

        private static int TerrainComputeIndex(VoxelTerrain3D terrain, int x, int y, int z)
        {
            return x + y * terrain.Width + z * terrain.Width * terrain.Height;
        }

        private void UploadTerrainRun(int runStart, int runEnd)
        {
            int count = runEnd - runStart + 1;
            if (count <= 0) return;
            terrainSolidBuffer.SetData(terrainSolidCache, runStart, runStart, count);
        }

        private void UploadTerrainTopSolidRun(int runStart, int runEnd)
        {
            if (terrainTopSolidYBuffer == null || terrainTopSolidCache == null) return;
            int count = runEnd - runStart + 1;
            if (count <= 0) return;
            terrainTopSolidYBuffer.SetData(terrainTopSolidCache, runStart, runStart, count);
        }


        public void DownloadTerrainTo(VoxelTerrain3D terrain, bool markNewSolidsAsDeposited)
        {
            if (terrain == null || terrainSolidBuffer == null || terrainTopSolidYBuffer == null) return;

            int solidCount = terrain.Width * terrain.Height * terrain.Depth;
            int topCount = terrain.Width * terrain.Depth;
            EnsureTerrainUploadCache(solidCount);
            EnsureTerrainTopSolidUploadCache(topCount);

            readbackPending = false;
            depositCandidateReadbackPending = false;
            gpuDepositReadbackPending = false;

            terrainSolidBuffer.GetData(terrainSolidCache, 0, 0, solidCount);
            terrainTopSolidYBuffer.GetData(terrainTopSolidCache, 0, 0, topCount);
            terrain.ReplaceSolidStateFromGpuBuffers(terrainSolidCache, terrainTopSolidCache, markNewSolidsAsDeposited);
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
            gpuDepositReadbackPending = false;
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
                gpuDepositReadbackPending = false;
                return;
            }

            readbackPending = false;
            depositCandidateReadbackPending = false;
            gpuDepositReadbackPending = false;
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

            bool tailDepositMode = c.IsTailDepositModeActive();
            float maxTempBonus = Mathf.Max(0f, c.centerCaptureTemperatureBonus, c.rimCaptureTemperatureBonus, c.forceDepositTemperatureBonus);
            float maxDepositTemperature = tailDepositMode ? 1000000f : c.solidifyTemperature + maxTempBonus;
            float maxDepositSpeed = tailDepositMode ? 1000000f : Mathf.Max(c.solidifySpeed, c.centerCaptureMaxSpeed, c.rimCaptureMaxSpeed, c.forceDepositMaxSpeed * 2.5f);
            float forcedAge = tailDepositMode ? 0f : (c.forceDepositOldParticles ? c.forceDepositAge : 999999f);
            float minAge = tailDepositMode ? 0f : Mathf.Min(c.solidifyMinAge, c.centerCaptureMinAge, c.rimCaptureMinAge, forcedAge);

            compute.SetInt("_ActiveCount", ActiveCount);
            compute.SetInt("_DepositCandidateCapacity", maxDepositCandidates);
            compute.SetFloat("_DepositMinAge", minAge);
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

        private void DispatchGpuDeposition(MeteoriteSPH3DController c)
        {
            if (!IsReady || gpuDepositResultBuffer == null || gpuDepositResultCounterBuffer == null || ActiveCount <= 0) return;

            bool tailDepositMode = c.IsTailDepositModeActive();
            float maxTempBonus = Mathf.Max(0f, c.centerCaptureTemperatureBonus, c.rimCaptureTemperatureBonus, c.forceDepositTemperatureBonus);
            float maxDepositTemperature = tailDepositMode ? 1000000f : c.solidifyTemperature + maxTempBonus;
            float maxDepositSpeed = tailDepositMode ? 1000000f : Mathf.Max(c.solidifySpeed, c.centerCaptureMaxSpeed, c.rimCaptureMaxSpeed, c.forceDepositMaxSpeed * 2.5f);
            float forcedAge = tailDepositMode ? 0f : (c.forceDepositOldParticles ? c.forceDepositAge : 999999f);
            float minAge = tailDepositMode ? 0f : Mathf.Min(c.solidifyMinAge, c.centerCaptureMinAge, c.rimCaptureMinAge, forcedAge);

            int searchRadius = tailDepositMode
                ? Mathf.Max(c.depositSearchRadiusCells, c.forcedDepositSearchRadiusCells)
                : c.depositSearchRadiusCells;
            int riseLimit = tailDepositMode
                ? Mathf.Max(c.maxDepositRiseAboveNeighbours, 2)
                : Mathf.Max(0, c.maxDepositRiseAboveNeighbours);

            compute.SetInt("_ActiveCount", ActiveCount);
            compute.SetInt("_GpuDepositResultCapacity", maxDepositCandidates);
            compute.SetInt("_GpuDepositSearchRadiusCells", Mathf.Clamp(searchRadius, 1, 12));
            compute.SetInt("_GpuDepositMaxRiseAboveNeighbours", Mathf.Clamp(riseLimit, 0, 16));
            compute.SetInt("_GpuDepositTailMode", tailDepositMode ? 1 : 0);
            compute.SetFloat("_GpuDepositMinAge", minAge);
            compute.SetFloat("_GpuDepositMaxTemperature", maxDepositTemperature);
            compute.SetFloat("_GpuDepositMaxSpeed", Mathf.Max(0.01f, maxDepositSpeed));
            compute.SetFloat("_GpuDepositForcedAge", forcedAge);

            Vector3 impactCenter = c.HasActiveImpact ? c.LastImpactCenter : Vector3.zero;
            float innerSmoothRadius = (c.HasActiveImpact && c.smoothInnerCraterDeposits)
                ? Mathf.Max(0f, c.LastImpactRadius * Mathf.Clamp(c.innerCraterSmoothRadiusFactor, 0.10f, 0.95f))
                : 0f;
            compute.SetVector("_GpuDepositImpactCenter", new Vector4(impactCenter.x, impactCenter.y, impactCenter.z, 0f));
            // No hard center ban. The center is allowed to receive material; the shader only
            // rejects/penalizes locally prominent placements so material spreads instead of
            // growing vertical pillars.
            compute.SetFloat("_GpuDepositInnerBlockRadius", 0f);
            compute.SetInt("_GpuDepositDiscardInnerTail", 0);
            compute.SetFloat("_GpuDepositInnerDiscardMinAge", Mathf.Max(0f, c.innerCraterDiscardMinAge));
            compute.SetFloat("_GpuDepositInnerDiscardMaxSpeed", Mathf.Max(0.01f, c.innerCraterDiscardMaxSpeed));
            compute.SetFloat("_GpuDepositInnerSmoothRadius", innerSmoothRadius);
            compute.SetFloat("_GpuDepositInnerMaxProminenceCells", Mathf.Max(0.25f, c.innerCraterMaxProminenceCells));
            gpuDepositClaimStamp++;
            if (gpuDepositClaimStamp <= 0)
            {
                gpuDepositColumnClaimsBuffer.SetData(new int[terrainColumnCount]);
                gpuDepositClaimStamp = 1;
            }
            compute.SetInt("_GpuDepositClaimStamp", gpuDepositClaimStamp);

            int groupsResults = Mathf.CeilToInt(maxDepositCandidates / 128f);
            compute.Dispatch(kClearGpuDeposits, groupsResults, 1, 1);

            int groupsParticles = Mathf.CeilToInt(ActiveCount / 128f);
            compute.Dispatch(kApplyGpuDeposits, groupsParticles, 1, 1);
        }

        public bool RequestGpuDepositReadback(MeteoriteSPH3DController c)
        {
            if (!SupportsAsyncReadback || !IsReady || ActiveCount <= 0 || gpuDepositReadbackPending) return false;
            DispatchGpuDeposition(c);
            gpuDepositReadbackUploadVersion = uploadVersion;
            gpuDepositReadbackRequest = AsyncGPUReadback.Request(gpuDepositResultBuffer);
            gpuDepositReadbackPending = true;
            return true;
        }

        public bool TryConsumeGpuDepositReadback(List<DepositedVoxel> results)
        {
            if (!gpuDepositReadbackPending) return false;
            if (!gpuDepositReadbackRequest.done) return false;

            gpuDepositReadbackPending = false;
            if (results == null) return false;
            results.Clear();

            if (gpuDepositReadbackRequest.hasError || gpuDepositReadbackUploadVersion != uploadVersion)
            {
                return false;
            }

            NativeArray<GpuDepositResult> data = gpuDepositReadbackRequest.GetData<GpuDepositResult>();
            int count = Mathf.Min(maxDepositCandidates, data.Length);
            int deactivatedOnly = 0;
            for (int i = 0; i < count; i++)
            {
                GpuDepositResult r = data[i];
                if (r.temperatureFlags.w < 0.5f) continue;
                if (r.x < 0 || r.y < 0 || r.z < 0)
                {
                    if (r.particleIndex >= 0) deactivatedOnly++;
                    continue;
                }

                DepositedVoxel d = new DepositedVoxel
                {
                    x = r.x,
                    y = r.y,
                    z = r.z,
                    temperature = r.temperatureFlags.x,
                    particleIndex = r.particleIndex
                };
                results.Add(d);
            }

            int inactiveDelta = results.Count + deactivatedOnly;
            if (inactiveDelta > 0) estimatedInactiveCount = Mathf.Min(ActiveCount, estimatedInactiveCount + inactiveDelta);
            return true;
        }

        public void DownloadGpuDeposits(MeteoriteSPH3DController c, List<DepositedVoxel> results)
        {
            if (!IsReady || gpuDepositResultBuffer == null || gpuDepositResultCache == null || results == null) return;
            gpuDepositReadbackPending = false;
            DispatchGpuDeposition(c);
            gpuDepositResultBuffer.GetData(gpuDepositResultCache, 0, 0, maxDepositCandidates);

            results.Clear();
            int deactivatedOnly = 0;
            for (int i = 0; i < maxDepositCandidates; i++)
            {
                GpuDepositResult r = gpuDepositResultCache[i];
                if (r.temperatureFlags.w < 0.5f) continue;
                if (r.x < 0 || r.y < 0 || r.z < 0)
                {
                    if (r.particleIndex >= 0) deactivatedOnly++;
                    continue;
                }

                DepositedVoxel d = new DepositedVoxel
                {
                    x = r.x,
                    y = r.y,
                    z = r.z,
                    temperature = r.temperatureFlags.x,
                    particleIndex = r.particleIndex
                };
                results.Add(d);
            }

            int inactiveDelta = results.Count + deactivatedOnly;
            if (inactiveDelta > 0) estimatedInactiveCount = Mathf.Min(ActiveCount, estimatedInactiveCount + inactiveDelta);
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
                gpuDepositReadbackPending = false;
                return;
            }

            readbackPending = false;
            depositCandidateReadbackPending = false;
            gpuDepositReadbackPending = false;
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
            float dampingBase = Mathf.Clamp(c.damping, 0.0001f, 1f);
            compute.SetFloat("_DampingFactor", Mathf.Pow(dampingBase, Mathf.Max(dt * 60f, 0f)));
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
