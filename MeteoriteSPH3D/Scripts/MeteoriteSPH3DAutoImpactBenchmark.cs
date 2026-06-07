using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.Profiling;

namespace MeteoriteSPH3D
{
    public sealed class MeteoriteSPH3DAutoImpactBenchmark : MonoBehaviour
    {
        public enum MapScaleMode
        {
            PhysicalWorldSizeWithVoxelBudget,
            StrictVoxelCounts,
            CellSizeOnly
        }

        public enum TerrainHeightScaleMode
        {
            SameAsHorizontalScale,
            CraterDepthBudget
        }

        [Header("Auto benchmark")]
        public bool runOnStart = false;
        public KeyCode startBenchmarkKey = KeyCode.F8;
        public KeyCode stopBenchmarkKey = KeyCode.F7;
        public int impactsPerSize = 1;
        public float[] impactSizeScales = new float[] { 1f, 2.5f, 5f, 10f };
        [Tooltip("Fresh terrain is generated before every impact. This keeps all 10 runs of one size comparable.")]
        public bool resetTerrainBeforeEachImpact = true;
        [Tooltip("How terrain is scaled for large benchmark sizes. Strict mode scales X/Z exactly by impact size; Y can be handled separately by terrainHeightScaleMode.")]
        public MapScaleMode mapScaleMode = MapScaleMode.PhysicalWorldSizeWithVoxelBudget;
        [Tooltip("Hard voxel budget for benchmark terrain. The physical map/impact scale is preserved by increasing cellSize when the requested X/Z/Y allocation would exceed this budget.")]
        public int maxBenchmarkVoxelCount = 50000000;
        [Tooltip("If strict mode is selected manually and the requested terrain is over budget, skip instead of risking an OutOfMemoryException.")]
        public bool skipStrictScaleWhenOverBudget = true;
        [Tooltip("Always clamp generated benchmark terrain to maxBenchmarkVoxelCount before ResetSimulation(). This prevents OutOfMemoryException even when strict X/Z scale is selected.")]
        public bool clampTerrainToVoxelBudget = true;
        [Tooltip("For very large impacts, keep the physical terrain footprint capped while the crater/impact itself can keep growing. Example: x10 impact on an x5 map.")]
        public bool capPhysicalTerrainScale = true;
        [Tooltip("Maximum physical X/Z map scale used by the benchmark. x10 uses an x5 terrain when this is 5.")]
        public float maxPhysicalTerrainScale = 5f;
        [Tooltip("Use one fixed terrain size for all impact sizes. The impact radius/depth still changes by size_scale, but the map is always generated as fixedBenchmarkTerrainScale.")]
        public bool useFixedBenchmarkTerrainScaleForAllImpacts = true;
        [Tooltip("Physical terrain scale used for every benchmark impact when useFixedBenchmarkTerrainScaleForAllImpacts is enabled. Set to 5 to run x1/x1.5/x5 on the same x5 map.")]
        public float fixedBenchmarkTerrainScale = 5f;

        [Header("Benchmark particle load scaling")]
        [Tooltip("When OOM-safe scaling increases cellSize, compensate by emitting several particles per activated voxel. This prevents x5 from having the same particle count as x2 when both hit the voxel budget.")]
        public bool compensateParticleCountForCoarseGrid = true;
        [Tooltip("No extra copies are added while sizeScale / horizontalVoxelScale is below this value. Keeps x2 close to the normal algorithm when it barely hits the voxel budget.")]
        public float coarseGridParticleMultiplierDeadZone = 1.20f;
        [Tooltip("Normal upper limit for particle copies per activated voxel. High values can trigger Windows GPU TDR on large impacts.")]
        public int maxParticleCopiesPerActivatedVoxel = 4;
        [Tooltip("Stricter copy limit used when the map was clamped to the voxel budget. This prevents x5 from exploding into a GPU timeout workload.")]
        public int maxParticleCopiesPerActivatedVoxelWhenClamped = 2;

        [Header("GPU TDR safety")]
        [Tooltip("Protect benchmark runs from Windows GPU Timeout Detection and Recovery by capping large particle workloads and reducing substeps on big impacts.")]
        public bool tdrSafeBenchmarkMode = true;
        [Tooltip("Hard cap for particle buffer size during auto benchmark. Keeps compute dispatches below the level that can reset D3D11.")]
        public int hardMaxParticlesPerBenchmarkImpact = 700000;
        [Tooltip("Hard cap for particles created by one benchmark impact.")]
        public int hardMaxCreatedParticlesPerBenchmarkImpact = 650000;
        [Tooltip("Hard cap for the GPU deposit candidate buffer during benchmark.")]
        public int hardGpuDepositCandidateCapacityBenchmark = 262144;
        [Tooltip("Enable cheaper GPU stepping at or above this size scale.")]
        public float tdrSafeScaleThreshold = 2f;
        public int tdrSafeSubsteps = 1;
        public int tdrSafeAdaptiveMediumSubsteps = 1;
        public int tdrSafeAdaptiveLowSubsteps = 1;
        public int tdrSafeMaxGpuSimulationIterationsPerFrame = 1;
        public int tdrSafeGpuGridMaxParticlesPerCell = 64;

        [Header("Scaled terrain height")]
        [Tooltip("SameAsHorizontalScale scales Y like X/Z. CraterDepthBudget scales Y only by the vertical crater depth and rim allowance, saving RAM for x5/x10 tests.")]
        public TerrainHeightScaleMode terrainHeightScaleMode = TerrainHeightScaleMode.CraterDepthBudget;
        [Tooltip("Safety margin above the estimated crater/rim depth, in cells.")]
        public int craterDepthHeightMarginCells = 28;
        [Tooltip("Minimum Y resolution used by CraterDepthBudget.")]
        public int minCraterDepthScaledHeight = 64;
        [Tooltip("Hard cap for Y resolution in CraterDepthBudget. X/Z can still scale strictly.")]
        public int maxCraterDepthScaledHeight = 320;
        [Tooltip("How much of the ellipsoid vertical radius must fit below the hit surface.")]
        public float craterDepthBelowSurfaceFactor = 1.20f;
        [Tooltip("Additional height allowance above the surface for the raised rim/deposited material, relative to impact radius.")]
        public float craterRimHeightAllowanceToRadius = 0.14f;
        public bool useHiddenDeferredVisualApply = true;
        public bool hideParticlesDuringBenchmark = true;
        [Tooltip("Benchmark-only mode: finish timing as soon as active particles reach zero. Do not download/commit the hidden terrain copy, do not rebuild the visible terrain, then move to the next run.")]
        public bool finishAtZeroParticlesWithoutVisualCommit = true;
        [Tooltip("Do not reframe camera or rebuild visible terrain between benchmark runs. The algorithm still resets/scales its internal terrain before each impact when requested.")]
        public bool keepVisibleEnvironmentUnchangedDuringBenchmark = true;
        public bool restoreFinalQualityForSummaryScreenshot = false;
        [Tooltip("For every impact-size group, render/commit the first run and save one screenshot of the crater. This keeps screenshots representative and avoids waiting for the last hidden run.")]
        public bool renderAndScreenshotFirstImpactInEachSeries = true;
        [Tooltip("If true, every benchmark run is rendered, committed and saved as a screenshot. Useful when you want visual output for every pass instead of only selected runs.")]
        public bool renderAndScreenshotEveryImpact = true;
        [Tooltip("Optional legacy behaviour: also render/commit the selected run of each size group.")]
        public bool renderAndScreenshotLastImpactInEachSeries = false;
        [Tooltip("Restore high-quality lighting/terrain settings before taking screenshots of the last run in each series.")]
        public bool restoreFinalQualityForLastImpactScreenshot = true;
        [Tooltip("How many frames to wait after the final terrain commit before taking the screenshot.")]
        public int screenshotDelayFramesAfterCommit = 3;
        [Header("Screenshot camera")]
        public bool reframeCameraBeforeFinalScreenshot = true;
        [Tooltip("Place the screenshot camera above a terrain corner and look at the crater/map center. This makes x5/x10 screenshots show the whole crater instead of a close edge crop.")]
        public bool screenshotFromTerrainCorner = true;
        [Tooltip("Corner side for X: -1 = min X corner, +1 = max X corner.")]
        public int screenshotCornerXSign = -1;
        [Tooltip("Corner side for Z: -1 = min Z corner, +1 = max Z corner.")]
        public int screenshotCornerZSign = -1;
        [Tooltip("How far outside the selected corner the camera is placed, relative to the larger map side.")]
        public float screenshotCornerOutsetToMapSpan = 0.0f;
        [Tooltip("Camera height above the target, relative to the larger map side.")]
        public float screenshotCornerHeightToMapSpan = 0.85f;
        [Tooltip("If true, screenshot target is the impact point. If false, target is exact map center.")]
        public bool screenshotCornerLookAtImpact = false;
        [Tooltip("Use orthographic camera for benchmark screenshots. This guarantees that large x5/x10 maps fit into the image from the selected corner.")]
        public bool useOrthographicCameraForScreenshots = true;
        [Tooltip("Orthographic size is computed from map diagonal multiplied by this factor. Increase if screenshots still crop map edges.")]
        public float screenshotOrthographicSizeToMapDiagonal = 0.72f;
        [Tooltip("Extra multiplier for orthographic screenshot framing.")]
        public float screenshotOrthographicPadding = 1.08f;
        [Tooltip("Fit screenshot camera by X/Z ground footprint instead of full 3D terrain height. This makes the map closer and more detailed.")]
        public bool screenshotFitGroundFootprintOnly = true;
        [Tooltip("Zoom factor after fitting the ground footprint. Values below 1 crop map corners slightly and make the crater more readable.")]
        public float screenshotCornerCropForDetail = 0.86f;
        [Tooltip("For large maps like x5, lift the corner camera extra high so the crater stays visible instead of the terrain wall dominating the frame.")]
        public float largeMapCornerHeightToDiagonal = 1.00f;
        [Tooltip("If size scale is at least this threshold, use the extra-tall corner framing for screenshots.")]
        public float largeMapScreenshotScaleThreshold = 4.5f;
        [Tooltip("Extra orthographic framing for large maps like x5 so the whole field fits into one screenshot.")]
        public float largeMapOrthoSizeToDiagonal = 0.80f;
        public float screenshotCameraDistanceToImpactRadius = 4.0f;
        public float screenshotCameraMinDistance = 48f;
        public float screenshotCameraYaw = 45f;
        public float screenshotCameraPitch = 42f;
        public float screenshotCameraTargetHeightToRadius = 0.12f;
        public string screenshotFilePrefix = "final_crater";
        [Tooltip("Use a dedicated temporary camera for benchmark screenshots instead of capturing the current Game view. This avoids CameraController/editor interference that caused corner-only screenshots.")]
        public bool useDedicatedRenderCameraForScreenshots = true;
        [Tooltip("Width of saved benchmark screenshots.")]
        public int screenshotWidth = 1920;
        [Tooltip("Height of saved benchmark screenshots.")]
        public int screenshotHeight = 1080;
        public int zeroActiveFramesToFinish = 1;
        [Header("Tiny tail safety")]
        [Tooltip("If the run is stuck on a tiny tail of particles, finish them forcibly instead of timing out. This is for benchmark stability; the ignored particles are visually negligible.")]
        public bool forceFinishTinyTail = true;
        [Tooltip("Maximum active particles that can be forcibly cleared after tinyTailMaxSeconds in tail mode.")]
        public int tinyTailMaxActiveParticles = 16;
        [Tooltip("Benchmark safety: if active particles are below this fraction of particles created by the current impact and stay there too long, finish them forcibly. Fixes x10 getting stuck on hundreds of particles after tail mode.")]
        public float stuckTailActiveFraction = 0.001f;
        [Tooltip("Minimum absolute particle count allowed for stuck-tail finish. The effective threshold is max(tinyTailMaxActiveParticles, createdParticles * stuckTailActiveFraction, stuckTailMinActiveParticles).") ]
        public int stuckTailMinActiveParticles = 512;
        [Tooltip("Seconds to wait in stuck-tail state before forcibly finishing remaining particles.")]
        public float stuckTailMaxSeconds = 10f;
        [Tooltip("How long the test may stay with <= tinyTailMaxActiveParticles before the remaining particles are cleared.")]
        public float tinyTailMaxSeconds = 6f;
        [Tooltip("Only apply the tiny-tail escape after the normal <1% tail mode has started.")]
        public bool tinyTailOnlyAfterTailMode = true;
        public float maxSecondsPerImpact = 120f;
        [Header("Timeout / retry policy")]
        [Tooltip("When enabled, each scale is run once. Extra attempts are started only if the previous attempt timed out.")]
        public bool retryOnlyTimedOutRuns = true;
        [Tooltip("Maximum attempts for one scale when retryOnlyTimedOutRuns is enabled. If the first run succeeds, no retries are made.")]
        public int maxAttemptsPerSizeWhenTimeout = 10;
        [Tooltip("Timeout for x10 and larger sizes, in seconds. 300 seconds = 5 minutes.")]
        public float x10MaxSecondsPerImpact = 300f;
        [Tooltip("Scale from which x10MaxSecondsPerImpact is used.")]
        public float x10TimeoutScaleThreshold = 9.5f;
        public float cooldownSecondsBetweenImpacts = 0.25f;
        public bool writePerFrameSamples = true;
        public bool disableContinuousBenchmarkRecorder = true;
        [Tooltip("Disable the old continuous benchmark component completely, so only this auto-impact benchmark runs.")]
        public bool disableLegacyBenchmarkComponent = true;
        public string outputFolderName = "MeteoriteSPH3D_Benchmark";

