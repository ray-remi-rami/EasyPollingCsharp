#include <windows.h>
#include <thread>
#include <atomic>
#include <iostream>
#include <cstring> // memcpy

#if defined(_WIN32) || defined(_WIN64)
    #define EXPORT_API extern "C" __declspec(dllexport)
#else
    #define EXPORT_API extern "C" __attribute__((visibility("default")))
#endif

// --- 1. 데이터 구조체 정의 ---
// C#의 구조체와 메모리 레이아웃이 정확히 일치해야 합니다.
struct InputEvent {
    int vKey;
    bool isPressed;
    double timestamp; // 0.0000000 꼴의 초 단위 (Zero-point 기준)
};

// --- 시간 동기화 전역 변수 ---
LARGE_INTEGER timerFrequency;
LARGE_INTEGER baseTime;

// --- 2. 전역 및 공유 변수 ---
const int MAX_EVENTS = 1024;
InputEvent eventQueue[MAX_EVENTS]; // C++ 내부 이벤트 버퍼
std::atomic<int> eventCount(0);    // 현재 쌓인 이벤트 개수 (Lock-free 연산용)

bool targetKeys[256] = { false };  // C#에서 지정한 관측 대상 키 필터

std::thread inputThread;
HWND messageWindow = NULL;
std::atomic<bool> isRunning(false);

// --- 3. 윈도우 메시지 처리 콜백 (Raw Input 후킹) ---
LRESULT CALLBACK WindowProc(HWND hwnd, UINT uMsg, WPARAM wParam, LPARAM lParam) 
{
    if (uMsg == WM_INPUT) 
    {
        UINT dwSize = 0;
        GetRawInputData((HRAWINPUT)lParam, RID_INPUT, NULL, &dwSize, sizeof(RAWINPUTHEADER));
        
        if (dwSize > 0) 
        {
            RAWINPUT* raw = (RAWINPUT*)malloc(dwSize);
            if (GetRawInputData((HRAWINPUT)lParam, RID_INPUT, raw, &dwSize, sizeof(RAWINPUTHEADER)) == dwSize) 
            {
                if (raw->header.dwType == RIM_TYPEKEYBOARD) 
                {
                    USHORT vKey = raw->data.keyboard.VKey;
                    USHORT flags = raw->data.keyboard.Flags;
                    
                    // 필터링: 등록된 관심 키(Target Key)인지 확인
                    if (vKey < 256 && targetKeys[vKey]) 
                    {
                        int idx = eventCount.fetch_add(1);
                        if (idx < MAX_EVENTS) 
                        {
                            eventQueue[idx].vKey = vKey;
                            eventQueue[idx].isPressed = !(flags & RI_KEY_BREAK);
                            
                            // 2. 입력이 발생한 정확한 시점(초) 계산
                            LARGE_INTEGER currentTick;
                            QueryPerformanceCounter(&currentTick);
                            // (현재 틱 - 기준 틱) / 주파수 = 기준점으로부터 경과한 시간(초)
                            eventQueue[idx].timestamp = (double)(currentTick.QuadPart - baseTime.QuadPart) / timerFrequency.QuadPart;
                        }
                        else {
                            eventCount.fetch_sub(1);
                        }
                    }
                }
            }
            free(raw);
        }
        return 0; // 처리가 완료되었으므로 0 반환
    }
    return DefWindowProc(hwnd, uMsg, wParam, lParam);
}

// --- 4. 백그라운드 스레드 루프 ---
void InputThreadLoop() 
{
    HINSTANCE hInstance = GetModuleHandle(NULL);
    
    // 유니코드 에러 방지를 위해 ASCII 버전(WNDCLASSA) 사용
    WNDCLASSA wc = {0};
    wc.lpfnWndProc = WindowProc;
    wc.hInstance = hInstance;
    wc.lpszClassName = "FastInputHiddenWindow";
    
    RegisterClassA(&wc);

    // 메시지 전용(Message-Only) 숨겨진 창 생성
    messageWindow = CreateWindowExA(0, wc.lpszClassName, "FastInput", 0, 0, 0, 0, 0, HWND_MESSAGE, NULL, hInstance, NULL);

    // Raw Input 디바이스 등록 (키보드)
    RAWINPUTDEVICE rid[1];
    rid[0].usUsagePage = 0x01; 
    rid[0].usUsage = 0x06;     
    rid[0].dwFlags = RIDEV_INPUTSINK; // 백그라운드 상태에서도 입력 수신
    rid[0].hwndTarget = messageWindow;
    RegisterRawInputDevices(rid, 1, sizeof(rid[0]));

    // 메시지 루프 가동
    MSG msg;
    while (GetMessage(&msg, NULL, 0, 0)) 
    {
        TranslateMessage(&msg);
        DispatchMessage(&msg);
    }
}

// --- 5. 유니티(C#)로 노출할 외부 API ---

// (1) 관심 키 등록 (C# 배열 복사)
EXPORT_API void SetTargetKeys(int* keys, int count) 
{
    for (int i = 0; i < 256; i++) targetKeys[i] = false; // 기존 필터 초기화
    for (int i = 0; i < count; i++) 
    {
        if (keys[i] >= 0 && keys[i] < 256) {
            targetKeys[keys[i]] = true;
        }
    }
}

// (3) 이벤트 쓸어오기 (C# 버퍼로 메모리 복사)
EXPORT_API int ConsumeEvents(InputEvent* outBuffer, int maxElements) 
{
    // 현재까지 쌓인 카운트를 가져오고, 즉시 0으로 초기화 (원자적 연산)
    int currentCount = eventCount.exchange(0); 
    
    // C#에서 넘겨준 버퍼 크기를 초과하지 않도록 안전장치 적용
    int elementsToCopy = (currentCount > maxElements) ? maxElements : currentCount;
    
    if (elementsToCopy > 0) {
        // C++ 내부 큐 -> C# 배열 버퍼로 O(1) 통째로 복사 (GC 발생 제로)
        memcpy(outBuffer, eventQueue, elementsToCopy * sizeof(InputEvent));
    }
    
    return elementsToCopy; // 유니티에게 실제로 넘겨준 이벤트 개수 반환
}

// (4) 엔진 종료 (좀비 스레드 방어)
EXPORT_API void ShutdownInput() 
{
    if (isRunning) 
    {
        isRunning = false;
        if (messageWindow != NULL) {
            PostMessage(messageWindow, WM_QUIT, 0, 0); // 메시지 루프 탈출 신호
        }
        if (inputThread.joinable()) {
            inputThread.join(); // 스레드가 안전하게 종료될 때까지 대기
        }
    }
}

// 3. 타이머 기준점(Zero-point) 초기화 함수
// 게임 시작 또는 음악 재생 시점에 C#에서 호출합니다.
EXPORT_API void ResetBaseTime() 
{
    QueryPerformanceFrequency(&timerFrequency);
    QueryPerformanceCounter(&baseTime);
}

// 4. 현재 고정밀 시간 가져오기
// C# 로직 스레드나 메인 스레드에서 "현재 게임 타임"을 확인하고 싶을 때 호출합니다.
EXPORT_API double GetHighPrecisionTime() 
{
    LARGE_INTEGER currentTick;
    QueryPerformanceCounter(&currentTick);
    return (double)(currentTick.QuadPart - baseTime.QuadPart) / timerFrequency.QuadPart;
}

EXPORT_API void InitializeInput() 
{
    if (!isRunning) 
    {
        ResetBaseTime(); // 엔진 시작 시 기본적으로 한 번 초기화
        eventCount.store(0);
        isRunning = true;
        inputThread = std::thread(InputThreadLoop);
    }
}