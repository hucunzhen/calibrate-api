using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Linq;
using System.Runtime.InteropServices;
using System.Numerics;
using Microsoft.Win32;
using CalibOperatorPInvoke;

namespace CalibOperatorCLI_Example
{
    public partial class FlowPage : Page
    {
        public string? CurrentFlowFilePath { get; private set; }
        public event Action<string>? FlowLoaded;

        // ================================================================
        // 算子定义模型
        // ================================================================

        /// <summary>
        /// 端口方向
        /// </summary>
        public enum PortDirection { Input, Output }

        /// <summary>
        /// 端口定义
        /// </summary>
        public class PortDef
        {
            public string Name { get; set; }
            public PortDirection Direction { get; set; }
            public Type DataType { get; set; }
            public string ColorHex { get; set; }  // 端口颜色
        }

        /// <summary>
        /// 算子参数定义
        /// </summary>
        public class OperatorParam
        {
            public string Name { get; set; }           // 参数名
            public string DisplayName { get; set; }    // 显示名
            public string DefaultValue { get; set; }   // 默认值（字符串）
            public string Description { get; set; }    // 参数说明
            public List<string> Options { get; set; } = new List<string>(); // 可选值（非空时渲染下拉框）
        }

        /// <summary>
        /// 算子类型定义（静态注册表）
        /// </summary>
        public class OperatorDef
        {
            public string TypeId { get; set; }
            public string DisplayName { get; set; }
            public string Description { get; set; }
            public string Category { get; set; }
            public List<PortDef> Ports { get; set; } = new List<PortDef>();
            public List<OperatorParam> Params { get; set; } = new List<OperatorParam>();
            public double DefaultWidth { get; set; } = 180;
            public double DefaultHeight { get; set; } = 80;
        }

        /// <summary>
        /// 可用算子注册表
        /// </summary>
        public static readonly List<OperatorDef> OperatorRegistry = new List<OperatorDef>
        {
            new OperatorDef
            {
                TypeId = "load_image",
                DisplayName = "加载图像",
                Description = "从文件或相机加载图像",
                Category = "输入",
                Params =
                {
                    new OperatorParam
                    {
                        Name = "filePath",
                        DisplayName = "图像路径",
                        DefaultValue = "",
                        Description = "可选；填写后自动加载，留空则弹窗选择"
                    }
                },
                Ports = { new PortDef { Name = "Image", Direction = PortDirection.Output, DataType = typeof(CalibImage), ColorHex = "#4CAF50" } }
            },
            new OperatorDef
            {
                TypeId = "camera_snap",
                DisplayName = "相机取一帧",
                Description = "从相机抓取单帧图像",
                Category = "输入",
                Params =
                {
                    new OperatorParam { Name = "deviceIndex", DisplayName = "设备索引", DefaultValue = "0", Description = "相机枚举索引，从0开始" },
                    new OperatorParam { Name = "targetWidth", DisplayName = "目标宽度", DefaultValue = "0", Description = "预留参数，当前未缩放（填0即可）" },
                    new OperatorParam { Name = "targetHeight", DisplayName = "目标高度", DefaultValue = "0", Description = "预留参数，当前未缩放（填0即可）" }
                },
                Ports = { new PortDef { Name = "Image", Direction = PortDirection.Output, DataType = typeof(CalibImage), ColorHex = "#4CAF50" } }
            },
            new OperatorDef
            {
                TypeId = "camera_loop",
                DisplayName = "相机循环取图",
                Description = "循环抓取多帧并输出最后一帧",
                Category = "输入",
                Params =
                {
                    new OperatorParam { Name = "deviceIndex", DisplayName = "设备索引", DefaultValue = "0", Description = "相机枚举索引，从0开始" },
                    new OperatorParam
                    {
                        Name = "mode",
                        DisplayName = "执行模式",
                        DefaultValue = "last_only",
                        Description = "last_only=仅输出最后一帧；per_frame=每帧驱动下游执行一次",
                        Options = new List<string> { "last_only", "per_frame" }
                    },
                    new OperatorParam { Name = "frameCount", DisplayName = "抓取帧数", DefaultValue = "10", Description = "循环抓取总帧数，>=1" },
                    new OperatorParam { Name = "intervalMs", DisplayName = "帧间隔(ms)", DefaultValue = "100", Description = "每帧之间等待时间" },
                    new OperatorParam { Name = "targetWidth", DisplayName = "目标宽度", DefaultValue = "0", Description = "预留参数，当前未缩放（填0即可）" },
                    new OperatorParam { Name = "targetHeight", DisplayName = "目标高度", DefaultValue = "0", Description = "预留参数，当前未缩放（填0即可）" }
                },
                Ports =
                {
                    new PortDef { Name = "Image", Direction = PortDirection.Output, DataType = typeof(CalibImage), ColorHex = "#4CAF50" },
                    new PortDef { Name = "Count", Direction = PortDirection.Output, DataType = typeof(int), ColorHex = "#607D8B" }
                }
            },
            new OperatorDef
            {
                TypeId = "grayscale",
                DisplayName = "灰度化",
                Description = "Step1: 图像转灰度",
                Category = "预处理",
                Ports =
                {
                    new PortDef { Name = "In", Direction = PortDirection.Input, DataType = typeof(CalibImage), ColorHex = "#4CAF50" },
                    new PortDef { Name = "Out", Direction = PortDirection.Output, DataType = typeof(CalibImage), ColorHex = "#4CAF50" }
                }
            },
            new OperatorDef
            {
                TypeId = "clahe",
                DisplayName = "CLAHE",
                Description = "对比度受限自适应直方图均衡化",
                Category = "预处理",
                Ports =
                {
                    new PortDef { Name = "In", Direction = PortDirection.Input, DataType = typeof(CalibImage), ColorHex = "#4CAF50" },
                    new PortDef { Name = "Out", Direction = PortDirection.Output, DataType = typeof(CalibImage), ColorHex = "#4CAF50" }
                },
                Params =
                {
                    new OperatorParam { Name = "clipLimit", DisplayName = "对比度限制", DefaultValue = "3.0", Description = "CLAHE clipLimit (1.0~4.0)" },
                    new OperatorParam { Name = "tileSize", DisplayName = "分块大小", DefaultValue = "8", Description = "网格大小 (像素)" }
                }
            },
            new OperatorDef
            {
                TypeId = "canny",
                DisplayName = "Canny边缘检测",
                Description = "Step1.5: Canny/Sobel 边缘检测",
                Category = "预处理",
                Ports =
                {
                    new PortDef { Name = "In", Direction = PortDirection.Input, DataType = typeof(CalibImage), ColorHex = "#4CAF50" },
                    new PortDef { Name = "Edge", Direction = PortDirection.Output, DataType = typeof(CalibImage), ColorHex = "#4CAF50" }
                }
            },
            new OperatorDef
            {
                TypeId = "sobel",
                DisplayName = "Sobel边缘检测",
                Description = "基于 Sobel 梯度生成边缘图",
                Category = "预处理",
                Ports =
                {
                    new PortDef { Name = "In", Direction = PortDirection.Input, DataType = typeof(CalibImage), ColorHex = "#4CAF50" },
                    new PortDef { Name = "Edge", Direction = PortDirection.Output, DataType = typeof(CalibImage), ColorHex = "#4CAF50" }
                },
                Params =
                {
                    new OperatorParam { Name = "threshold", DisplayName = "梯度阈值", DefaultValue = "48", Description = "Sobel 梯度阈值，越大越干净" }
                }
            },
            new OperatorDef
            {
                TypeId = "scharr",
                DisplayName = "Scharr边缘检测",
                Description = "基于 Scharr 3×3 梯度（比 Sobel 更贴近真实梯度方向）",
                Category = "预处理",
                Ports =
                {
                    new PortDef { Name = "In", Direction = PortDirection.Input, DataType = typeof(CalibImage), ColorHex = "#4CAF50" },
                    new PortDef { Name = "Edge", Direction = PortDirection.Output, DataType = typeof(CalibImage), ColorHex = "#4CAF50" }
                },
                Params =
                {
                    new OperatorParam { Name = "threshold", DisplayName = "梯度阈值", DefaultValue = "48", Description = "梯度幅值阈值，越大越干净（Scharr 幅值通常大于 Sobel）" }
                }
            },
            new OperatorDef
            {
                TypeId = "phase_congruency",
                DisplayName = "相位一致性边缘",
                Description = "频域相位检测(DFT + Log-Gabor + Phase-only重建)边缘检测",
                Category = "预处理",
                Ports =
                {
                    new PortDef { Name = "In", Direction = PortDirection.Input, DataType = typeof(CalibImage), ColorHex = "#4CAF50" },
                    new PortDef { Name = "Response", Direction = PortDirection.Output, DataType = typeof(CalibImage), ColorHex = "#4CAF50" },
                    new PortDef { Name = "Edge", Direction = PortDirection.Output, DataType = typeof(CalibImage), ColorHex = "#4CAF50" }
                },
                Params =
                {
                    new OperatorParam { Name = "threshold", DisplayName = "一致性阈值", DefaultValue = "0.45", Description = "相位一致性阈值(0~1)，越大越严格" },
                    new OperatorParam { Name = "noiseSigma", DisplayName = "噪声抑制", DefaultValue = "0.12", Description = "噪声抑制系数(0~1)，越大越抑制弱响应" },
                    new OperatorParam { Name = "blurKsize", DisplayName = "预平滑核", DefaultValue = "3", Description = "预平滑核大小(奇数，1表示不平滑)" },
                    new OperatorParam { Name = "debugDumpPrefix", DisplayName = "调试输出前缀", DefaultValue = "", Description = "可选；填写后会输出 *_response.bmp 与 *_binary.bmp" }
                }
            },
            new OperatorDef
            {
                TypeId = "freq_filter_binary",
                DisplayName = "频域滤波二值化",
                Description = "频域滤波后回到空域并输出二值图",
                Category = "预处理",
                Ports =
                {
                    new PortDef { Name = "In", Direction = PortDirection.Input, DataType = typeof(CalibImage), ColorHex = "#4CAF50" },
                    new PortDef { Name = "Filtered", Direction = PortDirection.Output, DataType = typeof(CalibImage), ColorHex = "#4CAF50" },
                    new PortDef { Name = "Binary", Direction = PortDirection.Output, DataType = typeof(CalibImage), ColorHex = "#4CAF50" }
                },
                Params =
                {
                    new OperatorParam
                    {
                        Name = "mode",
                        DisplayName = "滤波模式",
                        DefaultValue = "bandpass",
                        Description = "lowpass | highpass | bandpass",
                        Options = new List<string> { "lowpass", "highpass", "bandpass" }
                    },
                    new OperatorParam { Name = "lowCut", DisplayName = "低截止(归一化)", DefaultValue = "0.06", Description = "0~0.5，band/high 生效" },
                    new OperatorParam { Name = "highCut", DisplayName = "高截止(归一化)", DefaultValue = "0.24", Description = "0~0.5，band/low 生效" },
                    new OperatorParam { Name = "threshold", DisplayName = "二值阈值", DefaultValue = "0.48", Description = "0~1，useOtsu=false 时生效" },
                    new OperatorParam { Name = "useOtsu", DisplayName = "自动阈值", DefaultValue = "true", Description = "true/false" }
                }
            },
            new OperatorDef
            {
                TypeId = "local_freq_sauvola_niblack",
                DisplayName = "局部频域阈值(S/N)",
                Description = "先做频域带通，再用 Sauvola/Niblack 局部阈值输出二值图",
                Category = "预处理",
                Ports =
                {
                    new PortDef { Name = "In", Direction = PortDirection.Input, DataType = typeof(CalibImage), ColorHex = "#4CAF50" },
                    new PortDef { Name = "Filtered", Direction = PortDirection.Output, DataType = typeof(CalibImage), ColorHex = "#4CAF50" },
                    new PortDef { Name = "Binary", Direction = PortDirection.Output, DataType = typeof(CalibImage), ColorHex = "#4CAF50" }
                },
                Params =
                {
                    new OperatorParam
                    {
                        Name = "method",
                        DisplayName = "局部阈值方法",
                        DefaultValue = "sauvola",
                        Description = "sauvola | niblack",
                        Options = new List<string> { "sauvola", "niblack" }
                    },
                    new OperatorParam { Name = "windowSize", DisplayName = "窗口大小", DefaultValue = "25", Description = "奇数，建议 15~41" },
                    new OperatorParam { Name = "k", DisplayName = "k系数", DefaultValue = "0.32", Description = "Sauvola常用0.2~0.5；Niblack常用-0.2~0.2" },
                    new OperatorParam { Name = "R", DisplayName = "Sauvola动态范围R", DefaultValue = "0.5", Description = "0~1，通常0.3~0.7；仅Sauvola生效" },
                    new OperatorParam { Name = "lowCut", DisplayName = "低截止(归一化)", DefaultValue = "0.04", Description = "0~0.5，带通下限" },
                    new OperatorParam { Name = "highCut", DisplayName = "高截止(归一化)", DefaultValue = "0.28", Description = "0~0.5，带通上限" }
                }
            },
            new OperatorDef
            {
                TypeId = "pre_filter",
                DisplayName = "预滤波",
                Description = "Sobel 前预滤波（Gaussian/Median）",
                Category = "预处理",
                Ports =
                {
                    new PortDef { Name = "In", Direction = PortDirection.Input, DataType = typeof(CalibImage), ColorHex = "#4CAF50" },
                    new PortDef { Name = "Out", Direction = PortDirection.Output, DataType = typeof(CalibImage), ColorHex = "#4CAF50" }
                },
                Params =
                {
                    new OperatorParam
                    {
                        Name = "mode",
                        DisplayName = "滤波模式",
                        DefaultValue = "gaussian",
                        Description = "gaussian | median",
                        Options = new List<string> { "gaussian", "median" }
                    },
                    new OperatorParam { Name = "ksize", DisplayName = "核大小", DefaultValue = "3", Description = "奇数，建议 3/5" }
                }
            },
            new OperatorDef
            {
                TypeId = "nlmeans",
                DisplayName = "NLMeans去噪",
                Description = "非局部均值去噪（对纹理保留更好）",
                Category = "预处理",
                Ports =
                {
                    new PortDef { Name = "In", Direction = PortDirection.Input, DataType = typeof(CalibImage), ColorHex = "#4CAF50" },
                    new PortDef { Name = "Out", Direction = PortDirection.Output, DataType = typeof(CalibImage), ColorHex = "#4CAF50" }
                },
                Params =
                {
                    new OperatorParam { Name = "h", DisplayName = "滤波强度", DefaultValue = "12", Description = "越大去噪越强，细节也更易被抹平" },
                    new OperatorParam { Name = "searchWindow", DisplayName = "搜索窗口", DefaultValue = "11", Description = "奇数，建议 7~17" },
                    new OperatorParam { Name = "templateWindow", DisplayName = "模板窗口", DefaultValue = "3", Description = "奇数，建议 3/5" }
                }
            },
            new OperatorDef
            {
                TypeId = "binarize",
                DisplayName = "二值化",
                Description = "预处理(Otsu二值化)输出二值图",
                Category = "预处理",
                Ports =
                {
                    new PortDef { Name = "In", Direction = PortDirection.Input, DataType = typeof(CalibImage), ColorHex = "#4CAF50" },
                    new PortDef { Name = "Out", Direction = PortDirection.Output, DataType = typeof(CalibImage), ColorHex = "#4CAF50" }
                },
                Params =
                {
                    new OperatorParam { Name = "blurSize", DisplayName = "模糊核大小", DefaultValue = "7", Description = "高斯模糊核 (奇数)" },
                    new OperatorParam { Name = "morphSize", DisplayName = "形态学核大小", DefaultValue = "5", Description = "形态学操作核 (奇数)" }
                }
            },
            new OperatorDef
            {
                TypeId = "gray_range_binary",
                DisplayName = "灰度范围二值化",
                Description = "按指定灰度区间[low,high]二值化",
                Category = "预处理",
                Ports =
                {
                    new PortDef { Name = "In", Direction = PortDirection.Input, DataType = typeof(CalibImage), ColorHex = "#4CAF50" },
                    new PortDef { Name = "Out", Direction = PortDirection.Output, DataType = typeof(CalibImage), ColorHex = "#4CAF50" }
                },
                Params =
                {
                    new OperatorParam { Name = "grayLow", DisplayName = "灰度下限", DefaultValue = "5", Description = "灰度范围下界 (0~255)" },
                    new OperatorParam { Name = "grayHigh", DisplayName = "灰度上限", DefaultValue = "50", Description = "灰度范围上界 (0~255)" }
                }
            },
            new OperatorDef
            {
                TypeId = "find_contours",
                DisplayName = "查找轮廓",
                Description = "从二值图中提取暗条轮廓、排序并输出可视化图和轮廓数据",
                Category = "检测",
                Ports =
                {
                    new PortDef { Name = "In", Direction = PortDirection.Input, DataType = typeof(CalibImage), ColorHex = "#4CAF50" },
                    new PortDef { Name = "Out", Direction = PortDirection.Output, DataType = typeof(CalibImage), ColorHex = "#4CAF50" },
                    new PortDef { Name = "Count", Direction = PortDirection.Output, DataType = typeof(int), ColorHex = "#FF9800" },
                    new PortDef { Name = "Contours", Direction = PortDirection.Output, DataType = typeof(ValueTuple<int[], int[], int[], int>), ColorHex = "#9C27B0" }
                }
            },
            new OperatorDef
            {
                TypeId = "filter_contours",
                DisplayName = "规则轮廓筛选",
                Description = "按面积/长宽比/圆度筛选规整轮廓",
                Category = "检测",
                Ports =
                {
                    new PortDef { Name = "Contours", Direction = PortDirection.Input, DataType = typeof(ValueTuple<int[], int[], int[], int>), ColorHex = "#9C27B0" },
                    new PortDef { Name = "Contours", Direction = PortDirection.Output, DataType = typeof(ValueTuple<int[], int[], int[], int>), ColorHex = "#9C27B0" },
                    new PortDef { Name = "Count", Direction = PortDirection.Output, DataType = typeof(int), ColorHex = "#FF9800" }
                },
                Params =
                {
                    new OperatorParam { Name = "minArea", DisplayName = "最小面积", DefaultValue = "8000", Description = "保留轮廓最小面积(像素)" },
                    new OperatorParam { Name = "maxArea", DisplayName = "最大面积", DefaultValue = "4000000", Description = "保留轮廓最大面积(像素)" },
                    new OperatorParam { Name = "minAspect", DisplayName = "最小长宽比", DefaultValue = "0.2", Description = "保留包围盒宽高比最小值" },
                    new OperatorParam { Name = "maxAspect", DisplayName = "最大长宽比", DefaultValue = "5.0", Description = "保留包围盒宽高比最大值" },
                    new OperatorParam { Name = "minCircularity", DisplayName = "最小圆度", DefaultValue = "0.02", Description = "4πA/P² 最小阈值(0~1)" },
                    new OperatorParam { Name = "targetCount", DisplayName = "保留数量", DefaultValue = "15", Description = "按面积降序保留前N条" }
                }
            },
            new OperatorDef
            {
                TypeId = "match_contours",
                DisplayName = "形状匹配筛选",
                Description = "基于模板轮廓做形状匹配并筛选",
                Category = "检测",
                Ports =
                {
                    new PortDef { Name = "Contours", Direction = PortDirection.Input, DataType = typeof(ValueTuple<int[], int[], int[], int>), ColorHex = "#9C27B0" },
                    new PortDef { Name = "Contours", Direction = PortDirection.Output, DataType = typeof(ValueTuple<int[], int[], int[], int>), ColorHex = "#9C27B0" },
                    new PortDef { Name = "Count", Direction = PortDirection.Output, DataType = typeof(int), ColorHex = "#FF9800" }
                },
                Params =
                {
                    new OperatorParam { Name = "templateIndex", DisplayName = "模板索引", DefaultValue = "0", Description = "模板轮廓索引（-1=自动选面积最大）" },
                    new OperatorParam { Name = "maxDistance", DisplayName = "最大形状距离", DefaultValue = "0.22", Description = "保留 distance <= 阈值 的轮廓" },
                    new OperatorParam { Name = "samplePoints", DisplayName = "匹配采样点", DefaultValue = "96", Description = "形状描述采样点数（越大越精细）" },
                    new OperatorParam { Name = "targetCount", DisplayName = "保留数量", DefaultValue = "15", Description = "按匹配距离升序保留前N条" }
                }
            },
            new OperatorDef
            {
                TypeId = "shape_match_global",
                DisplayName = "全局形状匹配",
                Description = "在整幅边缘图上做模板匹配，输出 Top-N 候选轮廓",
                Category = "检测",
                Ports =
                {
                    new PortDef { Name = "Edge", Direction = PortDirection.Input, DataType = typeof(CalibImage), ColorHex = "#4CAF50" },
                    new PortDef { Name = "Template", Direction = PortDirection.Input, DataType = typeof(CalibImage), ColorHex = "#4CAF50" },
                    new PortDef { Name = "Contours", Direction = PortDirection.Input, DataType = typeof(ValueTuple<int[], int[], int[], int>), ColorHex = "#9C27B0" },
                    new PortDef { Name = "Contours", Direction = PortDirection.Output, DataType = typeof(ValueTuple<int[], int[], int[], int>), ColorHex = "#9C27B0" },
                    new PortDef { Name = "Count", Direction = PortDirection.Output, DataType = typeof(int), ColorHex = "#FF9800" }
                },
                Params =
                {
                    new OperatorParam { Name = "templatePath", DisplayName = "模板路径", DefaultValue = "", Description = "可选；为空时自动从边缘图提取最大连通域模板" },
                    new OperatorParam { Name = "topN", DisplayName = "输出数量", DefaultValue = "15", Description = "输出匹配得分最高的前 N 个候选" },
                    new OperatorParam { Name = "minScore", DisplayName = "最小得分", DefaultValue = "0.18", Description = "候选最低匹配得分(0~1)" },
                    new OperatorParam { Name = "areaWeight", DisplayName = "面积加权系数", DefaultValue = "0.6", Description = "按局部边缘密度加权排序，越大越偏向大目标" },
                    new OperatorParam { Name = "step", DisplayName = "滑窗步长", DefaultValue = "4", Description = "滑窗步长，越小越精细但更慢" },
                    new OperatorParam { Name = "downsample", DisplayName = "降采样倍数", DefaultValue = "2", Description = "匹配前图像降采样倍数，建议 1~4" },
                    new OperatorParam { Name = "maxTemplateSize", DisplayName = "模板最大边长", DefaultValue = "120", Description = "模板缩放后最大边长，控制速度" },
                    new OperatorParam { Name = "templateBars", DisplayName = "模板融合条数", DefaultValue = "16", Description = "Contours 输入时融合前 N 条轨迹构建模板" },
                    new OperatorParam { Name = "templateConsensus", DisplayName = "模板共识阈值", DefaultValue = "0.30", Description = "融合模板像素共识比例(0~1)" },
                    new OperatorParam { Name = "edgeMinComponent", DisplayName = "最小连通域像素", DefaultValue = "60", Description = "小于该值的边缘连通域将被移除" },
                    new OperatorParam { Name = "edgeOpenRadius", DisplayName = "开运算半径", DefaultValue = "1", Description = "0=关闭；1~2 可抑制毛刺噪点" }
                }
            },
            new OperatorDef
            {
                TypeId = "fuse_contours_template",
                DisplayName = "融合轮廓模板",
                Description = "将轮廓集合融合为模板图",
                Category = "检测",
                Ports =
                {
                    new PortDef { Name = "Contours", Direction = PortDirection.Input, DataType = typeof(ValueTuple<int[], int[], int[], int>), ColorHex = "#9C27B0" },
                    new PortDef { Name = "Template", Direction = PortDirection.Output, DataType = typeof(CalibImage), ColorHex = "#4CAF50" }
                },
                Params =
                {
                    new OperatorParam { Name = "templateBars", DisplayName = "模板融合条数", DefaultValue = "16", Description = "融合前 N 条轮廓生成模板" },
                    new OperatorParam { Name = "canvasSize", DisplayName = "模板最小画布尺寸", DefaultValue = "120", Description = "仅作为最小画布，不改变融合模板原始尺寸" },
                    new OperatorParam { Name = "templateConsensus", DisplayName = "模板共识阈值", DefaultValue = "0.30", Description = "像素共识比例(0~1)" },
                    new OperatorParam { Name = "samplePoints", DisplayName = "融合采样点数", DefaultValue = "96", Description = "相互对齐融合时每条轮廓重采样点数" },
                    new OperatorParam
                    {
                        Name = "fusionMode",
                        DisplayName = "融合模式",
                        DefaultValue = "mutual",
                        Description = "global_layout | mutual",
                        Options = new List<string> { "global_layout", "mutual" }
                    },
                    new OperatorParam
                    {
                        Name = "centerlineMode",
                        DisplayName = "中心线模式",
                        DefaultValue = "on",
                        Description = "off | on（将融合双边压缩为中心线）",
                        Options = new List<string> { "off", "on" }
                    }
                }
            },
            new OperatorDef
            {
                TypeId = "apply_mask",
                DisplayName = "Mask应用",
                Description = "用Mask处理原图，保留Mask区域内像素",
                Category = "预处理",
                Ports =
                {
                    new PortDef { Name = "Image", Direction = PortDirection.Input, DataType = typeof(CalibImage), ColorHex = "#4CAF50" },
                    new PortDef { Name = "Mask", Direction = PortDirection.Input, DataType = typeof(CalibImage), ColorHex = "#4CAF50" },
                    new PortDef { Name = "Out", Direction = PortDirection.Output, DataType = typeof(CalibImage), ColorHex = "#4CAF50" }
                }
            },
            new OperatorDef
            {
                TypeId = "create_mask",
                DisplayName = "生成Mask",
                Description = "Step2.5: 按轮廓索引填充内部生成Mask（-1=自动选最大）",
                Category = "检测",
                Params =
                {
                    new OperatorParam { Name = "contourIdx", DisplayName = "轮廓索引", DefaultValue = "-1", Description = "轮廓索引 (-1=自动选最大)" }
                },
                Ports =
                {
                    new PortDef { Name = "In", Direction = PortDirection.Input, DataType = typeof(CalibImage), ColorHex = "#4CAF50" },
                    new PortDef { Name = "Contours", Direction = PortDirection.Input, DataType = typeof(ValueTuple<int[], int[], int[], int>), ColorHex = "#9C27B0" },
                    new PortDef { Name = "Mask", Direction = PortDirection.Output, DataType = typeof(CalibImage), ColorHex = "#4CAF50" }
                }
            },
            new OperatorDef
            {
                TypeId = "detect_hollow",
                DisplayName = "空洞检测",
                Description = "Step3b: mask内按灰度范围检测空洞",
                Category = "检测",
                Ports =
                {
                    new PortDef { Name = "Mask", Direction = PortDirection.Input, DataType = typeof(CalibImage), ColorHex = "#4CAF50" },
                    new PortDef { Name = "Count", Direction = PortDirection.Output, DataType = typeof(int), ColorHex = "#FF9800" },
                    new PortDef { Name = "Hollow", Direction = PortDirection.Output, DataType = typeof(CalibImage), ColorHex = "#4CAF50" }
                },
                Params =
                {
                    new OperatorParam { Name = "hollowGrayLow", DisplayName = "空洞灰度下限", DefaultValue = "5", Description = "空洞灰度下界 (0~255)" },
                    new OperatorParam { Name = "hollowGrayHigh", DisplayName = "空洞灰度上限", DefaultValue = "50", Description = "空洞灰度上界 (0~255)" },
                    new OperatorParam { Name = "targetHollows", DisplayName = "目标空洞数", DefaultValue = "16", Description = "期望检测的空洞数量" },
                    new OperatorParam { Name = "morphKernelSize", DisplayName = "形态学核大小", DefaultValue = "5", Description = "形态学核大小 (奇数)" }
                }
            },
            new OperatorDef
            {
                TypeId = "detect_dark",
                DisplayName = "暗条检测",
                Description = "Step4: 检测暗条二值化",
                Category = "检测",
                Ports =
                {
                    new PortDef { Name = "In", Direction = PortDirection.Input, DataType = typeof(CalibImage), ColorHex = "#4CAF50" },
                    new PortDef { Name = "Mask", Direction = PortDirection.Input, DataType = typeof(CalibImage), ColorHex = "#4CAF50" },
                    new PortDef { Name = "Dark", Direction = PortDirection.Output, DataType = typeof(CalibImage), ColorHex = "#4CAF50" }
                },
                Params =
                {
                    new OperatorParam { Name = "darkThreshold", DisplayName = "暗条阈值", DefaultValue = "50", Description = "暗条灰度阈值" }
                }
            },
            new OperatorDef
            {
                TypeId = "morphology",
                DisplayName = "形态学清理",
                Description = "Step5: Morphology清理",
                Category = "后处理",
                Ports =
                {
                    new PortDef { Name = "In", Direction = PortDirection.Input, DataType = typeof(CalibImage), ColorHex = "#4CAF50" },
                    new PortDef { Name = "Out", Direction = PortDirection.Output, DataType = typeof(CalibImage), ColorHex = "#4CAF50" }
                },
                Params =
                {
                    new OperatorParam { Name = "kernelSize", DisplayName = "核大小", DefaultValue = "5", Description = "形态学核大小 (奇数)" },
                    new OperatorParam { Name = "blurKsize", DisplayName = "模糊核大小", DefaultValue = "9", Description = "高斯模糊核大小 (奇数)" },
                    new OperatorParam { Name = "blurSigma", DisplayName = "模糊Sigma", DefaultValue = "2.0", Description = "高斯模糊Sigma" }
                }
            },
            new OperatorDef
            {
                TypeId = "sort_contours",
                DisplayName = "排序轮廓",
                Description = "Step6: 找暗条轮廓并排序",
                Category = "后处理",
                Ports =
                {
                    new PortDef { Name = "In", Direction = PortDirection.Input, DataType = typeof(CalibImage), ColorHex = "#4CAF50" },
                    new PortDef { Name = "Count", Direction = PortDirection.Output, DataType = typeof(int), ColorHex = "#FF9800" }
                }
            },
            new OperatorDef
            {
                TypeId = "sample",
                DisplayName = "采样",
                Description = "使用上游轮廓数据进行等弧长采样",
                Category = "输出",
                Ports =
                {
                    new PortDef { Name = "Contours", Direction = PortDirection.Input, DataType = typeof(ValueTuple<int[], int[], int[], int>), ColorHex = "#9C27B0" },
                    new PortDef { Name = "Points", Direction = PortDirection.Output, DataType = typeof(Point2D[]), ColorHex = "#2196F3" },
                    new PortDef { Name = "BarIds", Direction = PortDirection.Output, DataType = typeof(int[]), ColorHex = "#FFC107" }
                },
                Params =
                {
                    new OperatorParam { Name = "targetBars", DisplayName = "目标轮廓数", DefaultValue = "16", Description = "按面积取前N个轮廓" },
                    new OperatorParam { Name = "spacing", DisplayName = "采样间距", DefaultValue = "3", Description = "等弧长采样间距 (像素)" }
                }
            },
            new OperatorDef
            {
                TypeId = "fit_shape",
                DisplayName = "形状拟合",
                Description = "Step7.5: Stadium拟合/曲率去噪",
                Category = "输出",
                Ports =
                {
                    new PortDef { Name = "In", Direction = PortDirection.Input, DataType = typeof(Point2D[]), ColorHex = "#2196F3" },
                    new PortDef { Name = "BarIds", Direction = PortDirection.Input, DataType = typeof(int[]), ColorHex = "#FFC107" },
                    new PortDef { Name = "Out", Direction = PortDirection.Output, DataType = typeof(Point2D[]), ColorHex = "#2196F3" },
                    new PortDef { Name = "OutBarIds", Direction = PortDirection.Output, DataType = typeof(int[]), ColorHex = "#FFC107" }
                },
                Params =
                {
                    new OperatorParam
                    {
                        Name = "mode",
                        DisplayName = "拟合模式",
                        DefaultValue = "hybrid",
                        Description = "moving_avg | simplify | hybrid",
                        Options = new List<string> { "moving_avg", "simplify", "hybrid" }
                    },
                    new OperatorParam { Name = "windowRadius", DisplayName = "平滑窗口半径", DefaultValue = "1", Description = "moving_avg/hybrid 生效，建议 1~3" },
                    new OperatorParam { Name = "epsilon", DisplayName = "简化阈值", DefaultValue = "2.0", Description = "simplify/hybrid 生效，值越大越平滑" },
                    new OperatorParam { Name = "splitGapFactor", DisplayName = "区域分割阈值倍数", DefaultValue = "3.0", Description = "按相邻点距离自动分割封闭区域，>中位步长*该倍数判定为新区域" },
                    new OperatorParam { Name = "minRegionPoints", DisplayName = "最小区域点数", DefaultValue = "16", Description = "小于该点数的区域不做独立拟合" }
                }
            },
            new OperatorDef
            {
                TypeId = "verify_mask",
                DisplayName = "Mask验证",
                Description = "Step8: 通过mask验证轨迹",
                Category = "验证",
                Ports =
                {
                    new PortDef { Name = "In", Direction = PortDirection.Input, DataType = typeof(Point2D[]), ColorHex = "#2196F3" },
                    new PortDef { Name = "Out", Direction = PortDirection.Output, DataType = typeof(Point2D[]), ColorHex = "#2196F3" }
                }
            },
            new OperatorDef
            {
                TypeId = "dedup",
                DisplayName = "去重排序",
                Description = "Step9: 去重+排序",
                Category = "验证",
                Ports =
                {
                    new PortDef { Name = "In", Direction = PortDirection.Input, DataType = typeof(Point2D[]), ColorHex = "#2196F3" },
                    new PortDef { Name = "Out", Direction = PortDirection.Output, DataType = typeof(Point2D[]), ColorHex = "#2196F3" }
                }
            },
            new OperatorDef
            {
                TypeId = "convert_output",
                DisplayName = "输出转换",
                Description = "Step10: 转换为输出格式",
                Category = "输出",
                Ports =
                {
                    new PortDef { Name = "In", Direction = PortDirection.Input, DataType = typeof(Point2D[]), ColorHex = "#2196F3" },
                    new PortDef { Name = "Result", Direction = PortDirection.Output, DataType = typeof(TrajectoryResult), ColorHex = "#9C27B0" }
                }
            },
            new OperatorDef
            {
                TypeId = "draw_color",
                DisplayName = "绘制彩色轨迹",
                Description = "Step11: 绘制彩色结果",
                Category = "可视化",
                Ports =
                {
                    new PortDef { Name = "Result", Direction = PortDirection.Input, DataType = typeof(TrajectoryResult), ColorHex = "#9C27B0" },
                    new PortDef { Name = "Image", Direction = PortDirection.Output, DataType = typeof(CalibImage), ColorHex = "#4CAF50" }
                }
            },
            new OperatorDef
            {
                TypeId = "display",
                DisplayName = "显示图像",
                Description = "在窗口中显示图像，可选背景图/点位叠加",
                Category = "可视化",
                Params =
                {
                    new OperatorParam { Name = "dotRadius", DisplayName = "点位半径", DefaultValue = "3", Description = "叠加点位的圆点半径 (像素)" }
                },
                Ports =
                {
                    new PortDef { Name = "Img", Direction = PortDirection.Input, DataType = typeof(CalibImage), ColorHex = "#4CAF50" },
                    new PortDef { Name = "Image", Direction = PortDirection.Input, DataType = typeof(CalibImage), ColorHex = "#4CAF50" },
                    new PortDef { Name = "Points", Direction = PortDirection.Input, DataType = typeof(Point2D[]), ColorHex = "#2196F3" }
                }
            },
            new OperatorDef
            {
                TypeId = "save_image",
                DisplayName = "保存图像",
                Description = "将输入图像保存到文件",
                Category = "可视化",
                Params =
                {
                    new OperatorParam
                    {
                        Name = "filePath",
                        DisplayName = "保存路径",
                        DefaultValue = "flow_output.bmp",
                        Description = "支持绝对路径或相对路径（相对可执行目录）"
                    }
                },
                Ports =
                {
                    new PortDef { Name = "Image", Direction = PortDirection.Input, DataType = typeof(CalibImage), ColorHex = "#4CAF50" },
                    new PortDef { Name = "Out", Direction = PortDirection.Output, DataType = typeof(CalibImage), ColorHex = "#4CAF50" }
                }
            },
            new OperatorDef
            {
                TypeId = "world_coords",
                DisplayName = "世界坐标",
                Description = "手动输入九点标定的世界坐标",
                Category = "标定",
                Ports =
                {
                    new PortDef { Name = "Points", Direction = PortDirection.Output, DataType = typeof(Point2D[]), ColorHex = "#2196F3" }
                }
            },
            new OperatorDef
            {
                TypeId = "calibrate",
                DisplayName = "九点标定",
                Description = "标定像素坐标→世界坐标",
                Category = "标定",
                Ports =
                {
                    new PortDef { Name = "ImagePts", Direction = PortDirection.Input, DataType = typeof(Point2D[]), ColorHex = "#2196F3" },
                    new PortDef { Name = "WorldPts", Direction = PortDirection.Input, DataType = typeof(Point2D[]), ColorHex = "#2196F3" },
                    new PortDef { Name = "Transform", Direction = PortDirection.Output, DataType = typeof(AffineTransform), ColorHex = "#E91E63" }
                }
            },
            new OperatorDef
            {
                TypeId = "img_to_world",
                DisplayName = "坐标转换",
                Description = "像素坐标→世界坐标",
                Category = "标定",
                Ports =
                {
                    new PortDef { Name = "Pixel", Direction = PortDirection.Input, DataType = typeof(Point2D[]), ColorHex = "#2196F3" },
                    new PortDef { Name = "Transform", Direction = PortDirection.Input, DataType = typeof(AffineTransform), ColorHex = "#E91E63" },
                    new PortDef { Name = "World", Direction = PortDirection.Output, DataType = typeof(Point2D[]), ColorHex = "#2196F3" }
                }
            },
            new OperatorDef
            {
                TypeId = "calibrate_homography",
                DisplayName = "透视标定(H)",
                Description = "基于单应矩阵(Homography)进行标定（适合存在透视畸变）",
                Category = "标定",
                Ports =
                {
                    new PortDef { Name = "ImagePts", Direction = PortDirection.Input, DataType = typeof(Point2D[]), ColorHex = "#2196F3" },
                    new PortDef { Name = "WorldPts", Direction = PortDirection.Input, DataType = typeof(Point2D[]), ColorHex = "#2196F3" },
                    new PortDef { Name = "H", Direction = PortDirection.Output, DataType = typeof(HomographyTransform), ColorHex = "#E91E63" }
                }
            },
            new OperatorDef
            {
                TypeId = "img_to_world_homography",
                DisplayName = "坐标转换(H)",
                Description = "使用单应矩阵进行像素->世界坐标转换",
                Category = "标定",
                Ports =
                {
                    new PortDef { Name = "Pixel", Direction = PortDirection.Input, DataType = typeof(Point2D[]), ColorHex = "#2196F3" },
                    new PortDef { Name = "H", Direction = PortDirection.Input, DataType = typeof(HomographyTransform), ColorHex = "#E91E63" },
                    new PortDef { Name = "World", Direction = PortDirection.Output, DataType = typeof(Point2D[]), ColorHex = "#2196F3" }
                }
            },
            new OperatorDef
            {
                TypeId = "calibrate_poly2d",
                DisplayName = "二次多项式标定",
                Description = "使用二次多项式拟合像素->世界映射（适合轻微非线性）",
                Category = "标定",
                Ports =
                {
                    new PortDef { Name = "ImagePts", Direction = PortDirection.Input, DataType = typeof(Point2D[]), ColorHex = "#2196F3" },
                    new PortDef { Name = "WorldPts", Direction = PortDirection.Input, DataType = typeof(Point2D[]), ColorHex = "#2196F3" },
                    new PortDef { Name = "Poly", Direction = PortDirection.Output, DataType = typeof(Poly2DTransform), ColorHex = "#E91E63" }
                }
            },
            new OperatorDef
            {
                TypeId = "img_to_world_poly2d",
                DisplayName = "坐标转换(Poly2D)",
                Description = "使用二次多项式进行像素->世界坐标转换",
                Category = "标定",
                Ports =
                {
                    new PortDef { Name = "Pixel", Direction = PortDirection.Input, DataType = typeof(Point2D[]), ColorHex = "#2196F3" },
                    new PortDef { Name = "Poly", Direction = PortDirection.Input, DataType = typeof(Poly2DTransform), ColorHex = "#E91E63" },
                    new PortDef { Name = "World", Direction = PortDirection.Output, DataType = typeof(Point2D[]), ColorHex = "#2196F3" }
                }
            },
            new OperatorDef
            {
                TypeId = "display_calibration",
                DisplayName = "显示标定结果",
                Description = "统一显示 Affine/Homography/Poly2D 标定参数",
                Category = "标定",
                Ports =
                {
                    new PortDef { Name = "Transform", Direction = PortDirection.Input, DataType = typeof(AffineTransform), ColorHex = "#E91E63" },
                    new PortDef { Name = "H", Direction = PortDirection.Input, DataType = typeof(HomographyTransform), ColorHex = "#E91E63" },
                    new PortDef { Name = "Poly", Direction = PortDirection.Input, DataType = typeof(Poly2DTransform), ColorHex = "#E91E63" },
                    new PortDef { Name = "Out", Direction = PortDirection.Output, DataType = typeof(string), ColorHex = "#607D8B" }
                }
            },
            new OperatorDef
            {
                TypeId = "send_plc",
                DisplayName = "发送PLC",
                Description = "将轨迹发送到PLC",
                Category = "输出",
                Ports =
                {
                    new PortDef { Name = "Points", Direction = PortDirection.Input, DataType = typeof(Point2D[]), ColorHex = "#2196F3" }
                }
            },
            new OperatorDef
            {
                TypeId = "detect_circles",
                DisplayName = "检测圆点",
                Description = "检测标定圆点位置",
                Category = "标定",
                Ports =
                {
                    new PortDef { Name = "Image", Direction = PortDirection.Input, DataType = typeof(CalibImage), ColorHex = "#4CAF50" },
                    new PortDef { Name = "Points", Direction = PortDirection.Output, DataType = typeof(Point2D[]), ColorHex = "#2196F3" }
                }
            },
            new OperatorDef
            {
                TypeId = "expand_edge",
                DisplayName = "边界膨胀",
                Description = "Step5.5: 沿边缘膨胀",
                Category = "后处理",
                Ports =
                {
                    new PortDef { Name = "Dark", Direction = PortDirection.Input, DataType = typeof(CalibImage), ColorHex = "#4CAF50" },
                    new PortDef { Name = "Edge", Direction = PortDirection.Input, DataType = typeof(CalibImage), ColorHex = "#4CAF50" },
                    new PortDef { Name = "Out", Direction = PortDirection.Output, DataType = typeof(CalibImage), ColorHex = "#4CAF50" }
                },
                Params =
                {
                    new OperatorParam { Name = "expandDist", DisplayName = "膨胀距离", DefaultValue = "15", Description = "边缘膨胀距离 (像素)" }
                }
            },
            new OperatorDef
            {
                TypeId = "composite",
                DisplayName = "组合算子",
                Description = "嵌入子流程(.flow.json)：在参数中指定子图路径或 JSON，并用 bindingsJson 绑定外部端口与子节点端口",
                Category = "流程",
                DefaultWidth = 200,
                DefaultHeight = 88,
                Ports =
                {
                    new PortDef { Name = "In", Direction = PortDirection.Input, DataType = typeof(object), ColorHex = "#607D8B" },
                    new PortDef { Name = "Out", Direction = PortDirection.Output, DataType = typeof(object), ColorHex = "#607D8B" },
                    new PortDef { Name = "Out2", Direction = PortDirection.Output, DataType = typeof(object), ColorHex = "#78909C" }
                },
                Params =
                {
                    new OperatorParam
                    {
                        Name = "innerFlowPath",
                        DisplayName = "子流程文件",
                        DefaultValue = "",
                        Description = "可选；*.flow.json；可为相对路径（相对当前组态目录或程序目录）。若填空则用 innerFlowJson"
                    },
                    new OperatorParam
                    {
                        Name = "innerFlowJson",
                        DisplayName = "子流程JSON",
                        DefaultValue = "",
                        Description = "可选；与 innerFlowPath 二选一；内容为与保存组态相同的 JSON（nodes+connections）"
                    },
                    new OperatorParam
                    {
                        Name = "bindingsJson",
                        DisplayName = "端口绑定JSON",
                        DefaultValue = "{\"inputs\":[{\"external\":\"In\",\"nodeId\":\"00000000-0000-0000-0000-000000000000\",\"port\":\"In\"}],\"outputs\":[{\"external\":\"Out\",\"nodeId\":\"00000000-0000-0000-0000-000000000000\",\"port\":\"Out\"}]}",
                        Description = "external=组合算子端口名；nodeId/port=子节点 GUID 与端口名；可将输出映射到 Out 或 Out2"
                    }
                }
            },
        };

