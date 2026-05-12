#if HALCON_ENABLED
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace CalibOperatorCLI_Example
{
    /// <summary>从导出目录收集 image / segmentation 配对及打乱顺序。</summary>
    internal static class HalconDlSegTrainDataset
    {
        private static readonly HashSet<string> ImageExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".bmp", ".png", ".jpg", ".jpeg", ".tif", ".tiff", ".webp",
        };

        internal static List<(string Img, string Seg)> CollectPairs(string exportRoot, Action<string> log)
        {
            string imgDir = Path.Combine(exportRoot, "images");
            string segDir = Path.Combine(exportRoot, "segmentation");
            if (!Directory.Exists(imgDir))
                throw new InvalidOperationException($"未找到导出图像目录: {imgDir}");
            if (!Directory.Exists(segDir))
                throw new InvalidOperationException($"未找到导出分割目录: {segDir}");

            var pairs = new List<(string, string)>();
            foreach (string imgPath in Directory.GetFiles(imgDir))
            {
                string ext = Path.GetExtension(imgPath);
                if (!ImageExtensions.Contains(ext))
                    continue;
                string stem = Path.GetFileNameWithoutExtension(imgPath);
                string segPath = Path.Combine(segDir, stem + ".png");
                if (!File.Exists(segPath))
                {
                    log($"[skip] 无对应 segmentation: {stem}");
                    continue;
                }
                pairs.Add((imgPath, segPath));
            }

            if (pairs.Count == 0)
                throw new InvalidOperationException("没有可用的 image + segmentation 配对样本。");
            return pairs;
        }

        internal static void Shuffle<T>(IList<T> list, Random rng)
        {
            for (int i = list.Count - 1; i > 0; i--)
            {
                int j = rng.Next(i + 1);
                (list[i], list[j]) = (list[j], list[i]);
            }
        }
    }
}
#endif
