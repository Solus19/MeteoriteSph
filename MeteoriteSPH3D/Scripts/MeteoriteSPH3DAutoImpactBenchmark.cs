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
        public int impactsPerSize = 10;
        public float[] impactSizeScales = new float[] { 1f, 1.5f, 5f, 10f };
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
        [Tooltip("For every impact-size group, render/commit the last run and save one screenshot of the final crater.")]
        public bool renderAndScreenshotLastImpactInEachSeries = true;
        [Tooltip("Restore high-quality lighting/terrain settings before taking screenshots of the last run in each series.")]
        public bool restoreFinalQualityForLastImpactScreenshot = true;
        [Tooltip("How many frames to wait after the final terrain commit before taking the screenshot.")]
        public int screenshotDelayFramesAfterCommit = 3;
        public string screenshotFilePrefix = "final_crater";
        public int zeroActiveFramesToFinish = 1;
        public float maxSecondsPerImpact = 120f;
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
            totalPlannedRuns = Mathf.Max(0, sizeCount) * Mathf.Max(1, impactsPerSize);
            Debug.Log("[MeteoriteSPH3D Benchmark] Запущен. План: " + totalPlannedRuns + " ударов (" + Mathf.Max(1, impactsPerSize) + " на размер). F7 — остановка.");
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
            if (verboseConsoleProgress && renderAndScreenshotLastImpactInEachSeries)
            {
                Debug.Log("[MeteoriteSPH3D Benchmark] Последний удар каждой серии будет применён визуально и сохранён скриншотом в папку запуска: " + OutputRunFolderPath);
            }

            if (controller.IsPaused) controller.TogglePause();

            try
            {
                for (int sizeIndex = 0; sizeIndex < impactSizeScales.Length; sizeIndex++)
                {
                    float scale = Mathf.Max(0.01f, impactSizeScales[sizeIndex]);
                    ScaleInfo scaleInfo;
                    bool canRun = ApplyScale(scale, out scaleInfo);
                    if (verboseConsoleProgress)
                    {
                        Debug.Log("[MeteoriteSPH3D Benchmark] Группа " + (sizeIndex + 1) + "/" + impactSizeScales.Length
                            + ": размер " + FormatScaleLabel(scale)
                            + ", terrain=" + controller.terrainWidth + "x" + controller.terrainHeight + "x" + controller.terrainDepth
                            + ", voxels=" + scaleInfo.targetVoxelCount
                            + ", approxTerrainRAM=" + F(EstimateTerrainMemoryMb(scaleInfo.targetVoxelCount)) + " MB"
                            + ", cellSize=" + F(controller.cellSize)
                            + ", radius=" + F(controller.impactRadius)
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
                        if (!scaleInfo.clampedToBudget && mapScaleMode == MapScaleMode.StrictVoxelCounts && scaleInfo.targetVoxelCount > 50000000L)
                        {
                            Debug.LogWarning("[MeteoriteSPH3D Benchmark] ВНИМАНИЕ: strict scaling без ограничения. Будет попытка создать "
                                + scaleInfo.targetVoxelCount + " вокселей. Для x5/x10 это может съесть память или уронить Unity.");
                        }
                    }
                    if (!canRun)
                    {
                        WriteSkippedScale(sizeIndex, scale, scaleInfo);
                        continue;
                    }

                    for (int run = 1; run <= Mathf.Max(1, impactsPerSize); run++)
                    {
                        if (resetTerrainBeforeEachImpact || run == 1)
                        {
                            if (verboseConsoleProgress)
                                Debug.Log("[MeteoriteSPH3D Benchmark] Подготовка внутреннего terrain: размер " + FormatScaleLabel(scale) + ", удар " + run + "/" + Mathf.Max(1, impactsPerSize) + ". Видимый mesh не обновляется.");
                            controller.ResetSimulation();
                            if (!keepVisibleEnvironmentUnchangedDuringBenchmark) controller.ReframeCameraToTerrain();
                            if (!finishAtZeroParticlesWithoutVisualCommit) yield return null;
                        }

                        bool renderLastRun = ShouldRenderLastImpactInSeries(run);
                        controller.deferredVisualCommitEnabled = renderLastRun ? true : !finishAtZeroParticlesWithoutVisualCommit;
                        controller.restoreHighQualityLightingWhenParticlesStop = renderLastRun && restoreFinalQualityForLastImpactScreenshot;
                        controller.restoreTerrainRenderWhenParticlesStop = renderLastRun && restoreFinalQualityForLastImpactScreenshot;

                        yield return StartCoroutine(RunSingleImpact(sizeIndex, scale, run, scaleInfo, renderLastRun));

                        if (cooldownSecondsBetweenImpacts > 0f)
                        {
                            if (verboseConsoleProgress) Debug.Log("[MeteoriteSPH3D Benchmark] Пауза " + F(cooldownSecondsBetweenImpacts) + " с перед следующим ударом.");
                            float cooldownStart = Time.realtimeSinceStartup;
                            while (Time.realtimeSinceStartup - cooldownStart < cooldownSecondsBetweenImpacts)
                                yield return null;
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

        private IEnumerator RunSingleImpact(int sizeIndex, float sizeScale, int runIndex, ScaleInfo scaleInfo, bool renderFinalVisualAndScreenshot)
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
            RunStats stats = new RunStats();

            if (verboseConsoleProgress)
            {
                int globalRun = sizeIndex * Mathf.Max(1, impactsPerSize) + runIndex;
                Debug.Log("[MeteoriteSPH3D Benchmark] Удар START " + globalRun + "/" + Mathf.Max(1, totalPlannedRuns)
                    + ": размер " + FormatScaleLabel(sizeScale)
                    + ", повтор " + runIndex + "/" + Mathf.Max(1, impactsPerSize)
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
                    MaybeLogImpactProgress(sizeIndex, sizeScale, runIndex, start, stats);
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

                if (Time.realtimeSinceStartup - start >= Mathf.Max(1f, maxSecondsPerImpact))
                {
                    timedOut = true;
                    if (verboseConsoleProgress)
                        Debug.LogWarning("[MeteoriteSPH3D Benchmark] TIMEOUT: размер " + FormatScaleLabel(sizeScale) + ", повтор " + runIndex + ", active=" + controller.ActiveParticleCount + ".");
                    break;
                }

                yield return null;
            }

            float wallTime = (simulationDoneTime >= 0f ? simulationDoneTime : Time.realtimeSinceStartup) - start;
            int frames = Mathf.Max(1, (simulationDoneFrame >= 0 ? simulationDoneFrame : Time.frameCount) - frameStart);
            int created = controller.TotalCreatedParticles - totalCreatedBefore;
            int solidified = controller.TotalSolidifiedParticles - totalSolidifiedBefore;
            int solidAfter = controller.SolidVoxelCount;

            string screenshotPath = string.Empty;
            if (renderFinalVisualAndScreenshot && !timedOut)
            {
                yield return StartCoroutine(CaptureFinalScreenshot(sizeIndex, sizeScale, runIndex, path => screenshotPath = path));
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

        private void MaybeLogImpactProgress(int sizeIndex, float sizeScale, int runIndex, float runStartTime, RunStats stats)
        {
            if (!verboseConsoleProgress) return;
            if (Time.realtimeSinceStartup < nextConsoleProgressTime) return;
            nextConsoleProgressTime = Time.realtimeSinceStartup + Mathf.Max(0.25f, consoleProgressIntervalSeconds);

            float elapsed = Time.realtimeSinceStartup - runStartTime;
            int globalRun = sizeIndex * Mathf.Max(1, impactsPerSize) + runIndex;
            float fps = Time.unscaledDeltaTime > 0.00001f ? 1f / Time.unscaledDeltaTime : 0f;
            float cpuLast = stats.cpuFrameMs.Count > 0 ? stats.cpuFrameMs[stats.cpuFrameMs.Count - 1] : 0f;
            float gpuLast = stats.gpuFrameMs.Count > 0 ? stats.gpuFrameMs[stats.gpuFrameMs.Count - 1] : 0f;

            Debug.Log("[MeteoriteSPH3D Benchmark] Прогресс " + globalRun + "/" + Mathf.Max(1, totalPlannedRuns)
                + ": размер " + FormatScaleLabel(sizeScale)
                + ", повтор " + runIndex + "/" + Mathf.Max(1, impactsPerSize)
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


        private bool ShouldRenderLastImpactInSeries(int runIndex)
        {
            return renderAndScreenshotLastImpactInEachSeries && runIndex >= Mathf.Max(1, impactsPerSize);
        }

        private IEnumerator CaptureFinalScreenshot(int sizeIndex, float sizeScale, int runIndex, Action<string> onSaved)
        {
            int delay = Mathf.Max(0, screenshotDelayFramesAfterCommit);
            for (int i = 0; i < delay; i++)
                yield return null;

            yield return new WaitForEndOfFrame();

            string folder = string.IsNullOrEmpty(OutputRunFolderPath) ? Path.Combine(Application.dataPath, outputFolderName) : OutputRunFolderPath;
            Directory.CreateDirectory(folder);
            string safeScale = sizeScale.ToString("0.###", invariant).Replace('.', '_').Replace(',', '_');
            string path = Path.Combine(folder, screenshotFilePrefix + "_size_" + safeScale + "x_run_" + runIndex.ToString("00", invariant) + ".png");

            Texture2D screenshot = ScreenCapture.CaptureScreenshotAsTexture();
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
            Debug.Log("[MeteoriteSPH3D Benchmark] Скриншот последнего удара серии сохранён: " + path);
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
            summaryWriter.WriteLine("size_index,size_scale,run_index,map_scale_mode,applied_voxel_scale,horizontal_voxel_scale,vertical_voxel_scale,world_cell_scale,terrain_width,terrain_height,terrain_depth,cell_size,impact_radius,max_particles,max_created_particles,created_particles,solidified_particles,solid_voxels_before,solid_voxels_after,wall_time_s,frames,timeout,cpu_frame_ms_avg,cpu_frame_ms_p95,cpu_frame_ms_max,gpu_frame_ms_avg,gpu_frame_ms_p95,gpu_frame_ms_max,frame_ms_avg,frame_ms_p95,frame_ms_max,controller_ms_avg,controller_ms_p95,controller_ms_max,gpu_sim_ms_avg,gpu_sim_ms_p95,gpu_sim_ms_max,cpu_sim_ms_avg,cpu_sim_ms_p95,cpu_sim_ms_max,readback_ms_avg,readback_ms_p95,readback_ms_max,solidify_ms_avg,solidify_ms_p95,solidify_ms_max,terrain_upload_ms_avg,terrain_upload_ms_p95,terrain_upload_ms_max,mesh_rebuild_ms_avg,mesh_rebuild_ms_p95,mesh_rebuild_ms_max,ram_allocated_mb_max,ram_reserved_mb_max,mono_heap_mb_max,gpu_memory_total_mb,hit_x,hit_y,hit_z,screenshot_path");

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
            summaryWriter.Write(F(scaleInfo.appliedVoxelScale)); summaryWriter.Write(',');
            summaryWriter.Write(F(scaleInfo.horizontalVoxelScale)); summaryWriter.Write(',');
            summaryWriter.Write(F(scaleInfo.verticalVoxelScale)); summaryWriter.Write(',');
            summaryWriter.Write(F(scaleInfo.worldCellScale)); summaryWriter.Write(',');
            summaryWriter.Write(controller.terrainWidth.ToString(invariant)); summaryWriter.Write(',');
            summaryWriter.Write(controller.terrainHeight.ToString(invariant)); summaryWriter.Write(',');
            summaryWriter.Write(controller.terrainDepth.ToString(invariant)); summaryWriter.Write(',');
            summaryWriter.Write(F(controller.cellSize)); summaryWriter.Write(',');
            summaryWriter.Write(F(controller.impactRadius)); summaryWriter.Write(',');
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
                + ", повтор " + runIndex + "/" + Mathf.Max(1, impactsPerSize)
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
            summaryWriter.Write(F(scaleInfo.appliedVoxelScale)); summaryWriter.Write(',');
            summaryWriter.Write(F(scaleInfo.horizontalVoxelScale)); summaryWriter.Write(',');
            summaryWriter.Write(F(scaleInfo.verticalVoxelScale)); summaryWriter.Write(',');
            summaryWriter.Write(F(scaleInfo.worldCellScale)); summaryWriter.Write(',');
            summaryWriter.WriteLine("0,0,0,0,0,0,0,0,0,0,0,0,0,1,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0");
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
        }

        private struct ScaleInfo
        {
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

            long baseVoxels = (long)baseConfig.terrainWidth * baseConfig.terrainHeight * baseConfig.terrainDepth;
            float horizontalVoxelScale = 1f;
            float verticalVoxelScale = 1f;
            float appliedVoxelScale = 1f;
            float worldCellScale = sizeScale;
            bool clampedToBudget = false;
            int budget = Mathf.Max(1, maxBenchmarkVoxelCount);

            if (mapScaleMode == MapScaleMode.StrictVoxelCounts)
            {
                horizontalVoxelScale = sizeScale;
                verticalVoxelScale = sizeScale;
                worldCellScale = 1f;
            }
            else if (mapScaleMode == MapScaleMode.CellSizeOnly)
            {
                horizontalVoxelScale = 1f;
                verticalVoxelScale = 1f;
                worldCellScale = sizeScale;
            }
            else
            {
                // First approximation. A second pass below clamps X/Z again after the final Y budget is known.
                float maxScaleByBudget = Mathf.Pow(Mathf.Max(1f, budget) / Mathf.Max(1f, (float)baseVoxels), 1f / 3f);
                horizontalVoxelScale = Mathf.Min(sizeScale, Mathf.Max(1f, maxScaleByBudget));
                verticalVoxelScale = horizontalVoxelScale;
                worldCellScale = sizeScale / Mathf.Max(0.0001f, horizontalVoxelScale);
            }

            int width;
            int depth;
            int height;
            int baseHeight;
            int reliefAmplitude;
            ComputeTerrainDimensions(sizeScale, horizontalVoxelScale, verticalVoxelScale, worldCellScale, out width, out height, out depth, out baseHeight, out reliefAmplitude, out verticalVoxelScale, out appliedVoxelScale);

            long targetVoxels = (long)width * height * depth;
            bool overBudget = targetVoxels > budget;

            if (overBudget && mapScaleMode == MapScaleMode.StrictVoxelCounts && skipStrictScaleWhenOverBudget && !clampTerrainToVoxelBudget)
            {
                info = new ScaleInfo
                {
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
                    horizontalVoxelScale = Mathf.Clamp(Mathf.Min(horizontalVoxelScale, maxHorizontalByBudget), 0.05f, Mathf.Max(0.05f, sizeScale));
                    worldCellScale = sizeScale / Mathf.Max(0.0001f, horizontalVoxelScale);

                    ComputeTerrainDimensions(sizeScale, horizontalVoxelScale, verticalVoxelScale, worldCellScale, out width, out height, out depth, out baseHeight, out reliefAmplitude, out verticalVoxelScale, out appliedVoxelScale);
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

            float particleBudgetScale = Mathf.Max(1f, horizontalVoxelScale * horizontalVoxelScale * Mathf.Max(1f, verticalVoxelScale));
            controller.maxParticles = Mathf.Max(baseConfig.maxParticles, SafeRoundToInt(baseConfig.maxParticles * particleBudgetScale));
            controller.maxCreatedParticlesPerImpact = Mathf.Max(baseConfig.maxCreatedParticlesPerImpact, SafeRoundToInt(baseConfig.maxCreatedParticlesPerImpact * particleBudgetScale));
            controller.gpuDepositCandidateCapacity = Mathf.Max(baseConfig.gpuDepositCandidateCapacity, SafeRoundToInt(baseConfig.gpuDepositCandidateCapacity * Mathf.Max(1f, horizontalVoxelScale * horizontalVoxelScale)));
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
