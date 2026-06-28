#include <windows.h>
#include <thread>
#include <atomic>
#include <cstring>
#include <cstdlib>

#if defined(_WIN32) || defined(_WIN64)
    #define EXPORT_API extern "C" __declspec(dllexport)
#else
    #define EXPORT_API extern "C" __attribute__((visibility("default")))
#endif

// --- 1. 확장된 데이터 구조체 ---
// [주의] double(timestamp)은 8바이트 정렬을 요구하므로, 5개의 int(20바이트) 뒤에
// 4바이트 패딩이 삽입되어 timestamp는 offset 24, 구조체 전체 크기는 32바이트가 됩니다.
// C# 쪽 InputEvent 도 반드시 동일하게(Size=32, timestamp offset=24) 맞춰야 합니다.
struct InputEvent {
    int type;        // 0 = 키보드, 1 = 마우스
    int vKey;        // [키보드] 가상 키코드
    int state;       // [키보드] 눌림 상태
    int deltaX;      // [마우스] X 누적 이동량
    int deltaY;      // [마우스] Y 누적 이동량
    double timestamp;
};

// --- 2. 전역 상태 ---
const int MAX_EVENTS = 4096;
InputEvent eventQueue[MAX_EVENTS];
int eventCount = 0;                  // [보호됨] queueLock 안에서만 접근
CRITICAL_SECTION queueLock;          // eventQueue / eventCount 동기화
bool queueLockReady = false;         // queueLock 초기화 여부 (메인 스레드 전용)

bool targetKeys[256] = { false };

// 마우스(LL 훅) 스로틀링 변수 — 입력 스레드의 훅 콜백에서만 접근
double mousePollInterval = 0.001;    // 기본 1000Hz
double lastMousePollTime = 0.0;
int accumX = 0;
int accumY = 0;
bool haveLastMousePos = false;
POINT lastMousePos = { 0, 0 };

// 키보드 폴링 변수 — 폴링 스레드에서만 접근
double keyboardPollInterval = 0.001; // 초 단위. 기본 1000Hz
std::atomic<bool> keyboardUseSpin(false); // >1000Hz 는 Sleep 불가 → 스핀 대기 필요
bool prevKeyDown[256] = { false };

std::thread inputThread;             // 마우스 LL 훅 + 메시지 루프
std::thread keyboardPollThread;      // 키보드 GetAsyncKeyState 폴링
std::atomic<bool> isRunning(false);
std::atomic<bool> keyboardPolling(false); // 폴링 스레드 활성 여부 (상태 비트용)
std::atomic<DWORD> inputThreadId(0);
std::atomic<DWORD> pollThreadId(0);       // 키보드 폴링 스레드 ID (CPU 측정용)
std::atomic<long long> kbPollIterations(0); // 키보드 폴링 루프 반복 수 (실측 Hz 계산용)
HANDLE windowReadyEvent = NULL;

std::atomic<int> lifetimeEvents(0);  // [진단용] 큐로 들어간 누적 이벤트 수
std::atomic<int> kbCallbackCount(0); // [진단용] 키보드 폴링이 감지한 키 전이(엣지) 수
std::atomic<int> lastKbVk(-1);       // [진단용] 폴링이 마지막으로 본 키 vkCode

// [핵심 변경 1] 키보드는 WH_KEYBOARD_LL 훅 대신 GetAsyncKeyState 폴링으로 캡처합니다.
//   이유: 일부 보안/안티키로거 SW 는 물리 키 입력을 저수준 키보드 훅으로부터 보호하여,
//   훅이 "설치는 되지만 콜백이 호출되지 않는" 상태가 됩니다(이 환경에서 실제로 확인됨).
//   GetAsyncKeyState 는 훅 체인이 아니라 OS 의 비동기 키 상태를 직접 읽으므로 영향이 적습니다.
// [핵심 변경 2] 마우스는 WH_MOUSE_LL 훅 유지(정상 동작). Unity raw input 과 충돌 없이 공존.
HHOOK mouseHook = NULL;
HINSTANCE g_hInstance = NULL;        // DllMain 에서 캡처 (LL 훅 설치에 필요)

