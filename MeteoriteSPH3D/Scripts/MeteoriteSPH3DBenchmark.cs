using System;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.Profiling;

namespace MeteoriteSPH3D
{
    public sealed class MeteoriteSPH3DBenchmark : MonoBehaviour
    {
        [Header("CSV benchmark")]
        public bool recordOnStart = true;
        public float sampleInterval = 0.25f;
        public int flushEverySamples = 16;
        public KeyCode toggleRecordingKey = KeyCode.B;
        public KeyCode newFileKey = KeyCode.F10;
        public KeyCode markerKey = KeyCode.F9;

        public string CurrentFilePath { get; private set; }
        public bool IsRecording { get { return writer != null; } }

        private MeteoriteSPH3DController controller;
        private StreamWriter writer;
        private float startRealtime;
        private float nextSampleTime;
        private float accumulatedFrameMs;
        private float maxFrameMs;
        private int accumulatedFrames;
        private int samplesSinceFlush;
        private int markerIndex;
        private readonly CultureInfo invariant = CultureInfo.InvariantCulture;

        private void Awake()
        {
            controller = GetComponent<MeteoriteSPH3DController>();
            if (controller == null) controller = FindExistingController();
        }

        private static MeteoriteSPH3DController FindExistingController()
        {
#if UNITY_2023_1_OR_NEWER
            return UnityEngine.Object.FindFirstObjectByType<MeteoriteSPH3DController>();
#else
            return UnityEngine.Object.FindObjectOfType<MeteoriteSPH3DController>();
#endif
        }

        private void Start()
        {
            if (recordOnStart) StartRecording();
        }

        private void Update()
        {
            if (InputBridge3D.KeyDown(toggleRecordingKey))
            {
                if (IsRecording) StopRecording();
                else StartRecording();
            }

            if (InputBridge3D.KeyDown(newFileKey))
            {
                StopRecording();
                StartRecording();
            }

            if (InputBridge3D.KeyDown(markerKey))
            {
                markerIndex++;
                WriteSample("marker_" + markerIndex.ToString(invariant), true);
            }

            float frameMs = Time.unscaledDeltaTime * 1000f;
            accumulatedFrameMs += frameMs;
            if (frameMs > maxFrameMs) maxFrameMs = frameMs;
            accumulatedFrames++;

            if (!IsRecording) return;

            if (Time.unscaledTime >= nextSampleTime)
            {
                WriteSample("sample", false);
                nextSampleTime = Time.unscaledTime + Mathf.Max(0.02f, sampleInterval);
            }
        }

        public void StartRecording()
        {
            StopRecording();

            string dir = Path.Combine(Application.dataPath, "MeteoriteSPH3D_Benchmark");
            Directory.CreateDirectory(dir);
            string fileName = "benchmark_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".csv";
            CurrentFilePath = Path.Combine(dir, fileName);

            writer = new StreamWriter(CurrentFilePath, false, new UTF8Encoding(false));
            writer.WriteLine("event,time_s,realtime_s,frame,frame_ms_avg,frame_ms_last,frame_ms_max,fps_avg,active_particles,solid_voxels,use_gpu,last_created,total_created,last_solidified,total_solidified,controller_update_ms,gpu_sim_ms,cpu_sim_ms,gpu_readback_ms,solidify_ms,gpu_terrain_upload_ms,gpu_particle_upload_ms,mesh_rebuild_ms,ram_allocated_mb,ram_reserved_mb,mono_heap_mb,gpu_memory_total_mb,terrain_width,terrain_height,terrain_depth,max_particles");

            startRealtime = Time.realtimeSinceStartup;
            nextSampleTime = Time.unscaledTime;
            accumulatedFrameMs = 0f;
            maxFrameMs = 0f;
            accumulatedFrames = 0;
            samplesSinceFlush = 0;

            Debug.Log("MeteoriteSPH3D benchmark CSV started in Assets folder: " + CurrentFilePath + "  | B = pause/resume, F9 = marker, F10 = new file");
        }