        [Header("Console progress")]
        public bool verboseConsoleProgress = true;
        [Tooltip("How often one running impact prints progress to Unity Console.")]
        public float consoleProgressIntervalSeconds = 2f;
        [Tooltip("Print one idle line on scene start, so it is clear that the benchmark component is alive and waiting for F8.")]
        public bool logIdleOnStart = true;

        public bool IsRunning { get { return routine != null; } }
        public string SummaryCsvPath { get; private set; }
        public string SamplesCsvPath { get; private set; }
        public string OutputRunFolderPath { get; private set; }

        private MeteoriteSPH3DController controller;
        private Coroutine routine;
        private StreamWriter summaryWriter;
        private StreamWriter samplesWriter;
        private readonly CultureInfo invariant = CultureInfo.InvariantCulture;
        private readonly FrameTiming[] frameTiming = new FrameTiming[1];

        private BaseConfig baseConfig;
        private bool hasBaseConfig;
        private float nextConsoleProgressTime;
        private int totalPlannedRuns;
        private int executedRunsSoFar;
        private bool lastRunTimedOut;

        private sealed class RunStats
        {
            public readonly List<float> cpuFrameMs = new List<float>(4096);
            public readonly List<float> gpuFrameMs = new List<float>(4096);
            public readonly List<float> frameMs = new List<float>(4096);
            public readonly List<float> controllerMs = new List<float>(4096);
            public readonly List<float> gpuSimMs = new List<float>(4096);
            public readonly List<float> cpuSimMs = new List<float>(4096);
            public readonly List<float> readbackMs = new List<float>(4096);
            public readonly List<float> solidifyMs = new List<float>(4096);
            public readonly List<float> terrainUploadMs = new List<float>(4096);
            public readonly List<float> meshRebuildMs = new List<float>(4096);
            public float maxAllocatedMb;
            public float maxReservedMb;
            public float maxMonoMb;
            public int frames;
        }

        private struct BaseConfig
        {
            public int terrainWidth;
            public int terrainHeight;
            public int terrainDepth;
            public int baseHeight;
            public int reliefAmplitudeCells;
            public float cellSize;
            public float extraWorldHeight;
            public float impactRadius;
            public float shockDepth;
            public float particleRadius;
            public float particleSpacing;
            public float smoothingRadius;
            public int maxParticles;
            public int maxCreatedParticlesPerImpact;
            public int gpuDepositCandidateCapacity;
            public int impactParticleCopiesPerActivatedVoxel;
            public int substeps;
            public int adaptiveMediumSubsteps;
            public int adaptiveLowSubsteps;
            public int maxGpuSimulationIterationsPerFrame;
            public int gpuGridMaxParticlesPerCell;
        }

        private void Awake()
        {
            Application.runInBackground = true;
            controller = GetComponent<MeteoriteSPH3DController>();
            if (controller == null) controller = FindExistingController();
            DisableLegacyBenchmarkIfNeeded();
        }

        private void DisableLegacyBenchmarkIfNeeded()
        {
            if (!disableLegacyBenchmarkComponent) return;
            MeteoriteSPH3DBenchmark legacy = GetComponent<MeteoriteSPH3DBenchmark>();
            if (legacy == null) return;

            if (legacy.IsRecording) legacy.StopRecording();
            legacy.recordOnStart = false;
            legacy.enabled = false;
            if (verboseConsoleProgress) Debug.Log("[MeteoriteSPH3D Benchmark] Старый фоновый benchmark отключён. Работает только AutoImpactBenchmark.");
        }

        private void Start()
        {
            if (runOnStart)
            {
                StartBenchmark();
            }
            else if (logIdleOnStart)
            {
                Debug.Log("[MeteoriteSPH3D Benchmark] Простой: компонент бенчмарка загружен, нажми F8 для запуска. F7 — остановка.");
            }
        }

        private void Update()
        {
            if (InputBridge3D.KeyDown(startBenchmarkKey)) StartBenchmark();
            if (InputBridge3D.KeyDown(stopBenchmarkKey)) StopBenchmark();
        }

        public void StartBenchmark()
        {
            DisableLegacyBenchmarkIfNeeded();
            if (routine != null) return;
            if (controller == null) controller = FindExistingController();
            if (controller == null)
            {
                Debug.LogError("MeteoriteSPH3D auto benchmark: controller not found.");
                return;
            }

            int sizeCount = impactSizeScales != null ? impactSizeScales.Length : 0;
            int plannedPerSize = retryOnlyTimedOutRuns ? Mathf.Max(1, maxAttemptsPerSizeWhenTimeout) : Mathf.Max(1, impactsPerSize);
            totalPlannedRuns = Mathf.Max(0, sizeCount) * plannedPerSize;
            executedRunsSoFar = 0;
            Debug.Log("[MeteoriteSPH3D Benchmark] Запущен. Размеры: x1, x1.5, x5. План: минимум " + sizeCount
                + ", максимум " + totalPlannedRuns
                + " ударов. Повторы запускаются только после timeout=" + retryOnlyTimedOutRuns + ". F7 — остановка.");
            routine = StartCoroutine(RunBenchmark());
        }

        public void StopBenchmark()
        {
            if (routine != null)
            {
                StopCoroutine(routine);
                routine = null;
            }
            CloseWriters();
            Debug.Log("MeteoriteSPH3D auto benchmark stopped.");
        }

        private IEnumerator RunBenchmark()
        {
            PrepareCsv();
            CaptureBaseConfig();

            if (verboseConsoleProgress)
            {
                Debug.Log("[MeteoriteSPH3D Benchmark] Стартовые параметры: terrain=" + baseConfig.terrainWidth + "x" + baseConfig.terrainHeight + "x" + baseConfig.terrainDepth
                    + ", cellSize=" + F(baseConfig.cellSize)
                    + ", impactRadius=" + F(baseConfig.impactRadius)
                    + ", maxParticles=" + baseConfig.maxParticles
                    + ", maxCreated=" + baseConfig.maxCreatedParticlesPerImpact + ".");
            }

            MeteoriteSPH3DBenchmark continuous = GetComponent<MeteoriteSPH3DBenchmark>();
            bool continuousWasRecording = continuous != null && continuous.IsRecording;
            if (disableContinuousBenchmarkRecorder && continuous != null && continuous.IsRecording)
            {
                continuous.StopRecording();
            }

            bool originalDefer = controller.deferVisualApplyUntilParticlesStop;
            bool originalHideParticles = controller.hideParticlesDuringDeferredApply;
            bool originalFinalQuality = controller.restoreHighQualityLightingWhenParticlesStop;
            bool originalTerrainFinalQuality = controller.restoreTerrainRenderWhenParticlesStop;
            bool originalDeferredCommitEnabled = controller.deferredVisualCommitEnabled;

            controller.deferVisualApplyUntilParticlesStop = useHiddenDeferredVisualApply;
            controller.hideParticlesDuringDeferredApply = hideParticlesDuringBenchmark;
            controller.deferredVisualCommitEnabled = !finishAtZeroParticlesWithoutVisualCommit;
            controller.restoreHighQualityLightingWhenParticlesStop = !finishAtZeroParticlesWithoutVisualCommit && restoreFinalQualityForSummaryScreenshot;
            controller.restoreTerrainRenderWhenParticlesStop = !finishAtZeroParticlesWithoutVisualCommit && restoreFinalQualityForSummaryScreenshot;

            if (verboseConsoleProgress && finishAtZeroParticlesWithoutVisualCommit)
            {
                Debug.Log("[MeteoriteSPH3D Benchmark] Режим без отрисовки: обычные повторы заканчиваются при active=0, финальный terrain/download/rebuild не выполняется, видимое окружение не меняется.");
            }
            if (verboseConsoleProgress && (renderAndScreenshotEveryImpact || renderAndScreenshotFirstImpactInEachSeries || renderAndScreenshotLastImpactInEachSeries))
            {
                Debug.Log("[MeteoriteSPH3D Benchmark] Скриншоты будут сохраняться в папку запуска: " + OutputRunFolderPath);
            }

            if (controller.IsPaused) controller.TogglePause();

            try
            {
                int maxAttemptsForOneSize = retryOnlyTimedOutRuns ? Mathf.Max(1, maxAttemptsPerSizeWhenTimeout) : Mathf.Max(1, impactsPerSize);
                List<int> runOrder = BuildScreenshotSizeOrderAscending();

                if (verboseConsoleProgress)
                {
                    Debug.Log("[MeteoriteSPH3D Benchmark] Порядок размеров: x1 → x1.5 → x5. "
                        + "Каждый размер запускается один раз; повторные попытки идут только если предыдущий проход ушёл в timeout. "
                        + "Для x10, если его вернуть в список, timeout=" + F(GetTimeoutSecondsForScale(10f)) + " с.");
                }

                for (int orderPos = 0; orderPos < runOrder.Count; orderPos++)
                {
                    int sizeIndex = runOrder[orderPos];
                    float scale = Mathf.Max(0.01f, impactSizeScales[sizeIndex]);
                    ScaleInfo scaleInfo;
                    bool canRun = ApplyScale(scale, out scaleInfo);
                    LogScaleInfo(sizeIndex, scale, scaleInfo);
                    if (!canRun)
                    {
                        WriteSkippedScale(sizeIndex, scale, scaleInfo);
                        continue;
                    }

                    int attempt = 1;
                    while (attempt <= maxAttemptsForOneSize)
                    {
                        bool renderThisRun = renderAndScreenshotEveryImpact || ShouldRenderSelectedImpactInSeries(attempt);
                        yield return StartCoroutine(RunConfiguredImpact(sizeIndex, scale, attempt, maxAttemptsForOneSize, scaleInfo, renderThisRun));

                        if (!retryOnlyTimedOutRuns) break;
                        if (!lastRunTimedOut) break;

                        attempt++;
                        if (attempt <= maxAttemptsForOneSize && verboseConsoleProgress)
                        {
                            Debug.LogWarning("[MeteoriteSPH3D Benchmark] Размер " + FormatScaleLabel(scale)
                                + " ушёл в timeout. Запускаю повторную попытку " + attempt + "/" + maxAttemptsForOneSize + ".");
                        }
                    }
                }
            }
            finally
            {
                RestoreBaseConfig();
                controller.deferVisualApplyUntilParticlesStop = originalDefer;
                controller.hideParticlesDuringDeferredApply = originalHideParticles;
                controller.deferredVisualCommitEnabled = originalDeferredCommitEnabled;
                controller.restoreHighQualityLightingWhenParticlesStop = originalFinalQuality;
                controller.restoreTerrainRenderWhenParticlesStop = originalTerrainFinalQuality;

                if (finishAtZeroParticlesWithoutVisualCommit)
                {
                    controller.CancelDeferredVisualApplyWithoutCommit();
                }
                if (!keepVisibleEnvironmentUnchangedDuringBenchmark)
                {
                    controller.ResetSimulation();
                    controller.ReframeCameraToTerrain();
                }
                CloseWriters();
                if (continuousWasRecording && continuous != null && !continuous.IsRecording) continuous.StartRecording();
                routine = null;
            }

            Debug.Log("MeteoriteSPH3D auto benchmark finished. Summary: " + SummaryCsvPath + " Samples: " + SamplesCsvPath);
        }

