using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using CalibOperatorPInvoke;

namespace CalibOperatorCLI_Example
{
    /// <summary>
    /// 命令行冒烟测试：标定 JSON 路径解析 + affine 加载 + 坐标转换（不启动 WPF）。
    /// 运行：dotnet run --project XVCalibrate/FlowSmokeTests/FlowSmokeTests.csproj
    /// </summary>
    public static class FlowCalibrationSmokeTest
    {
        private sealed class AffineDto
        {
            public double A { get; set; }
            public double B { get; set; }
            public double C { get; set; }
            public double D { get; set; }
            public double E { get; set; }
            public double F { get; set; }

            public AffineTransform ToAffine() =>
                new AffineTransform { A = A, B = B, C = C, D = D, E = E, F = F };
        }

        private sealed class CalibFileDto
        {
            public int SchemaVersion { get; set; }
            public AffineDto? Affine { get; set; }
        }

        public static int Run(string? repoRoot = null)
        {
            repoRoot ??= FindRepoRoot();
            if (repoRoot == null)
            {
                Console.Error.WriteLine("FAIL: 未找到仓库根（含 flows/halcon/calibration_result.json）");
                return 1;
            }

            string halconDir = Path.Combine(repoRoot, "flows", "halcon");
            string caliFlow = Path.Combine(halconDir, "cali.flow.json");
            string calJson = Path.Combine(halconDir, "calibration_result.json");

            int fails = 0;
            fails += Check("cali.flow.json 存在", () => File.Exists(caliFlow));
            fails += Check("calibration_result.json 存在", () => File.Exists(calJson));

            string resolved = ResolveFlowRelativePath("calibration_result.json", halconDir);
            fails += Check("相对路径解析到 halcon/calibration_result.json",
                () => string.Equals(resolved, Path.GetFullPath(calJson), StringComparison.OrdinalIgnoreCase));

            var jOpts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            var dto = JsonSerializer.Deserialize<CalibFileDto>(File.ReadAllText(calJson), jOpts);
            fails += Check("JSON 含 schemaVersion>=1", () => dto != null && dto.SchemaVersion >= 1);
            fails += Check("JSON 含 affine", () => dto?.Affine != null);

            if (dto?.Affine == null)
            {
                Console.Error.WriteLine("FAIL: 无法继续坐标转换测试（无 affine）");
                return 1;
            }

            var aff = dto.Affine.ToAffine();
            var pixel = new Point2D(100, 200);
            var world = CalibAPI.ImageToWorld(pixel, aff);
            fails += Check("ImageToWorld 返回有限坐标",
                () => double.IsFinite(world.X) && double.IsFinite(world.Y));

            Console.WriteLine($"  pixel ({pixel.X},{pixel.Y}) -> world ({world.X:F3},{world.Y:F3})");

            // 模拟 cali.flow 子图：仅 Transform→img_to_world 有连线，CalibrationJson 无连线
            var innerWired = new HashSet<(Guid, string)>
            {
                (Guid.Parse("a33020a5-000d-40cf-aed9-1813123d35f5"), "Transform")
            };
            fails += Check("子图内 Transform 应写出", () => innerWired.Contains((Guid.Parse("a33020a5-000d-40cf-aed9-1813123d35f5"), "Transform")));
            fails += Check("子图内未连 CalibrationJson 不应写出", () => !innerWired.Contains((Guid.Parse("a33020a5-000d-40cf-aed9-1813123d35f5"), "CalibrationJson")));

            fails += RunSendPlcBatchPlannerTests();
            RunSendPlcDispatchReport();

            if (fails == 0)
            {
                Console.WriteLine("PASS: FlowCalibrationSmokeTest 全部通过");
                return 0;
            }

            Console.Error.WriteLine($"FAIL: {fails} 项未通过");
            return 1;
        }