        // ================================================================
        // 画布上的节点模型
        // ================================================================

        /// <summary>
        /// 画布上已放置的节点实例
        /// </summary>
        public class FlowNode
        {
            public Guid Id { get; }
            public OperatorDef Def { get; }
            public double X { get; set; }
            public double Y { get; set; }

            // UI 元素（由 FlowPage 创建）
            public FrameworkElement? Visual { get; set; }
            public List<PortVisual> PortVisuals { get; } = new List<PortVisual>();

            // 执行结果数据（端口名 → 数据对象）
            public Dictionary<string, object?> Outputs { get; } = new Dictionary<string, object?>();
            public bool Executed { get; set; }
            public string? ErrorMessage { get; set; }

            // 算子参数（参数名 → 当前值）
            public Dictionary<string, string> Params { get; } = new Dictionary<string, string>();

            // 结果摘要文本
            public string? ResultSummary { get; set; }

            public FlowNode(OperatorDef def, double x, double y, Guid? fixedId = null)
            {
                Id = fixedId ?? Guid.NewGuid();
                Def = def;
                X = x;
                Y = y;
                // 初始化参数为默认值
                foreach (var p in def.Params)
                    Params[p.Name] = p.DefaultValue;
            }
        }

        /// <summary>
        /// 端口可视元素（一个小圆圈 + 在节点上的位置）
        /// </summary>
        public class PortVisual
        {
            public PortDef Definition { get; }
            public FlowNode Owner { get; }
            public Ellipse Ellipse { get; }
            public Point Center { get; set; }  // 画布坐标系下的中心点

            public PortVisual(PortDef def, FlowNode owner, Ellipse ellipse)
            {
                Definition = def;
                Owner = owner;
                Ellipse = ellipse;
            }
        }

        public class ToolboxGroup
        {
            public string Name { get; set; } = "";
            public bool IsExpanded { get; set; } = true;
            public ObservableCollection<OperatorDef> Operators { get; } = new ObservableCollection<OperatorDef>();
        }

        /// <summary>
        /// 连线
        /// </summary>
        public class FlowConnection
        {
            public Guid Id { get; } = Guid.NewGuid();
            public PortVisual FromPort { get; }
            public PortVisual ToPort { get; }
            public Path? PathVisual { get; set; }

            public FlowConnection(PortVisual from, PortVisual to)
            {
                FromPort = from;
                ToPort = to;
            }
        }

        // ================================================================
        // 页面状态
        // ================================================================

        private readonly List<FlowNode> _nodes = new List<FlowNode>();
        private readonly List<FlowConnection> _connections = new List<FlowConnection>();
        private readonly ObservableCollection<ToolboxGroup> _toolboxGroups = new ObservableCollection<ToolboxGroup>();

        // 拖拽状态
        private bool _isDraggingNode;
        private FlowNode? _dragNode;
        private Point _dragStart;
        private Point _nodeStartPos;

        // 连线状态
        private PortVisual? _connectingFromPort;
        private Path? _tempConnectionPath;
        private Window? _livePreviewWindow;
        private System.Windows.Controls.Image? _livePreviewImageCtrl;
        private System.Threading.CancellationTokenSource? _runCts;
        private bool _isRunInProgress;

        private int _nodeCounter;

        public FlowPage()
        {
            InitializeComponent();
            InitializeToolbox();
            if (StopRunButton != null) StopRunButton.IsEnabled = false;
        }

        private void ThrowIfExecutionCancelled()
        {
            if (_runCts?.IsCancellationRequested == true)
                throw new OperationCanceledException("用户停止执行");
        }

        private void InitializeToolbox()
        {
            string[] categoryOrder =
            {
                "输入",
                "预处理",
                "后处理",
                "验证",
                "流程",
                "标定",
                "输出",
                "可视化"
            };

            _toolboxGroups.Clear();
            var grouped = OperatorRegistry
                .GroupBy(o => o.Category ?? "")
                .ToDictionary(g => g.Key, g => g.OrderBy(x => x.DisplayName).ToList());

            foreach (var category in categoryOrder)
            {
                if (!grouped.TryGetValue(category, out var ops) || ops.Count == 0) continue;
                var group = new ToolboxGroup { Name = category, IsExpanded = true };
                foreach (var op in ops) group.Operators.Add(op);
                _toolboxGroups.Add(group);
                grouped.Remove(category);
            }

            foreach (var kv in grouped.OrderBy(k => k.Key))
            {
                var group = new ToolboxGroup { Name = kv.Key, IsExpanded = true };
                foreach (var op in kv.Value) group.Operators.Add(op);
                _toolboxGroups.Add(group);
            }

            OperatorToolbox.ItemsSource = _toolboxGroups;
        }

        // ================================================================
        // 从工具箱拖拽到画布
        // ================================================================

