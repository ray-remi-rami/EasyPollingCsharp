using System;
using System.Text;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using FastInputAPI;

public class KeyboardBurstBenchmark : MonoBehaviour
{
    public struct CapturedEvent
    {
        public string key;
        public string state;
        public double timestamp;
    }

    private int[] targetKeys = { 0x41, 0x53, 0x44, 0x46 }; // A, S, D, F
    // Explicitly use FastInputAPI.InputEvent to avoid name clashes.
    private FastInputAPI.InputEvent[] nativeBuffer = new FastInputAPI.InputEvent[4096];

    private List<CapturedEvent> legacyEvents = new List<CapturedEvent>();
    private List<CapturedEvent> newSysEvents = new List<CapturedEvent>();
    private List<CapturedEvent> nativeEvents = new List<CapturedEvent>();

    private InputAction actionA, actionS, actionD, actionF;

    private bool isRecording = false;
    private bool hasFinished = false;
    private double globalStartTime = -1;
    private double qpcOffset = 0;

    private const double RECORD_DURATION = 1.0; // measure for 1 second

    // [Diagnostics] counters to narrow down why native capture could be 0
    private int dbgTotalConsumed = 0;   // total returned by ConsumeEvents across frames
    private int dbgKeyConsumed = 0;     // of those, keyboard (type==0) events
    private bool dbgFirstNativeLogged = false;
    private bool dbgFirstKeyLogged = false;

    void Awake()
    {
        FastInput.SetTargetKeys(targetKeys, targetKeys.Length);
        FastInput.InitializeInput();

        // [Diagnostics] confirm the new DLL is actually loaded. An old DLL has no GetBuildTag
        // export, so this throws EntryPointNotFoundException = "Unity is holding the old DLL".
        try
        {
            int tag = FastInput.GetBuildTag();
            Debug.Log($"<color=lime>[DIAG] DLL BuildTag = {tag}</color>");
        }
        catch (System.Exception e)
        {
            Debug.LogError($"[DIAG] An old DLL is loaded! (GetBuildTag missing) -> fully quit Unity and relaunch. {e.GetType().Name}: {e.Message}");
        }

        SetupNewInputSystem();

        Debug.Log("<b>[3-system burst benchmark ready]</b>\n<color=white>Press any of A, S, D, F to set the zero-point (0ms) and start measuring.</color>");
    }

    void SetupNewInputSystem()
    {
        // InputActionType.Button must be specified for events to be captured correctly.
        actionA = new InputAction(name: "A", type: InputActionType.Button, binding: "<Keyboard>/a");
        actionS = new InputAction(name: "S", type: InputActionType.Button, binding: "<Keyboard>/s");
        actionD = new InputAction(name: "D", type: InputActionType.Button, binding: "<Keyboard>/d");
        actionF = new InputAction(name: "F", type: InputActionType.Button, binding: "<Keyboard>/f");

        actionA.performed += ctx => RecordNewSystemEvent("A", "Down", ctx.time);
        actionA.canceled += ctx => RecordNewSystemEvent("A", "Up", ctx.time);

        actionS.performed += ctx => RecordNewSystemEvent("S", "Down", ctx.time);
        actionS.canceled += ctx => RecordNewSystemEvent("S", "Up", ctx.time);

        actionD.performed += ctx => RecordNewSystemEvent("D", "Down", ctx.time);
        actionD.canceled += ctx => RecordNewSystemEvent("D", "Up", ctx.time);

        actionF.performed += ctx => RecordNewSystemEvent("F", "Down", ctx.time);
        actionF.canceled += ctx => RecordNewSystemEvent("F", "Up", ctx.time);

        actionA.Enable(); actionS.Enable(); actionD.Enable(); actionF.Enable();
    }

    void RecordNewSystemEvent(string key, string state, double eventTime)
    {
        if (hasFinished) return;
        double syncTime = eventTime + qpcOffset;
        CheckStart(state == "Down", syncTime);

        if (isRecording)
        {
            newSysEvents.Add(new CapturedEvent { key = key, state = state, timestamp = syncTime });
        }
    }