        private List<int> BuildScreenshotSizeOrderAscending()
        {
            List<int> indices = new List<int>();
            if (impactSizeScales == null) return indices;
            for (int i = 0; i < impactSizeScales.Length; i++) indices.Add(i);
            indices.Sort((a, b) =>
            {
                float sa = impactSizeScales[a];
                float sb = impactSizeScales[b];
                int cmp = sa.CompareTo(sb);
                return cmp != 0 ? cmp : a.CompareTo(b);
            });
            return indices;
        }

        private void LogScaleInfo(int sizeIndex, float scale, ScaleInfo scaleInfo)
        {
            if (!verboseConsoleProgress) return;
            Debug.Log("[MeteoriteSPH3D Benchmark] Группа " + (sizeIndex + 1) + "/" + impactSizeScales.Length
                + ": размер " + FormatScaleLabel(scale)
                + ", terrain=" + controller.terrainWidth + "x" + controller.terrainHeight + "x" + controller.terrainDepth
                + ", voxels=" + scaleInfo.targetVoxelCount
                + ", approxTerrainRAM=" + F(EstimateTerrainMemoryMb(scaleInfo.targetVoxelCount)) + " MB"
                + ", cellSize=" + F(controller.cellSize)
                + ", radius=" + F(controller.impactRadius)
                + ", terrainPhysicalScale=" + F(scaleInfo.terrainPhysicalScale)
                + ", mode=" + mapScaleMode
                + ", voxelScale=" + F(scaleInfo.appliedVoxelScale)
                + ", worldCellScale=" + F(scaleInfo.worldCellScale)
                + ", yScale=" + F(scaleInfo.verticalVoxelScale)
                + ", heightMode=" + terrainHeightScaleMode
                + (scaleInfo.clampedToBudget ? ", CLAMPED_TO_BUDGET" : string.Empty) + ".");
            if (scaleInfo.clampedToBudget)
            {
                Debug.LogWarning("[MeteoriteSPH3D Benchmark] Запрошенная карта была слишком большой. Сетка уменьшена под бюджет "
                    + maxBenchmarkVoxelCount + " вокселей, физический масштаб сохранён через cellSize=" + F(controller.cellSize) + ".");
            }
            if (scaleInfo.terrainPhysicalScale + 0.0001f < scale)
            {
                Debug.LogWarning("[MeteoriteSPH3D Benchmark] Размер удара " + FormatScaleLabel(scale)
                    + " считается на карте " + FormatScaleLabel(scaleInfo.terrainPhysicalScale)
                    + ". Это тест x10 на физическом размере карты x5.");
            }
            if (!scaleInfo.clampedToBudget && mapScaleMode == MapScaleMode.StrictVoxelCounts && scaleInfo.targetVoxelCount > 50000000L)
            {
                Debug.LogWarning("[MeteoriteSPH3D Benchmark] ВНИМАНИЕ: strict scaling без ограничения. Будет попытка создать "
                    + scaleInfo.targetVoxelCount + " вокселей. Для x5/x10 это может съесть память или уронить Unity.");
            }
        }

        private IEnumerator RunConfiguredImpact(int sizeIndex, float scale, int runIndex, int runsPerSize, ScaleInfo scaleInfo, bool renderSelectedRun)
        {
            if (resetTerrainBeforeEachImpact || runIndex == 1)
            {
                if (verboseConsoleProgress)
                    Debug.Log("[MeteoriteSPH3D Benchmark] Подготовка внутреннего terrain: размер " + FormatScaleLabel(scale) + ", удар " + runIndex + "/" + runsPerSize + ". Видимый mesh не обновляется.");
                controller.ResetSimulation();
                if (!keepVisibleEnvironmentUnchangedDuringBenchmark) controller.ReframeCameraToTerrain();
                if (!finishAtZeroParticlesWithoutVisualCommit || renderSelectedRun) yield return null;
            }

            controller.deferredVisualCommitEnabled = renderSelectedRun ? true : !finishAtZeroParticlesWithoutVisualCommit;
            controller.restoreHighQualityLightingWhenParticlesStop = renderSelectedRun && restoreFinalQualityForLastImpactScreenshot;
            controller.restoreTerrainRenderWhenParticlesStop = renderSelectedRun && restoreFinalQualityForLastImpactScreenshot;

            executedRunsSoFar++;
            yield return StartCoroutine(RunSingleImpact(sizeIndex, scale, runIndex, scaleInfo, renderSelectedRun, executedRunsSoFar));

            if (cooldownSecondsBetweenImpacts > 0f)
            {
                if (verboseConsoleProgress) Debug.Log("[MeteoriteSPH3D Benchmark] Пауза " + F(cooldownSecondsBetweenImpacts) + " с перед следующим ударом.");
                float cooldownStart = Time.realtimeSinceStartup;
                while (Time.realtimeSinceStartup - cooldownStart < cooldownSecondsBetweenImpacts)
                    yield return null;
            }
        }