LARGE_INTEGER timerFrequency;
LARGE_INTEGER baseTime;

BOOL WINAPI DllMain(HINSTANCE hinst, DWORD reason, LPVOID reserved) {
    (void)reserved;
    if (reason == DLL_PROCESS_ATTACH) g_hInstance = hinst;
    return TRUE;
}

double GetCurrentTimeSec() {
    LARGE_INTEGER currentTick;
    QueryPerformanceCounter(&currentTick);
    return (double)(currentTick.QuadPart - baseTime.QuadPart) / timerFrequency.QuadPart;
}

// 큐에 이벤트를 안전하게 추가 (마우스 훅 스레드 + 키보드 폴링 스레드에서 호출 → 락 필요)
static void PushEvent(const InputEvent& ev) {
    lifetimeEvents.fetch_add(1, std::memory_order_relaxed);
    EnterCriticalSection(&queueLock);
    if (eventCount < MAX_EVENTS) {
        eventQueue[eventCount] = ev;
        eventCount++;
    }
    LeaveCriticalSection(&queueLock);
}

// --- 3a. 키보드 폴링 스레드 ---
void KeyboardPollLoop()
{
    timeBeginPeriod(1);              // Sleep(1) 의 분해능을 ~1ms 로 높임
    keyboardPolling = true;
    pollThreadId = GetCurrentThreadId();

    // 시작 시점에 이미 눌려있는 키로 인한 가짜 Down 을 막기 위해 기준선 설정
    for (int vk = 0; vk < 256; vk++) {
        prevKeyDown[vk] = (GetAsyncKeyState(vk) & 0x8000) != 0;
    }

    while (isRunning)
    {
        double loopStart = GetCurrentTimeSec();

        for (int vk = 0; vk < 256; vk++)
        {
            if (!targetKeys[vk]) continue;
            bool down = (GetAsyncKeyState(vk) & 0x8000) != 0;
            if (down != prevKeyDown[vk])
            {
                prevKeyDown[vk] = down;
                kbCallbackCount.fetch_add(1, std::memory_order_relaxed);
                lastKbVk.store(vk, std::memory_order_relaxed);

                InputEvent ev;
                ev.type = 0;
                ev.vKey = vk;
                ev.state = down ? 1 : 0;
                ev.deltaX = 0;
                ev.deltaY = 0;
                ev.timestamp = GetCurrentTimeSec();
                PushEvent(ev);
            }
        }
        kbPollIterations.fetch_add(1, std::memory_order_relaxed);

        // 폴링 레이트 페이싱:
        //  - <=1000Hz: Sleep(1) → CPU 거의 0 (스레드가 잠듦)
        //  - >1000Hz : 스핀 대기 → 정밀하지만 코어를 점유(이 비용을 벤치마크로 측정)
        if (keyboardUseSpin)
        {
            while (isRunning && (GetCurrentTimeSec() - loopStart) < keyboardPollInterval)
                YieldProcessor();
        }
        else
        {
            Sleep(1);
        }
    }

    keyboardPolling = false;
    pollThreadId = 0;
    timeEndPeriod(1);
}

