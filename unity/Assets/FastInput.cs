using System.Runtime.InteropServices;

namespace FastInputAPI
{
    // 메모리 크기와 위치를 강제로 고정하여 쓰레기값을 원천 차단합니다. (총 32바이트)
    // [중요] C++ 쪽 double(timestamp)은 8바이트 정렬이 필요하므로, 5개의 int(20바이트)
    // 뒤에 4바이트 패딩이 들어가 timestamp는 offset 24, 구조체 크기는 32바이트가 됩니다.
    // C# 레이아웃도 반드시 이와 동일해야 합니다. (g++로 sizeof=32, offsetof(timestamp)=24 확인됨)
    [StructLayout(LayoutKind.Explicit, Size = 32)]
    public struct InputEvent
    {
        [FieldOffset(0)]  public int type;      // 0: 키보드, 1: 마우스
        [FieldOffset(4)]  public int vKey;      // 가상 키코드
        [FieldOffset(8)]  public int state;     // 1: Down, 0: Up
        [FieldOffset(12)] public int deltaX;
        [FieldOffset(16)] public int deltaY;
        [FieldOffset(24)] public double timestamp;  // offset 20 -> 24 (정렬 패딩 반영)
    }

    public static class FastInput
    {
        private const string DLL_NAME = "FastInputNative";

        [DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
        public static extern void SetMousePollingRate(int hz);

        [DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
        public static extern void SetKeyboardPollingRate(int hz);

        // 네이티브 입력 스레드 누적 CPU 시간(초) — 리소스 측정용
        [DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
        public static extern double GetInputThreadCpuSeconds();

        // 키보드 폴링 루프 누적 반복 수 — 실측 폴링 Hz 계산용
        [DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
        public static extern double GetKbPollIterations();

        [DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
        public static extern void ResetBaseTime();

        [DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
        public static extern double GetHighPrecisionTime();

        [DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
        public static extern void SetTargetKeys(int[] keys, int count);

        [DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
        public static extern void InitializeInput();

        [DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
        public static extern int ConsumeEvents([Out] FastInputAPI.InputEvent[] buffer, int maxElements);

        [DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
        public static extern void ShutdownInput();

        // --- 진단용 (새 LL-hook DLL 에만 존재) ---
        [DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
        public static extern int GetBuildTag();

        [DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
        public static extern int GetStatus();

        [DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
        public static extern int GetLifetimeEventCount();

        [DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
        public static extern int GetKbCallbackCount();

        [DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
        public static extern int GetLastKbVk();

        [DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
        public static extern int GetTargetKeyState(int vk);
    }
}