        private IEnumerator RunSingleImpact(int sizeIndex, float sizeScale, int runIndex, ScaleInfo scaleInfo, bool renderFinalVisualAndScreenshot, int globalRunOrdinal)
        {
            Vector3 hit = controller.GetDefaultBenchmarkImpactPoint();
            int solidBefore = controller.SolidVoxelCount;
            int totalCreatedBefore = controller.TotalCreatedParticles;
            int totalSolidifiedBefore = controller.TotalSolidifiedParticles;
            int frameStart = Time.frameCount;
            float start = Time.realtimeSinceStartup;
            float simulationDoneTime = -1f;
            int simulationDoneFrame = -1;
            bool timedOut = false;
            int zeroFrames = 0;
            bool tinyTailForced = false;
            int tinyTailForcedActiveCount = 0;
            float tinyTailStartTime = -1f;
            float stuckTailStartTime = -1f;
            RunStats stats = new RunStats();
            float timeoutSeconds = GetTimeoutSecondsForScale(sizeScale);

            if (verboseConsoleProgress)
            {
                int globalRun = globalRunOrdinal;
                Debug.Log("[MeteoriteSPH3D Benchmark] Удар START " + globalRun + "/" + Mathf.Max(1, totalPlannedRuns)
                    + ": размер " + FormatScaleLabel(sizeScale)
                    + ", попытка " + runIndex
                    + ", timeout=" + F(timeoutSeconds) + "s"
                    + ", mode=" + (renderFinalVisualAndScreenshot ? "FINAL_RENDER_SCREENSHOT" : "HIDDEN_NO_COMMIT")
                    + ", hit=(" + F(hit.x) + ", " + F(hit.y) + ", " + F(hit.z) + ").");
            }

            nextConsoleProgressTime = Time.realtimeSinceStartup + Mathf.Max(0.25f, consoleProgressIntervalSeconds);
            FrameTimingManager.CaptureFrameTimings();
            controller.ApplyBenchmarkImpact(hit);
            yield return null;

            if (verboseConsoleProgress)
            {
                int createdAfterApply = controller.TotalCreatedParticles - totalCreatedBefore;
                Debug.Log("[MeteoriteSPH3D Benchmark] Удар применён: размер " + FormatScaleLabel(sizeScale)
                    + ", повтор " + runIndex
                    + ", создано частиц=" + createdAfterApply
                    + ", active=" + controller.ActiveParticleCount + ".");
            }

            while (true)
            {
                bool simulationAlreadyFinished = simulationDoneTime >= 0f;
                if (!simulationAlreadyFinished)
                {
                    SampleFrame(stats, sizeIndex, sizeScale, runIndex, start);
                    MaybeLogImpactProgress(sizeIndex, sizeScale, runIndex, globalRunOrdinal, start, stats);
                }

                if (!tinyTailForced && forceFinishTinyTail && controller.ActiveParticleCount > 0)
                {
                    int activeNow = controller.ActiveParticleCount;
                    int createdForRun = Mathf.Max(1, controller.TotalCreatedParticles - totalCreatedBefore);
                    int stuckTailThreshold = Mathf.Max(Mathf.Max(1, tinyTailMaxActiveParticles), stuckTailMinActiveParticles, Mathf.CeilToInt(createdForRun * Mathf.Max(0f, stuckTailActiveFraction)));
                    bool tinyTail = activeNow <= Mathf.Max(1, tinyTailMaxActiveParticles);
                    bool stuckTail = activeNow <= stuckTailThreshold;
                    bool tailModeReady = !tinyTailOnlyAfterTailMode || controller.IsTailDepositModeActive();

                    if ((tinyTail || stuckTail) && tailModeReady)
                    {
                        float waitSeconds = tinyTail ? tinyTailMaxSeconds : stuckTailMaxSeconds;
                        if (tinyTailStartTime < 0f)
                        {
                            tinyTailStartTime = Time.realtimeSinceStartup;
                            stuckTailStartTime = Time.realtimeSinceStartup;
                            if (verboseConsoleProgress)
                            {
                                Debug.Log("[MeteoriteSPH3D Benchmark] Tail finish watch: active=" + activeNow
                                    + ", created=" + createdForRun
                                    + ", threshold=" + stuckTailThreshold
                                    + ", размер " + FormatScaleLabel(sizeScale)
                                    + ", повтор " + runIndex
                                    + ". Если хвост не исчезнет за " + F(waitSeconds) + " с, он будет принудительно завершён.");
                            }
                        }

                        float tailElapsed = Time.realtimeSinceStartup - (tinyTail ? tinyTailStartTime : stuckTailStartTime);
                        if (tailElapsed >= Mathf.Max(0.1f, waitSeconds))
                        {
                            tinyTailForced = true;
                            tinyTailForcedActiveCount = activeNow;
                            controller.ForceFinishRemainingParticlesForBenchmark(renderFinalVisualAndScreenshot);
                            if (simulationDoneTime < 0f)
                            {
                                simulationDoneTime = Time.realtimeSinceStartup;
                                simulationDoneFrame = Time.frameCount;
                            }
                            if (verboseConsoleProgress)
                            {
                                Debug.LogWarning("[MeteoriteSPH3D Benchmark] Tail forced finish: active=" + tinyTailForcedActiveCount
                                    + ", created=" + createdForRun
                                    + ", threshold=" + stuckTailThreshold
                                    + ", размер " + FormatScaleLabel(sizeScale)
                                    + ", повтор " + runIndex
                                    + ". Это не timeout; оставшиеся частицы отброшены, чтобы не держать тест.");
                            }
                        }
                    }
                    else
                    {
                        tinyTailStartTime = -1f;
                        stuckTailStartTime = -1f;
                    }
                }

                bool particlesAreZero = controller.ActiveParticleCount <= 0;
                if (particlesAreZero && simulationDoneTime < 0f)
                {
                    simulationDoneTime = Time.realtimeSinceStartup;
                    simulationDoneFrame = Time.frameCount;
                    if (verboseConsoleProgress && renderFinalVisualAndScreenshot)
                        Debug.Log("[MeteoriteSPH3D Benchmark] Частицы = 0. Замер остановлен, выполняется только финальный visual commit + screenshot.");
                }
                bool finishedByZeroOnly = finishAtZeroParticlesWithoutVisualCommit && !renderFinalVisualAndScreenshot && particlesAreZero;
                bool finishedAfterVisualCommit = particlesAreZero && !controller.IsDeferredVisualApplyActive;
                if (finishedByZeroOnly || finishedAfterVisualCommit)
                    zeroFrames++;
                else
                    zeroFrames = 0;

                if (zeroFrames >= Mathf.Max(1, zeroActiveFramesToFinish)) break;

                if (Time.realtimeSinceStartup - start >= Mathf.Max(1f, timeoutSeconds))
                {
                    timedOut = true;
                    if (verboseConsoleProgress)
                        Debug.LogWarning("[MeteoriteSPH3D Benchmark] TIMEOUT: размер " + FormatScaleLabel(sizeScale)
                            + ", попытка " + runIndex
                            + ", timeoutLimit=" + F(timeoutSeconds) + "s"
                            + ", active=" + controller.ActiveParticleCount + ".");
                    break;
                }

                yield return null;
            }

            float wallTime = (simulationDoneTime >= 0f ? simulationDoneTime : Time.realtimeSinceStartup) - start;
            int frames = Mathf.Max(1, (simulationDoneFrame >= 0 ? simulationDoneFrame : Time.frameCount) - frameStart);
            int created = controller.TotalCreatedParticles - totalCreatedBefore;
            int solidified = controller.TotalSolidifiedParticles - totalSolidifiedBefore;
            int solidAfter = controller.SolidVoxelCount;

            lastRunTimedOut = timedOut;

            string screenshotPath = string.Empty;
            if (renderFinalVisualAndScreenshot && !timedOut)
            {
                yield return StartCoroutine(CaptureFinalScreenshot(sizeIndex, sizeScale, runIndex, hit, path => screenshotPath = path));
            }

            WriteSummary(sizeIndex, sizeScale, runIndex, scaleInfo, hit, wallTime, frames, created, solidified, solidBefore, solidAfter, timedOut, stats, screenshotPath);

            if (finishAtZeroParticlesWithoutVisualCommit && !renderFinalVisualAndScreenshot)
            {
                controller.CancelDeferredVisualApplyWithoutCommit();
                if (verboseConsoleProgress)
                    Debug.Log("[MeteoriteSPH3D Benchmark] Финальный visual apply пропущен: результат не скачивался и видимое окружение не менялось.");
            }
        }

        private void SampleFrame(RunStats stats, int sizeIndex, float sizeScale, int runIndex, float runStartTime)
        {
            FrameTimingManager.CaptureFrameTimings();
            uint count = FrameTimingManager.GetLatestTimings(1, frameTiming);

            float frameMs = Time.unscaledDeltaTime * 1000f;
            float cpuMs = frameMs;
            float gpuMs = 0f;
            if (count > 0)
            {
                cpuMs = (float)frameTiming[0].cpuFrameTime;
                gpuMs = (float)frameTiming[0].gpuFrameTime;
                if (cpuMs <= 0.0001f) cpuMs = frameMs;
            }

            stats.frames++;
            stats.frameMs.Add(frameMs);
            stats.cpuFrameMs.Add(cpuMs);
            if (gpuMs > 0.0001f) stats.gpuFrameMs.Add(gpuMs);
            stats.controllerMs.Add(controller.LastControllerUpdateMs);
            stats.gpuSimMs.Add(controller.LastGpuSimulationMs);
            stats.cpuSimMs.Add(controller.LastCpuSimulationMs);
            stats.readbackMs.Add(controller.LastGpuReadbackMs);
            stats.solidifyMs.Add(controller.LastSolidifyMs);
            stats.terrainUploadMs.Add(controller.LastGpuTerrainUploadMs);
            stats.meshRebuildMs.Add(controller.LastMeshRebuildMs);
            stats.maxAllocatedMb = Mathf.Max(stats.maxAllocatedMb, Profiler.GetTotalAllocatedMemoryLong() / (1024f * 1024f));
            stats.maxReservedMb = Mathf.Max(stats.maxReservedMb, Profiler.GetTotalReservedMemoryLong() / (1024f * 1024f));
            stats.maxMonoMb = Mathf.Max(stats.maxMonoMb, Profiler.GetMonoHeapSizeLong() / (1024f * 1024f));

            if (samplesWriter != null && writePerFrameSamples)
            {
                samplesWriter.Write(sizeIndex.ToString(invariant)); samplesWriter.Write(',');
                samplesWriter.Write(F(sizeScale)); samplesWriter.Write(',');
                samplesWriter.Write(runIndex.ToString(invariant)); samplesWriter.Write(',');
                samplesWriter.Write(F(Time.realtimeSinceStartup - runStartTime)); samplesWriter.Write(',');
                samplesWriter.Write(Time.frameCount.ToString(invariant)); samplesWriter.Write(',');
                samplesWriter.Write(F(frameMs)); samplesWriter.Write(',');
                samplesWriter.Write(F(cpuMs)); samplesWriter.Write(',');
                samplesWriter.Write(F(gpuMs)); samplesWriter.Write(',');
                samplesWriter.Write(controller.ActiveParticleCount.ToString(invariant)); samplesWriter.Write(',');
                samplesWriter.Write(controller.SolidVoxelCount.ToString(invariant)); samplesWriter.Write(',');
                samplesWriter.Write(F(controller.LastControllerUpdateMs)); samplesWriter.Write(',');
                samplesWriter.Write(F(controller.LastGpuSimulationMs)); samplesWriter.Write(',');
                samplesWriter.Write(F(controller.LastCpuSimulationMs)); samplesWriter.Write(',');
                samplesWriter.Write(F(controller.LastGpuReadbackMs)); samplesWriter.Write(',');
                samplesWriter.Write(F(controller.LastSolidifyMs)); samplesWriter.Write(',');
                samplesWriter.Write(F(controller.LastGpuTerrainUploadMs)); samplesWriter.Write(',');
                samplesWriter.Write(F(controller.LastGpuParticleUploadMs)); samplesWriter.Write(',');
                samplesWriter.WriteLine(F(controller.LastMeshRebuildMs));
            }
        }

        private void MaybeLogImpactProgress(int sizeIndex, float sizeScale, int runIndex, int globalRunOrdinal, float runStartTime, RunStats stats)
        {
            if (!verboseConsoleProgress) return;
            if (Time.realtimeSinceStartup < nextConsoleProgressTime) return;
            nextConsoleProgressTime = Time.realtimeSinceStartup + Mathf.Max(0.25f, consoleProgressIntervalSeconds);

            float elapsed = Time.realtimeSinceStartup - runStartTime;
            int globalRun = globalRunOrdinal;
            float fps = Time.unscaledDeltaTime > 0.00001f ? 1f / Time.unscaledDeltaTime : 0f;
            float cpuLast = stats.cpuFrameMs.Count > 0 ? stats.cpuFrameMs[stats.cpuFrameMs.Count - 1] : 0f;
            float gpuLast = stats.gpuFrameMs.Count > 0 ? stats.gpuFrameMs[stats.gpuFrameMs.Count - 1] : 0f;

            Debug.Log("[MeteoriteSPH3D Benchmark] Прогресс " + globalRun + "/" + Mathf.Max(1, totalPlannedRuns)
                + ": размер " + FormatScaleLabel(sizeScale)
                + ", попытка " + runIndex
                + ", t=" + F(elapsed) + "s"
                + ", active=" + controller.ActiveParticleCount
                + ", deferred=" + controller.IsDeferredVisualApplyActive
                + ", fps=" + F(fps)
                + ", CPU=" + F(cpuLast) + " ms"
                + ", GPU=" + F(gpuLast) + " ms"
                + ", simGPU=" + F(controller.LastGpuSimulationMs) + " ms"
                + ", readback=" + F(controller.LastGpuReadbackMs) + " ms"
                + ", mesh=" + F(controller.LastMeshRebuildMs) + " ms"
                + ".");
        }


        private float GetTimeoutSecondsForScale(float sizeScale)
        {
            float defaultTimeout = Mathf.Max(1f, maxSecondsPerImpact);
            float threshold = Mathf.Max(0.01f, x10TimeoutScaleThreshold);
            if (sizeScale >= threshold)
                return Mathf.Max(defaultTimeout, x10MaxSecondsPerImpact);
            return defaultTimeout;
        }

