using System;
using System.Collections;
using System.IO;
using System.Text;
using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace MeteoriteSPH3D
{
    /// <summary>
    /// Animated GIF recorder for the MeteoriteSPH3D demo.
    /// S = start recording, S again = stop and save.
    ///
    /// Version MAIN_CAMERA_VIEW_NEUTRAL_GIF_V5:
    /// - default capture is the visible Game view produced by the user's Main Camera;
    /// - does not pick a random camera from the scene;
    /// - hides the recorder overlay during frame grab so the GIF contains only the simulation view;
    /// - uses a neutral-gray-safe GIF palette so gray voxel terrain does not turn green during recording.
    /// </summary>
    public sealed class GifRecorder3D : MonoBehaviour
    {
        [Header("Hotkey")]
        public KeyCode toggleKey = KeyCode.S;

        [Header("Capture")]
        public int outputWidth = 640;
        public int outputHeight = 360;
        public int framesPerSecond = 12;
        public int maxFrames = 360;

        [Tooltip("Default: record exactly what the user sees in Game view from Main Camera. This is the safest mode for user-controlled camera movement.")]
        public bool captureVisibleMainCameraGameView = true;

        [Tooltip("Fallback mode: render the Main Camera into a RenderTexture. Used only if captureVisibleMainCameraGameView is disabled or fails.")]
        public bool allowOffscreenMainCameraFallback = true;

        [Tooltip("Optional explicit camera. Leave empty to use Camera.main / object named 'Main Camera'.")]
        public Camera sourceCamera;

        public string mainCameraObjectName = "Main Camera";
        public bool requireMainCameraForGameViewCapture = true;
        public bool loopGif = true;

        [Header("Output")]
        public string outputFolderName = "GifRecordings";
        public string filePrefix = "meteorite_capture";
        public bool saveToProjectFolderInEditor = true;

        [Header("Debug")]
        public bool showOverlay = true;
        public bool hideOverlayWhileCapturing = true;
        public bool logEveryCapturedFrame = false;

        private const string Version = "MAIN_CAMERA_VIEW_NEUTRAL_GIF_V5";
        private static readonly byte[] NeutralGifPalette = BuildNeutralGifPalette();

        private Texture2D readTexture;
        private Texture2D screenTexture;
        private RenderTexture renderTexture;
        private byte[] indexedBuffer;
        private StreamingGifWriter gifWriter;
        private FileStream outputStream;
        private bool isRecording;
        private bool capturePending;
        private bool warnedNoMainCamera;
        private int recordingSessionId;
        private int frameCount;
        private int captureWidth;
        private int captureHeight;
        private float captureInterval;
        private float nextCaptureTime;
        private string currentPath;
        private string lastSavedPath;

        public bool IsRecording { get { return isRecording; } }
        public int CapturedFrameCount { get { return frameCount; } }
        public string LastSavedPath { get { return lastSavedPath; } }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void AutoInstall()
        {
            if (FindExistingRecorder() != null) return;

            GameObject go = new GameObject("GIF Recorder 3D");
            DontDestroyOnLoad(go);
            go.AddComponent<GifRecorder3D>();
        }

        private static GifRecorder3D FindExistingRecorder()
        {
#if UNITY_2023_1_OR_NEWER
            return UnityEngine.Object.FindFirstObjectByType<GifRecorder3D>();
#else
            return UnityEngine.Object.FindObjectOfType<GifRecorder3D>();
#endif
        }

        private void Update()
        {
            if (WasTogglePressed())
            {
                if (isRecording) StopAndSave();
                else StartRecording();
            }

            if (!isRecording || capturePending) return;

            if (frameCount >= Mathf.Max(1, maxFrames))
            {
                StopAndSave();
                return;
            }

            if (Time.unscaledTime >= nextCaptureTime)
            {
                int session = recordingSessionId;
                StartCoroutine(CaptureFrameAtEndOfFrame(session));
                nextCaptureTime += captureInterval;
                if (nextCaptureTime < Time.unscaledTime - captureInterval)
                {
                    nextCaptureTime = Time.unscaledTime + captureInterval;
                }
            }
        }

        private void OnDestroy()
        {
            if (isRecording)
            {
                StopAndSave();
            }

            ReleaseCaptureResources();
        }

        private void OnGUI()
        {
            if (!showOverlay) return;
            if (hideOverlayWhileCapturing && isRecording && capturePending) return;

            int x = 10;
            int y = 10;
            string text = isRecording
                ? "GIF REC " + Version + ": S = stop/save | frames: " + frameCount + " / " + Mathf.Max(1, maxFrames)
                : "GIF " + Version + ": S = start recording";

            GUI.Box(new Rect(x - 4, y - 4, 830, string.IsNullOrEmpty(lastSavedPath) ? 30 : 54), GUIContent.none);
            GUI.Label(new Rect(x, y, 820, 22), text);

            if (!string.IsNullOrEmpty(lastSavedPath))
            {
                y += 22;
                GUI.Label(new Rect(x, y, 1000, 22), "Last GIF: " + lastSavedPath);
            }
        }

        public void StartRecording()
        {
            if (isRecording) return;

            captureWidth = Mathf.Clamp(outputWidth, 16, 4096);
            captureHeight = Mathf.Clamp(outputHeight, 16, 4096);
            framesPerSecond = Mathf.Clamp(framesPerSecond, 1, 60);
            maxFrames = Mathf.Max(1, maxFrames);
            captureInterval = 1f / framesPerSecond;
            frameCount = 0;
            recordingSessionId++;
            capturePending = false;
            warnedNoMainCamera = false;
            nextCaptureTime = Time.unscaledTime;
            currentPath = BuildOutputPath();

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(currentPath));
                outputStream = new FileStream(currentPath, FileMode.Create, FileAccess.Write, FileShare.Read);
                int delayCentiseconds = Mathf.Clamp(Mathf.RoundToInt(100f / framesPerSecond), 1, 65535);
                gifWriter = new StreamingGifWriter(outputStream, captureWidth, captureHeight, delayCentiseconds, NeutralGifPalette, loopGif);
                gifWriter.WriteHeader();
                isRecording = true;
                Debug.Log("[GifRecorder3D " + Version + "] Recording started from user's Main Camera/Game view: " + currentPath + ". Press S again to stop and save GIF.");
            }
            catch (Exception e)
            {
                Debug.LogError("[GifRecorder3D " + Version + "] Failed to start recording: " + e);
                SafeCloseWriter(false);
                isRecording = false;
            }
        }

        public void StopAndSave()
        {
            if (!isRecording && gifWriter == null) return;

            isRecording = false;
            recordingSessionId++;
            capturePending = false;

            bool hasFrames = frameCount > 0;
            SafeCloseWriter(hasFrames);

            if (!hasFrames)
            {
                Debug.LogWarning("[GifRecorder3D " + Version + "] Recording stopped, but no frames were captured.");
                TryDeleteEmptyFile(currentPath);
                return;
            }

            lastSavedPath = currentPath;
            long size = 0;
            try
            {
                FileInfo info = new FileInfo(currentPath);
                if (info.Exists) size = info.Length;
            }
            catch { }

            Debug.Log("[GifRecorder3D " + Version + "] GIF saved: " + currentPath + " | frames: " + frameCount + " | size: " + FormatBytes(size));
        }

        private IEnumerator CaptureFrameAtEndOfFrame(int sessionId)
        {
            capturePending = true;
            yield return new WaitForEndOfFrame();

            if (!isRecording || sessionId != recordingSessionId || gifWriter == null)
            {
                capturePending = false;
                yield break;
            }

            try
            {
                EnsureCaptureResources(captureWidth, captureHeight);

                bool indexedFrameReady = false;

                if (captureVisibleMainCameraGameView)
                {
                    indexedFrameReady = CaptureVisibleGameViewToIndexedBuffer();
                }

                if (!indexedFrameReady && allowOffscreenMainCameraFallback)
                {
                    bool cameraCaptured = CaptureMainCameraToTexture();
                    if (cameraCaptured)
                    {
                        FillIndexedBufferFromTexture(readTexture, indexedBuffer, captureWidth, captureHeight);
                        indexedFrameReady = true;
                    }
                }

                if (!indexedFrameReady)
                {
                    throw new InvalidOperationException("Could not capture Main Camera. Check that a Camera named 'Main Camera' exists and has tag MainCamera.");
                }

                gifWriter.WriteFrame(indexedBuffer);
                outputStream.Flush();
                frameCount++;

                if (logEveryCapturedFrame)
                {
                    Debug.Log("[GifRecorder3D " + Version + "] Captured frame " + frameCount);
                }
            }
            catch (Exception e)
            {
                Debug.LogError("[GifRecorder3D " + Version + "] Capture failed, stopping recording: " + e);
                StopAndSave();
                yield break;
            }

            capturePending = false;

            if (frameCount >= Mathf.Max(1, maxFrames))
            {
                StopAndSave();
            }
        }

        private void EnsureCaptureResources(int width, int height)
        {
            if (readTexture == null || readTexture.width != width || readTexture.height != height)
            {
                ReleaseReadTextureOnly();
                readTexture = new Texture2D(width, height, TextureFormat.RGB24, false);
            }

            if (renderTexture == null || renderTexture.width != width || renderTexture.height != height)
            {
                ReleaseRenderTextureOnly();
                renderTexture = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32);
                renderTexture.Create();
            }

            int expected = width * height;
            if (indexedBuffer == null || indexedBuffer.Length != expected)
            {
                indexedBuffer = new byte[expected];
            }
        }

        private bool CaptureVisibleGameViewToIndexedBuffer()
        {
            if (requireMainCameraForGameViewCapture && ResolveUserMainCamera() == null)
            {
                WarnNoMainCameraOnce();
                return false;
            }

            int screenWidth = Mathf.Max(1, Screen.width);
            int screenHeight = Mathf.Max(1, Screen.height);
            if (screenWidth <= 1 || screenHeight <= 1) return false;

            EnsureScreenTexture(screenWidth, screenHeight);

            screenTexture.ReadPixels(new Rect(0, 0, screenWidth, screenHeight), 0, 0, false);
            screenTexture.Apply(false, false);

            Color32[] sourcePixels = screenTexture.GetPixels32();
            DownscaleAndQuantizeNeutralPalette(sourcePixels, screenWidth, screenHeight, indexedBuffer, captureWidth, captureHeight);
            return true;
        }

        private bool CaptureMainCameraToTexture()
        {
            Camera camera = ResolveUserMainCamera();
            if (camera == null)
            {
                WarnNoMainCameraOnce();
                return false;
            }

            RenderTexture previousTarget = camera.targetTexture;
            RenderTexture previousActive = RenderTexture.active;

            try
            {
                camera.targetTexture = renderTexture;
                RenderTexture.active = renderTexture;
                camera.Render();
                readTexture.ReadPixels(new Rect(0, 0, captureWidth, captureHeight), 0, 0, false);
                readTexture.Apply(false, false);
                return true;
            }
            finally
            {
                camera.targetTexture = previousTarget;
                RenderTexture.active = previousActive;
            }
        }

        private Camera ResolveUserMainCamera()
        {
            if (sourceCamera != null) return sourceCamera;

            Camera camera = Camera.main;
            if (camera != null) return camera;

            try
            {
                GameObject tagged = GameObject.FindGameObjectWithTag("MainCamera");
                if (tagged != null)
                {
                    Camera taggedCamera = tagged.GetComponent<Camera>();
                    if (taggedCamera != null) return taggedCamera;
                }
            }
            catch { }

            if (!string.IsNullOrEmpty(mainCameraObjectName))
            {
                GameObject named = GameObject.Find(mainCameraObjectName);
                if (named != null)
                {
                    Camera namedCamera = named.GetComponent<Camera>();
                    if (namedCamera != null) return namedCamera;
                }
            }

#if UNITY_2023_1_OR_NEWER
            Camera[] cameras = FindObjectsByType<Camera>(FindObjectsSortMode.None);
#else
            Camera[] cameras = FindObjectsOfType<Camera>();
#endif
            if (cameras == null) return null;

            for (int i = 0; i < cameras.Length; i++)
            {
                Camera c = cameras[i];
                if (c != null && c.gameObject.name == mainCameraObjectName) return c;
            }

            for (int i = 0; i < cameras.Length; i++)
            {
                Camera c = cameras[i];
                if (c == null) continue;
                try
                {
                    if (c.CompareTag("MainCamera")) return c;
                }
                catch { }
            }

            return null;
        }

        private void WarnNoMainCameraOnce()
        {
            if (warnedNoMainCamera) return;
            warnedNoMainCamera = true;
            Debug.LogWarning("[GifRecorder3D " + Version + "] Main Camera was not found. Expected Camera.main, tag MainCamera, or object named '" + mainCameraObjectName + "'.");
        }

        private void EnsureScreenTexture(int width, int height)
        {
            if (screenTexture != null && screenTexture.width == width && screenTexture.height == height) return;

            ReleaseScreenTextureOnly();
            screenTexture = new Texture2D(width, height, TextureFormat.RGB24, false);
        }

        private static void FillIndexedBufferFromTexture(Texture2D texture, byte[] target, int width, int height)
        {
            Color32[] pixels = texture.GetPixels32();

            for (int y = 0; y < height; y++)
            {
                // GIF rows are top-to-bottom. Unity Texture2D pixels are bottom-to-top.
                int sourceY = height - 1 - y;
                int sourceRow = sourceY * width;
                int targetRow = y * width;

                for (int x = 0; x < width; x++)
                {
                    target[targetRow + x] = QuantizeNeutralPalette(pixels[sourceRow + x]);
                }
            }
        }

        private static void DownscaleAndQuantizeNeutralPalette(Color32[] sourcePixels, int sourceWidth, int sourceHeight, byte[] target, int targetWidth, int targetHeight)
        {
            for (int y = 0; y < targetHeight; y++)
            {
                int sourceY = sourceHeight - 1 - Mathf.Clamp((y * sourceHeight) / targetHeight, 0, sourceHeight - 1);
                int sourceRow = sourceY * sourceWidth;
                int targetRow = y * targetWidth;

                for (int x = 0; x < targetWidth; x++)
                {
                    int sourceX = Mathf.Clamp((x * sourceWidth) / targetWidth, 0, sourceWidth - 1);
                    target[targetRow + x] = QuantizeNeutralPalette(sourcePixels[sourceRow + sourceX]);
                }
            }
        }

        private static byte QuantizeNeutralPalette(Color32 color)
        {
            int min = Mathf.Min(color.r, Mathf.Min(color.g, color.b));
            int max = Mathf.Max(color.r, Mathf.Max(color.g, color.b));

            // The old RGB332 palette had only two blue bits. Neutral gray terrain such as
            // RGB(117,117,117) was encoded as roughly RGB(109,109,85), which looked
            // green/yellow in GIF recordings. Near-neutral colors now go to a dedicated
            // grayscale ramp, so the surface stays visually gray.
            if (max - min <= 18)
            {
                int luma = (color.r * 299 + color.g * 587 + color.b * 114 + 500) / 1000;
                int grayIndex = Mathf.Clamp(Mathf.RoundToInt(luma * 39f / 255f), 0, 39);
                return (byte)(216 + grayIndex);
            }

            int r = Mathf.Clamp((color.r + 25) / 51, 0, 5);
            int g = Mathf.Clamp((color.g + 25) / 51, 0, 5);
            int b = Mathf.Clamp((color.b + 25) / 51, 0, 5);
            return (byte)(r * 36 + g * 6 + b);
        }

        private static byte[] BuildNeutralGifPalette()
        {
            byte[] palette = new byte[256 * 3];

            // 0..215: 6x6x6 color cube. This keeps hot/brown particles acceptable.
            int index = 0;
            for (int r = 0; r < 6; r++)
            {
                for (int g = 0; g < 6; g++)
                {
                    for (int b = 0; b < 6; b++)
                    {
                        int offset = index * 3;
                        palette[offset] = (byte)(r * 51);
                        palette[offset + 1] = (byte)(g * 51);
                        palette[offset + 2] = (byte)(b * 51);
                        index++;
                    }
                }
            }

            // 216..255: exact neutral grayscale ramp for terrain and shadows.
            for (int i = 0; i < 40; i++)
            {
                byte gray = (byte)Mathf.Clamp(Mathf.RoundToInt(i * 255f / 39f), 0, 255);
                int offset = (216 + i) * 3;
                palette[offset] = gray;
                palette[offset + 1] = gray;
                palette[offset + 2] = gray;
            }

            return palette;
        }

        private bool WasTogglePressed()
        {
#if ENABLE_INPUT_SYSTEM
            Keyboard keyboard = Keyboard.current;
            if (keyboard != null)
            {
                if (toggleKey == KeyCode.S) return keyboard.sKey.wasPressedThisFrame;
                if (toggleKey == KeyCode.R) return keyboard.rKey.wasPressedThisFrame;
                if (toggleKey == KeyCode.Space) return keyboard.spaceKey.wasPressedThisFrame;
                if (toggleKey == KeyCode.F1) return keyboard.f1Key.wasPressedThisFrame;
            }
            return false;
#elif ENABLE_LEGACY_INPUT_MANAGER
            return Input.GetKeyDown(toggleKey);
#else
            return false;
#endif
        }

        private string BuildOutputPath()
        {
            string folder;
#if UNITY_EDITOR
            if (saveToProjectFolderInEditor)
            {
                DirectoryInfo projectRoot = Directory.GetParent(Application.dataPath);
                folder = projectRoot != null
                    ? Path.Combine(projectRoot.FullName, outputFolderName)
                    : Path.Combine(Application.persistentDataPath, outputFolderName);
            }
            else
            {
                folder = Path.Combine(Application.persistentDataPath, outputFolderName);
            }
#else
            folder = Path.Combine(Application.persistentDataPath, outputFolderName);
#endif
            Directory.CreateDirectory(folder);
            string fileName = filePrefix + "_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".gif";
            return Path.Combine(folder, fileName);
        }

        private void SafeCloseWriter(bool finishGif)
        {
            try
            {
                if (gifWriter != null)
                {
                    if (finishGif) gifWriter.Finish();
                    gifWriter = null;
                }
            }
            catch (Exception e)
            {
                Debug.LogError("[GifRecorder3D " + Version + "] Failed to finish GIF: " + e);
            }

            try
            {
                if (outputStream != null)
                {
                    outputStream.Flush();
                    outputStream.Dispose();
                    outputStream = null;
                }
            }
            catch { }
        }

        private static void TryDeleteEmptyFile(string path)
        {
            if (string.IsNullOrEmpty(path)) return;
            try
            {
                if (File.Exists(path)) File.Delete(path);
            }
            catch { }
        }

        private void ReleaseCaptureResources()
        {
            ReleaseReadTextureOnly();
            ReleaseScreenTextureOnly();
            ReleaseRenderTextureOnly();
            indexedBuffer = null;
        }

        private void ReleaseReadTextureOnly()
        {
            if (readTexture == null) return;
#if UNITY_EDITOR
            DestroyImmediate(readTexture);
#else
            Destroy(readTexture);
#endif
            readTexture = null;
        }

        private void ReleaseScreenTextureOnly()
        {
            if (screenTexture == null) return;
#if UNITY_EDITOR
            DestroyImmediate(screenTexture);
#else
            Destroy(screenTexture);
#endif
            screenTexture = null;
        }

        private void ReleaseRenderTextureOnly()
        {
            if (renderTexture == null) return;
            renderTexture.Release();
#if UNITY_EDITOR
            DestroyImmediate(renderTexture);
#else
            Destroy(renderTexture);
#endif
            renderTexture = null;
        }

        private static string FormatBytes(long bytes)
        {
            if (bytes < 1024) return bytes + " B";
            if (bytes < 1024 * 1024) return (bytes / 1024f).ToString("0.0") + " KB";
            return (bytes / (1024f * 1024f)).ToString("0.0") + " MB";
        }

        private sealed class StreamingGifWriter
        {
            private const int PaletteSize = 256;
            private const int MinCodeSize = 8;
            private const int ClearCode = 256;
            private const int EndCode = 257;
            private const int FixedCodeSize = 9;
            private const int PixelsPerClearBlock = 254;

            private readonly Stream stream;
            private readonly int width;
            private readonly int height;
            private readonly int delayCentiseconds;
            private readonly byte[] globalPalette;
            private readonly bool loop;
            private bool headerWritten;
            private bool finished;

            public StreamingGifWriter(Stream stream, int width, int height, int delayCentiseconds, byte[] globalPalette, bool loop)
            {
                if (stream == null) throw new ArgumentNullException("stream");
                if (width <= 0) throw new ArgumentOutOfRangeException("width");
                if (height <= 0) throw new ArgumentOutOfRangeException("height");
                if (globalPalette == null || globalPalette.Length != PaletteSize * 3)
                    throw new ArgumentException("GIF palette must contain exactly 256 RGB colors.");

                this.stream = stream;
                this.width = width;
                this.height = height;
                this.delayCentiseconds = Mathf.Clamp(delayCentiseconds, 1, 65535);
                this.globalPalette = globalPalette;
                this.loop = loop;
            }

            public void WriteHeader()
            {
                if (headerWritten) return;

                WriteAscii(stream, "GIF89a");
                WriteShort(stream, width);
                WriteShort(stream, height);
                stream.WriteByte(0xF7); // global color table, 8-bit color resolution, 256 colors
                stream.WriteByte(0x00); // background index
                stream.WriteByte(0x00); // pixel aspect ratio
                stream.Write(globalPalette, 0, globalPalette.Length);

                if (loop) WriteLoopExtension(stream);
                headerWritten = true;
            }

            public void WriteFrame(byte[] indexedPixels)
            {
                if (finished) throw new InvalidOperationException("GIF writer is already finished.");
                if (!headerWritten) WriteHeader();
                if (indexedPixels == null || indexedPixels.Length != width * height)
                    throw new ArgumentException("Frame has invalid pixel count.");

                WriteGraphicControlExtension(stream, delayCentiseconds);
                WriteImageDescriptor(stream, width, height);
                WriteImageData(stream, indexedPixels);
            }

            public void Finish()
            {
                if (finished) return;
                if (!headerWritten) WriteHeader();
                stream.WriteByte(0x3B); // GIF trailer
                stream.Flush();
                finished = true;
            }

            private static void WriteLoopExtension(Stream stream)
            {
                stream.WriteByte(0x21);
                stream.WriteByte(0xFF);
                stream.WriteByte(0x0B);
                WriteAscii(stream, "NETSCAPE2.0");
                stream.WriteByte(0x03);
                stream.WriteByte(0x01);
                WriteShort(stream, 0);
                stream.WriteByte(0x00);
            }

            private static void WriteGraphicControlExtension(Stream stream, int delayCentiseconds)
            {
                stream.WriteByte(0x21);
                stream.WriteByte(0xF9);
                stream.WriteByte(0x04);
                stream.WriteByte(0x00);
                WriteShort(stream, delayCentiseconds);
                stream.WriteByte(0x00);
                stream.WriteByte(0x00);
            }

            private static void WriteImageDescriptor(Stream stream, int width, int height)
            {
                stream.WriteByte(0x2C);
                WriteShort(stream, 0);
                WriteShort(stream, 0);
                WriteShort(stream, width);
                WriteShort(stream, height);
                stream.WriteByte(0x00);
            }

            private static void WriteImageData(Stream stream, byte[] indexedPixels)
            {
                stream.WriteByte(MinCodeSize);
                byte[] lzwBytes = EncodePixelsAsRawLiteralLzw(indexedPixels);
                WriteSubBlocks(stream, lzwBytes);
            }

            private static byte[] EncodePixelsAsRawLiteralLzw(byte[] indexedPixels)
            {
                BitWriter writer = new BitWriter(indexedPixels.Length + indexedPixels.Length / PixelsPerClearBlock + 8);

                int offset = 0;
                while (offset < indexedPixels.Length)
                {
                    writer.Write(ClearCode, FixedCodeSize);

                    int count = Math.Min(PixelsPerClearBlock, indexedPixels.Length - offset);
                    for (int i = 0; i < count; i++)
                    {
                        writer.Write(indexedPixels[offset + i], FixedCodeSize);
                    }

                    offset += count;
                }

                writer.Write(EndCode, FixedCodeSize);
                return writer.ToArray();
            }

            private static void WriteSubBlocks(Stream stream, byte[] data)
            {
                int offset = 0;
                while (offset < data.Length)
                {
                    int blockSize = Math.Min(255, data.Length - offset);
                    stream.WriteByte((byte)blockSize);
                    stream.Write(data, offset, blockSize);
                    offset += blockSize;
                }
                stream.WriteByte(0x00);
            }

            private static void WriteAscii(Stream stream, string value)
            {
                byte[] bytes = Encoding.ASCII.GetBytes(value);
                stream.Write(bytes, 0, bytes.Length);
            }

            private static void WriteShort(Stream stream, int value)
            {
                stream.WriteByte((byte)(value & 0xFF));
                stream.WriteByte((byte)((value >> 8) & 0xFF));
            }
        }

        private sealed class BitWriter
        {
            private readonly MemoryStream stream;
            private int currentByte;
            private int bitPosition;

            public BitWriter(int expectedCodeCount)
            {
                int capacity = Mathf.Max(256, expectedCodeCount * 9 / 8 + 32);
                stream = new MemoryStream(capacity);
            }

            public void Write(int code, int bitCount)
            {
                for (int i = 0; i < bitCount; i++)
                {
                    currentByte |= ((code >> i) & 1) << bitPosition;
                    bitPosition++;

                    if (bitPosition == 8)
                    {
                        stream.WriteByte((byte)currentByte);
                        currentByte = 0;
                        bitPosition = 0;
                    }
                }
            }

            public byte[] ToArray()
            {
                if (bitPosition > 0)
                {
                    stream.WriteByte((byte)currentByte);
                    currentByte = 0;
                    bitPosition = 0;
                }

                return stream.ToArray();
            }
        }
    }
}
