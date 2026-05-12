// Split from CalibOperatorPInvoke.cs — Native flow engine + run result.

using System;
using System.Linq;
using System.Text;
using System.Runtime.InteropServices;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Collections.Generic;

namespace CalibOperatorPInvoke
{
    public class FlowEngineRunResult
    {
        public bool Success { get; set; }
        public int ExecutedNodes { get; set; }
        public int TotalNodes { get; set; }
        public string ErrorMessage { get; set; } = "";
        public string ReportJson { get; set; } = "{}";
    }

    /// <summary>
    /// Native Flow Engine wrapper (flow.json parsed and executed in C++).
    /// </summary>
    public sealed class NativeFlowEngine : IDisposable
    {
        private IntPtr _ctx;
        private bool _disposed;

        public NativeFlowEngine()
        {
            _ctx = NativeAPI.CALIB_FlowEngine_Create();
            if (_ctx == IntPtr.Zero) throw new OutOfMemoryException("Failed to create native flow engine");
        }

        public void Dispose()
        {
            if (_disposed) return;
            if (_ctx != IntPtr.Zero)
            {
                NativeAPI.CALIB_FlowEngine_Free(_ctx);
                _ctx = IntPtr.Zero;
            }
            _disposed = true;
            GC.SuppressFinalize(this);
        }

        ~NativeFlowEngine()
        {
            Dispose();
        }

        private static string PtrToUtf8String(IntPtr ptr)
        {
            return ptr == IntPtr.Zero ? "" : (Marshal.PtrToStringAnsi(ptr) ?? "");
        }

        public void LoadFromFile(string flowPath)
        {
            if (string.IsNullOrWhiteSpace(flowPath))
                throw new ArgumentException("flowPath is empty", nameof(flowPath));
            int rc = NativeAPI.CALIB_FlowEngine_LoadFromFile(_ctx, flowPath);
            if (rc != 0)
                throw new InvalidOperationException("NativeFlowEngine load failed: " + PtrToUtf8String(NativeAPI.CALIB_FlowEngine_GetLastError(_ctx)));
        }

        /// <param name="flowDirectoryForRelativePaths">当前组态文件所在目录；未保存到磁盘时可不传。</param>
        public void LoadFromJson(string flowJson, string? flowDirectoryForRelativePaths = null)
        {
            if (string.IsNullOrWhiteSpace(flowJson))
                throw new ArgumentException("flowJson is empty", nameof(flowJson));
            int rc = NativeAPI.CALIB_FlowEngine_LoadFromJson(_ctx, flowJson, flowDirectoryForRelativePaths);
            if (rc != 0)
                throw new InvalidOperationException("NativeFlowEngine load failed: " + PtrToUtf8String(NativeAPI.CALIB_FlowEngine_GetLastError(_ctx)));
        }

        public FlowEngineRunResult Run()
        {
            var r = NativeAPI.CALIB_FlowEngine_Run(_ctx);
            return new FlowEngineRunResult
            {
                Success = r.success != 0,
                ExecutedNodes = r.executedNodes,
                TotalNodes = r.totalNodes,
                ErrorMessage = PtrToUtf8String(NativeAPI.CALIB_FlowEngine_GetLastError(_ctx)),
                ReportJson = PtrToUtf8String(NativeAPI.CALIB_FlowEngine_GetLastReportJson(_ctx))
            };
        }
    }
}