// --- 3b. 마우스 저수준 훅 ---
LRESULT CALLBACK LowLevelMouseProc(int nCode, WPARAM wParam, LPARAM lParam)
{
    if (nCode == HC_ACTION && wParam == WM_MOUSEMOVE)
    {
        MSLLHOOKSTRUCT* ms = (MSLLHOOKSTRUCT*)lParam;

        // LL 마우스 훅은 절대 좌표만 제공하므로 델타를 차분으로 계산합니다.
        if (haveLastMousePos)
        {
            accumX += (int)(ms->pt.x - lastMousePos.x);
            accumY += (int)(ms->pt.y - lastMousePos.y);
        }
        lastMousePos = ms->pt;
        haveLastMousePos = true;

        double currentTime = GetCurrentTimeSec();
        if (currentTime - lastMousePollTime >= mousePollInterval)
        {
            if (accumX != 0 || accumY != 0)
            {
                InputEvent ev;
                ev.type = 1;
                ev.vKey = 0;
                ev.state = 0;
                ev.deltaX = accumX;
                ev.deltaY = accumY;
                ev.timestamp = currentTime;
                PushEvent(ev);
            }
            accumX = 0;
            accumY = 0;
            lastMousePollTime = currentTime;
        }
    }
    return CallNextHookEx(NULL, nCode, wParam, lParam);
}

void InputThreadLoop()
{
    mouseHook = SetWindowsHookExA(WH_MOUSE_LL, LowLevelMouseProc, g_hInstance, 0);

    inputThreadId = GetCurrentThreadId();
    if (windowReadyEvent) SetEvent(windowReadyEvent);

    // LL 훅 콜백은 이 스레드의 메시지 펌프 컨텍스트에서 호출되므로 루프가 필요합니다.
    MSG msg;
    while (GetMessage(&msg, NULL, 0, 0))
    {
        TranslateMessage(&msg);
        DispatchMessage(&msg);
    }

    if (mouseHook) { UnhookWindowsHookEx(mouseHook); mouseHook = NULL; }
}

// --- 4. 유니티 C# 노출 API ---

EXPORT_API void SetMousePollingRate(int hz)
{
    if (hz > 0) {
        mousePollInterval = 1.0 / (double)hz;
    }
}

// 키보드 폴링 레이트(Hz). >1000Hz 는 스핀 대기를 사용하므로 CPU 점유가 커집니다.
EXPORT_API void SetKeyboardPollingRate(int hz)
{
    if (hz > 0) {
        keyboardPollInterval = 1.0 / (double)hz;
        keyboardUseSpin = (hz > 1000);
    }
}

EXPORT_API void ResetBaseTime() { QueryPerformanceFrequency(&timerFrequency); QueryPerformanceCounter(&baseTime); }
EXPORT_API double GetHighPrecisionTime() { return GetCurrentTimeSec(); }

EXPORT_API void SetTargetKeys(int* keys, int count) {
    for (int i = 0; i < 256; i++) targetKeys[i] = false;
    for (int i = 0; i < count; i++) { if (keys[i] >= 0 && keys[i] < 256) targetKeys[keys[i]] = true; }
}

EXPORT_API void InitializeInput() {
    if (!isRunning) {
        ResetBaseTime();
        if (!queueLockReady) { InitializeCriticalSection(&queueLock); queueLockReady = true; }

        EnterCriticalSection(&queueLock);
        eventCount = 0;
        LeaveCriticalSection(&queueLock);
        accumX = 0;
        accumY = 0;
        lastMousePollTime = 0.0;
        haveLastMousePos = false;
        inputThreadId = 0;
        lifetimeEvents.store(0);
        kbCallbackCount.store(0);
        lastKbVk.store(-1);
        kbPollIterations.store(0);
        pollThreadId = 0;
        for (int i = 0; i < 256; i++) prevKeyDown[i] = false;

        if (windowReadyEvent == NULL) windowReadyEvent = CreateEventA(NULL, TRUE, FALSE, NULL);
        else ResetEvent(windowReadyEvent);

        isRunning = true;
        inputThread = std::thread(InputThreadLoop);          // 마우스 LL 훅
        keyboardPollThread = std::thread(KeyboardPollLoop);  // 키보드 폴링
    }
}

