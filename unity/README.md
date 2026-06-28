# Unity integration (C#)

These are the Unity-side C# files that pair with `FastInputNative.dll`.
Copy them into your Unity project's `Assets/` folder, and put the built DLL in `Assets/Plugins/`.

| File | Purpose |
|---|---|
| `FastInput.cs` | P/Invoke bindings + the `InputEvent` struct (must stay binary-compatible with the C++ struct: 32 bytes, `timestamp` at offset 24). |
| `InputBenchmark.cs` | `KeyboardBurstBenchmark` — 1-second A/S/D/F burst comparison of Native vs New Input System vs Legacy. |
| `InputCapabilityBenchmark.cs` | `InputCapabilityBenchmark` — live HUD: per-device throughput, min event-time gap, reaction latency (with `[T]` MainThread/DedicatedThread toggle), and resource usage. |
| `BenchmarkRecorder.cs` | `FastInputBench.BenchmarkRecorder` — collects per-second samples and exports CSV / text. |
| `FastInputManager.cs` | Empty placeholder. |

Notes:
- Keyboard capture uses `GetAsyncKeyState` polling (works alongside anti-keylogger software that blocks low-level keyboard hooks); mouse uses a `WH_MOUSE_LL` hook.
- A native DLL cannot be hot-reloaded — fully restart the Unity Editor after replacing `FastInputNative.dll`.
- Only run ONE benchmark component at a time (both call `ConsumeEvents`, which drains the shared queue).
