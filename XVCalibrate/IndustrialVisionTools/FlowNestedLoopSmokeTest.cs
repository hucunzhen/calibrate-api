using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace CalibOperatorCLI_Example
{
    /// <summary>
    /// 双层 flow_loop 示例流程冒烟测试（通过 IndustrialVisionTools --flow CLI 托管执行）。
    /// 运行：dotnet run --project XVCalibrate/FlowSmokeTests/FlowSmokeTests.csproj -- --nested-flow-loop
    /// </summary>
    public static class FlowNestedLoopSmokeTest
    {
        private const int OuterRounds = 2;
        private const int InnerRounds = 3;

        public static int Run(string? repoRoot = null)
        {
            repoRoot ??= FlowCalibrationSmokeTest.FindRepoRoot();
            if (repoRoot == null)
            {
                Console.Error.WriteLine("FAIL: 未找到仓库根");
                return 1;
            }

            string flowPath = Path.Combine(repoRoot, "flows", "halcon", "nested_flow_loop_example.flow.json");
            string tracePath = Path.Combine(repoRoot, "flows", "halcon", "nested_loop_trace.txt");
            string logPath = Path.Combine(repoRoot, "flows", "halcon", "nested_flow_loop_example.run.log");
            string projectPath = Path.Combine(repoRoot, "XVCalibrate", "IndustrialVisionTools", "IndustrialVisionTools.csproj");

            int fails = 0;
            fails += Check("nested_flow_loop_example.flow.json 存在", () => File.Exists(flowPath));
            fails += Check("IndustrialVisionTools.csproj 存在", () => File.Exists(projectPath));
            if (fails > 0)
                return 1;

            try
            {
                if (File.Exists(tracePath))
                    File.Delete(tracePath);
                if (File.Exists(logPath))
                    File.Delete(logPath);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"FAIL: 无法清理旧输出 — {ex.Message}");
                return 1;
            }

            var psi = new ProcessStartInfo
            {
                FileName = "dotnet",
                Arguments = $"run --project \"{projectPath}\" --no-build -- --flow \"{flowPath}\"",
                WorkingDirectory = repoRoot,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            psi.Environment["FLOW_RUN_LOG_PATH"] = logPath;

            using var proc = Process.Start(psi);
            if (proc == null)
            {
                Console.Error.WriteLine("FAIL: 无法启动 IndustrialVisionTools CLI");
                return 1;
            }

            string stdout = proc.StandardOutput.ReadToEnd();
            string stderr = proc.StandardError.ReadToEnd();
            proc.WaitForExit(TimeSpan.FromMinutes(2));

            Console.WriteLine(stdout.TrimEnd());
            if (!string.IsNullOrWhiteSpace(stderr))
                Console.Error.WriteLine(stderr.TrimEnd());

            fails += Check("CLI 退出码为 0", () => proc.ExitCode == 0);
            fails += Check("trace 文件已生成", () => File.Exists(tracePath));
            fails += Check("run.log 已生成", () => File.Exists(logPath));

            string logText = File.Exists(logPath) ? File.ReadAllText(logPath) : string.Empty;
            if (!string.IsNullOrWhiteSpace(logText))
            {
                // 日志形如 [ROOT:循环:1][LOOP loop 1/3 v=1]，v= 在 ] 之前
                int innerStarts = Regex.Matches(
                    logText,
                    @"\[LOOP loop \d+/" + InnerRounds + @"(?: v=[^\]]+)?\]",
                    RegexOptions.CultureInvariant).Count;
                int sinkRuns = Regex.Matches(logText, @"points_sink \(轨迹收集\)", RegexOptions.CultureInvariant).Count;
                fails += Check(
                    $"日志含 {OuterRounds * InnerRounds} 次内层轮次（{InnerRounds} 轮 × {OuterRounds} 外层）",
                    () => innerStarts >= OuterRounds * InnerRounds && sinkRuns >= OuterRounds * InnerRounds);

                int childDone = Regex.Matches(logText, @"子循环完成", RegexOptions.CultureInvariant).Count;
                fails += Check($"日志含 {OuterRounds} 次子循环完成", () => childDone >= OuterRounds);

                fails += Check("轨迹收集未被整体跳过", () => !Regex.IsMatch(logText, @"\[LOOP\].*跳过: 轨迹收集", RegexOptions.CultureInvariant));

                int outerDone = Regex.Matches(logText, @"flow_loop 完成: 循环", RegexOptions.CultureInvariant).Count;
                fails += Check("日志含外层 flow_loop 完成", () => outerDone >= 1);
            }
            else
            {
                fails += Check("run.log 非空", () => false);
            }

            if (fails == 0)
            {
                Console.WriteLine("PASS: FlowNestedLoopSmokeTest 全部通过");
                return 0;
            }

            Console.Error.WriteLine($"FAIL: {fails} 项未通过");
            return 1;
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
    }
}