        private bool ShouldRenderSelectedImpactInSeries(int runIndex)
        {
            if (renderAndScreenshotEveryImpact) return true;
            bool firstRun = renderAndScreenshotFirstImpactInEachSeries && runIndex == 1;
            bool lastRun = renderAndScreenshotLastImpactInEachSeries && runIndex >= Mathf.Max(1, impactsPerSize);
            return firstRun || lastRun;
        }

        private Camera GetScreenshotCamera()
        {
            Camera cam = Camera.main;
            if (cam != null) return cam;

#if UNITY_2023_1_OR_NEWER
            return UnityEngine.Object.FindFirstObjectByType<Camera>();
#else
            return UnityEngine.Object.FindObjectOfType<Camera>();
#endif
        }

        private void SetupScreenshotCameraTransform(Camera cam, float sizeScale, Vector3 impactHitPoint, bool allowControllerSync)
        {
            if (cam == null)
            {
                Debug.LogWarning("[MeteoriteSPH3D Benchmark] Не нашёл Camera для скриншота.");
                return;
            }

            Vector3 impactTarget = impactHitPoint;
            if (float.IsNaN(impactTarget.x) || float.IsInfinity(impactTarget.x) || float.IsNaN(impactTarget.y) || float.IsInfinity(impactTarget.y) || float.IsNaN(impactTarget.z) || float.IsInfinity(impactTarget.z))
                impactTarget = controller != null ? controller.GetDefaultBenchmarkImpactPoint() : Vector3.zero;

            float width = controller != null ? controller.terrainWidth * controller.cellSize : 100f;
            float depth = controller != null ? controller.terrainDepth * controller.cellSize : 100f;
            float height = controller != null ? controller.terrainHeight * controller.cellSize : 80f;
            float mapSpan = Mathf.Max(width, depth);
            float radius = controller != null ? Mathf.Max(controller.LastImpactRadius, controller.impactRadius) : 20f;

            Vector3 mapCenter = new Vector3(width * 0.5f, impactTarget.y, depth * 0.5f);
            Vector3 target = screenshotCornerLookAtImpact ? impactTarget : mapCenter;
            target.y += radius * Mathf.Clamp(screenshotCameraTargetHeightToRadius, 0f, 1f);

            if (screenshotFromTerrainCorner)
            {
                float sx = screenshotCornerXSign < 0 ? -1f : 1f;
                float sz = screenshotCornerZSign < 0 ? -1f : 1f;
                float mapDiagonal = Mathf.Sqrt(width * width + depth * depth);
                bool largeMapFrame = sizeScale >= Mathf.Max(1f, largeMapScreenshotScaleThreshold);

                Vector3 viewDir = new Vector3(-sx * 0.78f, -1.18f, -sz * 0.78f).normalized;
                float cornerDistance = Mathf.Max(mapSpan * Mathf.Max(0.70f, screenshotCornerHeightToMapSpan), mapDiagonal * (largeMapFrame ? Mathf.Max(0.85f, largeMapCornerHeightToDiagonal) : 0.75f), height * 1.2f, screenshotCameraMinDistance);
                Vector3 pos = target - viewDir * cornerDistance;

                cam.transform.position = pos;
                cam.transform.rotation = Quaternion.LookRotation((target - pos).normalized, Vector3.up);
                cam.farClipPlane = Mathf.Max(cam.farClipPlane, mapDiagonal * 4f + height * 4f + radius * 3f + 200f);
                cam.nearClipPlane = 0.1f;

                if (useOrthographicCameraForScreenshots)
                {
                    cam.orthographic = true;

                    Vector3 right = cam.transform.right;
                    Vector3 up = cam.transform.up;
                    float footprintY = screenshotFitGroundFootprintOnly ? target.y : 0f;
                    Vector3[] boundsCorners = screenshotFitGroundFootprintOnly
                        ? new Vector3[4]
                        {
                            new Vector3(0f, footprintY, 0f),
                            new Vector3(width, footprintY, 0f),
                            new Vector3(0f, footprintY, depth),
                            new Vector3(width, footprintY, depth)
                        }
                        : new Vector3[8]
                        {
                            new Vector3(0f, 0f, 0f),
                            new Vector3(width, 0f, 0f),
                            new Vector3(0f, 0f, depth),
                            new Vector3(width, 0f, depth),
                            new Vector3(0f, height, 0f),
                            new Vector3(width, height, 0f),
                            new Vector3(0f, height, depth),
                            new Vector3(width, height, depth)
                        };

                    float halfWidthNeeded = 0f;
                    float halfHeightNeeded = 0f;
                    for (int i = 0; i < boundsCorners.Length; i++)
                    {
                        Vector3 rel = boundsCorners[i] - target;
                        halfWidthNeeded = Mathf.Max(halfWidthNeeded, Mathf.Abs(Vector3.Dot(rel, right)));
                        halfHeightNeeded = Mathf.Max(halfHeightNeeded, Mathf.Abs(Vector3.Dot(rel, up)));
                    }

                    float aspect = cam.aspect > 0.01f ? cam.aspect : (16f / 9f);
                    float fitByWidth = halfWidthNeeded / aspect;
                    float fitByHeight = halfHeightNeeded;
                    float padding = Mathf.Max(1.0f, screenshotOrthographicPadding) * (largeMapFrame ? 1.03f : 1.0f);
                    float cropForDetail = Mathf.Clamp(screenshotCornerCropForDetail, 0.55f, 1.05f);
                    cam.orthographicSize = Mathf.Max(screenshotCameraMinDistance * 0.25f, Mathf.Max(fitByWidth, fitByHeight) * padding * cropForDetail);
                }

                CameraController3D cameraController = allowControllerSync ? cam.GetComponent<CameraController3D>() : null;
                if (cameraController != null)
                {
                    Vector3 euler = cam.transform.rotation.eulerAngles;
                    float pitch = euler.x;
                    if (pitch > 180f) pitch -= 360f;
                    cameraController.yaw = euler.y;
                    cameraController.pitch = pitch;
                    cameraController.Initialize(target, Vector3.Distance(pos, target));
                    cam.transform.position = pos;
                    cam.transform.rotation = Quaternion.LookRotation((target - pos).normalized, Vector3.up);
                }

                if (verboseConsoleProgress)
                {
                    Debug.Log("[MeteoriteSPH3D Benchmark] Камера для скриншота поставлена над углом карты: размер "
                        + FormatScaleLabel(sizeScale)
                        + ", map=" + F(width) + "x" + F(depth)
                        + ", diagonal=" + F(mapDiagonal)
                        + ", target=(" + F(target.x) + ", " + F(target.y) + ", " + F(target.z) + ")"
                        + ", camPos=(" + F(cam.transform.position.x) + ", " + F(cam.transform.position.y) + ", " + F(cam.transform.position.z) + ")"
                        + ", orthographic=" + cam.orthographic
                        + ", orthoSize=" + F(cam.orthographicSize)
                        + ", crop=" + F(screenshotCornerCropForDetail)
                        + (screenshotFitGroundFootprintOnly ? ", fit=XZ" : ", fit=XYZ")
                        + (largeMapFrame ? ", largeMapFrame=ON" : ", largeMapFrame=OFF") + ".");
                }
                return;
            }

            float distance = Mathf.Max(screenshotCameraMinDistance, radius * Mathf.Max(1.0f, screenshotCameraDistanceToImpactRadius));
            float pitchFallback = Mathf.Clamp(screenshotCameraPitch, 10f, 82f);
            Quaternion rot = Quaternion.Euler(pitchFallback, screenshotCameraYaw, 0f);
            Vector3 fallbackPos = target - rot * Vector3.forward * distance;

            CameraController3D fallbackController = allowControllerSync ? cam.GetComponent<CameraController3D>() : null;
            if (fallbackController != null)
            {
                fallbackController.yaw = screenshotCameraYaw;
                fallbackController.pitch = pitchFallback;
                fallbackController.Initialize(target, distance);
            }

            cam.transform.position = fallbackPos;
            cam.transform.rotation = rot;
            cam.orthographic = false;
            cam.farClipPlane = Mathf.Max(cam.farClipPlane, distance * 4f + radius * 4f + 100f);

            if (verboseConsoleProgress)
            {
                Debug.Log("[MeteoriteSPH3D Benchmark] Камера для скриншота наведена на точку удара: размер "
                    + FormatScaleLabel(sizeScale)
                    + ", hit=(" + F(impactHitPoint.x) + ", " + F(impactHitPoint.y) + ", " + F(impactHitPoint.z) + ")"
                    + ", radius=" + F(radius) + ", distance=" + F(distance)
                    + ", camPos=(" + F(cam.transform.position.x) + ", " + F(cam.transform.position.y) + ", " + F(cam.transform.position.z) + ").");
            }
        }

        private void ReframeCameraForFinalScreenshot(float sizeScale, Vector3 impactHitPoint)
        {
            Camera cam = GetScreenshotCamera();
            if (cam == null)
            {
                Debug.LogWarning("[MeteoriteSPH3D Benchmark] Не нашёл активную Camera для скриншота. Проверь тег MainCamera.");
                return;
            }
            SetupScreenshotCameraTransform(cam, sizeScale, impactHitPoint, true);
        }

        private Texture2D RenderScreenshotWithDedicatedCamera(float sizeScale, Vector3 impactHitPoint)
        {
            Camera source = GetScreenshotCamera();
            GameObject go = new GameObject("BenchmarkScreenshotCamera_Temp");
            Camera cam = go.AddComponent<Camera>();
            if (source != null)
            {
                cam.CopyFrom(source);
                cam.targetDisplay = 0;
            }
            else
            {
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.backgroundColor = new Color(0.68f, 0.78f, 0.92f, 1f);
                cam.nearClipPlane = 0.1f;
                cam.farClipPlane = 5000f;
            }

            SetupScreenshotCameraTransform(cam, sizeScale, impactHitPoint, false);

            int width = Mathf.Max(256, screenshotWidth);
            int height = Mathf.Max(256, screenshotHeight);
            RenderTexture rt = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32);
            rt.Create();
            RenderTexture prev = RenderTexture.active;
            cam.targetTexture = rt;
            cam.Render();
            RenderTexture.active = rt;
            Texture2D tex = new Texture2D(width, height, TextureFormat.RGB24, false);
            tex.ReadPixels(new Rect(0, 0, width, height), 0, 0, false);
            tex.Apply(false, false);
            cam.targetTexture = null;
            RenderTexture.active = prev;
            rt.Release();
            UnityEngine.Object.DestroyImmediate(rt);
            UnityEngine.Object.DestroyImmediate(go);
            return tex;
        }

