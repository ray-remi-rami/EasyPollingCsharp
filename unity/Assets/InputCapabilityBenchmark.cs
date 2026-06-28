using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
using FastInputAPI;
using FastInputBench;

// Purpose:
//  (1) Show the native (our) layer captures input at a high rate (e.g. 8000Hz) INDEPENDENTLY
//      of frame rate, vs Legacy / New Input System, via "captured events/sec" + "min event-time gap".
//  (2) Show REACTION latency (event -> when game code can act): native (on a dedicated thread)
//      is sub-frame; Legacy / InputSystem are frame-bound.
//  (3) Measure resource usage (native thread CPU, process CPU, memory, per-frame cost).
//  Both keyboard AND mouse are benched separately for every system, and every 1s window can be
//  exported to CSV / text.
//
// Usage: put ONLY this component on an empty GameObject and press Play.
//  (Do NOT run together with KeyboardBurstBenchmark - both call ConsumeEvents and split events.)
//   - Move the mouse continuously AND tap A/S/D/F.
//   - [1]=60fps [2]=144fps [3]=uncapped   [8]=kb 8000Hz [9]=kb 1000Hz
//   - [T]=toggle native consume MainThread/DedicatedThread   [F5]=save CSV+TXT   [F6]=clear samples
public class InputCapabilityBenchmark : MonoBehaviour
{
    [Header("Initial settings")]
    public int keyboardPollingRateHz = 8000;
    public int mousePollingRateHz = 8000;
    private readonly int[] frameRateOptions = { 60, 144, -1 };
    private int frameRateIndex = 0;

    private readonly int[] targetKeys = { 0x41, 0x53, 0x44, 0x46 }; // A S D F
    private readonly KeyCode[] legacyKeys = { KeyCode.A, KeyCode.S, KeyCode.D, KeyCode.F };
    private FastInputAPI.InputEvent[] nativeBuffer = new FastInputAPI.InputEvent[16384];

    // Tracks count + minimum interval between consecutive events over one window.
    private class Stat
    {
        public int count;
        public double lastTs;
        public double minGap = double.MaxValue;
        public void Reset() { count = 0; lastTs = -1; minGap = double.MaxValue; }
        public void Record(double t)
        {
            count++;
            if (lastTs >= 0) { double d = t - lastTs; if (d > 0 && d < minGap) minGap = d; }
            lastTs = t;
        }
        public double GapMs() => minGap >= double.MaxValue ? 0.0 : minGap * 1000.0;
        public string Gap() => minGap >= double.MaxValue ? "   -   " : (minGap * 1000.0).ToString("F2") + " ms";
    }

    // Per-system, per-device stats (keyboard + mouse separately)
    private readonly Stat natKb = new Stat(), natMouse = new Stat();
    private readonly Stat isysKb = new Stat(), isysMouse = new Stat();
    private readonly Stat legKb = new Stat(), legMouse = new Stat();
    private Vector3 legacyLastMouse;

    // Reaction latency accumulators (seconds)
    private double natReactSum, natReactMax; private int natReactCount;     // native (guarded by natLock)
    private double isysReactSum, isysReactMax; private int isysReactCount;  // InputSystem (main thread only)

    // Native consume can run on the main thread (Update) or a dedicated thread.
    private readonly object natLock = new object();
    // Default MainThread: native consumed in Update (reaction is frame-bound, like InputSystem).
    // Press [T] -> DedicatedThread: native consumed by a tight-polling thread => sub-frame reaction,
    // at the cost of one busy core (visible in "Process total CPU"). This is the real proof.
    private volatile bool offThread = false;
    private volatile bool threadRunning = false;
    private Thread consumerThread;

    // Main-thread handling time spent in OUR code per system (accumulated ms / window)
    private double natHandleMs, isysHandleMs, legacyHandleMs;
    private readonly Stopwatch sw = new Stopwatch();

    // Resource-measurement baselines
    private Process proc;
    private double lastNatCpu, lastProcCpu, lastIter;
    private float windowStart, startTime;
    private int frameAccum;

    // Recorder + UI
    private readonly BenchmarkRecorder recorder = new BenchmarkRecorder();
    private string hud = "Warming up... move the mouse and tap A/S/D/F.";
    private string savedMsg = "";

