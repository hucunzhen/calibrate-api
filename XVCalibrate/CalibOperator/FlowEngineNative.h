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
/// @param flowRootDirectoryOrNull 主流程 .flow.json 所在目录（UTF-8），用于相对 innerFlowPath / 标定文件等；可为 nullptr。
int FlowEngine_LoadFromJson(NativeFlowEngineHandle handle, const char* flowJsonText, const char* flowRootDirectoryOrNull = nullptr);
NativeFlowRunResult FlowEngine_Run(NativeFlowEngineHandle handle);

const char* FlowEngine_GetLastError(NativeFlowEngineHandle handle);
const char* FlowEngine_GetLastReportJson(NativeFlowEngineHandle handle);
