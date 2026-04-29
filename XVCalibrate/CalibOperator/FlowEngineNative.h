#pragma once

#include "CalibOperator.h"
#include <string>

struct NativeFlowRunResult {
    int success;
    int executedNodes;
    int totalNodes;
};

// opaque handle
typedef void* NativeFlowEngineHandle;

NativeFlowEngineHandle FlowEngine_Create();
void FlowEngine_Free(NativeFlowEngineHandle handle);

int FlowEngine_LoadFromFile(NativeFlowEngineHandle handle, const char* flowFilePath);
int FlowEngine_LoadFromJson(NativeFlowEngineHandle handle, const char* flowJsonText);
NativeFlowRunResult FlowEngine_Run(NativeFlowEngineHandle handle);

const char* FlowEngine_GetLastError(NativeFlowEngineHandle handle);
const char* FlowEngine_GetLastReportJson(NativeFlowEngineHandle handle);
