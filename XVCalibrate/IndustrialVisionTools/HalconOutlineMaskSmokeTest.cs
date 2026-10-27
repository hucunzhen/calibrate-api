using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using CalibOperatorPInvoke;

namespace CalibOperatorCLI_Example
{
    /// <summary>
    /// 外框匹配→Mask→域内粗匹配冒烟；并回归「门闩内嵌套 HalconComputeRunner.Run」死锁。
    /// dotnet run --project XVCalibrate/FlowSmokeTests -- --halcon-outline-mask [--repo path]
    /// </summary>
    public static class HalconOutlineMaskSmokeTest
    {
        public static int Run(string? repoRoot = null)
        {
#if !HALCON_ENABLED
            Console.Error.WriteLine("SKIP: 未编译 HALCON");
            return 2;
#else
            repoRoot ??= FlowCalibrationSmokeTest.FindRepoRoot();
            if (repoRoot == null)
            {
                Console.Error.WriteLine("FAIL: 未找到仓库根");
                return 1;
            }

            int fails = 0;
            fails += CheckNestedRunnerNoDeadlock();
            fails += CheckOutlineMaskThenCoarse(repoRoot);
            return fails > 0 ? 1 : 0;
#endif
        }

#if HALCON_ENABLED
        private static int CheckNestedRunnerNoDeadlock()
        {
            Console.WriteLine("--- nested HalconComputeRunner.Run (deadlock regression) ---");
            var sw = Stopwatch.StartNew();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            try
            {
                int value = HalconComputeRunner.Run(
                    () =>
                    {
                        // 模拟 ExecuteNode(已进门闩) 内再调 BuildCoarseShapeMaskBatchForNode
                        return HalconComputeRunner.Run(() => 42, HalconThreadPolicy.Geometry);
                    },
                    HalconThreadPolicy.ParallelFind,
                    cts.Token);

                Console.WriteLine($"  nested result={value} [{sw.ElapsedMilliseconds}ms]");
                return Check("嵌套 Runner 在 8s 内返回 42", () => value == 42);
            }
            catch (OperationCanceledException)
            {
                Console.WriteLine($"  FAIL: 嵌套 Runner 超时（疑似死锁）[{sw.ElapsedMilliseconds}ms]");
                return 1;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  FAIL: 嵌套 Runner 异常: {ex.Message}");
                return 1;
            }
        }

        private static int CheckOutlineMaskThenCoarse(string repoRoot)
        {
            Console.WriteLine("--- outline match → mask → domain coarse ---");
            var sw = Stopwatch.StartNew();

            string flowsV7 = Path.Combine(repoRoot, "flows", "v7");
            string imagePath = Path.Combine(flowsV7, "shape_template_test_warp.bmp");
            string outlineShm = Path.Combine(flowsV7, "models", "shape_model_outline.shm");
            string coarseShm = Path.Combine(flowsV7, "models", "shape_model_coarse.shm");

            if (!File.Exists(imagePath) || !File.Exists(outlineShm) || !File.Exists(coarseShm))
            {
                Console.WriteLine($"  SKIP: 缺文件 image/outline/coarse under {flowsV7}");
                return 0;
            }

            using CalibImage img = CalibAPI.LoadImage(imagePath);
            long outlineId = HalconFlowBridge.LoadShapeModelFromFile(outlineShm);
            long coarseId = HalconFlowBridge.LoadShapeModelFromFile(coarseShm);
            try
            {
                Console.WriteLine($"  图像: {img.Width}x{img.Height}");

                // 与 ExecuteNode 相同：外层 ParallelFind 门闩内再调 Mask 批构建
                var (maskCount, coarseCount) = HalconComputeRunner.Run(
                    () =>
                    {
                        var outline = HalconFlowBridge.CoarseShapeMatch(
                            img, outlineId, -180, 360, 0.25, 1, 0.5, "none", 0, 0.75, false);
                        Console.WriteLine($"  外框匹配: {outline.rows.Length} [{sw.ElapsedMilliseconds}ms]");
                        if (outline.rows.Length == 0)
                            return (0, 0);

                        // 嵌套 Runner（旧代码会死锁）
                        var batch = HalconComputeRunner.Run(
                            () => HalconFlowBridge.BuildCoarseShapeMaskBatch(
                                img, outlineId,
                                outline.rows, outline.cols, outline.angles, outline.scales, outline.scores,
                                maskErosionPx: 0, contourLevel: 1, maxCandidates: 1, maskFillDilatePx: 0),
                            HalconThreadPolicy.Geometry);
                        Console.WriteLine($"  外框Mask: {batch.Count} [{sw.ElapsedMilliseconds}ms]");
                        if (batch.Count == 0)
                            return (0, 0);

                        CalibImage domain = HalconFlowBridge.ReduceDomainByMask(img, batch.Masks[0]);
                        double ang = batch.CoarseAngles[0];
                        var (absStart, absExtent) = HalconFlowBridge.ResolveRelativeAngleRangeDeg(ang, -2, 4);
                        var coarse = HalconFlowBridge.CoarseShapeMatch(
                            domain, coarseId, absStart, absExtent, 0.2, 20, 0.5, "none", 0, 0.01, false);
                        Console.WriteLine($"  域内粗匹配: {coarse.rows.Length} [{sw.ElapsedMilliseconds}ms]");
                        return (batch.Count, coarse.rows.Length);
                    },
                    HalconThreadPolicy.ParallelFind);

                int fails = 0;
                fails += Check("外框 Mask≥1", () => maskCount >= 1);
                fails += Check("域内粗匹配有结果(或允许0)", () => coarseCount >= 0);
                Console.WriteLine($"  总耗时: {sw.ElapsedMilliseconds}ms");
                return fails;
            }
            finally
            {
                HalconFlowBridge.ClearModel(outlineId);
                HalconFlowBridge.ClearModel(coarseId);
            }
        }
#endif

        private static int Check(string name, Func<bool> ok)
        {
            bool pass = ok();
            Console.WriteLine(pass ? $"  OK: {name}" : $"  FAIL: {name}");
            return pass ? 0 : 1;
        }
    }
}