        private void OperatorItem_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.DataContext is OperatorDef def)
            {
                var data = new DataObject("OperatorDef", def);
                DragDrop.DoDragDrop(fe, data, DragDropEffects.Copy);
            }
        }

        private void FlowCanvas_DragOver(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent("OperatorDef"))
                e.Effects = DragDropEffects.Copy;
            else
                e.Effects = DragDropEffects.None;
            e.Handled = true;
        }

        private void FlowCanvas_Drop(object sender, DragEventArgs e)
        {
            if (!e.Data.GetDataPresent("OperatorDef")) return;

            var def = (OperatorDef)e.Data.GetData("OperatorDef");
            var pos = e.GetPosition(FlowCanvas);
            AddNode(def, pos.X - def.DefaultWidth / 2, pos.Y - def.DefaultHeight / 2);
            e.Handled = true;
        }

        // ================================================================
        // 节点创建
        // ================================================================

        private FlowNode AddNode(OperatorDef def, double x, double y)
        {
            var node = new FlowNode(def, x, y);
            _nodes.Add(node);
            CreateNodeVisual(node);
            return node;
        }

        private void CreateNodeVisual(FlowNode node)
        {
            var def = node.Def;
            double w = def.DefaultWidth;
            int inputCount = def.Ports.Count(p => p.Direction == PortDirection.Input);
            int outputCount = def.Ports.Count(p => p.Direction == PortDirection.Output);
            int rowCount = Math.Max(inputCount, outputCount);
            if (rowCount == 0) rowCount = 1;
            // 标题+端口区+摘要的最小自适应高度，避免新增端口被裁切
            double minAutoHeight = 52 + rowCount * 18 + 18;
            double h = Math.Max(def.DefaultHeight, minAutoHeight);

            // 主容器
            var border = new Border
            {
                Width = w,
                Height = h,
                Background = new SolidColorBrush(Color.FromRgb(0x2D, 0x2D, 0x30)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x3C, 0x3C, 0x3C)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Tag = node
            };
            border.MouseLeftButtonDown += Node_MouseLeftButtonDown;

            // 右键菜单
            var menuDelete = new MenuItem { Header = "删除算子" };
            menuDelete.Click += (s, e) => DeleteNode(node);

            var menuParams = new MenuItem { Header = "参数设置" };
            menuParams.Click += (s, e) => EditNodeParams(node);

            border.ContextMenu = new ContextMenu { Items = { menuParams, menuDelete } };

            var panel = new StackPanel();

            // 标题栏
            var titleBorder = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(0x37, 0x37, 0x3D)),
                CornerRadius = new CornerRadius(6, 6, 0, 0),
                Padding = new Thickness(8, 4, 8, 4)
            };
            var titleText = new TextBlock
            {
                Text = def.DisplayName,
                Foreground = new SolidColorBrush(Color.FromRgb(0xDC, 0xDC, 0xDC)),
                FontSize = 12,
                FontWeight = FontWeights.SemiBold
            };
            titleBorder.Child = titleText;
            panel.Children.Add(titleBorder);

            // 端口区域
            var portsGrid = new Grid { Margin = new Thickness(6, 4, 6, 4) };
            portsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            portsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            int inputIdx = 0;
            int outputIdx = 0;

            for (int r = 0; r < rowCount; r++)
            {
                portsGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Auto) });
            }

            foreach (var portDef in def.Ports)
            {
                var portEllipse = new Ellipse
                {
                    Width = 12,
                    Height = 12,
                    Stroke = new SolidColorBrush((Color)ColorConverter.ConvertFromString(portDef.ColorHex)),
                    Fill = Brushes.Transparent,
                    StrokeThickness = 2,
                    Cursor = Cursors.Cross
                };

                var portVisual = new PortVisual(portDef, node, portEllipse);
                node.PortVisuals.Add(portVisual);
                portEllipse.Tag = portVisual;
                portEllipse.MouseLeftButtonDown += Port_MouseLeftButtonDown;

                var label = new TextBlock
                {
                    Text = portDef.Name,
                    Foreground = new SolidColorBrush(Color.FromRgb(0xBB, 0xBB, 0xBB)),
                    FontSize = 10,
                    VerticalAlignment = VerticalAlignment.Center
                };

                var portPanel = new StackPanel { Orientation = Orientation.Horizontal };

                if (portDef.Direction == PortDirection.Input)
                {
                    portPanel.Children.Add(portEllipse);
                    portPanel.Children.Add(label);
                    Grid.SetColumn(portPanel, 0);
                    Grid.SetRow(portPanel, inputIdx);
                    portPanel.HorizontalAlignment = HorizontalAlignment.Left;
                    inputIdx++;
                }
                else
                {
                    label.HorizontalAlignment = HorizontalAlignment.Right;
                    portPanel.Children.Add(label);
                    portPanel.Children.Add(portEllipse);
                    Grid.SetColumn(portPanel, 1);
                    Grid.SetRow(portPanel, outputIdx);
                    portPanel.HorizontalAlignment = HorizontalAlignment.Right;
                    outputIdx++;
                }

                portsGrid.Children.Add(portPanel);
            }

            panel.Children.Add(portsGrid);

            // 结果摘要文本（执行后显示）
            var summaryText = new TextBlock
            {
                Text = "",
                Foreground = new SolidColorBrush(Color.FromRgb(0x80, 0xCB, 0xC4)),  // 浅青色
                FontSize = 9,
                Margin = new Thickness(8, 2, 8, 4),
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth = w - 16,
                Tag = "SummaryText"
            };
            panel.Children.Add(summaryText);
            node.ResultSummary = null;

            border.Child = panel;

            Canvas.SetLeft(border, node.X);
            Canvas.SetTop(border, node.Y);
            FlowCanvas.Children.Add(border);
            node.Visual = border;

            // 初始化端口位置
            UpdatePortPositions(node);
        }

        private void UpdatePortPositions(FlowNode node)
        {
            if (node.Visual == null) return;

            double nodeLeft = Canvas.GetLeft(node.Visual);
            double nodeTop = Canvas.GetTop(node.Visual);

            foreach (var pv in node.PortVisuals)
            {
                // 获取端口椭圆在画布坐标系中的位置
                var ellipseCenter = pv.Ellipse.TransformToVisual(FlowCanvas)
                    .Transform(new Point(pv.Ellipse.ActualWidth / 2, pv.Ellipse.ActualHeight / 2));
                pv.Center = ellipseCenter;
            }
        }

        // ================================================================
        // 节点拖拽
        // ================================================================

        private void Node_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount == 2)
            {
                if (sender is FrameworkElement fe2 && fe2.Tag is FlowNode node2)
                {
                    if (node2.Def.Params.Count > 0)
                        EditNodeParams(node2);
                }
                return;
            }

            if (sender is FrameworkElement fe && fe.Tag is FlowNode node)
            {
                _isDraggingNode = true;
                _dragNode = node;
                _dragStart = e.GetPosition(FlowCanvas);
                _nodeStartPos = new Point(Canvas.GetLeft(node.Visual), Canvas.GetTop(node.Visual));
                fe.CaptureMouse();
                e.Handled = true;
            }
        }

        // ================================================================
        // 端口连线交互
        // ================================================================

        private void Port_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is Ellipse ellipse && ellipse.Tag is PortVisual pv)
            {
                // 开始连线
                _connectingFromPort = pv;
                ellipse.CaptureMouse();

                _tempConnectionPath = new Path
                {
                    Stroke = new SolidColorBrush((Color)ColorConverter.ConvertFromString(pv.Definition.ColorHex)),
                    StrokeThickness = 2,
                    StrokeDashArray = new DoubleCollection { 4, 4 },
                    Data = new PathGeometry()
                };
                FlowCanvas.Children.Add(_tempConnectionPath);
                e.Handled = true;
            }
        }

        // ================================================================
        // 画布鼠标事件（节点拖拽 + 连线绘制）
        // ================================================================

        private void FlowCanvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            // 点击空白处取消连线
            if (_connectingFromPort != null)
            {
                CancelConnection();
                e.Handled = true;
            }
        }

        private void FlowCanvas_MouseMove(object sender, MouseEventArgs e)
        {
            var pos = e.GetPosition(FlowCanvas);

            // 节点拖拽
            if (_isDraggingNode && _dragNode?.Visual != null)
            {
                double dx = pos.X - _dragStart.X;
                double dy = pos.Y - _dragStart.Y;
                double newX = Math.Max(0, _nodeStartPos.X + dx);
                double newY = Math.Max(0, _nodeStartPos.Y + dy);

                Canvas.SetLeft(_dragNode.Visual, newX);
                Canvas.SetTop(_dragNode.Visual, newY);
                _dragNode.X = newX;
                _dragNode.Y = newY;

                UpdatePortPositions(_dragNode);
                UpdateAllConnections();
                e.Handled = true;
            }

            // 连线拖拽
            if (_connectingFromPort != null && _tempConnectionPath != null)
            {
                UpdateTempConnection(pos);
                e.Handled = true;
            }
        }

        private void FlowCanvas_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            // 结束节点拖拽
            if (_isDraggingNode && _dragNode?.Visual != null)
            {
                _dragNode.Visual.ReleaseMouseCapture();
                _isDraggingNode = false;
                _dragNode = null;
            }

            // 结束连线（检查是否落在目标端口上）
            if (_connectingFromPort != null)
            {
                _connectingFromPort.Ellipse.ReleaseMouseCapture();
                var target = FindPortAtPosition(e.GetPosition(FlowCanvas));
                if (target != null && CanConnect(_connectingFromPort, target))
                {
                    CreateConnection(_connectingFromPort, target);
                }
                else
                {
                    CancelConnection();
                }
            }

            e.Handled = true;
        }

        private PortVisual? FindPortAtPosition(Point pos)
        {
            foreach (var node in _nodes)
            {
                foreach (var pv in node.PortVisuals)
                {
                    UpdatePortPositions(node);
                    double dx = pos.X - pv.Center.X;
                    double dy = pos.Y - pv.Center.Y;
                    if (dx * dx + dy * dy < 144) // 12px radius
                        return pv;
                }
            }
            return null;
        }

        private bool CanConnect(PortVisual from, PortVisual to)
        {
            // 不同方向才能连
            if (from.Definition.Direction == to.Definition.Direction) return false;
            // 不能自连
            if (from.Owner == to.Owner) return false;
            // 相同数据类型（object 端口可与任意类型互连，便于组合算子等）
            if (!PortTypesCompatible(from.Definition.DataType, to.Definition.DataType)) return false;
            // 不重复连线
            if (_connections.Any(c =>
                (c.FromPort == from && c.ToPort == to) ||
                (c.FromPort == to && c.ToPort == from))) return false;
            // 目标端口只能有一条输入连线
            if (to.Definition.Direction == PortDirection.Input &&
                _connections.Any(c => c.ToPort == to)) return false;

            return true;
        }

        private static bool PortTypesCompatible(Type a, Type b)
        {
            if (a == b) return true;
            if (a == typeof(object) || b == typeof(object)) return true;
            return false;
        }

        // ================================================================
        // 连线管理
        // ================================================================

        private void CreateConnection(PortVisual from, PortVisual to)
        {
            // 确保 from 是 output，to 是 input
            var outPort = from.Definition.Direction == PortDirection.Output ? from : to;
            var inPort = from.Definition.Direction == PortDirection.Input ? from : to;

            var conn = new FlowConnection(outPort, inPort);
            var path = new Path
            {
                Stroke = new SolidColorBrush((Color)ColorConverter.ConvertFromString(outPort.Definition.ColorHex)),
                StrokeThickness = 2,
                Data = new PathGeometry(),
                Tag = conn
            };
            path.MouseRightButtonDown += Connection_MouseRightButtonDown;
            path.MouseLeftButtonDown += Connection_MouseLeftButtonDown;

            conn.PathVisual = path;
            _connections.Add(conn);
            FlowCanvas.Children.Add(path);

            UpdateConnectionPath(conn);
            CleanupTempConnection();
        }

        private void UpdateConnectionPath(FlowConnection conn)
        {
            if (conn.PathVisual == null) return;

            UpdatePortPositions(conn.FromPort.Owner);
            UpdatePortPositions(conn.ToPort.Owner);

            var start = conn.FromPort.Center;
            var end = conn.ToPort.Center;
            conn.PathVisual.Data = CreateBezierGeometry(start, end);
        }

        private PathGeometry CreateBezierGeometry(Point start, Point end)
        {
            double dx = Math.Abs(end.X - start.X);
            double offset = Math.Max(dx * 0.5, 50);

            var fig = new PathFigure
            {
                StartPoint = start,
                IsClosed = false
            };

            var seg = new BezierSegment
            {
                Point1 = new Point(start.X + offset, start.Y),
                Point2 = new Point(end.X - offset, end.Y),
                Point3 = end
            };

            fig.Segments.Add(seg);

            var geo = new PathGeometry();
            geo.Figures.Add(fig);
            return geo;
        }

        private void UpdateTempConnection(Point mousePos)
        {
            if (_tempConnectionPath == null || _connectingFromPort == null) return;

            UpdatePortPositions(_connectingFromPort.Owner);
            var start = _connectingFromPort.Center;
            var end = mousePos;

            if (_connectingFromPort.Definition.Direction == PortDirection.Output)
                _tempConnectionPath.Data = CreateBezierGeometry(start, end);
            else
                _tempConnectionPath.Data = CreateBezierGeometry(end, start);
        }

        private void CancelConnection()
        {
            CleanupTempConnection();
            _connectingFromPort = null;
        }

        private void CleanupTempConnection()
        {
            if (_tempConnectionPath != null)
            {
                FlowCanvas.Children.Remove(_tempConnectionPath);
                _tempConnectionPath = null;
            }
        }

        private void UpdateAllConnections()
        {
            foreach (var conn in _connections)
                UpdateConnectionPath(conn);
        }

        private void Connection_MouseRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is Path path && path.Tag is FlowConnection conn)
            {
                // 右键删除连线
                _connections.Remove(conn);
                if (conn.PathVisual != null)
                    FlowCanvas.Children.Remove(conn.PathVisual);
                e.Handled = true;
            }
        }

        private void Connection_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is not Path path || path.Tag is not FlowConnection conn) return;

            if (!conn.FromPort.Owner.Outputs.TryGetValue(conn.FromPort.Definition.Name, out var data) || data == null)
            {
                MessageBox.Show(
                    $"该连线上暂无可用数据。\n上游节点：{conn.FromPort.Owner.Def.DisplayName}\n端口：{conn.FromPort.Definition.Name}\n请先运行 Flow。",
                    "连线数据",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                e.Handled = true;
                return;
            }

            // 图像类型：直接弹窗预览
            if (data is CalibImage img)
            {
                ShowImagePreview(img);
                e.Handled = true;
                return;
            }

            // 点位类型：如果上游同节点有图像，叠加点位预览；否则显示摘要
            if (data is Point2D[] pts)
            {
                var baseImg = conn.FromPort.Owner.Outputs.Values.OfType<CalibImage>().FirstOrDefault();
                if (baseImg != null)
                {
                    ShowImagePreview(baseImg, pts, 3);
                }
                else
                {
                    MessageBox.Show(
                        $"点位数量：{pts.Length}\n示例首点：{(pts.Length > 0 ? $"({pts[0].X:F2}, {pts[0].Y:F2})" : "N/A")}",
                        "连线数据 - 点位",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                }
                e.Handled = true;
                return;
            }

            if (data is ValueTuple<int[], int[], int[], int> contours)
            {
                var baseImg = conn.FromPort.Owner.Outputs.Values.OfType<CalibImage>().FirstOrDefault();
                ShowContoursPreview(contours, baseImg);
                e.Handled = true;
                return;
            }

            // 其他类型：结构化文本展示
            MessageBox.Show(
                BuildConnectionDataText(conn, data),
                "连线数据",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            e.Handled = true;
        }

        private static string BuildConnectionDataText(FlowConnection conn, object data)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"上游节点: {conn.FromPort.Owner.Def.DisplayName}");
            sb.AppendLine($"下游节点: {conn.ToPort.Owner.Def.DisplayName}");
            sb.AppendLine($"端口: {conn.FromPort.Definition.Name} -> {conn.ToPort.Definition.Name}");
            sb.AppendLine($"类型: {data.GetType().Name}");
            sb.AppendLine();

            switch (data)
            {
                case int n:
                    sb.AppendLine($"值: {n}");
                    break;
                case AffineTransform t:
                    sb.AppendLine($"X = {t.A:F6}*x + {t.B:F6}*y + {t.C:F6}");
                    sb.AppendLine($"Y = {t.D:F6}*x + {t.E:F6}*y + {t.F:F6}");
                    break;
                case TrajectoryResult tr:
                    sb.AppendLine($"Success: {tr.Success}");
                    sb.AppendLine($"Count: {tr.Count}");
                    if (tr.Points != null && tr.Points.Length > 0)
                        sb.AppendLine($"First Point: ({tr.Points[0].X:F2}, {tr.Points[0].Y:F2})");
                    break;
                case ValueTuple<int[], int[], int[], int> contours:
                    sb.AppendLine($"Contours: {contours.Item4}");
                    sb.AppendLine($"Total Points: {contours.Item1?.Length ?? 0}");
                    if (contours.Item3 != null && contours.Item3.Length > 0)
                        sb.AppendLine($"First Contour Length: {contours.Item3[0]}");
                    break;
                default:
                    sb.AppendLine(data.ToString() ?? "(无可显示内容)");
                    break;
            }

            return sb.ToString();
        }

        // ================================================================
        // 工具栏按钮
        // ================================================================

        // ================================================================
        // 保存 / 加载组态
        // ================================================================

        private void SaveFlow_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new SaveFileDialog
            {
                Filter = "组态文件|*.flow.json|所有文件|*.*",
                DefaultExt = ".flow.json",
                Title = "保存组态"
            };
            if (dlg.ShowDialog() != true) return;

            try
            {
                var data = BuildCurrentFlowData();
                var options = new JsonSerializerOptions { WriteIndented = true };
                var json = JsonSerializer.Serialize(data, options);
                System.IO.File.WriteAllText(dlg.FileName, json);

                StatusText.Text = $"已保存: {System.IO.Path.GetFileName(dlg.FileName)}";
            }
            catch (Exception ex)
            {
                MessageBox.Show($"保存失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private FlowData BuildCurrentFlowData()
        {
            var nodes = _nodes.Select(n => new FlowNodeData
            {
                Id = n.Id.ToString(),
                TypeId = n.Def.TypeId,
                X = n.X,
                Y = n.Y,
                Params = new Dictionary<string, string>(n.Params)
            }).ToList();

            var connections = _connections.Select(c => new FlowConnData
            {
                FromNodeId = c.FromPort.Owner.Id.ToString(),
                FromPort = c.FromPort.Definition.Name,
                ToNodeId = c.ToPort.Owner.Id.ToString(),
                ToPort = c.ToPort.Definition.Name
            }).ToList();

            return new FlowData { Nodes = nodes, Connections = connections };
        }

        public bool LoadFlowFromFile(string filePath, bool showErrorDialog = true)
        {
            try
            {
                var json = System.IO.File.ReadAllText(filePath);
                var data = JsonSerializer.Deserialize<FlowData>(json);
                if (data == null) throw new Exception("文件内容为空");

                // 先清空画布
                ClearCanvas_Click(this, new RoutedEventArgs());

                // 按 Id 查找算子定义
                var defLookup = OperatorRegistry.ToDictionary(d => d.TypeId);

                // 第一遍：创建所有节点
                var nodeLookup = new Dictionary<string, FlowNode>();
                foreach (var nd in data.Nodes)
                {
                    if (!defLookup.TryGetValue(nd.TypeId, out var def))
                        throw new Exception($"未知算子类型: {nd.TypeId}");
                    var node = AddNode(def, nd.X, nd.Y);
                    // 恢复参数
                    if (nd.Params != null)
                    {
                        foreach (var kv in nd.Params)
                        {
                            if (node.Params.ContainsKey(kv.Key))
                                node.Params[kv.Key] = kv.Value;
                        }
                    }
                    nodeLookup[nd.Id] = node;
                }

                // 第二遍：建立连线
                foreach (var cd in data.Connections)
                {
                    if (!nodeLookup.TryGetValue(cd.FromNodeId, out var fromNode))
                        continue;
                    if (!nodeLookup.TryGetValue(cd.ToNodeId, out var toNode))
                        continue;

                    var fromPort = fromNode.PortVisuals.FirstOrDefault(p =>
                        p.Definition.Name == cd.FromPort && p.Definition.Direction == PortDirection.Output);
                    var toPort = toNode.PortVisuals.FirstOrDefault(p =>
                        p.Definition.Name == cd.ToPort && p.Definition.Direction == PortDirection.Input);

                    if (fromPort != null && toPort != null && CanConnect(fromPort, toPort))
                        CreateConnection(fromPort, toPort);
                }

                // 节点和端口在刚创建后可能尚未完成布局，端口中心点会是默认值。
                // 强制刷新布局后重算端口与连线，避免“加载后不显示线，拖一下节点才显示”。
                FlowCanvas.UpdateLayout();
                foreach (var n in _nodes) UpdatePortPositions(n);
                UpdateAllConnections();
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    FlowCanvas.UpdateLayout();
                    foreach (var n in _nodes) UpdatePortPositions(n);
                    UpdateAllConnections();
                }), System.Windows.Threading.DispatcherPriority.Loaded);

                CurrentFlowFilePath = System.IO.Path.GetFullPath(filePath);
                FlowLoaded?.Invoke(CurrentFlowFilePath);
                StatusText.Text = $"已加载: {System.IO.Path.GetFileName(filePath)} ({data.Nodes.Count} 节点, {data.Connections.Count} 连线)";
                return true;
            }
            catch (Exception ex)
            {
                if (showErrorDialog)
                    MessageBox.Show($"加载失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                StatusText.Text = $"加载失败: {ex.Message}";
                AppendLog($"[ERROR] 加载组态失败: {ex.Message}", true);
                return false;
            }
        }

        private void LoadFlow_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Filter = "组态文件|*.flow.json|所有文件|*.*",
                DefaultExt = ".flow.json",
                Title = "加载组态"
            };
            if (dlg.ShowDialog() != true) return;
            LoadFlowFromFile(dlg.FileName, showErrorDialog: true);
        }

        // ================================================================
        // JSON 序列化模型
        // ================================================================

        private class FlowData
        {
            public List<FlowNodeData> Nodes { get; set; } = new();
            public List<FlowConnData> Connections { get; set; } = new();
        }

        private class FlowNodeData
        {
            public string Id { get; set; } = "";
            public string TypeId { get; set; } = "";
            public double X { get; set; }
            public double Y { get; set; }
            public Dictionary<string, string>? Params { get; set; }
        }

        private class FlowConnData
        {
            public string FromNodeId { get; set; } = "";
            public string FromPort { get; set; } = "";
            public string ToNodeId { get; set; } = "";
            public string ToPort { get; set; } = "";
        }

        private class CompositeBindingsSpec
        {
            public List<CompositeIoBind>? Inputs { get; set; }
            public List<CompositeIoBind>? Outputs { get; set; }
        }

        private class CompositeIoBind
        {
            public string? External { get; set; }
            public string? NodeId { get; set; }
            public string? Port { get; set; }
        }

        private void ClearCanvas_Click(object sender, RoutedEventArgs e)
        {
            // 删除所有连线
            foreach (var conn in _connections)
            {
                if (conn.PathVisual != null)
                    FlowCanvas.Children.Remove(conn.PathVisual);
            }
            _connections.Clear();

            // 删除所有节点
            foreach (var node in _nodes)
            {
                if (node.Visual != null)
                    FlowCanvas.Children.Remove(node.Visual);
            }
            _nodes.Clear();

            _nodeCounter = 0;
            StatusText.Text = "画布已清空";
        }

        private void DeleteNode(FlowNode node)
        {
            // 删除与该节点相关的所有连线
            var toRemove = _connections.Where(c => c.FromPort.Owner == node || c.ToPort.Owner == node).ToList();
            foreach (var conn in toRemove)
            {
                if (conn.PathVisual != null)
                    FlowCanvas.Children.Remove(conn.PathVisual);
                _connections.Remove(conn);
            }

            // 删除节点视觉元素
            if (node.Visual != null)
                FlowCanvas.Children.Remove(node.Visual);
            _nodes.Remove(node);

            StatusText.Text = $"已删除: {node.Def.DisplayName}";
        }

        private void AutoLayout_Click(object sender, RoutedEventArgs e)
        {
            if (_nodes.Count == 0) return;

            // 拓扑排序 + 自动布局
            var sorted = TopologicalSort();

            double xBase = 60;
            double yBase = 40;
            double xStep = 240;
            double yStep = 120;

            // 分层：按拓扑层级排列
            var levels = new Dictionary<FlowNode, int>();
            var inDegree = new Dictionary<FlowNode, int>();

            foreach (var n in _nodes) inDegree[n] = 0;
            foreach (var c in _connections)
            {
                if (inDegree.ContainsKey(c.ToPort.Owner))
                    inDegree[c.ToPort.Owner]++;
            }

            var queue = new Queue<FlowNode>();
            foreach (var n in _nodes)
            {
                if (inDegree[n] == 0)
                {
                    levels[n] = 0;
                    queue.Enqueue(n);
                }
            }

            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                foreach (var c in _connections)
                {
                    if (c.FromPort.Owner == current)
                    {
                        var target = c.ToPort.Owner;
                        int newLevel = levels[current] + 1;
                        if (!levels.ContainsKey(target) || levels[target] < newLevel)
                            levels[target] = newLevel;
                        inDegree[target]--;
                        if (inDegree[target] == 0 && !levels.ContainsKey(target))
                        {
                            levels[target] = newLevel;
                            queue.Enqueue(target);
                        }
                    }
                }
            }

            // 未分层的节点放到最后一层
            foreach (var n in _nodes)
            {
                if (!levels.ContainsKey(n))
                    levels[n] = levels.Values.DefaultIfEmpty(0).Max() + 1;
            }

            // 按层级分组
            var groups = _nodes.GroupBy(n => levels[n])
                              .OrderBy(g => g.Key)
                              .ToList();

            foreach (var group in groups)
            {
                int col = group.Key;
                for (int i = 0; i < group.Count(); i++)
                {
                    var n = group.ElementAt(i);
                    double newX = xBase + col * xStep;
                    double newY = yBase + i * yStep;

                    if (n.Visual != null)
                    {
                        Canvas.SetLeft(n.Visual, newX);
                        Canvas.SetTop(n.Visual, newY);
                    }
                    n.X = newX;
                    n.Y = newY;
                }
            }

            // 更新端口和连线
            foreach (var n in _nodes) UpdatePortPositions(n);
            UpdateAllConnections();

            StatusText.Text = "自动布局完成";
        }

        private List<FlowNode> TopologicalSort()
        {
            var visited = new HashSet<Guid>();
            var result = new List<FlowNode>();
            var inDegree = new Dictionary<Guid, int>();

            foreach (var n in _nodes) inDegree[n.Id] = 0;
            foreach (var c in _connections) inDegree[c.ToPort.Owner.Id]++;

            var queue = new Queue<FlowNode>();
            foreach (var n in _nodes)
            {
                if (inDegree[n.Id] == 0)
                    queue.Enqueue(n);
            }

            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                if (visited.Contains(current.Id)) continue;
                visited.Add(current.Id);
                result.Add(current);

                foreach (var c in _connections)
                {
                    if (c.FromPort.Owner == current)
                    {
                        inDegree[c.ToPort.Owner.Id]--;
                        if (inDegree[c.ToPort.Owner.Id] == 0)
                            queue.Enqueue(c.ToPort.Owner);
                    }
                }
            }

            // 添加未被访问的节点
            foreach (var n in _nodes)
            {
                if (!visited.Contains(n.Id))
                    result.Add(n);
            }

            return result;
        }

        // ================================================================
        // 算子执行引擎
        // ================================================================

        /// <summary>
        /// 获取节点的输入端口数据（从上游连线的输出端口取值）
        /// </summary>
        private object? GetInputData(FlowNode node, string portName)
        {
            var inPort = node.PortVisuals.FirstOrDefault(pv =>
                pv.Definition.Direction == PortDirection.Input && pv.Definition.Name == portName);
            if (inPort == null) return null;

            var conn = _connections.FirstOrDefault(c => c.ToPort == inPort);
            if (conn == null) return null;

            return conn.FromPort.Owner.Outputs.GetValueOrDefault(conn.FromPort.Definition.Name);
        }

        /// <summary>
        /// 获取所有上游连接的输出数据
        /// </summary>
        private Dictionary<string, object?> GetNodeInputs(FlowNode node)
        {
            var inputs = new Dictionary<string, object?>();
            foreach (var pv in node.PortVisuals.Where(p => p.Definition.Direction == PortDirection.Input))
            {
                var conn = _connections.FirstOrDefault(c => c.ToPort == pv);
                if (conn != null)
                    inputs[pv.Definition.Name] = conn.FromPort.Owner.Outputs.GetValueOrDefault(conn.FromPort.Definition.Name);
            }
            return inputs;
        }

        /// <summary>
        /// 高亮/恢复节点边框（执行状态可视化）
        /// </summary>
        private void SetNodeStatus(FlowNode node, bool running, bool error = false)
        {
            if (node.Visual is not Border border) return;
            border.Dispatcher.Invoke(() =>
            {
                if (error)
                    border.BorderBrush = new SolidColorBrush(Colors.Red);
                else if (running)
                    border.BorderBrush = new SolidColorBrush(Colors.Orange);
                else
                    border.BorderBrush = new SolidColorBrush(Color.FromRgb(0x3C, 0x3C, 0x3C));
                border.BorderThickness = new Thickness(running || error ? 2 : 1);
            });
        }

        /// <summary>
        /// 向日志区追加一行（线程安全）
        /// </summary>
        private void AppendLog(string text, bool isError = false)
        {
            LogBox.Dispatcher.Invoke(() =>
            {
                var timestamp = DateTime.Now.ToString("HH:mm:ss");
                LogBox.AppendText($"[{timestamp}] {text}\n");
                LogBox.ScrollToEnd();
                if (isError)
                {
                    // 最后追加的行标红不方便，改为整体不改色，靠前缀 [ERROR] 区分
                }
            });
        }

        private void ClearLog_Click(object sender, RoutedEventArgs e)
        {
            LogBox.Text = "";
        }

        private static Point2D[] SmoothPoints(Point2D[] points, int windowRadius = 1)
        {
            if (points == null || points.Length < 3 || windowRadius <= 0) return points ?? Array.Empty<Point2D>();
            int n = points.Length;
            var smoothed = new Point2D[n];
            for (int i = 0; i < n; i++)
            {
                double sx = 0;
                double sy = 0;
                int cnt = 0;
                for (int k = -windowRadius; k <= windowRadius; k++)
                {
                    int idx = i + k;
                    if (idx < 0) idx += n;
                    if (idx >= n) idx -= n;
                    sx += points[idx].X;
                    sy += points[idx].Y;
                    cnt++;
                }
                smoothed[i] = new Point2D(sx / cnt, sy / cnt);
            }
            return smoothed;
        }

        private static Point2D[] SimplifyClosedPolyline(Point2D[] points, double epsilon)
        {
            if (points == null || points.Length < 4 || epsilon <= 0) return points ?? Array.Empty<Point2D>();
            var open = points.ToList();
            open.Add(points[0]);
            var simplified = SimplifyOpenPolyline(open, epsilon);
            if (simplified.Count > 1)
                simplified.RemoveAt(simplified.Count - 1);
            return simplified.ToArray();
        }

        private static List<Point2D> SimplifyOpenPolyline(List<Point2D> points, double epsilon)
        {
            if (points.Count <= 2) return new List<Point2D>(points);

            int index = -1;
            double maxDist = -1;
            var start = points[0];
            var end = points[^1];

            for (int i = 1; i < points.Count - 1; i++)
            {
                double dist = PointLineDistance(points[i], start, end);
                if (dist > maxDist)
                {
                    maxDist = dist;
                    index = i;
                }
            }

            if (maxDist <= epsilon || index <= 0)
                return new List<Point2D> { start, end };

            var left = SimplifyOpenPolyline(points.GetRange(0, index + 1), epsilon);
            var right = SimplifyOpenPolyline(points.GetRange(index, points.Count - index), epsilon);
            left.RemoveAt(left.Count - 1);
            left.AddRange(right);
            return left;
        }

        private static double PointLineDistance(Point2D p, Point2D a, Point2D b)
        {
            double vx = b.X - a.X;
            double vy = b.Y - a.Y;
            double wx = p.X - a.X;
            double wy = p.Y - a.Y;

            double c1 = vx * wx + vy * wy;
            if (c1 <= 0) return Math.Sqrt(wx * wx + wy * wy);

            double c2 = vx * vx + vy * vy;
            if (c2 <= 1e-9)
                return Math.Sqrt((p.X - a.X) * (p.X - a.X) + (p.Y - a.Y) * (p.Y - a.Y));

            double t = c1 / c2;
            double px = a.X + t * vx;
            double py = a.Y + t * vy;
            double dx = p.X - px;
            double dy = p.Y - py;
            return Math.Sqrt(dx * dx + dy * dy);
        }

        private static Point2D[] ResampleClosedPolyline(Point2D[] points, int targetCount)
        {
            if (points == null || points.Length == 0 || targetCount <= 0) return Array.Empty<Point2D>();
            if (points.Length == 1) return Enumerable.Repeat(points[0], targetCount).ToArray();

            int n = points.Length;
            var cum = new double[n + 1];
            cum[0] = 0;
            for (int i = 0; i < n; i++)
            {
                var p0 = points[i];
                var p1 = points[(i + 1) % n];
                double dx = p1.X - p0.X;
                double dy = p1.Y - p0.Y;
                cum[i + 1] = cum[i] + Math.Sqrt(dx * dx + dy * dy);
            }

            double perimeter = cum[n];
            if (perimeter <= 1e-6) return Enumerable.Repeat(points[0], targetCount).ToArray();

            var result = new Point2D[targetCount];
            for (int i = 0; i < targetCount; i++)
            {
                double s = (i * perimeter) / targetCount;
                int seg = 0;
                while (seg < n - 1 && cum[seg + 1] < s) seg++;
                double segStart = cum[seg];
                double segLen = cum[seg + 1] - segStart;
                var a = points[seg];
                var b = points[(seg + 1) % n];
                double t = segLen <= 1e-9 ? 0 : (s - segStart) / segLen;
                result[i] = new Point2D(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t);
            }
            return result;
        }

        private static double PointDistance(Point2D a, Point2D b)
        {
            double dx = a.X - b.X;
            double dy = a.Y - b.Y;
            return Math.Sqrt(dx * dx + dy * dy);
        }

        private static List<Point2D[]> SplitIntoClosedRegions(Point2D[] points, double splitGapFactor, int minRegionPoints)
        {
            var regions = new List<Point2D[]>();
            if (points == null || points.Length == 0) return regions;
            if (points.Length < 4)
            {
                regions.Add(points);
                return regions;
            }

            var steps = new List<double>(points.Length - 1);
            for (int i = 0; i < points.Length - 1; i++)
                steps.Add(PointDistance(points[i], points[i + 1]));
            var ordered = steps.OrderBy(v => v).ToArray();
            double medianStep = ordered.Length == 0 ? 0 : ordered[ordered.Length / 2];
            if (medianStep <= 1e-9 || splitGapFactor <= 1.0)
            {
                regions.Add(points);
                return regions;
            }

            double threshold = medianStep * splitGapFactor;
            int start = 0;
            for (int i = 0; i < points.Length - 1; i++)
            {
                if (steps[i] <= threshold) continue;
                int len = i - start + 1;
                if (len >= Math.Max(3, minRegionPoints))
                    regions.Add(points.Skip(start).Take(len).ToArray());
                start = i + 1;
            }

            int tailLen = points.Length - start;
            if (tailLen >= Math.Max(3, minRegionPoints))
                regions.Add(points.Skip(start).Take(tailLen).ToArray());

            // 若自动分割失败，回退整体拟合
            if (regions.Count == 0)
                regions.Add(points);
            return regions;
        }

        private static List<(Point2D[] Points, int[] BarIds)> SplitRegionsByBarIds(Point2D[] points, int[] barIds, int minRegionPoints)
        {
            var regions = new List<(Point2D[] Points, int[] BarIds)>();
            if (points == null || barIds == null || points.Length == 0 || barIds.Length != points.Length)
                return regions;

            int start = 0;
            for (int i = 1; i < barIds.Length; i++)
            {
                if (barIds[i] == barIds[i - 1]) continue;
                int len = i - start;
                if (len >= Math.Max(3, minRegionPoints))
                    regions.Add((points.Skip(start).Take(len).ToArray(), barIds.Skip(start).Take(len).ToArray()));
                start = i;
            }

            int tailLen = barIds.Length - start;
            if (tailLen >= Math.Max(3, minRegionPoints))
                regions.Add((points.Skip(start).Take(tailLen).ToArray(), barIds.Skip(start).Take(tailLen).ToArray()));

            return regions;
        }

        private static (int[] flatX, int[] flatY, int[] lengths, int count) FilterContoursByGeometry(
            ValueTuple<int[], int[], int[], int> contourData,
            double minArea, double maxArea, double minAspect, double maxAspect, double minCircularity, int targetCount)
        {
            var (flatX, flatY, contourLengths, contourCount) = contourData;
            if (contourCount <= 0 || contourLengths == null || contourLengths.Length == 0)
                return (Array.Empty<int>(), Array.Empty<int>(), Array.Empty<int>(), 0);

            var accepted = new List<(int Start, int Len, double Area)>();
            int offset = 0;
            for (int ci = 0; ci < contourCount && ci < contourLengths.Length; ci++)
            {
                int len = contourLengths[ci];
                if (len < 3 || offset + len > flatX.Length || offset + len > flatY.Length)
                {
                    offset += Math.Max(0, len);
                    continue;
                }

                int minX = int.MaxValue, maxX = int.MinValue, minY = int.MaxValue, maxY = int.MinValue;
                double perimeter = 0;
                var pts = new List<System.Drawing.Point>(len);
                for (int k = 0; k < len; k++)
                {
                    int x = flatX[offset + k];
                    int y = flatY[offset + k];
                    pts.Add(new System.Drawing.Point(x, y));
                    if (x < minX) minX = x;
                    if (x > maxX) maxX = x;
                    if (y < minY) minY = y;
                    if (y > maxY) maxY = y;

                    int nk = (k + 1) % len;
                    double dx = flatX[offset + nk] - x;
                    double dy = flatY[offset + nk] - y;
                    perimeter += Math.Sqrt(dx * dx + dy * dy);
                }

                double area = PolygonArea(pts);
                double w = Math.Max(1, maxX - minX + 1);
                double h = Math.Max(1, maxY - minY + 1);
                double aspect = w / h;
                double circularity = perimeter <= 1e-6 ? 0 : (4.0 * Math.PI * area) / (perimeter * perimeter);

                if (area >= minArea && area <= maxArea &&
                    aspect >= minAspect && aspect <= maxAspect &&
                    circularity >= minCircularity)
                {
                    accepted.Add((offset, len, area));
                }

                offset += len;
            }

            if (accepted.Count == 0)
                return (Array.Empty<int>(), Array.Empty<int>(), Array.Empty<int>(), 0);

            var selected = accepted
                .OrderByDescending(c => c.Area)
                .Take(Math.Max(1, targetCount))
                .ToList();

            var outLens = new List<int>(selected.Count);
            var outX = new List<int>(selected.Sum(s => s.Len));
            var outY = new List<int>(selected.Sum(s => s.Len));

            foreach (var s in selected)
            {
                outLens.Add(s.Len);
                for (int i = 0; i < s.Len; i++)
                {
                    outX.Add(flatX[s.Start + i]);
                    outY.Add(flatY[s.Start + i]);
                }
            }

            return (outX.ToArray(), outY.ToArray(), outLens.ToArray(), outLens.Count);
        }

        private static Point2D[] ResampleContourByArc(IReadOnlyList<Point2D> points, int sampleCount)
        {
            if (points == null || points.Count < 3 || sampleCount < 8) return Array.Empty<Point2D>();
            int n = points.Count;
            var cum = new double[n + 1];
            cum[0] = 0;
            for (int i = 0; i < n; i++)
            {
                var a = points[i];
                var b = points[(i + 1) % n];
                double dx = b.X - a.X;
                double dy = b.Y - a.Y;
                cum[i + 1] = cum[i] + Math.Sqrt(dx * dx + dy * dy);
            }
            double perimeter = cum[n];
            if (perimeter <= 1e-6) return Array.Empty<Point2D>();

            var sampled = new Point2D[sampleCount];
            for (int i = 0; i < sampleCount; i++)
            {
                double s = i * perimeter / sampleCount;
                int seg = 0;
                while (seg < n - 1 && cum[seg + 1] < s) seg++;
                double segLen = cum[seg + 1] - cum[seg];
                double t = segLen <= 1e-9 ? 0 : (s - cum[seg]) / segLen;
                var p0 = points[seg];
                var p1 = points[(seg + 1) % n];
                sampled[i] = new Point2D(p0.X + (p1.X - p0.X) * t, p0.Y + (p1.Y - p0.Y) * t);
            }
            return sampled;
        }

        private static Point2D[] NormalizeShape(Point2D[] shape)
        {
            if (shape == null || shape.Length == 0) return Array.Empty<Point2D>();
            double cx = shape.Average(p => p.X);
            double cy = shape.Average(p => p.Y);
            double scale = Math.Sqrt(shape.Sum(p =>
            {
                double dx = p.X - cx, dy = p.Y - cy;
                return dx * dx + dy * dy;
            }) / shape.Length);
            if (scale <= 1e-9) scale = 1.0;
            return shape.Select(p => new Point2D((p.X - cx) / scale, (p.Y - cy) / scale)).ToArray();
        }

        private static double ComputeShapeDistance(Point2D[] a, Point2D[] b)
        {
            if (a.Length == 0 || b.Length == 0 || a.Length != b.Length) return double.PositiveInfinity;
            int n = a.Length;
            double best = double.PositiveInfinity;
            for (int shift = 0; shift < n; shift++)
            {
                double err1 = 0;
                double err2 = 0;
                for (int i = 0; i < n; i++)
                {
                    var pa = a[i];
                    var pb1 = b[(i + shift) % n];
                    var pb2 = b[(n - i + shift) % n]; // 反向匹配，消除走向差异
                    double dx1 = pa.X - pb1.X, dy1 = pa.Y - pb1.Y;
                    double dx2 = pa.X - pb2.X, dy2 = pa.Y - pb2.Y;
                    err1 += dx1 * dx1 + dy1 * dy1;
                    err2 += dx2 * dx2 + dy2 * dy2;
                }
                double cur = Math.Min(err1, err2) / n;
                if (cur < best) best = cur;
            }
            return Math.Sqrt(best);
        }

        private static (int[] flatX, int[] flatY, int[] lengths, int count) MatchContoursByTemplate(
            ValueTuple<int[], int[], int[], int> contourData, int templateIndex, double maxDistance, int samplePoints, int targetCount)
        {
            var (flatX, flatY, contourLengths, contourCount) = contourData;
            if (contourCount <= 0 || contourLengths == null || contourLengths.Length == 0)
                return (Array.Empty<int>(), Array.Empty<int>(), Array.Empty<int>(), 0);

            var contours = new List<(int Start, int Len, double Area, Point2D[] Pts)>();
            int offset = 0;
            for (int ci = 0; ci < contourCount && ci < contourLengths.Length; ci++)
            {
                int len = contourLengths[ci];
                if (len < 8 || offset + len > flatX.Length || offset + len > flatY.Length)
                {
                    offset += Math.Max(0, len);
                    continue;
                }
                var pts = new List<System.Drawing.Point>(len);
                var pts2 = new Point2D[len];
                for (int i = 0; i < len; i++)
                {
                    int x = flatX[offset + i], y = flatY[offset + i];
                    pts.Add(new System.Drawing.Point(x, y));
                    pts2[i] = new Point2D(x, y);
                }
                contours.Add((offset, len, PolygonArea(pts), pts2));
                offset += len;
            }
            if (contours.Count == 0) return (Array.Empty<int>(), Array.Empty<int>(), Array.Empty<int>(), 0);

            int tplIdx = templateIndex;
            if (tplIdx < 0 || tplIdx >= contours.Count)
            {
                double bestArea = double.MinValue;
                tplIdx = 0;
                for (int i = 0; i < contours.Count; i++)
                {
                    if (contours[i].Area > bestArea)
                    {
                        bestArea = contours[i].Area;
                        tplIdx = i;
                    }
                }
            }

            var tplResampled = ResampleContourByArc(contours[tplIdx].Pts, Math.Max(32, samplePoints));
            var tplNorm = NormalizeShape(tplResampled);
            if (tplNorm.Length == 0) return (Array.Empty<int>(), Array.Empty<int>(), Array.Empty<int>(), 0);

            var matched = new List<(int Start, int Len, double Dist)>();
            for (int i = 0; i < contours.Count; i++)
            {
                var cResampled = ResampleContourByArc(contours[i].Pts, tplNorm.Length);
                var cNorm = NormalizeShape(cResampled);
                double dist = ComputeShapeDistance(tplNorm, cNorm);
                if (dist <= maxDistance)
                    matched.Add((contours[i].Start, contours[i].Len, dist));
            }
            if (matched.Count == 0) return (Array.Empty<int>(), Array.Empty<int>(), Array.Empty<int>(), 0);

            var selected = matched.OrderBy(m => m.Dist).Take(Math.Max(1, targetCount)).ToList();
            var outX = new List<int>(selected.Sum(s => s.Len));
            var outY = new List<int>(selected.Sum(s => s.Len));
            var outLens = new List<int>(selected.Count);
            foreach (var s in selected)
            {
                outLens.Add(s.Len);
                for (int i = 0; i < s.Len; i++)
                {
                    outX.Add(flatX[s.Start + i]);
                    outY.Add(flatY[s.Start + i]);
                }
            }
            return (outX.ToArray(), outY.ToArray(), outLens.ToArray(), outLens.Count);
        }

        private static bool[,] BitmapToBinary(System.Drawing.Bitmap bmp, byte threshold = 1)
        {
            int w = bmp.Width, h = bmp.Height;
            var data = new bool[h, w];
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    var c = bmp.GetPixel(x, y);
                    data[y, x] = ((c.R + c.G + c.B) / 3) >= threshold;
                }
            }
            return data;
        }

        private static bool[,] DownsampleBinary(bool[,] src, int factor)
        {
            if (factor <= 1) return src;
            int h = src.GetLength(0), w = src.GetLength(1);
            int nh = Math.Max(1, h / factor), nw = Math.Max(1, w / factor);
            var dst = new bool[nh, nw];
            for (int y = 0; y < nh; y++)
            {
                int sy = Math.Min(h - 1, y * factor);
                for (int x = 0; x < nw; x++)
                {
                    int sx = Math.Min(w - 1, x * factor);
                    dst[y, x] = src[sy, sx];
                }
            }
            return dst;
        }

        private static bool[,] ResizeBinary(bool[,] src, int maxSide)
        {
            int h = src.GetLength(0), w = src.GetLength(1);
            int maxDim = Math.Max(w, h);
            if (maxSide <= 0 || maxDim <= maxSide) return src;
            double scale = (double)maxSide / maxDim;
            int nw = Math.Max(1, (int)Math.Round(w * scale));
            int nh = Math.Max(1, (int)Math.Round(h * scale));
            var dst = new bool[nh, nw];
            for (int y = 0; y < nh; y++)
            {
                int sy = Math.Min(h - 1, (int)Math.Round(y / scale));
                for (int x = 0; x < nw; x++)
                {
                    int sx = Math.Min(w - 1, (int)Math.Round(x / scale));
                    dst[y, x] = src[sy, sx];
                }
            }
            return dst;
        }

        private static (int X, int Y, int W, int H)? LargestConnectedBoundingBox(bool[,] bin)
        {
            int h = bin.GetLength(0), w = bin.GetLength(1);
            var vis = new bool[h, w];
            (int X, int Y, int W, int H)? best = null;
            int bestCount = 0;
            int[] dx = { 1, -1, 0, 0 };
            int[] dy = { 0, 0, 1, -1 };
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    if (!bin[y, x] || vis[y, x]) continue;
                    int minX = x, maxX = x, minY = y, maxY = y, cnt = 0;
                    var q = new Queue<(int X, int Y)>();
                    q.Enqueue((x, y));
                    vis[y, x] = true;
                    while (q.Count > 0)
                    {
                        var p = q.Dequeue();
                        cnt++;
                        if (p.X < minX) minX = p.X;
                        if (p.X > maxX) maxX = p.X;
                        if (p.Y < minY) minY = p.Y;
                        if (p.Y > maxY) maxY = p.Y;
                        for (int k = 0; k < 4; k++)
                        {
                            int nx = p.X + dx[k], ny = p.Y + dy[k];
                            if (nx < 0 || ny < 0 || nx >= w || ny >= h) continue;
                            if (vis[ny, nx] || !bin[ny, nx]) continue;
                            vis[ny, nx] = true;
                            q.Enqueue((nx, ny));
                        }
                    }
                    if (cnt > bestCount)
                    {
                        bestCount = cnt;
                        best = (minX, minY, maxX - minX + 1, maxY - minY + 1);
                    }
                }
            }
            return best;
        }

        private static bool[,] CropBinary(bool[,] src, int x, int y, int w, int h)
        {
            int sh = src.GetLength(0), sw = src.GetLength(1);
            int cx = Math.Max(0, Math.Min(sw - 1, x));
            int cy = Math.Max(0, Math.Min(sh - 1, y));
            int cw = Math.Max(1, Math.Min(sw - cx, w));
            int ch = Math.Max(1, Math.Min(sh - cy, h));
            var dst = new bool[ch, cw];
            for (int yy = 0; yy < ch; yy++)
                for (int xx = 0; xx < cw; xx++)
                    dst[yy, xx] = src[cy + yy, cx + xx];
            return dst;
        }

        private static List<(int X, int Y)> CollectTruePoints(bool[,] bin)
        {
            int h = bin.GetLength(0), w = bin.GetLength(1);
            var pts = new List<(int X, int Y)>();
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    if (bin[y, x]) pts.Add((x, y));
            return pts;
        }

        private static bool[,] ErodeBinary(bool[,] src, int radius)
        {
            if (radius <= 0) return src;
            int h = src.GetLength(0), w = src.GetLength(1);
            var dst = new bool[h, w];
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    bool ok = true;
                    for (int dy = -radius; dy <= radius && ok; dy++)
                    {
                        for (int dx = -radius; dx <= radius; dx++)
                        {
                            int ny = y + dy, nx = x + dx;
                            if (nx < 0 || ny < 0 || nx >= w || ny >= h || !src[ny, nx]) { ok = false; break; }
                        }
                    }
                    dst[y, x] = ok;
                }
            }
            return dst;
        }

        private static bool[,] DilateBinary(bool[,] src, int radius)
        {
            if (radius <= 0) return src;
            int h = src.GetLength(0), w = src.GetLength(1);
            var dst = new bool[h, w];
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    bool on = false;
                    for (int dy = -radius; dy <= radius && !on; dy++)
                    {
                        for (int dx = -radius; dx <= radius; dx++)
                        {
                            int ny = y + dy, nx = x + dx;
                            if (nx < 0 || ny < 0 || nx >= w || ny >= h) continue;
                            if (src[ny, nx]) { on = true; break; }
                        }
                    }
                    dst[y, x] = on;
                }
            }
            return dst;
        }

        private static bool[,] RemoveSmallComponents(bool[,] src, int minSize)
        {
            if (minSize <= 1) return src;
            int h = src.GetLength(0), w = src.GetLength(1);
            var vis = new bool[h, w];
            var keep = new bool[h, w];
            int[] dx = { 1, -1, 0, 0 };
            int[] dy = { 0, 0, 1, -1 };
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    if (!src[y, x] || vis[y, x]) continue;
                    var comp = new List<(int X, int Y)>();
                    var q = new Queue<(int X, int Y)>();
                    q.Enqueue((x, y));
                    vis[y, x] = true;
                    while (q.Count > 0)
                    {
                        var p = q.Dequeue();
                        comp.Add(p);
                        for (int k = 0; k < 4; k++)
                        {
                            int nx = p.X + dx[k], ny = p.Y + dy[k];
                            if (nx < 0 || ny < 0 || nx >= w || ny >= h) continue;
                            if (vis[ny, nx] || !src[ny, nx]) continue;
                            vis[ny, nx] = true;
                            q.Enqueue((nx, ny));
                        }
                    }
                    if (comp.Count >= minSize)
                        foreach (var p in comp) keep[p.Y, p.X] = true;
                }
            }
            return keep;
        }

        private static Point2D[] GlobalShapeMatchTopN(bool[,] edge, bool[,] template, int topN, double minScore, int step, double areaWeight)
        {
            int h = edge.GetLength(0), w = edge.GetLength(1);
            int th = template.GetLength(0), tw = template.GetLength(1);
            if (tw <= 0 || th <= 0 || tw > w || th > h) return Array.Empty<Point2D>();
            var tPts = CollectTruePoints(template);
            if (tPts.Count == 0) return Array.Empty<Point2D>();
            int tplCount = tPts.Count;
            step = Math.Max(1, step);
            areaWeight = Math.Max(0, areaWeight);

            // 边缘积分图，用于 O(1) 计算窗口内边缘像素密度
            var integral = new int[h + 1, w + 1];
            for (int y = 0; y < h; y++)
            {
                int rowAcc = 0;
                for (int x = 0; x < w; x++)
                {
                    rowAcc += edge[y, x] ? 1 : 0;
                    integral[y + 1, x + 1] = integral[y, x + 1] + rowAcc;
                }
            }

            // 评分改为与 cv2.matchTemplate 的 TM_CCORR_NORMED 等价形式（在二值图上）：
            // baseScore = sum(I*T) / sqrt(sum(I^2)*sum(T^2))
            // 其中 I/T 为 0/1 时，sum(I*T)=hit，sum(I^2)=窗口内边缘像素数，sum(T^2)=模板像素数。
            var cands = new List<(double RankScore, double BaseScore, int Cx, int Cy)>();
            for (int y = 0; y <= h - th; y += step)
            {
                for (int x = 0; x <= w - tw; x += step)
                {
                    int hit = 0;
                    for (int i = 0; i < tPts.Count; i++)
                    {
                        var p = tPts[i];
                        if (edge[y + p.Y, x + p.X]) hit++;
                    }
                    int x2 = x + tw, y2 = y + th;
                    int winEdge = integral[y2, x2] - integral[y, x2] - integral[y2, x] + integral[y, x];
                    if (winEdge <= 0) continue;

                    double denom = Math.Sqrt((double)winEdge * tplCount);
                    if (denom <= 1e-9) continue;
                    double baseScore = hit / denom;
                    if (baseScore < minScore) continue;

                    double localDensity = (double)winEdge / Math.Max(1, tw * th);
                    double rankScore = baseScore * (1.0 + areaWeight * localDensity);
                    cands.Add((rankScore, baseScore, x + tw / 2, y + th / 2));
                }
            }
            if (cands.Count == 0) return Array.Empty<Point2D>();

            var ordered = cands.OrderByDescending(c => c.RankScore).ToList();
            var outPts = new List<Point2D>();
            double minDist2 = Math.Max(tw, th) * Math.Max(tw, th) * 0.16; // 简单 NMS
            foreach (var c in ordered)
            {
                bool overlap = outPts.Any(p =>
                {
                    double dx = p.X - c.Cx, dy = p.Y - c.Cy;
                    return dx * dx + dy * dy < minDist2;
                });
                if (overlap) continue;
                outPts.Add(new Point2D(c.Cx, c.Cy));
                if (outPts.Count >= topN) break;
            }
            return outPts.ToArray();
        }

        private static Point2D[] TemplateToPolyline(bool[,] template, int maxPoints = 512)
        {
            var pts = CollectTruePoints(template);
            if (pts.Count < 8) return Array.Empty<Point2D>();
            double cx = pts.Average(p => p.X);
            double cy = pts.Average(p => p.Y);
            var ordered = pts
                .OrderBy(p => Math.Atan2(p.Y - cy, p.X - cx))
                .Select(p => new Point2D(p.X, p.Y))
                .ToArray();
            if (ordered.Length <= maxPoints) return ordered;
            double stride = (double)ordered.Length / maxPoints;
            var sampled = new Point2D[maxPoints];
            for (int i = 0; i < maxPoints; i++)
                sampled[i] = ordered[(int)Math.Floor(i * stride)];
            return sampled;
        }

        private static ValueTuple<int[], int[], int[], int> BuildMatchedContoursFromTemplate(Point2D[] centers, Point2D[] templatePolyline, int factor)
        {
            if (centers == null || centers.Length == 0 || templatePolyline == null || templatePolyline.Length == 0)
                return (Array.Empty<int>(), Array.Empty<int>(), Array.Empty<int>(), 0);

            double tcx = templatePolyline.Average(p => p.X);
            double tcy = templatePolyline.Average(p => p.Y);
            var lens = new int[centers.Length];
            var outX = new List<int>(centers.Length * templatePolyline.Length);
            var outY = new List<int>(centers.Length * templatePolyline.Length);

            for (int i = 0; i < centers.Length; i++)
            {
                lens[i] = templatePolyline.Length;
                double dx = centers[i].X / factor - tcx;
                double dy = centers[i].Y / factor - tcy;
                for (int k = 0; k < templatePolyline.Length; k++)
                {
                    int x = (int)Math.Round((templatePolyline[k].X + dx) * factor);
                    int y = (int)Math.Round((templatePolyline[k].Y + dy) * factor);
                    outX.Add(x);
                    outY.Add(y);
                }
            }

            return (outX.ToArray(), outY.ToArray(), lens, centers.Length);
        }

        private static bool[,] BuildFusedTemplateFromContours(
            ValueTuple<int[], int[], int[], int> contourData, int templateBars, int canvasSize, double consensus)
        {
            var (flatX, flatY, contourLengths, contourCount) = contourData;
            if (contourCount <= 0 || contourLengths == null || contourLengths.Length == 0)
                return new bool[Math.Max(16, canvasSize), Math.Max(16, canvasSize)];

            templateBars = Math.Max(1, templateBars);
            canvasSize = Math.Max(16, canvasSize);
            consensus = Math.Max(0.01, Math.Min(1.0, consensus));

            var cand = new List<(int Start, int Len, double Area)>();
            int offset = 0;
            for (int ci = 0; ci < contourCount && ci < contourLengths.Length; ci++)
            {
                int len = contourLengths[ci];
                if (len >= 8 && offset + len <= flatX.Length && offset + len <= flatY.Length)
                {
                    var pts = new List<System.Drawing.Point>(len);
                    for (int i = 0; i < len; i++)
                        pts.Add(new System.Drawing.Point(flatX[offset + i], flatY[offset + i]));
                    cand.Add((offset, len, PolygonArea(pts)));
                }
                offset += Math.Max(0, len);
            }
            if (cand.Count == 0) return new bool[canvasSize, canvasSize];

            var selected = cand.OrderByDescending(c => c.Area).Take(templateBars).ToList();
            int globalMinX = int.MaxValue, globalMaxX = int.MinValue, globalMinY = int.MaxValue, globalMaxY = int.MinValue;
            foreach (var s in selected)
            {
                for (int i = 0; i < s.Len; i++)
                {
                    int x = flatX[s.Start + i], y = flatY[s.Start + i];
                    if (x < globalMinX) globalMinX = x;
                    if (x > globalMaxX) globalMaxX = x;
                    if (y < globalMinY) globalMinY = y;
                    if (y > globalMaxY) globalMaxY = y;
                }
            }

            int gw = Math.Max(1, globalMaxX - globalMinX + 1);
            int gh = Math.Max(1, globalMaxY - globalMinY + 1);
            int outW = Math.Max(canvasSize, gw + 4);
            int outH = Math.Max(canvasSize, gh + 4);
            int xPad = Math.Max(2, (outW - gw) / 2);
            int yPad = Math.Max(2, (outH - gh) / 2);

            int[,] acc = new int[outH, outW];
            foreach (var s in selected)
            {
                for (int i = 0; i < s.Len; i++)
                {
                    int x = flatX[s.Start + i], y = flatY[s.Start + i];
                    int nx = (x - globalMinX) + xPad;
                    int ny = (y - globalMinY) + yPad;
                    if (nx >= 0 && ny >= 0 && nx < outW && ny < outH)
                        acc[ny, nx]++;
                }
            }

            var bin = new bool[outH, outW];
            int th = Math.Max(1, (int)Math.Ceiling(selected.Count * consensus));
            for (int y = 0; y < outH; y++)
                for (int x = 0; x < outW; x++)
                    bin[y, x] = acc[y, x] >= th;
            return bin;
        }

        private static Point2D[] BestCyclicAlign(Point2D[] src, Point2D[] reference)
        {
            int n = src.Length;
            double bestErr = double.PositiveInfinity;
            Point2D[] best = src;

            void TryAlign(bool reverse)
            {
                for (int shift = 0; shift < n; shift++)
                {
                    double err = 0;
                    var aligned = new Point2D[n];
                    for (int i = 0; i < n; i++)
                    {
                        int idx = reverse ? (n - 1 - i + shift + n) % n : (i + shift) % n;
                        var p = src[idx];
                        aligned[i] = p;
                        double dx = p.X - reference[i].X;
                        double dy = p.Y - reference[i].Y;
                        err += dx * dx + dy * dy;
                    }
                    if (err < bestErr)
                    {
                        bestErr = err;
                        best = aligned;
                    }
                }
            }

            TryAlign(false);
            TryAlign(true);
            return best;
        }

        private static void DrawLineOnBinary(bool[,] canvas, int x0, int y0, int x1, int y1)
        {
            int w = canvas.GetLength(1), h = canvas.GetLength(0);
            int dx = Math.Abs(x1 - x0), sx = x0 < x1 ? 1 : -1;
            int dy = -Math.Abs(y1 - y0), sy = y0 < y1 ? 1 : -1;
            int err = dx + dy;
            while (true)
            {
                if (x0 >= 0 && y0 >= 0 && x0 < w && y0 < h) canvas[y0, x0] = true;
                if (x0 == x1 && y0 == y1) break;
                int e2 = 2 * err;
                if (e2 >= dy) { err += dy; x0 += sx; }
                if (e2 <= dx) { err += dx; y0 += sy; }
            }
        }

        private static bool[,] BuildMutualTemplateFromContours(
            ValueTuple<int[], int[], int[], int> contourData, int templateBars, int canvasSize, int samplePoints)
        {
            var (flatX, flatY, contourLengths, contourCount) = contourData;
            if (contourCount <= 0 || contourLengths == null || contourLengths.Length == 0)
                return new bool[Math.Max(16, canvasSize), Math.Max(16, canvasSize)];

            templateBars = Math.Max(1, templateBars);
            canvasSize = Math.Max(16, canvasSize);
            samplePoints = Math.Max(24, samplePoints);

            var cand = new List<(int Start, int Len, double Area)>();
            int offset = 0;
            for (int ci = 0; ci < contourCount && ci < contourLengths.Length; ci++)
            {
                int len = contourLengths[ci];
                if (len >= 8 && offset + len <= flatX.Length && offset + len <= flatY.Length)
                {
                    var pts = new List<System.Drawing.Point>(len);
                    for (int i = 0; i < len; i++)
                        pts.Add(new System.Drawing.Point(flatX[offset + i], flatY[offset + i]));
                    cand.Add((offset, len, PolygonArea(pts)));
                }
                offset += Math.Max(0, len);
            }
            if (cand.Count == 0) return new bool[canvasSize, canvasSize];

            var selected = cand.OrderByDescending(c => c.Area).Take(templateBars).ToList();
            var centeredShapes = new List<Point2D[]>();
            var centroids = new List<Point2D>();
            foreach (var s in selected)
            {
                var pts = new Point2D[s.Len];
                for (int i = 0; i < s.Len; i++)
                    pts[i] = new Point2D(flatX[s.Start + i], flatY[s.Start + i]);
                var resampled = ResampleContourByArc(pts, samplePoints);
                if (resampled.Length == 0) continue;
                double cx = resampled.Average(p => p.X);
                double cy = resampled.Average(p => p.Y);
                centroids.Add(new Point2D(cx, cy));
                centeredShapes.Add(resampled.Select(p => new Point2D(p.X - cx, p.Y - cy)).ToArray());
            }
            if (centeredShapes.Count == 0) return new bool[canvasSize, canvasSize];

            var reference = centeredShapes[0];
            var aligned = new List<Point2D[]> { reference };
            for (int i = 1; i < centeredShapes.Count; i++)
                aligned.Add(BestCyclicAlign(centeredShapes[i], reference));

            var avgCentered = new Point2D[samplePoints];
            for (int i = 0; i < samplePoints; i++)
            {
                double sx = 0, sy = 0;
                for (int k = 0; k < aligned.Count; k++)
                {
                    sx += aligned[k][i].X;
                    sy += aligned[k][i].Y;
                }
                avgCentered[i] = new Point2D(sx / aligned.Count, sy / aligned.Count);
            }

            double meanCx = centroids.Average(c => c.X);
            double meanCy = centroids.Average(c => c.Y);
            var avg = avgCentered.Select(p => new Point2D(p.X + meanCx, p.Y + meanCy)).ToArray();

            double minX = avg.Min(p => p.X), maxX = avg.Max(p => p.X);
            double minY = avg.Min(p => p.Y), maxY = avg.Max(p => p.Y);
            int w = Math.Max(1, (int)Math.Ceiling(maxX - minX + 1));
            int h = Math.Max(1, (int)Math.Ceiling(maxY - minY + 1));
            int outW = Math.Max(canvasSize, w + 4);
            int outH = Math.Max(canvasSize, h + 4);
            int xPad = Math.Max(2, (outW - w) / 2);
            int yPad = Math.Max(2, (outH - h) / 2);

            var canvas = new bool[outH, outW];
            var pix = new (int X, int Y)[samplePoints];
            for (int i = 0; i < samplePoints; i++)
            {
                int x = (int)Math.Round(avg[i].X - minX) + xPad;
                int y = (int)Math.Round(avg[i].Y - minY) + yPad;
                pix[i] = (x, y);
            }
            for (int i = 0; i < samplePoints; i++)
            {
                var a = pix[i];
                var b = pix[(i + 1) % samplePoints];
                DrawLineOnBinary(canvas, a.X, a.Y, b.X, b.Y);
            }
            return canvas;
        }

        private static bool[,] ExtractCenterline(bool[,] bin)
        {
            int h = bin.GetLength(0), w = bin.GetLength(1);
            var outBin = new bool[h, w];

            // 行方向：取每行最左与最右的中点
            for (int y = 0; y < h; y++)
            {
                int left = -1, right = -1;
                for (int x = 0; x < w; x++)
                {
                    if (!bin[y, x]) continue;
                    if (left < 0) left = x;
                    right = x;
                }
                if (left >= 0 && right >= left)
                {
                    int cx = (left + right) / 2;
                    outBin[y, cx] = true;
                }
            }

            // 列方向：取每列最上与最下的中点（补全断裂）
            for (int x = 0; x < w; x++)
            {
                int top = -1, bottom = -1;
                for (int y = 0; y < h; y++)
                {
                    if (!bin[y, x]) continue;
                    if (top < 0) top = y;
                    bottom = y;
                }
                if (top >= 0 && bottom >= top)
                {
                    int cy = (top + bottom) / 2;
                    outBin[cy, x] = true;
                }
            }

            return outBin;
        }

        private static CalibImage BinaryToCalibImage(bool[,] bin)
        {
            int h = bin.GetLength(0), w = bin.GetLength(1);
            var img = new CalibImage(w, h, 1);
            var native = img.GetNativeStruct();
            var bytes = new byte[w * h];
            int idx = 0;
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                    bytes[idx++] = bin[y, x] ? (byte)255 : (byte)0;
            }
            Marshal.Copy(bytes, 0, native.data, bytes.Length);
            return img;
        }

        private static CalibImage SobelEdgeImage(CalibImage src, int threshold)
        {
            using var bmp = src.ToBitmap();
            if (bmp == null) throw new InvalidOperationException("Sobel: 输入图像转换失败");
            int w = bmp.Width, h = bmp.Height;
            var gray = new byte[w * h];
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    var c = bmp.GetPixel(x, y);
                    gray[y * w + x] = (byte)((c.R * 30 + c.G * 59 + c.B * 11) / 100);
                }
            }

            threshold = Math.Max(0, Math.Min(255, threshold));
            var outBytes = new byte[w * h];
            for (int y = 1; y < h - 1; y++)
            {
                for (int x = 1; x < w - 1; x++)
                {
                    int p00 = gray[(y - 1) * w + (x - 1)], p01 = gray[(y - 1) * w + x], p02 = gray[(y - 1) * w + (x + 1)];
                    int p10 = gray[y * w + (x - 1)],     p12 = gray[y * w + (x + 1)];
                    int p20 = gray[(y + 1) * w + (x - 1)], p21 = gray[(y + 1) * w + x], p22 = gray[(y + 1) * w + (x + 1)];

                    int gx = -p00 + p02 - 2 * p10 + 2 * p12 - p20 + p22;
                    int gy = -p00 - 2 * p01 - p02 + p20 + 2 * p21 + p22;
                    int mag = (int)Math.Sqrt(gx * gx + gy * gy);
                    outBytes[y * w + x] = mag >= threshold ? (byte)255 : (byte)0;
                }
            }

            var edge = new CalibImage(w, h, 1);
            var native = edge.GetNativeStruct();
            Marshal.Copy(outBytes, 0, native.data, outBytes.Length);
            return edge;
        }

        /// <summary>
        /// OpenCV 风格 Scharr 3×3，梯度幅值二值化输出。
        /// Gx: [-3,0,3; -10,0,10; -3,0,3], Gy: [-3,-10,-3; 0,0,0; 3,10,3]
        /// </summary>
        private static CalibImage ScharrEdgeImage(CalibImage src, int threshold)
        {
            using var bmp = src.ToBitmap();
            if (bmp == null) throw new InvalidOperationException("Scharr: 输入图像转换失败");
            int w = bmp.Width, h = bmp.Height;
            var gray = new byte[w * h];
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    var c = bmp.GetPixel(x, y);
                    gray[y * w + x] = (byte)((c.R * 30 + c.G * 59 + c.B * 11) / 100);
                }
            }

            threshold = Math.Max(0, Math.Min(255, threshold));
            var outBytes = new byte[w * h];
            for (int y = 1; y < h - 1; y++)
            {
                for (int x = 1; x < w - 1; x++)
                {
                    int p00 = gray[(y - 1) * w + (x - 1)], p01 = gray[(y - 1) * w + x], p02 = gray[(y - 1) * w + (x + 1)];
                    int p10 = gray[y * w + (x - 1)], p12 = gray[y * w + (x + 1)];
                    int p20 = gray[(y + 1) * w + (x - 1)], p21 = gray[(y + 1) * w + x], p22 = gray[(y + 1) * w + (x + 1)];

                    int gx = -3 * p00 + 3 * p02 - 10 * p10 + 10 * p12 - 3 * p20 + 3 * p22;
                    int gy = -3 * p00 - 10 * p01 - 3 * p02 + 3 * p20 + 10 * p21 + 3 * p22;
                    int mag = (int)Math.Sqrt(gx * gx + gy * gy);
                    outBytes[y * w + x] = mag >= threshold ? (byte)255 : (byte)0;
                }
            }

            var edge = new CalibImage(w, h, 1);
            var native = edge.GetNativeStruct();
            Marshal.Copy(outBytes, 0, native.data, outBytes.Length);
            return edge;
        }

        private static int NextPow2(int v)
        {
            v = Math.Max(1, v);
            int p = 1;
            while (p < v) p <<= 1;
            return p;
        }

        private static void FFT1D(Complex[] a, bool inverse)
        {
            int n = a.Length;
            for (int i = 1, j = 0; i < n; i++)
            {
                int bit = n >> 1;
                for (; (j & bit) != 0; bit >>= 1) j ^= bit;
                j ^= bit;
                if (i < j) (a[i], a[j]) = (a[j], a[i]);
            }
            for (int len = 2; len <= n; len <<= 1)
            {
                double ang = 2 * Math.PI / len * (inverse ? 1 : -1);
                Complex wlen = new Complex(Math.Cos(ang), Math.Sin(ang));
                for (int i = 0; i < n; i += len)
                {
                    Complex w = Complex.One;
                    int half = len >> 1;
                    for (int j = 0; j < half; j++)
                    {
                        Complex u = a[i + j];
                        Complex v = a[i + j + half] * w;
                        a[i + j] = u + v;
                        a[i + j + half] = u - v;
                        w *= wlen;
                    }
                }
            }
            if (inverse)
            {
                for (int i = 0; i < n; i++) a[i] /= n;
            }
        }

        private static void FFT2D(Complex[,] data, bool inverse)
        {
            int h = data.GetLength(0), w = data.GetLength(1);
            var row = new Complex[w];
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++) row[x] = data[y, x];
                FFT1D(row, inverse);
                for (int x = 0; x < w; x++) data[y, x] = row[x];
            }

            var col = new Complex[h];
            for (int x = 0; x < w; x++)
            {
                for (int y = 0; y < h; y++) col[y] = data[y, x];
                FFT1D(col, inverse);
                for (int y = 0; y < h; y++) data[y, x] = col[y];
            }
        }

        /// <summary>
        /// 频域相位检测器（纯 C# FFT）：
        /// 2D FFT -> Log-Gabor 带通 -> 相位归一化(phase-only) -> iFFT -> 阈值边缘
        /// </summary>
        private static (CalibImage Response, CalibImage Edge) PhaseCongruencyEdgeImage(CalibImage src, double threshold, double noiseSigma, int blurKsize, string? debugDumpPrefix = null)
        {
            using var bmp = src.ToBitmap();
            if (bmp == null) throw new InvalidOperationException("PhaseCongruency: 输入图像转换失败");
            int w = bmp.Width, h = bmp.Height;
            var gray = new byte[w * h];
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    var c = bmp.GetPixel(x, y);
                    gray[y * w + x] = (byte)((c.R * 30 + c.G * 59 + c.B * 11) / 100);
                }
            }

            blurKsize = Math.Max(1, blurKsize);
            if ((blurKsize & 1) == 0) blurKsize += 1;
            threshold = Math.Max(0.0, Math.Min(1.0, threshold));
            noiseSigma = Math.Max(0.0, Math.Min(1.0, noiseSigma));

            var srcF = new float[w * h];
            for (int i = 0; i < gray.Length; i++) srcF[i] = gray[i] / 255f;
            if (blurKsize > 1)
            {
                int[] k = blurKsize <= 3 ? new[] { 1, 2, 1 } : new[] { 1, 4, 6, 4, 1 };
                int kr = k.Length / 2;
                float sum = k.Sum();
                var tmp = new float[w * h];
                for (int y = 0; y < h; y++)
                {
                    for (int x = 0; x < w; x++)
                    {
                        float acc = 0;
                        for (int i = -kr; i <= kr; i++)
                        {
                            int nx = Math.Max(0, Math.Min(w - 1, x + i));
                            acc += srcF[y * w + nx] * k[i + kr];
                        }
                        tmp[y * w + x] = acc / sum;
                    }
                }
                for (int y = 0; y < h; y++)
                {
                    for (int x = 0; x < w; x++)
                    {
                        float acc = 0;
                        for (int i = -kr; i <= kr; i++)
                        {
                            int ny = Math.Max(0, Math.Min(h - 1, y + i));
                            acc += tmp[ny * w + x] * k[i + kr];
                        }
                        srcF[y * w + x] = acc / sum;
                    }
                }
            }

            // 控制频域计算规模（保持实时可用）
            int maxDim = 512;
            int dw = w, dh = h;
            int ds = 1;
            while (Math.Max(dw, dh) > maxDim)
            {
                ds <<= 1;
                dw = Math.Max(1, w / ds);
                dh = Math.Max(1, h / ds);
            }

            var small = new float[dw * dh];
            for (int y = 0; y < dh; y++)
                for (int x = 0; x < dw; x++)
                    small[y * dw + x] = srcF[Math.Min(h - 1, y * ds) * w + Math.Min(w - 1, x * ds)];

            int fw = NextPow2(dw);
            int fh = NextPow2(dh);
            var spec = new Complex[fh, fw];
            for (int y = 0; y < dh; y++)
                for (int x = 0; x < dw; x++)
                    spec[y, x] = new Complex(small[y * dw + x], 0);
            FFT2D(spec, inverse: false);

            // Log-Gabor 带通（频域），centerFreq 与阈值联动：阈值越低频带越宽
            double centerFreq = 0.22;
            double sigmaOnf = 0.55 + 0.25 * (1.0 - threshold);
            double logSigma = Math.Log(Math.Max(1.05, sigmaOnf));
            if (Math.Abs(logSigma) < 1e-9) logSigma = 0.2;

            var outBytes = new byte[w * h];
            var respBytes = new byte[w * h];
            const double eps = 1e-9;
            var bandSpec = new Complex[fh, fw];
            var phaseSpec = new Complex[fh, fw];
            for (int y = 0; y < fh; y++)
            {
                double fy = (y <= fh / 2 ? y : y - fh) / (double)fh;
                for (int x = 0; x < fw; x++)
                {
                    double fx = (x <= fw / 2 ? x : x - fw) / (double)fw;
                    double r = Math.Sqrt(fx * fx + fy * fy);
                    if (r < 1e-9)
                    {
                        bandSpec[y, x] = Complex.Zero;
                        phaseSpec[y, x] = Complex.Zero;
                        continue;
                    }

                    double lr = Math.Log(r / centerFreq);
                    double lg = Math.Exp(-(lr * lr) / (2.0 * logSigma * logSigma));
                    Complex f = spec[y, x];
                    double mag = f.Magnitude + eps;
                    bandSpec[y, x] = lg * f;           // 保留幅值（结构更接近原图）
                    phaseSpec[y, x] = (lg / mag) * f;  // phase-only（边缘增强）
                }
            }

            FFT2D(bandSpec, inverse: true);
            FFT2D(phaseSpec, inverse: true);

            // 统计带通响应和相位响应范围（小图）
            double minBand = double.MaxValue, maxBand = double.MinValue;
            double minPhase = double.MaxValue, maxPhase = double.MinValue;
            for (int y = 0; y < dh; y++)
            {
                for (int x = 0; x < dw; x++)
                {
                    double vb = bandSpec[y, x].Magnitude;
                    double vp = phaseSpec[y, x].Magnitude;
                    if (vb < minBand) minBand = vb;
                    if (vb > maxBand) maxBand = vb;
                    if (vp < minPhase) minPhase = vp;
                    if (vp > maxPhase) maxPhase = vp;
                }
            }
            double bandRange = Math.Max(1e-9, maxBand - minBand);
            double phaseRange = Math.Max(1e-9, maxPhase - minPhase);
            double th = Math.Max(0.0, Math.Min(1.0, threshold));
            double damp = Math.Max(0.0, Math.Min(1.0, noiseSigma));

            var response01 = new float[w * h];
            for (int y = 0; y < h; y++)
            {
                int sy = Math.Min(dh - 1, y / ds);
                for (int x = 0; x < w; x++)
                {
                    int sx = Math.Min(dw - 1, x / ds);
                    double vb = (bandSpec[sy, sx].Magnitude - minBand) / bandRange;
                    double vp = (phaseSpec[sy, sx].Magnitude - minPhase) / phaseRange;
                    // 响应图以保幅结构为主，叠加少量 phase-only 增强边缘
                    double v = 0.78 * vb + 0.22 * vp;
                    v *= (1.0 - 0.45 * damp);
                    response01[y * w + x] = (float)v;
                    int rv = (int)Math.Round(Math.Max(0.0, Math.Min(1.0, v)) * 255.0);
                    respBytes[y * w + x] = (byte)rv;
                }
            }

            // Edge: 基于响应图做 Canny 风格后处理（梯度 -> NMS -> 双阈值连通）
            var gxArr = new float[w * h];
            var gyArr = new float[w * h];
            var gradMag = new float[w * h];
            for (int y = 1; y < h - 1; y++)
            {
                int y0 = (y - 1) * w, y1 = y * w, y2 = (y + 1) * w;
                for (int x = 1; x < w - 1; x++)
                {
                    float p00 = response01[y0 + x - 1], p01 = response01[y0 + x], p02 = response01[y0 + x + 1];
                    float p10 = response01[y1 + x - 1],                          p12 = response01[y1 + x + 1];
                    float p20 = response01[y2 + x - 1], p21 = response01[y2 + x], p22 = response01[y2 + x + 1];
                    float gx = -p00 + p02 - 2f * p10 + 2f * p12 - p20 + p22;
                    float gy = -p00 - 2f * p01 - p02 + p20 + 2f * p21 + p22;
                    gxArr[y1 + x] = gx;
                    gyArr[y1 + x] = gy;
                    gradMag[y1 + x] = (float)Math.Sqrt(gx * gx + gy * gy);
                }
            }

            var nms = new float[w * h];
            var nmsVals = new List<float>(Math.Max(1, (w - 2) * (h - 2)));
            for (int y = 1; y < h - 1; y++)
            {
                int row = y * w;
                for (int x = 1; x < w - 1; x++)
                {
                    int idx = row + x;
                    float gm = gradMag[idx];
                    if (gm <= 1e-9f) continue;

                    float ang = (float)(Math.Atan2(gyArr[idx], gxArr[idx]) * 180.0 / Math.PI);
                    if (ang < 0f) ang += 180f;

                    float n1, n2;
                    if ((ang >= 0f && ang < 22.5f) || (ang >= 157.5f && ang <= 180f))
                    {
                        n1 = gradMag[idx - 1];
                        n2 = gradMag[idx + 1];
                    }
                    else if (ang >= 22.5f && ang < 67.5f)
                    {
                        n1 = gradMag[idx - w + 1];
                        n2 = gradMag[idx + w - 1];
                    }
                    else if (ang >= 67.5f && ang < 112.5f)
                    {
                        n1 = gradMag[idx - w];
                        n2 = gradMag[idx + w];
                    }
                    else
                    {
                        n1 = gradMag[idx - w - 1];
                        n2 = gradMag[idx + w + 1];
                    }

                    if (gm >= n1 && gm >= n2)
                    {
                        nms[idx] = gm;
                        nmsVals.Add(gm);
                    }
                }
            }

            nmsVals.Sort();
            float q70 = nmsVals.Count > 0 ? nmsVals[(int)(0.70 * (nmsVals.Count - 1))] : 0f;
            float q92 = nmsVals.Count > 0 ? nmsVals[(int)(0.92 * (nmsVals.Count - 1))] : 0f;
            float tBlend = (float)Math.Max(0.0, Math.Min(1.0, th));
            float high = q70 + (q92 - q70) * tBlend;
            high *= (float)(1.0 - 0.25 * damp);
            float low = high * (0.38f + 0.15f * (float)damp);

            var marks = new byte[w * h]; // 0=none,1=weak,2=strong
            var q = new Queue<int>();
            for (int y = 1; y < h - 1; y++)
            {
                int row = y * w;
                for (int x = 1; x < w - 1; x++)
                {
                    int idx = row + x;
                    float v = nms[idx];
                    if (v >= high)
                    {
                        marks[idx] = 2;
                        q.Enqueue(idx);
                    }
                    else if (v >= low)
                    {
                        marks[idx] = 1;
                    }
                }
            }

            // hysteresis: 保留与强边连通的弱边
            while (q.Count > 0)
            {
                int idx = q.Dequeue();
                int cy = idx / w;
                int cx = idx - cy * w;
                for (int dy = -1; dy <= 1; dy++)
                {
                    int ny = cy + dy;
                    if (ny <= 0 || ny >= h - 1) continue;
                    int nrow = ny * w;
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        if (dx == 0 && dy == 0) continue;
                        int nx = cx + dx;
                        if (nx <= 0 || nx >= w - 1) continue;
                        int ni = nrow + nx;
                        if (marks[ni] == 1)
                        {
                            marks[ni] = 2;
                            q.Enqueue(ni);
                        }
                    }
                }
            }

            for (int i = 0; i < outBytes.Length; i++)
                outBytes[i] = marks[i] == 2 ? (byte)255 : (byte)0;

            if (!string.IsNullOrWhiteSpace(debugDumpPrefix))
            {
                try
                {
                    string prefix = debugDumpPrefix.Trim();
                    if (!System.IO.Path.IsPathRooted(prefix))
                        prefix = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, prefix);
                    string? dir = System.IO.Path.GetDirectoryName(prefix);
                    if (!string.IsNullOrWhiteSpace(dir))
                        System.IO.Directory.CreateDirectory(dir);

                    // 响应图（0~1 -> 0~255）
                    using var respBmp = new System.Drawing.Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
                    for (int y = 0; y < h; y++)
                    {
                        for (int x = 0; x < w; x++)
                        {
                            int v = (int)Math.Round(Math.Max(0.0, Math.Min(1.0, response01[y * w + x])) * 255.0);
                            var c = System.Drawing.Color.FromArgb(v, v, v);
                            respBmp.SetPixel(x, y, c);
                        }
                    }
                    respBmp.Save(prefix + "_response.bmp", System.Drawing.Imaging.ImageFormat.Bmp);

                    // 二值图
                    using var binBmp = new System.Drawing.Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
                    for (int y = 0; y < h; y++)
                    {
                        for (int x = 0; x < w; x++)
                        {
                            byte b = outBytes[y * w + x];
                            var c = System.Drawing.Color.FromArgb(b, b, b);
                            binBmp.SetPixel(x, y, c);
                        }
                    }
                    binBmp.Save(prefix + "_binary.bmp", System.Drawing.Imaging.ImageFormat.Bmp);
                }
                catch
                {
                    // 调试输出失败不影响主流程
                }
            }

            var resp = new CalibImage(w, h, 1);
            var respNative = resp.GetNativeStruct();
            Marshal.Copy(respBytes, 0, respNative.data, respBytes.Length);

            var edge = new CalibImage(w, h, 1);
            var native = edge.GetNativeStruct();
            Marshal.Copy(outBytes, 0, native.data, outBytes.Length);
            return (resp, edge);
        }

        private static (CalibImage Filtered, CalibImage Binary) FrequencyFilterToBinaryImage(
            CalibImage src, string mode, double lowCut, double highCut, double threshold, bool useOtsu)
        {
            using var bmp = src.ToBitmap();
            if (bmp == null) throw new InvalidOperationException("频域滤波二值化: 输入图像转换失败");
            int w = bmp.Width, h = bmp.Height;
            var gray = new byte[w * h];
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    var c = bmp.GetPixel(x, y);
                    gray[y * w + x] = (byte)((c.R * 30 + c.G * 59 + c.B * 11) / 100);
                }
            }

            mode = (mode ?? "bandpass").Trim().ToLowerInvariant();
            if (mode != "lowpass" && mode != "highpass" && mode != "bandpass") mode = "bandpass";
            lowCut = Math.Max(0.0, Math.Min(0.5, lowCut));
            highCut = Math.Max(0.0, Math.Min(0.5, highCut));
            if (highCut < lowCut)
            {
                double t = lowCut;
                lowCut = highCut;
                highCut = t;
            }
            threshold = Math.Max(0.0, Math.Min(1.0, threshold));

            int fw = NextPow2(w);
            int fh = NextPow2(h);
            var spec = new Complex[fh, fw];
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                    spec[y, x] = new Complex(gray[y * w + x] / 255.0, 0);
            }
            FFT2D(spec, inverse: false);

            for (int y = 0; y < fh; y++)
            {
                double fy = (y <= fh / 2 ? y : y - fh) / (double)fh;
                for (int x = 0; x < fw; x++)
                {
                    double fx = (x <= fw / 2 ? x : x - fw) / (double)fw;
                    double r = Math.Sqrt(fx * fx + fy * fy);
                    bool keep = mode switch
                    {
                        "lowpass" => r <= highCut,
                        "highpass" => r >= lowCut,
                        _ => r >= lowCut && r <= highCut
                    };
                    if (!keep) spec[y, x] = Complex.Zero;
                }
            }

            FFT2D(spec, inverse: true);
            var filtered = new float[w * h];
            double vmin = double.MaxValue, vmax = double.MinValue;
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    double v = spec[y, x].Magnitude;
                    filtered[y * w + x] = (float)v;
                    if (v < vmin) vmin = v;
                    if (v > vmax) vmax = v;
                }
            }
            double vr = Math.Max(1e-9, vmax - vmin);
            var filteredBytes = new byte[w * h];
            for (int i = 0; i < filtered.Length; i++)
                filteredBytes[i] = (byte)Math.Max(0, Math.Min(255, (int)Math.Round((filtered[i] - vmin) / vr * 255.0)));

            int binTh;
            if (useOtsu)
            {
                int[] hist = new int[256];
                for (int i = 0; i < filteredBytes.Length; i++) hist[filteredBytes[i]]++;
                int total = filteredBytes.Length;
                double sum = 0;
                for (int i = 0; i < 256; i++) sum += i * hist[i];
                double sumB = 0;
                int wB = 0;
                double maxVar = -1;
                int best = 127;
                for (int t = 0; t < 256; t++)
                {
                    wB += hist[t];
                    if (wB == 0) continue;
                    int wF = total - wB;
                    if (wF == 0) break;
                    sumB += t * hist[t];
                    double mB = sumB / wB;
                    double mF = (sum - sumB) / wF;
                    double between = wB * (double)wF * (mB - mF) * (mB - mF);
                    if (between > maxVar)
                    {
                        maxVar = between;
                        best = t;
                    }
                }
                binTh = best;
            }
            else
            {
                binTh = (int)Math.Round(threshold * 255.0);
            }

            var binBytes = new byte[w * h];
            for (int i = 0; i < binBytes.Length; i++)
                binBytes[i] = filteredBytes[i] >= binTh ? (byte)255 : (byte)0;

            var filteredImg = new CalibImage(w, h, 1);
            var fNative = filteredImg.GetNativeStruct();
            Marshal.Copy(filteredBytes, 0, fNative.data, filteredBytes.Length);

            var binaryImg = new CalibImage(w, h, 1);
            var bNative = binaryImg.GetNativeStruct();
            Marshal.Copy(binBytes, 0, bNative.data, binBytes.Length);
            return (filteredImg, binaryImg);
        }

        private static (CalibImage Filtered, CalibImage Binary) LocalFreqSauvolaNiblackImage(
            CalibImage src, string method, int windowSize, double k, double r, double lowCut, double highCut)
        {
            using var bmp = src.ToBitmap();
            if (bmp == null) throw new InvalidOperationException("局部频域阈值: 输入图像转换失败");
            int w = bmp.Width, h = bmp.Height;
            var gray = new double[w * h];
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    var c = bmp.GetPixel(x, y);
                    gray[y * w + x] = ((c.R * 30 + c.G * 59 + c.B * 11) / 100.0) / 255.0;
                }
            }

            method = (method ?? "sauvola").Trim().ToLowerInvariant();
            if (method != "sauvola" && method != "niblack") method = "sauvola";
            windowSize = Math.Max(3, windowSize);
            if ((windowSize & 1) == 0) windowSize += 1;
            int wr = windowSize / 2;
            r = Math.Max(1e-6, Math.Min(1.0, r));
            lowCut = Math.Max(0.0, Math.Min(0.5, lowCut));
            highCut = Math.Max(0.0, Math.Min(0.5, highCut));
            if (highCut < lowCut)
            {
                double tmp = lowCut;
                lowCut = highCut;
                highCut = tmp;
            }

            // 频域带通
            int fw = NextPow2(w);
            int fh = NextPow2(h);
            var spec = new Complex[fh, fw];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    spec[y, x] = new Complex(gray[y * w + x], 0);
            FFT2D(spec, inverse: false);

            for (int y = 0; y < fh; y++)
            {
                double fy = (y <= fh / 2 ? y : y - fh) / (double)fh;
                for (int x = 0; x < fw; x++)
                {
                    double fx = (x <= fw / 2 ? x : x - fw) / (double)fw;
                    double fr = Math.Sqrt(fx * fx + fy * fy);
                    if (!(fr >= lowCut && fr <= highCut))
                        spec[y, x] = Complex.Zero;
                }
            }

            FFT2D(spec, inverse: true);
            var filtered01 = new double[w * h];
            double fmin = double.MaxValue, fmax = double.MinValue;
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    double v = spec[y, x].Magnitude;
                    filtered01[y * w + x] = v;
                    if (v < fmin) fmin = v;
                    if (v > fmax) fmax = v;
                }
            }
            double frange = Math.Max(1e-9, fmax - fmin);
            var filteredBytes = new byte[w * h];
            for (int i = 0; i < filtered01.Length; i++)
            {
                filtered01[i] = (filtered01[i] - fmin) / frange;
                filteredBytes[i] = (byte)Math.Max(0, Math.Min(255, (int)Math.Round(filtered01[i] * 255.0)));
            }

            // 积分图用于局部均值/方差
            int iw = w + 1, ih = h + 1;
            var integ = new double[iw * ih];
            var integ2 = new double[iw * ih];
            for (int y = 1; y <= h; y++)
            {
                double row = 0, row2 = 0;
                int srcRow = (y - 1) * w;
                int iiRow = y * iw;
                int iiPrev = (y - 1) * iw;
                for (int x = 1; x <= w; x++)
                {
                    double v = filtered01[srcRow + (x - 1)];
                    row += v;
                    row2 += v * v;
                    integ[iiRow + x] = integ[iiPrev + x] + row;
                    integ2[iiRow + x] = integ2[iiPrev + x] + row2;
                }
            }

            static double RectSum(double[] itg, int stride, int x0, int y0, int x1, int y1)
            {
                return itg[y1 * stride + x1] - itg[y0 * stride + x1] - itg[y1 * stride + x0] + itg[y0 * stride + x0];
            }

            var bin = new byte[w * h];
            for (int y = 0; y < h; y++)
            {
                int y0 = Math.Max(0, y - wr);
                int y1 = Math.Min(h - 1, y + wr);
                for (int x = 0; x < w; x++)
                {
                    int x0 = Math.Max(0, x - wr);
                    int x1 = Math.Min(w - 1, x + wr);
                    int ax0 = x0, ay0 = y0, ax1 = x1 + 1, ay1 = y1 + 1;
                    int area = (x1 - x0 + 1) * (y1 - y0 + 1);
                    double sum = RectSum(integ, iw, ax0, ay0, ax1, ay1);
                    double sum2 = RectSum(integ2, iw, ax0, ay0, ax1, ay1);
                    double mean = sum / Math.Max(1, area);
                    double var = Math.Max(0.0, sum2 / Math.Max(1, area) - mean * mean);
                    double std = Math.Sqrt(var);
                    double t = method == "niblack"
                        ? mean + k * std
                        : mean * (1.0 + k * ((std / r) - 1.0));
                    bin[y * w + x] = filtered01[y * w + x] >= t ? (byte)255 : (byte)0;
                }
            }

            var filteredImg = new CalibImage(w, h, 1);
            var fNative = filteredImg.GetNativeStruct();
            Marshal.Copy(filteredBytes, 0, fNative.data, filteredBytes.Length);

            var binaryImg = new CalibImage(w, h, 1);
            var bNative = binaryImg.GetNativeStruct();
            Marshal.Copy(bin, 0, bNative.data, bin.Length);
            return (filteredImg, binaryImg);
        }

        private static (int WhitePixels, double WhiteRatio) AnalyzeBinaryImage(CalibImage img)
        {
            var native = img.GetNativeStruct();
            if (native.data == IntPtr.Zero || native.width <= 0 || native.height <= 0)
                return (0, 0);
            int len = native.width * native.height;
            var bytes = new byte[len];
            Marshal.Copy(native.data, bytes, 0, len);
            int white = 0;
            for (int i = 0; i < len; i++) if (bytes[i] > 0) white++;
            return (white, len > 0 ? (double)white / len : 0);
        }

        private static CalibImage PreFilterImage(CalibImage src, string mode, int ksize)
        {
            using var bmp = src.ToBitmap();
            if (bmp == null) throw new InvalidOperationException("预滤波: 输入图像转换失败");
            int w = bmp.Width, h = bmp.Height;
            var gray = new byte[w * h];
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    var c = bmp.GetPixel(x, y);
                    gray[y * w + x] = (byte)((c.R * 30 + c.G * 59 + c.B * 11) / 100);
                }
            }

            ksize = Math.Max(3, ksize);
            if ((ksize & 1) == 0) ksize += 1;
            int r = ksize / 2;
            var outBytes = new byte[w * h];
            mode = (mode ?? "gaussian").Trim().ToLowerInvariant();

            if (mode == "median")
            {
                var win = new List<byte>(ksize * ksize);
                for (int y = 0; y < h; y++)
                {
                    for (int x = 0; x < w; x++)
                    {
                        win.Clear();
                        for (int dy = -r; dy <= r; dy++)
                        {
                            int ny = Math.Max(0, Math.Min(h - 1, y + dy));
                            for (int dx = -r; dx <= r; dx++)
                            {
                                int nx = Math.Max(0, Math.Min(w - 1, x + dx));
                                win.Add(gray[ny * w + nx]);
                            }
                        }
                        win.Sort();
                        outBytes[y * w + x] = win[win.Count / 2];
                    }
                }
            }
            else
            {
                // 简单可分离高斯近似：binomial 权重
                int[] k = ksize switch
                {
                    3 => new[] { 1, 2, 1 },
                    5 => new[] { 1, 4, 6, 4, 1 },
                    _ => new[] { 1, 4, 6, 4, 1 }
                };
                int kr = k.Length / 2;
                int sum = k.Sum();
                var tmp = new int[w * h];
                for (int y = 0; y < h; y++)
                {
                    for (int x = 0; x < w; x++)
                    {
                        int acc = 0;
                        for (int i = -kr; i <= kr; i++)
                        {
                            int nx = Math.Max(0, Math.Min(w - 1, x + i));
                            acc += gray[y * w + nx] * k[i + kr];
                        }
                        tmp[y * w + x] = acc / sum;
                    }
                }
                for (int y = 0; y < h; y++)
                {
                    for (int x = 0; x < w; x++)
                    {
                        int acc = 0;
                        for (int i = -kr; i <= kr; i++)
                        {
                            int ny = Math.Max(0, Math.Min(h - 1, y + i));
                            acc += tmp[ny * w + x] * k[i + kr];
                        }
                        outBytes[y * w + x] = (byte)Math.Max(0, Math.Min(255, acc / sum));
                    }
                }
            }

            var output = new CalibImage(w, h, 1);
            var native = output.GetNativeStruct();
            Marshal.Copy(outBytes, 0, native.data, outBytes.Length);
            return output;
        }

        private static CalibImage NLMeansDenoiseImage(CalibImage src, double hStrength, int searchWindow, int templateWindow)
        {
            using var bmp = src.ToBitmap();
            if (bmp == null) throw new InvalidOperationException("NLMeans: 输入图像转换失败");
            int w = bmp.Width, h = bmp.Height;
            var gray = new byte[w * h];
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    var c = bmp.GetPixel(x, y);
                    gray[y * w + x] = (byte)((c.R * 30 + c.G * 59 + c.B * 11) / 100);
                }
            }

            searchWindow = Math.Max(3, searchWindow);
            if ((searchWindow & 1) == 0) searchWindow += 1;
            templateWindow = Math.Max(3, templateWindow);
            if ((templateWindow & 1) == 0) templateWindow += 1;
            // 控制计算复杂度，避免 UI 卡死
            searchWindow = Math.Min(17, searchWindow);
            templateWindow = Math.Min(5, templateWindow);

            int sr = searchWindow / 2;
            int tr = templateWindow / 2;
            int patchArea = templateWindow * templateWindow;
            double hh = Math.Max(1e-6, hStrength * hStrength * patchArea);

            var outBytes = new byte[w * h];
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    double wSum = 0.0;
                    double vSum = 0.0;

                    for (int dy = -sr; dy <= sr; dy++)
                    {
                        int ny = Math.Max(0, Math.Min(h - 1, y + dy));
                        for (int dx = -sr; dx <= sr; dx++)
                        {
                            int nx = Math.Max(0, Math.Min(w - 1, x + dx));
                            double dist2 = 0.0;
                            for (int py = -tr; py <= tr; py++)
                            {
                                int y1 = Math.Max(0, Math.Min(h - 1, y + py));
                                int y2 = Math.Max(0, Math.Min(h - 1, ny + py));
                                int row1 = y1 * w;
                                int row2 = y2 * w;
                                for (int px = -tr; px <= tr; px++)
                                {
                                    int x1 = Math.Max(0, Math.Min(w - 1, x + px));
                                    int x2 = Math.Max(0, Math.Min(w - 1, nx + px));
                                    int d = gray[row1 + x1] - gray[row2 + x2];
                                    dist2 += d * d;
                                }
                            }

                            double weight = Math.Exp(-dist2 / hh);
                            wSum += weight;
                            vSum += weight * gray[ny * w + nx];
                        }
                    }

                    int outV = (int)Math.Round(vSum / Math.Max(1e-9, wSum));
                    outBytes[y * w + x] = (byte)Math.Max(0, Math.Min(255, outV));
                }
            }

            var output = new CalibImage(w, h, 1);
            var native = output.GetNativeStruct();
            Marshal.Copy(outBytes, 0, native.data, outBytes.Length);
            return output;
        }

        private static Point2D[] FilterPointsInImage(Point2D[] points, int width, int height)
        {
            if (points == null || points.Length == 0) return Array.Empty<Point2D>();
            return points.Where(p => p.X >= 0 && p.X < width && p.Y >= 0 && p.Y < height).ToArray();
        }

        private static double PolygonArea(IReadOnlyList<System.Drawing.Point> pts)
        {
            if (pts == null || pts.Count < 3) return 0;
            double area2 = 0;
            for (int i = 0; i < pts.Count; i++)
            {
                var p1 = pts[i];
                var p2 = pts[(i + 1) % pts.Count];
                area2 += (double)p1.X * p2.Y - (double)p2.X * p1.Y;
            }
            return Math.Abs(area2) * 0.5;
        }

        private static CalibImage BuildMaskFromContours(ValueTuple<int[], int[], int[], int> contourData, int contourIdx, int width, int height)
        {
            var (flatX, flatY, contourLengths, contourCount) = contourData;
            if (contourCount <= 0 || contourLengths == null || contourLengths.Length == 0)
                throw new InvalidOperationException("Contours 为空，无法生成 Mask");

            int selected = contourIdx;
            if (selected < 0)
            {
                double maxArea = -1;
                int maxIdx = -1;
                int offset = 0;
                for (int i = 0; i < contourCount && i < contourLengths.Length; i++)
                {
                    int len = contourLengths[i];
                    if (len < 3)
                    {
                        offset += Math.Max(0, len);
                        continue;
                    }

                    if (offset + len > flatX.Length || offset + len > flatY.Length)
                        break;

                    var pts = new List<System.Drawing.Point>(len);
                    for (int k = 0; k < len; k++)
                        pts.Add(new System.Drawing.Point(flatX[offset + k], flatY[offset + k]));
                    var area = PolygonArea(pts);

                    if (area > maxArea)
                    {
                        maxArea = area;
                        maxIdx = i;
                    }
                    offset += len;
                }
                selected = maxIdx;
            }

            if (selected < 0 || selected >= contourCount || selected >= contourLengths.Length)
                throw new InvalidOperationException($"轮廓索引无效: {contourIdx}");

            int start = 0;
            for (int i = 0; i < selected; i++) start += contourLengths[i];
            int selectedLen = contourLengths[selected];
            if (selectedLen < 3) throw new InvalidOperationException("目标轮廓点数不足，无法生成 Mask");
            if (start + selectedLen > flatX.Length || start + selectedLen > flatY.Length)
                throw new InvalidOperationException("Contours 数据损坏，超出数组边界");

            var selectedPts = new System.Drawing.Point[selectedLen];
            for (int i = 0; i < selectedLen; i++)
                selectedPts[i] = new System.Drawing.Point(flatX[start + i], flatY[start + i]);

            using var bmp = new System.Drawing.Bitmap(width, height, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
            using (var g = System.Drawing.Graphics.FromImage(bmp))
            {
                g.Clear(System.Drawing.Color.Black);
                g.FillPolygon(System.Drawing.Brushes.White, selectedPts);
            }

            var bytes = new byte[width * height];
            var rect = new System.Drawing.Rectangle(0, 0, width, height);
            var bd = bmp.LockBits(rect, System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
            try
            {
                int srcStride = Math.Abs(bd.Stride);
                var row = new byte[srcStride];
                for (int y = 0; y < height; y++)
                {
                    IntPtr srcRow = IntPtr.Add(bd.Scan0, y * srcStride);
                    Marshal.Copy(srcRow, row, 0, srcStride);
                    for (int x = 0; x < width; x++)
                    {
                        int bgr = x * 3;
                        bytes[y * width + x] = (byte)(row[bgr] > 0 || row[bgr + 1] > 0 || row[bgr + 2] > 0 ? 255 : 0);
                    }
                }
            }
            finally
            {
                bmp.UnlockBits(bd);
            }

            var mask = new CalibImage(width, height, 1);
            var native = mask.GetNativeStruct();
            Marshal.Copy(bytes, 0, native.data, bytes.Length);
            return mask;
        }

        private string ResolveCompositeFlowPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return path;
            path = path.Trim();
            if (System.IO.Path.IsPathRooted(path))
                return System.IO.Path.GetFullPath(path);
            if (!string.IsNullOrEmpty(CurrentFlowFilePath))
            {
                var dir = System.IO.Path.GetDirectoryName(CurrentFlowFilePath);
                if (!string.IsNullOrEmpty(dir))
                    return System.IO.Path.GetFullPath(System.IO.Path.Combine(dir, path));
            }
            return System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, path);
        }

        private static List<FlowNode> TopologicalSortInner(
            List<FlowNode> nodes,
            List<(Guid FromId, string FromPort, Guid ToId, string ToPort)> edges,
            Dictionary<Guid, FlowNode> idMap)
        {
            var inDegree = nodes.ToDictionary(n => n.Id, _ => 0);
            foreach (var e in edges)
            {
                if (inDegree.ContainsKey(e.ToId))
                    inDegree[e.ToId]++;
            }

            var visited = new HashSet<Guid>();
            var result = new List<FlowNode>();
            var queue = new Queue<FlowNode>();
            foreach (var n in nodes)
            {
                if (inDegree[n.Id] == 0)
                    queue.Enqueue(n);
            }

            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                if (visited.Contains(current.Id)) continue;
                visited.Add(current.Id);
                result.Add(current);

                foreach (var e in edges)
                {
                    if (e.FromId != current.Id) continue;
                    if (!inDegree.ContainsKey(e.ToId)) continue;
                    inDegree[e.ToId]--;
                    if (inDegree[e.ToId] == 0 && idMap.TryGetValue(e.ToId, out var nextNode))
                        queue.Enqueue(nextNode);
                }
            }

            if (visited.Count != nodes.Count)
                throw new InvalidOperationException("组合算子: 子流程存在循环依赖");

            return result;
        }

        private static Dictionary<string, object?> BuildInnerInputsFromEdges(
            Guid innerId,
            List<(Guid FromId, string FromPort, Guid ToId, string ToPort)> edges,
            Dictionary<Guid, FlowNode> idMap)
        {
            var inputs = new Dictionary<string, object?>();
            foreach (var e in edges)
            {
                if (e.ToId != innerId) continue;
                if (!idMap.TryGetValue(e.FromId, out var fromNode)) continue;
                inputs[e.ToPort] = fromNode.Outputs.GetValueOrDefault(e.FromPort);
            }
            return inputs;
        }

        private static void MergeCompositeExternalInputs(
            FlowNode inner,
            Dictionary<string, object?> innerInputs,
            Dictionary<string, object?> compositeInputs,
            CompositeBindingsSpec? spec)
        {
            if (spec?.Inputs == null) return;
            foreach (var bi in spec.Inputs)
            {
                if (bi == null || string.IsNullOrWhiteSpace(bi.NodeId)) continue;
                if (!Guid.TryParse(bi.NodeId.Trim(), out var nid) || nid != inner.Id) continue;
                if (string.IsNullOrWhiteSpace(bi.Port) || string.IsNullOrWhiteSpace(bi.External)) continue;
                if (!innerInputs.ContainsKey(bi.Port))
                    innerInputs[bi.Port] = compositeInputs.GetValueOrDefault(bi.External);
            }
        }

        private void ExecuteCompositeSubFlow(FlowNode compositeNode, Dictionary<string, object?> compositeInputs)
        {
            string path = compositeNode.Params.GetValueOrDefault("innerFlowPath", "")?.Trim() ?? "";
            string embedded = compositeNode.Params.GetValueOrDefault("innerFlowJson", "") ?? "";
            string bindRaw = compositeNode.Params.GetValueOrDefault("bindingsJson", "") ?? "";

            string jsonText;
            if (!string.IsNullOrWhiteSpace(path))
            {
                var rp = ResolveCompositeFlowPath(path);
                if (!System.IO.File.Exists(rp))
                    throw new InvalidOperationException($"组合算子: 子流程文件不存在: {rp}");
                jsonText = System.IO.File.ReadAllText(rp);
            }
            else if (!string.IsNullOrWhiteSpace(embedded.Trim()))
                jsonText = embedded.Trim();
            else
                throw new InvalidOperationException("组合算子: 请设置 innerFlowPath 或 innerFlowJson");

            if (string.IsNullOrWhiteSpace(bindRaw.Trim()))
                throw new InvalidOperationException("组合算子: 请填写 bindingsJson（端口绑定）");

            var jOpts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            var data = JsonSerializer.Deserialize<FlowData>(jsonText, jOpts);
            if (data?.Nodes == null || data.Nodes.Count == 0)
                throw new InvalidOperationException("组合算子: 子流程为空");

            var spec = JsonSerializer.Deserialize<CompositeBindingsSpec>(bindRaw.Trim(), jOpts);
            if (spec?.Outputs == null || spec.Outputs.Count == 0)
                throw new InvalidOperationException("组合算子: bindingsJson 至少包含 outputs 数组");

            var defLookup = OperatorRegistry.ToDictionary(d => d.TypeId);
            var idMap = new Dictionary<Guid, FlowNode>();
            foreach (var nd in data.Nodes)
            {
                if (!defLookup.TryGetValue(nd.TypeId, out var def))
                    throw new InvalidOperationException($"组合算子: 未知子节点类型 {nd.TypeId}");
                if (!Guid.TryParse(nd.Id?.Trim(), out var nid))
                    throw new InvalidOperationException($"组合算子: 子节点 Id 必须为 GUID 字符串: {nd.Id}");
                var fn = new FlowNode(def, 0, 0, nid);
                if (nd.Params != null)
                {
                    foreach (var kv in nd.Params)
                    {
                        if (fn.Params.ContainsKey(kv.Key))
                            fn.Params[kv.Key] = kv.Value;
                    }
                }
                idMap[nid] = fn;
            }

            var edges = new List<(Guid FromId, string FromPort, Guid ToId, string ToPort)>();
            foreach (var cd in data.Connections ?? new List<FlowConnData>())
            {
                if (!Guid.TryParse(cd.FromNodeId?.Trim(), out var fid)) continue;
                if (!Guid.TryParse(cd.ToNodeId?.Trim(), out var tid)) continue;
                edges.Add((fid, cd.FromPort ?? "", tid, cd.ToPort ?? ""));
            }

            var innerList = idMap.Values.ToList();
            var sorted = TopologicalSortInner(innerList, edges, idMap);

            foreach (var inner in sorted)
            {
                var innerInputs = BuildInnerInputsFromEdges(inner.Id, edges, idMap);
                MergeCompositeExternalInputs(inner, innerInputs, compositeInputs, spec);
                ExecuteNode(inner, innerInputs);
            }

            compositeNode.Outputs.Clear();
            foreach (var ob in spec.Outputs)
            {
                if (ob == null || string.IsNullOrWhiteSpace(ob.External) ||
                    string.IsNullOrWhiteSpace(ob.NodeId) || string.IsNullOrWhiteSpace(ob.Port))
                    continue;
                if (!Guid.TryParse(ob.NodeId.Trim(), out var oid)) continue;
                if (!idMap.TryGetValue(oid, out var srcNode)) continue;
                compositeNode.Outputs[ob.External] = srcNode.Outputs.GetValueOrDefault(ob.Port);
            }

            compositeNode.ResultSummary = $"Composite {innerList.Count} nodes → {compositeNode.Outputs.Count} outs";
        }

        private struct HomographyTransform
        {
            public double H11, H12, H13;
            public double H21, H22, H23;
            public double H31, H32, H33;
        }

        private struct Poly2DTransform
        {
            public double X_x, X_y, X_1, X_x2, X_xy, X_y2;
            public double Y_x, Y_y, Y_1, Y_x2, Y_xy, Y_y2;
        }

        private static bool SolveLinear(double[,] a, double[] b, out double[] x)
        {
            int n = b.Length;
            x = new double[n];
            var aug = new double[n, n + 1];
            for (int r = 0; r < n; r++)
            {
                for (int c = 0; c < n; c++) aug[r, c] = a[r, c];
                aug[r, n] = b[r];
            }

            for (int i = 0; i < n; i++)
            {
                int pivot = i;
                double best = Math.Abs(aug[i, i]);
                for (int r = i + 1; r < n; r++)
                {
                    double v = Math.Abs(aug[r, i]);
                    if (v > best) { best = v; pivot = r; }
                }
                if (best < 1e-12) return false;
                if (pivot != i)
                {
                    for (int c = i; c <= n; c++)
                    {
                        double tmp = aug[i, c];
                        aug[i, c] = aug[pivot, c];
                        aug[pivot, c] = tmp;
                    }
                }

                double diag = aug[i, i];
                for (int c = i; c <= n; c++) aug[i, c] /= diag;
                for (int r = 0; r < n; r++)
                {
                    if (r == i) continue;
                    double f = aug[r, i];
                    if (Math.Abs(f) < 1e-15) continue;
                    for (int c = i; c <= n; c++) aug[r, c] -= f * aug[i, c];
                }
            }
            for (int i = 0; i < n; i++) x[i] = aug[i, n];
            return true;
        }

        private static bool SolveLeastSquares(double[][] rows, double[] y, int cols, out double[] coeffs)
        {
            coeffs = new double[cols];
            var ata = new double[cols, cols];
            var aty = new double[cols];
            for (int r = 0; r < rows.Length; r++)
            {
                var row = rows[r];
                for (int i = 0; i < cols; i++)
                {
                    aty[i] += row[i] * y[r];
                    for (int j = 0; j < cols; j++) ata[i, j] += row[i] * row[j];
                }
            }
            return SolveLinear(ata, aty, out coeffs);
        }

        private static HomographyTransform FitHomography(Point2D[] imgPts, Point2D[] worldPts)
        {
            int n = imgPts.Length;
            var rows = new List<double[]>(n * 2);
            var yy = new List<double>(n * 2);
            for (int i = 0; i < n; i++)
            {
                double x = imgPts[i].X, y = imgPts[i].Y;
                double X = worldPts[i].X, Y = worldPts[i].Y;
                rows.Add(new[] { x, y, 1.0, 0, 0, 0, -x * X, -y * X });
                yy.Add(X);
                rows.Add(new[] { 0, 0, 0, x, y, 1.0, -x * Y, -y * Y });
                yy.Add(Y);
            }
            if (!SolveLeastSquares(rows.ToArray(), yy.ToArray(), 8, out var c))
                throw new InvalidOperationException("透视标定失败: 方程不可解");
            return new HomographyTransform
            {
                H11 = c[0], H12 = c[1], H13 = c[2],
                H21 = c[3], H22 = c[4], H23 = c[5],
                H31 = c[6], H32 = c[7], H33 = 1.0
            };
        }

        private static Point2D ApplyHomography(Point2D p, HomographyTransform h)
        {
            double den = h.H31 * p.X + h.H32 * p.Y + h.H33;
            if (Math.Abs(den) < 1e-12) den = 1e-12;
            return new Point2D(
                (h.H11 * p.X + h.H12 * p.Y + h.H13) / den,
                (h.H21 * p.X + h.H22 * p.Y + h.H23) / den);
        }

        private static Poly2DTransform FitPoly2D(Point2D[] imgPts, Point2D[] worldPts)
        {
            int n = imgPts.Length;
            var rows = new double[n][];
            var yx = new double[n];
            var yy = new double[n];
            for (int i = 0; i < n; i++)
            {
                double x = imgPts[i].X, y = imgPts[i].Y;
                rows[i] = new[] { x, y, 1.0, x * x, x * y, y * y };
                yx[i] = worldPts[i].X;
                yy[i] = worldPts[i].Y;
            }
            if (!SolveLeastSquares(rows, yx, 6, out var cx) || !SolveLeastSquares(rows, yy, 6, out var cy))
                throw new InvalidOperationException("Poly2D标定失败: 方程不可解");
            return new Poly2DTransform
            {
                X_x = cx[0], X_y = cx[1], X_1 = cx[2], X_x2 = cx[3], X_xy = cx[4], X_y2 = cx[5],
                Y_x = cy[0], Y_y = cy[1], Y_1 = cy[2], Y_x2 = cy[3], Y_xy = cy[4], Y_y2 = cy[5]
            };
        }

        private static Point2D ApplyPoly2D(Point2D p, Poly2DTransform t)
        {
            double x = p.X, y = p.Y;
            double xx = x * x, xy = x * y, yy = y * y;
            return new Point2D(
                t.X_x * x + t.X_y * y + t.X_1 + t.X_x2 * xx + t.X_xy * xy + t.X_y2 * yy,
                t.Y_x * x + t.Y_y * y + t.Y_1 + t.Y_x2 * xx + t.Y_xy * xy + t.Y_y2 * yy);
        }

        private static string FormatHomography(HomographyTransform h)
        {
            return $"H = [[{h.H11:F6}, {h.H12:F6}, {h.H13:F6}], [{h.H21:F6}, {h.H22:F6}, {h.H23:F6}], [{h.H31:F6}, {h.H32:F6}, {h.H33:F6}]]";
        }

        private static string FormatPoly2D(Poly2DTransform p)
        {
            return $"X = {p.X_x:F6}*x + {p.X_y:F6}*y + {p.X_1:F6} + {p.X_x2:F6}*x^2 + {p.X_xy:F6}*x*y + {p.X_y2:F6}*y^2\n" +
                   $"Y = {p.Y_x:F6}*x + {p.Y_y:F6}*y + {p.Y_1:F6} + {p.Y_x2:F6}*x^2 + {p.Y_xy:F6}*x*y + {p.Y_y2:F6}*y^2";
        }

        private static CalibImage GrabOneCameraFrameOrThrow(int deviceIndex, int targetWidth, int targetHeight)
        {
            using var cam = new CameraService();
            if (!cam.ConnectByIndex(deviceIndex))
                throw new InvalidOperationException($"相机连接失败，deviceIndex={deviceIndex}");
            var img = cam.GrabOneFrame(targetWidth, targetHeight);
            if (img == null)
                throw new InvalidOperationException($"相机取图失败: {cam.LastError ?? "未知错误"}");
            return img;
        }

        private static (CalibImage LastImage, int Count) GrabLoopCameraFramesOrThrow(
            int deviceIndex, int frameCount, int intervalMs, int targetWidth, int targetHeight)
        {
            frameCount = Math.Max(1, frameCount);
            intervalMs = Math.Max(0, intervalMs);
            CalibImage? last = null;
            int okCount = 0;
            using var cam = new CameraService();
            if (!cam.ConnectByIndex(deviceIndex))
                throw new InvalidOperationException($"相机连接失败，deviceIndex={deviceIndex}");
            for (int i = 0; i < frameCount; i++)
            {
                var img = cam.GrabOneFrame(targetWidth, targetHeight);
                if (img != null)
                {
                    last?.Dispose();
                    last = img;
                    okCount++;
                }
                if (intervalMs > 0 && i < frameCount - 1)
                    System.Threading.Thread.Sleep(intervalMs);
            }
            if (last == null)
                throw new InvalidOperationException($"相机循环取图失败: {cam.LastError ?? "未抓到有效帧"}");
            return (last, okCount);
        }

        private static (List<CalibImage> Frames, int Count) GrabLoopCameraFramesListOrThrow(
            int deviceIndex, int frameCount, int intervalMs, int targetWidth, int targetHeight)
        {
            frameCount = Math.Max(1, frameCount);
            intervalMs = Math.Max(0, intervalMs);
            var frames = new List<CalibImage>(frameCount);
            int okCount = 0;
            using var cam = new CameraService();
            if (!cam.ConnectByIndex(deviceIndex))
                throw new InvalidOperationException($"相机连接失败，deviceIndex={deviceIndex}");
            for (int i = 0; i < frameCount; i++)
            {
                var img = cam.GrabOneFrame(targetWidth, targetHeight);
                if (img != null)
                {
                    frames.Add(img);
                    okCount++;
                }
                if (intervalMs > 0 && i < frameCount - 1)
                    System.Threading.Thread.Sleep(intervalMs);
            }
            if (frames.Count == 0)
                throw new InvalidOperationException($"相机循环取图失败: {cam.LastError ?? "未抓到有效帧"}");
            return (frames, okCount);
        }

        private HashSet<FlowNode> GetDownstreamNodes(FlowNode source)
        {
            var result = new HashSet<FlowNode>();
            var q = new Queue<FlowNode>();
            q.Enqueue(source);
            result.Add(source);
            while (q.Count > 0)
            {
                var cur = q.Dequeue();
                foreach (var c in _connections)
                {
                    if (c.FromPort.Owner != cur) continue;
                    var nxt = c.ToPort.Owner;
                    if (result.Add(nxt))
                        q.Enqueue(nxt);
                }
            }
            return result;
        }

        private void ExecuteNode(FlowNode node, Dictionary<string, object?>? explicitInputs = null)
        {
            var inputs = explicitInputs ?? GetNodeInputs(node);
            node.Outputs.Clear();
            node.ErrorMessage = null;
            SetNodeStatus(node, true);

            try
            {
                switch (node.Def.TypeId)
                {
                    case "load_image":
                    {
                        string configuredPath = node.Params.GetValueOrDefault("filePath", "")?.Trim() ?? "";
                        string resolvedPath = configuredPath;
                        if (!string.IsNullOrWhiteSpace(configuredPath) && !System.IO.Path.IsPathRooted(configuredPath))
                            resolvedPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, configuredPath);

                        if (!string.IsNullOrWhiteSpace(resolvedPath))
                        {
                            if (!System.IO.File.Exists(resolvedPath))
                                throw new System.IO.FileNotFoundException($"加载图像失败，文件不存在: {resolvedPath}");
                            var img = CalibAPI.LoadImage(resolvedPath);
                            node.Outputs["Image"] = img;
                            break;
                        }

                        var dlg = new OpenFileDialog
                        {
                            Filter = "图像文件|*.bmp;*.png;*.jpg;*.tif|所有文件|*.*",
                            Title = "选择图像文件"
                        };
                        if (dlg.ShowDialog() == true)
                        {
                            var img = CalibAPI.LoadImage(dlg.FileName);
                            node.Outputs["Image"] = img;
                        }
                        else
                        {
                            node.ErrorMessage = "用户取消";
                        }
                        break;
                    }

                    case "camera_snap":
                    {
                        int deviceIndex = int.TryParse(node.Params.GetValueOrDefault("deviceIndex"), out var di) ? di : 0;
                        int targetWidth = int.TryParse(node.Params.GetValueOrDefault("targetWidth"), out var tw) ? tw : 0;
                        int targetHeight = int.TryParse(node.Params.GetValueOrDefault("targetHeight"), out var th) ? th : 0;
                        var img = GrabOneCameraFrameOrThrow(deviceIndex, targetWidth, targetHeight);
                        node.Outputs["Image"] = img;
                        node.ResultSummary = $"Snap OK: dev={deviceIndex}";
                        break;
                    }

                    case "camera_loop":
                    {
                        int deviceIndex = int.TryParse(node.Params.GetValueOrDefault("deviceIndex"), out var di) ? di : 0;
                        string mode = (node.Params.GetValueOrDefault("mode", "last_only") ?? "last_only").Trim().ToLowerInvariant();
                        int frameCount = int.TryParse(node.Params.GetValueOrDefault("frameCount"), out var fc) ? fc : 10;
                        int intervalMs = int.TryParse(node.Params.GetValueOrDefault("intervalMs"), out var im) ? im : 100;
                        int targetWidth = int.TryParse(node.Params.GetValueOrDefault("targetWidth"), out var tw) ? tw : 0;
                        int targetHeight = int.TryParse(node.Params.GetValueOrDefault("targetHeight"), out var th) ? th : 0;
                        var loopResult = GrabLoopCameraFramesOrThrow(deviceIndex, frameCount, intervalMs, targetWidth, targetHeight);
                        node.Outputs["Image"] = loopResult.LastImage;
                        node.Outputs["Count"] = loopResult.Count;
                        node.ResultSummary = mode == "per_frame"
                            ? $"Loop(per_frame): captured {loopResult.Count}/{Math.Max(1, frameCount)} (single-node fallback=last frame)"
                            : $"Loop OK: {loopResult.Count}/{Math.Max(1, frameCount)}";
                        break;
                    }

                    case "world_coords":
                    {
                        var coords = InputWorldCoordinates();
                        if (coords != null)
                            node.Outputs["Points"] = coords;
                        else
                            node.ErrorMessage = "用户取消";
                        break;
                    }

                    case "grayscale":
                    {
                        var srcImg = inputs["In"] as CalibImage;
                        if (srcImg == null) throw new InvalidOperationException("灰度化: 缺少输入图像");
                        using var detector = new TrajectoryStepDetector();
                        detector.ConvertToGrayscale(srcImg);
                        var gray = detector.GetStepImage(1);
                        node.Outputs["Out"] = gray;
                        break;
                    }

                    case "clahe":
                    {
                        var srcImg = inputs["In"] as CalibImage;
                        if (srcImg == null) throw new InvalidOperationException("CLAHE: 缺少输入图像");
                        double clipLimit = double.Parse(node.Params["clipLimit"]);
                        int tileSize = int.Parse(node.Params["tileSize"]);
                        using var detector = new TrajectoryStepDetector();
                        detector.ConvertToGrayscale(srcImg);
                        detector.ApplyCLAHE(clipLimit, tileSize);
                        var claheImg = detector.GetStepImage(1);
                        node.Outputs["Out"] = claheImg;
                        break;
                    }

                    case "binarize":
                    {
                        var srcImg = inputs["In"] as CalibImage;
                        if (srcImg == null) throw new InvalidOperationException("二值化: 缺少输入图像");
                        int blurSize = int.Parse(node.Params["blurSize"]);
                        int morphSize = int.Parse(node.Params["morphSize"]);
                        using var detector = new TrajectoryStepDetector();
                        detector.ConvertToGrayscale(srcImg);
                        detector.PreprocessAndFindContours(blurSize, morphSize, false);
                        var binImg = detector.GetStepImage(2);
                        node.Outputs["Out"] = binImg;
                        break;
                    }

                    case "gray_range_binary":
                    {
                        var srcImg = inputs["In"] as CalibImage;
                        if (srcImg == null) throw new InvalidOperationException("灰度范围二值化: 缺少输入图像");
                        int grayLow = int.Parse(node.Params["grayLow"]);
                        int grayHigh = int.Parse(node.Params["grayHigh"]);
                        using var detector = new TrajectoryStepDetector();
                        detector.ConvertToGrayscale(srcImg);
                        detector.GrayRangeBinary(grayLow, grayHigh);
                        var grayBinImg = detector.GetStepImage(2);
                        node.Outputs["Out"] = grayBinImg;
                        break;
                    }

                    case "canny":
                    {
                        var srcImg = inputs["In"] as CalibImage;
                        if (srcImg == null) throw new InvalidOperationException("Canny: 缺少输入图像");
                        using var detector = new TrajectoryStepDetector();
                        detector.ConvertToGrayscale(srcImg);
                        detector.CannyEdges(0, 0, 0); // 全部自适应
                        var edgeImg = detector.GetStepImage(6);
                        node.Outputs["Edge"] = edgeImg;
                        break;
                    }

                    case "sobel":
                    {
                        var srcImg = inputs["In"] as CalibImage;
                        if (srcImg == null) throw new InvalidOperationException("Sobel: 缺少输入图像");
                        int threshold = int.TryParse(node.Params.GetValueOrDefault("threshold"), out var th) ? th : 48;
                        var edgeImg = SobelEdgeImage(srcImg, threshold);
                        node.Outputs["Edge"] = edgeImg;
                        break;
                    }

                    case "scharr":
                    {
                        var srcImg = inputs["In"] as CalibImage;
                        if (srcImg == null) throw new InvalidOperationException("Scharr: 缺少输入图像");
                        int threshold = int.TryParse(node.Params.GetValueOrDefault("threshold"), out var th) ? th : 48;
                        var edgeImg = ScharrEdgeImage(srcImg, threshold);
                        node.Outputs["Edge"] = edgeImg;
                        break;
                    }

                    case "phase_congruency":
                    {
                        var srcImg = inputs["In"] as CalibImage;
                        if (srcImg == null) throw new InvalidOperationException("PhaseCongruency: 缺少输入图像");
                        double threshold = double.TryParse(node.Params.GetValueOrDefault("threshold"), out var t) ? t : 0.45;
                        double noiseSigma = double.TryParse(node.Params.GetValueOrDefault("noiseSigma"), out var ns) ? ns : 0.12;
                        int blurKsize = int.TryParse(node.Params.GetValueOrDefault("blurKsize"), out var bk) ? bk : 3;
                        string debugDumpPrefix = node.Params.GetValueOrDefault("debugDumpPrefix", "") ?? "";
                        var resultImgs = PhaseCongruencyEdgeImage(srcImg, threshold, noiseSigma, blurKsize, debugDumpPrefix);
                        node.Outputs["Response"] = resultImgs.Response;
                        var edgeImg = resultImgs.Edge;
                        node.Outputs["Edge"] = edgeImg;
                        var stats = AnalyzeBinaryImage(edgeImg);
                        node.ResultSummary = $"PC->Edge: {stats.WhitePixels} ({stats.WhiteRatio:P2})";
                        string phaseLog = $"[phase_congruency] th={threshold:F3}, noise={noiseSigma:F3}, blur={blurKsize}, white={stats.WhitePixels} ({stats.WhiteRatio:P2}), debug='{debugDumpPrefix}'";
                        AppendLog(phaseLog);
                        Console.WriteLine(phaseLog);
                        break;
                    }

                    case "freq_filter_binary":
                    {
                        var srcImg = inputs["In"] as CalibImage;
                        if (srcImg == null) throw new InvalidOperationException("频域滤波二值化: 缺少输入图像");
                        string mode = node.Params.GetValueOrDefault("mode", "bandpass") ?? "bandpass";
                        double lowCut = double.TryParse(node.Params.GetValueOrDefault("lowCut"), out var lc) ? lc : 0.06;
                        double highCut = double.TryParse(node.Params.GetValueOrDefault("highCut"), out var hc) ? hc : 0.24;
                        double threshold = double.TryParse(node.Params.GetValueOrDefault("threshold"), out var t2) ? t2 : 0.48;
                        bool useOtsu = bool.TryParse(node.Params.GetValueOrDefault("useOtsu"), out var b2) ? b2 : true;
                        var result = FrequencyFilterToBinaryImage(srcImg, mode, lowCut, highCut, threshold, useOtsu);
                        node.Outputs["Filtered"] = result.Filtered;
                        node.Outputs["Binary"] = result.Binary;
                        var stats = AnalyzeBinaryImage(result.Binary);
                        node.ResultSummary = $"FreqBin: {stats.WhitePixels} ({stats.WhiteRatio:P2})";
                        break;
                    }

                    case "local_freq_sauvola_niblack":
                    {
                        var srcImg = inputs["In"] as CalibImage;
                        if (srcImg == null) throw new InvalidOperationException("局部频域阈值: 缺少输入图像");
                        string method = node.Params.GetValueOrDefault("method", "sauvola") ?? "sauvola";
                        int windowSize = int.TryParse(node.Params.GetValueOrDefault("windowSize"), out var ws) ? ws : 25;
                        double k = double.TryParse(node.Params.GetValueOrDefault("k"), out var kk) ? kk : 0.32;
                        double r = double.TryParse(node.Params.GetValueOrDefault("R"), out var rr) ? rr : 0.5;
                        double lowCut = double.TryParse(node.Params.GetValueOrDefault("lowCut"), out var lc) ? lc : 0.04;
                        double highCut = double.TryParse(node.Params.GetValueOrDefault("highCut"), out var hc) ? hc : 0.28;
                        var result = LocalFreqSauvolaNiblackImage(srcImg, method, windowSize, k, r, lowCut, highCut);
                        node.Outputs["Filtered"] = result.Filtered;
                        node.Outputs["Binary"] = result.Binary;
                        var stats = AnalyzeBinaryImage(result.Binary);
                        node.ResultSummary = $"{method}: {stats.WhitePixels} ({stats.WhiteRatio:P2})";
                        break;
                    }

                    case "pre_filter":
                    {
                        var srcImg = inputs["In"] as CalibImage;
                        if (srcImg == null) throw new InvalidOperationException("预滤波: 缺少输入图像");
                        string mode = node.Params.GetValueOrDefault("mode", "gaussian") ?? "gaussian";
                        int ksize = int.TryParse(node.Params.GetValueOrDefault("ksize"), out var ks) ? ks : 3;
                        var outImg = PreFilterImage(srcImg, mode, ksize);
                        node.Outputs["Out"] = outImg;
                        break;
                    }

                    case "nlmeans":
                    {
                        var srcImg = inputs["In"] as CalibImage;
                        if (srcImg == null) throw new InvalidOperationException("NLMeans: 缺少输入图像");
                        double hStrength = double.TryParse(node.Params.GetValueOrDefault("h"), out var hs) ? hs : 12.0;
                        int searchWindow = int.TryParse(node.Params.GetValueOrDefault("searchWindow"), out var sw) ? sw : 11;
                        int templateWindow = int.TryParse(node.Params.GetValueOrDefault("templateWindow"), out var tw) ? tw : 3;
                        var outImg = NLMeansDenoiseImage(srcImg, hStrength, searchWindow, templateWindow);
                        node.Outputs["Out"] = outImg;
                        break;
                    }

                    case "find_contours":
                    {
                        var inImg = inputs["In"] as CalibImage;
                        if (inImg == null) throw new InvalidOperationException("查找轮廓: 缺少输入二值图像");
                        using var detector = new TrajectoryStepDetector();
                        detector.SetDarkBinary(inImg);
                        int count = detector.FindAndSortDarkContours();
                        var contourVis = detector.GetContourVis();
                        node.Outputs["Out"] = contourVis;
                        node.Outputs["Count"] = count;
                        var (cx, cy, cl, cc) = detector.ExportSortedContours();
                        node.Outputs["Contours"] = (cx, cy, cl, cc);
                        break;
                    }

                    case "apply_mask":
                    {
                        var srcImg = inputs["Image"] as CalibImage;
                        var maskImg = inputs["Mask"] as CalibImage;
                        if (srcImg == null) throw new InvalidOperationException("Mask应用: 缺少输入图像");
                        if (maskImg == null) throw new InvalidOperationException("Mask应用: 缺少Mask图像");
                        var resultImg = ImageOps.ApplyMask(srcImg, maskImg);
                        node.Outputs["Out"] = resultImg;
                        break;
                    }

                    case "create_mask":
                    {
                        int contourIdx = int.Parse(node.Params["contourIdx"]);
                        CalibImage maskImg;
                        var inImg = inputs["In"] as CalibImage;

                        if (inputs.TryGetValue("Contours", out var contourObj) &&
                            contourObj is ValueTuple<int[], int[], int[], int> contourData)
                        {
                            int w = inImg?.Width ?? CalibAPI.ImageWidth;
                            int h = inImg?.Height ?? CalibAPI.ImageHeight;
                            maskImg = BuildMaskFromContours(contourData, contourIdx, w, h);
                        }
                        else
                        {
                            if (inImg == null) throw new InvalidOperationException("生成Mask: 缺少输入图像或Contours");
                            using var detector = new TrajectoryStepDetector();
                            detector.ConvertToGrayscale(inImg);
                            detector.PreprocessAndFindContours();
                            int ret = detector.CreateMaskFromLargestContour(contourIdx);
                            if (ret != 0) throw new InvalidOperationException("生成Mask: 轮廓为空或查找失败");
                            maskImg = detector.GetStepImage(3) ?? throw new InvalidOperationException("生成Mask: 获取输出失败");
                        }
                        node.Outputs["Mask"] = maskImg;
                        break;
                    }

                    case "detect_hollow":
                    {
                        var srcImg = (inputs.TryGetValue("Mask", out var maskObj) ? maskObj : null) as CalibImage
                                     ?? (inputs.TryGetValue("Image", out var imgObj) ? imgObj : null) as CalibImage;
                        if (srcImg == null) throw new InvalidOperationException("空洞检测: 缺少输入图像");
                        int hLow = int.Parse(node.Params["hollowGrayLow"]);
                        int hHigh = int.Parse(node.Params["hollowGrayHigh"]);
                        int targetH = int.Parse(node.Params["targetHollows"]);
                        int morphK = int.Parse(node.Params["morphKernelSize"]);
                        var result = CalibAPI.DetectHollowTrajectory(srcImg, morphKernelSize: morphK, targetHollows: targetH,
                            hollowGrayLow: hLow, hollowGrayHigh: hHigh);
                        node.Outputs["Count"] = result.Count;
                        var hollowImg = result.StepImages.Count > 3 ? result.StepImages[3] : null;
                        // Transfer ownership of one image to flow output, release the rest.
                        for (int i = 0; i < result.StepImages.Count; i++)
                        {
                            if (!ReferenceEquals(result.StepImages[i], hollowImg))
                                result.StepImages[i]?.Dispose();
                        }
                        node.Outputs["Hollow"] = hollowImg;
                        break;
                    }

                    case "detect_dark":
                    {
                        var srcImg = inputs["In"] as CalibImage;
                        var maskImg = inputs["Mask"] as CalibImage;
                        if (srcImg == null) throw new InvalidOperationException("暗条检测: 缺少输入图像");
                        if (maskImg == null) throw new InvalidOperationException("暗条检测: 缺少Mask图像");
                        int darkTh = int.Parse(node.Params["darkThreshold"]);
                        using var detector = new TrajectoryStepDetector();
                        detector.ConvertToGrayscale(srcImg);
                        detector.SetMask(maskImg);
                        detector.DetectDarkBars(darkTh, 0, 0);
                        var darkImg = detector.GetStepImage(4);
                        node.Outputs["Dark"] = darkImg;
                        break;
                    }

                    case "morphology":
                    {
                        var darkImg = inputs["In"] as CalibImage;
                        if (darkImg == null) throw new InvalidOperationException("形态学: 缺少输入图像");
                        int kernelSize = int.Parse(node.Params["kernelSize"]);
                        int blurKsize = int.Parse(node.Params["blurKsize"]);
                        double blurSigma = double.Parse(node.Params["blurSigma"]);
                        using var detector = new TrajectoryStepDetector();
                        detector.SetDarkBinary(darkImg);
                        detector.MorphologyCleanup(kernelSize, blurKsize, blurSigma);
                        var morphImg = detector.GetStepImage(4);
                        node.Outputs["Out"] = morphImg;
                        break;
                    }

                    case "expand_edge":
                    {
                        var darkImg = inputs["Dark"] as CalibImage;
                        var edgeImg = inputs["Edge"] as CalibImage;
                        if (darkImg == null) throw new InvalidOperationException("边界膨胀: 缺少Dark输入");
                        if (edgeImg == null) throw new InvalidOperationException("边界膨胀: 缺少Edge输入");
                        int expandDist = int.Parse(node.Params["expandDist"]);
                        using var detector = new TrajectoryStepDetector();
                        detector.SetDarkBinary(darkImg);
                        detector.SetGrayMat(edgeImg);
                        detector.CannyEdges();
                        detector.ExpandToEdgeBoundary(expandDist);
                        var expandImg = detector.GetStepImage(7);
                        node.Outputs["Out"] = expandImg;
                        break;
                    }

                    case "sort_contours":
                    {
                        var srcImg = inputs["In"] as CalibImage;
                        if (srcImg == null) throw new InvalidOperationException("排序: 缺少输入图像");
                        using var detector = new TrajectoryStepDetector();
                        detector.SetDarkBinary(srcImg);
                        int count = detector.FindAndSortDarkContours();
                        node.Outputs["Count"] = count;
                        break;
                    }

                    case "sample":
                    {
                        if (!inputs.TryGetValue("Contours", out var contourObj))
                            throw new InvalidOperationException("采样: 缺少输入轮廓数据");
                        if (contourObj is not ValueTuple<int[], int[], int[], int> contourData)
                            throw new InvalidOperationException("采样: 轮廓数据格式错误");
                        var (flatX, flatY, contourLengths, contourCount) = contourData;
                        if (contourCount <= 0)
                            throw new InvalidOperationException("采样: 轮廓为空，请先执行查找轮廓");
                        int targetBars = int.Parse(node.Params["targetBars"]);
                        int spacing = int.Parse(node.Params["spacing"]);
                        var (pts, barIds) = CalibAPI.SampleContoursFromPointsWithBarIds(flatX, flatY, contourLengths,
                            CalibAPI.ImageWidth, CalibAPI.ImageHeight, targetBars, (double)spacing);
                        node.Outputs["Points"] = pts;
                        node.Outputs["BarIds"] = barIds;
                        break;
                    }

                    case "filter_contours":
                    {
                        if (!inputs.TryGetValue("Contours", out var contourObj))
                            throw new InvalidOperationException("轮廓筛选: 缺少输入轮廓数据");
                        if (contourObj is not ValueTuple<int[], int[], int[], int> contourData)
                            throw new InvalidOperationException("轮廓筛选: 轮廓数据格式错误");

                        double minArea = double.Parse(node.Params["minArea"]);
                        double maxArea = double.Parse(node.Params["maxArea"]);
                        double minAspect = double.Parse(node.Params["minAspect"]);
                        double maxAspect = double.Parse(node.Params["maxAspect"]);
                        double minCircularity = double.Parse(node.Params["minCircularity"]);
                        int targetCount = int.Parse(node.Params["targetCount"]);

                        var filtered = FilterContoursByGeometry(
                            contourData, minArea, maxArea, minAspect, maxAspect, minCircularity, targetCount);

                        node.Outputs["Contours"] = filtered;
                        node.Outputs["Count"] = filtered.count;
                        node.ResultSummary = $"Filtered: {filtered.count}";
                        break;
                    }

                    case "match_contours":
                    {
                        if (!inputs.TryGetValue("Contours", out var contourObj))
                            throw new InvalidOperationException("形状匹配: 缺少输入轮廓数据");
                        if (contourObj is not ValueTuple<int[], int[], int[], int> contourData)
                            throw new InvalidOperationException("形状匹配: 轮廓数据格式错误");

                        int templateIndex = int.Parse(node.Params["templateIndex"]);
                        double maxDistance = double.Parse(node.Params["maxDistance"]);
                        int samplePoints = int.Parse(node.Params["samplePoints"]);
                        int targetCount = int.Parse(node.Params["targetCount"]);

                        var matched = MatchContoursByTemplate(contourData, templateIndex, maxDistance, samplePoints, targetCount);
                        node.Outputs["Contours"] = matched;
                        node.Outputs["Count"] = matched.count;
                        node.ResultSummary = $"Matched: {matched.count}";
                        break;
                    }

                    case "shape_match_global":
                    {
                        var edgeImg = inputs["Edge"] as CalibImage;
                        if (edgeImg == null) throw new InvalidOperationException("全局形状匹配: 缺少边缘图输入");

                        int topN = int.Parse(node.Params["topN"]);
                        double minScore = double.Parse(node.Params["minScore"]);
                        double areaWeight = double.Parse(node.Params.GetValueOrDefault("areaWeight", "0.6"));
                        int step = int.Parse(node.Params["step"]);
                        int downsample = int.Parse(node.Params["downsample"]);
                        int maxTemplateSize = int.Parse(node.Params["maxTemplateSize"]);
                        int templateBars = int.Parse(node.Params["templateBars"]);
                        double templateConsensus = double.Parse(node.Params["templateConsensus"]);
                        int edgeMinComponent = int.Parse(node.Params["edgeMinComponent"]);
                        int edgeOpenRadius = int.Parse(node.Params["edgeOpenRadius"]);
                        string templatePath = (node.Params.GetValueOrDefault("templatePath", "") ?? "").Trim();

                        using var edgeBmp = edgeImg.ToBitmap();
                        if (edgeBmp == null) throw new InvalidOperationException("全局形状匹配: 边缘图转换失败");

                        var edgeBin = BitmapToBinary(edgeBmp, 1);
                        if (edgeOpenRadius > 0)
                            edgeBin = DilateBinary(ErodeBinary(edgeBin, edgeOpenRadius), edgeOpenRadius);
                        edgeBin = RemoveSmallComponents(edgeBin, Math.Max(1, edgeMinComponent));
                        var edgeSmall = DownsampleBinary(edgeBin, Math.Max(1, downsample));

                        bool[,] templateBin;
                        if (inputs.TryGetValue("Template", out var tplObj) && tplObj is CalibImage tplImg)
                        {
                            using var tplBmp = tplImg.ToBitmap();
                            if (tplBmp == null) throw new InvalidOperationException("全局形状匹配: 模板图转换失败");
                            templateBin = BitmapToBinary(tplBmp, 1);
                            templateBin = DownsampleBinary(templateBin, Math.Max(1, downsample));
                        }
                        else if (!string.IsNullOrWhiteSpace(templatePath) && System.IO.File.Exists(templatePath))
                        {
                            using var tplBmp = new System.Drawing.Bitmap(templatePath);
                            templateBin = BitmapToBinary(tplBmp, 1);
                            templateBin = DownsampleBinary(templateBin, Math.Max(1, downsample));
                        }
                        else if (inputs.TryGetValue("Contours", out var cObj) && cObj is ValueTuple<int[], int[], int[], int> contourData)
                        {
                            templateBin = BuildFusedTemplateFromContours(contourData, templateBars, Math.Max(24, maxTemplateSize), templateConsensus);
                        }
                        else
                        {
                            var bbox = LargestConnectedBoundingBox(edgeSmall);
                            if (bbox == null) throw new InvalidOperationException("全局形状匹配: 未找到可用模板（边缘连通域为空）");
                            templateBin = CropBinary(edgeSmall, bbox.Value.X, bbox.Value.Y, bbox.Value.W, bbox.Value.H);
                        }

                        templateBin = ResizeBinary(templateBin, Math.Max(24, maxTemplateSize));
                        var ptsSmall = GlobalShapeMatchTopN(edgeSmall, templateBin, Math.Max(1, topN), Math.Max(0, Math.Min(1, minScore)), Math.Max(1, step), areaWeight);
                        int factor = Math.Max(1, downsample);
                        var points = ptsSmall.Select(p => new Point2D(p.X * factor, p.Y * factor)).ToArray();
                        var templatePolyline = TemplateToPolyline(templateBin, 256);
                        var matchedContours = BuildMatchedContoursFromTemplate(points, templatePolyline, factor);

                        node.Outputs["Contours"] = matchedContours;
                        node.Outputs["Count"] = matchedContours.Item4;
                        node.ResultSummary = $"TopNContours: {matchedContours.Item4}";
                        break;
                    }

                    case "fuse_contours_template":
                    {
                        if (!inputs.TryGetValue("Contours", out var contourObj))
                            throw new InvalidOperationException("融合模板: 缺少输入轮廓数据");
                        if (contourObj is not ValueTuple<int[], int[], int[], int> contourData)
                            throw new InvalidOperationException("融合模板: 轮廓数据格式错误");

                        int templateBars = int.Parse(node.Params["templateBars"]);
                        int canvasSize = int.Parse(node.Params["canvasSize"]);
                        double templateConsensus = double.Parse(node.Params["templateConsensus"]);
                        int samplePoints = int.Parse(node.Params["samplePoints"]);
                        string fusionMode = (node.Params.GetValueOrDefault("fusionMode", "mutual") ?? "mutual").Trim().ToLowerInvariant();
                        string centerlineMode = (node.Params.GetValueOrDefault("centerlineMode", "on") ?? "on").Trim().ToLowerInvariant();

                        bool[,] templateBin = fusionMode == "global_layout"
                            ? BuildFusedTemplateFromContours(contourData, templateBars, canvasSize, templateConsensus)
                            : BuildMutualTemplateFromContours(contourData, templateBars, canvasSize, samplePoints);
                        if (centerlineMode == "on")
                            templateBin = ExtractCenterline(templateBin);
                        var templateImg = BinaryToCalibImage(templateBin);
                        node.Outputs["Template"] = templateImg;
                        node.ResultSummary = $"Template: {canvasSize}x{canvasSize}, mode={fusionMode}, centerline={centerlineMode}";
                        break;
                    }

                    case "fit_shape":
                    {
                        var inPts = inputs["In"] as Point2D[];
                        if (inPts == null) throw new InvalidOperationException("拟合: 缺少输入点");
                        if (inPts.Length < 3)
                        {
                            node.Outputs["Out"] = inPts;
                            break;
                        }

                        string mode = (node.Params.GetValueOrDefault("mode", "hybrid") ?? "hybrid").Trim().ToLowerInvariant();
                        int windowRadius = 1;
                        if (node.Params.TryGetValue("windowRadius", out var wrText))
                            int.TryParse(wrText, out windowRadius);
                        if (windowRadius < 0) windowRadius = 0;

                        double epsilon = 2.0;
                        if (node.Params.TryGetValue("epsilon", out var epText))
                            double.TryParse(epText, out epsilon);
                        if (epsilon < 0) epsilon = 0;

                        double splitGapFactor = 3.0;
                        if (node.Params.TryGetValue("splitGapFactor", out var sgText))
                            double.TryParse(sgText, out splitGapFactor);
                        if (splitGapFactor < 1.2) splitGapFactor = 1.2;

                        int minRegionPoints = 16;
                        if (node.Params.TryGetValue("minRegionPoints", out var mrText))
                            int.TryParse(mrText, out minRegionPoints);
                        if (minRegionPoints < 3) minRegionPoints = 3;

                        var inputBarIds = inputs.TryGetValue("BarIds", out var barObj) ? barObj as int[] : null;

                        Point2D[] FitOneRegion(Point2D[] region)
                        {
                            if (region.Length < 3) return region;
                            switch (mode)
                            {
                                case "moving_avg":
                                    return SmoothPoints(region, windowRadius <= 0 ? 1 : windowRadius);
                                case "simplify":
                                {
                                    var simplified = SimplifyClosedPolyline(region, epsilon <= 0 ? 1.0 : epsilon);
                                    return ResampleClosedPolyline(simplified, region.Length);
                                }
                                case "hybrid":
                                default:
                                {
                                    var simplified = SimplifyClosedPolyline(region, epsilon <= 0 ? 1.0 : epsilon);
                                    var resampled = ResampleClosedPolyline(simplified, region.Length);
                                    return SmoothPoints(resampled, windowRadius <= 0 ? 1 : windowRadius);
                                }
                            }
                        }

                        Point2D[] fitPts;
                        int[] outBarIds;
                        var barRegions = SplitRegionsByBarIds(inPts, inputBarIds ?? Array.Empty<int>(), minRegionPoints);
                        if (barRegions.Count > 0)
                        {
                            var mergedPts = new List<Point2D>(inPts.Length);
                            var mergedIds = new List<int>(inPts.Length);
                            foreach (var (regionPts, regionIds) in barRegions)
                            {
                                var fitted = FitOneRegion(regionPts);
                                mergedPts.AddRange(fitted);
                                if (fitted.Length == regionIds.Length) mergedIds.AddRange(regionIds);
                                else mergedIds.AddRange(Enumerable.Repeat(regionIds.Length > 0 ? regionIds[0] : -1, fitted.Length));
                            }
                            fitPts = mergedPts.ToArray();
                            outBarIds = mergedIds.ToArray();
                        }
                        else
                        {
                            var regions = SplitIntoClosedRegions(inPts, splitGapFactor, minRegionPoints);
                            var merged = new List<Point2D>(inPts.Length);
                            foreach (var region in regions)
                                merged.AddRange(FitOneRegion(region));
                            fitPts = merged.Count == inPts.Length ? merged.ToArray() : FitOneRegion(inPts);
                            outBarIds = inputBarIds != null && inputBarIds.Length == fitPts.Length ? inputBarIds : Array.Empty<int>();
                        }
                        node.Outputs["Out"] = fitPts;
                        node.Outputs["OutBarIds"] = outBarIds;
                        break;
                    }

                    case "verify_mask":
                    {
                        var inPts = inputs["In"] as Point2D[];
                        if (inPts == null) throw new InvalidOperationException("验证: 缺少输入点");
                        var verPts = FilterPointsInImage(inPts, CalibAPI.ImageWidth, CalibAPI.ImageHeight);
                        node.Outputs["Out"] = verPts;
                        break;
                    }

                    case "dedup":
                    {
                        var inPts = inputs["In"] as Point2D[];
                        if (inPts == null) throw new InvalidOperationException("去重: 缺少输入点");
                        var dedupPts = inPts
                            .GroupBy(p => $"{Math.Round(p.X, 3)}_{Math.Round(p.Y, 3)}")
                            .Select(g => g.First())
                            .ToArray();
                        node.Outputs["Out"] = dedupPts;
                        break;
                    }

                    case "convert_output":
                    {
                        var inPts = inputs["In"] as Point2D[];
                        if (inPts == null) throw new InvalidOperationException("输出转换: 缺少输入点");
                        var result = new TrajectoryResult
                        {
                            Success = inPts.Length > 0,
                            Count = inPts.Length,
                            Points = inPts
                        };
                        node.Outputs["Result"] = result;
                        break;
                    }

                    case "draw_color":
                    {
                        var result = inputs["Result"] as TrajectoryResult;
                        if (result == null || result.Points == null || result.Points.Length == 0)
                            throw new InvalidOperationException("绘制彩色: 缺少轨迹结果");
                        var colorImg = new CalibImage(CalibAPI.ImageWidth, CalibAPI.ImageHeight, 3);
                        CalibAPI.DrawTrajectoryColored(colorImg, result.Points, result.BarIds);
                        node.Outputs["Image"] = colorImg;
                        break;
                    }

                    case "detect_circles":
                    {
                        var circleImg = inputs["Image"] as CalibImage;
                        if (circleImg == null) throw new InvalidOperationException("检测圆点: 缺少输入图像");
                        var circles = CalibAPI.DetectCircles(circleImg);
                        node.Outputs["Points"] = circles;
                        break;
                    }

                    case "calibrate":
                    {
                        var imagePts = inputs["ImagePts"] as Point2D[];
                        var worldPts = inputs["WorldPts"] as Point2D[];
                        if (imagePts == null || worldPts == null)
                            throw new InvalidOperationException("标定: 缺少图像点或世界坐标点");
                        if (imagePts.Length != worldPts.Length)
                            throw new InvalidOperationException("标定: 图像点和世界点数量不一致");
                        var calResult = CalibAPI.CalibrateNinePoint(imagePts, worldPts);
                        if (!calResult.Success)
                            throw new InvalidOperationException($"标定失败: {calResult.ErrorMessage}");
                        node.Outputs["Transform"] = calResult.Transform;
                        break;
                    }

                    case "img_to_world":
                    {
                        var pixelPts = inputs["Pixel"] as Point2D[];
                        var transform = inputs["Transform"] as AffineTransform?;
                        if (pixelPts == null || transform == null)
                            throw new InvalidOperationException("坐标转换: 缺少输入点或变换矩阵");
                        CalibAPI.SetTransform(transform.Value);
                        var worldPts2 = pixelPts.Select(p => CalibAPI.ImageToWorld(p, transform.Value)).ToArray();
                        node.Outputs["World"] = worldPts2;
                        break;
                    }

                    case "calibrate_homography":
                    {
                        var imagePts = inputs["ImagePts"] as Point2D[];
                        var worldPts = inputs["WorldPts"] as Point2D[];
                        if (imagePts == null || worldPts == null)
                            throw new InvalidOperationException("透视标定: 缺少图像点或世界坐标点");
                        if (imagePts.Length != worldPts.Length)
                            throw new InvalidOperationException("透视标定: 图像点和世界点数量不一致");
                        if (imagePts.Length < 4)
                            throw new InvalidOperationException("透视标定: 至少需要4个点");
                        var h = FitHomography(imagePts, worldPts);
                        node.Outputs["H"] = h;
                        break;
                    }

                    case "img_to_world_homography":
                    {
                        var pixelPts = inputs["Pixel"] as Point2D[];
                        if (!inputs.TryGetValue("H", out var hObj) || hObj is not HomographyTransform h)
                            throw new InvalidOperationException("坐标转换(H): 缺少H矩阵");
                        if (pixelPts == null)
                            throw new InvalidOperationException("坐标转换(H): 缺少输入点");
                        node.Outputs["World"] = pixelPts.Select(p => ApplyHomography(p, h)).ToArray();
                        break;
                    }

                    case "calibrate_poly2d":
                    {
                        var imagePts = inputs["ImagePts"] as Point2D[];
                        var worldPts = inputs["WorldPts"] as Point2D[];
                        if (imagePts == null || worldPts == null)
                            throw new InvalidOperationException("Poly2D标定: 缺少图像点或世界坐标点");
                        if (imagePts.Length != worldPts.Length)
                            throw new InvalidOperationException("Poly2D标定: 图像点和世界点数量不一致");
                        if (imagePts.Length < 6)
                            throw new InvalidOperationException("Poly2D标定: 至少需要6个点");
                        var poly = FitPoly2D(imagePts, worldPts);
                        node.Outputs["Poly"] = poly;
                        break;
                    }

                    case "img_to_world_poly2d":
                    {
                        var pixelPts = inputs["Pixel"] as Point2D[];
                        if (!inputs.TryGetValue("Poly", out var polyObj) || polyObj is not Poly2DTransform poly)
                            throw new InvalidOperationException("坐标转换(Poly2D): 缺少Poly参数");
                        if (pixelPts == null)
                            throw new InvalidOperationException("坐标转换(Poly2D): 缺少输入点");
                        node.Outputs["World"] = pixelPts.Select(p => ApplyPoly2D(p, poly)).ToArray();
                        break;
                    }

                    case "display_calibration":
                    {
                        string text;
                        if (inputs.TryGetValue("Transform", out var tObj) && tObj is AffineTransform t)
                        {
                            text = $"[Affine]\n{t}";
                        }
                        else if (inputs.TryGetValue("H", out var hObj) && hObj is HomographyTransform h)
                        {
                            text = $"[Homography]\n{FormatHomography(h)}";
                        }
                        else if (inputs.TryGetValue("Poly", out var pObj) && pObj is Poly2DTransform p)
                        {
                            text = $"[Poly2D]\n{FormatPoly2D(p)}";
                        }
                        else
                        {
                            throw new InvalidOperationException("显示标定结果: 未检测到 Transform/H/Poly 输入");
                        }

                        node.Outputs["Out"] = text;
                        node.ResultSummary = text.Replace("\n", " | ");
                        AppendLog(text);
                        Console.WriteLine(text);
                        StatusText.Dispatcher.Invoke(() => { StatusText.Text = text.Replace("\n", "  "); });
                        break;
                    }

                    case "display":
                    {
                        // 显示图像到预览窗口：可选背景图 Img，Image 作为前景层，Points 透明叠加
                        var foregroundImg = (inputs.TryGetValue("Image", out var fgObj) ? fgObj : null) as CalibImage;
                        var backgroundImg = (inputs.TryGetValue("Img", out var bgObj) ? bgObj : null) as CalibImage;
                        if (foregroundImg == null && backgroundImg == null)
                            throw new InvalidOperationException("显示: 缺少输入图像(Image 或 Img)");
                        inputs.TryGetValue("Points", out var ptsObj);
                        Point2D[]? overlayPts = ptsObj as Point2D[];
                        int dotRadius = int.TryParse(node.Params.GetValueOrDefault("dotRadius"), out int r) ? r : 3;
                        ShowImagePreview(foregroundImg, overlayPts, dotRadius, backgroundImg);
                        break;
                    }

                    case "save_image":
                    {
                        var inputImg = inputs["Image"] as CalibImage;
                        if (inputImg == null) throw new InvalidOperationException("保存图像: 缺少输入图像");

                        var pathParam = node.Params.GetValueOrDefault("filePath", "flow_output.bmp");
                        if (string.IsNullOrWhiteSpace(pathParam))
                            pathParam = "flow_output.bmp";
                        var resolvedPath = System.IO.Path.IsPathRooted(pathParam)
                            ? pathParam
                            : System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, pathParam);
                        var dir = System.IO.Path.GetDirectoryName(resolvedPath);
                        if (!string.IsNullOrWhiteSpace(dir))
                            System.IO.Directory.CreateDirectory(dir);

                        var ok = CalibAPI.SaveImage(resolvedPath, inputImg);
                        if (!ok) throw new InvalidOperationException($"保存图像失败: {resolvedPath}");

                        node.Outputs["Out"] = inputImg;
                        node.ResultSummary = $"Saved: {System.IO.Path.GetFileName(resolvedPath)}";
                        break;
                    }

                    case "send_plc":
                    {
                        var pts = inputs["Points"] as Point2D[];
                        if (pts == null || pts.Length == 0)
                            throw new InvalidOperationException("发送PLC: 缺少有效的点位数据");
                        // TODO: 接入 PlcPage 的发送逻辑
                        StatusText.Dispatcher.Invoke(() =>
                        {
                            StatusText.Text = $"轨迹共 {pts.Length} 个点 (PLC发送待接入)";
                        });
                        break;
                    }

                    case "composite":
                    {
                        ExecuteCompositeSubFlow(node, inputs);
                        break;
                    }

                    default:
                        throw new InvalidOperationException($"未知算子: {node.Def.TypeId}");
                }

                node.Executed = true;
                SetNodeStatus(node, false);
                UpdateNodeSummary(node);
            }
            catch (Exception ex)
            {
                node.ErrorMessage = ex.Message;
                node.Executed = false;
                SetNodeStatus(node, false, true);
                throw;
            }
        }

        /// <summary>
        /// 弹窗编辑算子参数
        /// </summary>
        private void EditNodeParams(FlowNode node)
        {
            if (node.Def.Params.Count == 0) return;

            bool compositeUi = node.Def.TypeId == "composite";

            var win = new Window
            {
                Title = $"{node.Def.DisplayName} - 参数设置",
                Width = compositeUi ? 640 : 420,
                Height = compositeUi ? Math.Min(420 + node.Def.Params.Count * 56, 680) : Math.Min(60 + node.Def.Params.Count * 60, 500),
                MinWidth = compositeUi ? 520 : 380,
                MinHeight = compositeUi ? 360 : 200,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = Window.GetWindow(this),
                ResizeMode = compositeUi ? ResizeMode.CanResizeWithGrip : ResizeMode.NoResize
            };

            var root = new DockPanel();

            var fieldsPanel = new StackPanel { Margin = new Thickness(12, 12, 12, 8) };
            var inputs = new Control[node.Def.Params.Count];

            for (int i = 0; i < node.Def.Params.Count; i++)
            {
                var param = node.Def.Params[i];

                var row = new DockPanel { Margin = new Thickness(0, 4, 0, 4) };

                var label = new TextBlock
                {
                    Text = param.DisplayName,
                    Width = 120,
                    VerticalAlignment = VerticalAlignment.Center,
                    FontWeight = FontWeights.Medium
                };

                string currentValue = node.Params.GetValueOrDefault(param.Name, param.DefaultValue);
                Control input;
                if (param.Options != null && param.Options.Count > 0)
                {
                    var cb = new ComboBox
                    {
                        Width = 200,
                        VerticalAlignment = VerticalAlignment.Center,
                        ItemsSource = param.Options
                    };
                    cb.SelectedItem = param.Options.Contains(currentValue) ? currentValue : param.DefaultValue;
                    input = cb;
                }
                else if (compositeUi && (param.Name == "innerFlowJson" || param.Name == "bindingsJson"))
                {
                    input = new TextBox
                    {
                        Width = 460,
                        MinHeight = param.Name == "innerFlowJson" ? 140 : 96,
                        Text = currentValue,
                        TextWrapping = TextWrapping.Wrap,
                        AcceptsReturn = true,
                        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                        VerticalAlignment = VerticalAlignment.Top,
                        FontFamily = new FontFamily("Consolas"),
                        FontSize = 11
                    };
                }
                else
                {
                    var tb = new TextBox
                    {
                        Width = compositeUi ? 380 : 200,
                        Text = currentValue,
                        VerticalAlignment = VerticalAlignment.Center
                    };
                    input = tb;
                }
                inputs[i] = input;

                Button? browseBtn = null;
                if (param.Name == "filePath")
                {
                    browseBtn = new Button
                    {
                        Content = "...",
                        Width = 28,
                        Margin = new Thickness(6, 0, 0, 0),
                        VerticalAlignment = VerticalAlignment.Center
                    };
                    browseBtn.Click += (_, _) =>
                    {
                        var ofd = new OpenFileDialog
                        {
                            Title = "选择文件路径",
                            Filter = node.Def.TypeId == "load_image"
                                ? "图像文件|*.bmp;*.png;*.jpg;*.jpeg;*.tif;*.tiff|所有文件|*.*"
                                : "所有文件|*.*"
                        };
                        if (ofd.ShowDialog() == true && input is TextBox pathBox)
                            pathBox.Text = ofd.FileName;
                    };
                }
                else if (param.Name == "innerFlowPath")
                {
                    browseBtn = new Button
                    {
                        Content = "...",
                        Width = 28,
                        Margin = new Thickness(6, 0, 0, 0),
                        VerticalAlignment = VerticalAlignment.Center
                    };
                    browseBtn.Click += (_, _) =>
                    {
                        var ofd = new OpenFileDialog
                        {
                            Title = "选择子流程 flow.json",
                            Filter = "组态文件|*.flow.json|所有文件|*.*"
                        };
                        if (ofd.ShowDialog() == true && input is TextBox pathBox)
                            pathBox.Text = ofd.FileName;
                    };
                }

                var tip = new TextBlock
                {
                    Text = param.Description,
                    Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)),
                    FontSize = 9,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(8, 0, 0, 0)
                };

                row.Children.Add(label);
                row.Children.Add(input);
                if (browseBtn != null) row.Children.Add(browseBtn);
                row.Children.Add(tip);
                fieldsPanel.Children.Add(row);
            }

            var scroll = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Content = fieldsPanel
            };

            // 底部固定：保存 / 取消（避免内容过高时按钮被挤出可视区域）
            var btnPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(12, 8, 12, 12)
            };
            string okLabel = compositeUi ? "保存" : "确定";
            var btnOk = new Button { Content = okLabel, Width = 88, Margin = new Thickness(0, 0, 8, 0), IsDefault = true };
            var btnCancel = new Button { Content = "取消", Width = 80, IsCancel = true };

            btnOk.Click += (_, _) =>
            {
                for (int i = 0; i < node.Def.Params.Count; i++)
                {
                    string value = inputs[i] switch
                    {
                        ComboBox cb => cb.SelectedItem?.ToString() ?? node.Def.Params[i].DefaultValue,
                        TextBox tb => tb.Text,
                        _ => node.Def.Params[i].DefaultValue
                    };
                    node.Params[node.Def.Params[i].Name] = value;
                }
                win.DialogResult = true;
                win.Close();
            };
            btnCancel.Click += (_, _) =>
            {
                win.DialogResult = false;
                win.Close();
            };

            btnPanel.Children.Add(btnOk);
            btnPanel.Children.Add(btnCancel);
            DockPanel.SetDock(btnPanel, Dock.Bottom);
            root.Children.Add(btnPanel);
            root.Children.Add(scroll);

            win.Content = root;
            win.ShowDialog();
        }

        /// <summary>
        /// 更新节点上的结果摘要文本
        /// </summary>
        private void UpdateNodeSummary(FlowNode node)
        {
            // 构建摘要
            string? summary = null;

            foreach (var kv in node.Outputs)
            {
                if (kv.Value == null) continue;

                if (kv.Value is Point2D[] pts)
                {
                    summary = $"{kv.Key}: {pts.Length} 个点";
                    break;
                }
                else if (kv.Value is int count)
                {
                    summary = $"{kv.Key}: {count}";
                    break;
                }
                else if (kv.Value is CalibImage ci)
                {
                    summary = $"{kv.Key}: {ci.Width}x{ci.Height}";
                }
            }

            node.ResultSummary = summary;

            // 更新 UI
            if (node.Visual is Border border)
            {
                border.Dispatcher.Invoke(() =>
                {
                    if (border.Child is StackPanel panel)
                    {
                        foreach (var child in panel.Children)
                        {
                            if (child is TextBlock tb && (string)tb.Tag == "SummaryText")
                            {
                                tb.Text = summary ?? "";
                                break;
                            }
                        }
                    }
                });
            }
        }

        /// <summary>
        /// 弹窗输入九点世界坐标（X,Y 两个 TextBox × 9行）
        /// </summary>
        private Point2D[]? InputWorldCoordinates()
        {
            var points = new Point2D[9];
            bool confirmed = false;

            var win = new Window
            {
                Title = "输入九点标定的世界坐标",
                Width = 500,
                Height = 480,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = Window.GetWindow(this),
                ResizeMode = ResizeMode.NoResize
            };

            var sp = new StackPanel { Margin = new Thickness(10) };

            // 标题说明
            sp.Children.Add(new TextBlock
            {
                Text = "请输入 9 个标定点的世界坐标（X, Y）:",
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 8)
            });

            var txts = new TextBox[9, 2];
            // 表头
            var headerPanel = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
            headerPanel.Children.Add(new TextBlock { Text = "序号", Width = 50, FontWeight = FontWeights.Bold });
            headerPanel.Children.Add(new TextBlock { Text = "X", Width = 180, FontWeight = FontWeights.Bold, TextAlignment = TextAlignment.Center });
            headerPanel.Children.Add(new TextBlock { Text = "Y", Width = 180, FontWeight = FontWeights.Bold, TextAlignment = TextAlignment.Center });
            sp.Children.Add(headerPanel);

            for (int i = 0; i < 9; i++)
            {
                var row = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };
                row.Children.Add(new TextBlock { Text = $"  {i + 1}", Width = 50, VerticalAlignment = VerticalAlignment.Center });
                var tbx = new TextBox { Width = 180, Text = "0" };
                var tby = new TextBox { Width = 180, Text = "0" };
                txts[i, 0] = tbx;
                txts[i, 1] = tby;
                row.Children.Add(tbx);
                row.Children.Add(tby);
                sp.Children.Add(row);
            }

            // 按钮区
            var btnPanel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
            var btnOk = new Button { Content = "确定", Width = 80, Margin = new Thickness(0, 0, 8, 0) };
            var btnCancel = new Button { Content = "取消", Width = 80 };

            btnOk.Click += (_, _) =>
            {
                for (int i = 0; i < 9; i++)
                {
                    if (!double.TryParse(txts[i, 0].Text, out double x) || !double.TryParse(txts[i, 1].Text, out double y))
                    {
                        MessageBox.Show($"第 {i + 1} 个点的坐标格式不正确，请输入数值", "输入错误",
                            MessageBoxButton.OK, MessageBoxImage.Warning);
                        return;
                    }
                    points[i] = new Point2D(x, y);
                }
                confirmed = true;
                win.Close();
            };
            btnCancel.Click += (_, _) => win.Close();

            btnPanel.Children.Add(btnOk);
            btnPanel.Children.Add(btnCancel);
            sp.Children.Add(btnPanel);

            win.Content = sp;
            win.ShowDialog();

            return confirmed ? points : null;
        }

        /// <summary>
        /// 显示图像预览窗口，支持缩放和平移，可选叠加点位
        /// 滚轮缩放，左键拖拽平移，右键/F 适应窗口，1 重置100%
        /// </summary>
        private void ShowImagePreview(CalibImage? img, Point2D[]? overlayPoints = null, int dotRadius = 3, CalibImage? backgroundImg = null)
        {
            var baseSource = backgroundImg ?? img;
            if (baseSource == null) return;
            var bmp = baseSource.ToBitmap();
            if (bmp == null) return;

            // 如果有前景图且存在背景图，使用半透明叠加前景
            if (backgroundImg != null && img != null)
            {
                using var fgBmpRaw = img.ToBitmap();
                if (fgBmpRaw != null)
                {
                    var drawBmp = new System.Drawing.Bitmap(bmp.Width, bmp.Height, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
                    using (var gTemp = System.Drawing.Graphics.FromImage(drawBmp))
                    {
                        gTemp.DrawImage(bmp, 0, 0);
                        var attr = new System.Drawing.Imaging.ImageAttributes();
                        var matrix = new System.Drawing.Imaging.ColorMatrix
                        {
                            Matrix33 = 0.45f // 前景半透明
                        };
                        attr.SetColorMatrix(matrix, System.Drawing.Imaging.ColorMatrixFlag.Default, System.Drawing.Imaging.ColorAdjustType.Bitmap);
                        gTemp.DrawImage(fgBmpRaw,
                            new System.Drawing.Rectangle(0, 0, drawBmp.Width, drawBmp.Height),
                            0, 0, fgBmpRaw.Width, fgBmpRaw.Height,
                            System.Drawing.GraphicsUnit.Pixel, attr);
                    }
                    bmp.Dispose();
                    bmp = drawBmp;
                }
            }

            // 如果有叠加点位，在 bitmap 上画点
            if (overlayPoints != null && overlayPoints.Length > 0)
            {
                // Graphics.FromImage 不支持索引像素格式，先转为 24bppRgb
                var drawBmp = new System.Drawing.Bitmap(bmp.Width, bmp.Height, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
                using (var gTemp = System.Drawing.Graphics.FromImage(drawBmp))
                {
                    gTemp.DrawImage(bmp, 0, 0);
                }
                bmp.Dispose();
                bmp = drawBmp;

                using (var g = System.Drawing.Graphics.FromImage(bmp))
                {
                    g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
                    using var pointPen = new System.Drawing.Pen(System.Drawing.Color.FromArgb(140, 0, 255, 0), 1.2f);
                    for (int i = 0; i < overlayPoints.Length; i++)
                    {
                        var p = overlayPoints[i];
                        int cx = (int)p.X, cy = (int)p.Y;
                        int r = dotRadius;
                        g.DrawEllipse(pointPen, cx - r, cy - r, r * 2, r * 2);
                        g.DrawLine(pointPen, cx - r - 1, cy, cx + r + 1, cy);
                        g.DrawLine(pointPen, cx, cy - r - 1, cx, cy + r + 1);
                    }
                }
            }

            IntPtr hBitmap = bmp.GetHbitmap();
            var bitmapSource = System.Windows.Interop.Imaging.CreateBitmapSourceFromHBitmap(
                hBitmap, IntPtr.Zero, Int32Rect.Empty,
                System.Windows.Media.Imaging.BitmapSizeOptions.FromWidthAndHeight(baseSource.Width, baseSource.Height));
            bitmapSource.Freeze();
            DeleteObject(hBitmap);
            bmp.Dispose();

            string previewTitle = overlayPoints != null ? $"图像预览 ({overlayPoints.Length} 个点)" : "图像预览";
            if (_livePreviewWindow != null && _livePreviewWindow.IsVisible && _livePreviewImageCtrl != null)
            {
                _livePreviewWindow.Title = previewTitle;
                _livePreviewImageCtrl.Source = bitmapSource;
                return;
            }

            var win = new Window
            {
                Title = previewTitle,
                Width = Math.Min(baseSource.Width + 40, 1400),
                Height = Math.Min(baseSource.Height + 60, 950),
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = Window.GetWindow(this),
                Background = SystemColors.ControlDarkDarkBrush
            };

            // ---- 缩放/平移核心 ----
            var scaleTransform = new ScaleTransform(1.0, 1.0);
            var translateTransform = new TranslateTransform(0, 0);
            var transformGroup = new TransformGroup();
            transformGroup.Children.Add(scaleTransform);
            transformGroup.Children.Add(translateTransform);

            // 外层：剪裁区域（防止图像超出窗口）
            var border = new Border { ClipToBounds = true };
            // 内层：承载图像，应用变换
            var canvas = new System.Windows.Controls.Canvas { RenderTransform = transformGroup, RenderTransformOrigin = new Point(0, 0) };
            var imageCtrl = new System.Windows.Controls.Image { Source = bitmapSource, Width = baseSource.Width, Height = baseSource.Height };
            _livePreviewImageCtrl = imageCtrl;
            canvas.Children.Add(imageCtrl);
            border.Child = canvas;

            // 缩放比例标签
            var zoomText = new TextBlock
            {
                Text = "100%",
                Foreground = Brushes.White,
                FontSize = 14,
                FontWeight = FontWeights.Bold,
                Padding = new Thickness(8, 4, 8, 4),
                Background = new SolidColorBrush(Color.FromArgb(160, 0, 0, 0)),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top
            };

            var rootPanel = new Grid();
            rootPanel.Children.Add(border);
            rootPanel.Children.Add(zoomText);

            win.Content = rootPanel;

            // ---- 状态 ----
            bool isPanning = false;
            Point panStart = default;
            double panOffsetX0 = 0, panOffsetY0 = 0;

            // ---- 鼠标滚轮缩放（以鼠标位置为中心） ----
            border.MouseWheel += (_, e) =>
            {
                var pos = e.GetPosition(border);
                double factor = e.Delta > 0 ? 1.15 : 1.0 / 1.15;
                double newScale = scaleTransform.ScaleX * factor;
                newScale = Math.Max(0.05, Math.Min(newScale, 50.0));

                // 以鼠标位置为锚点缩放
                translateTransform.X = pos.X - (pos.X - translateTransform.X) * (newScale / scaleTransform.ScaleX);
                translateTransform.Y = pos.Y - (pos.Y - translateTransform.Y) * (newScale / scaleTransform.ScaleY);
                scaleTransform.ScaleX = newScale;
                scaleTransform.ScaleY = newScale;

                zoomText.Text = $"{(int)(newScale * 100)}%";
            };

            // ---- 左键拖拽平移 ----
            border.MouseLeftButtonDown += (_, e) =>
            {
                if (e.LeftButton == MouseButtonState.Pressed)
                {
                    isPanning = true;
                    panStart = e.GetPosition(border);
                    panOffsetX0 = translateTransform.X;
                    panOffsetY0 = translateTransform.Y;
                    border.Cursor = Cursors.ScrollAll;
                    border.CaptureMouse();
                    e.Handled = true;
                }
            };
            border.MouseMove += (_, e) =>
            {
                if (isPanning)
                {
                    var cur = e.GetPosition(border);
                    translateTransform.X = panOffsetX0 + (cur.X - panStart.X);
                    translateTransform.Y = panOffsetY0 + (cur.Y - panStart.Y);
                }
            };
            border.MouseLeftButtonUp += (_, e) =>
            {
                if (isPanning)
                {
                    isPanning = false;
                    border.ReleaseMouseCapture();
                    border.Cursor = null;
                }
            };

            // ---- 中键/右键双击 适应窗口 ----
            void FitToView()
            {
                double scaleX = border.ActualWidth / baseSource.Width;
                double scaleY = border.ActualHeight / baseSource.Height;
                double fitScale = Math.Min(scaleX, scaleY) * 0.95;
                fitScale = Math.Max(fitScale, 0.05);
                scaleTransform.ScaleX = fitScale;
                scaleTransform.ScaleY = fitScale;
                double offsetX = (border.ActualWidth - baseSource.Width * fitScale) / 2;
                double offsetY = (border.ActualHeight - baseSource.Height * fitScale) / 2;
                translateTransform.X = offsetX;
                translateTransform.Y = offsetY;
                zoomText.Text = $"{(int)(fitScale * 100)}%";
            }

            border.MouseRightButtonDown += (_, e) => { e.Handled = true; };

            // 键盘快捷键
            win.KeyDown += (_, e) =>
            {
                if (e.Key == Key.F)
                {
                    FitToView();
                    e.Handled = true;
                }
                else if (e.Key == Key.D1 || e.Key == Key.NumPad1)
                {
                    scaleTransform.ScaleX = 1.0;
                    scaleTransform.ScaleY = 1.0;
                    translateTransform.X = 0;
                    translateTransform.Y = 0;
                    zoomText.Text = "100%";
                    e.Handled = true;
                }
                else if (e.Key == Key.OemPlus || e.Key == Key.Add)
                {
                    double newScale = Math.Min(scaleTransform.ScaleX * 1.2, 50.0);
                    double cx = border.ActualWidth / 2, cy = border.ActualHeight / 2;
                    translateTransform.X = cx - (cx - translateTransform.X) * (newScale / scaleTransform.ScaleX);
                    translateTransform.Y = cy - (cy - translateTransform.Y) * (newScale / scaleTransform.ScaleY);
                    scaleTransform.ScaleX = scaleTransform.ScaleY = newScale;
                    zoomText.Text = $"{(int)(newScale * 100)}%";
                    e.Handled = true;
                }
                else if (e.Key == Key.OemMinus || e.Key == Key.Subtract)
                {
                    double newScale = Math.Max(scaleTransform.ScaleX / 1.2, 0.05);
                    double cx = border.ActualWidth / 2, cy = border.ActualHeight / 2;
                    translateTransform.X = cx - (cx - translateTransform.X) * (newScale / scaleTransform.ScaleX);
                    translateTransform.Y = cy - (cy - translateTransform.Y) * (newScale / scaleTransform.ScaleY);
                    scaleTransform.ScaleX = scaleTransform.ScaleY = newScale;
                    zoomText.Text = $"{(int)(newScale * 100)}%";
                    e.Handled = true;
                }
            };

            // 窗口打开后自适应
            win.ContentRendered += (_, _) => FitToView();
            win.Closed += (_, _) =>
            {
                _livePreviewWindow = null;
                _livePreviewImageCtrl = null;
            };

            _livePreviewWindow = win;
            win.Show();
        }

        private void ShowContoursPreview(ValueTuple<int[], int[], int[], int> contourData, CalibImage? baseImg = null)
        {
            var (flatX, flatY, lengths, contourCount) = contourData;
            if (contourCount <= 0 || flatX == null || flatY == null || lengths == null)
            {
                MessageBox.Show("Contours 为空，无法预览。", "轮廓预览", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            System.Drawing.Bitmap bmp;
            if (baseImg != null)
            {
                bmp = baseImg.ToBitmap() ?? new System.Drawing.Bitmap(Math.Max(1, CalibAPI.ImageWidth), Math.Max(1, CalibAPI.ImageHeight), System.Drawing.Imaging.PixelFormat.Format24bppRgb);
            }
            else
            {
                int maxX = flatX.Length > 0 ? flatX.Max() : 0;
                int maxY = flatY.Length > 0 ? flatY.Max() : 0;
                int w = Math.Max(1, maxX + 20);
                int h = Math.Max(1, maxY + 20);
                bmp = new System.Drawing.Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
                using var g0 = System.Drawing.Graphics.FromImage(bmp);
                g0.Clear(System.Drawing.Color.Black);
            }

            // Graphics.FromImage 不支持索引像素格式，统一转 24bpp 以避免崩溃
            if (bmp.PixelFormat != System.Drawing.Imaging.PixelFormat.Format24bppRgb)
            {
                var converted = new System.Drawing.Bitmap(bmp.Width, bmp.Height, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
                using (var g0 = System.Drawing.Graphics.FromImage(converted))
                    g0.DrawImage(bmp, 0, 0);
                bmp.Dispose();
                bmp = converted;
            }

            using (var g = System.Drawing.Graphics.FromImage(bmp))
            {
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                int offset = 0;
                for (int ci = 0; ci < contourCount && ci < lengths.Length; ci++)
                {
                    int len = lengths[ci];
                    if (len < 2 || offset + len > flatX.Length || offset + len > flatY.Length)
                    {
                        offset += Math.Max(0, len);
                        continue;
                    }

                    var pts = new System.Drawing.Point[len + 1];
                    for (int i = 0; i < len; i++)
                        pts[i] = new System.Drawing.Point(flatX[offset + i], flatY[offset + i]);
                    pts[len] = pts[0];

                    var color = System.Drawing.Color.FromArgb(220, (ci * 53) % 256, (ci * 97 + 80) % 256, (ci * 151 + 40) % 256);
                    using var pen = new System.Drawing.Pen(color, 1.4f);
                    g.DrawLines(pen, pts);
                    offset += len;
                }
            }

            IntPtr hBitmap = bmp.GetHbitmap();
            var bitmapSource = System.Windows.Interop.Imaging.CreateBitmapSourceFromHBitmap(
                hBitmap, IntPtr.Zero, Int32Rect.Empty,
                System.Windows.Media.Imaging.BitmapSizeOptions.FromWidthAndHeight(bmp.Width, bmp.Height));

            var win = new Window
            {
                Title = $"轮廓预览 ({contourCount} 条)",
                Width = Math.Min(bmp.Width + 40, 1400),
                Height = Math.Min(bmp.Height + 60, 950),
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = Window.GetWindow(this),
                Background = SystemColors.ControlDarkDarkBrush
            };
            var imageCtrl = new System.Windows.Controls.Image
            {
                Source = bitmapSource,
                Width = bmp.Width,
                Height = bmp.Height,
                Stretch = Stretch.None,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            var hostGrid = new Grid();
            hostGrid.Children.Add(imageCtrl);
            var scroll = new ScrollViewer
            {
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = hostGrid
            };
            win.Content = scroll;
            win.Closed += (_, _) =>
            {
                DeleteObject(hBitmap);
                bmp.Dispose();
            };
            win.Show();
        }

        [DllImport("gdi32.dll")]
        private static extern bool DeleteObject(IntPtr hObject);

        public async System.Threading.Tasks.Task<bool> RunAllAsync(bool clearLog = true, bool preferNativeEngine = false)
        {
            if (_isRunInProgress)
            {
                AppendLog("[WARN] 已有执行在进行中");
                return false;
            }

            _isRunInProgress = true;
            _runCts?.Cancel();
            _runCts?.Dispose();
            _runCts = new System.Threading.CancellationTokenSource();
            if (StopRunButton != null) StopRunButton.IsEnabled = true;
            StatusText.Text = "运行中...";
            StatusText.Foreground = new SolidColorBrush(Colors.Orange);
            if (clearLog) LogBox.Text = "";
            AppendLog("========== 开始执行 ==========");

            // 清除所有节点的执行状态
            foreach (var n in _nodes)
            {
                n.Executed = false;
                n.ErrorMessage = null;
                n.Outputs.Clear();
                SetNodeStatus(n, false);
            }

            try
            {
                ThrowIfExecutionCancelled();
                if (preferNativeEngine)
                {
                    // C++ 原生流程引擎：后台执行优先走 native；UI 保留托管执行保证交互输出可用
                    var flowData = BuildCurrentFlowData();
                    var flowJson = JsonSerializer.Serialize(flowData, new JsonSerializerOptions { WriteIndented = false });
                    await System.Threading.Tasks.Task.Yield();
                    ThrowIfExecutionCancelled();

                    using var engine = new NativeFlowEngine();
                    engine.LoadFromJson(flowJson);
                    var run = engine.Run();

                    if (run.Success)
                    {
                        foreach (var n in _nodes)
                        {
                            n.Executed = true;
                            n.ErrorMessage = null;
                            SetNodeStatus(n, false);
                        }
                        StatusText.Text = $"执行完成: {run.ExecutedNodes}/{run.TotalNodes} 个节点成功";
                        StatusText.Foreground = new SolidColorBrush(Colors.LightGreen);
                        AppendLog($"[NATIVE] 执行成功: {run.ExecutedNodes}/{run.TotalNodes}");
                        if (!string.IsNullOrWhiteSpace(run.ReportJson))
                            AppendLog($"[NATIVE] Report: {run.ReportJson}");
                        AppendLog("========== 执行完成 ==========");
                        return true;
                    }

                    StatusText.Text = $"Native执行失败，回退托管: {run.ErrorMessage}";
                    StatusText.Foreground = new SolidColorBrush(Colors.OrangeRed);
                    AppendLog($"[NATIVE][ERROR] {run.ErrorMessage}", true);
                    if (!string.IsNullOrWhiteSpace(run.ReportJson))
                        AppendLog($"[NATIVE] Report: {run.ReportJson}");
                    AppendLog("[NATIVE] 回退托管执行...");
                    return await RunAllManagedFallbackAsync();
                }

                // 交互式 UI 默认走托管执行：保证 display/连线点击查看数据可用
                return await RunAllManagedFallbackAsync();
            }
            catch (OperationCanceledException)
            {
                StatusText.Text = "执行已停止";
                StatusText.Foreground = new SolidColorBrush(Colors.OrangeRed);
                AppendLog("[STOP] 用户中断执行");
                AppendLog("========== 执行中止 ==========");
                return false;
            }
            catch (Exception ex)
            {
                AppendLog($"[WARN] 执行异常，回退到托管执行: {ex.Message}");
                return await RunAllManagedFallbackAsync();
            }
            finally
            {
                _isRunInProgress = false;
                if (StopRunButton != null) StopRunButton.IsEnabled = false;
            }
        }

        private async System.Threading.Tasks.Task<bool> RunAllManagedFallbackAsync()
        {
            try
            {
                var sorted = TopologicalSort();
                if (sorted.Count != _nodes.Count)
                {
                    StatusText.Text = "错误: 检测到循环依赖!";
                    StatusText.Foreground = new SolidColorBrush(Colors.Red);
                    AppendLog("[ERROR] 检测到循环依赖，无法执行!", true);
                    return false;
                }

                var perFrameLoops = sorted
                    .Where(n => n.Def.TypeId == "camera_loop" &&
                                string.Equals((n.Params.GetValueOrDefault("mode", "last_only") ?? "last_only").Trim(),
                                    "per_frame", StringComparison.OrdinalIgnoreCase))
                    .ToList();

                if (perFrameLoops.Count > 1)
                {
                    StatusText.Text = "执行失败: 当前仅支持一个 per_frame camera_loop 节点";
                    StatusText.Foreground = new SolidColorBrush(Colors.Red);
                    AppendLog("[ERROR] 检测到多个 per_frame camera_loop，当前版本仅支持一个", true);
                    return false;
                }

                if (perFrameLoops.Count == 1)
                {
                    var loopNode = perFrameLoops[0];
                    var downstream = GetDownstreamNodes(loopNode);
                    var preNodes = sorted.Where(n => !downstream.Contains(n)).ToList();
                    var postNodes = sorted.Where(n => downstream.Contains(n) && n != loopNode).ToList();

                    AppendLog($"检测到 per_frame: {loopNode.Def.DisplayName}，前置 {preNodes.Count} 节点，下游 {postNodes.Count} 节点");

                    int successCountPre = 0;
                    for (int i = 0; i < preNodes.Count; i++)
                    {
                    ThrowIfExecutionCancelled();
                        var node = preNodes[i];
                        StatusText.Text = $"执行前置 [{i + 1}/{preNodes.Count}] {node.Def.DisplayName}...";
                        AppendLog($"[PRE {i + 1}/{preNodes.Count}] 执行: {node.Def.DisplayName}");
                        await System.Threading.Tasks.Task.Yield();
                        ExecuteNode(node);
                        successCountPre++;
                    }

                    int deviceIndex = int.TryParse(loopNode.Params.GetValueOrDefault("deviceIndex"), out var di) ? di : 0;
                    int frameCount = int.TryParse(loopNode.Params.GetValueOrDefault("frameCount"), out var fc) ? fc : 10;
                    int intervalMs = int.TryParse(loopNode.Params.GetValueOrDefault("intervalMs"), out var im) ? im : 100;
                    int targetWidth = int.TryParse(loopNode.Params.GetValueOrDefault("targetWidth"), out var tw) ? tw : 0;
                    int targetHeight = int.TryParse(loopNode.Params.GetValueOrDefault("targetHeight"), out var th) ? th : 0;

                    frameCount = Math.Max(1, frameCount);
                    int okFrames = 0;
                    using var cam = new CameraService();
                    if (!cam.ConnectByIndex(deviceIndex))
                        throw new InvalidOperationException($"相机连接失败，deviceIndex={deviceIndex}");

                    for (int fi = 0; fi < frameCount; fi++)
                    {
                        ThrowIfExecutionCancelled();
                        var frame = cam.GrabOneFrame(targetWidth, targetHeight);
                        if (frame == null)
                        {
                            AppendLog($"[FRAME {fi + 1}/{frameCount}] 抓帧失败: {cam.LastError ?? "未知错误"}", true);
                            if (intervalMs > 0 && fi < frameCount - 1)
                                await System.Threading.Tasks.Task.Delay(intervalMs, _runCts?.Token ?? System.Threading.CancellationToken.None);
                            continue;
                        }
                        okFrames++;
                        loopNode.Outputs.Clear();
                        loopNode.Outputs["Image"] = frame;
                        loopNode.Outputs["Count"] = okFrames;
                        loopNode.Executed = true;
                        loopNode.ErrorMessage = null;
                        SetNodeStatus(loopNode, false);
                        loopNode.ResultSummary = $"per_frame {okFrames}/{frameCount}";
                        UpdateNodeSummary(loopNode);

                        AppendLog($"[FRAME {fi + 1}/{frameCount}] 开始");
                        for (int j = 0; j < postNodes.Count; j++)
                        {
                            ThrowIfExecutionCancelled();
                            var node = postNodes[j];
                            node.Outputs.Clear();
                            node.ErrorMessage = null;
                            node.Executed = false;
                            StatusText.Text = $"Frame[{fi + 1}/{frameCount}] 执行 [{j + 1}/{postNodes.Count}] {node.Def.DisplayName}...";
                            await System.Threading.Tasks.Task.Yield();
                            ExecuteNode(node);
                        }

                        if (intervalMs > 0 && fi < frameCount - 1)
                            await System.Threading.Tasks.Task.Delay(intervalMs, _runCts?.Token ?? System.Threading.CancellationToken.None);
                    }

                    if (okFrames <= 0)
                        throw new InvalidOperationException("per_frame 未抓到任何有效帧");

                    StatusText.Text = $"per_frame 执行完成: 前置 {successCountPre}/{preNodes.Count}, 有效帧 {okFrames}/{frameCount}";
                    StatusText.Foreground = new SolidColorBrush(Colors.LightGreen);
                    AppendLog($"========== per_frame 执行完成: frames={okFrames}/{frameCount} ==========");
                    return true;
                }

                AppendLog($"共 {sorted.Count} 个节点待执行");
                int successCount = 0;
                for (int i = 0; i < sorted.Count; i++)
                {
                    ThrowIfExecutionCancelled();
                    var node = sorted[i];
                    StatusText.Text = $"执行 [{i + 1}/{sorted.Count}] {node.Def.DisplayName}...";
                    AppendLog($"[{i + 1}/{sorted.Count}] 执行: {node.Def.DisplayName}");
                    await System.Threading.Tasks.Task.Yield();
                    try
                    {
                        ExecuteNode(node);
                        successCount++;
                        AppendLog($"  -> OK: {node.Def.DisplayName}");
                    }
                    catch (Exception ex)
                    {
                        AppendLog($"  -> [ERROR] {node.Def.DisplayName}: {ex.Message}", true);
                        StatusText.Text = $"执行失败: {node.Def.DisplayName} - {ex.Message}";
                        StatusText.Foreground = new SolidColorBrush(Colors.Red);
                        AppendLog("========== 执行中止 ==========");
                        return false;
                    }
                }
                StatusText.Text = $"执行完成(托管回退): {successCount}/{sorted.Count} 个节点成功";
                StatusText.Foreground = new SolidColorBrush(Colors.LightGreen);
                AppendLog($"========== 执行完成(托管回退): {successCount}/{sorted.Count} ==========");
                return true;
            }
            catch (Exception ex)
            {
                StatusText.Text = $"执行失败: {ex.Message}";
                StatusText.Foreground = new SolidColorBrush(Colors.Red);
                AppendLog($"[ERROR] 托管回退执行失败: {ex.Message}", true);
                AppendLog("========== 执行中止 ==========");
                return false;
            }
        }

        private async void RunAll_Click(object sender, RoutedEventArgs e)
        {
            await RunAllAsync(clearLog: true, preferNativeEngine: false);
        }

        private void StopRun_Click(object sender, RoutedEventArgs e)
        {
            if (_isRunInProgress && _runCts != null && !_runCts.IsCancellationRequested)
            {
                _runCts.Cancel();
                AppendLog("[STOP] 已请求停止...");
                StatusText.Text = "正在停止...";
                StatusText.Foreground = new SolidColorBrush(Colors.OrangeRed);
            }
        }
    }
}