    void Awake()
    {
        proc = Process.GetCurrentProcess();

        FastInput.SetTargetKeys(targetKeys, targetKeys.Length);
        try
        {
            FastInput.SetKeyboardPollingRate(keyboardPollingRateHz);
            FastInput.SetMousePollingRate(mousePollingRateHz);
        }
        catch (Exception e) { UnityEngine.Debug.LogWarning("Old DLL? polling-rate API missing: " + e.Message); }

        FastInput.InitializeInput();

        // New Input System: count every keyboard/mouse event queued (processed in per-frame batches)
        InputSystem.onEvent += OnInputSystemEvent;

        // Dedicated native consumer thread (only consumes while offThread == true).
        threadRunning = true;
        consumerThread = new Thread(NativeConsumerLoop) { IsBackground = true, Name = "FastInputConsumer" };
        consumerThread.Start();

        ApplyFrameRate();
        startTime = Time.realtimeSinceStartup;
        ResetWindow(startTime, true);
    }

    void ApplyFrameRate()
    {
        QualitySettings.vSyncCount = 0; // targetFrameRate is ignored unless vSync is off
        Application.targetFrameRate = frameRateOptions[frameRateIndex];
    }

    // ---- Native event processing (called from EITHER the main thread or the consumer thread) ----
    // nowQpc = FastInput.GetHighPrecisionTime() sampled right after the consume call.
    void ProcessNativeBatch(FastInputAPI.InputEvent[] buf, int n, double nowQpc)
    {
        if (n <= 0) return;
        lock (natLock)
        {
            for (int i = 0; i < n; i++)
            {
                var e = buf[i];
                if (e.type == 0) natKb.Record(e.timestamp);
                else natMouse.Record(e.timestamp);

                double lat = nowQpc - e.timestamp; // reaction latency (seconds)
                if (lat >= 0)
                {
                    natReactSum += lat;
                    natReactCount++;
                    if (lat > natReactMax) natReactMax = lat;
                }
            }
        }
    }

    void NativeConsumerLoop()
    {
        var buf = new FastInputAPI.InputEvent[16384];
        while (threadRunning)
        {
            if (offThread)
            {
                int n = FastInput.ConsumeEvents(buf, buf.Length);
                if (n > 0)
                {
                    double now = FastInput.GetHighPrecisionTime();
                    ProcessNativeBatch(buf, n, now);
                }
                Thread.SpinWait(400); // tight sub-ms poll (this is the CPU cost of true frame-independent reaction)
            }
            else
            {
                Thread.Sleep(2); // idle; the main thread (Update) consumes instead
            }
        }
    }

    void OnInputSystemEvent(InputEventPtr ev, InputDevice device)
    {
        bool isKb = device is Keyboard;
        bool isMouse = device is Mouse;
        if (!isKb && !isMouse) return;
        if (!ev.IsA<StateEvent>() && !ev.IsA<DeltaStateEvent>()) return;

        sw.Restart();
        double t = ev.time; // hardware timestamp (seconds)
        if (isKb) isysKb.Record(t); else isysMouse.Record(t);

        double lat = Time.realtimeSinceStartupAsDouble - t; // reaction latency = when callback fires - event time
        if (lat >= 0) { isysReactSum += lat; isysReactCount++; if (lat > isysReactMax) isysReactMax = lat; }
        sw.Stop();
        isysHandleMs += sw.Elapsed.TotalMilliseconds;
    }

    void Update()
    {
        // Toggles
        if (Input.GetKeyDown(KeyCode.Alpha1)) { frameRateIndex = 0; ApplyFrameRate(); }
        if (Input.GetKeyDown(KeyCode.Alpha2)) { frameRateIndex = 1; ApplyFrameRate(); }
        if (Input.GetKeyDown(KeyCode.Alpha3)) { frameRateIndex = 2; ApplyFrameRate(); }
        if (Input.GetKeyDown(KeyCode.Alpha8)) { try { FastInput.SetKeyboardPollingRate(8000); keyboardPollingRateHz = 8000; } catch { } }
        if (Input.GetKeyDown(KeyCode.Alpha9)) { try { FastInput.SetKeyboardPollingRate(1000); keyboardPollingRateHz = 1000; } catch { } }
        if (Input.GetKeyDown(KeyCode.T)) offThread = !offThread;
        if (Input.GetKeyDown(KeyCode.F5)) SaveData();
        if (Input.GetKeyDown(KeyCode.F6)) { recorder.Clear(); savedMsg = "Samples cleared."; }

        // --- Native: only consume on the main thread when NOT in dedicated-thread mode ---
        if (!offThread)
        {
            sw.Restart();
            int n = FastInput.ConsumeEvents(nativeBuffer, nativeBuffer.Length);
            double now = FastInput.GetHighPrecisionTime();
            ProcessNativeBatch(nativeBuffer, n, now);
            sw.Stop();
            natHandleMs += sw.Elapsed.TotalMilliseconds;
        }

        // --- Legacy: one sample per frame (this is the frame-bound part) ---
        sw.Restart();
        double frameTime = Time.realtimeSinceStartupAsDouble;
        Vector3 mp = Input.mousePosition;
        if (mp != legacyLastMouse) { legMouse.Record(frameTime); legacyLastMouse = mp; }
        for (int k = 0; k < legacyKeys.Length; k++)
        {
            if (Input.GetKeyDown(legacyKeys[k])) legKb.Record(frameTime);
            if (Input.GetKeyUp(legacyKeys[k])) legKb.Record(frameTime);
        }
        sw.Stop();
        legacyHandleMs += sw.Elapsed.TotalMilliseconds;

        frameAccum++;

        // --- Report once per second ---
        float now2 = Time.realtimeSinceStartup;
        if (now2 - windowStart >= 1f) BuildReport(now2, now2 - windowStart);
    }

