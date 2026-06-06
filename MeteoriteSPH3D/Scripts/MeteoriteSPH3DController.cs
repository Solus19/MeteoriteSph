using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace MeteoriteSPH3D
{
    public sealed class MeteoriteSPH3DController : MonoBehaviour
    {
        public enum SliceAxis { X, Y, Z }

        public static MeteoriteSPH3DController Instance { get; private set; }

        private struct PendingDeposit
        {
            public Vector3Int cell;
            public float temperature;
            public bool rim;
            public bool center;
            public bool forced;

            public PendingDeposit(Vector3Int cell, float temperature, bool rim, bool center, bool forced)
            {
                this.cell = cell;
                this.temperature = temperature;
                this.rim = rim;
                this.center = center;
                this.forced = forced;
            }
        }

        [Header("Voxel terrain")]
        public int terrainWidth = 240;
        public int terrainHeight = 128;
        public int terrainDepth = 240;
        public float cellSize = 0.35f;
        public int baseHeight = 24;
        public bool useReliefTerrain = false;
        public int reliefAmplitudeCells = 44;
        public float reliefNoiseScale = 0.085f;
        public int reliefSeed = 23;

        [Header("GPU simulation")]
        public bool useGpuSimulation = true;
        public int gpuGridMaxParticlesPerCell = 96;
        public int gpuReadbackInterval = 3;
        [Tooltip("Use AsyncGPUReadback for particle state transfer. This avoids blocking the render thread while GPU particles are copied back for deposition.")]
        public bool useAsyncGpuReadback = true;
        [Tooltip("When enabled, GPU sends only broad deposit candidates to CPU instead of downloading the full particle buffer every readback interval.")]
        public bool useGpuDepositCandidateReadback = true;
        [Tooltip("Maximum number of potential deposit particles copied from GPU to CPU per readback. Increase if very large impacts stop depositing material fast enough.")]
        public int gpuDepositCandidateCapacity = 8192;
        [Tooltip("Upload modified voxel terrain back to the GPU only once per N solidification batches. Higher values reduce lag while particles are freezing.")]
        public int gpuTerrainUploadInterval = 4;
        [Tooltip("After a fresh impact keep the original substep count for a short burst, then drop to cheaper substeps as the ejecta slows down.")]
        public bool adaptiveSubsteps = true;
        public int adaptiveMediumSubsteps = 1;
        public int adaptiveLowSubsteps = 1;
        public float adaptiveHighQualityDuration = 0.4f;
        public float adaptiveMediumDuration = 1.5f;
        [Tooltip("Hard cap for fixed simulation iterations in one rendered frame. Lower values avoid FPS collapse when Unity falls behind.")]
        public int maxGpuSimulationIterationsPerFrame = 2;
        [Tooltip("If false, _XsphVelocityBlend is forced to 0 so the extra XSPH neighbour loop is skipped on GPU.")]
        public bool enableXsphVelocityBlend = false;
        [Tooltip("Run a full GPU particle compaction when too many already-solidified particles were deactivated and left as holes in the GPU buffer.")]
        public bool compactInactiveGpuParticles = true;
        [Range(0.05f, 0.95f)] public float gpuCompactInactiveRatio = 0.28f;
        public int gpuCompactMinInactiveCount = 12000;
        public int gpuCompactCooldownFrames = 45;
        [Tooltip("Rebuild the visible voxel mesh only once per N frames while particles are freezing. Higher values reduce lag during deposition.")]
        public int terrainMeshRebuildInterval = 1;
        [Tooltip("Voxel chunk side size used by the terrain renderer. 16 is a good default for this prototype.")]
        public int terrainChunkSize = 12;
        [Tooltip("Maximum number of voxel chunks rebuilt in one frame. Lower values reduce spikes; higher values update terrain faster.")]
        public int maxTerrainChunkRebuildsPerFrame = 24;
        [Tooltip("MeshCollider for rebuilt chunks is refreshed only once per N frames. Physics particles use the voxel buffer, so visual mesh can update more often than colliders.")]
        public int terrainColliderUpdateInterval = 60;

        [Header("Impact")]
        public float impactRadius = 17.20f;
        public float impactPressure = 470f;
        public float pressureThreshold = 52f;
        public float impactTemperature = 860f;
        public float meltTemperature = 260f;
        public float pressureTemperatureFalloffPower = 1.45f;
        public float damageThreshold = 0.56f;
        public float damageScale = 1.55f;
        public bool damageCanActivateVoxels = false;
        public float impactImpulse = 304f;
        public float horizontalEjectaBias = 12.40f;
        public float upwardBias = 1.08f;
        public float rimEjectaBoost = 11.50f;
        public float centralLiftSuppression = 0.42f;
        [Tooltip("Manual fallback depth below the clicked surface when automatic depth scaling is disabled.")]
        public float shockDepth = 0f;
        [Tooltip("When true, the impact ellipsoid is centered exactly at the click point. When false, it is moved below the surface along the local normal.")]
        public bool useMouseClickAsImpactCenter = true;
        [Tooltip("When enabled, the impact center depth is computed from impactRadius. Disabled by default so the explosion center stays at the clicked surface point.")]
        public bool autoScaleShockDepthWithRadius = false;
        [Tooltip("Impact center depth as a fraction of crater radius. Keep 0 to leave the impact center at the clicked surface point.")]
        public float shockDepthToRadiusRatio = 0f;
        public float impactVerticalScale = 0.36f;
        [Tooltip("When true, the pressure/temperature impact field is an ellipsoid oriented by the locally estimated terrain normal at the click point.")]
        public bool impactUseEllipsoid = true;
        [Tooltip("Estimate the impact normal from neighbouring voxel heights instead of using mesh/raycast normals.")]
        public bool useLocalVoxelNormalForImpact = true;
        public int localNormalSampleRadiusCells = 3;
        [Tooltip("Debug only. When false, voxels are converted to particles only after pressure/temperature thresholds are exceeded.")]
        public bool impactActivateAllVoxelsInside = false;
        public int maxParticles = 500000;
        public int maxCreatedParticlesPerImpact = 320000;

        [Header("SPH")]
        public float smoothingRadius = 0.95f;
        public float particleRadius = 0.16f;
        public float particleSpacing = 0.34f;
        public float particleMass = 1f;
        public float restDensity = 5.2f;
        public float minDensity = 1.5f;
        public float maxDensity = 90f;
        public float gasConstant = 105f;
        public float pressureStrength = 2.25f;
        public float nearPressureStrength = 820f;
        [Tooltip("Small attraction between neighbouring particles outside direct contact. Keep this low for explosive ejecta; high values make particles stick together.")]
        public float cohesionStrength = 1.25f;
        [Tooltip("XSPH-like velocity blending between neighbours. Keep this low for meteorite ejecta; high values make one sticky blob.")]
        public float xsphVelocityBlend = 0.08f;
        public float viscosity = 10.50f;
        public float coldViscosityMultiplier = 1.75f;
        public float coldViscosityTemperature = 120f;
        public float hotViscosityTemperature = 260f;
        public float gravity = 15.0f;
        public float damping = 0.996f;
        public float collisionFriction = 0.985f;
        public float maxVelocity = 170.0f;
        public float maxAcceleration = 18000.0f;
        public float timeStep = 1f / 90f;
        public int substeps = 2;
        public float coolingRate = 2.2f;
        public float groundCoolingRate = 60f;
        public float extraWorldHeight = 20f;

        [Header("Visco-plastic ejecta")]
        public bool useViscoPlasticEjecta = true;
        public float semiSolidTemperature = 190f;
        public float semiSolidViscosityMultiplier = 3.60f;
        public float semiSolidVelocityDamping = 0.72f;
        public float semiSolidGravityMultiplier = 0.62f;
        public float groundTangentialDamping = 0.58f;
        public float groundNormalDamping = 0.18f;
        public float groundContactCoolingBoost = 0.85f;

        [Header("Layer view")]
        public bool layerViewEnabled = false;
        public SliceAxis layerViewAxis = SliceAxis.Y;
        public bool singleLayerMode = false;
        public int visibleLayerMin = 0;
        public int visibleLayerMax = 999;
        public int singleVisibleLayer = 0;

        [Header("Particle to voxel")]
        public float solidifyTemperature = 118f;
        public float solidifySpeed = 0.72f;
        public float solidifyMinAge = 3.0f;
        public int depositSearchRadiusCells = 3;
        public int requiredSolidNeighboursForDeposit = 1;
        public bool depositRequireBelowSupport = true;
        public int minBelowFootprintSupport = 3;
        public int maxDepositRiseAboveNeighbours = 1;
        [Tooltip("Reject/penalize deposits that would create isolated bumps above the local neighbour height. Useful on slopes to prevent small voxel pillars.")]
        public bool antiPillarDepositEnabled = true;
        public float maxDepositProminenceAboveNeighbours = 0.55f;
        public int minSameLevelNeighboursForProminentDeposit = 1;
        public float antiPillarProminencePenalty = 7.5f;
        public int maxSolidifyPerFrame = 4096;
        [Tooltip("Maximum number of particles checked for solidification in one frame. Prevents full-list scans when many particles exist.")]
        public int maxSolidifyChecksPerFrame = 50000;

        [Header("Tail deposition optimization")]
        [Tooltip("Queue particle deposits for a few frames, then write them to the voxel terrain in one batch. This reduces repeated dirty chunk rebuilds while particles freeze.")]
        public bool useBatchedParticleDeposition = true;
        [Tooltip("How many frames deposits can be accumulated before they are written to the voxel terrain.")]
        public int depositionBatchFrames = 4;
        [Tooltip("Flush the deposit queue immediately when it reaches this size, even if depositionBatchFrames has not elapsed.")]
        public int depositionBatchMaxQueued = 2048;
        [Tooltip("When active particles drop below this fraction of particles created by the last impact, remaining deposit candidates are forced to settle instead of simulating a long tail.")]
        public bool forceDepositTailUnderInitialFraction = true;
        [Range(0.001f, 0.10f)] public float tailForceDepositInitialFraction = 0.01f;
        [Tooltip("Visible terrain mesh rebuild interval used after the tail cleanup mode starts.")]
        public int tailMeshRebuildInterval = 8;
        [Tooltip("Chunk rebuild budget used after the tail cleanup mode starts.")]
        public int tailMaxTerrainChunkRebuildsPerFrame = 6;

        [Header("Particle cleanup")]
        [Tooltip("Force old, cold ejecta that is already lying near the surface to settle into voxels. This prevents a permanent carpet of particles around the crater.")]
        public bool forceDepositOldParticles = true;
        [Tooltip("Particle age after which fallback deposition can start.")]
        public float forceDepositAge = 5.2f;
        [Tooltip("Maximum speed for fallback deposition. Older particles get a slightly wider tolerance.")]
        public float forceDepositMaxSpeed = 8.5f;
        [Tooltip("Additional temperature allowance over solidifyTemperature for fallback deposition.")]
        public float forceDepositTemperatureBonus = 500f;
        [Tooltip("Maximum distance from a terrain surface, in voxel cells, where old particles may be snapped into voxels.")]
        public float forceDepositGroundSnapDistanceCells = 6f;
        [Tooltip("Search radius used for fallback deposition. Larger value helps particles scattered around the crater find the closest valid terrain column.")]
        public int forcedDepositSearchRadiusCells = 7;

        [Tooltip("When true, a particle is removed only after at least one new visible voxel is actually added to the terrain. Prevents cleanup from just hiding particles.")]
        public bool removeParticleOnlyAfterVisibleVoxelDeposit = true;
        [Tooltip("Additional tiny footprint for old fallback deposits. 0 means one voxel per particle; 1 makes scattered cleanup visibly affect the terrain, but can add more material.")]
        public int forcedDepositFootprintRadiusCells = 0;

        [Header("Rim capture")]
        public bool rimCaptureEnabled = true;
        public float rimCaptureStartRadiusFactor = 0.58f;
        public float rimCaptureEndRadiusFactor = 1.55f;
        public float rimCaptureMaxSpeed = 7.20f;
        public float rimCaptureMinAge = 1.20f;
        public float rimCaptureTemperatureBonus = 220f;
        public float outwardDepositBias = 0.20f;
        [Tooltip("Where the raised rim is preferred relative to impact radius. Values below 1 keep the wall close to the crater edge.")]
        public float rimDepositTargetRadiusFactor = 1.00f;
        public float rimDepositTargetBias = 6.40f;
        public int rimMaxDepositRiseAboveNeighbours = 7;
        public float rimProminenceAllowanceBonus = 5.50f;
        public int rimMinBelowFootprintSupport = 4;

        [Header("Raised rim sculpting")]
        [Tooltip("Optional debug helper. When false, no artificial rim is added at impact time; the crater wall/rim forms only from simulated particles that later settle back into voxels.")]
        public bool sculptRaisedRimAfterImpact = false;
        [Tooltip("Inner radius of the sculpted rim relative to impactRadius.")]
        public float sculptRimInnerRadiusFactor = 0.80f;
        [Tooltip("Radius where the sculpted rim reaches maximum height relative to impactRadius.")]
        public float sculptRimPeakRadiusFactor = 1.02f;
        [Tooltip("Outer radius of the sculpted rim relative to impactRadius.")]
        public float sculptRimOuterRadiusFactor = 1.24f;
        [Tooltip("Fallback maximum added rim height in voxel cells. Used when automatic radius scaling is disabled.")]
        public int sculptRimPeakHeightCells = 11;
        [Tooltip("When enabled, the raised rim height is computed from impactRadius, so larger craters automatically get taller walls.")]
        public bool autoScaleRimHeightWithRadius = true;
        [Tooltip("Rim height as a fraction of crater radius measured in voxel cells. Real simple-crater rim height is about 0.07R; 0.10 keeps it readable in voxels without making a tower.")]
        public float rimHeightToRadiusRatio = 0.10f;
        [Tooltip("Lower clamp for automatically computed rim height.")]
        public int minAutoRimHeightCells = 4;
        [Tooltip("Upper clamp for automatically computed rim height. Keeps very large craters from forming absurd towers.")]
        public int maxAutoRimHeightCells = 16;
        [Tooltip("Apply the same radius-based height limit to particles that later settle onto the raised rim.")]
        public bool autoScaleRimDepositHeightWithRadius = true;
        [Tooltip("Small irregularity so the rim does not look like a perfectly mathematical torus.")]
        public float sculptRimNoiseStrength = 0.22f;
        [Tooltip("Temperature assigned to the freshly raised rim voxels so they render as deposited material.")]
        public float sculptRimTemperature = 95f;

        [Header("Center capture")]
        public bool centerCaptureEnabled = true;
        public float centerCaptureRadiusFactor = 0.36f;
        public float centerCaptureMaxSpeed = 1.55f;
        public float centerCaptureMinAge = 1.15f;
        public float centerCaptureTemperatureBonus = 70f;
        public float centerDepositBias = 0.55f;

        public List<SPHParticle3D> Particles { get { return particles; } }
        public bool UseGpuSimulation { get { return useGpuSimulation && gpuSolver != null && gpuSolver.IsReady; } }
        public ComputeBuffer GpuParticleBuffer { get { return UseGpuSimulation ? gpuSolver.ParticleBuffer : null; } }
        public int ActiveParticleCount { get { return UseGpuSimulation ? gpuSolver.ActiveCount : particles.Count; } }
        public int SolidVoxelCount { get { return terrain != null ? terrain.SolidCount : 0; } }
        public bool IsPaused { get { return paused; } }

        public float LastFrameMs { get; private set; }
        public float LastControllerUpdateMs { get; private set; }
        public float LastGpuSimulationMs { get; private set; }
        public float LastCpuSimulationMs { get; private set; }
        public float LastGpuReadbackMs { get; private set; }
        public float LastSolidifyMs { get; private set; }
        public float LastGpuTerrainUploadMs { get; private set; }
        public float LastGpuParticleUploadMs { get; private set; }
        public float LastMeshRebuildMs { get; private set; }
        public int LastCreatedParticles { get; private set; }
        public int LastSolidifiedParticles { get; private set; }
        public int TotalCreatedParticles { get; private set; }
        public int TotalSolidifiedParticles { get; private set; }

        private VoxelTerrain3D terrain;
        private readonly List<SPHParticle3D> particles = new List<SPHParticle3D>(8192);
        private readonly List<int> pendingGpuDeactivateIndices = new List<int>(512);
        private readonly SPHSolver3D solver = new SPHSolver3D();
        private readonly GPUSPH3DSolver gpuSolver = new GPUSPH3DSolver();
        private int readbackFrame;
        private int solidifyScanCursor = -1;
        private int terrainMeshDirtyFrames;
        private int gpuTerrainDirtyFrames;
        private readonly List<PendingDeposit> pendingDeposits = new List<PendingDeposit>(2048);
        private int depositionBatchFramesElapsed;
        private int lastImpactInitialParticleCount;
        private bool tailDepositionModeLatched;
        private bool gpuTerrainDirty;
        private bool lastGpuMode;

        private VoxelMeshRenderer3D voxelRenderer;
        private ParticleRenderer3D particleRenderer;
        private CameraController3D cameraController;
        private Camera mainCamera;
        private bool terrainDirty;
        private bool paused;
        private Vector3 lastImpactCenter;
        private float lastImpactRadius = 4f;
        private bool hasImpact;
        private float simulationAccumulator;
        private float timeSinceLastImpact = 999f;
        private int gpuCompactCooldownRemaining;

        private void Awake()
        {
            Instance = this;
            Initialize();
        }

        private void OnDestroy()
        {
            gpuSolver.Release();
            if (Instance == this) Instance = null;
        }

        private void Initialize()
        {
            Application.targetFrameRate = 90;

            terrain = new VoxelTerrain3D(terrainWidth, terrainHeight, terrainDepth, cellSize);
            visibleLayerMin = 0;
            visibleLayerMax = terrainHeight - 1;
            singleVisibleLayer = Mathf.Clamp(baseHeight, 0, terrainHeight - 1);
            gpuSolver.Initialize(this, terrain);
            ResetSimulation();

            GameObject camGo = new GameObject("Main Camera");
            mainCamera = camGo.AddComponent<Camera>();
            mainCamera.clearFlags = CameraClearFlags.SolidColor;
            mainCamera.backgroundColor = new Color(0.68f, 0.76f, 0.86f, 1f);
            mainCamera.nearClipPlane = 0.03f;
            mainCamera.farClipPlane = 800f;
            // Do not force legacy camera depth textures here. URP shadow maps are used by the voxel shader,
            // and the shader deliberately avoids screen-space shadows to prevent depth-buffer CommandBuffer warnings.
            mainCamera.depthTextureMode = DepthTextureMode.None;
            camGo.tag = "MainCamera";
            cameraController = camGo.AddComponent<CameraController3D>();
            float cameraDistance = Mathf.Max(terrain.WorldWidth, terrain.WorldDepth) * 1.25f;
            cameraController.Initialize(new Vector3(terrain.WorldWidth * 0.5f, terrain.WorldHeight * 0.25f, terrain.WorldDepth * 0.5f), cameraDistance);

            SetupLighting();

            GameObject terrainGo = new GameObject("Voxel Terrain 3D");
            voxelRenderer = terrainGo.AddComponent<VoxelMeshRenderer3D>();
            voxelRenderer.Initialize();
            voxelRenderer.Configure(terrainChunkSize, maxTerrainChunkRebuildsPerFrame, terrainColliderUpdateInterval);
            voxelRenderer.RebuildImmediate(terrain);

            GameObject particlesGo = new GameObject("SPH Particles 3D");
            particleRenderer = particlesGo.AddComponent<ParticleRenderer3D>();
            particleRenderer.Initialize(particleRadius);

            // No runtime parameter menu: all tuning is done through inspector/defaults.
            if (GetComponent<MeteoriteSPH3DBenchmark>() == null)
            {
                gameObject.AddComponent<MeteoriteSPH3DBenchmark>();
            }
        }

        private void SetupLighting()
        {
            RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
            RenderSettings.ambientLight = new Color(0.08f, 0.085f, 0.095f, 1f);
            RenderSettings.subtractiveShadowColor = new Color(0.05f, 0.055f, 0.065f, 1f);

            // Real shadow maps are enabled here. The voxel chunks below have
            // Cast Shadows and Receive Shadows enabled, and the vertex-color shader
            // now has proper Built-in + URP shadow passes.
            QualitySettings.shadows = ShadowQuality.All;
            QualitySettings.shadowDistance = 440f;
            QualitySettings.shadowResolution = ShadowResolution.High;
            QualitySettings.shadowCascades = 2;
            QualitySettings.shadowProjection = ShadowProjection.StableFit;
            TryConfigureUrpShadows();

            Light key = GetOrCreateDirectionalLight("Voxel Key Light");
            key.color = new Color(1f, 0.96f, 0.88f, 1f);
            key.intensity = 1.12f;
            key.shadows = LightShadows.Soft;
            key.shadowStrength = 0.96f;
            key.shadowBias = 0.006f;
            key.shadowNormalBias = 0.035f;
            key.shadowNearPlane = 0.1f;
            key.renderMode = LightRenderMode.ForcePixel;
            // Low side light: shadows from crater rim and voxel columns are much more visible.
            key.transform.rotation = Quaternion.Euler(24f, -48f, 0f);
            RenderSettings.sun = key;

            Light fill = GetOrCreateDirectionalLight("Voxel Fill Light");
            fill.color = new Color(0.70f, 0.82f, 1.0f, 1f);
            fill.intensity = 0.0f;
            fill.shadows = LightShadows.None;
            fill.transform.rotation = Quaternion.Euler(18f, 132f, 0f);
        }

        private static void TryConfigureUrpShadows()
        {
            // If the host project uses URP, its asset can silently disable main-light shadows.
            // Use reflection so this folder still compiles in Built-in projects without a URP assembly reference.
            object asset = UnityEngine.Rendering.GraphicsSettings.currentRenderPipeline;
            if (asset == null) return;

            System.Type type = asset.GetType();
            if (type == null || type.FullName == null || !type.FullName.Contains("UniversalRenderPipelineAsset")) return;

            SetFieldOrProperty(asset, type, "m_MainLightShadowsSupported", true);
            SetFieldOrProperty(asset, type, "supportsMainLightShadows", true);
            SetFieldOrProperty(asset, type, "m_SoftShadowsSupported", true);
            SetFieldOrProperty(asset, type, "supportsSoftShadows", true);
            SetFieldOrProperty(asset, type, "m_ShadowDistance", 440f);
            SetFieldOrProperty(asset, type, "shadowDistance", 440f);
            SetFieldOrProperty(asset, type, "m_MainLightShadowmapResolution", 4096);
            SetFieldOrProperty(asset, type, "mainLightShadowmapResolution", 4096);
            SetFieldOrProperty(asset, type, "m_ShadowCascadeCount", 2);
            SetFieldOrProperty(asset, type, "shadowCascadeCount", 2);
        }

        private static void SetFieldOrProperty(object target, System.Type type, string name, object value)
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

            FieldInfo field = type.GetField(name, flags);
            if (field != null)
            {
                try
                {
                    if (field.FieldType == typeof(int) && value is int) field.SetValue(target, value);
                    else if (field.FieldType == typeof(float) && value is float) field.SetValue(target, value);
                    else if (field.FieldType == typeof(bool) && value is bool) field.SetValue(target, value);
                }
                catch { }
                return;
            }

            PropertyInfo property = type.GetProperty(name, flags);
            if (property != null && property.CanWrite)
            {
                try
                {
                    if (property.PropertyType == typeof(int) && value is int) property.SetValue(target, value, null);
                    else if (property.PropertyType == typeof(float) && value is float) property.SetValue(target, value, null);
                    else if (property.PropertyType == typeof(bool) && value is bool) property.SetValue(target, value, null);
                }
                catch { }
            }
        }

        private static Light GetOrCreateDirectionalLight(string objectName)
        {
            GameObject lightGo = GameObject.Find(objectName);
            if (lightGo == null) lightGo = new GameObject(objectName);

            Light light = lightGo.GetComponent<Light>();
            if (light == null) light = lightGo.AddComponent<Light>();

            light.type = LightType.Directional;
            return light;
        }

        public void MarkTerrainDirty()
        {
            if (terrain != null) terrain.MarkAllDirty();
            terrainDirty = true;
            terrainMeshDirtyFrames = terrainMeshRebuildInterval;
        }

        public int LayerAxisMax()
        {
            switch (layerViewAxis)
            {
                case SliceAxis.X: return terrainWidth - 1;
                case SliceAxis.Z: return terrainDepth - 1;
                default: return terrainHeight - 1;
            }
        }

        public bool IsVoxelVisible(int x, int y, int z)
        {
            if (!layerViewEnabled) return true;

            int layer = y;
            switch (layerViewAxis)
            {
                case SliceAxis.X: layer = x; break;
                case SliceAxis.Z: layer = z; break;
            }

            if (singleLayerMode) return layer == singleVisibleLayer;
            return layer >= visibleLayerMin && layer <= visibleLayerMax;
        }

        public bool IsPositionVisible(Vector3 position)
        {
            if (!layerViewEnabled) return true;
            int x = Mathf.FloorToInt(position.x / cellSize);
            int y = Mathf.FloorToInt(position.y / cellSize);
            int z = Mathf.FloorToInt(position.z / cellSize);
            return IsVoxelVisible(x, y, z);
        }

        public void ClampLayerView()
        {
            int max = Mathf.Max(0, LayerAxisMax());
            visibleLayerMin = Mathf.Clamp(visibleLayerMin, 0, max);
            visibleLayerMax = Mathf.Clamp(visibleLayerMax, 0, max);
            if (visibleLayerMin > visibleLayerMax)
            {
                int t = visibleLayerMin;
                visibleLayerMin = visibleLayerMax;
                visibleLayerMax = t;
            }
            singleVisibleLayer = Mathf.Clamp(singleVisibleLayer, 0, max);
        }

        public void StepLayer(int delta)
        {
            int max = Mathf.Max(0, LayerAxisMax());
            if (singleLayerMode)
            {
                singleVisibleLayer = Mathf.Clamp(singleVisibleLayer + delta, 0, max);
            }
            else
            {
                int size = Mathf.Max(0, visibleLayerMax - visibleLayerMin);
                visibleLayerMin = Mathf.Clamp(visibleLayerMin + delta, 0, max);
                visibleLayerMax = Mathf.Clamp(visibleLayerMin + size, 0, max);
            }
            if (terrain != null) terrain.MarkAllDirty();
            terrainDirty = true;
        }

        public void TogglePause()
        {
            paused = !paused;
        }

        public void ResetSimulation()
        {
            if (terrain == null || terrain.Width != terrainWidth || terrain.Height != terrainHeight || terrain.Depth != terrainDepth || Mathf.Abs(terrain.CellSize - cellSize) > 0.0001f)
            {
                terrain = new VoxelTerrain3D(terrainWidth, terrainHeight, terrainDepth, cellSize);
                if (gpuSolver != null && gpuSolver.IsReady) gpuSolver.Release();
                gpuSolver.Initialize(this, terrain);
                visibleLayerMin = 0;
                visibleLayerMax = terrainHeight - 1;
                singleVisibleLayer = Mathf.Clamp(baseHeight, 0, terrainHeight - 1);
            }

            if (useReliefTerrain)
                terrain.GenerateRelief(baseHeight, reliefAmplitudeCells, reliefNoiseScale, reliefSeed);
            else
                terrain.GenerateFlat(baseHeight);

            particles.Clear();
            hasImpact = false;
            terrainDirty = true;
            terrainMeshDirtyFrames = terrainMeshRebuildInterval;
            gpuTerrainDirty = false;
            gpuTerrainDirtyFrames = 0;
            solidifyScanCursor = -1;
            readbackFrame = 0;
            simulationAccumulator = 0f;
            timeSinceLastImpact = 999f;
            gpuCompactCooldownRemaining = 0;
            pendingGpuDeactivateIndices.Clear();
            pendingDeposits.Clear();
            depositionBatchFramesElapsed = 0;
            lastImpactInitialParticleCount = 0;
            tailDepositionModeLatched = false;
            LastCreatedParticles = 0;
            LastSolidifiedParticles = 0;
            TotalCreatedParticles = 0;
            TotalSolidifiedParticles = 0;
            LastFrameMs = 0f;
            LastControllerUpdateMs = 0f;
            LastGpuSimulationMs = 0f;
            LastCpuSimulationMs = 0f;
            LastGpuReadbackMs = 0f;
            LastSolidifyMs = 0f;
            LastGpuTerrainUploadMs = 0f;
            LastGpuParticleUploadMs = 0f;
            LastMeshRebuildMs = 0f;
            lastGpuMode = UseGpuSimulation;
            if (useGpuSimulation && gpuSolver.IsReady)
            {
                gpuSolver.UploadTerrain(terrain);
                gpuSolver.UploadFromParticles(particles);
            }
        }

        public void ApplyCraterRimPreset()
        {
            // 2x large crater preset: wider terrain and stronger impact while keeping voxel resolution unchanged.
            // Does not force relief terrain, so flat-surface tests stay flat unless the menu toggle is enabled.
            terrainWidth = 240;
            terrainHeight = 128;
            terrainDepth = 240;
            baseHeight = 24;
            reliefAmplitudeCells = 44;
            extraWorldHeight = 20f;
            useReliefTerrain = false;
            impactRadius = 17.20f;
            impactPressure = 470f;
            pressureThreshold = 52f;
            impactTemperature = 860f;
            meltTemperature = 260f;
            pressureTemperatureFalloffPower = 1.45f;
            damageThreshold = 0.56f;
            damageScale = 1.55f;
            damageCanActivateVoxels = false;
            impactImpulse = 304f;
            horizontalEjectaBias = 12.40f;
            upwardBias = 1.08f;
            centralLiftSuppression = 0.42f;
            rimEjectaBoost = 9.20f;
            shockDepth = 0f;
            useMouseClickAsImpactCenter = true;
            autoScaleShockDepthWithRadius = false;
            shockDepthToRadiusRatio = 0f;
            impactVerticalScale = 0.36f;
            impactUseEllipsoid = true;
            useLocalVoxelNormalForImpact = true;
            localNormalSampleRadiusCells = 3;
            impactActivateAllVoxelsInside = false;
            maxParticles = 500000;
            maxCreatedParticlesPerImpact = 320000;
            gpuGridMaxParticlesPerCell = 96;

            smoothingRadius = 0.95f;
            particleRadius = 0.16f;
            particleSpacing = 0.34f;
            particleMass = 1.0f;
            restDensity = 5.2f;
            minDensity = 1.0f;
            maxDensity = 120.0f;
            gasConstant = 105f;
            pressureStrength = 2.25f;
            nearPressureStrength = 820f;
            cohesionStrength = 1.25f;
            xsphVelocityBlend = 0.08f;

            // Rim-forming preset: SPH pressure/near-pressure is strong, cohesion is very low,
            // so particles still interact as fluid but do not hold each other as one sticky blob.
            viscosity = 10.50f;
            coldViscosityMultiplier = 1.75f;
            coldViscosityTemperature = 120f;
            hotViscosityTemperature = 260f;
            gravity = 15.0f;
            damping = 0.996f;
            collisionFriction = 0.985f;
            maxVelocity = 170.0f;
            maxAcceleration = 18000.0f;
            timeStep = 1f / 90f;
            substeps = 2;
            coolingRate = 2.2f;
            groundCoolingRate = 60.0f;

            useViscoPlasticEjecta = true;
            semiSolidTemperature = 190f;
            semiSolidViscosityMultiplier = 3.60f;
            semiSolidVelocityDamping = 0.72f;
            semiSolidGravityMultiplier = 0.62f;
            groundTangentialDamping = 0.58f;
            groundNormalDamping = 0.18f;
            groundContactCoolingBoost = 0.85f;

            solidifyTemperature = 118f;
            solidifySpeed = 0.72f;
            solidifyMinAge = 3.0f;
            depositSearchRadiusCells = 3;
            requiredSolidNeighboursForDeposit = 1;
            depositRequireBelowSupport = true;
            minBelowFootprintSupport = 3;
            maxDepositRiseAboveNeighbours = 1;
            antiPillarDepositEnabled = true;
            maxDepositProminenceAboveNeighbours = 0.55f;
            minSameLevelNeighboursForProminentDeposit = 1;
            antiPillarProminencePenalty = 7.5f;
            maxSolidifyPerFrame = 4096;
            maxSolidifyChecksPerFrame = 50000;
            useBatchedParticleDeposition = true;
            depositionBatchFrames = 4;
            depositionBatchMaxQueued = 2048;
            forceDepositTailUnderInitialFraction = true;
            tailForceDepositInitialFraction = 0.01f;
            tailMeshRebuildInterval = 8;
            tailMaxTerrainChunkRebuildsPerFrame = 6;
            forceDepositOldParticles = true;
            forceDepositAge = 5.2f;
            forceDepositMaxSpeed = 8.5f;
            forceDepositTemperatureBonus = 500f;
            forceDepositGroundSnapDistanceCells = 6f;
            forcedDepositSearchRadiusCells = 7;
            gpuReadbackInterval = 3;
            useAsyncGpuReadback = true;
            useGpuDepositCandidateReadback = true;
            gpuDepositCandidateCapacity = 8192;
            gpuTerrainUploadInterval = 4;
            adaptiveSubsteps = true;
            adaptiveMediumSubsteps = 1;
            adaptiveLowSubsteps = 1;
            adaptiveHighQualityDuration = 0.4f;
            adaptiveMediumDuration = 1.5f;
            maxGpuSimulationIterationsPerFrame = 2;
            enableXsphVelocityBlend = false;
            compactInactiveGpuParticles = true;
            gpuCompactInactiveRatio = 0.28f;
            gpuCompactMinInactiveCount = 12000;
            gpuCompactCooldownFrames = 45;
            terrainMeshRebuildInterval = 1;
            terrainChunkSize = 12;
            maxTerrainChunkRebuildsPerFrame = 24;
            terrainColliderUpdateInterval = 60;

            rimCaptureEnabled = true;
            rimCaptureStartRadiusFactor = 0.58f;
            rimCaptureEndRadiusFactor = 1.55f;
            rimCaptureMaxSpeed = 7.20f;
            rimCaptureMinAge = 1.20f;
            rimCaptureTemperatureBonus = 220f;
            outwardDepositBias = 0.20f;
            rimDepositTargetRadiusFactor = 1.00f;
            rimDepositTargetBias = 6.40f;
            rimMaxDepositRiseAboveNeighbours = 7;
            rimProminenceAllowanceBonus = 5.50f;
            rimMinBelowFootprintSupport = 4;

            // Keep this disabled for physical-looking behavior: the rim should appear from particle deposition, not from an instant terrain sculpt.
            sculptRaisedRimAfterImpact = false;
            sculptRimInnerRadiusFactor = 0.80f;
            sculptRimPeakRadiusFactor = 1.02f;
            sculptRimOuterRadiusFactor = 1.24f;
            sculptRimPeakHeightCells = 11;
            autoScaleRimHeightWithRadius = true;
            rimHeightToRadiusRatio = 0.10f;
            minAutoRimHeightCells = 3;
            maxAutoRimHeightCells = 16;
            autoScaleRimDepositHeightWithRadius = true;
            sculptRimNoiseStrength = 0.22f;
            sculptRimTemperature = 95f;

            centerCaptureEnabled = true;
            centerCaptureRadiusFactor = 0.36f;
            centerCaptureMaxSpeed = 1.55f;
            centerCaptureMinAge = 1.15f;
            centerCaptureTemperatureBonus = 70f;
            centerDepositBias = 0.55f;
        }

        private void Update()
        {
            float updateStartMs = Time.realtimeSinceStartup * 1000f;
            LastFrameMs = Time.unscaledDeltaTime * 1000f;
            LastControllerUpdateMs = 0f;
            LastGpuSimulationMs = 0f;
            LastCpuSimulationMs = 0f;
            LastGpuReadbackMs = 0f;
            LastSolidifyMs = 0f;
            LastGpuTerrainUploadMs = 0f;
            LastGpuParticleUploadMs = 0f;
            LastMeshRebuildMs = 0f;
            LastCreatedParticles = 0;
            LastSolidifiedParticles = 0;

            if (InputBridge3D.KeyDown(KeyCode.R)) ResetSimulation();
            if (InputBridge3D.KeyDown(KeyCode.Space)) TogglePause();
            ClampLayerView();
            if (InputBridge3D.KeyDown(KeyCode.LeftBracket)) StepLayer(-1);
            if (InputBridge3D.KeyDown(KeyCode.RightBracket)) StepLayer(1);

            if (InputBridge3D.MouseDown(0))
            {
                TryImpactFromMouse();
            }

            bool currentGpuMode = UseGpuSimulation;
            if (currentGpuMode != lastGpuMode)
            {
                if (currentGpuMode)
                {
                    float uploadTerrainStartMs = Time.realtimeSinceStartup * 1000f;
                    gpuSolver.UploadTerrain(terrain);
                    LastGpuTerrainUploadMs += Time.realtimeSinceStartup * 1000f - uploadTerrainStartMs;

                    float uploadParticlesStartMs = Time.realtimeSinceStartup * 1000f;
                    gpuSolver.UploadFromParticles(particles);
                    LastGpuParticleUploadMs += Time.realtimeSinceStartup * 1000f - uploadParticlesStartMs;
                }
                else
                {
                    float readbackStartMs = Time.realtimeSinceStartup * 1000f;
                    gpuSolver.DownloadToParticles(particles);
                    LastGpuReadbackMs += Time.realtimeSinceStartup * 1000f - readbackStartMs;
                }
                lastGpuMode = currentGpuMode;
            }

            if (!paused)
            {
                float dt = Mathf.Min(Time.deltaTime, 1f / 20f);
                if (hasImpact) timeSinceLastImpact += dt;
                if (gpuCompactCooldownRemaining > 0) gpuCompactCooldownRemaining--;
                float fixedStep = Mathf.Max(0.001f, timeStep);
                int runtimeSubsteps = GetRuntimeSubsteps();
                float step = fixedStep / Mathf.Max(1, runtimeSubsteps);
                int maxIterationsThisFrame = UseGpuSimulation ? Mathf.Clamp(maxGpuSimulationIterationsPerFrame, 1, 4) : 4;
                simulationAccumulator = Mathf.Min(simulationAccumulator + dt, fixedStep * maxIterationsThisFrame);
                int iterations = 0;
                while (simulationAccumulator >= fixedStep && iterations < maxIterationsThisFrame)
                {
                    simulationAccumulator -= fixedStep;
                    iterations++;
                }
                if (UseGpuSimulation)
                {
                    float simStartMs = Time.realtimeSinceStartup * 1000f;
                    for (int i = 0; i < iterations; i++)
                    {
                        for (int s = 0; s < runtimeSubsteps; s++)
                        {
                            gpuSolver.Step(this, terrain, step);
                        }
                    }
                    LastGpuSimulationMs += Time.realtimeSinceStartup * 1000f - simStartMs;

                    if (useAsyncGpuReadback && gpuSolver.SupportsAsyncReadback)
                    {
                        float readbackStartMs = Time.realtimeSinceStartup * 1000f;
                        bool completed = useGpuDepositCandidateReadback
                            ? gpuSolver.TryConsumeDepositCandidateReadback(particles)
                            : gpuSolver.TryConsumeParticleReadback(particles);
                        if (completed)
                        {
                            LastGpuReadbackMs += Time.realtimeSinceStartup * 1000f - readbackStartMs;
                            ProcessDownloadedGpuParticles(true, useGpuDepositCandidateReadback);
                        }

                        readbackFrame++;
                        bool pending = useGpuDepositCandidateReadback
                            ? gpuSolver.IsDepositCandidateReadbackPending
                            : gpuSolver.IsReadbackPending;
                        if (readbackFrame >= Mathf.Max(1, gpuReadbackInterval) && !pending)
                        {
                            readbackFrame = 0;
                            if (useGpuDepositCandidateReadback)
                                gpuSolver.RequestDepositCandidateReadback(this);
                            else
                                gpuSolver.RequestParticleReadback();
                        }
                    }
                    else
                    {
                        readbackFrame++;
                        if (readbackFrame >= Mathf.Max(1, gpuReadbackInterval))
                        {
                            readbackFrame = 0;

                            float readbackStartMs = Time.realtimeSinceStartup * 1000f;
                            if (useGpuDepositCandidateReadback)
                                gpuSolver.DownloadDepositCandidates(this, particles);
                            else
                                gpuSolver.DownloadToParticles(particles);
                            LastGpuReadbackMs += Time.realtimeSinceStartup * 1000f - readbackStartMs;
                            ProcessDownloadedGpuParticles(false, useGpuDepositCandidateReadback);
                        }
                    }
                }
                else
                {
                    float cpuSimStartMs = Time.realtimeSinceStartup * 1000f;
                    for (int i = 0; i < iterations; i++)
                    {
                        for (int s = 0; s < runtimeSubsteps; s++)
                        {
                            solver.Step(particles, terrain, this, step);
                        }
                        float solidifyStartMs = Time.realtimeSinceStartup * 1000f;
                        int solidified = SolidifyParticles();
                        LastSolidifyMs += Time.realtimeSinceStartup * 1000f - solidifyStartMs;
                        LastSolidifiedParticles += solidified;
                        TotalSolidifiedParticles += solidified;
                    }
                    LastCpuSimulationMs += Time.realtimeSinceStartup * 1000f - cpuSimStartMs;
                }

                terrain.CoolVoxels(Time.deltaTime, coolingRate * 0.2f);
                FlushPendingDepositsIfNeeded(false);
                UploadDirtyTerrainIfDue();
            }

            if (terrainDirty && voxelRenderer != null)
            {
                terrainMeshDirtyFrames++;
                int meshInterval = GetEffectiveTerrainMeshRebuildInterval();
                if (terrainMeshDirtyFrames >= meshInterval)
                {
                    voxelRenderer.Configure(terrainChunkSize, GetEffectiveMaxTerrainChunkRebuildsPerFrame(), terrainColliderUpdateInterval);
                    float meshStartMs = Time.realtimeSinceStartup * 1000f;
                    voxelRenderer.Rebuild(terrain);
                    LastMeshRebuildMs += Time.realtimeSinceStartup * 1000f - meshStartMs;
                    terrainDirty = voxelRenderer.HasPendingRebuilds || (terrain != null && (terrain.HasDirtyBounds || terrain.HasDirtyVoxels));
                    terrainMeshDirtyFrames = terrainDirty && !IsTailDepositModeActive() ? meshInterval : 0;
                }
            }

            if (particleRenderer != null)
            {
                particleRenderer.SetRadius(particleRadius);
            }

            LastControllerUpdateMs = Time.realtimeSinceStartup * 1000f - updateStartMs;
        }


        private void ProcessDownloadedGpuParticles(bool readbackWasAsync, bool partialCandidateReadback = false)
        {
            int before = particles.Count;

            float solidifyStartMs = Time.realtimeSinceStartup * 1000f;
            int solidified = SolidifyParticles();
            LastSolidifyMs += Time.realtimeSinceStartup * 1000f - solidifyStartMs;
            LastSolidifiedParticles += solidified;
            TotalSolidifiedParticles += solidified;

            UploadDirtyTerrainIfDue();

            if (readbackWasAsync || partialCandidateReadback)
            {
                if (pendingGpuDeactivateIndices.Count > 0)
                {
                    gpuSolver.DeactivateParticles(pendingGpuDeactivateIndices);
                    pendingGpuDeactivateIndices.Clear();
                }
            }
            else if (particles.Count != before || solidified > 0)
            {
                float uploadParticlesStartMs = Time.realtimeSinceStartup * 1000f;
                gpuSolver.UploadFromParticles(particles);
                LastGpuParticleUploadMs += Time.realtimeSinceStartup * 1000f - uploadParticlesStartMs;
            }

            TryCompactGpuParticles();
        }


        public bool IsTailDepositModeActive()
        {
            if (tailDepositionModeLatched) return true;
            if (!hasImpact || !forceDepositTailUnderInitialFraction || lastImpactInitialParticleCount <= 0) return false;

            int active = ActiveParticleCount;
            int threshold = Mathf.Max(1, Mathf.CeilToInt(lastImpactInitialParticleCount * Mathf.Clamp(tailForceDepositInitialFraction, 0.001f, 0.10f)));
            if (active > 0 && active <= threshold)
            {
                tailDepositionModeLatched = true;
                return true;
            }

            return false;
        }

        private int GetEffectiveTerrainMeshRebuildInterval()
        {
            return Mathf.Max(1, IsTailDepositModeActive() ? tailMeshRebuildInterval : terrainMeshRebuildInterval);
        }

        private int GetEffectiveMaxTerrainChunkRebuildsPerFrame()
        {
            return Mathf.Max(1, IsTailDepositModeActive() ? tailMaxTerrainChunkRebuildsPerFrame : maxTerrainChunkRebuildsPerFrame);
        }

        private void QueuePendingDeposit(Vector3Int cell, float temperature, bool rim, bool center, bool forced)
        {
            pendingDeposits.Add(new PendingDeposit(cell, temperature, rim, center, forced));
            if (pendingDeposits.Count >= Mathf.Max(1, depositionBatchMaxQueued))
            {
                FlushPendingDeposits(true);
            }
        }

        private void FlushPendingDepositsIfNeeded(bool force)
        {
            if (pendingDeposits.Count == 0)
            {
                depositionBatchFramesElapsed = 0;
                return;
            }

            depositionBatchFramesElapsed++;
            int frames = Mathf.Max(1, depositionBatchFrames);
            if (force || depositionBatchFramesElapsed >= frames)
            {
                FlushPendingDeposits(true);
            }
        }

        private void FlushPendingDeposits(bool force)
        {
            if (pendingDeposits.Count == 0 || terrain == null) return;

            int placed = 0;
            for (int i = 0; i < pendingDeposits.Count; i++)
            {
                PendingDeposit d = pendingDeposits[i];
                placed += DepositParticleMaterial(d.cell, d.temperature, d.rim, d.center, d.forced);
            }

            pendingDeposits.Clear();
            depositionBatchFramesElapsed = 0;

            if (placed > 0)
            {
                terrainDirty = true;
                gpuTerrainDirty = true;
            }
        }

        private void UploadDirtyTerrainIfDue()
        {
            if (!UseGpuSimulation || !gpuTerrainDirty || gpuSolver == null || !gpuSolver.IsReady) return;

            gpuTerrainDirtyFrames++;
            if (gpuTerrainDirtyFrames >= GetEffectiveGpuTerrainUploadInterval())
            {
                gpuTerrainDirtyFrames = 0;
                gpuTerrainDirty = false;
                float uploadTerrainStartMs = Time.realtimeSinceStartup * 1000f;
                gpuSolver.UploadDirtyTerrain(terrain);
                LastGpuTerrainUploadMs += Time.realtimeSinceStartup * 1000f - uploadTerrainStartMs;
            }
        }

        private int GetRuntimeSubsteps()
        {
            int baseSubsteps = Mathf.Max(1, substeps);
            if (!adaptiveSubsteps || !UseGpuSimulation) return baseSubsteps;

            int medium = Mathf.Clamp(adaptiveMediumSubsteps, 1, baseSubsteps);
            int low = Mathf.Clamp(adaptiveLowSubsteps, 1, medium);
            int active = ActiveParticleCount;

            if (!hasImpact || timeSinceLastImpact <= adaptiveHighQualityDuration) return baseSubsteps;
            if (timeSinceLastImpact <= adaptiveMediumDuration) return medium;
            if (active > 80000) return medium;
            if (active > 20000) return medium;
            return low;
        }

        private int GetEffectiveGpuTerrainUploadInterval()
        {
            int interval = Mathf.Max(1, gpuTerrainUploadInterval);
            if (!UseGpuSimulation) return interval;

            int active = ActiveParticleCount;
            if (active > 120000) return Mathf.Max(interval, 6);
            if (active > 60000) return Mathf.Max(interval, 4);
            if (active > 15000) return Mathf.Max(interval, 3);
            return interval;
        }

        private void TryCompactGpuParticles()
        {
            if (!UseGpuSimulation || !compactInactiveGpuParticles) return;
            if (gpuCompactCooldownRemaining > 0) return;
            if (gpuSolver.EstimatedInactiveCount < Mathf.Max(1, gpuCompactMinInactiveCount)) return;
            if (gpuSolver.EstimatedInactiveRatio < Mathf.Clamp01(gpuCompactInactiveRatio)) return;
            if (gpuSolver.IsReadbackPending || gpuSolver.IsDepositCandidateReadbackPending) return;

            float compactStartMs = Time.realtimeSinceStartup * 1000f;
            gpuSolver.CompactActiveParticles(particles);
            LastGpuReadbackMs += Time.realtimeSinceStartup * 1000f - compactStartMs;
            readbackFrame = 0;
            gpuCompactCooldownRemaining = Mathf.Max(1, gpuCompactCooldownFrames);
        }

        private void TryImpactFromMouse()
        {
            if (mainCamera == null) return;
            Ray ray = mainCamera.ScreenPointToRay(InputBridge3D.MousePosition());

            Vector3 voxelHit;
            if (RaycastVoxelTerrain(ray, out voxelHit))
            {
                ApplyImpact(voxelHit);
                return;
            }

            RaycastHit hit;
            if (Physics.Raycast(ray, out hit, 1000f))
            {
                ApplyImpact(hit.point);
            }
        }

        private bool RaycastVoxelTerrain(Ray ray, out Vector3 hitPoint)
        {
            hitPoint = Vector3.zero;
            if (terrain == null) return false;

            float maxDistance = Mathf.Max(terrain.WorldWidth, Mathf.Max(terrain.WorldHeight, terrain.WorldDepth)) * 3f;
            float step = Mathf.Max(terrain.CellSize * 0.45f, 0.05f);
            float start = Mathf.Max(0f, mainCamera != null ? mainCamera.nearClipPlane : 0f);

            for (float t = start; t <= maxDistance; t += step)
            {
                Vector3 p = ray.origin + ray.direction * t;
                int x = Mathf.FloorToInt(p.x / terrain.CellSize);
                int z = Mathf.FloorToInt(p.z / terrain.CellSize);
                if (x < 0 || x >= terrain.Width || z < 0 || z >= terrain.Depth) continue;

                int top = terrain.TopSolidY(x, z);
                if (top < 0) continue;

                float surfaceY = (top + 1) * terrain.CellSize;
                if (p.y <= surfaceY)
                {
                    hitPoint = new Vector3(
                        Mathf.Clamp(p.x, 0f, terrain.WorldWidth),
                        surfaceY,
                        Mathf.Clamp(p.z, 0f, terrain.WorldDepth));
                    return true;
                }
            }

            return false;
        }

        private int TopSolidYInColumnForImpact(int x, int z)
        {
            if (terrain == null || x < 0 || x >= terrainWidth || z < 0 || z >= terrainDepth) return -1;
            for (int y = terrainHeight - 1; y >= 0; y--)
            {
                if (terrain.IsSolid(x, y, z)) return y;
            }
            return -1;
        }

        private Vector3 EstimateLocalVoxelNormal(Vector3 hitPoint)
        {
            if (terrain == null) return Vector3.up;

            Vector3Int c = terrain.WorldToCell(hitPoint);
            int r = Mathf.Max(1, localNormalSampleRadiusCells);

            int xL = Mathf.Clamp(c.x - r, 0, terrainWidth - 1);
            int xR = Mathf.Clamp(c.x + r, 0, terrainWidth - 1);
            int zD = Mathf.Clamp(c.z - r, 0, terrainDepth - 1);
            int zU = Mathf.Clamp(c.z + r, 0, terrainDepth - 1);

            int hL = TopSolidYInColumnForImpact(xL, c.z);
            int hR = TopSolidYInColumnForImpact(xR, c.z);
            int hD = TopSolidYInColumnForImpact(c.x, zD);
            int hU = TopSolidYInColumnForImpact(c.x, zU);

            if (hL < 0 || hR < 0 || hD < 0 || hU < 0) return Vector3.up;

            float dxWorld = Mathf.Max(cellSize, (xR - xL) * cellSize);
            float dzWorld = Mathf.Max(cellSize, (zU - zD) * cellSize);
            float dhdx = ((hR - hL) * cellSize) / dxWorld;
            float dhdz = ((hU - hD) * cellSize) / dzWorld;

            Vector3 n = new Vector3(-dhdx, 1f, -dhdz);
            if (n.sqrMagnitude < 0.0001f) return Vector3.up;
            return n.normalized;
        }

        private static void BuildImpactBasis(Vector3 normal, out Vector3 tangentA, out Vector3 tangentB)
        {
            Vector3 n = normal.sqrMagnitude > 0.0001f ? normal.normalized : Vector3.up;
            tangentA = Vector3.Cross(n, Vector3.forward);
            if (tangentA.sqrMagnitude < 0.0001f)
            {
                tangentA = Vector3.Cross(n, Vector3.right);
            }
            tangentA.Normalize();
            tangentB = Vector3.Cross(n, tangentA).normalized;
        }

        private float ComputeImpactCenterDepth()
        {
            if (useMouseClickAsImpactCenter) return 0f;
            if (autoScaleShockDepthWithRadius)
            {
                return Mathf.Max(0f, impactRadius * Mathf.Max(0f, shockDepthToRadiusRatio));
            }
            return Mathf.Max(0f, shockDepth);
        }

        private void ApplyImpact(Vector3 hitPoint)
        {
            FlushPendingDeposits(true);

            // New impact appends particles on CPU. Sync once on click so we do not overwrite
            // the current GPU simulation with an older async readback snapshot.
            if (UseGpuSimulation && gpuSolver.ActiveCount > 0)
            {
                float readbackStartMs = Time.realtimeSinceStartup * 1000f;
                gpuSolver.DownloadToParticles(particles);
                LastGpuReadbackMs += Time.realtimeSinceStartup * 1000f - readbackStartMs;
                pendingGpuDeactivateIndices.Clear();
            }

            Vector3 impactNormal = useLocalVoxelNormalForImpact ? EstimateLocalVoxelNormal(hitPoint) : Vector3.up;
            Vector3 tangentA;
            Vector3 tangentB;
            BuildImpactBasis(impactNormal, out tangentA, out tangentB);

            float centerDepth = ComputeImpactCenterDepth();
            Vector3 center = hitPoint - impactNormal * centerDepth;
            lastImpactCenter = center;
            lastImpactRadius = impactRadius;
            hasImpact = true;
            timeSinceLastImpact = 0f;
            gpuCompactCooldownRemaining = 0;

            int created = 0;
            Vector3Int min = terrain.WorldToCell(center - Vector3.one * impactRadius);
            Vector3Int max = terrain.WorldToCell(center + Vector3.one * impactRadius);

            for (int y = min.y; y <= max.y; y++)
            {
                for (int z = min.z; z <= max.z; z++)
                {
                    for (int x = min.x; x <= max.x; x++)
                    {
                        if (!terrain.InBounds(x, y, z) || !terrain.IsSolid(x, y, z)) continue;
                        Vector3 pos = terrain.CellCenter(x, y, z);
                        Vector3 rel = pos - center;
                        float shape;
                        if (impactUseEllipsoid)
                        {
                            float normalRadius = Mathf.Max(cellSize * 1.5f, impactRadius * Mathf.Clamp(impactVerticalScale, 0.15f, 1.0f));
                            float tx = Vector3.Dot(rel, tangentA) / Mathf.Max(0.001f, impactRadius);
                            float tz = Vector3.Dot(rel, tangentB) / Mathf.Max(0.001f, impactRadius);
                            float tn = Vector3.Dot(rel, impactNormal) / Mathf.Max(0.001f, normalRadius);
                            shape = Mathf.Sqrt(tx * tx + tz * tz + tn * tn);
                        }
                        else
                        {
                            shape = rel.magnitude / Mathf.Max(0.001f, impactRadius);
                        }
                        if (shape > 1f) continue;

                        float nr = Mathf.Clamp01(shape);
                        float falloff = Mathf.Pow(1f - nr, Mathf.Max(0.25f, pressureTemperatureFalloffPower));
                        VoxelCell3D cell = terrain.Get(x, y, z);
                        cell.pressure += impactPressure * falloff;
                        cell.temperature += impactTemperature * falloff;
                        cell.damage += damageScale * falloff;
                        terrain.Set(x, y, z, cell);

                        if (created >= maxCreatedParticlesPerImpact || particles.Count >= maxParticles) continue;
                        bool pressureTemperatureActivated = cell.temperature >= meltTemperature || cell.pressure >= pressureThreshold;
                        bool damageActivated = damageCanActivateVoxels && cell.damage >= damageThreshold;
                        bool shouldActivate = impactActivateAllVoxelsInside || pressureTemperatureActivated || damageActivated;
                        if (shouldActivate)
                        {
                            Vector3 v = InitialEjectaVelocity(pos, center, falloff, nr);
                            terrain.SetSolid(x, y, z, false, 0f, 0f, 0f);
                            particles.Add(new SPHParticle3D(pos, v, Mathf.Max(cell.temperature, impactTemperature * falloff * 0.65f), particleMass));
                            created++;
                        }
                    }
                }
            }

            if (sculptRaisedRimAfterImpact)
            {
                SculptRaisedRim(center, impactRadius);
            }

            LastCreatedParticles = created;
            TotalCreatedParticles += created;
            lastImpactInitialParticleCount = Mathf.Max(1, created);
            tailDepositionModeLatched = false;

            terrainDirty = true;
            terrainMeshDirtyFrames = terrainMeshRebuildInterval;
            if (useGpuSimulation && gpuSolver.IsReady)
            {
                float uploadTerrainStartMs = Time.realtimeSinceStartup * 1000f;
                gpuSolver.UploadDirtyTerrain(terrain);
                LastGpuTerrainUploadMs += Time.realtimeSinceStartup * 1000f - uploadTerrainStartMs;
                gpuTerrainDirty = false;
                gpuTerrainDirtyFrames = 0;

                float uploadParticlesStartMs = Time.realtimeSinceStartup * 1000f;
                gpuSolver.UploadFromParticles(particles);
                LastGpuParticleUploadMs += Time.realtimeSinceStartup * 1000f - uploadParticlesStartMs;
            }
        }

        private int ComputeScaledRimHeightCells(float radius)
        {
            if (terrain == null) return Mathf.Max(0, sculptRimPeakHeightCells);

            if (!autoScaleRimHeightWithRadius)
            {
                return Mathf.Clamp(sculptRimPeakHeightCells, 0, terrain.Height - 2);
            }

            float radiusCells = radius / Mathf.Max(0.0001f, cellSize);
            int computed = Mathf.RoundToInt(radiusCells * Mathf.Max(0f, rimHeightToRadiusRatio));
            int minHeight = Mathf.Max(0, minAutoRimHeightCells);
            int maxHeight = Mathf.Max(minHeight, maxAutoRimHeightCells);
            return Mathf.Clamp(computed, minHeight, Mathf.Min(maxHeight, terrain.Height - 2));
        }

        private int ComputeRimDepositRiseLimit()
        {
            int fixedLimit = Mathf.Max(0, rimMaxDepositRiseAboveNeighbours);
            if (!autoScaleRimDepositHeightWithRadius || terrain == null) return fixedLimit;
            return Mathf.Max(fixedLimit, ComputeScaledRimHeightCells(lastImpactRadius));
        }

        private void SculptRaisedRim(Vector3 center, float radius)
        {
            if (terrain == null || radius <= 0.001f) return;

            float inner = Mathf.Max(cellSize, radius * Mathf.Clamp(sculptRimInnerRadiusFactor, 0.10f, 2.0f));
            float peak = Mathf.Max(inner + cellSize, radius * Mathf.Clamp(sculptRimPeakRadiusFactor, 0.10f, 2.5f));
            float outer = Mathf.Max(peak + cellSize, radius * Mathf.Clamp(sculptRimOuterRadiusFactor, 0.10f, 3.0f));
            int peakHeight = ComputeScaledRimHeightCells(radius);
            if (peakHeight <= 0) return;

            Vector3Int min = terrain.WorldToCell(center - new Vector3(outer, 0f, outer));
            Vector3Int max = terrain.WorldToCell(center + new Vector3(outer, 0f, outer));
            min.x = Mathf.Clamp(min.x, 0, terrain.Width - 1);
            min.z = Mathf.Clamp(min.z, 0, terrain.Depth - 1);
            max.x = Mathf.Clamp(max.x, 0, terrain.Width - 1);
            max.z = Mathf.Clamp(max.z, 0, terrain.Depth - 1);

            for (int z = min.z; z <= max.z; z++)
            {
                for (int x = min.x; x <= max.x; x++)
                {
                    Vector3 cp = terrain.CellCenter(x, 0, z);
                    float dx = cp.x - center.x;
                    float dz = cp.z - center.z;
                    float r = Mathf.Sqrt(dx * dx + dz * dz);
                    if (r < inner || r > outer) continue;

                    float profile;
                    if (r <= peak)
                    {
                        profile = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(inner, peak, r));
                    }
                    else
                    {
                        profile = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(outer, peak, r));
                    }

                    if (profile <= 0.02f) continue;

                    float noise = Mathf.PerlinNoise(x * 0.173f + 31.7f, z * 0.173f + 91.3f) * 2f - 1f;
                    float noisyProfile = Mathf.Clamp01(profile * (1f + noise * Mathf.Clamp01(sculptRimNoiseStrength)));
                    int extraCells = Mathf.RoundToInt(noisyProfile * peakHeight);
                    if (profile > 0.18f) extraCells = Mathf.Max(1, extraCells);
                    if (extraCells <= 0) continue;

                    int top = terrain.TopSolidY(x, z);
                    if (top < 0 || top >= terrain.Height - 2) continue;

                    int yMax = Mathf.Min(terrain.Height - 2, top + extraCells);
                    for (int y = top + 1; y <= yMax; y++)
                    {
                        terrain.SetSolid(x, y, z, true, sculptRimTemperature, 0f, 0.15f, true);
                    }
                }
            }
        }

        private Vector3 InitialEjectaVelocity(Vector3 pos, Vector3 center, float falloff, float normalizedRadius)
        {
            Vector3 radial = pos - center;
            if (radial.sqrMagnitude < 0.0001f) radial = Vector3.up;
            radial.Normalize();

            Vector3 flatRadial = new Vector3(radial.x, 0f, radial.z);
            if (flatRadial.sqrMagnitude < 0.0001f) flatRadial = Vector3.right;
            flatRadial.Normalize();

            float curtain = Mathf.SmoothStep(0.12f, 1f, normalizedRadius);
            float rim = Mathf.SmoothStep(0.35f, 1f, normalizedRadius);
            float lift = Mathf.SmoothStep(0.18f, 0.78f, normalizedRadius);
            if (normalizedRadius < 0.25f) lift *= centralLiftSuppression;

            Vector3 v = radial * (impactImpulse * horizontalEjectaBias * curtain * falloff * 0.24f);
            v += flatRadial * (impactImpulse * rimEjectaBoost * rim * falloff * 0.20f);
            v += Vector3.up * (impactImpulse * upwardBias * lift * falloff * 0.26f);
            return v;
        }

        private void RemoveParticleAtSwap(int index)
        {
            int last = particles.Count - 1;
            if (index < 0 || index > last) return;
            if (index != last) particles[index] = particles[last];
            particles.RemoveAt(last);
        }

        private int SolidifyParticles()
        {
            int solidified = 0;
            int checkedParticles = 0;
            int maxChecks = Mathf.Max(64, maxSolidifyChecksPerFrame);
            int maxSolidify = Mathf.Max(1, maxSolidifyPerFrame);
            bool tailForcedDeposit = IsTailDepositModeActive();
            bool batchDeposits = useBatchedParticleDeposition && Mathf.Max(1, depositionBatchFrames) > 1;

            if (particles.Count == 0)
            {
                solidifyScanCursor = -1;
                return 0;
            }

            if (solidifyScanCursor < 0 || solidifyScanCursor >= particles.Count)
                solidifyScanCursor = particles.Count - 1;

            int i = solidifyScanCursor;
            while (particles.Count > 0 && checkedParticles < maxChecks && solidified < maxSolidify)
            {
                if (i >= particles.Count) i = particles.Count - 1;
                if (i < 0) i = particles.Count - 1;

                SPHParticle3D p = particles[i];
                checkedParticles++;

                if (p == null || !p.active)
                {
                    RemoveParticleAtSwap(i);
                    i--;
                    continue;
                }

                float speed = p.velocity.magnitude;
                bool normal = p.age >= solidifyMinAge && p.temperature <= solidifyTemperature && speed <= solidifySpeed && p.recentGroundContact > 0f;
                bool center = IsCenterCaptureAllowed(p, speed);
                bool rim = IsRimCaptureAllowed(p, speed);
                bool forced = tailForcedDeposit || IsForcedDepositAllowed(p, speed);

                if (normal || center || rim || forced)
                {
                    Vector3Int deposit;
                    if (FindDepositCell(p.position, rim, center, forced, out deposit))
                    {
                        int placedVoxels;
                        float depositTemperature = Mathf.Min(p.temperature, sculptRimTemperature);
                        if (batchDeposits)
                        {
                            QueuePendingDeposit(deposit, depositTemperature, rim, center, forced);
                            placedVoxels = 1;
                        }
                        else
                        {
                            placedVoxels = DepositParticleMaterial(deposit, depositTemperature, rim, center, forced);
                            if (placedVoxels > 0)
                            {
                                terrainDirty = true;
                                gpuTerrainDirty = true;
                            }
                        }

                        if (!removeParticleOnlyAfterVisibleVoxelDeposit || placedVoxels > 0)
                        {
                            if (UseGpuSimulation && p.gpuIndex >= 0) pendingGpuDeactivateIndices.Add(p.gpuIndex);
                            RemoveParticleAtSwap(i);
                            solidified++;
                            i--;
                            continue;
                        }
                    }
                }

                i--;
            }

            solidifyScanCursor = i;
            return solidified;
        }

        private int DepositParticleMaterial(Vector3Int mainCell, float temperature, bool rim, bool center, bool forced)
        {
            if (terrain == null) return 0;

            int placed = 0;
            int footprint = forced ? Mathf.Clamp(forcedDepositFootprintRadiusCells, 0, 1) : 0;
            float temp = Mathf.Max(1f, temperature);

            for (int dz = -footprint; dz <= footprint; dz++)
            {
                for (int dx = -footprint; dx <= footprint; dx++)
                {
                    if (Mathf.Abs(dx) + Mathf.Abs(dz) > footprint) continue;

                    int x = mainCell.x + dx;
                    int z = mainCell.z + dz;
                    if (x < 0 || x >= terrain.Width || z < 0 || z >= terrain.Depth) continue;

                    int y;
                    if (dx == 0 && dz == 0)
                    {
                        y = mainCell.y;
                    }
                    else
                    {
                        int top = terrain.TopSolidY(x, z);
                        if (top < 0 || top >= terrain.Height - 1) continue;
                        y = top + 1;
                    }

                    if (!terrain.InBounds(x, y, z) || terrain.IsSolid(x, y, z)) continue;

                    terrain.SetSolid(x, y, z, true, temp, 0f, 0.1f, true);
                    placed++;
                }
            }

            return placed;
        }

        private bool IsCenterCaptureAllowed(SPHParticle3D p, float speed)
        {
            if (!centerCaptureEnabled || !hasImpact || p.recentGroundContact <= 0f || p.age < centerCaptureMinAge) return false;
            Vector3 flat = new Vector3(p.position.x - lastImpactCenter.x, 0f, p.position.z - lastImpactCenter.z);
            float r = flat.magnitude;
            if (r > lastImpactRadius * centerCaptureRadiusFactor) return false;
            if (speed > centerCaptureMaxSpeed) return false;
            return p.temperature <= solidifyTemperature + centerCaptureTemperatureBonus;
        }

        private bool IsRimCaptureAllowed(SPHParticle3D p, float speed)
        {
            if (!rimCaptureEnabled || !hasImpact || p.recentGroundContact <= 0f || p.age < rimCaptureMinAge) return false;
            Vector3 flat = new Vector3(p.position.x - lastImpactCenter.x, 0f, p.position.z - lastImpactCenter.z);
            float r = flat.magnitude;
            if (r < lastImpactRadius * rimCaptureStartRadiusFactor || r > lastImpactRadius * rimCaptureEndRadiusFactor) return false;
            if (speed > rimCaptureMaxSpeed) return false;
            return p.temperature <= solidifyTemperature + rimCaptureTemperatureBonus;
        }

        private bool IsForcedDepositAllowed(SPHParticle3D p, float speed)
        {
            if (!forceDepositOldParticles || p == null || terrain == null) return false;
            if (p.age < forceDepositAge) return false;

            bool coldEnough = p.temperature <= solidifyTemperature + forceDepositTemperatureBonus;
            if (!coldEnough) return false;

            float speedLimit = Mathf.Max(forceDepositMaxSpeed, solidifySpeed);
            if (p.age >= forceDepositAge * 1.8f) speedLimit *= 1.75f;
            if (p.age >= forceDepositAge * 2.8f) speedLimit *= 2.50f;
            if (speed > speedLimit) return false;

            // recentGroundContact is short-lived and can expire between sparse GPU readbacks.
            // For old particles, also accept positions that are physically close to the top of the voxel terrain.
            return p.recentGroundContact > 0f || IsNearTerrainSurface(p.position, forceDepositGroundSnapDistanceCells);
        }

        private bool IsNearTerrainSurface(Vector3 position, float maxDistanceCells)
        {
            if (terrain == null) return false;

            Vector3Int c = terrain.WorldToCell(position);
            int radius = Mathf.Max(1, Mathf.CeilToInt(maxDistanceCells));
            float maxWorldDistance = Mathf.Max(cellSize, maxDistanceCells * cellSize);

            for (int z = c.z - radius; z <= c.z + radius; z++)
            {
                for (int x = c.x - radius; x <= c.x + radius; x++)
                {
                    if (x < 0 || x >= terrain.Width || z < 0 || z >= terrain.Depth) continue;

                    int top = terrain.TopSolidY(x, z);
                    if (top < 0) continue;

                    Vector3 surface = terrain.CellCenter(x, top, z);
                    surface.y = (top + 1) * terrain.CellSize;
                    float horizontal = Vector2.Distance(new Vector2(position.x, position.z), new Vector2(surface.x, surface.z));
                    if (horizontal > maxWorldDistance) continue;

                    float vertical = Mathf.Abs(position.y - surface.y);
                    if (vertical <= maxWorldDistance + horizontal * 0.20f && position.y >= surface.y - terrain.CellSize * 2.0f)
                        return true;
                }
            }

            return false;
        }

        private int TopSolidYInColumn(int x, int z)
        {
            return terrain != null ? terrain.TopSolidY(x, z) : -1;
        }

        private int MaxNeighbourTopY(int x, int z)
        {
            int maxY = -1;
            maxY = Mathf.Max(maxY, TopSolidYInColumn(x + 1, z));
            maxY = Mathf.Max(maxY, TopSolidYInColumn(x - 1, z));
            maxY = Mathf.Max(maxY, TopSolidYInColumn(x, z + 1));
            maxY = Mathf.Max(maxY, TopSolidYInColumn(x, z - 1));
            return maxY;
        }

        private bool TryNeighbourTopStats(int x, int z, out float averageTopY, out int minTopY, out int maxTopY, out int count)
        {
            int sum = 0;
            count = 0;
            minTopY = int.MaxValue;
            maxTopY = int.MinValue;

            for (int oz = -1; oz <= 1; oz++)
            {
                for (int ox = -1; ox <= 1; ox++)
                {
                    if (ox == 0 && oz == 0) continue;
                    int h = TopSolidYInColumn(x + ox, z + oz);
                    if (h < 0) continue;
                    sum += h;
                    count++;
                    if (h < minTopY) minTopY = h;
                    if (h > maxTopY) maxTopY = h;
                }
            }

            if (count <= 0)
            {
                averageTopY = -1f;
                minTopY = -1;
                maxTopY = -1;
                return false;
            }

            averageTopY = sum / (float)count;
            return true;
        }

        private bool FindDepositCell(Vector3 position, bool rim, bool center, bool forced, out Vector3Int best)
        {
            best = Vector3Int.zero;
            if (terrain == null) return false;

            Vector3Int c0 = terrain.WorldToCell(position);
            float bestScore = float.PositiveInfinity;
            int radius = Mathf.Max(1, forced ? Mathf.Max(depositSearchRadiusCells, forcedDepositSearchRadiusCells) : depositSearchRadiusCells);

            for (int z = c0.z - radius; z <= c0.z + radius; z++)
            {
                for (int x = c0.x - radius; x <= c0.x + radius; x++)
                {
                    if (x < 0 || x >= terrain.Width || z < 0 || z >= terrain.Depth) continue;

                    float dx = x - c0.x;
                    float dz = z - c0.z;
                    float horizontalCellDist = Mathf.Sqrt(dx * dx + dz * dz);
                    if (horizontalCellDist > radius + 0.01f) continue;

                    int columnTopY = terrain.TopSolidY(x, z);
                    if (columnTopY < 0 || columnTopY >= terrain.Height - 1) continue;

                    int y = columnTopY + 1;
                    if (!terrain.InBounds(x, y, z) || terrain.IsSolid(x, y, z)) continue;

                    int belowFootprintSupport = 0;
                    for (int oz = -1; oz <= 1; oz++)
                    {
                        for (int ox = -1; ox <= 1; ox++)
                        {
                            if (terrain.IsSolid(x + ox, y - 1, z + oz)) belowFootprintSupport++;
                        }
                    }
                    int requiredFootprint = forced ? 1 : (center ? Mathf.Max(1, minBelowFootprintSupport - 1) : Mathf.Max(1, minBelowFootprintSupport));
                    if (belowFootprintSupport < requiredFootprint) continue;

                    int neighbourTopY = MaxNeighbourTopY(x, z);
                    int allowedRiseAboveNeighbours = rim ? Mathf.Max(maxDepositRiseAboveNeighbours, ComputeRimDepositRiseLimit()) : Mathf.Max(0, maxDepositRiseAboveNeighbours);
                    if (forced) allowedRiseAboveNeighbours = Mathf.Max(allowedRiseAboveNeighbours, 2);
                    if (neighbourTopY >= 0 && y > neighbourTopY + allowedRiseAboveNeighbours) continue;

                    float averageNeighbourTopY;
                    int minNeighbourTopY;
                    int maxNeighbourTopY;
                    int neighbourTopCount;
                    bool hasNeighbourStats = TryNeighbourTopStats(x, z, out averageNeighbourTopY, out minNeighbourTopY, out maxNeighbourTopY, out neighbourTopCount);
                    float localProminence = hasNeighbourStats ? y - averageNeighbourTopY : 0f;
                    if (antiPillarDepositEnabled && hasNeighbourStats)
                    {
                        float allowedProminence = maxDepositProminenceAboveNeighbours;
                        if (rim) allowedProminence += rimProminenceAllowanceBonus;
                        if (center) allowedProminence += 0.15f;
                        if (forced) allowedProminence += 0.85f;
                        if (localProminence > allowedProminence) continue;
                    }

                    int sameLevelCardinalSupport = 0;
                    if (terrain.IsSolid(x + 1, y, z)) sameLevelCardinalSupport++;
                    if (terrain.IsSolid(x - 1, y, z)) sameLevelCardinalSupport++;
                    if (terrain.IsSolid(x, y, z + 1)) sameLevelCardinalSupport++;
                    if (terrain.IsSolid(x, y, z - 1)) sameLevelCardinalSupport++;

                    if (antiPillarDepositEnabled && hasNeighbourStats && localProminence > 0.25f && sameLevelCardinalSupport < Mathf.Max(0, minSameLevelNeighboursForProminentDeposit))
                    {
                        bool supportedRimStart = rim && belowFootprintSupport >= Mathf.Max(1, rimMinBelowFootprintSupport) && localProminence <= rimProminenceAllowanceBonus;
                        bool supportedForcedDeposit = forced && belowFootprintSupport >= 1 && localProminence <= maxDepositProminenceAboveNeighbours + 0.85f;
                        if (!supportedRimStart && !supportedForcedDeposit) continue;
                    }

                    Vector3 cp = terrain.CellCenter(x, y, z);
                    float verticalPenalty = Mathf.Abs(cp.y - position.y) * 0.35f;
                    float upwardPenalty = Mathf.Max(0f, cp.y - position.y) * 1.2f;
                    float score = horizontalCellDist * (forced ? 1.65f : 2.8f) + verticalPenalty + upwardPenalty;

                    score -= forced ? 0.80f : 0.25f;
                    score -= Mathf.Min(0.75f, (belowFootprintSupport - 1) * 0.10f);
                    score -= Mathf.Min(0.40f, sameLevelCardinalSupport * 0.10f);
                    if (antiPillarDepositEnabled && hasNeighbourStats)
                    {
                        float prominencePenaltyMul = forced ? 0.35f : 1f;
                        score += Mathf.Max(0f, localProminence) * antiPillarProminencePenalty * prominencePenaltyMul;
                        if (sameLevelCardinalSupport == 0) score += forced ? 0.45f : 1.4f;
                    }

                    if (hasImpact)
                    {
                        Vector3 flat = new Vector3(cp.x - lastImpactCenter.x, 0f, cp.z - lastImpactCenter.z);
                        float craterR = flat.magnitude;

                        if (center)
                        {
                            score += craterR * centerDepositBias;
                            score += Mathf.Max(0f, cp.y - lastImpactCenter.y) * 0.7f;
                        }
                        else if (rim)
                        {
                            float targetR = Mathf.Max(cellSize, lastImpactRadius * rimDepositTargetRadiusFactor);
                            score += Mathf.Abs(craterR - targetR) * rimDepositTargetBias;
                            score -= craterR * outwardDepositBias;
                            score -= Mathf.Max(0f, cp.y - lastImpactCenter.y) * 0.55f;
                            if (cp.y < lastImpactCenter.y) score += 3.5f;
                        }
                    }

                    if (score < bestScore)
                    {
                        bestScore = score;
                        best = new Vector3Int(x, y, z);
                    }
                }
            }

            return bestScore < float.PositiveInfinity;
        }
    }
}