    void Update()
    {
        qpcOffset = FastInput.GetHighPrecisionTime() - Time.realtimeSinceStartupAsDouble;
        double currentQpcTime = FastInput.GetHighPrecisionTime();

        // End-of-measurement check (1 second)
        if (isRecording && !hasFinished)
        {
            if (currentQpcTime - globalStartTime >= RECORD_DURATION)
            {
                hasFinished = true;
                isRecording = false;
                PrintResults();
                return;
            }
        }

        // --- 1. Native Plugin ---
        int nativeCount = FastInput.ConsumeEvents(nativeBuffer, nativeBuffer.Length);
        dbgTotalConsumed += nativeCount;
        if (nativeCount > 0 && !dbgFirstNativeLogged)
        {
            dbgFirstNativeLogged = true;
            Debug.Log($"<color=lime>[DIAG] First native event received! nativeCount={nativeCount}, type0={nativeBuffer[0].type}, vKey=0x{nativeBuffer[0].vKey:X2}, state={nativeBuffer[0].state}</color>");
        }
        // [Diagnostics] separately track whether keyboard (type==0) events actually arrive
        for (int i = 0; i < nativeCount; i++)
        {
            if (nativeBuffer[i].type == 0)
            {
                dbgKeyConsumed++;
                if (!dbgFirstKeyLogged)
                {
                    dbgFirstKeyLogged = true;
                    Debug.Log($"<color=lime>[DIAG] First 'keyboard' native event received! vKey=0x{nativeBuffer[i].vKey:X2}, state={nativeBuffer[i].state} -> keyboard capture OK</color>");
                }
            }
        }
        if (!hasFinished)
        {
            for (int i = 0; i < nativeCount; i++)
            {
                if (nativeBuffer[i].type == 0) // keyboard events only
                {
                    bool isDown = (nativeBuffer[i].state == 1);
                    double evTime = nativeBuffer[i].timestamp;

                    CheckStart(isDown, evTime);

                    if (isRecording)
                    {
                        string kName = GetKeyName(nativeBuffer[i].vKey);
                        string kState = isDown ? "Down" : "Up";
                        nativeEvents.Add(new CapturedEvent { key = kName, state = kState, timestamp = evTime });
                    }
                }
            }
        }

        // --- 2. Legacy Input ---
        if (!hasFinished)
        {
            CheckLegacyKey(KeyCode.A, "A", currentQpcTime);
            CheckLegacyKey(KeyCode.S, "S", currentQpcTime);
            CheckLegacyKey(KeyCode.D, "D", currentQpcTime);
            CheckLegacyKey(KeyCode.F, "F", currentQpcTime);
        }
    }

    void CheckLegacyKey(KeyCode code, string keyName, double time)
    {
        if (Input.GetKeyDown(code))
        {
            CheckStart(true, time);
            if (isRecording) legacyEvents.Add(new CapturedEvent { key = keyName, state = "Down", timestamp = time });
        }
        if (Input.GetKeyUp(code))
        {
            CheckStart(false, time);
            if (isRecording) legacyEvents.Add(new CapturedEvent { key = keyName, state = "Up", timestamp = time });
        }
    }

    void CheckStart(bool isDownEvent, double exactEventTime)
    {
        if (!isRecording && !hasFinished && isDownEvent)
        {
            // Use the exact time of the very first real event as the zero-point.
            isRecording = true;
            globalStartTime = exactEventTime;
            Debug.Log("<color=yellow><b>Measurement started! (auto-stops after 1 second)</b></color>");
        }
    }