EXPORT_API int ConsumeEvents(InputEvent* outBuffer, int maxElements) {
    if (!isRunning || !queueLockReady || outBuffer == NULL || maxElements <= 0) return 0;

    EnterCriticalSection(&queueLock);
    int n = (eventCount < maxElements) ? eventCount : maxElements;
    if (n > 0) {
        memcpy(outBuffer, eventQueue, (size_t)n * sizeof(InputEvent));
        int remaining = eventCount - n;
        if (remaining > 0) {
            memmove(eventQueue, eventQueue + n, (size_t)remaining * sizeof(InputEvent));
        }
        eventCount = remaining;
    }
    LeaveCriticalSection(&queueLock);
    return n;
}

EXPORT_API void ShutdownInput() {
    if (isRunning) {
        isRunning = false; // 폴링 스레드는 이 플래그를 보고 곧 종료

        // 마우스 훅 스레드 종료
        if (windowReadyEvent) WaitForSingleObject(windowReadyEvent, 2000);
        if (inputThreadId != 0) PostThreadMessageA(inputThreadId, WM_QUIT, 0, 0);
        if (inputThread.joinable()) inputThread.join();

        // 키보드 폴링 스레드 종료
        if (keyboardPollThread.joinable()) keyboardPollThread.join();

        inputThreadId = 0;
        if (windowReadyEvent) { CloseHandle(windowReadyEvent); windowReadyEvent = NULL; }
        if (queueLockReady) { DeleteCriticalSection(&queueLock); queueLockReady = false; }
    }
}

// --- 리소스/측정용 API ---
// 특정 스레드의 누적 CPU 시간(초) = 커널+유저 시간. 100ns 단위를 초로 환산.
static double ThreadCpuById(DWORD id) {
    if (id == 0) return 0.0;
    HANDLE h = OpenThread(THREAD_QUERY_INFORMATION, FALSE, id);
    if (!h) return 0.0;
    FILETIME c, e, k, u;
    double sec = 0.0;
    if (GetThreadTimes(h, &c, &e, &k, &u)) {
        ULARGE_INTEGER ku, uu;
        ku.LowPart = k.dwLowDateTime; ku.HighPart = k.dwHighDateTime;
        uu.LowPart = u.dwLowDateTime; uu.HighPart = u.dwHighDateTime;
        sec = (double)(ku.QuadPart + uu.QuadPart) / 1.0e7;
    }
    CloseHandle(h);
    return sec;
}
// 네이티브 입력 스레드(마우스 훅 + 키보드 폴링) 누적 CPU 시간(초).
// 일정 간격으로 두 번 읽어 차이를 경과시간으로 나누면 코어 점유율(%)을 얻습니다.
EXPORT_API double GetInputThreadCpuSeconds() {
    return ThreadCpuById(inputThreadId.load()) + ThreadCpuById(pollThreadId.load());
}
// 키보드 폴링 루프 누적 반복 수. (반복수 차이 / 경과시간) = 실측 폴링 Hz.
EXPORT_API double GetKbPollIterations() { return (double)kbPollIterations.load(); }

// --- 진단용 API ---
EXPORT_API int GetBuildTag() { return 20260630; } // 폴링 + 측정 버전
// 비트필드: 1=isRunning, 2=keyboardPolling, 4=mouseHook, 8=threadId (15=모두 정상)
EXPORT_API int GetStatus() {
    int s = 0;
    if (isRunning) s |= 1;
    if (keyboardPolling.load()) s |= 2;
    if (mouseHook != NULL) s |= 4;
    if (inputThreadId.load() != 0) s |= 8;
    return s;
}
EXPORT_API int GetLifetimeEventCount() { return lifetimeEvents.load(); }
// 키보드 폴링이 감지한 키 전이(Down/Up 엣지) 수. >0 이면 키보드 캡처 동작.
EXPORT_API int GetKbCallbackCount() { return kbCallbackCount.load(); }
EXPORT_API int GetLastKbVk() { return lastKbVk.load(); }
EXPORT_API int GetTargetKeyState(int vk) { return (vk >= 0 && vk < 256 && targetKeys[vk]) ? 1 : 0; }
