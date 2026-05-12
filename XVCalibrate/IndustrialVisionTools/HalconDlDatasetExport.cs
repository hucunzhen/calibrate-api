using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace CalibOperatorCLI_Example
{
    /// <summary>
    /// 将「YOLO 分割」数据集（images/train + labels/train 多边形 txt）导出为 HALCON 语义分割常用的逐像素类别图。
    /// 像素值：0 = 背景；类别 id 为 c（0..N-1）时写入像素值 (c+1)，与 HALCON 文档中 segmentation_image 惯例一致。
    /// </summary>
    internal static class HalconDlDatasetExport
    {
        private static readonly HashSet<string> ImageExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".bmp", ".png", ".jpg", ".jpeg", ".tif", ".tiff", ".webp",
        };

        public sealed class ExportReport
        {
            public int ImageCount { get; set; }
            public int LabelWritten { get; set; }
            public int EmptyLabelFallback { get; set; }
            public int MaxClassIdSeen { get; set; }
        }

        private static string ImagesTrainDir(string root) => Path.Combine(root, "images", "train");
        private static string LabelsTrainDir(string root) => Path.Combine(root, "labels", "train");

        /// <summary>从 data.yaml 尽力解析 names（支持 names: [a,b] 或换行列表）；失败返回 null。</summary>
        public static string[]? TryParseClassNamesFromDataYaml(string yamlPath)
        {
            if (string.IsNullOrEmpty(yamlPath) || !File.Exists(yamlPath))
                return null;
            try
            {
                string text = File.ReadAllText(yamlPath, Encoding.UTF8);
                int idx = text.IndexOf("names", StringComparison.OrdinalIgnoreCase);
                if (idx < 0) return null;
                int colon = text.IndexOf(':', idx);
                if (colon < 0) return null;
                string rest = text.Substring(colon + 1);
                int lb = rest.IndexOf('[');
                int rb = rest.IndexOf(']');
                if (lb >= 0 && rb > lb)
                {
                    string inner = rest.Substring(lb + 1, rb - lb - 1);
                    var parts = inner.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
                    var names = new List<string>();
                    foreach (var p in parts)
                    {
                        string t = p.Trim().Trim('\'', '"', ' ', '\t', '\r', '\n');
                        if (t.Length > 0) names.Add(t);
                    }
                    return names.Count > 0 ? names.ToArray() : null;
                }
                return null;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>解析单行 YOLO-Seg：class nx ny ...（归一化多边形）。</summary>
        public static List<(int ClassId, List<PointF> Pixels)> ParseYoloSegLabelLines(IEnumerable<string> lines, int imgW, int imgH)
        {
            var result = new List<(int, List<PointF>)>();
            foreach (string raw in lines)
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal)) continue;
                string[] parts = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 7) continue;
                if (!int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int cls))
                    continue;
                var pts = new List<PointF>();
                for (int i = 1; i + 1 < parts.Length; i += 2)
                {
                    if (!double.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out double nx)) break;
                    if (!double.TryParse(parts[i + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out double ny)) break;
                    float px = (float)Math.Clamp(nx * imgW, 0, imgW);
                    float py = (float)Math.Clamp(ny * imgH, 0, imgH);
                    pts.Add(new PointF(px, py));
                }
                if (pts.Count >= 3)
                    result.Add((cls, pts));
            }
            return result;
        }

        /// <summary>将多边形栅格化为 8 位灰度缓冲（值域 0..255）。</summary>
        public static byte[] RasterizeSemanticMask(int width, int height, IReadOnlyList<(int ClassId, List<PointF> Pixels)> instances)
        {
            if (width <= 0 || height <= 0)
                throw new ArgumentException("invalid image size");
            using var bmp = new Bitmap(width, height, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.FromArgb(0, 0, 0, 0));
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.None;
                foreach (var inst in instances)
                {
                    int v = inst.ClassId + 1;
                    if (v < 0) v = 0;
                    if (v > 255)
                        throw new InvalidOperationException($"类别 id {inst.ClassId} 超出 0..254（像素值需≤255）。");
                    var poly = inst.Pixels.Select(p => new Point((int)Math.Round(p.X), (int)Math.Round(p.Y))).ToArray();
                    if (poly.Length < 3) continue;
                    using var br = new SolidBrush(Color.FromArgb(255, v, v, v));
                    g.FillPolygon(br, poly);
                }
            }

            var gray = new byte[width * height];
            BitmapData data = bmp.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                int stride = data.Stride;
                IntPtr scan0 = data.Scan0;
                for (int y = 0; y < height; y++)
                {
                    int rowOff = y * stride;
                    for (int x = 0; x < width; x++)
                    {
                        int offset = rowOff + x * 4;
                        byte b = Marshal.ReadByte(scan0, offset);
                        gray[y * width + x] = b;
                    }
                }
            }
            finally
            {
                bmp.UnlockBits(data);
            }
            return gray;
        }

        public static ExportReport ExportFromYoloDataset(string datasetRoot, string exportRoot, string[]? classNamesHint, Action<string>? log)
        {
            if (string.IsNullOrWhiteSpace(datasetRoot) || !Directory.Exists(datasetRoot))
                throw new ArgumentException("数据集根目录无效。");
            string imgDir = ImagesTrainDir(datasetRoot);
            string lblDir = LabelsTrainDir(datasetRoot);
            if (!Directory.Exists(imgDir))
                throw new InvalidOperationException($"未找到 {imgDir}，请先使用「YOLO分割」页面准备 images/train。");

            Directory.CreateDirectory(exportRoot);
            string outImgDir = Path.Combine(exportRoot, "images");
            string outSegDir = Path.Combine(exportRoot, "segmentation");
            Directory.CreateDirectory(outImgDir);
            Directory.CreateDirectory(outSegDir);

            var yamlPath = Path.Combine(datasetRoot, "data.yaml");
            string[]? names = classNamesHint;
            if (names == null || names.Length == 0)
                names = TryParseClassNamesFromDataYaml(yamlPath);

            var report = new ExportReport();
            int maxCls = -1;

            foreach (string imgPath in Directory.GetFiles(imgDir))
            {
                string ext = Path.GetExtension(imgPath);
                if (!ImageExtensions.Contains(ext))
                    continue;
                string stem = Path.GetFileNameWithoutExtension(imgPath);
                string lblPath = Path.Combine(lblDir, stem + ".txt");

                int w, h;
                using (var probe = new Bitmap(imgPath))
                {
                    w = probe.Width;
                    h = probe.Height;
                }

                List<(int ClassId, List<PointF> Pixels)> instances;
                if (File.Exists(lblPath))
                {
                    var lines = File.ReadAllLines(lblPath);
                    instances = ParseYoloSegLabelLines(lines, w, h);
                    foreach (var z in instances)
                        maxCls = Math.Max(maxCls, z.ClassId);
                    report.LabelWritten++;
                }
                else
                {
                    instances = new List<(int, List<PointF>)>();
                    report.EmptyLabelFallback++;
                    log?.Invoke($"[warn] 无标签文件，生成全背景掩膜: {stem}");
                }

                byte[] mask = RasterizeSemanticMask(w, h, instances);
                string destImg = Path.Combine(outImgDir, Path.GetFileName(imgPath));
                File.Copy(imgPath, destImg, overwrite: true);

                string segPng = Path.Combine(outSegDir, stem + ".png");
                WriteGrayscalePng(w, h, mask, segPng);

                report.ImageCount++;
            }

            report.MaxClassIdSeen = maxCls;
            if (names != null && names.Length > 0 && maxCls >= names.Length)
                log?.Invoke($"[warn] 标签中出现类别 id {maxCls}，但 data.yaml 仅声明 {names.Length} 类，请在 HALCON 中核对 class_ids。");

            var spec = new
            {
                format = "halcon_semantic_segmentation_export_v1",
                source_dataset_root = Path.GetFullPath(datasetRoot),
                export_root = Path.GetFullPath(exportRoot),
                class_names = names ?? Array.Empty<string>(),
                note = "像素 0=背景；像素值 (class_id+1) 对应 YOLO 标签中的类别 id。请在 HALCON Deep Learning / HDevelop 中使用官方语义分割流程（如 read_dl_dataset_segmentation、preprocess_dl_dataset、train_dl_model）导入本目录下的 images 与 segmentation。",
                images_subdir = "images",
                segmentation_subdir = "segmentation",
                image_count = report.ImageCount,
                max_class_id_in_labels = report.MaxClassIdSeen,
            };
            string jsonPath = Path.Combine(exportRoot, "halcon_dl_dataset_spec.json");
            File.WriteAllText(jsonPath, JsonSerializer.Serialize(spec, new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
            log?.Invoke($"已写入说明: {jsonPath}");

            return report;
        }

        private static void WriteGrayscalePng(int width, int height, byte[] gray, string path)
        {
            using var bmp = new Bitmap(width, height, PixelFormat.Format8bppIndexed);
            ColorPalette pal = bmp.Palette;
            for (int i = 0; i < 256; i++)
                pal.Entries[i] = Color.FromArgb(255, i, i, i);
            bmp.Palette = pal;
            BitmapData data = bmp.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, PixelFormat.Format8bppIndexed);
            try
            {
                int stride = data.Stride;
                var row = new byte[stride];
                for (int y = 0; y < height; y++)
                {
                    Buffer.BlockCopy(gray, y * width, row, 0, width);
                    Marshal.Copy(row, 0, IntPtr.Add(data.Scan0, y * stride), stride);
                }
            }
            finally
            {
                bmp.UnlockBits(data);
            }
            bmp.Save(path, ImageFormat.Png);
        }
    }
}
