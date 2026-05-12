using System.Collections.Generic;
using UnityEngine;

namespace MeteoriteSPH3D
{
    public sealed class MeteoriteSPH3DController : MonoBehaviour
    {
        public enum SliceAxis { X, Y, Z }

        public static MeteoriteSPH3DController Instance { get; private set; }

        [Header("Voxel terrain")]
        public int terrainWidth = 120;
        public int terrainHeight = 64;
        public int terrainDepth = 120;
        public float cellSize = 0.35f;
        public int baseHeight = 12;
        public bool useReliefTerrain = false;
        public int reliefAmplitudeCells = 22;
        public float reliefNoiseScale = 0.085f;
        public int reliefSeed = 23;

        [Header("GPU simulation")]
        public bool useGpuSimulation = true;
        public int gpuGridMaxParticlesPerCell = 160;
        public int gpuReadbackInterval = 4;
        [Tooltip("Upload modified voxel terrain back to the GPU only once per N solidification batches. Higher values reduce lag while particles are freezing.")]
        public int gpuTerrainUploadInterval = 3;
        [Tooltip("Rebuild the visible voxel mesh only once per N frames while particles are freezing. Higher values reduce lag during deposition.")]
        public int terrainMeshRebuildInterval = 6;

        [Header("Impact")]
        public float impactRadius = 8.60f;
        public float impactPressure = 470f;
        public float pressureThreshold = 52f;
        public float impactTemperature = 860f;
        public float meltTemperature = 260f;
        public float pressureTemperatureFalloffPower = 1.45f;
        public float damageThreshold = 0.56f;
        public float damageScale = 1.55f;
        public bool damageCanActivateVoxels = false;
        public float impactImpulse = 152f;
        public float horizontalEjectaBias = 12.40f;
        public float upwardBias = 0.92f;
        public float rimEjectaBoost = 9.20f;
        public float centralLiftSuppression = 0.42f;
        public float shockDepth = 0f;
        public bool useMouseClickAsImpactCenter = true;
        public float impactVerticalScale = 0.36f;
        [Tooltip("When true, the pressure/temperature impact field is an ellipsoid oriented by the locally estimated terrain normal at the click point.")]
        public bool impactUseEllipsoid = true;
        [Tooltip("Estimate the impact normal from neighbouring voxel heights instead of using mesh/raycast normals.")]
        public bool useLocalVoxelNormalForImpact = true;
        public int localNormalSampleRadiusCells = 3;
        [Tooltip("Debug only. When false, voxels are converted to particles only after pressure/temperature thresholds are exceeded.")]
        public bool impactActivateAllVoxelsInside = false;
        public int maxParticles = 220000;
        public int maxCreatedParticlesPerImpact = 90000;

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
        public float maxVelocity = 85.0f;
        public float maxAcceleration = 9000.0f;
        public float timeStep = 1f / 90f;
        public int substeps = 5;
        public float coolingRate = 2.2f;
        public float groundCoolingRate = 60f;
        public float extraWorldHeight = 10f;

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
        public int depositSearchRadiusCells = 5;
        public int requiredSolidNeighboursForDeposit = 1;
        public bool depositRequireBelowSupport = true;
        public int minBelowFootprintSupport = 3;
        public int maxDepositRiseAboveNeighbours = 1;
        [Tooltip("Reject/penalize deposits that would create isolated bumps above the local neighbour height. Useful on slopes to prevent small voxel pillars.")]
        public bool antiPillarDepositEnabled = true;
        public float maxDepositProminenceAboveNeighbours = 0.55f;
        public int minSameLevelNeighboursForProminentDeposit = 1;
        public float antiPillarProminencePenalty = 7.5f;
        public int maxSolidifyPerFrame = 120;
        [Tooltip("Maximum number of particles checked for solidification in one frame. Prevents full-list scans when many particles exist.")]
        public int maxSolidifyChecksPerFrame = 9000;

        [Header("Rim capture")]
        public bool rimCaptureEnabled = true;
        public float rimCaptureStartRadiusFactor = 0.42f;
        public float rimCaptureEndRadiusFactor = 2.10f;
        public float rimCaptureMaxSpeed = 4.60f;
        public float rimCaptureMinAge = 0.75f;
        public float rimCaptureTemperatureBonus = 170f;
        public float outwardDepositBias = 1.35f;
        [Tooltip("Where the raised rim is preferred relative to impact radius. Values below 1 keep the wall close to the crater edge.")]
        public float rimDepositTargetRadiusFactor = 0.92f;
        public float rimDepositTargetBias = 2.40f;
        public int rimMaxDepositRiseAboveNeighbours = 3;
        public float rimProminenceAllowanceBonus = 2.25f;
        public int rimMinBelowFootprintSupport = 5;

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
        private readonly SPHSolver3D solver = new SPHSolver3D();
        private readonly GPUSPH3DSolver gpuSolver = new GPUSPH3DSolver();
        private int readbackFrame;
        private int solidifyScanCursor = -1;
        private int terrainMeshDirtyFrames;
        private int gpuTerrainDirtyFrames;
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
            mainCamera.farClipPlane = 400f;
            camGo.tag = "MainCamera";
            cameraController = camGo.AddComponent<CameraController3D>();
            float cameraDistance = Mathf.Max(terrain.WorldWidth, terrain.WorldDepth) * 1.25f;
            cameraController.Initialize(new Vector3(terrain.WorldWidth * 0.5f, terrain.WorldHeight * 0.25f, terrain.WorldDepth * 0.5f), cameraDistance);