        private IEnumerator CaptureFinalScreenshot(int sizeIndex, float sizeScale, int runIndex, Vector3 impactHitPoint, Action<string> onSaved)
        {
            int delay = Mathf.Max(0, screenshotDelayFramesAfterCommit);
            for (int i = 0; i < delay; i++)
                yield return null;

            Texture2D screenshot = null;
            if (useDedicatedRenderCameraForScreenshots)
            {
                screenshot = RenderScreenshotWithDedicatedCamera(sizeScale, impactHitPoint);
            }
            else
            {
                if (reframeCameraBeforeFinalScreenshot)
                {
                    ReframeCameraForFinalScreenshot(sizeScale, impactHitPoint);
                    yield return null;
                    // Re-apply once on the actual capture frame. This prevents the editor camera/controller from leaving us on a map corner.
                    ReframeCameraForFinalScreenshot(sizeScale, impactHitPoint);
                }

                yield return new WaitForEndOfFrame();
                screenshot = ScreenCapture.CaptureScreenshotAsTexture();
            }

            string folder = string.IsNullOrEmpty(OutputRunFolderPath) ? Path.Combine(Application.dataPath, outputFolderName) : OutputRunFolderPath;
            Directory.CreateDirectory(folder);
            string safeScale = sizeScale.ToString("0.###", invariant).Replace('.', '_').Replace(',', '_');
            string path = Path.Combine(folder, screenshotFilePrefix + "_size_" + safeScale + "x_run_" + runIndex.ToString("00", invariant) + ".png");

            if (screenshot == null)
            {
                Debug.LogWarning("[MeteoriteSPH3D Benchmark] Не удалось сделать скриншот для размера " + FormatScaleLabel(sizeScale) + ", повтор " + runIndex + ".");
                if (onSaved != null) onSaved(string.Empty);
                yield break;
            }

            byte[] png = screenshot.EncodeToPNG();
            File.WriteAllBytes(path, png);
            Destroy(screenshot);

            if (onSaved != null) onSaved(path);
            Debug.Log("[MeteoriteSPH3D Benchmark] Скриншот удара сохранён: " + path + (useDedicatedRenderCameraForScreenshots ? " (dedicated camera)" : string.Empty));
        }

        private string FormatScaleLabel(float sizeScale)
        {
            string label = sizeScale.ToString("0.###", invariant) + "x";
            if (Mathf.Abs(sizeScale - 1f) < 0.0001f) label += " (текущий / простой)";
            return label;
        }

        private void PrepareCsv()
        {
            string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            string dir = Path.Combine(Application.dataPath, outputFolderName, stamp);
            Directory.CreateDirectory(dir);
            OutputRunFolderPath = dir;
            SummaryCsvPath = Path.Combine(dir, "auto_impact_summary.csv");
            SamplesCsvPath = Path.Combine(dir, "auto_impact_samples.csv");

            summaryWriter = new StreamWriter(SummaryCsvPath, false, new UTF8Encoding(false));
            summaryWriter.WriteLine("size_index,size_scale,run_index,map_scale_mode,terrain_physical_scale,applied_voxel_scale,horizontal_voxel_scale,vertical_voxel_scale,world_cell_scale,terrain_width,terrain_height,terrain_depth,cell_size,impact_radius,impact_particle_copies_per_voxel,max_particles,max_created_particles,created_particles,solidified_particles,solid_voxels_before,solid_voxels_after,wall_time_s,frames,timeout,cpu_frame_ms_avg,cpu_frame_ms_p95,cpu_frame_ms_max,gpu_frame_ms_avg,gpu_frame_ms_p95,gpu_frame_ms_max,frame_ms_avg,frame_ms_p95,frame_ms_max,controller_ms_avg,controller_ms_p95,controller_ms_max,gpu_sim_ms_avg,gpu_sim_ms_p95,gpu_sim_ms_max,cpu_sim_ms_avg,cpu_sim_ms_p95,cpu_sim_ms_max,readback_ms_avg,readback_ms_p95,readback_ms_max,solidify_ms_avg,solidify_ms_p95,solidify_ms_max,terrain_upload_ms_avg,terrain_upload_ms_p95,terrain_upload_ms_max,mesh_rebuild_ms_avg,mesh_rebuild_ms_p95,mesh_rebuild_ms_max,ram_allocated_mb_max,ram_reserved_mb_max,mono_heap_mb_max,gpu_memory_total_mb,hit_x,hit_y,hit_z,screenshot_path");

            if (writePerFrameSamples)
            {
                samplesWriter = new StreamWriter(SamplesCsvPath, false, new UTF8Encoding(false));
                samplesWriter.WriteLine("size_index,size_scale,run_index,time_s,frame,frame_ms,cpu_frame_ms,gpu_frame_ms,active_particles,solid_voxels,controller_update_ms,gpu_sim_ms,cpu_sim_ms,gpu_readback_ms,solidify_ms,gpu_terrain_upload_ms,gpu_particle_upload_ms,mesh_rebuild_ms");
            }

            Debug.Log("[MeteoriteSPH3D Benchmark] CSV открыт. F7 = stop. Summary: " + SummaryCsvPath + " Samples: " + SamplesCsvPath);
        }

        private void CloseWriters()
        {
            if (summaryWriter != null)
            {
                summaryWriter.Flush();
                summaryWriter.Dispose();
                summaryWriter = null;
            }
            if (samplesWriter != null)
            {
                samplesWriter.Flush();
                samplesWriter.Dispose();
                samplesWriter = null;
            }
#if UNITY_EDITOR
            UnityEditor.AssetDatabase.Refresh();
#endif
        }

        private void WriteSummary(int sizeIndex, float sizeScale, int runIndex, ScaleInfo scaleInfo, Vector3 hit, float wallTime, int frames, int created, int solidified, int solidBefore, int solidAfter, bool timedOut, RunStats stats, string screenshotPath)
        {
            if (summaryWriter == null) return;

            summaryWriter.Write(sizeIndex.ToString(invariant)); summaryWriter.Write(',');
            summaryWriter.Write(F(sizeScale)); summaryWriter.Write(',');
            summaryWriter.Write(runIndex.ToString(invariant)); summaryWriter.Write(',');
            summaryWriter.Write(mapScaleMode.ToString()); summaryWriter.Write(',');
            summaryWriter.Write(F(scaleInfo.terrainPhysicalScale)); summaryWriter.Write(',');
            summaryWriter.Write(F(scaleInfo.appliedVoxelScale)); summaryWriter.Write(',');
            summaryWriter.Write(F(scaleInfo.horizontalVoxelScale)); summaryWriter.Write(',');
            summaryWriter.Write(F(scaleInfo.verticalVoxelScale)); summaryWriter.Write(',');
            summaryWriter.Write(F(scaleInfo.worldCellScale)); summaryWriter.Write(',');
            summaryWriter.Write(controller.terrainWidth.ToString(invariant)); summaryWriter.Write(',');
            summaryWriter.Write(controller.terrainHeight.ToString(invariant)); summaryWriter.Write(',');
            summaryWriter.Write(controller.terrainDepth.ToString(invariant)); summaryWriter.Write(',');
            summaryWriter.Write(F(controller.cellSize)); summaryWriter.Write(',');
            summaryWriter.Write(F(controller.impactRadius)); summaryWriter.Write(',');
            summaryWriter.Write(controller.impactParticleCopiesPerActivatedVoxel.ToString(invariant)); summaryWriter.Write(',');
            summaryWriter.Write(controller.maxParticles.ToString(invariant)); summaryWriter.Write(',');
            summaryWriter.Write(controller.maxCreatedParticlesPerImpact.ToString(invariant)); summaryWriter.Write(',');
            summaryWriter.Write(created.ToString(invariant)); summaryWriter.Write(',');
            summaryWriter.Write(solidified.ToString(invariant)); summaryWriter.Write(',');
            summaryWriter.Write(solidBefore.ToString(invariant)); summaryWriter.Write(',');
            summaryWriter.Write(solidAfter.ToString(invariant)); summaryWriter.Write(',');
            summaryWriter.Write(F(wallTime)); summaryWriter.Write(',');
            summaryWriter.Write(frames.ToString(invariant)); summaryWriter.Write(',');
            summaryWriter.Write(timedOut ? "1" : "0"); summaryWriter.Write(',');
            WriteStatsTriple(summaryWriter, stats.cpuFrameMs); summaryWriter.Write(',');
            WriteStatsTriple(summaryWriter, stats.gpuFrameMs); summaryWriter.Write(',');
            WriteStatsTriple(summaryWriter, stats.frameMs); summaryWriter.Write(',');
            WriteStatsTriple(summaryWriter, stats.controllerMs); summaryWriter.Write(',');
            WriteStatsTriple(summaryWriter, stats.gpuSimMs); summaryWriter.Write(',');
            WriteStatsTriple(summaryWriter, stats.cpuSimMs); summaryWriter.Write(',');
            WriteStatsTriple(summaryWriter, stats.readbackMs); summaryWriter.Write(',');
            WriteStatsTriple(summaryWriter, stats.solidifyMs); summaryWriter.Write(',');
            WriteStatsTriple(summaryWriter, stats.terrainUploadMs); summaryWriter.Write(',');
            WriteStatsTriple(summaryWriter, stats.meshRebuildMs); summaryWriter.Write(',');
            summaryWriter.Write(F(stats.maxAllocatedMb)); summaryWriter.Write(',');
            summaryWriter.Write(F(stats.maxReservedMb)); summaryWriter.Write(',');
            summaryWriter.Write(F(stats.maxMonoMb)); summaryWriter.Write(',');
            summaryWriter.Write(SystemInfo.graphicsMemorySize.ToString(invariant)); summaryWriter.Write(',');
            summaryWriter.Write(F(hit.x)); summaryWriter.Write(',');
            summaryWriter.Write(F(hit.y)); summaryWriter.Write(',');
            summaryWriter.Write(F(hit.z)); summaryWriter.Write(',');
            summaryWriter.WriteLine(CsvEscape(screenshotPath));
            summaryWriter.Flush();

            Debug.Log("[MeteoriteSPH3D Benchmark] Удар DONE: размер " + FormatScaleLabel(sizeScale)
                + ", попытка " + runIndex
                + ", time=" + wallTime.ToString("0.###", invariant) + "s"
                + ", frames=" + frames
                + ", created=" + created
                + ", solidified=" + solidified
                + ", CPU p95=" + FStatic(Percentile(stats.cpuFrameMs, 0.95f)) + " ms"
                + ", GPU p95=" + FStatic(Percentile(stats.gpuFrameMs, 0.95f)) + " ms"
                + ", timeout=" + timedOut + ".");
        }

        private void WriteSkippedScale(int sizeIndex, float sizeScale, ScaleInfo scaleInfo)
        {
            if (summaryWriter == null) return;
            summaryWriter.Write(sizeIndex.ToString(invariant)); summaryWriter.Write(',');
            summaryWriter.Write(F(sizeScale)); summaryWriter.Write(',');
            summaryWriter.Write("0,");
            summaryWriter.Write(mapScaleMode.ToString()); summaryWriter.Write(',');
            summaryWriter.Write(F(scaleInfo.terrainPhysicalScale)); summaryWriter.Write(',');
            summaryWriter.Write(F(scaleInfo.appliedVoxelScale)); summaryWriter.Write(',');
            summaryWriter.Write(F(scaleInfo.horizontalVoxelScale)); summaryWriter.Write(',');
            summaryWriter.Write(F(scaleInfo.verticalVoxelScale)); summaryWriter.Write(',');
            summaryWriter.Write(F(scaleInfo.worldCellScale)); summaryWriter.Write(',');
            summaryWriter.WriteLine("0,0,0,0,0,0,0,0,0,0,0,0,0,0,1,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0");
            summaryWriter.Flush();
            Debug.LogWarning("Benchmark scale skipped by voxel budget: " + sizeScale.ToString("0.###", invariant));
        }

