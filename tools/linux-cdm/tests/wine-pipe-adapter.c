

#include <windows.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
static HANDLE guest, pipeHandle;
static DWORD WINAPI pumpInput(void* ignored) {
    char buffer[65536]; DWORD count, written;
    while (ReadFile(GetStdHandle(STD_INPUT_HANDLE), buffer, sizeof(buffer), &count, NULL) && count) {
        DWORD at = 0;
        while (at < count) {
            if (!WriteFile(pipeHandle, buffer + at, count - at, &written, NULL) || !written) return 1;
            at += written;
        }
    }
    return 0;
}
static DWORD WINAPI pumpOutput(void* ignored) {
    char buffer[65536]; DWORD count, written;
    while (ReadFile(pipeHandle, buffer, sizeof(buffer), &count, NULL) && count) {
        DWORD at = 0;
        while (at < count) {
            if (!WriteFile(GetStdHandle(STD_OUTPUT_HANDLE), buffer + at, count - at, &written, NULL) || !written) return 1;
            at += written;
        }
    }
    return 0;
}
int main(int argc, char** argv) {
    if (argc < 3) return 2;
    char command[32768] = "", name[256];
    for (int i = 2; i < argc; ++i) {
        if (strchr(argv[i], '"') || strlen(command) + strlen(argv[i]) + 4 >= sizeof(command)) return 2;
        strcat(command, "\""); strcat(command, argv[i]); strcat(command, "\" ");
    }
    STARTUPINFOA start = {0}; PROCESS_INFORMATION process = {0}; start.cb = sizeof(start);
    if (!CreateProcessA(NULL, command, NULL, NULL, TRUE, 0, NULL, NULL, &start, &process)) return 3;
    guest = process.hProcess; CloseHandle(process.hThread);
    snprintf(name, sizeof(name), "\\\\.\\pipe\\%s", argv[1]);
    pipeHandle = INVALID_HANDLE_VALUE;
    for (int i = 0; i < 1000 && pipeHandle == INVALID_HANDLE_VALUE; ++i) {
        pipeHandle = CreateFileA(name, GENERIC_READ | GENERIC_WRITE, 0, NULL, OPEN_EXISTING, 0, NULL);
        if (pipeHandle == INVALID_HANDLE_VALUE) {
            if (WaitForSingleObject(guest, 10) == WAIT_OBJECT_0) return 4;
        }
    }
    if (pipeHandle == INVALID_HANDLE_VALUE) { TerminateProcess(guest, 5); return 5; }
    HANDLE input = CreateThread(NULL, 0, pumpInput, NULL, 0, NULL);
    HANDLE output = CreateThread(NULL, 0, pumpOutput, NULL, 0, NULL);
    if (!input || !output) { TerminateProcess(guest, 6); return 6; }
    WaitForSingleObject(guest, INFINITE);
    WaitForSingleObject(output, 1000);
    DWORD status; GetExitCodeProcess(guest, &status); CloseHandle(guest); CloseHandle(pipeHandle);
    return status;
}
