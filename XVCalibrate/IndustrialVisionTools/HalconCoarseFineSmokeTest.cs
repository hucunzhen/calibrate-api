using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using CalibOperatorPInvoke;

namespace CalibOperatorCLI_Example
{
    /// <summary>
    /// 粗+精匹配冒烟（需 HALCON）。仅验证最高分 1 个粗候选，控制在数秒内。
    /// dotnet run --project XVCalibrate/FlowSmokeTests -- --halcon-coarse-fine [--repo path]
    /// </summary>
    public static class HalconCoarseFineSmokeTest
    {
        public static int Run(string? repoRoot = null)
        {
#if !HALCON_ENABLED
            Console.Error.WriteLine("SKIP: 未编译 HALCON（HalconDotNet.dll 未找到）");
            return 2;
#else
            var sw = Stopwatch.StartNew();
            repoRoot ??= FlowCalibrationSmokeTest.FindRepoRoot();
            if (repoRoot == null)
            {
                Console.Error.WriteLine("FAIL: 未找到仓库根");
                return 1;
            }

            string imagePath = @"D:\work\data\Image_20260518154103272.bmp";
            string shmPath = Path.Combine(repoRoot, "flows", "shape_model_fixed.shm");
            string dfmPath = Path.Combine(repoRoot, "flows", "deformable_model.dfm");

            if (!File.Exists(imagePath))
                imagePath = Path.Combine(repoRoot, "flows", "halcon", "test_image.bmp");
            if (!File.Exists(imagePath))
            {
                Console.Error.WriteLine($"FAIL: 测试图不存在: {imagePath}");
                return 1;
            }

            int fails = 0;
            fails += Check("shape_model_fixed.shm", () => File.Exists(shmPath));
            fails += Check("deformable_model.dfm", () => File.Exists(dfmPath));
            if (fails > 0)
                return 1;

            using CalibImage img = CalibAPI.LoadImage(imagePath);
            long rigidId = HalconFlowBridge.LoadShapeModelFromFile(shmPath);
            long deformId = HalconFlowBridge.LoadDeformableModelFromFile(dfmPath);

            try
            {
                Console.WriteLine($"  图像: {imagePath} ({img.Width}x{img.Height})");
                Console.WriteLine($"  可变形: {HalconFlowBridge.GetDeformableModelSubtypeLabel(deformId)}");

                var coarse = HalconFlowBridge.CoarseShapeMatch(
                    img, rigidId, -5, 10, 0.8, 5, 0.5, "none", 0, 0.85, false);
                Console.WriteLine($"  粗匹配: {coarse.rows.Length} 个 [{sw.ElapsedMilliseconds}ms]");

                fails += Check("粗匹配至少 1 个", () => coarse.rows.Length > 0);
                if (coarse.rows.Length == 0)
                    return 1;

                var maskBatch = HalconFlowBridge.BuildCoarseShapeMaskBatch(
                    img, rigidId, coarse.rows, coarse.cols, coarse.angles, coarse.scales, coarse.scores, 2);
                Console.WriteLine($"  Mask: {maskBatch.Count} 张 [{sw.ElapsedMilliseconds}ms]");
                if (maskBatch.Count > 0)
                {
                    double fillRatio = HalconFlowBridge.ComputeMaskFillRatio(maskBatch.Masks[0]);
                    Console.WriteLine($"  Mask[0] 填充率(白像素占比): {fillRatio:P2}");
                    fails += Check("Mask 为实心填充(占比>1%)", () => fillRatio > 0.01);
                }

                var fineRows = new List<double>();
                var fineCols = new List<double>();
                var fineAngles = new List<double>();
                var fineScores = new List<double>();
                for (int i = 0; i < maskBatch.Count; i++)
                {
                    CalibImage domainImg = HalconFlowBridge.ReduceDomainByMask(img, maskBatch.Masks[i]);
                    var round = HalconFlowBridge.FineDeformableMatchOnDomainImage(
                        domainImg,
                        deformId,
                        maskBatch.CoarseRows[i],
                        maskBatch.CoarseCols[i],
                        maskBatch.CoarseAngles[i]);
                    if (round.FineCount > 0)
                    {
                        fineRows.Add(round.FineRows[0]);
                        fineCols.Add(round.FineCols[0]);
                        fineAngles.Add(round.FineAngles[0]);
                        fineScores.Add(round.FineScores[0]);
                    }
                }

                int fineCount = fineRows.Count;
                Console.WriteLine($"  精匹配: {fineCount}/{maskBatch.Count} [{sw.ElapsedMilliseconds}ms]");
                for (int i = 0; i < Math.Min(5, fineCount); i++)
                    Console.WriteLine($"    [{i}] row={fineRows[i]:F1} col={fineCols[i]:F1} sc={fineScores[i]:F3}");

                fails += Check("精匹配数≥2（多实例）", () => fineCount >= 2);
                Console.WriteLine($"  总耗时: {sw.ElapsedMilliseconds}ms");
                return fails > 0 ? 1 : 0;
            }
            finally
            {
                HalconFlowBridge.ClearModel(rigidId);
                HalconFlowBridge.ClearModel(deformId);
            }
#endif
        }

        private static int Check(string name, Func<bool> ok)
        {
            bool pass = ok();
            Console.WriteLine(pass ? $"  OK: {name}" : $"  FAIL: {name}");
            return pass ? 0 : 1;
        }
    }
}
