using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace CalibOperatorCLI_Example
{
    public enum PlcSendStepKind
    {
        ClearHostFlag,
        WriteSegmentCount,
        WriteGvarBatch,
        SignalHostComplete
    }

    public readonly record struct PlcSendStep(
        PlcSendStepKind Kind,
        int BatchIndex,
        int BarId,
        int SegmentCount);

    /// <summary>
    /// 发送 PLC 的 Modbus 写顺序规划（可单元测试，保证 D804L 不在批次循环内置位）。
    /// </summary>
    public static class SendPlcBatchPlanner
    {
        public static bool ParseBoolParam(string? value, bool defaultValue)
        {
            if (string.IsNullOrWhiteSpace(value))
                return defaultValue;
            string s = value.Trim();
            return s.Equals("true", StringComparison.OrdinalIgnoreCase)
                || s == "1"
                || s.Equals("on", StringComparison.OrdinalIgnoreCase)
                || s.Equals("yes", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// 是否在全部批次写完后置位上位机下发完成标志（D804L 等）。
        /// separate_batch 默认 true；单次下发默认跟随 setWeldDoneHostOnSend。
        /// </summary>
        public static bool ShouldSignalHostAfterAllBatches(
            bool separateBatchMode,
            int batchCount,
            bool setWeldDoneHostOnSend,
            bool setWeldDoneHostAfterAllBatchesParam,
            bool hasHostFlagRegister)
        {
            if (!hasHostFlagRegister)
                return false;
            if (setWeldDoneHostOnSend)
                return true;
            if (separateBatchMode && batchCount > 0)
                return setWeldDoneHostAfterAllBatchesParam;
            return false;
        }

        public static IReadOnlyList<PlcSendStep> BuildSteps(
            bool separateBatchMode,
            IReadOnlyList<(int BarId, int SegmentCount)>? batches,
            int singleSegmentCount,
            bool writeCountPerBatch,
            bool skipCountWrite,
            bool signalHostAfterAllBatches)
        {
            var steps = new List<PlcSendStep>();
            bool writeCount = writeCountPerBatch || !skipCountWrite;

            if (separateBatchMode && batches != null && batches.Count > 0)
            {
                if (signalHostAfterAllBatches)
                    steps.Add(new PlcSendStep(PlcSendStepKind.ClearHostFlag, -1, 0, 0));

                for (int i = 0; i < batches.Count; i++)
                {
                    var (barId, segCount) = batches[i];
                    if (writeCount)
                        steps.Add(new PlcSendStep(PlcSendStepKind.WriteSegmentCount, i, barId, segCount));
                    steps.Add(new PlcSendStep(PlcSendStepKind.WriteGvarBatch, i, barId, segCount));
                }

                if (signalHostAfterAllBatches)
                    steps.Add(new PlcSendStep(PlcSendStepKind.SignalHostComplete, batches.Count, 0, 0));

                return steps;
            }

            if (writeCount)
                steps.Add(new PlcSendStep(PlcSendStepKind.WriteSegmentCount, 0, 0, singleSegmentCount));
            steps.Add(new PlcSendStep(PlcSendStepKind.WriteGvarBatch, 0, 0, singleSegmentCount));
            if (signalHostAfterAllBatches)
            {
                steps.Insert(0, new PlcSendStep(PlcSendStepKind.ClearHostFlag, -1, 0, 0));
                steps.Add(new PlcSendStep(PlcSendStepKind.SignalHostComplete, 1, 0, 0));
            }

            return steps;
        }

        /// <summary>测试/诊断：SignalHostComplete 必须出现在最后一个批次 WriteGvarBatch 之后。</summary>
        public static bool ValidateHostSignalLast(IReadOnlyList<PlcSendStep> steps, out string? error)
        {
            error = null;
            int lastGvar = -1;
            int signal = -1;
            for (int i = 0; i < steps.Count; i++)
            {
                if (steps[i].Kind == PlcSendStepKind.WriteGvarBatch)
                    lastGvar = i;
                if (steps[i].Kind == PlcSendStepKind.SignalHostComplete)
                    signal = i;
            }

            if (signal < 0)
                return true;

            if (lastGvar < 0)
            {
                error = "存在 SignalHostComplete 但无 WriteGvarBatch";
                return false;
            }

            if (signal <= lastGvar)
            {
                error = $"SignalHostComplete 下标 {signal} 不在最后 WriteGvarBatch {lastGvar} 之后";
                return false;
            }

            for (int i = 0; i < signal; i++)
            {
                if (steps[i].Kind == PlcSendStepKind.SignalHostComplete)
                {
                    error = $"批次循环内出现多次 SignalHostComplete（下标 {i}）";
                    return false;
                }
            }

            return true;
        }

        public static string SummarizePlan(IReadOnlyList<PlcSendStep> steps, string hostFlagRegister = "D804L")
        {
            int batches = steps.Count(s => s.Kind == PlcSendStepKind.WriteGvarBatch);
            int d800 = steps.Count(s => s.Kind == PlcSendStepKind.WriteSegmentCount);
            int d804Clear = steps.Count(s => s.Kind == PlcSendStepKind.ClearHostFlag);
            int d804Set = steps.Count(s => s.Kind == PlcSendStepKind.SignalHostComplete);
            var sb = new StringBuilder();
            sb.Append($"批次数={batches}, D800写={d800}, {hostFlagRegister}清0={d804Clear}, {hostFlagRegister}置1={d804Set}");
            if (batches > 0)
            {
                var perBatch = steps
                    .Where(s => s.Kind == PlcSendStepKind.WriteGvarBatch)
                    .Select(s => $"bar{s.BarId}:{s.SegmentCount}")
                    .ToArray();
                sb.Append(" [").Append(string.Join(", ", perBatch)).Append(']');
            }

            return sb.ToString();
        }
    }
}