    void PrintResults()
    {
        StringBuilder sb = new StringBuilder();
        sb.AppendLine($"<color=cyan><b>[1-second ASDF burst - 3-system precision benchmark]</b></color>");
        sb.AppendLine($"<color=red><b>(input window closed.)</b></color>\n");

        sb.AppendLine($"Total detected (Native): <color=green><b>{nativeEvents.Count}</b></color>");
        sb.AppendLine($"Total detected (New Sys): <color=yellow><b>{newSysEvents.Count}</b></color>");
        sb.AppendLine($"Total detected (Legacy): <color=orange><b>{legacyEvents.Count}</b></color>\n");

        // [Diagnostics] locate the cause when native is 0: DLL capture vs benchmark record logic
        int dllStatus = 0, dllLifetime = -1;
        try { dllStatus = FastInput.GetStatus(); dllLifetime = FastInput.GetLifetimeEventCount(); } catch { }
        sb.AppendLine($"<color=lime>[DIAG] DLL Status={dllStatus} (15=all OK: run|kbPoll|msHook|thread), " +
                      $"DLL lifetimeCaptured={dllLifetime}, ConsumeEvents total={dbgTotalConsumed}, of-which keyboard={dbgKeyConsumed}</color>");
        sb.AppendLine($"<color=lime>[DIAG] Reading: keyboard>0 = capture OK (Native=0 would be record logic) / keyboard=0 but total>0 = mouse only (keyboard capture not working) / Status missing bit 2 = capture install failed</color>");

        // [Diagnostics2] confirm the keyboard polling actually detects keys + targetKeys registration
        int kbcb = -1, lastVk = -2, tkA = -1, tkS = -1;
        try {
            kbcb = FastInput.GetKbCallbackCount();
            lastVk = FastInput.GetLastKbVk();
            tkA = FastInput.GetTargetKeyState(0x41);
            tkS = FastInput.GetTargetKeyState(0x53);
        } catch { }
        sb.AppendLine($"<color=cyan>[DIAG2] keyboardPollDetections={kbcb}, lastVkCode=0x{(lastVk < 0 ? 0 : lastVk):X2}({lastVk}), targetKeys[A]={tkA}, targetKeys[S]={tkS}</color>");
        sb.AppendLine($"<color=cyan>[DIAG2] Reading (polling): detections>0 = keyboard capture OK / detections=0 with targetKeys[A]=1 = GetAsyncKeyState also blocked (needs another approach)</color>\n");

        sb.AppendLine("<b>--- [Native Plugin timeline] ---</b>");
        AppendEvents(sb, nativeEvents);

        sb.AppendLine("\n<b>--- [New Input System timeline] ---</b>");
        AppendEvents(sb, newSysEvents);

        sb.AppendLine("\n<b>--- [Legacy Input timeline] ---</b>");
        AppendEvents(sb, legacyEvents);

        Debug.Log(sb.ToString());
    }

    void AppendEvents(StringBuilder sb, List<CapturedEvent> events)
    {
        if (events.Count == 0)
        {
            sb.AppendLine("  (no events recorded)");
            return;
        }

        foreach (var ev in events)
        {
            // Relative time from the zero-point
            double relativeSec = ev.timestamp - globalStartTime;

            // Guard against negative values from clock-axis error
            string sign = relativeSec < 0 ? "-" : "+";
            double absSec = Math.Abs(relativeSec);

            // Split into integer ms and microsecond parts
            int ms = (int)Math.Floor(absSec * 1000.0);
            int us = (int)Math.Floor((absSec * 1000000.0) % 1000.0);

            string stateFormat = ev.state.PadRight(4);

            sb.AppendLine($"[{sign}{ms,3} ms {us,3} us] Key: {ev.key} | {stateFormat}");
        }
    }

    string GetKeyName(int vKey)
    {
        switch (vKey) { case 0x41: return "A"; case 0x53: return "S"; case 0x44: return "D"; case 0x46: return "F"; default: return "?"; }
    }

    void OnDestroy()
    {
        if (actionA != null) { actionA.Disable(); actionS.Disable(); actionD.Disable(); actionF.Disable(); }
    }

    void OnApplicationQuit()
    {
        FastInput.ShutdownInput();
    }
}