    void BuildReport(float now, float dt)
    {
        proc.Refresh();
        double natCpu = SafeCpu();
        double procCpu = proc.TotalProcessorTime.TotalSeconds;
        double iter = SafeIter();

        double natCpuPct = (natCpu - lastNatCpu) / dt * 100.0;    // % of one core
        double procCpuPct = (procCpu - lastProcCpu) / dt * 100.0; // % of one core
        double pollHz = (iter - lastIter) / dt;
        int fps = Mathf.RoundToInt(frameAccum / dt);
        int cores = SystemInfo.processorCount;
        double wsMB = proc.WorkingSet64 / (1024.0 * 1024.0);
        string fr = frameRateOptions[frameRateIndex] < 0 ? "uncapped" : frameRateOptions[frameRateIndex].ToString();
        string mode = offThread ? "DedicatedThread" : "MainThread";

        // Reaction latencies (ms). Legacy has no per-event timestamp -> bounded by frame interval.
        double frameMs = fps > 0 ? 1000.0 / fps : 0;
        double natReactAvg, natReactMx; int natKbN, natMouseN; double natKbGap, natMouseGap;
        lock (natLock)
        {
            natReactAvg = natReactCount > 0 ? natReactSum / natReactCount * 1000.0 : 0;
            natReactMx = natReactMax * 1000.0;
            natKbN = natKb.count; natMouseN = natMouse.count; natKbGap = natKb.GapMs(); natMouseGap = natMouse.GapMs();
        }
        double isysReactAvg = isysReactCount > 0 ? isysReactSum / isysReactCount * 1000.0 : 0;
        double isysReactMx = isysReactMax * 1000.0;
        double legReactAvg = frameMs * 0.5, legReactMx = frameMs;

        double natUs = natHandleMs / frameAccum * 1000.0;
        double isysUs = isysHandleMs / frameAccum * 1000.0;
        double legUs = legacyHandleMs / frameAccum * 1000.0;

        // Record one sample row
        recorder.Add(new BenchSample
        {
            timeSec = now - startTime,
            fps = fps, targetFps = frameRateOptions[frameRateIndex], kbPollSetHz = keyboardPollingRateHz, kbPollActualHz = pollHz,
            nativeMode = mode,
            natKbN = natKbN, natMouseN = natMouseN, isysKbN = isysKb.count, isysMouseN = isysMouse.count, legKbN = legKb.count, legMouseN = legMouse.count,
            natKbGapMs = natKbGap, natMouseGapMs = natMouseGap, isysKbGapMs = isysKb.GapMs(), isysMouseGapMs = isysMouse.GapMs(), legKbGapMs = legKb.GapMs(), legMouseGapMs = legMouse.GapMs(),
            natReactAvgMs = natReactAvg, natReactMaxMs = natReactMx, isysReactAvgMs = isysReactAvg, isysReactMaxMs = isysReactMx, legReactAvgMs = legReactAvg, legReactMaxMs = legReactMx,
            nativeCpuPct = natCpuPct, procCpuPct = procCpuPct, memMB = wsMB,
            perFrameNatUs = natUs, perFrameIsysUs = isysUs, perFrameLegUs = legUs
        });

        var sb = new StringBuilder();
        sb.AppendLine("=== Input Capability Benchmark ===");
        sb.AppendLine($"FPS {fps} (target {fr})    [1]=60 [2]=144 [3]=uncapped");
        sb.AppendLine($"Keyboard poll: set {keyboardPollingRateHz}Hz, actual {pollHz:F0}Hz    [8]=8000Hz [9]=1000Hz");
        sb.AppendLine($"Native consume: {mode}    [T] toggle (MainThread <-> DedicatedThread)");
        sb.AppendLine("");
        sb.AppendLine("(1) Captured events/sec & min event-time gap   -> move mouse + tap A/S/D/F");
        sb.AppendLine("                  KEYBOARD                    MOUSE");
        sb.AppendLine($"  Native      :  N={natKbN,4}  gap {GapStr(natKbGap)}   |  N={natMouseN,5}  gap {GapStr(natMouseGap)}");
        sb.AppendLine($"  InputSystem :  N={isysKb.count,4}  gap {isysKb.Gap()}   |  N={isysMouse.count,5}  gap {isysMouse.Gap()}");
        sb.AppendLine($"  Legacy      :  N={legKb.count,4}  gap {legKb.Gap()}   |  N={legMouse.count,5}  gap {legMouse.Gap()}  <- frame-bound");
        sb.AppendLine("");
        sb.AppendLine("(2) Reaction latency (event -> game can act), ms avg/max");
        sb.AppendLine($"  Native      :  {natReactAvg,6:F2} / {natReactMx,6:F2}   ({(offThread ? "sub-frame; DedicatedThread" : "frame-bound in Update - press [T] for sub-frame")})");
        sb.AppendLine($"  InputSystem :  {isysReactAvg,6:F2} / {isysReactMx,6:F2}   (frame-bound)");
        sb.AppendLine($"  Legacy      :  {legReactAvg,6:F2} / {legReactMx,6:F2}   (frame-bound, derived from FPS)");
        sb.AppendLine("");
        sb.AppendLine("(3) Resource");
        sb.AppendLine($"  Native input-thread CPU : {natCpuPct,6:F1} %  of 1 core   (8000Hz spins; 1000Hz ~0)");
        sb.AppendLine($"  Process total CPU       : {procCpuPct,6:F1} %  of 1 core   ({cores} cores; incl. consumer thread)");
        sb.AppendLine($"  Working-set memory      : {wsMB,6:F0} MB");
        sb.AppendLine($"  Per-frame our-code (us) : native {natUs:F1}   inputsystem {isysUs:F1}   legacy {legUs:F1}");
        sb.AppendLine("");
        sb.AppendLine($"Recorded {recorder.Count} samples   [F5]=save CSV+TXT   [F6]=clear");
        sb.AppendLine($"  folder: {Application.persistentDataPath}");
        if (savedMsg.Length > 0) sb.AppendLine(savedMsg);
        hud = sb.ToString();

        ResetWindow(now, false);
        lastNatCpu = natCpu; lastProcCpu = procCpu; lastIter = iter;
    }