            SetupLighting();

            GameObject terrainGo = new GameObject("Voxel Terrain 3D");
            voxelRenderer = terrainGo.AddComponent<VoxelMeshRenderer3D>();
            voxelRenderer.Initialize();
            voxelRenderer.Rebuild(terrain);

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
            RenderSettings.ambientLight = new Color(0.40f, 0.43f, 0.48f, 1f);

            GameObject lightGo = new GameObject("Voxel Key Light");
            Light key = lightGo.AddComponent<Light>();
            key.type = LightType.Directional;
            key.color = new Color(1f, 0.97f, 0.92f, 1f);
            key.intensity = 1.55f;
            key.shadows = LightShadows.None;
            key.shadowStrength = 0f;
            lightGo.transform.rotation = Quaternion.Euler(54f, -48f, 0f);

            GameObject fillGo = new GameObject("Voxel Fill Light");
            Light fill = fillGo.AddComponent<Light>();
            fill.type = LightType.Directional;
            fill.color = new Color(0.72f, 0.82f, 1.0f, 1f);
            fill.intensity = 0.28f;
            fill.shadows = LightShadows.None;
            fillGo.transform.rotation = Quaternion.Euler(18f, 132f, 0f);
        }

        public void MarkTerrainDirty()
        {
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
            // Bigger crater preset: wider and stronger impact, still shallow vertically to avoid drilling to the bottom.
            // Does not force relief terrain, so flat-surface tests stay flat unless the menu toggle is enabled.
            useReliefTerrain = false;
            impactRadius = 8.60f;
            impactPressure = 470f;
            pressureThreshold = 52f;
            impactTemperature = 860f;
            meltTemperature = 260f;
            pressureTemperatureFalloffPower = 1.45f;
            damageThreshold = 0.56f;
            damageScale = 1.55f;
            damageCanActivateVoxels = false;
            impactImpulse = 152f;
            horizontalEjectaBias = 12.40f;
            upwardBias = 0.92f;
            centralLiftSuppression = 0.42f;
            rimEjectaBoost = 9.20f;
            shockDepth = 0f;
            useMouseClickAsImpactCenter = true;
            impactVerticalScale = 0.36f;
            impactUseEllipsoid = true;
            useLocalVoxelNormalForImpact = true;
            localNormalSampleRadiusCells = 3;
            impactActivateAllVoxelsInside = false;
            maxParticles = 220000;
            maxCreatedParticlesPerImpact = 90000;
            gpuGridMaxParticlesPerCell = 160;

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
            maxVelocity = 85.0f;
            maxAcceleration = 9000.0f;
            timeStep = 1f / 90f;
            substeps = 5;
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
            depositSearchRadiusCells = 5;
            requiredSolidNeighboursForDeposit = 1;
            depositRequireBelowSupport = true;
            minBelowFootprintSupport = 3;
            maxDepositRiseAboveNeighbours = 1;
            antiPillarDepositEnabled = true;
            maxDepositProminenceAboveNeighbours = 0.55f;
            minSameLevelNeighboursForProminentDeposit = 1;
            antiPillarProminencePenalty = 7.5f;
            maxSolidifyPerFrame = 120;
            maxSolidifyChecksPerFrame = 9000;
            gpuReadbackInterval = 4;
            gpuTerrainUploadInterval = 3;
            terrainMeshRebuildInterval = 6;

            rimCaptureEnabled = true;
            rimCaptureStartRadiusFactor = 0.42f;
            rimCaptureEndRadiusFactor = 2.10f;
            rimCaptureMaxSpeed = 4.60f;
            rimCaptureMinAge = 0.75f;
            rimCaptureTemperatureBonus = 170f;
            outwardDepositBias = 1.35f;
            rimDepositTargetRadiusFactor = 0.92f;
            rimDepositTargetBias = 2.40f;
            rimMaxDepositRiseAboveNeighbours = 3;
            rimProminenceAllowanceBonus = 2.25f;
            rimMinBelowFootprintSupport = 5;

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
                float dt = Mathf.Min(Time.deltaTime, 1f / 25f);
                float step = timeStep / Mathf.Max(1, substeps);
                int iterations = Mathf.Max(1, Mathf.CeilToInt(dt / timeStep));

                if (UseGpuSimulation)
                {
                    float simStartMs = Time.realtimeSinceStartup * 1000f;
                    for (int i = 0; i < iterations; i++)
                    {
                        for (int s = 0; s < Mathf.Max(1, substeps); s++)
                        {
                            gpuSolver.Step(this, terrain, step);
                        }
                    }
                    LastGpuSimulationMs += Time.realtimeSinceStartup * 1000f - simStartMs;

                    readbackFrame++;
                    if (readbackFrame >= Mathf.Max(1, gpuReadbackInterval))
                    {
                        readbackFrame = 0;

                        float readbackStartMs = Time.realtimeSinceStartup * 1000f;
                        gpuSolver.DownloadToParticles(particles);
                        LastGpuReadbackMs += Time.realtimeSinceStartup * 1000f - readbackStartMs;

                        int before = particles.Count;
                        float solidifyStartMs = Time.realtimeSinceStartup * 1000f;
                        int solidified = SolidifyParticles();
                        LastSolidifyMs += Time.realtimeSinceStartup * 1000f - solidifyStartMs;
                        LastSolidifiedParticles += solidified;
                        TotalSolidifiedParticles += solidified;

                        if (gpuTerrainDirty)
                        {
                            gpuTerrainDirtyFrames++;
                            if (gpuTerrainDirtyFrames >= Mathf.Max(1, gpuTerrainUploadInterval))
                            {
                                gpuTerrainDirtyFrames = 0;
                                gpuTerrainDirty = false;
                                float uploadTerrainStartMs = Time.realtimeSinceStartup * 1000f;
                                gpuSolver.UploadTerrain(terrain);
                                LastGpuTerrainUploadMs += Time.realtimeSinceStartup * 1000f - uploadTerrainStartMs;
                            }
                        }

                        if (particles.Count != before || solidified > 0)
                        {
                            float uploadParticlesStartMs = Time.realtimeSinceStartup * 1000f;
                            gpuSolver.UploadFromParticles(particles);
                            LastGpuParticleUploadMs += Time.realtimeSinceStartup * 1000f - uploadParticlesStartMs;
                        }
                    }
                }
                else
                {
                    float cpuSimStartMs = Time.realtimeSinceStartup * 1000f;
                    for (int i = 0; i < iterations; i++)
                    {
                        for (int s = 0; s < Mathf.Max(1, substeps); s++)
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
            }

            if (terrainDirty && voxelRenderer != null)
            {
                terrainMeshDirtyFrames++;
                if (terrainMeshDirtyFrames >= Mathf.Max(1, terrainMeshRebuildInterval))
                {
                    float meshStartMs = Time.realtimeSinceStartup * 1000f;
                    voxelRenderer.Rebuild(terrain);
                    LastMeshRebuildMs += Time.realtimeSinceStartup * 1000f - meshStartMs;
                    terrainDirty = false;
                    terrainMeshDirtyFrames = 0;
                }
            }

            if (particleRenderer != null)
            {
                particleRenderer.SetRadius(particleRadius);
            }

            LastControllerUpdateMs = Time.realtimeSinceStartup * 1000f - updateStartMs;
        }


        private void TryImpactFromMouse()
        {
            if (mainCamera == null) return;
            Ray ray = mainCamera.ScreenPointToRay(InputBridge3D.MousePosition());
            RaycastHit hit;
            if (Physics.Raycast(ray, out hit, 1000f))
            {
                ApplyImpact(hit.point);
            }
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

        private void ApplyImpact(Vector3 hitPoint)
        {
            Vector3 impactNormal = useLocalVoxelNormalForImpact ? EstimateLocalVoxelNormal(hitPoint) : Vector3.up;
            Vector3 tangentA;
            Vector3 tangentB;
            BuildImpactBasis(impactNormal, out tangentA, out tangentB);

            Vector3 center = useMouseClickAsImpactCenter ? hitPoint : hitPoint - impactNormal * shockDepth;
            lastImpactCenter = center;
            lastImpactRadius = impactRadius;
            hasImpact = true;

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

            LastCreatedParticles = created;
            TotalCreatedParticles += created;

            terrainDirty = true;
            terrainMeshDirtyFrames = terrainMeshRebuildInterval;
            if (useGpuSimulation && gpuSolver.IsReady)
            {
                float uploadTerrainStartMs = Time.realtimeSinceStartup * 1000f;
                gpuSolver.UploadTerrain(terrain);
                LastGpuTerrainUploadMs += Time.realtimeSinceStartup * 1000f - uploadTerrainStartMs;
                gpuTerrainDirty = false;
                gpuTerrainDirtyFrames = 0;

                float uploadParticlesStartMs = Time.realtimeSinceStartup * 1000f;
                gpuSolver.UploadFromParticles(particles);
                LastGpuParticleUploadMs += Time.realtimeSinceStartup * 1000f - uploadParticlesStartMs;
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

        private int SolidifyParticles()
        {
            int solidified = 0;
            int checkedParticles = 0;
            int maxChecks = Mathf.Max(64, maxSolidifyChecksPerFrame);
            int maxSolidify = Mathf.Max(1, maxSolidifyPerFrame);

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
                    particles.RemoveAt(i);
                    i--;
                    continue;
                }

                float speed = p.velocity.magnitude;
                bool normal = p.age >= solidifyMinAge && p.temperature <= solidifyTemperature && speed <= solidifySpeed && p.recentGroundContact > 0f;
                bool center = IsCenterCaptureAllowed(p, speed);
                bool rim = IsRimCaptureAllowed(p, speed);

                if (normal || center || rim)
                {
                    Vector3Int deposit;
                    if (FindDepositCell(p.position, rim, center, out deposit))
                    {
                        terrain.SetSolid(deposit.x, deposit.y, deposit.z, true, p.temperature, 0f, 0.1f, true);
                        particles.RemoveAt(i);
                        solidified++;
                        terrainDirty = true;
                        gpuTerrainDirty = true;
                        i--;
                        continue;
                    }
                }

                i--;
            }

            solidifyScanCursor = i;
            return solidified;
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

        private int TopSolidYInColumn(int x, int z)
        {
            if (x < 0 || x >= terrainWidth || z < 0 || z >= terrainDepth) return -1;
            for (int y = terrainHeight - 1; y >= 0; y--)
            {
                if (terrain.IsSolid(x, y, z)) return y;
            }
            return -1;
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

        private bool FindDepositCell(Vector3 position, bool rim, bool center, out Vector3Int best)
        {
            best = Vector3Int.zero;
            Vector3Int c0 = terrain.WorldToCell(position);
            float bestScore = float.PositiveInfinity;
            int radius = Mathf.Max(1, depositSearchRadiusCells);

            for (int z = c0.z - radius; z <= c0.z + radius; z++)
            {
                for (int x = c0.x - radius; x <= c0.x + radius; x++)
                {
                    float horizontalCellDist = Vector2.Distance(new Vector2(x, z), new Vector2(c0.x, c0.z));
                    if (horizontalCellDist > radius + 0.01f) continue;

                    for (int y = c0.y - radius; y <= c0.y + radius; y++)
                    {
                        if (!terrain.InBounds(x, y, z) || terrain.IsSolid(x, y, z)) continue;
                        if (!terrain.HasSupport(x, y, z, requiredSolidNeighboursForDeposit)) continue;

                        bool belowSupported = terrain.IsSolid(x, y - 1, z);
                        if (depositRequireBelowSupport && !belowSupported) continue;

                        int belowFootprintSupport = 0;
                        for (int oz = -1; oz <= 1; oz++)
                        {
                            for (int ox = -1; ox <= 1; ox++)
                            {
                                if (terrain.IsSolid(x + ox, y - 1, z + oz)) belowFootprintSupport++;
                            }
                        }
                        int requiredFootprint = center ? Mathf.Max(1, minBelowFootprintSupport - 1) : Mathf.Max(1, minBelowFootprintSupport);
                        if (belowFootprintSupport < requiredFootprint) continue;

                        int columnTopY = TopSolidYInColumn(x, z);
                        if (antiPillarDepositEnabled && columnTopY >= 0 && y != columnTopY + 1) continue;

                        int neighbourTopY = MaxNeighbourTopY(x, z);
                        int allowedRiseAboveNeighbours = rim ? Mathf.Max(maxDepositRiseAboveNeighbours, rimMaxDepositRiseAboveNeighbours) : Mathf.Max(0, maxDepositRiseAboveNeighbours);
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
                            if (!supportedRimStart) continue;
                        }

                        Vector3 cp = terrain.CellCenter(x, y, z);
                        float verticalPenalty = Mathf.Abs(cp.y - position.y) * 0.35f;
                        float upwardPenalty = Mathf.Max(0f, cp.y - position.y) * 1.2f;
                        float score = horizontalCellDist * 2.8f + verticalPenalty + upwardPenalty;

                        if (belowSupported) score -= 0.25f;
                        score -= Mathf.Min(0.75f, (belowFootprintSupport - 1) * 0.10f);
                        score -= Mathf.Min(0.40f, sameLevelCardinalSupport * 0.10f);
                        if (antiPillarDepositEnabled && hasNeighbourStats)
                        {
                            score += Mathf.Max(0f, localProminence) * antiPillarProminencePenalty;
                            if (sameLevelCardinalSupport == 0) score += 1.4f;
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
            }

            return bestScore < float.PositiveInfinity;
        }
    }
}
