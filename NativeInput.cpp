#include <iostream>

#if defined(_WIN32) || defined(_WIN64)
    #define EXPORT_API extern "C" __declspec(dllexport)
#else
    #define EXPORT_API extern "C" __attribute__((visibility("default")))
#endif

EXPORT_API int TestConnection()
{
    return 8000;
}