        private static void WriteStatsTriple(StreamWriter w, List<float> values)
        {
            w.Write(FStatic(Average(values))); w.Write(',');
            w.Write(FStatic(Percentile(values, 0.95f))); w.Write(',');
            w.Write(FStatic(Max(values)));
        }

        private void CaptureBaseConfig()
        {
            baseConfig.terrainWidth = controller.terrainWidth;
            baseConfig.terrainHeight = controller.terrainHeight;
            baseConfig.terrainDepth = controller.terrainDepth;
            baseConfig.baseHeight = controller.baseHeight;
            baseConfig.reliefAmplitudeCells = controller.reliefAmplitudeCells;
            baseConfig.cellSize = controller.cellSize;
            baseConfig.extraWorldHeight = controller.extraWorldHeight;
            baseConfig.impactRadius = controller.impactRadius;
            baseConfig.shockDepth = controller.shockDepth;
            baseConfig.particleRadius = controller.particleRadius;
            baseConfig.particleSpacing = controller.particleSpacing;
            baseConfig.smoothingRadius = controller.smoothingRadius;
            baseConfig.maxParticles = controller.maxParticles;
            baseConfig.maxCreatedParticlesPerImpact = controller.maxCreatedParticlesPerImpact;
            baseConfig.gpuDepositCandidateCapacity = controller.gpuDepositCandidateCapacity;
            baseConfig.impactParticleCopiesPerActivatedVoxel = controller.impactParticleCopiesPerActivatedVoxel;
            baseConfig.substeps = controller.substeps;
            baseConfig.adaptiveMediumSubsteps = controller.adaptiveMediumSubsteps;
            baseConfig.adaptiveLowSubsteps = controller.adaptiveLowSubsteps;
            baseConfig.maxGpuSimulationIterationsPerFrame = controller.maxGpuSimulationIterationsPerFrame;
            baseConfig.gpuGridMaxParticlesPerCell = controller.gpuGridMaxParticlesPerCell;
            hasBaseConfig = true;
        }

        private void RestoreBaseConfig()
        {
            if (!hasBaseConfig) return;
            ApplyBaseConfig(baseConfig);
        }

        private void ApplyBaseConfig(BaseConfig c)
        {
            controller.terrainWidth = c.terrainWidth;
            controller.terrainHeight = c.terrainHeight;
            controller.terrainDepth = c.terrainDepth;
            controller.baseHeight = c.baseHeight;
            controller.reliefAmplitudeCells = c.reliefAmplitudeCells;
            controller.cellSize = c.cellSize;
            controller.extraWorldHeight = c.extraWorldHeight;
            controller.impactRadius = c.impactRadius;
            controller.shockDepth = c.shockDepth;
            controller.particleRadius = c.particleRadius;
            controller.particleSpacing = c.particleSpacing;
            controller.smoothingRadius = c.smoothingRadius;
            controller.maxParticles = c.maxParticles;
            controller.maxCreatedParticlesPerImpact = c.maxCreatedParticlesPerImpact;
            controller.gpuDepositCandidateCapacity = c.gpuDepositCandidateCapacity;
            controller.impactParticleCopiesPerActivatedVoxel = c.impactParticleCopiesPerActivatedVoxel;
            controller.substeps = c.substeps;
            controller.adaptiveMediumSubsteps = c.adaptiveMediumSubsteps;
            controller.adaptiveLowSubsteps = c.adaptiveLowSubsteps;
            controller.maxGpuSimulationIterationsPerFrame = c.maxGpuSimulationIterationsPerFrame;
            controller.gpuGridMaxParticlesPerCell = c.gpuGridMaxParticlesPerCell;
        }

        private struct ScaleInfo
        {
            public float terrainPhysicalScale;
            public float appliedVoxelScale;
            public float horizontalVoxelScale;
            public float verticalVoxelScale;
            public float worldCellScale;
            public long targetVoxelCount;
            public bool overBudget;
            public bool clampedToBudget;
        }

        private bool ApplyScale(float sizeScale, out ScaleInfo info)
        {
            if (!hasBaseConfig) CaptureBaseConfig();
            // Restore the original per-scale settings before applying a new benchmark group.
            // Without this, the x5 TDR-safe settings would silently leak into x2/x1 groups.
            ApplyBaseConfig(baseConfig);

            long baseVoxels = (long)baseConfig.terrainWidth * baseConfig.terrainHeight * baseConfig.terrainDepth;
            float requestedTerrainScale = useFixedBenchmarkTerrainScaleForAllImpacts
                ? Mathf.Max(0.01f, fixedBenchmarkTerrainScale)
                : sizeScale;
            float terrainPhysicalScale = capPhysicalTerrainScale
                ? Mathf.Min(requestedTerrainScale, Mathf.Max(0.01f, maxPhysicalTerrainScale))
                : requestedTerrainScale;
            float horizontalVoxelScale = 1f;
            float verticalVoxelScale = 1f;
            float appliedVoxelScale = 1f;
            float worldCellScale = sizeScale;
            bool clampedToBudget = false;
            int budget = Mathf.Max(1, maxBenchmarkVoxelCount);

            if (mapScaleMode == MapScaleMode.StrictVoxelCounts)
            {
                horizontalVoxelScale = terrainPhysicalScale;
                verticalVoxelScale = terrainPhysicalScale;
                worldCellScale = 1f;
            }
            else if (mapScaleMode == MapScaleMode.CellSizeOnly)
            {
                horizontalVoxelScale = 1f;
                verticalVoxelScale = 1f;
                worldCellScale = terrainPhysicalScale;
            }
            else
            {
                // First approximation. A second pass below clamps X/Z again after the final Y budget is known.
                float maxScaleByBudget = Mathf.Pow(Mathf.Max(1f, budget) / Mathf.Max(1f, (float)baseVoxels), 1f / 3f);
                horizontalVoxelScale = Mathf.Min(terrainPhysicalScale, Mathf.Max(1f, maxScaleByBudget));
                verticalVoxelScale = horizontalVoxelScale;
                worldCellScale = terrainPhysicalScale / Mathf.Max(0.0001f, horizontalVoxelScale);
            }

            int width;
            int depth;
            int height;
            int baseHeight;
            int reliefAmplitude;
            ComputeTerrainDimensions(terrainPhysicalScale, horizontalVoxelScale, verticalVoxelScale, worldCellScale, out width, out height, out depth, out baseHeight, out reliefAmplitude, out verticalVoxelScale, out appliedVoxelScale);

            long targetVoxels = (long)width * height * depth;
            bool overBudget = targetVoxels > budget;

            if (overBudget && mapScaleMode == MapScaleMode.StrictVoxelCounts && skipStrictScaleWhenOverBudget && !clampTerrainToVoxelBudget)
            {
                info = new ScaleInfo
                {
                    terrainPhysicalScale = terrainPhysicalScale,
                    appliedVoxelScale = appliedVoxelScale,
                    horizontalVoxelScale = horizontalVoxelScale,
                    verticalVoxelScale = verticalVoxelScale,
                    worldCellScale = worldCellScale,
                    targetVoxelCount = targetVoxels,
                    overBudget = true,
                    clampedToBudget = false
                };
                return false;
            }

            if (overBudget && clampTerrainToVoxelBudget)
            {
                clampedToBudget = true;

                // Keep the requested physical map/impact size, but reduce X/Z resolution and increase cellSize.
                // Y is then recomputed from the crater depth in cells, so the terrain stays just deep enough.
                for (int pass = 0; pass < 3; pass++)
                {
                    float maxHorizontalByBudget = Mathf.Sqrt(Mathf.Max(1f, budget) / Mathf.Max(1f, (float)baseConfig.terrainWidth * baseConfig.terrainDepth * Mathf.Max(8, height)));
                    horizontalVoxelScale = Mathf.Clamp(Mathf.Min(horizontalVoxelScale, maxHorizontalByBudget), 0.05f, Mathf.Max(0.05f, terrainPhysicalScale));
                    worldCellScale = terrainPhysicalScale / Mathf.Max(0.0001f, horizontalVoxelScale);

                    ComputeTerrainDimensions(terrainPhysicalScale, horizontalVoxelScale, verticalVoxelScale, worldCellScale, out width, out height, out depth, out baseHeight, out reliefAmplitude, out verticalVoxelScale, out appliedVoxelScale);
                    targetVoxels = (long)width * height * depth;
                    if (targetVoxels <= budget) break;
                }

                // Last-resort integer trim to guarantee VoxelTerrain3D constructor does not try an oversized allocation.
                while ((long)width * height * depth > budget && (width > 8 || depth > 8))
                {
                    if (width >= depth && width > 8) width--;
                    else if (depth > 8) depth--;
                    else break;
                }

                targetVoxels = (long)width * height * depth;
                overBudget = targetVoxels > budget;
            }

            info = new ScaleInfo
            {
                terrainPhysicalScale = terrainPhysicalScale,
                appliedVoxelScale = appliedVoxelScale,
                horizontalVoxelScale = horizontalVoxelScale,
                verticalVoxelScale = verticalVoxelScale,
                worldCellScale = worldCellScale,
                targetVoxelCount = targetVoxels,
                overBudget = overBudget,
                clampedToBudget = clampedToBudget
            };

            if (overBudget && mapScaleMode == MapScaleMode.StrictVoxelCounts && skipStrictScaleWhenOverBudget)
            {
                return false;
            }

            controller.terrainWidth = width;
            controller.terrainHeight = height;
            controller.terrainDepth = depth;
            controller.baseHeight = Mathf.Clamp(baseHeight, 1, height - 2);
            controller.reliefAmplitudeCells = Mathf.Clamp(reliefAmplitude, 1, Mathf.Max(1, height - 2));
            controller.cellSize = baseConfig.cellSize * worldCellScale;
            controller.extraWorldHeight = baseConfig.extraWorldHeight * sizeScale;
            controller.impactRadius = baseConfig.impactRadius * sizeScale;
            controller.shockDepth = baseConfig.shockDepth * sizeScale;
            controller.particleRadius = Mathf.Max(0.001f, baseConfig.particleRadius * worldCellScale);
            controller.particleSpacing = Mathf.Max(0.001f, baseConfig.particleSpacing * worldCellScale);
            controller.smoothingRadius = Mathf.Max(0.001f, baseConfig.smoothingRadius * worldCellScale);

            int particleCopiesPerVoxel = 1;
            if (compensateParticleCountForCoarseGrid)
            {
                float coarseRatio = sizeScale / Mathf.Max(0.0001f, horizontalVoxelScale);
                int copyLimit = clampedToBudget
                    ? Mathf.Max(1, maxParticleCopiesPerActivatedVoxelWhenClamped)
                    : Mathf.Max(1, maxParticleCopiesPerActivatedVoxel);
                if (coarseRatio > Mathf.Max(1.0f, coarseGridParticleMultiplierDeadZone))
                    particleCopiesPerVoxel = Mathf.Clamp(Mathf.CeilToInt(coarseRatio), 1, copyLimit);
            }

            float particleBudgetScale = Mathf.Max(1f, horizontalVoxelScale * horizontalVoxelScale * Mathf.Max(1f, verticalVoxelScale));

            // Prevent one very large hidden run from producing a D3D11 TDR. The previous x5 fix could create
            // several particles per voxel on an already large grid; one compute dispatch then became too expensive.
            int hardCreatedCap = Mathf.Max(baseConfig.maxCreatedParticlesPerImpact, hardMaxCreatedParticlesPerBenchmarkImpact);
            int hardParticleCap = Mathf.Max(baseConfig.maxParticles, hardMaxParticlesPerBenchmarkImpact);
            while (tdrSafeBenchmarkMode && particleCopiesPerVoxel > 1)
            {
                float projectedCreated = baseConfig.maxCreatedParticlesPerImpact * particleBudgetScale * particleCopiesPerVoxel;
                float projectedMaxParticles = baseConfig.maxParticles * particleBudgetScale * particleCopiesPerVoxel;
                if (projectedCreated <= hardCreatedCap && projectedMaxParticles <= hardParticleCap) break;
                particleCopiesPerVoxel--;
            }

            float particleLoadScale = particleBudgetScale * Mathf.Max(1, particleCopiesPerVoxel);
            int requestedMaxParticles = Mathf.Max(baseConfig.maxParticles, SafeRoundToInt(baseConfig.maxParticles * particleLoadScale));
            int requestedMaxCreated = Mathf.Max(baseConfig.maxCreatedParticlesPerImpact, SafeRoundToInt(baseConfig.maxCreatedParticlesPerImpact * particleLoadScale));
            int requestedCandidateCapacity = Mathf.Max(baseConfig.gpuDepositCandidateCapacity, SafeRoundToInt(baseConfig.gpuDepositCandidateCapacity * Mathf.Max(1f, horizontalVoxelScale * horizontalVoxelScale * Mathf.Max(1, particleCopiesPerVoxel))));

            if (tdrSafeBenchmarkMode)
            {
                requestedMaxParticles = Mathf.Min(requestedMaxParticles, hardParticleCap);
                requestedMaxCreated = Mathf.Min(requestedMaxCreated, hardCreatedCap);
                requestedCandidateCapacity = Mathf.Min(requestedCandidateCapacity, Mathf.Max(baseConfig.gpuDepositCandidateCapacity, hardGpuDepositCandidateCapacityBenchmark));

                if (sizeScale >= Mathf.Max(0.01f, tdrSafeScaleThreshold))
                {
                    controller.substeps = Mathf.Max(1, Mathf.Min(controller.substeps, tdrSafeSubsteps));
                    controller.adaptiveMediumSubsteps = Mathf.Max(1, Mathf.Min(controller.adaptiveMediumSubsteps, tdrSafeAdaptiveMediumSubsteps));
                    controller.adaptiveLowSubsteps = Mathf.Max(1, Mathf.Min(controller.adaptiveLowSubsteps, tdrSafeAdaptiveLowSubsteps));
                    controller.maxGpuSimulationIterationsPerFrame = Mathf.Max(1, Mathf.Min(controller.maxGpuSimulationIterationsPerFrame, tdrSafeMaxGpuSimulationIterationsPerFrame));
                    controller.gpuGridMaxParticlesPerCell = Mathf.Max(16, Mathf.Min(controller.gpuGridMaxParticlesPerCell, tdrSafeGpuGridMaxParticlesPerCell));
                }
            }

            controller.impactParticleCopiesPerActivatedVoxel = particleCopiesPerVoxel;
            controller.maxParticles = requestedMaxParticles;
            controller.maxCreatedParticlesPerImpact = requestedMaxCreated;
            controller.gpuDepositCandidateCapacity = requestedCandidateCapacity;

            if (verboseConsoleProgress && useFixedBenchmarkTerrainScaleForAllImpacts)
            {
                Debug.Log("[MeteoriteSPH3D Benchmark] Размер удара " + FormatScaleLabel(sizeScale)
                    + " выполняется на фиксированной карте " + FormatScaleLabel(terrainPhysicalScale)
                    + ". Радиус удара=" + F(controller.impactRadius)
                    + ", shockDepth=" + F(controller.shockDepth)
                    + ", terrain=" + controller.terrainWidth + "x" + controller.terrainHeight + "x" + controller.terrainDepth
                    + ", cellSize=" + F(controller.cellSize) + ".");
            }

            if (verboseConsoleProgress && particleCopiesPerVoxel > 1)
            {
                Debug.Log("[MeteoriteSPH3D Benchmark] Для размера " + FormatScaleLabel(sizeScale)
                    + " включена безопасная компенсация coarse grid: " + particleCopiesPerVoxel
                    + " частиц(ы) на активированный воксель. maxCreated=" + controller.maxCreatedParticlesPerImpact
                    + ", maxParticles=" + controller.maxParticles + ".");
            }
            else if (verboseConsoleProgress && tdrSafeBenchmarkMode && sizeScale >= Mathf.Max(0.01f, tdrSafeScaleThreshold))
            {
                Debug.Log("[MeteoriteSPH3D Benchmark] TDR-safe режим для " + FormatScaleLabel(sizeScale)
                    + ": substeps=" + controller.substeps
                    + ", maxGpuIterations=" + controller.maxGpuSimulationIterationsPerFrame
                    + ", gridCellCap=" + controller.gpuGridMaxParticlesPerCell
                    + ", maxCreated=" + controller.maxCreatedParticlesPerImpact
                    + ", maxParticles=" + controller.maxParticles + ".");
            }
            return true;
        }