        public void StopRecording()
        {
            if (writer == null) return;
            WriteSample("stop", true);
            writer.Flush();
            writer.Dispose();
            writer = null;
            Debug.Log("MeteoriteSPH3D benchmark CSV saved: " + CurrentFilePath);
#if UNITY_EDITOR
            UnityEditor.AssetDatabase.Refresh();
#endif
        }

        private void OnApplicationQuit()
        {
            StopRecording();
        }

        private void OnDestroy()
        {
            StopRecording();
        }

        private void WriteSample(string eventName, bool forceFlush)
        {
            if (writer == null || controller == null) return;

            int frameCount = Mathf.Max(1, accumulatedFrames);
            float avgFrameMs = accumulatedFrameMs / frameCount;
            float fpsAvg = avgFrameMs > 0.0001f ? 1000f / avgFrameMs : 0f;
            float timeS = Time.realtimeSinceStartup - startRealtime;

            writer.Write(eventName); writer.Write(',');
            writer.Write(F(timeS)); writer.Write(',');
            writer.Write(F(Time.realtimeSinceStartup)); writer.Write(',');
            writer.Write(Time.frameCount.ToString(invariant)); writer.Write(',');
            writer.Write(F(avgFrameMs)); writer.Write(',');
            writer.Write(F(controller.LastFrameMs)); writer.Write(',');
            writer.Write(F(maxFrameMs)); writer.Write(',');
            writer.Write(F(fpsAvg)); writer.Write(',');
            writer.Write(controller.ActiveParticleCount.ToString(invariant)); writer.Write(',');
            writer.Write(controller.SolidVoxelCount.ToString(invariant)); writer.Write(',');
            writer.Write(controller.UseGpuSimulation ? "1" : "0"); writer.Write(',');
            writer.Write(controller.LastCreatedParticles.ToString(invariant)); writer.Write(',');
            writer.Write(controller.TotalCreatedParticles.ToString(invariant)); writer.Write(',');
            writer.Write(controller.LastSolidifiedParticles.ToString(invariant)); writer.Write(',');
            writer.Write(controller.TotalSolidifiedParticles.ToString(invariant)); writer.Write(',');
            writer.Write(F(controller.LastControllerUpdateMs)); writer.Write(',');
            writer.Write(F(controller.LastGpuSimulationMs)); writer.Write(',');
            writer.Write(F(controller.LastCpuSimulationMs)); writer.Write(',');
            writer.Write(F(controller.LastGpuReadbackMs)); writer.Write(',');
            writer.Write(F(controller.LastSolidifyMs)); writer.Write(',');
            writer.Write(F(controller.LastGpuTerrainUploadMs)); writer.Write(',');
            writer.Write(F(controller.LastGpuParticleUploadMs)); writer.Write(',');
            writer.Write(F(controller.LastMeshRebuildMs)); writer.Write(',');
            writer.Write(F(Profiler.GetTotalAllocatedMemoryLong() / (1024f * 1024f))); writer.Write(',');
            writer.Write(F(Profiler.GetTotalReservedMemoryLong() / (1024f * 1024f))); writer.Write(',');
            writer.Write(F(Profiler.GetMonoHeapSizeLong() / (1024f * 1024f))); writer.Write(',');
            writer.Write(SystemInfo.graphicsMemorySize.ToString(invariant)); writer.Write(',');
            writer.Write(controller.terrainWidth.ToString(invariant)); writer.Write(',');
            writer.Write(controller.terrainHeight.ToString(invariant)); writer.Write(',');
            writer.Write(controller.terrainDepth.ToString(invariant)); writer.Write(',');
            writer.WriteLine(controller.maxParticles.ToString(invariant));

            accumulatedFrameMs = 0f;
            maxFrameMs = 0f;
            accumulatedFrames = 0;
            samplesSinceFlush++;

            if (forceFlush || samplesSinceFlush >= Mathf.Max(1, flushEverySamples))
            {
                writer.Flush();
                samplesSinceFlush = 0;
            }
        }

        private string F(float value)
        {
            return value.ToString("0.###", invariant);
        }
    }
}