        /// <summary>控制台打印分批下发计划统计（不连 PLC）。</summary>
        public static void RunSendPlcDispatchReport()
        {
            Console.WriteLine();
            Console.WriteLine("=== send_plc 下发统计（计划模拟，未连 PLC）===");

            // 场景 A：3 条 BarId 分批
            var ptsA = new CalibPoint3D[10];
            for (int i = 0; i < ptsA.Length; i++)
                ptsA[i] = new CalibPoint3D(i, i, 0);
            var barA = new[] { 1, 1, 1, 2, 2, 2, 2, 3, 3, 3 };
            var batchesA = PlcGvarBuilder.BuildSegmentGvarBatchesByBarId(ptsA, barA, 1);
            var metaA = batchesA.Select(b => (b.BarId, b.Gvars.Length)).ToList();
            var planA = SendPlcBatchPlanner.BuildSteps(true, metaA, 0, true, true, true);
            Console.WriteLine($"[A] 3条BarId: {SendPlcBatchPlanner.SummarizePlan(planA)}");

            // 场景 B：错误 BarIds（段级 3 个 vs 点 10 个）→ 旧行为会整批 744 段
            var inputsBad = new Dictionary<string, object?>
            {
                ["Points3D"] = ptsA,
                ["BarIds"] = new[] { 1, 2, 3 }
            };
            bool okBad = PlcGvarBuilder.TryResolveSendPlcGvar(
                inputsBad, "false", 1, "separate_batch",
                out _, out _, out _, out var diagBad);
            Console.WriteLine($"[B] BarIds不等长 separate_batch: 解析={(okBad ? "成功" : "拒绝")} {diagBad}");

            // 场景 C：16 条轮廓模拟（每条约 47 点 → 46 段，共 736 段，接近 744）
            var ptsC = new CalibPoint3D[16 * 47];
            var barC = new int[ptsC.Length];
            int idx = 0;
            for (int b = 0; b < 16; b++)
            {
                for (int p = 0; p < 47; p++)
                {
                    ptsC[idx] = new CalibPoint3D(b * 100 + p, p, 0);
                    barC[idx] = b;
                    idx++;
                }
            }

            var batchesC = PlcGvarBuilder.BuildSegmentGvarBatchesByBarId(ptsC, barC, 1);
            var metaC = batchesC.Select(x => (x.BarId, x.Gvars.Length)).ToList();
            int totalSegC = metaC.Sum(x => x.Item2);
            var planC = SendPlcBatchPlanner.BuildSteps(true, metaC, 0, true, true, true);
            Console.WriteLine(
                $"[C] 16条BarId: 批次数={batchesC.Count}, 总段数={totalSegC}, " +
                $"{SendPlcBatchPlanner.SummarizePlan(planC)}");

            // 场景 D：caliSendContour 默认参数
            var planD = SendPlcBatchPlanner.BuildSteps(
                true,
                metaC,
                0,
                writeCountPerBatch: true,
                skipCountWrite: false,
                signalHostAfterAllBatches: SendPlcBatchPlanner.ParseBoolParam("true", true));
            Console.WriteLine($"[D] caliSendContour参数: {SendPlcBatchPlanner.SummarizePlan(planD)}");
            Console.WriteLine(
                "[E] runDownstreamPerBatch: separate_batch 默认 true → 每批 GVAR 后应执行下游 N 次（由流程日志 [send_plc↓] 统计）");
            Console.WriteLine();
        }