        private void ComputeTerrainDimensions(float sizeScale, float horizontalVoxelScale, float verticalVoxelScale, float worldCellScale, out int width, out int height, out int depth, out int baseHeight, out int reliefAmplitude, out float finalVerticalVoxelScale, out float appliedVoxelScale)
        {
            width = Mathf.Max(8, Mathf.RoundToInt(baseConfig.terrainWidth * horizontalVoxelScale));
            depth = Mathf.Max(8, Mathf.RoundToInt(baseConfig.terrainDepth * horizontalVoxelScale));

            if (terrainHeightScaleMode == TerrainHeightScaleMode.CraterDepthBudget)
            {
                height = ComputeCraterDepthScaledHeight(sizeScale, worldCellScale, out baseHeight, out reliefAmplitude);
                finalVerticalVoxelScale = height / Mathf.Max(1f, (float)baseConfig.terrainHeight);
                appliedVoxelScale = horizontalVoxelScale;
            }
            else
            {
                height = Mathf.Max(8, Mathf.RoundToInt(baseConfig.terrainHeight * verticalVoxelScale));
                baseHeight = Mathf.Clamp(Mathf.RoundToInt(baseConfig.baseHeight * verticalVoxelScale), 1, height - 2);
                reliefAmplitude = Mathf.Max(1, Mathf.RoundToInt(baseConfig.reliefAmplitudeCells * verticalVoxelScale));
                finalVerticalVoxelScale = height / Mathf.Max(1f, (float)baseConfig.terrainHeight);
                appliedVoxelScale = horizontalVoxelScale;
            }
        }

        private int ComputeCraterDepthScaledHeight(float sizeScale, float worldCellScale, out int scaledBaseHeight, out int scaledReliefAmplitude)
        {
            float scaledCellSize = Mathf.Max(0.0001f, baseConfig.cellSize * worldCellScale);
            float scaledRadius = Mathf.Max(0.0001f, baseConfig.impactRadius * sizeScale);
            float verticalRadiusWorld = scaledRadius * Mathf.Clamp(controller.impactVerticalScale, 0.15f, 1.0f);
            float explicitDepthWorld = Mathf.Max(baseConfig.shockDepth * sizeScale, controller.autoScaleShockDepthWithRadius ? scaledRadius * Mathf.Max(0f, controller.shockDepthToRadiusRatio) : 0f);
            float requiredDepthWorld = Mathf.Max(verticalRadiusWorld, explicitDepthWorld);

            int belowSurfaceCells = Mathf.CeilToInt(requiredDepthWorld / scaledCellSize * Mathf.Max(0.25f, craterDepthBelowSurfaceFactor));
            int rimAllowanceCells = Mathf.CeilToInt(scaledRadius / scaledCellSize * Mathf.Max(0f, craterRimHeightAllowanceToRadius));
            int marginCells = Mathf.Max(4, craterDepthHeightMarginCells);

            scaledBaseHeight = Mathf.Max(baseConfig.baseHeight, belowSurfaceCells + marginCells / 2);
            scaledReliefAmplitude = Mathf.Max(1, Mathf.Min(baseConfig.reliefAmplitudeCells, Mathf.RoundToInt(rimAllowanceCells * 0.75f)));

            int wantedHeight = scaledBaseHeight + rimAllowanceCells + marginCells;
            int minHeight = Mathf.Max(8, minCraterDepthScaledHeight);
            int maxHeight = Mathf.Max(minHeight, maxCraterDepthScaledHeight);
            return Mathf.Clamp(wantedHeight, minHeight, maxHeight);
        }

        private static float EstimateTerrainMemoryMb(long voxelCount)
        {
            // Rough lower-bound estimate for the main voxel arrays/caches only. Real Unity/ComputeBuffer memory can be higher.
            double bytes = voxelCount * 8.0;
            return (float)(bytes / (1024.0 * 1024.0));
        }

        private static int SafeRoundToInt(float value)
        {
            if (float.IsNaN(value) || value <= 0f) return 1;
            if (value >= int.MaxValue) return int.MaxValue;
            return Mathf.RoundToInt(value);
        }

        private static MeteoriteSPH3DController FindExistingController()
        {
#if UNITY_2023_1_OR_NEWER
            return UnityEngine.Object.FindFirstObjectByType<MeteoriteSPH3DController>();
#else
            return UnityEngine.Object.FindObjectOfType<MeteoriteSPH3DController>();
#endif
        }


        private static string CsvEscape(string value)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;
            if (value.IndexOfAny(new char[] { ',', '\"', '\n', '\r' }) < 0) return value;
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        }

        private string F(float value)
        {
            return value.ToString("0.###", invariant);
        }

        private static string FStatic(float value)
        {
            return value.ToString("0.###", CultureInfo.InvariantCulture);
        }

        private static float Average(List<float> values)
        {
            if (values == null || values.Count == 0) return 0f;
            double sum = 0.0;
            for (int i = 0; i < values.Count; i++) sum += values[i];
            return (float)(sum / values.Count);
        }

        private static float Max(List<float> values)
        {
            if (values == null || values.Count == 0) return 0f;
            float max = values[0];
            for (int i = 1; i < values.Count; i++) if (values[i] > max) max = values[i];
            return max;
        }

        private static float Percentile(List<float> values, float p)
        {
            if (values == null || values.Count == 0) return 0f;
            List<float> copy = new List<float>(values);
            copy.Sort();
            int index = Mathf.Clamp(Mathf.CeilToInt(p * copy.Count) - 1, 0, copy.Count - 1);
            return copy[index];
        }
    }
}