    string GapStr(double ms) => ms <= 0 ? "   -   " : ms.ToString("F2") + " ms";

    void SaveData()
    {
        try
        {
            string ts = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            string csv = recorder.SaveCsv(Path.Combine(Application.persistentDataPath, $"inputbench_{ts}.csv"));
            string txt = recorder.SaveText(Path.Combine(Application.persistentDataPath, $"inputbench_{ts}.txt"));
            savedMsg = $"Saved {recorder.Count} samples -> {Path.GetFileName(csv)} , {Path.GetFileName(txt)}";
            UnityEngine.Debug.Log($"[Benchmark] Saved:\n  {csv}\n  {txt}");
        }
        catch (Exception e) { savedMsg = "Save failed: " + e.Message; UnityEngine.Debug.LogError(savedMsg); }
    }

    void ResetWindow(float now, bool initBaseline)
    {
        lock (natLock)
        {
            natKb.Reset(); natMouse.Reset();
            natReactSum = natReactMax = 0; natReactCount = 0;
        }
        isysKb.Reset(); isysMouse.Reset();
        legKb.Reset(); legMouse.Reset();
        isysReactSum = isysReactMax = 0; isysReactCount = 0;
        natHandleMs = isysHandleMs = legacyHandleMs = 0;
        frameAccum = 0;
        windowStart = now;
        if (initBaseline)
        {
            lastNatCpu = SafeCpu();
            lastProcCpu = proc.TotalProcessorTime.TotalSeconds;
            lastIter = SafeIter();
        }
    }

    double SafeCpu() { try { return FastInput.GetInputThreadCpuSeconds(); } catch { return 0; } }
    double SafeIter() { try { return FastInput.GetKbPollIterations(); } catch { return 0; } }

    private GUIStyle hudStyle;
    void OnGUI()
    {
        if (hudStyle == null)
        {
            hudStyle = new GUIStyle(GUI.skin.label) { fontSize = 15, richText = false };
            hudStyle.normal.textColor = Color.white;
        }
        GUI.Box(new Rect(8, 8, 800, 520), "");
        GUI.Label(new Rect(18, 14, 784, 508), hud, hudStyle);
    }

    void OnDestroy()
    {
        InputSystem.onEvent -= OnInputSystemEvent;
        threadRunning = false;
        if (consumerThread != null && consumerThread.IsAlive) consumerThread.Join(500);
    }

    void OnApplicationQuit()
    {
        threadRunning = false;
        if (consumerThread != null && consumerThread.IsAlive) consumerThread.Join(500);
        FastInput.ShutdownInput();
    }
}