        private static int RunSendPlcBatchPlannerTests()
        {
            int fails = 0;

            // 3 条 BarId → 3 批，每批段数 = 点数-1
            var pts3 = new CalibPoint3D[10];
            for (int i = 0; i < pts3.Length; i++)
                pts3[i] = new CalibPoint3D(i, i, 0);
            var barIds = new[] { 1, 1, 1, 2, 2, 2, 2, 3, 3, 3 };
            var batches = PlcGvarBuilder.BuildSegmentGvarBatchesByBarId(pts3, barIds, 1);
            fails += Check("按条分批: 3 个 BarId → 3 批",
                () => batches.Count == 3);

            // 同条号分两段出现 → 仍 2 批（按条号种类），非 3 批（按连续段）
            var barSplit = new[] { 1, 1, 2, 2, 1, 1 };
            var ptsSplit = new CalibPoint3D[barSplit.Length];
            for (int i = 0; i < ptsSplit.Length; i++)
                ptsSplit[i] = new CalibPoint3D(i, i, 0);
            var batchesSplit = PlcGvarBuilder.BuildSegmentGvarBatchesByBarId(ptsSplit, barSplit, 1);
            fails += Check("按条分批: 条号 1 分两段仍合并为 1 批 → 共 2 批",
                () => batchesSplit.Count == 2 && batchesSplit[0].Gvars.Length == 2 && batchesSplit[1].Gvars.Length == 1);
            fails += Check("按条分批: 各批 D800 段数分别为 2,3,2",
                () => batches[0].Gvars.Length == 2
                    && batches[1].Gvars.Length == 3
                    && batches[2].Gvars.Length == 2);

            var batchMeta = batches.Select(b => (b.BarId, b.Gvars.Length)).ToList();
            var plan3 = SendPlcBatchPlanner.BuildSteps(
                separateBatchMode: true,
                batchMeta,
                singleSegmentCount: 0,
                writeCountPerBatch: true,
                skipCountWrite: true,
                signalHostAfterAllBatches: true);

            fails += Check("下发计划: 3 批含 1 次 ClearHost + 1 次 SignalHost",
                () => plan3.Count(s => s.Kind == PlcSendStepKind.ClearHostFlag) == 1
                    && plan3.Count(s => s.Kind == PlcSendStepKind.SignalHostComplete) == 1);
            fails += Check("下发计划: D804 在最后一批 GVAR 之后",
                () =>
                {
                    if (!SendPlcBatchPlanner.ValidateHostSignalLast(plan3, out _))
                        return false;
                    int lastGvar = -1;
                    int signal = -1;
                    for (int i = 0; i < plan3.Count; i++)
                    {
                        if (plan3[i].Kind == PlcSendStepKind.WriteGvarBatch)
                            lastGvar = i;
                        if (plan3[i].Kind == PlcSendStepKind.SignalHostComplete)
                            signal = i;
                    }

                    return signal > lastGvar && lastGvar >= 0;
                });
            fails += Check("下发计划: 首批后无 SignalHost",
                () =>
                {
                    int firstGvar = -1;
                    for (int i = 0; i < plan3.Count; i++)
                    {
                        if (plan3[i].Kind == PlcSendStepKind.WriteGvarBatch)
                        {
                            firstGvar = i;
                            break;
                        }
                    }

                    if (firstGvar < 0)
                        return false;
                    for (int i = 0; i <= firstGvar; i++)
                    {
                        if (plan3[i].Kind == PlcSendStepKind.SignalHostComplete)
                            return false;
                    }

                    return true;
                });
            fails += Check("下发计划: 每批 D800 写入步序在对应 GVAR 之前",
                () =>
                {
                    for (int bi = 0; bi < 3; bi++)
                    {
                        int wc = -1, gv = -1;
                        for (int i = 0; i < plan3.Count; i++)
                        {
                            if (plan3[i].BatchIndex != bi)
                                continue;
                            if (plan3[i].Kind == PlcSendStepKind.WriteSegmentCount)
                                wc = i;
                            if (plan3[i].Kind == PlcSendStepKind.WriteGvarBatch)
                                gv = i;
                        }

                        if (wc < 0 || gv < 0 || wc >= gv)
                            return false;
                    }

                    return true;
                });

            var planNoD804 = SendPlcBatchPlanner.BuildSteps(
                true, batchMeta, 0, true, true, signalHostAfterAllBatches: false);
            fails += Check("setWeldDoneHostAfterAllBatches=false 时无 SignalHost",
                () => !planNoD804.Any(s => s.Kind == PlcSendStepKind.SignalHostComplete));

            // BarIds 不等长 → separate_batch 应失败（不再静默整批 744）
            var inputs = new Dictionary<string, object?>
            {
                ["Points3D"] = pts3,
                ["BarIds"] = new[] { 1, 2, 3 }
            };
            fails += Check("BarIds 不等长时 separate_batch 解析失败",
                () => !PlcGvarBuilder.TryResolveSendPlcGvar(
                    inputs, "false", 1, "separate_batch",
                    out _, out _, out _, out var diag)
                    && diag != null
                    && diag.Contains("等长"));

            return fails;
        }

        private static int Check(string name, Func<bool> ok)
        {
            bool pass = false;
            try { pass = ok(); }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"FAIL: {name} — {ex.Message}");
                return 1;
            }

            if (pass)
            {
                Console.WriteLine($"OK: {name}");
                return 0;
            }

            Console.Error.WriteLine($"FAIL: {name}");
            return 1;
        }

        private static string ResolveFlowRelativePath(string path, string baseDir)
        {
            if (Path.IsPathRooted(path))
                return Path.GetFullPath(path);
            return Path.GetFullPath(Path.Combine(baseDir, path));
        }

        private static string? FindRepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                string candidate = Path.Combine(dir.FullName, "flows", "halcon", "calibration_result.json");
                if (File.Exists(candidate))
                    return dir.FullName;
                dir = dir.Parent;
            }

            // 开发时从 FlowSmokeTests/bin 向上找
            dir = new DirectoryInfo(Directory.GetCurrentDirectory());
            while (dir != null)
            {
                string candidate = Path.Combine(dir.FullName, "flows", "halcon", "calibration_result.json");
                if (File.Exists(candidate))
                    return dir.FullName;
                dir = dir.Parent;
            }

            return null;
        }
    }
}
