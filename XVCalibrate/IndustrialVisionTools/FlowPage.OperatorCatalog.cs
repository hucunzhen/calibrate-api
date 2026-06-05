// 流程编辑器：算子端口/参数/类型定义与 OperatorRegistry 静态注册表（由 FlowPage.xaml.cs 拆分）。

using System;
using System.Diagnostics;
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
using HslCommunication.ModBus;
#if HALCON_ENABLED
using HalconDotNet;
#endif

namespace CalibOperatorCLI_Example
{
    public partial class FlowPage : UserControl
    {
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
            public bool IsOptional { get; set; }
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
                Description = "从文件加载图像。可选 After 输入：接在上游算子 Out 之后，先执行上游再加载；Out 透传 After 供下游继续。",
                Category = "输入",
                Params =
                {
                    new OperatorParam
                    {
                        Name = "filePath",
                        DisplayName = "图像路径",
                        DefaultValue = "",
                        Description = "可选；填写后自动加载（相对路径相对当前 .flow.json 目录）；留空则弹窗选择"
                    }
                },
                Ports =
                {
                    new PortDef { Name = "After", Direction = PortDirection.Input, DataType = typeof(object), ColorHex = "#607D8B", IsOptional = true },
                    new PortDef { Name = "Image", Direction = PortDirection.Output, DataType = typeof(CalibImage), ColorHex = "#4CAF50" },
                    new PortDef { Name = "Out", Direction = PortDirection.Output, DataType = typeof(object), ColorHex = "#607D8B", IsOptional = true }
                }
            },
            new OperatorDef
            {
                TypeId = "load_image_dir",
                DisplayName = "加载图像目录",
                Description = "扫描目录下图像（非递归），按文件名排序；each=运行流程时对每张图各驱动下游执行一遍；single=始终只加载序号所指的一张",
                Category = "输入",
                Params =
                {
                    new OperatorParam
                    {
                        Name = "mode",
                        DisplayName = "模式",
                        DefaultValue = "each",
                        Description = "each=「运行」时对目录内每张图像循环执行下游（托管与 Native 均支持）；single=仅加载一张",
                        Options = new List<string> { "each", "single" }
                    },
                    new OperatorParam
                    {
                        Name = "directory",
                        DisplayName = "目录路径",
                        DefaultValue = "",
                        Description = "相对当前 .flow.json 目录；留空时在界面线程弹出选文件夹"
                    },
                    new OperatorParam
                    {
                        Name = "extensions",
                        DisplayName = "扩展名",
                        DefaultValue = ".bmp;.png;.jpg;.jpeg;.tif;.tiff",
                        Description = "分号分隔；可写 .png 或 png；也可用 *.png"
                    },
                    new OperatorParam
                    {
                        Name = "index",
                        DisplayName = "序号",
                        DefaultValue = "0",
                        Description = "single 模式或单节点调试时：排序后的文件索引（从 0 起）；each 模式下「运行」时忽略此项"
                    }
                },
                Ports =
                {
                    new PortDef { Name = "Image", Direction = PortDirection.Output, DataType = typeof(CalibImage), ColorHex = "#4CAF50" },
                    new PortDef { Name = "Count", Direction = PortDirection.Output, DataType = typeof(int), ColorHex = "#607D8B" },
                    new PortDef { Name = "Path", Direction = PortDirection.Output, DataType = typeof(string), ColorHex = "#9CCC65" }
                }
            },
            new OperatorDef
            {
                TypeId = "jit_sample",
                DisplayName = "JiT采样",
                Description =
                    "Just Image Transformers（Li&He / flow-matching，像素空间 ViT）ImageNet 类条件采样；需克隆 jkyl/just-image-transformer、uv sync、下载权重 npz；GPU Ampere+",
                Category = "AI模型",
                Params =
                {
                    new OperatorParam { Name = "pythonPath", DisplayName = "Python", DefaultValue = "python", Description = "已安装该 JiT 仓库依赖的解释器（常用仓库内 .venv）" },
                    new OperatorParam { Name = "launcherScript", DisplayName = "启动脚本", DefaultValue = "JIT_Inference/jit_calibrate_launcher.py", Description = "本仓库内 launcher；路径解析同 ONNX" },
                    new OperatorParam { Name = "jitRepoRoot", DisplayName = "JiT仓库根目录", DefaultValue = "just-image-transformer", Description = "克隆的 just-image-transformer 根路径（相对 flow 目录，否则向上查找源码树）" },
                    new OperatorParam { Name = "configYaml", DisplayName = "配置YAML", DefaultValue = "config/jit_L_32.yaml", Description = "相对 JiT 仓库根，如 config/jit_L_32.yaml" },
                    new OperatorParam
                    {
                        Name = "checkpointPath",
                        DisplayName = "权重 npz 或 zip",
                        DefaultValue = "",
                        Description = "model.npz，或官方 jit_L_32_ckpt_442k.zip（内含 model.npz）；launcher 会自动解压 zip"
                    },
                    new OperatorParam { Name = "seed", DisplayName = "随机种子", DefaultValue = "555", Description = "噪声初始化" },
                    new OperatorParam { Name = "label", DisplayName = "ImageNet类别", DefaultValue = "123", Description = "类条件标签 0~999" },
                    new OperatorParam { Name = "cfgStrength", DisplayName = "CFG强度", DefaultValue = "3.0", Description = "classifier-free guidance" },
                    new OperatorParam { Name = "numSteps", DisplayName = "采样步数", DefaultValue = "50", Description = "越大越慢" },
                    new OperatorParam
                    {
                        Name = "schedule",
                        DisplayName = "时间步调度",
                        DefaultValue = "linear",
                        Description = "linear | logit_normal",
                        Options = new List<string> { "linear", "logit_normal" }
                    },
                    new OperatorParam { Name = "timeoutSec", DisplayName = "超时(秒)", DefaultValue = "3600", Description = "整段推理超时，至少 120" }
                },
                Ports = { new PortDef { Name = "Image", Direction = PortDirection.Output, DataType = typeof(CalibImage), ColorHex = "#4CAF50" } }
            },
            new OperatorDef
            {
                TypeId = "camera_snap",
                DisplayName = "相机取一帧",
                Description = "从相机抓取单帧图像。上游算子 Out → After 可排在任意算子之后取图；Out 透传 After 供下游继续。",
                Category = "输入",
                Params =
                {
                    new OperatorParam { Name = "deviceIndex", DisplayName = "设备索引", DefaultValue = "0", Description = "相机枚举索引，从0开始" },
                    new OperatorParam { Name = "targetWidth", DisplayName = "目标宽度", DefaultValue = "0", Description = "预留参数，当前未缩放（填0即可）" },
                    new OperatorParam { Name = "targetHeight", DisplayName = "目标高度", DefaultValue = "0", Description = "预留参数，当前未缩放（填0即可）" }
                },
                Ports =
                {
                    new PortDef { Name = "After", Direction = PortDirection.Input, DataType = typeof(object), ColorHex = "#607D8B", IsOptional = true },
                    new PortDef { Name = "Image", Direction = PortDirection.Output, DataType = typeof(CalibImage), ColorHex = "#4CAF50" },
                    new PortDef { Name = "Out", Direction = PortDirection.Output, DataType = typeof(object), ColorHex = "#607D8B", IsOptional = true }
                }
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
                TypeId = "exposure_fusion",
                DisplayName = "Exposure Fusion",
                Description = "多曝光/多帧图像融合为单张灰度图。接收 ImageList（如循环收集输出）。max=亮部保留；min=暗部；mean=平均；mertens=按梯度权重软融合。循环场景下在全部轮次结束后执行一次。",
                Category = "图像",
                Params =
                {
                    new OperatorParam
                    {
                        Name = "mode",
                        DisplayName = "融合模式",
                        DefaultValue = "mertens",
                        Description = "max | min | mean | mertens",
                        Options = new List<string> { "mertens", "max", "min", "mean" }
                    }
                },
                Ports =
                {
                    new PortDef { Name = "Images", Direction = PortDirection.Input, DataType = typeof(List<CalibImage>), ColorHex = "#FF9800" },
                    new PortDef { Name = "Image", Direction = PortDirection.Output, DataType = typeof(CalibImage), ColorHex = "#FF9800" }
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
                TypeId = "image_flip",
                DisplayName = "图像翻转",
                Description = "水平/垂直/双向翻转 CalibImage（灰度或 BGR）",
                Category = "预处理",
                Params =
                {
                    new OperatorParam
                    {
                        Name = "flipMode",
                        DisplayName = "翻转方式",
                        DefaultValue = "horizontal",
                        Description = "horizontal=左右；vertical=上下；both=水平+垂直",
                        Options = new List<string> { "horizontal", "vertical", "both", "水平", "垂直", "双向" }
                    }
                },
                Ports =
                {
                    new PortDef { Name = "In", Direction = PortDirection.Input, DataType = typeof(CalibImage), ColorHex = "#4CAF50" },
                    new PortDef { Name = "Out", Direction = PortDirection.Output, DataType = typeof(CalibImage), ColorHex = "#4CAF50" }
                }
            },
            new OperatorDef
            {
                TypeId = "image_rotate",
                DisplayName = "图像旋转",
                Description = "顺时针旋转。90/180/270 为快速直角旋转；其它角度用双线性插值。expandCanvas=true 时扩大画布容纳整图。",
                Category = "预处理",
                Params =
                {
                    new OperatorParam { Name = "angleDeg", DisplayName = "角度(°)", DefaultValue = "90", Description = "顺时针角度，如 90、180、270 或任意小数" },
                    new OperatorParam
                    {
                        Name = "expandCanvas",
                        DisplayName = "扩大画布",
                        DefaultValue = "true",
                        Description = "仅非 90/180/270° 时有效：true=不裁切；false=保持原宽高",
                        Options = new List<string> { "true", "false" }
                    }
                },
                Ports =
                {
                    new PortDef { Name = "In", Direction = PortDirection.Input, DataType = typeof(CalibImage), ColorHex = "#4CAF50" },
                    new PortDef { Name = "Out", Direction = PortDirection.Output, DataType = typeof(CalibImage), ColorHex = "#4CAF50" }
                }
            },
            new OperatorDef
            {
                TypeId = "image_resize",
                DisplayName = "图像缩放",
                Description = "缩放 CalibImage（灰度或 BGR）。factor=按比例；absolute=指定宽高；max_side=最长边限制",
                Category = "预处理",
                Params =
                {
                    new OperatorParam
                    {
                        Name = "mode",
                        DisplayName = "模式",
                        DefaultValue = "factor",
                        Description = "factor=scale；absolute=width/height；max_side=maxSide",
                        Options = new List<string> { "factor", "absolute", "max_side" }
                    },
                    new OperatorParam { Name = "scale", DisplayName = "比例", DefaultValue = "1.0", Description = "factor 模式：如 0.5、2.0" },
                    new OperatorParam { Name = "width", DisplayName = "目标宽", DefaultValue = "0", Description = "absolute 模式；0=由 height 推算" },
                    new OperatorParam { Name = "height", DisplayName = "目标高", DefaultValue = "0", Description = "absolute 模式；0=由 width 推算" },
                    new OperatorParam { Name = "maxSide", DisplayName = "最长边", DefaultValue = "0", Description = "max_side 模式：输出最长边像素数" },
                    new OperatorParam
                    {
                        Name = "keepAspect",
                        DisplayName = "保持宽高比",
                        DefaultValue = "true",
                        Description = "absolute 且只填宽或高时生效",
                        Options = new List<string> { "true", "false" }
                    },
                    new OperatorParam
                    {
                        Name = "interpolation",
                        DisplayName = "插值",
                        DefaultValue = "linear",
                        Description = "linear / nearest / bicubic",
                        Options = new List<string> { "linear", "nearest", "bicubic" }
                    }
                },
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
                TypeId = "dip_denoise",
                DisplayName = "DIP去噪",
                Description = "Deep Image Prior（PyTorch U-Net 迭代优化）去噪；需安装 torch，详见 DIP_Inference/requirements-dip.txt",
                Category = "AI模型",
                Ports =
                {
                    new PortDef { Name = "In", Direction = PortDirection.Input, DataType = typeof(CalibImage), ColorHex = "#4CAF50" },
                    new PortDef { Name = "Out", Direction = PortDirection.Output, DataType = typeof(CalibImage), ColorHex = "#4CAF50" }
                },
                Params =
                {
                    new OperatorParam { Name = "pythonPath", DisplayName = "Python", DefaultValue = "python", Description = "python.exe 或可执行文件名（PATH 中）" },
                    new OperatorParam { Name = "scriptPath", DisplayName = "脚本路径", DefaultValue = "DIP_Inference/dip_denoise.py", Description = "相对 flow 目录，否则向上查找仓库根；解析规则同 ONNX 模型路径" },
                    new OperatorParam { Name = "iterations", DisplayName = "迭代次数", DefaultValue = "2400", Description = "Adam 步数，越大越慢" },
                    new OperatorParam { Name = "learningRate", DisplayName = "学习率", DefaultValue = "0.01", Description = "Adam lr" },
                    new OperatorParam { Name = "tvWeight", DisplayName = "TV权重", DefaultValue = "0.000001", Description = "全变分正则，0 关闭" },
                    new OperatorParam { Name = "maxSide", DisplayName = "最长边上限", DefaultValue = "0", Description = "0=原分辨率；>0 时按比例缩小最长边以加速" },
                    new OperatorParam { Name = "useGpu", DisplayName = "使用 CUDA", DefaultValue = "false", Description = "true 时 --device cuda（需 CUDA 版 torch）" },
                    new OperatorParam { Name = "timeoutSec", DisplayName = "超时(秒)", DefaultValue = "600", Description = "整段优化超时，至少 30" }
                }
            },
            new OperatorDef
            {
                TypeId = "swin_transformer",
                DisplayName = "Swin特征",
                Description = "timm Swin：可选 ImageNet Top-K 分类、全局特征向量 JSON、最后一层特征的空间范数热力图（显著性/粗分割可视化，非实例分割）。依赖 Swin_Inference/requirements-swin.txt",
                Category = "AI模型",
                Ports =
                {
                    new PortDef { Name = "In", Direction = PortDirection.Input, DataType = typeof(CalibImage), ColorHex = "#4CAF50" },
                    new PortDef { Name = "Out", Direction = PortDirection.Output, DataType = typeof(CalibImage), ColorHex = "#4CAF50" },
                    new PortDef { Name = "LabelsJson", Direction = PortDirection.Output, DataType = typeof(string), ColorHex = "#607D8B" },
                    new PortDef { Name = "EmbeddingJson", Direction = PortDirection.Output, DataType = typeof(string), ColorHex = "#795548" },
                    new PortDef { Name = "SegmentHeatmap", Direction = PortDirection.Output, DataType = typeof(CalibImage), ColorHex = "#FF5722" }
                },
                Params =
                {
                    new OperatorParam { Name = "pythonPath", DisplayName = "Python", DefaultValue = "python", Description = "已安装 torch/timm/torchvision 的解释器" },
                    new OperatorParam { Name = "scriptPath", DisplayName = "脚本路径", DefaultValue = "Swin_Inference/swin_infer.py", Description = "相对 flow 目录，否则向上查找仓库根；解析规则同 ONNX" },
                    new OperatorParam
                    {
                        Name = "modelName",
                        DisplayName = "timm模型名",
                        DefaultValue = "swin_tiny_patch4_window7_224",
                        Description = "任意 timm Swin 注册名，如 swin_small_patch4_window7_224、swin_base_patch4_window7_224（须与 ImageNet 224 预处理匹配）"
                    },
                    new OperatorParam { Name = "enableClassification", DisplayName = "输出分类", DefaultValue = "true", Description = "true 时计算 Top-K 并写入 LabelsJson" },
                    new OperatorParam { Name = "topK", DisplayName = "Top-K", DefaultValue = "5", Description = "开启分类时前 K 个类别及置信度" },
                    new OperatorParam { Name = "enableEmbedding", DisplayName = "输出特征向量", DefaultValue = "true", Description = "true 时输出 EmbeddingJson（全局池化后向量）" },
                    new OperatorParam { Name = "enableSegmentHeatmap", DisplayName = "输出分割热力图", DefaultValue = "true", Description = "true 时输出与原图同尺寸的灰度热力图（伪分割）" },
                    new OperatorParam { Name = "useGpu", DisplayName = "使用 CUDA", DefaultValue = "false", Description = "true 时需 CUDA 版 PyTorch" },
                    new OperatorParam { Name = "timeoutSec", DisplayName = "超时(秒)", DefaultValue = "300", Description = "首次会下载权重，建议 ≥120" }
                }
            },
            new OperatorDef
            {
                TypeId = "yolo_seg_infer",
                DisplayName = "YOLO分割推理",
                Description = "Ultralytics YOLO-Seg：加载自定义 .pt（如 runs/.../weights/best.pt），输出原图透传、可视化叠加图、检测 JSON（含 polygon_norm）。依赖 pip install ultralytics",
                Category = "AI模型",
                Ports =
                {
                    new PortDef { Name = "In", Direction = PortDirection.Input, DataType = typeof(CalibImage), ColorHex = "#4CAF50" },
                    new PortDef { Name = "Out", Direction = PortDirection.Output, DataType = typeof(CalibImage), ColorHex = "#4CAF50" },
                    new PortDef { Name = "Vis", Direction = PortDirection.Output, DataType = typeof(CalibImage), ColorHex = "#00BCD4" },
                    new PortDef { Name = "DetectJson", Direction = PortDirection.Output, DataType = typeof(string), ColorHex = "#607D8B" }
                },
                Params =
                {
                    new OperatorParam { Name = "pythonPath", DisplayName = "Python", DefaultValue = "python", Description = "已安装 ultralytics 的解释器" },
                    new OperatorParam { Name = "scriptPath", DisplayName = "脚本路径", DefaultValue = "YoloSeg_Tools/predict_seg.py", Description = "相对 flow 目录，否则向上查找仓库根" },
                    new OperatorParam
                    {
                        Name = "weightsPath",
                        DisplayName = "权重 .pt",
                        DefaultValue = "yolo_data/runs/segment/train-2/weights/best.pt",
                        Description = "best.pt 或 last.pt；相对 flow 目录，否则向上查找仓库根"
                    },
                    new OperatorParam { Name = "conf", DisplayName = "置信度阈值", DefaultValue = "0.25", Description = "与 Ultralytics predict conf 一致" },
                    new OperatorParam
                    {
                        Name = "imgsz",
                        DisplayName = "推理 imgsz",
                        DefaultValue = "640",
                        Description = "predict 输入缩放边长；0=Ultralytics 模型默认。可与训练 imgsz 不同，常用 640/960/1280（越大越细越慢占显存）"
                    },
                    new OperatorParam { Name = "useGpu", DisplayName = "使用 CUDA", DefaultValue = "false", Description = "true 时 device=cuda:0" },
                    new OperatorParam { Name = "timeoutSec", DisplayName = "超时(秒)", DefaultValue = "120", Description = "单张推理超时，至少 15" },
                    new OperatorParam
                    {
                        Name = "visNoBoxes",
                        DisplayName = "可视化不画框",
                        DefaultValue = "false",
                        Description = "true 时 Vis 仅叠加分割掩码，不绘制检测框（JSON 仍含 xyxy）"
                    }
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
                Description = "fixed：固定灰度区间 [grayLow,grayHigh] 内置信为白；percentile：按整图灰度直方图分位数定上下限（如剔除最暗10%、最亮10%，中间约80%像素对应的灰度段）",
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
                        Name = "rangeMode",
                        DisplayName = "区间模式",
                        DefaultValue = "fixed",
                        Description = "fixed=使用 grayLow/grayHigh；percentile=按百分比分位数自动定上下限",
                        Options = new List<string> { "fixed", "percentile" }
                    },
                    new OperatorParam { Name = "grayLow", DisplayName = "灰度下限", DefaultValue = "5", Description = "仅 fixed：范围下界 0~255" },
                    new OperatorParam { Name = "grayHigh", DisplayName = "灰度上限", DefaultValue = "50", Description = "仅 fixed：范围上界 0~255" },
                    new OperatorParam { Name = "percentileExcludeLow", DisplayName = "剔除最暗比例(%)", DefaultValue = "10", Description = "仅 percentile：累计占比达到该百分比处的灰度作为下限（约等于「低于该灰度的像素占图中比例」）" },
                    new OperatorParam { Name = "percentileExcludeHigh", DisplayName = "剔除最亮比例(%)", DefaultValue = "10", Description = "仅 percentile：累计占比达到 (100−该值)% 处的灰度作为上限；与剔除最暗合计宜小于100" }
                }
            },
            new OperatorDef
            {
                TypeId = "binary_merge",
                DisplayName = "二值图合并",
                Description = "两幅图先转单通道灰度，尺寸须一致；可选按阈值再二值化后做逐像素位运算合并，输出单通道 0/255",
                Category = "预处理",
                Ports =
                {
                    new PortDef { Name = "InA", Direction = PortDirection.Input, DataType = typeof(CalibImage), ColorHex = "#4CAF50" },
                    new PortDef { Name = "InB", Direction = PortDirection.Input, DataType = typeof(CalibImage), ColorHex = "#2196F3" },
                    new PortDef { Name = "Out", Direction = PortDirection.Output, DataType = typeof(CalibImage), ColorHex = "#4CAF50" }
                },
                Params =
                {
                    new OperatorParam { Name = "mergeMode", DisplayName = "合并方式", DefaultValue = "or", Description = "or=并集 | and=交集 | xor=对称差" },
                    new OperatorParam { Name = "foregroundThreshold", DisplayName = "前景阈", DefaultValue = "0", Description = "灰度大于该值视为前景(255)再合并；与 OpenCV THRESH_BINARY 一致。设为 -1 时不预二值化，直接对原灰度字节做位运算" }
                }
            },
            new OperatorDef
            {
                TypeId = "binary_morph_rect",
                DisplayName = "矩形形态学(二值)",
                Description = "矩形结构元腐蚀/膨胀/开/闭。横向细长核对横贯图像的长条前景可做 opening，打断竖直方向的细连接，便于拆开误合并的多条标定条带；vertical_strips 则使用竖向核。可选剔除过小连通域",
                Category = "预处理",
                Ports =
                {
                    new PortDef { Name = "In", Direction = PortDirection.Input, DataType = typeof(CalibImage), ColorHex = "#4CAF50" },
                    new PortDef { Name = "Out", Direction = PortDirection.Output, DataType = typeof(CalibImage), ColorHex = "#4CAF50" }
                },
                Params =
                {
                    new OperatorParam { Name = "op", DisplayName = "运算", DefaultValue = "open", Description = "open | close | erode | dilate" },
                    new OperatorParam { Name = "orientation", DisplayName = "条带方向预设", DefaultValue = "horizontal_strips", Description = "horizontal_strips=横向长条(默认核宽约31高约5)打断竖直连通 | vertical_strips=纵向长条 | custom=完全由 kernelW/H 决定" },
                    new OperatorParam { Name = "kernelW", DisplayName = "结构元宽度", DefaultValue = "", Description = "奇数；留空则用预设" },
                    new OperatorParam { Name = "kernelH", DisplayName = "结构元高度", DefaultValue = "", Description = "奇数；留空则用预设" },
                    new OperatorParam { Name = "iterations", DisplayName = "迭代次数", DefaultValue = "1", Description = ">=1" },
                    new OperatorParam { Name = "foregroundThreshold", DisplayName = "前景阈", DefaultValue = "0", Description = "灰度大于阈值为前景；-1 表示非零即前景" },
                    new OperatorParam { Name = "minComponentPixels", DisplayName = "最小连通像素", DefaultValue = "0", Description = "形态学后剔除小于该像素数的 4-连通域；0 表示关闭" }
                }
            },
            new OperatorDef
            {
                TypeId = "gray_erode_rect",
                DisplayName = "灰度腐蚀",
                Description = "单通道灰度矩形腐蚀（邻域取最小值）；彩色输入先按 ITU-R BT.601 权重复制为灰度。边界按边缘复制延拓",
                Category = "预处理",
                Ports =
                {
                    new PortDef { Name = "In", Direction = PortDirection.Input, DataType = typeof(CalibImage), ColorHex = "#4CAF50" },
                    new PortDef { Name = "Out", Direction = PortDirection.Output, DataType = typeof(CalibImage), ColorHex = "#4CAF50" }
                },
                Params =
                {
                    new OperatorParam { Name = "kernelW", DisplayName = "结构元宽度", DefaultValue = "5", Description = "奇数，≥1；非奇数自动调整为下一个奇数" },
                    new OperatorParam { Name = "kernelH", DisplayName = "结构元高度", DefaultValue = "5", Description = "奇数，≥1" },
                    new OperatorParam { Name = "iterations", DisplayName = "迭代次数", DefaultValue = "1", Description = ">=1，重复腐蚀" }
                }
            },
            new OperatorDef
            {
                TypeId = "gray_dilate_rect",
                DisplayName = "灰度膨胀",
                Description = "单通道灰度矩形膨胀（邻域取最大值）；彩色输入先转灰度。边界按边缘复制延拓",
                Category = "预处理",
                Ports =
                {
                    new PortDef { Name = "In", Direction = PortDirection.Input, DataType = typeof(CalibImage), ColorHex = "#4CAF50" },
                    new PortDef { Name = "Out", Direction = PortDirection.Output, DataType = typeof(CalibImage), ColorHex = "#4CAF50" }
                },
                Params =
                {
                    new OperatorParam { Name = "kernelW", DisplayName = "结构元宽度", DefaultValue = "5", Description = "奇数，≥1" },
                    new OperatorParam { Name = "kernelH", DisplayName = "结构元高度", DefaultValue = "5", Description = "奇数，≥1" },
                    new OperatorParam { Name = "iterations", DisplayName = "迭代次数", DefaultValue = "1", Description = ">=1，重复膨胀" }
                }
            },
            new OperatorDef
            {
                TypeId = "gray_blend_ratio",
                DisplayName = "灰度合并",
                Description = "两幅图转灰度后尺寸须一致，输出单通道灰度。weighted：加权混合 ratioA·InA+(1-ratioA)·InB；add：饱和相加 min(255,InA+InB)；subtract：InA−InB 饱和下溢为 0",
                Category = "预处理",
                Ports =
                {
                    new PortDef { Name = "InA", Direction = PortDirection.Input, DataType = typeof(CalibImage), ColorHex = "#4CAF50" },
                    new PortDef { Name = "InB", Direction = PortDirection.Input, DataType = typeof(CalibImage), ColorHex = "#2196F3" },
                    new PortDef { Name = "Out", Direction = PortDirection.Output, DataType = typeof(CalibImage), ColorHex = "#4CAF50" }
                },
                Params =
                {
                    new OperatorParam { Name = "grayBlendMode", DisplayName = "合并方式", DefaultValue = "weighted", Description = "weighted=加权混合 | add/sum=相加饱和截断 | subtract/sub=InA−InB 饱和" },
                    new OperatorParam { Name = "ratioA", DisplayName = "InA 权重", DefaultValue = "0.5", Description = "仅 weighted 有效：0~1，Out=ratioA·InA+(1−ratioA)·InB；超出裁剪到 [0,1]" }
                }
            },
            new OperatorDef
            {
                TypeId = "hough_circles",
                DisplayName = "霍夫圆",
                Description = "HoughCircles：输出绿圈与青圆心叠加图；CirclesJson [[cx,cy,r],...] 可接霍夫跑道形",
                Category = "检测",
                Ports =
                {
                    new PortDef { Name = "Image", Direction = PortDirection.Input, DataType = typeof(CalibImage), ColorHex = "#4CAF50" },
                    new PortDef { Name = "Out", Direction = PortDirection.Output, DataType = typeof(CalibImage), ColorHex = "#4CAF50" },
                    new PortDef { Name = "CirclePoints", Direction = PortDirection.Output, DataType = typeof(Point2D[]), ColorHex = "#2196F3" },
                    new PortDef { Name = "CircleCount", Direction = PortDirection.Output, DataType = typeof(int), ColorHex = "#FF9800" },
                    new PortDef { Name = "CirclesJson", Direction = PortDirection.Output, DataType = typeof(string), ColorHex = "#607D8B" }
                },
                Params =
                {
                    new OperatorParam { Name = "blurKsize", DisplayName = "高斯模糊核", DefaultValue = "9", Description = "奇数 ≥3；降噪" },
                    new OperatorParam { Name = "hcDp", DisplayName = "累加器分辨(dp)", DefaultValue = "1.2", Description = "HoughCircles dp" },
                    new OperatorParam { Name = "hcMinDist", DisplayName = "圆心最小距", DefaultValue = "40", Description = "minDist" },
                    new OperatorParam { Name = "hcParam1", DisplayName = "Canny上阈", DefaultValue = "100", Description = "param1" },
                    new OperatorParam { Name = "hcParam2", DisplayName = "圆心累加阈", DefaultValue = "30", Description = "param2" },
                    new OperatorParam { Name = "hcMinRadius", DisplayName = "最小半径", DefaultValue = "5", Description = "像素" },
                    new OperatorParam { Name = "hcMaxRadius", DisplayName = "最大半径", DefaultValue = "200", Description = "像素" }
                }
            },
            new OperatorDef
            {
                TypeId = "hough_lines",
                DisplayName = "霍夫线段",
                Description = "Edge 接边缘图（8 位灰度或 BGR）；转灰度后可选按「霍夫匹配半宽」将边缘二值并膨胀再送入 HoughLinesP。检出后按线段长度降序、再按沿线覆盖率（在同一张用于霍夫的边缘图上 Bresenham 采样）降序排序，再截取最多线段数。下列为 HoughLinesP 与匹配参数",
                Category = "检测",
                Ports =
                {
                    new PortDef { Name = "Edge", Direction = PortDirection.Input, DataType = typeof(CalibImage), ColorHex = "#4CAF50" },
                    new PortDef { Name = "Out", Direction = PortDirection.Output, DataType = typeof(CalibImage), ColorHex = "#4CAF50" },
                    new PortDef { Name = "LinesJson", Direction = PortDirection.Output, DataType = typeof(string), ColorHex = "#607D8B" },
                    new PortDef { Name = "LineCount", Direction = PortDirection.Output, DataType = typeof(int), ColorHex = "#FF9800" }
                },
                Params =
                {
                    new OperatorParam { Name = "hlRho", DisplayName = "ρ步长", DefaultValue = "1", Description = "HoughLinesP rho（像素）" },
                    new OperatorParam { Name = "hlThetaDeg", DisplayName = "θ步长(度)", DefaultValue = "1", Description = "角度分辨率" },
                    new OperatorParam { Name = "hlThreshold", DisplayName = "累加阈", DefaultValue = "50", Description = "(ρ,θ) 投票阈值" },
                    new OperatorParam { Name = "hlMinLineLength", DisplayName = "最短线段", DefaultValue = "40", Description = "像素" },
                    new OperatorParam { Name = "hlMaxLineGap", DisplayName = "最大断裂", DefaultValue = "15", Description = "像素" },
                    new OperatorParam { Name = "maxLinesOut", DisplayName = "最多线段数", DefaultValue = "400", Description = "绘制与 JSON 上限" },
                    new OperatorParam { Name = "hlCoverageHalfWidthPx", DisplayName = "霍夫匹配半宽(px)", DefaultValue = "0", Description = "0=边缘图直接参与 HoughLinesP；N>0=边缘先按灰度>0 二值再以半径 N 圆形膨胀，膨胀图作为霍夫输入（线段更易在带宽内成形）；排序用覆盖率在同一张膨胀图上统计" }
                }
            },
            new OperatorDef
            {
                TypeId = "lines_nms",
                DisplayName = "线段非极大值抑制",
                Description = "对 LinesJson [[x1,y1,x2,y2],...] 按法向角 θ 与距原点垂距 ρ 分桶，每桶保留最长线段，用于合并霍夫重复检测",
                Category = "检测",
                Ports =
                {
                    new PortDef { Name = "LinesJson", Direction = PortDirection.Input, DataType = typeof(string), ColorHex = "#607D8B" },
                    new PortDef { Name = "LinesJson", Direction = PortDirection.Output, DataType = typeof(string), ColorHex = "#607D8B" },
                    new PortDef { Name = "LineCount", Direction = PortDirection.Output, DataType = typeof(int), ColorHex = "#FF9800" }
                },
                Params =
                {
                    new OperatorParam { Name = "angleTolDeg", DisplayName = "角度桶宽(度)", DefaultValue = "5", Description = "法向角 θ∈[0,π) 分桶宽度；越小越严格" },
                    new OperatorParam { Name = "rhoTolPx", DisplayName = "ρ桶宽(像素)", DefaultValue = "10", Description = "垂距 ρ 分桶宽度（像素）" }
                }
            },
            new OperatorDef
            {
                TypeId = "lines_threshold",
                DisplayName = "线段阈值筛选",
                Description = "按线段长度（像素）筛选 LinesJson；maxLengthPx≤0 表示不限制上限",
                Category = "检测",
                Ports =
                {
                    new PortDef { Name = "LinesJson", Direction = PortDirection.Input, DataType = typeof(string), ColorHex = "#607D8B" },
                    new PortDef { Name = "LinesJson", Direction = PortDirection.Output, DataType = typeof(string), ColorHex = "#607D8B" },
                    new PortDef { Name = "LineCount", Direction = PortDirection.Output, DataType = typeof(int), ColorHex = "#FF9800" }
                },
                Params =
                {
                    new OperatorParam { Name = "minLengthPx", DisplayName = "最小长度(px)", DefaultValue = "0", Description = "长度小于此值的线段剔除；0 表示不限制下限" },
                    new OperatorParam { Name = "maxLengthPx", DisplayName = "最大长度(px)", DefaultValue = "0", Description = "长度大于此值的线段剔除；0 或负数表示不限制上限" }
                }
            },
            new OperatorDef
            {
                TypeId = "hough_runway",
                DisplayName = "霍夫跑道形",
                Description = "parallel：主导方向 + ρ 分桶线段（JSON）；stadium：圆线参考→跑道闭合。可选 LinesJson（霍夫线段）、CirclesJson（霍夫圆）覆盖内部检测",
                Category = "检测",
                Ports =
                {
                    new PortDef { Name = "Image", Direction = PortDirection.Input, DataType = typeof(CalibImage), ColorHex = "#4CAF50" },
                    new PortDef { Name = "LinesJson", Direction = PortDirection.Input, DataType = typeof(string), ColorHex = "#607D8B", IsOptional = true },
                    new PortDef { Name = "CirclesJson", Direction = PortDirection.Input, DataType = typeof(string), ColorHex = "#607D8B", IsOptional = true },
                    new PortDef { Name = "Out", Direction = PortDirection.Output, DataType = typeof(CalibImage), ColorHex = "#4CAF50" },
                    new PortDef { Name = "RunwayLinesJson", Direction = PortDirection.Output, DataType = typeof(string), ColorHex = "#E040FB" },
                    new PortDef { Name = "RunwayLineCount", Direction = PortDirection.Output, DataType = typeof(int), ColorHex = "#AB47BC" }
                },
                Params =
                {
                    new OperatorParam { Name = "blurKsize", DisplayName = "高斯模糊核", DefaultValue = "9", Description = "与霍夫线段一致时可叠在同一张图上对比" },
                    new OperatorParam { Name = "cannyTh1", DisplayName = "Canny低阈", DefaultValue = "50", Description = "" },
                    new OperatorParam { Name = "cannyTh2", DisplayName = "Canny高阈", DefaultValue = "150", Description = "" },
                    new OperatorParam { Name = "hlRho", DisplayName = "ρ步长", DefaultValue = "1", Description = "" },
                    new OperatorParam { Name = "hlThetaDeg", DisplayName = "θ步长(度)", DefaultValue = "1", Description = "" },
                    new OperatorParam { Name = "hlThreshold", DisplayName = "累加阈", DefaultValue = "50", Description = "" },
                    new OperatorParam { Name = "hlMinLineLength", DisplayName = "最短线段", DefaultValue = "40", Description = "" },
                    new OperatorParam { Name = "hlMaxLineGap", DisplayName = "最大断裂", DefaultValue = "15", Description = "" },
                    new OperatorParam { Name = "maxLinesOut", DisplayName = "内部线段预算", DefaultValue = "400", Description = "先完整检测线段再筛跑道；影响性能" },
                    new OperatorParam { Name = "runwayShape", DisplayName = "形状", DefaultValue = "parallel", Description = "parallel | stadium（体育场=两直边+两半圆）" },
                    new OperatorParam { Name = "runwayAngleTolDeg", DisplayName = "跑道方向桶宽(度)", DefaultValue = "10", Description = "" },
                    new OperatorParam { Name = "runwayRhoBinPx", DisplayName = "跑道ρ桶宽(像素)", DefaultValue = "25", Description = "" },
                    new OperatorParam { Name = "runwayStripCount", DisplayName = "跑道条带数", DefaultValue = "2", Description = "parallel：取前 N 个 ρ 桶；stadium 固定取最强两桶合并为直道" },
                    new OperatorParam { Name = "maxRunwayLinesOut", DisplayName = "跑道线段上限", DefaultValue = "120", Description = "仅 parallel 模式" },
                    new OperatorParam { Name = "hcDp", DisplayName = "圆 dp(stadium)", DefaultValue = "1.2", Description = "stadium 端点半径检测 HoughCircles" },
                    new OperatorParam { Name = "hcMinDist", DisplayName = "圆心最小距(stadium)", DefaultValue = "40", Description = "" },
                    new OperatorParam { Name = "hcParam1", DisplayName = "圆 Canny上阈(stadium)", DefaultValue = "100", Description = "" },
                    new OperatorParam { Name = "hcParam2", DisplayName = "圆累加阈(stadium)", DefaultValue = "30", Description = "" },
                    new OperatorParam { Name = "hcMinRadius", DisplayName = "最小半径(stadium)", DefaultValue = "5", Description = "像素" },
                    new OperatorParam { Name = "hcMaxRadius", DisplayName = "最大半径(stadium)", DefaultValue = "200", Description = "像素" }
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
                },
                Params =
                {
                    new OperatorParam { Name = "minContourArea", DisplayName = "最小轮廓面积", DefaultValue = "", Description = "留空=默认 max(500,0.002×宽高)；填像素²阈值（如 400）可保留细长条带轮廓" }
                }
            },
            new OperatorDef
            {
                TypeId = "filter_contours",
                DisplayName = "规则轮廓筛选",
                Description = "按面积/长宽比/圆度筛选规整轮廓。轮廓条数为 0 时跳过（输出空 Contours，Count=0）。",
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
                    new OperatorParam { Name = "minCircularity", DisplayName = "最小圆度", DefaultValue = "0.02", Description = "4πA/P²；≤0 表示不限制下限" },
                    new OperatorParam { Name = "maxCircularity", DisplayName = "最大圆度", DefaultValue = "1", Description = "≥1 表示不限制；否则剔除过圆的块状域（细长条圆度低）" },
                    new OperatorParam { Name = "sortByCentroidY", DisplayName = "按重心Y排序", DefaultValue = "false", Description = "true=筛选后自上而下排序（适合横向条带）" },
                    new OperatorParam { Name = "targetCount", DisplayName = "保留数量", DefaultValue = "15", Description = "按面积降序保留前N条" }
                }
            },
            new OperatorDef
            {
                TypeId = "match_contours",
                DisplayName = "形状匹配筛选",
                Description = "基于模板轮廓做形状匹配并筛选。轮廓条数为 0 时跳过（输出空 Contours，Count=0）。",
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
                Description = "将轮廓集合融合为模板图。轮廓条数为 0 时跳过（输出空白 Template）。",
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
                Description = "Step2.5: 按轮廓索引填充内部生成Mask（-1=自动选最大）。无轮廓时输出全黑 Mask，不报错。",
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
                },
                Params =
                {
                    new OperatorParam { Name = "minContourArea", DisplayName = "最小轮廓面积", DefaultValue = "", Description = "留空=默认；填像素²可降低面积门槛" }
                }
            },
            new OperatorDef
            {
                TypeId = "sample",
                DisplayName = "采样",
                Description = "使用上游轮廓数据进行等弧长采样。若轮廓条数为 0 则跳过（Points / BarIds 输出空数组，不报错）。",
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
                Description = "Step7.5: Stadium拟合/曲率去噪。输入 0 个点时跳过（空 Out / OutBarIds）。",
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
                Description = "Step8: 通过mask验证轨迹。输入 0 个点时跳过（空 Out）。",
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
                Description = "Step9: 去重+排序。输入 0 个点时跳过（空 Out）。",
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
                Description = "Step10: 转换为输出格式。输入 0 个点时仍输出 Result（Success=false），不报错。",
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
                Description = "Step11: 绘制彩色结果。轨迹为空时输出空白画布，不报错。",
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
                Description = "每个画布上的「显示图像」节点独占一个预览窗口（标题含短 Guid）；同一节点多次运行会刷新该窗口。可选 Points（单条点列）或 PointsList（多条点列；可接「循环收集」PointsList / List、flow_loop 的 PointsList）。可选 Xld：叠加 HALCON XLD 折线（橘色）。可选 BarIds 与参数「点列折线」控制青色折线：默认 auto 仅在条号不全相同时分段；焊头/整段轨迹选 single。右键连线看图仍共用快捷预览窗口。",
                Category = "可视化",
                Params =
                {
                    new OperatorParam { Name = "dotRadius", DisplayName = "点位半径", DefaultValue = "3", Description = "叠加点位的圆点半径 (像素)" },
                    new OperatorParam
                    {
                        Name = "pointLineJoin",
                        DisplayName = "点列折线",
                        DefaultValue = "auto",
                        Description = "auto=仅当 BarIds 与 Points 等长且条号不全相同时分段（转换前多轮廓）；single=强制整条折线（焊头轨迹、转换后整路径）；bars=只要 BarIds 等长就分段",
                        Options = new List<string> { "auto", "single", "bars" }
                    }
                },
                Ports =
                {
                    new PortDef { Name = "Img", Direction = PortDirection.Input, DataType = typeof(CalibImage), ColorHex = "#4CAF50" },
                    new PortDef { Name = "Image", Direction = PortDirection.Input, DataType = typeof(CalibImage), ColorHex = "#4CAF50" },
                    new PortDef { Name = "Points", Direction = PortDirection.Input, DataType = typeof(Point2D[]), ColorHex = "#2196F3", IsOptional = true },
                    new PortDef { Name = "PointsList", Direction = PortDirection.Input, DataType = typeof(object), ColorHex = "#64B5F6", IsOptional = true },
                    new PortDef { Name = "BarIds", Direction = PortDirection.Input, DataType = typeof(int[]), ColorHex = "#FFC107", IsOptional = true },
                    new PortDef { Name = "Xld", Direction = PortDirection.Input, DataType = typeof(HalconXldContourBundle), ColorHex = "#E65100", IsOptional = true }
                }
            },
            new OperatorDef
            {
                TypeId = "display_3d_trajectory",
                DisplayName = "显示3D轨迹",
                Description = "在独立窗口中用 3D 视图显示轨迹（HelixToolkit 管状体）。优先连接 Points3D(CalibPoint3D[])；或连接 Point2D[] 的 Points，可选同长度 Z(double[]) 或仅用参数 zDefault 作为统一高度。同一节点多次运行刷新该节点对应窗口。鼠标旋转视角（Turntable）。",
                Category = "可视化",
                Params =
                {
                    new OperatorParam { Name = "tubeDiameter", DisplayName = "管径", DefaultValue = "0.8", Description = "轨迹管状体直径（与坐标同单位）" },
                    new OperatorParam { Name = "showGrid", DisplayName = "地面网格", DefaultValue = "true", Description = "是否显示参考网格" },
                    new OperatorParam { Name = "gridExtent", DisplayName = "网格边长", DefaultValue = "200", Description = "地面网格正方形边长（与坐标同单位）" },
                    new OperatorParam { Name = "zDefault", DisplayName = "默认Z", DefaultValue = "0", Description = "仅接 Points 且未接 Z 时，各点的 Z 坐标" }
                },
                Ports =
                {
                    new PortDef { Name = "Points3D", Direction = PortDirection.Input, DataType = typeof(CalibPoint3D[]), ColorHex = "#00BCD4" },
                    new PortDef { Name = "Points", Direction = PortDirection.Input, DataType = typeof(Point2D[]), ColorHex = "#2196F3" },
                    new PortDef { Name = "Z", Direction = PortDirection.Input, DataType = typeof(double[]), ColorHex = "#9E9E9E" },
                    new PortDef { Name = "Out", Direction = PortDirection.Output, DataType = typeof(CalibPoint3D[]), ColorHex = "#00BCD4" }
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
                        Description = "支持绝对路径或相对路径（相对当前 .flow.json 目录）"
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
                TypeId = "save_text",
                DisplayName = "保存文本",
                Description = "将字符串以 UTF-8 写入文件（如棋盘格内参 JSON）",
                Category = "可视化",
                Params =
                {
                    new OperatorParam
                    {
                        Name = "filePath",
                        DisplayName = "保存路径",
                        DefaultValue = "flow_output.txt",
                        Description = "绝对路径或相对当前 .flow.json 目录"
                    }
                },
                Ports =
                {
                    new PortDef { Name = "Text", Direction = PortDirection.Input, DataType = typeof(string), ColorHex = "#607D8B" },
                    new PortDef { Name = "Out", Direction = PortDirection.Output, DataType = typeof(string), ColorHex = "#607D8B" }
                }
            },
            new OperatorDef
            {
                TypeId = "points_to_text",
                DisplayName = "点列转文本",
                Description = "将 Point2D[] 转为每行 x,y 文本，便于接「保存文本」落盘（如棋盘平面 mm 轨迹）。0 个点时输出空字符串，不报错。",
                Category = "可视化",
                Params =
                {
                    new OperatorParam { Name = "lineSeparator", DisplayName = "换行", DefaultValue = "lf", Description = "lf 或 crlf" }
                },
                Ports =
                {
                    new PortDef { Name = "Points", Direction = PortDirection.Input, DataType = typeof(Point2D[]), ColorHex = "#2196F3" },
                    new PortDef { Name = "Text", Direction = PortDirection.Output, DataType = typeof(string), ColorHex = "#607D8B" }
                }
            },
            new OperatorDef
            {
                TypeId = "polyline_simplify_dp",
                DisplayName = "轮廓点简化",
                Description = "对有序点列做 Douglas–Peucker。须接与 In 等长的 BarIds 或 GroupBarIds（落格轨迹请用 GroupBarIds=按匹配序号），按条号分段简化并保留原条号到 OutBarIds；未接或不等长时整条视为一条、OutBarIds 全 0（send_plc separate_batch 只会发 1 批）。",
                Category = "预处理",
                Params =
                {
                    new OperatorParam { Name = "epsilon", DisplayName = "偏差阈值 ε", DefaultValue = "2.0", Description = "与坐标同单位（一般为像素）；越大顶点越少" },
                    new OperatorParam { Name = "closed", DisplayName = "闭合轮廓", DefaultValue = "true", Description = "true=首尾闭合；false=开折线。接 BarIds 时每段轮廓单独应用该选项" }
                },
                Ports =
                {
                    new PortDef { Name = "In", Direction = PortDirection.Input, DataType = typeof(Point2D[]), ColorHex = "#2196F3" },
                    new PortDef { Name = "BarIds", Direction = PortDirection.Input, DataType = typeof(int[]), ColorHex = "#FFC107", IsOptional = true },
                    new PortDef { Name = "GroupBarIds", Direction = PortDirection.Input, DataType = typeof(int[]), ColorHex = "#FFEB3B", IsOptional = true },
                    new PortDef { Name = "Out", Direction = PortDirection.Output, DataType = typeof(Point2D[]), ColorHex = "#2196F3" },
                    new PortDef { Name = "OutBarIds", Direction = PortDirection.Output, DataType = typeof(int[]), ColorHex = "#FFC107" }
                }
            },
            new OperatorDef
            {
                TypeId = "polyline_uniform_offset",
                DisplayName = "轮廓/轨迹均匀外扩",
                Description = "沿法向等距偏移（平行曲线）：正距离=闭合轮廓外扩一圈、开折线向左侧偏置。支持 Point2D[] / CalibPoint3D[]（仅 XY 偏移、保留 Z）/ HalconXld（HALCON gen_parallel_contour_xld）。按 BarIds 或 GroupBarIds 分段；负距离=内缩。",
                Category = "预处理",
                Params =
                {
                    new OperatorParam { Name = "offsetDistance", DisplayName = "偏移距离", DefaultValue = "5", Description = "与坐标同单位（像素或 mm）；正=外扩，负=内缩" },
                    new OperatorParam { Name = "closed", DisplayName = "闭合轮廓", DefaultValue = "true", Description = "true=首尾闭合；false=开折线" },
                    new OperatorParam { Name = "halconMode", DisplayName = "HALCON法向模式", DefaultValue = "regression_normal", Description = "仅 Xld：gen_parallel_contour_xld 的 Mode；折线轮廓推荐 regression_normal", Options = new List<string> { "regression_normal", "contour_normal", "gradient" } }
                },
                Ports =
                {
                    new PortDef { Name = "In", Direction = PortDirection.Input, DataType = typeof(Point2D[]), ColorHex = "#2196F3", IsOptional = true },
                    new PortDef { Name = "Points3D", Direction = PortDirection.Input, DataType = typeof(CalibPoint3D[]), ColorHex = "#00ACC1", IsOptional = true },
                    new PortDef { Name = "Xld", Direction = PortDirection.Input, DataType = typeof(HalconXldContourBundle), ColorHex = "#E65100", IsOptional = true },
                    new PortDef { Name = "BarIds", Direction = PortDirection.Input, DataType = typeof(int[]), ColorHex = "#FFC107", IsOptional = true },
                    new PortDef { Name = "GroupBarIds", Direction = PortDirection.Input, DataType = typeof(int[]), ColorHex = "#FFEB3B", IsOptional = true },
                    new PortDef { Name = "Out", Direction = PortDirection.Output, DataType = typeof(Point2D[]), ColorHex = "#2196F3", IsOptional = true },
                    new PortDef { Name = "Out3D", Direction = PortDirection.Output, DataType = typeof(CalibPoint3D[]), ColorHex = "#00ACC1", IsOptional = true },
                    new PortDef { Name = "XldOut", Direction = PortDirection.Output, DataType = typeof(HalconXldContourBundle), ColorHex = "#E65100", IsOptional = true },
                    new PortDef { Name = "OutBarIds", Direction = PortDirection.Output, DataType = typeof(int[]), ColorHex = "#FFC107", IsOptional = true }
                }
            },
            new OperatorDef
            {
                TypeId = "world_coords",
                DisplayName = "世界坐标",
                Description = "通过参数配置九点标定的世界坐标",
                Category = "标定",
                Params =
                {
                    new OperatorParam
                    {
                        Name = "points",
                        DisplayName = "坐标点列表",
                        DefaultValue = "100,100;400,100;700,100;100,300;400,300;700,300;100,500;400,500;700,500",
                        Description = "格式: x,y;x,y;...（建议9点）"
                    }
                },
                Ports =
                {
                    new PortDef { Name = "Points", Direction = PortDirection.Output, DataType = typeof(Point2D[]), ColorHex = "#2196F3" }
                }
            },
            new OperatorDef
            {
                TypeId = "weld_trajectory_world",
                DisplayName = "焊接轨迹(世界mm)",
                Description = "在工件世界坐标系(mm)生成规则点列（含 Z）：轨迹类型用下拉选择；兼容旧流程英文键。输出 Points(XY) 与 Points3D(XYZ)。接「发送PLC」/「发送PLC(每点一点)」或后续标定。",
                Category = "标定",
                Params =
                {
                    new OperatorParam
                    {
                        Name = "pattern",
                        DisplayName = "轨迹类型",
                        DefaultValue = "九宫格 (3×3)",
                        Description = "下拉选择；保存为中文项。兼容旧流程中的英文：nine_3x3 / cross_lines 等。",
                        Options = new List<string>
                        {
                            "九宫格 (3×3)",
                            "十字交叉折线",
                            "五点十字",
                            "L形轨迹",
                            "直线段",
                            "矩形周长",
                            "网格蛇形"
                        }
                    },
                    new OperatorParam { Name = "centerX", DisplayName = "中心X(mm)", DefaultValue = "0", Description = "世界坐标中心 X" },
                    new OperatorParam { Name = "centerY", DisplayName = "中心Y(mm)", DefaultValue = "0", Description = "世界坐标中心 Y" },
                    new OperatorParam { Name = "centerZ", DisplayName = "中心Z(mm)", DefaultValue = "0", Description = "所有轨迹点的 Z 高度（基座/world mm）" },
                    new OperatorParam { Name = "stepXmm", DisplayName = "X步距(mm)", DefaultValue = "10", Description = "九宫格/网格蛇形：列方向(世界X)点间距；L形水平段、矩形水平边插补步长" },
                    new OperatorParam { Name = "stepYmm", DisplayName = "Y步距(mm)", DefaultValue = "10", Description = "九宫格/网格蛇形：行方向(世界Y)点间距；L形垂直段、矩形垂直边插补步长" },
                    new OperatorParam { Name = "stepMm", DisplayName = "步距(mm,兼容)", DefaultValue = "10", Description = "旧参数：未填 stepXmm/stepYmm 或填 0 时，X、Y 均用本值" },
                    new OperatorParam { Name = "armMm", DisplayName = "臂长/半长(mm)", DefaultValue = "50", Description = "十字/直线：半边长；矩形：半边宽=armMm、半边高=armMm（与 line 总长=2×armMm）" },
                    new OperatorParam { Name = "legXmm", DisplayName = "L水平腿长(mm)", DefaultValue = "50", Description = "l_shape 水平段总长" },
                    new OperatorParam { Name = "legYmm", DisplayName = "L垂直腿长(mm)", DefaultValue = "50", Description = "l_shape 垂直段总长" },
                    new OperatorParam { Name = "angleDeg", DisplayName = "直线角度(°)", DefaultValue = "0", Description = "line：与 +X 夹角，0=水平" },
                    new OperatorParam { Name = "gridCols", DisplayName = "列数", DefaultValue = "3", Description = "grid_snake 列数" },
                    new OperatorParam { Name = "gridRows", DisplayName = "行数", DefaultValue = "3", Description = "grid_snake 行数" },
                    new OperatorParam { Name = "samplesPerSegment", DisplayName = "段内插值点数", DefaultValue = "1", Description = "cross_lines/line：每段≥1 时仅端点；>1 时沿线插值细分" }
                },
                Ports =
                {
                    new PortDef { Name = "Points", Direction = PortDirection.Output, DataType = typeof(Point2D[]), ColorHex = "#2196F3" },
                    new PortDef { Name = "Points3D", Direction = PortDirection.Output, DataType = typeof(CalibPoint3D[]), ColorHex = "#00ACC1" }
                }
            },
            new OperatorDef
            {
                TypeId = "calibrate",
                DisplayName = "九点标定",
                Description = "标定像素→世界坐标。世界点可填参数 worldPoints，或由 worldPointsFile 读取「点列转文本」保存的 txt（每行 x,y）。默认 pixelPickMode=手选：仅需 Image，在弹窗中按世界点列表左键手选像素。也可连接 ImagePts 作参考或匹配检测点。",
                Category = "标定",
                Params =
                {
                    new OperatorParam
                    {
                        Name = "worldPointsFile",
                        DisplayName = "世界坐标文件",
                        DefaultValue = "",
                        Description = "可选；「点列转文本」+「保存文本」生成的 txt（每行 x,y，与 points_to_text 一致）。填写后优先于下方 worldPoints；相对路径相对当前 .flow.json 目录。"
                    },
                    new OperatorParam
                    {
                        Name = "worldPoints",
                        DisplayName = "世界坐标点",
                        DefaultValue = "100,100;400,100;700,100;100,300;400,300;700,300;100,500;400,500;700,500",
                        Description = "未填 worldPointsFile 时使用。格式 x,y 每行或 x,y;x,y;...（建议 9 点行优先）。至少 4 点。"
                    },
                    new OperatorParam
                    {
                        Name = "pixelPickMode",
                        DisplayName = "像素取点方式",
                        DefaultValue = "manual",
                        Description = "manual/手选=在图像任意位置手选像素；detected/匹配检测点=点击 ImagePts 附近；auto=数量一致时按顺序标定否则弹窗",
                        Options = new List<string> { "manual", "detected", "auto", "手选", "匹配检测点" }
                    },
                    new OperatorParam
                    {
                        Name = "confirmCorrespondence",
                        DisplayName = "图像确认对应",
                        DefaultValue = "true",
                        Description = "true=弹窗确认；false=仅 auto 且 ImagePts 数量一致时按顺序标定（手选模式仍会弹窗）",
                        Options = new List<string> { "true", "false" }
                    }
                },
                Ports =
                {
                    new PortDef { Name = "Image", Direction = PortDirection.Input, DataType = typeof(CalibImage), ColorHex = "#4CAF50" },
                    new PortDef { Name = "ImagePts", Direction = PortDirection.Input, DataType = typeof(Point2D[]), ColorHex = "#2196F3", IsOptional = true },
                    new PortDef { Name = "Transform", Direction = PortDirection.Output, DataType = typeof(AffineTransform), ColorHex = "#E91E63" }
                }
            },
            new OperatorDef
            {
                TypeId = "img_to_world",
                DisplayName = "坐标转换",
                Description = "像素坐标→世界坐标。0 个点时跳过（空 World）。",
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
                Description = "单应矩阵透视标定。默认 targetSpace=image：像素→校正后图像坐标(px)，后续检测/量测仍在图像系。targetSpace=world 时为像素→世界(mm)。目标点可填 worldPoints/worldPointsFile（与九点相同格式）。至少 4 对点。",
                Category = "标定",
                Params =
                {
                    new OperatorParam
                    {
                        Name = "targetSpace",
                        DisplayName = "目标坐标系",
                        DefaultValue = "image",
                        Description = "image/图像=输出仍为图像像素坐标；world/世界=输出为世界 mm（与九点标定一致）",
                        Options = new List<string> { "image", "world", "图像", "世界" }
                    },
                    new OperatorParam
                    {
                        Name = "worldPointsFile",
                        DisplayName = "目标坐标文件",
                        DefaultValue = "",
                        Description = "可选；每行 x,y。image 模式为校正后图像坐标(px)；world 模式为世界 mm。相对路径相对 .flow.json 目录。"
                    },
                    new OperatorParam
                    {
                        Name = "worldPoints",
                        DisplayName = "目标坐标点",
                        DefaultValue = "0,0;1000,0;1000,800;0,800",
                        Description = "未填文件时使用。image 模式填校正平面四角/网格(px)；world 模式填世界 mm。至少 4 点。"
                    },
                    new OperatorParam
                    {
                        Name = "pixelPickMode",
                        DisplayName = "像素取点方式",
                        DefaultValue = "manual",
                        Description = "manual/手选=在图像上按世界点列表手选像素；detected/匹配检测点=在 ImagePts 附近点选；auto=数量一致时按顺序标定否则弹窗",
                        Options = new List<string> { "manual", "detected", "auto", "手选", "匹配检测点" }
                    },
                    new OperatorParam
                    {
                        Name = "confirmCorrespondence",
                        DisplayName = "图像确认对应",
                        DefaultValue = "true",
                        Description = "true=弹窗确认；false=仅 auto 且 ImagePts 数量一致时按顺序标定（手选模式仍会弹窗）",
                        Options = new List<string> { "true", "false" }
                    },
                    new OperatorParam
                    {
                        Name = "showVerifyPreview",
                        DisplayName = "标定验证预览",
                        DefaultValue = "true",
                        Description = "标定成功后叠加显示像素点网格",
                        Options = new List<string> { "true", "false" }
                    }
                },
                Ports =
                {
                    new PortDef { Name = "Image", Direction = PortDirection.Input, DataType = typeof(CalibImage), ColorHex = "#4CAF50" },
                    new PortDef { Name = "ImagePts", Direction = PortDirection.Input, DataType = typeof(Point2D[]), ColorHex = "#2196F3", IsOptional = true },
                    new PortDef { Name = "WorldPts", Direction = PortDirection.Input, DataType = typeof(Point2D[]), ColorHex = "#2196F3", IsOptional = true },
                    new PortDef { Name = "H", Direction = PortDirection.Output, DataType = typeof(HomographyTransform), ColorHex = "#E91E63" }
                }
            },
            new OperatorDef
            {
                TypeId = "img_to_world_homography",
                DisplayName = "坐标转换(H)",
                Description = "应用单应 H。H 为 image 标定时输出 Mapped(校正后图像 px)；为 world 标定时输出 World(mm)。targetSpace=auto 时跟随 H。",
                Category = "标定",
                Params =
                {
                    new OperatorParam
                    {
                        Name = "targetSpace",
                        DisplayName = "目标坐标系",
                        DefaultValue = "auto",
                        Description = "auto=跟随 H；image=强制输出 Mapped(px)；world=强制输出 World(mm)",
                        Options = new List<string> { "auto", "image", "world", "图像", "世界" }
                    }
                },
                Ports =
                {
                    new PortDef { Name = "Pixel", Direction = PortDirection.Input, DataType = typeof(Point2D[]), ColorHex = "#2196F3" },
                    new PortDef { Name = "H", Direction = PortDirection.Input, DataType = typeof(HomographyTransform), ColorHex = "#E91E63" },
                    new PortDef { Name = "Mapped", Direction = PortDirection.Output, DataType = typeof(Point2D[]), ColorHex = "#4CAF50" },
                    new PortDef { Name = "World", Direction = PortDirection.Output, DataType = typeof(Point2D[]), ColorHex = "#2196F3", IsOptional = true }
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
                Description = "使用二次多项式进行像素->世界坐标转换。0 个点时跳过（空 World）。",
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
                TypeId = "plane_to_base_handeye",
                DisplayName = "平面→基座(手眼)",
                Description =
                    "固定相机：将平面坐标系下的点列 (Point2D，即 Xp,Yp) 经手眼标定得到的 4×4 刚体变换到机器人基座系，输出 CalibPoint3D[]。平面内第三维用参数 planeZ（默认 0，与轨迹共面时通常保持 0）。手眼 JSON：baseFromPlaneRowMajor 为长度 16 的行主序数组，或 baseFromPlane 为 4×4 二维数组；乘法约定为 [xb,yb,zb,wb]^T = M * [xp,yp,zp,1]^T。可连接 HandEyeJson 字符串覆盖文件；未连接时从 filePath 读取（相对路径相对当前流程 .flow.json 所在目录，组合子流程内相对子流程文件目录）。",
                Category = "标定",
                Params =
                {
                    new OperatorParam
                    {
                        Name = "filePath",
                        DisplayName = "手眼 JSON 路径",
                        DefaultValue = "handeye_plane_to_base.json",
                        Description = "含 baseFromPlaneRowMajor 或 baseFromPlane；未连接 HandEyeJson 时读取此文件"
                    },
                    new OperatorParam
                    {
                        Name = "planeZ",
                        DisplayName = "平面坐标 Z",
                        DefaultValue = "0",
                        Description = "点在平面坐标系中的 Z（mm 等，与标定单位一致）；共面轨迹一般为 0"
                    }
                },
                Ports =
                {
                    new PortDef { Name = "Points", Direction = PortDirection.Input, DataType = typeof(Point2D[]), ColorHex = "#2196F3" },
                    new PortDef { Name = "BarIds", Direction = PortDirection.Input, DataType = typeof(int[]), ColorHex = "#FFC107", IsOptional = true },
                    new PortDef { Name = "GroupBarIds", Direction = PortDirection.Input, DataType = typeof(int[]), ColorHex = "#FFEB3B", IsOptional = true },
                    new PortDef { Name = "HandEyeJson", Direction = PortDirection.Input, DataType = typeof(string), ColorHex = "#607D8B", IsOptional = true },
                    new PortDef { Name = "Points3D", Direction = PortDirection.Output, DataType = typeof(CalibPoint3D[]), ColorHex = "#00BCD4" },
                    new PortDef { Name = "BarIds", Direction = PortDirection.Output, DataType = typeof(int[]), ColorHex = "#FFC107", IsOptional = true }
                }
            },
            new OperatorDef
            {
                TypeId = "contours_pixel_to_world",
                DisplayName = "轮廓像素→世界",
                Description = "将像素条带轮廓 (int) 逐点变换为世界坐标条带 (double)。请只连接 Affine 的 Transform、或 H、或 Poly 之一。输出 ContoursWorld 可接「轮廓转焊道路径」：焊道算子会用参数 planarZ 将 XY 抬成基座 3D（或改用 ContoursBase3D / 平面→基座后再 SamplePts）。",
                Category = "标定",
                Ports =
                {
                    new PortDef { Name = "Contours", Direction = PortDirection.Input, DataType = typeof(ValueTuple<int[], int[], int[], int>), ColorHex = "#9C27B0" },
                    new PortDef { Name = "Transform", Direction = PortDirection.Input, DataType = typeof(AffineTransform), ColorHex = "#E91E63" },
                    new PortDef { Name = "H", Direction = PortDirection.Input, DataType = typeof(HomographyTransform), ColorHex = "#E91E63" },
                    new PortDef { Name = "Poly", Direction = PortDirection.Input, DataType = typeof(Poly2DTransform), ColorHex = "#E91E63" },
                    new PortDef { Name = "ContoursWorld", Direction = PortDirection.Output, DataType = typeof(ValueTuple<double[], double[], int[], int>), ColorHex = "#7B1FA2" }
                }
            },
            new OperatorDef
            {
                TypeId = "simplify_contours_to_corners",
                DisplayName = "轮廓四角点",
                Description = "使用PCA确定每个轮廓的主方向，提取四个对称极点（左、右、上、下）。按BarIds分段处理，每条轮廓输出4个角点。",
                Category = "预处理",
                Ports =
                {
                    new PortDef { Name = "In", Direction = PortDirection.Input, DataType = typeof(Point2D[]), ColorHex = "#2196F3" },
                    new PortDef { Name = "BarIds", Direction = PortDirection.Input, DataType = typeof(int[]), ColorHex = "#FFC107" },
                    new PortDef { Name = "Out", Direction = PortDirection.Output, DataType = typeof(Point2D[]), ColorHex = "#2196F3" },
                    new PortDef { Name = "OutBarIds", Direction = PortDirection.Output, DataType = typeof(int[]), ColorHex = "#FFC107" }
                }
            },
            new OperatorDef
            {
                TypeId = "contour_perpendicular_entry",
                DisplayName = "轮廓垂直进入",
                Description = "生成带垂直进入/退出的焊头点列（基座3D）：从回退点接近轮廓时，在接近轨迹前有一段垂直于轮廓平面的距离；退出轨迹后也有一段垂直距离后再回退到待机点。输出 BarIds 与 Points 等长（逐点条号），供 send_plc 按条分批写 D800。输入优先级同「轮廓转焊道路径」。",
                Category = "标定",
                Ports =
                {
                    new PortDef { Name = "Contours", Direction = PortDirection.Input, DataType = typeof(ValueTuple<int[], int[], int[], int>), ColorHex = "#9C27B0", IsOptional = true },
                    new PortDef { Name = "ContoursWorld", Direction = PortDirection.Input, DataType = typeof(ValueTuple<double[], double[], int[], int>), ColorHex = "#7B1FA2", IsOptional = true },
                    new PortDef { Name = "ContoursBase3D", Direction = PortDirection.Input, DataType = typeof(ValueTuple<double[], double[], double[], int[], int>), ColorHex = "#00ACC1", IsOptional = true },
                    new PortDef { Name = "SamplePts", Direction = PortDirection.Input, DataType = typeof(CalibPoint3D[]), ColorHex = "#00BCD4", IsOptional = true },
                    new PortDef { Name = "BarIds", Direction = PortDirection.Input, DataType = typeof(int[]), ColorHex = "#FFC107", IsOptional = true },
                    new PortDef { Name = "Points", Direction = PortDirection.Output, DataType = typeof(CalibPoint3D[]), ColorHex = "#00BCD4" },
                    new PortDef { Name = "BarIds", Direction = PortDirection.Output, DataType = typeof(int[]), ColorHex = "#FFC107" }
                },
                Params =
                {
                    new OperatorParam { Name = "retreatX", DisplayName = "回退点X", DefaultValue = "0", Description = "待机点 X（基座 mm）" },
                    new OperatorParam { Name = "retreatY", DisplayName = "回退点Y", DefaultValue = "0", Description = "待机点 Y（基座 mm）" },
                    new OperatorParam { Name = "retreatZ", DisplayName = "回退点Z", DefaultValue = "0", Description = "待机点 Z（基座 mm）" },
                    new OperatorParam { Name = "planarZ", DisplayName = "平面条带Z", DefaultValue = "0", Description = "仅 ContoursWorld：各点抬升到基座 Z" },
                    new OperatorParam { Name = "verticalDist", DisplayName = "垂直距离", DefaultValue = "30", Description = "进入轮廓前和退出轮廓后的垂直段距离（mm），沿进入点/退出点的法线方向" },
                    new OperatorParam { Name = "leadIn", DisplayName = "首条从回退点接近", DefaultValue = "true", Description = "true=第一条轮廓前从回退点接近；false=直接从首点开始" },
                    new OperatorParam { Name = "leadOut", DisplayName = "末条后回退", DefaultValue = "true", Description = "true=最后一条轮廓结束后回退到待机点；false=不返回" },
                    new OperatorParam { Name = "transitSpacing", DisplayName = "移行插补间距", DefaultValue = "0", Description = "移行直线插补步长（mm）；0=仅写移行终点" },
                    new OperatorParam { Name = "closeContour", DisplayName = "轮廓闭合", DefaultValue = "false", Description = "每条轮廓点走完后是否闭合到起点" }
                }
            },
            new OperatorDef
            {
                TypeId = "contours_to_weld_path",
                DisplayName = "轮廓转焊道路径",
                Description =
                    "按轮廓顺序生成焊头点列（机器人基座坐标系 CalibPoint3D[]）：每条轮廓走完后先尽量沿轮廓闭合到起点（见 closeContour），再到「回退/待机」三维点，再接近下一条轮廓。输入优先级：ContoursBase3D（平面 X、Y、Z 条带）> ContoursWorld（双精度 XY 条带，Z 由参数 planarZ 抬升）> Contours（像素条带，Z=0）> SamplePts（基座 3D 采样点 + 可选 BarIds）。barSplit=auto 时仅「同条号且端距小」才合并分段。未接 BarIds 时整条 SamplePts 视为单条轮廓。",
                Category = "标定",
                Ports =
                {
                    new PortDef { Name = "Contours", Direction = PortDirection.Input, DataType = typeof(ValueTuple<int[], int[], int[], int>), ColorHex = "#9C27B0", IsOptional = true },
                    new PortDef { Name = "ContoursWorld", Direction = PortDirection.Input, DataType = typeof(ValueTuple<double[], double[], int[], int>), ColorHex = "#7B1FA2", IsOptional = true },
                    new PortDef
                    {
                        Name = "ContoursBase3D",
                        Direction = PortDirection.Input,
                        DataType = typeof(ValueTuple<double[], double[], double[], int[], int>),
                        ColorHex = "#00ACC1",
                        IsOptional = true
                    },
                    new PortDef { Name = "SamplePts", Direction = PortDirection.Input, DataType = typeof(CalibPoint3D[]), ColorHex = "#00BCD4", IsOptional = true },
                    new PortDef { Name = "BarIds", Direction = PortDirection.Input, DataType = typeof(int[]), ColorHex = "#FFC107", IsOptional = true },
                    new PortDef { Name = "Points", Direction = PortDirection.Output, DataType = typeof(CalibPoint3D[]), ColorHex = "#00BCD4" }
                },
                Params =
                {
                    new OperatorParam { Name = "retreatX", DisplayName = "回退点X", DefaultValue = "0", Description = "待机点 X（基座 mm 等，与点列同单位）" },
                    new OperatorParam { Name = "retreatY", DisplayName = "回退点Y", DefaultValue = "0", Description = "待机点 Y（基座）" },
                    new OperatorParam { Name = "retreatZ", DisplayName = "回退点Z", DefaultValue = "0", Description = "待机点 Z（基座）" },
                    new OperatorParam { Name = "planarZ", DisplayName = "平面条带Z", DefaultValue = "0", Description = "仅 ContoursWorld：各点抬升到基座 Z（ContoursBase3D 不需要）" },
                    new OperatorParam { Name = "leadIn", DisplayName = "首条从回退点接近", DefaultValue = "true", Description = "true=第一条轮廓前先插入 回退点→轮廓起点 的移行；false=假定焊头已在首轮廓起点" },
                    new OperatorParam { Name = "leadOut", DisplayName = "末条后回退", DefaultValue = "true", Description = "true=最后一条轮廓结束后再插入到回退点" },
                    new OperatorParam { Name = "transitSpacing", DisplayName = "移行插补间距", DefaultValue = "0", Description = "移行直线插补步长（与坐标同单位）；0=仅写移行终点" },
                    new OperatorParam { Name = "barSplit", DisplayName = "轮廓分段", DefaultValue = "auto", Description = "auto=仅当相邻分段为同一 BarId 且端距小于阈值时才合并（避免不同轮廓焊成直连）；by_bar=严格按条带/条号分段不合并；single=整条点列一条轨迹(无条间回退)" },
                    new OperatorParam { Name = "segmentJoinMaxDist", DisplayName = "合并端距上限", DefaultValue = "0", Description = "仅 barSplit=auto：>0 时用该值作为段间合并阈值；0=按点列步长自动推断（3D 欧氏距离）" },
                    new OperatorParam { Name = "sanitizeBarIds", DisplayName = "条号轮廓内对齐", DefaultValue = "true", Description = "仅 SamplePts+BarIds：在轮廓内窄步长上若条号突变则改为与前点相同（一条轮廓一个 BarId）。barSplit=by_bar 时不生效。设为 false 可保留原始条号" },
                    new OperatorParam { Name = "closeContour", DisplayName = "轮廓闭合", DefaultValue = "auto", Description = "每条轮廓点走完后是否插到起点闭合：auto=首尾间距相对周长或边长中位数较小时沿直线闭合；true=始终尝试闭合；false=不闭合。闭合后再回待机点，避免未闭环就回退" }
                }
            },
            new OperatorDef
            {
                TypeId = "display_calibration",
                DisplayName = "显示标定结果",
                Description = "统一显示 Affine/Homography/Poly2D；棋盘格标定可接 CalibrationJson（含内参+每视图外参，并在摘要末尾附带用法说明）或仅接 Intrinsics 结构体",
                Category = "标定",
                Ports =
                {
                    new PortDef { Name = "CalibrationJson", Direction = PortDirection.Input, DataType = typeof(string), ColorHex = "#607D8B" },
                    new PortDef { Name = "Transform", Direction = PortDirection.Input, DataType = typeof(AffineTransform), ColorHex = "#E91E63" },
                    new PortDef { Name = "H", Direction = PortDirection.Input, DataType = typeof(HomographyTransform), ColorHex = "#E91E63" },
                    new PortDef { Name = "Poly", Direction = PortDirection.Input, DataType = typeof(Poly2DTransform), ColorHex = "#E91E63" },
                    new PortDef { Name = "Intrinsics", Direction = PortDirection.Input, DataType = typeof(CameraIntrinsics), ColorHex = "#E91E63" },
                    new PortDef { Name = "Out", Direction = PortDirection.Output, DataType = typeof(string), ColorHex = "#607D8B" }
                }
            },
            new OperatorDef
            {
                TypeId = "save_calibration_result",
                DisplayName = "保存标定结果",
                Description = "将 CalibrationJson（棋盘多视图）、Affine / Homography / Poly2D、Intrinsics 以 JSON 落盘（可同时写入多项）。至少连接一路输入。相对路径相对当前 .flow.json 目录。",
                Category = "标定",
                Params =
                {
                    new OperatorParam { Name = "filePath", DisplayName = "保存路径", DefaultValue = "calibration_result.json", Description = "UTF-8 JSON；目录不存在时自动创建" }
                },
                Ports =
                {
                    new PortDef { Name = "CalibrationJson", Direction = PortDirection.Input, DataType = typeof(string), ColorHex = "#607D8B", IsOptional = true },
                    new PortDef { Name = "Transform", Direction = PortDirection.Input, DataType = typeof(AffineTransform), ColorHex = "#E91E63", IsOptional = true },
                    new PortDef { Name = "H", Direction = PortDirection.Input, DataType = typeof(HomographyTransform), ColorHex = "#E91E63", IsOptional = true },
                    new PortDef { Name = "Poly", Direction = PortDirection.Input, DataType = typeof(Poly2DTransform), ColorHex = "#E91E63", IsOptional = true },
                    new PortDef { Name = "Intrinsics", Direction = PortDirection.Input, DataType = typeof(CameraIntrinsics), ColorHex = "#E91E63", IsOptional = true }
                }
            },
            new OperatorDef
            {
                TypeId = "load_calibration_result",
                DisplayName = "读取标定结果",
                Description = "从 JSON 恢复标定（由「保存标定结果」生成）。上游算子 Out → After 可排在任意算子之后读取；Out 透传 After 供下游继续。同一次运行中若存在「保存标定结果」，会先落盘再读取。坐标转换也可直连「标定」的 Transform。仅连接需要的输出端口。",
                Category = "标定",
                Params =
                {
                    new OperatorParam { Name = "filePath", DisplayName = "文件路径", DefaultValue = "", Description = "可选；填写后自动读取（相对路径相对当前 .flow.json 目录）；留空则弹窗选择" }
                },
                Ports =
                {
                    new PortDef { Name = "After", Direction = PortDirection.Input, DataType = typeof(object), ColorHex = "#607D8B", IsOptional = true },
                    new PortDef { Name = "CalibrationJson", Direction = PortDirection.Output, DataType = typeof(string), ColorHex = "#607D8B" },
                    new PortDef { Name = "Transform", Direction = PortDirection.Output, DataType = typeof(AffineTransform), ColorHex = "#E91E63" },
                    new PortDef { Name = "H", Direction = PortDirection.Output, DataType = typeof(HomographyTransform), ColorHex = "#E91E63" },
                    new PortDef { Name = "Poly", Direction = PortDirection.Output, DataType = typeof(Poly2DTransform), ColorHex = "#E91E63" },
                    new PortDef { Name = "Intrinsics", Direction = PortDirection.Output, DataType = typeof(CameraIntrinsics), ColorHex = "#E91E63" },
                    new PortDef { Name = "Out", Direction = PortDirection.Output, DataType = typeof(object), ColorHex = "#607D8B", IsOptional = true }
                }
            },
            new OperatorDef
            {
                TypeId = "light_connect",
                DisplayName = "光源连接",
                Description = "连接光源控制器（以太网 IP 或串口）。流程内保持连接直至「光源断开」。",
                Category = "光源",
                Params =
                {
                    new OperatorParam
                    {
                        Name = "connectMode",
                        DisplayName = "连接方式",
                        DefaultValue = "ip",
                        Description = "ip=以太网；serial=串口",
                        Options = new List<string> { "ip", "serial" }
                    },
                    new OperatorParam { Name = "ip", DisplayName = "IP", DefaultValue = "192.168.0.100", Description = "connectMode=ip 时有效" },
                    new OperatorParam { Name = "comPort", DisplayName = "串口号", DefaultValue = "1", Description = "connectMode=serial 时 COM 口编号" },
                    new OperatorParam { Name = "timeoutSec", DisplayName = "超时(秒)", DefaultValue = "1", Description = "IP 连接超时 1~30" }
                },
                Ports =
                {
                    new PortDef { Name = "Connected", Direction = PortDirection.Output, DataType = typeof(bool), ColorHex = "#FFC107" }
                }
            },
            new OperatorDef
            {
                TypeId = "light_disconnect",
                DisplayName = "光源断开",
                Description = "断开流程内光源控制器连接。",
                Category = "光源",
                Ports =
                {
                    new PortDef { Name = "Disconnected", Direction = PortDirection.Output, DataType = typeof(bool), ColorHex = "#607D8B" }
                }
            },
            new OperatorDef
            {
                TypeId = "light_set",
                DisplayName = "光源控制",
                Description = "设置亮度/脉宽等。上游 Out→After 可排在任意算子之后；Value 输入可接循环 StepValue 覆盖参数 value。autoConnect=true 时未连接则按参数自动连接。",
                Category = "光源",
                Params =
                {
                    new OperatorParam
                    {
                        Name = "action",
                        DisplayName = "动作",
                        DefaultValue = "brightness",
                        Description = "brightness=通道亮度；strobe=脉宽；multi=多通道(channels)；keepalive=心跳",
                        Options = new List<string> { "brightness", "strobe", "intCycle", "triMode", "lightState", "keepalive", "multi" }
                    },
                    new OperatorParam { Name = "channel", DisplayName = "通道", DefaultValue = "1", Description = "1 起，单通道动作时使用" },
                    new OperatorParam { Name = "value", DisplayName = "数值(默认)", DefaultValue = "200", Description = "未连接 Value 输入时使用；亮度/脉宽/模式等（依 action）" },
                    new OperatorParam { Name = "channels", DisplayName = "多通道", DefaultValue = "", Description = "action=multi 时，如 1:200;2:150;3:180" },
                    new OperatorParam
                    {
                        Name = "autoConnect",
                        DisplayName = "自动连接",
                        DefaultValue = "true",
                        Description = "未连接时按下方 IP/串口参数连接",
                        Options = new List<string> { "true", "false" }
                    },
                    new OperatorParam
                    {
                        Name = "connectMode",
                        DisplayName = "连接方式",
                        DefaultValue = "ip",
                        Options = new List<string> { "ip", "serial" }
                    },
                    new OperatorParam { Name = "ip", DisplayName = "IP", DefaultValue = "192.168.0.100", Description = "autoConnect 用" },
                    new OperatorParam { Name = "comPort", DisplayName = "串口号", DefaultValue = "1", Description = "autoConnect 用" },
                    new OperatorParam { Name = "timeoutSec", DisplayName = "超时(秒)", DefaultValue = "1", Description = "IP 连接超时" }
                },
                Ports =
                {
                    new PortDef { Name = "After", Direction = PortDirection.Input, DataType = typeof(object), ColorHex = "#607D8B", IsOptional = true },
                    new PortDef { Name = "Value", Direction = PortDirection.Input, DataType = typeof(object), ColorHex = "#FFC107", IsOptional = true },
                    new PortDef { Name = "Out", Direction = PortDirection.Output, DataType = typeof(object), ColorHex = "#607D8B", IsOptional = true },
                    new PortDef { Name = "Ok", Direction = PortDirection.Output, DataType = typeof(bool), ColorHex = "#FFC107" }
                }
            },
            new OperatorDef
            {
                TypeId = "plc_read_camera_capture",
                DisplayName = "PLC 读相机拍照信号",
                Description = "读取相机开始拍照位（默认 D1800L）。可将上游任意算子 Out 连到 After，保证在本算子之后再读 PLC。",
                Category = "输出",
                Params =
                {
                    new OperatorParam { Name = "signalRegister", DisplayName = "信号地址", DefaultValue = "", Description = "留空则用 plc_config CameraCaptureStart（D1800L）" },
                    new OperatorParam { Name = "bit", DisplayName = "位号", DefaultValue = "0", Description = "相对 L/H 后缀后的位偏移，D1800L 一般为 bit0" }
                },
                Ports =
                {
                    new PortDef { Name = "After", Direction = PortDirection.Input, DataType = typeof(object), ColorHex = "#607D8B", IsOptional = true },
                    new PortDef { Name = "Triggered", Direction = PortDirection.Output, DataType = typeof(bool), ColorHex = "#5E35B1" },
                    new PortDef { Name = "Out", Direction = PortDirection.Output, DataType = typeof(object), ColorHex = "#607D8B", IsOptional = true }
                }
            },
            new OperatorDef
            {
                TypeId = "plc_wait_camera_capture",
                DisplayName = "PLC 监听相机拍照",
                Description = "阻塞轮询直到拍照位为 1。上游算子 Out → After 可排在任意算子之后执行；Out 透传 After 数据供下游继续。",
                Category = "输出",
                Params =
                {
                    new OperatorParam { Name = "signalRegister", DisplayName = "信号地址", DefaultValue = "", Description = "留空则用 plc_config CameraCaptureStart（D1800L）" },
                    new OperatorParam { Name = "bit", DisplayName = "位号", DefaultValue = "0", Description = "D1800L = D1800 低字节 bit0" },
                    new OperatorParam { Name = "timeoutMs", DisplayName = "超时(ms)", DefaultValue = "60000", Description = "超时未收到信号则流程失败" },
                    new OperatorParam { Name = "pollIntervalMs", DisplayName = "轮询间隔(ms)", DefaultValue = "20", Description = "读 PLC 间隔，5~5000" },
                    new OperatorParam
                    {
                        Name = "clearAfter",
                        DisplayName = "收到后清 0",
                        DefaultValue = "false",
                        Description = "true=触发后复位该位，便于 PLC 下次再发脉冲",
                        Options = new List<string> { "false", "true" }
                    }
                },
                Ports =
                {
                    new PortDef { Name = "After", Direction = PortDirection.Input, DataType = typeof(object), ColorHex = "#607D8B", IsOptional = true },
                    new PortDef { Name = "Triggered", Direction = PortDirection.Output, DataType = typeof(bool), ColorHex = "#5E35B1" },
                    new PortDef { Name = "Out", Direction = PortDirection.Output, DataType = typeof(object), ColorHex = "#607D8B", IsOptional = true }
                }
            },
            new OperatorDef
            {
                TypeId = "plc_connect",
                DisplayName = "PLC连接",
                Description = "连接 Flow 专用 PLC 会话（信捷 Modbus TCP）。后续 PLC 算子将复用该连接。",
                Category = "输出",
                Params =
                {
                    new OperatorParam { Name = "ip", DisplayName = "PLC IP", DefaultValue = "192.168.6.6", Description = "目标 PLC IP" },
                    new OperatorParam { Name = "port", DisplayName = "PLC端口", DefaultValue = "502", Description = "默认 502" },
                    new OperatorParam { Name = "station", DisplayName = "PLC站号", DefaultValue = "", Description = "留空用 plc_config 的 ModbusStation；可覆盖为 1~247" }
                },
                Ports =
                {
                    new PortDef { Name = "Connected", Direction = PortDirection.Output, DataType = typeof(bool), ColorHex = "#4CAF50" }
                }
            },
            new OperatorDef
            {
                TypeId = "plc_disconnect",
                DisplayName = "PLC断开",
                Description = "断开 Flow 专用 PLC 会话。若当前连接来自 PLC 页会话，则不会主动断开。",
                Category = "输出",
                Ports =
                {
                    new PortDef { Name = "Disconnected", Direction = PortDirection.Output, DataType = typeof(bool), ColorHex = "#F44336" }
                }
            },
            new OperatorDef
            {
                TypeId = "plc_read_weld_done",
                DisplayName = "PLC 读焊接完成",
                Description = "读取 D803L（PLC→上位机，1=PLC 侧焊接完成）。",
                Category = "输出",
                Params =
                {
                    new OperatorParam { Name = "flagRegister", DisplayName = "标志地址", DefaultValue = "", Description = "留空则用 plc_config WeldDoneFlag（D803L）" }
                },
                Ports =
                {
                    new PortDef { Name = "Done", Direction = PortDirection.Output, DataType = typeof(bool), ColorHex = "#9C27B0" }
                }
            },
            new OperatorDef
            {
                TypeId = "plc_wait_weld_done",
                DisplayName = "PLC 监听焊接完成",
                Description = "阻塞轮询直到 D803L=1（PLC→上位机焊接完成）。建议接在「发送PLC」及「PLC 通知轨迹已下发」之后。",
                Category = "输出",
                Params =
                {
                    new OperatorParam { Name = "flagRegister", DisplayName = "标志地址", DefaultValue = "", Description = "留空则用 plc_config WeldDoneFlag（D803L）" },
                    new OperatorParam { Name = "bit", DisplayName = "位号", DefaultValue = "0", Description = "D803L = D803 低字节 bit0" },
                    new OperatorParam { Name = "timeoutMs", DisplayName = "超时(ms)", DefaultValue = "600000", Description = "焊接超时则流程失败" },
                    new OperatorParam { Name = "pollIntervalMs", DisplayName = "轮询间隔(ms)", DefaultValue = "50", Description = "读 PLC 间隔" },
                    new OperatorParam
                    {
                        Name = "clearAfter",
                        DisplayName = "收到后清 0",
                        DefaultValue = "true",
                        Description = "true=收到完成后清 D803，便于下一轮",
                        Options = new List<string> { "true", "false" }
                    }
                },
                Ports =
                {
                    new PortDef { Name = "After", Direction = PortDirection.Input, DataType = typeof(bool), ColorHex = "#607D8B", IsOptional = true },
                    new PortDef { Name = "Done", Direction = PortDirection.Output, DataType = typeof(bool), ColorHex = "#9C27B0" }
                }
            },
            new OperatorDef
            {
                TypeId = "plc_clear_weld_done",
                DisplayName = "PLC 清焊接完成",
                Description = "将焊接完成标志清 0（默认 D803L，PLC→上位机）。下一轮流程开始前应执行。",
                Category = "输出",
                Params =
                {
                    new OperatorParam { Name = "flagRegister", DisplayName = "标志地址", DefaultValue = "D803L", Description = "PLC→上位机 完成标志" }
                },
                Ports =
                {
                    new PortDef { Name = "Cleared", Direction = PortDirection.Output, DataType = typeof(bool), ColorHex = "#9C27B0" }
                }
            },
            new OperatorDef
            {
                TypeId = "plc_set_weld_done_to_plc",
                DisplayName = "PLC 通知轨迹已下发",
                Description = "上位机→PLC：GVAR 下发后置 D804L=1（默认）。建议接在「发送PLC」与「PLC 监听焊接完成」之间。",
                Category = "输出",
                Params =
                {
                    new OperatorParam { Name = "flagRegister", DisplayName = "标志地址", DefaultValue = "", Description = "留空则用 plc_config WeldDoneHostFlag（D804L）" }
                },
                Ports =
                {
                    new PortDef { Name = "After", Direction = PortDirection.Input, DataType = typeof(bool), ColorHex = "#607D8B", IsOptional = true },
                    new PortDef { Name = "Signaled", Direction = PortDirection.Output, DataType = typeof(bool), ColorHex = "#673AB7" }
                }
            },
            new OperatorDef
            {
                TypeId = "plc_pou_enable",
                DisplayName = "PLC POU使能",
                Description = "写 POU 使能位到 PLC（默认 D801L.bit0=ON）。",
                Category = "输出",
                Params =
                {
                    new OperatorParam { Name = "enableRegister", DisplayName = "使能地址", DefaultValue = "D801L", Description = "信捷 D801L = D801 低字节 bit0" },
                    new OperatorParam
                    {
                        Name = "enable",
                        DisplayName = "使能",
                        DefaultValue = "ON",
                        Description = "ON=置位，OFF=复位；未接 Enable 输入时生效",
                        Options = new List<string> { "ON", "OFF" }
                    }
                },
                Ports =
                {
                    new PortDef { Name = "Enable", Direction = PortDirection.Input, DataType = typeof(bool), ColorHex = "#4CAF50", IsOptional = true },
                    new PortDef { Name = "Enabled", Direction = PortDirection.Output, DataType = typeof(bool), ColorHex = "#4CAF50" }
                }
            },
            new OperatorDef
            {
                TypeId = "plc_set_segment_count",
                DisplayName = "PLC 设置线段数量",
                Description = "将待下发的线段条数写入 PLC（默认 D800，16 位整数）。可接数组长度或手动 segmentCount 参数。",
                Category = "输出",
                Params =
                {
                    new OperatorParam { Name = "countRegister", DisplayName = "数量寄存器", DefaultValue = "800", Description = "信捷 D800 → Modbus 寄存器 800；也可填 D800" },
                    new OperatorParam { Name = "segmentCount", DisplayName = "线段数量", DefaultValue = "", Description = "未接 Count 输入时使用；留空则必须接 Count" }
                },
                Ports =
                {
                    new PortDef { Name = "Count", Direction = PortDirection.Input, DataType = typeof(int), ColorHex = "#607D8B", IsOptional = true },
                    new PortDef { Name = "Count", Direction = PortDirection.Output, DataType = typeof(int), ColorHex = "#607D8B" }
                }
            },
            new OperatorDef
            {
                TypeId = "send_plc",
                DisplayName = "发送PLC",
                Description = "与 PLC 页 Write All 相同：须先 PLC连接；优先 GvarList；否则 Points3D/Points 生成 GVAR（相邻点→线段，closePolyline 默认补末点→首点闭合）。separate_batch=按条分批写 D800+GVAR；下发完成通知请使用「PLC通知轨迹已下发」。",
                Category = "输出",
                Params =
                {
                    new OperatorParam
                    {
                        Name = "splitByBar",
                        DisplayName = "按条号下发",
                        DefaultValue = "none",
                        Description = "none=整列连续线段；break_segment=同条号内才成段；separate_batch=每个不同 BarId(焊道/落格条号)一批 GVAR，须接与点列等长的 BarIds(来自轨迹 GroupBarIds→简化 OutBarIds)，批次数=条号种类数",
                        Options = new List<string> { "none", "break_segment", "separate_batch" }
                    },
                    new OperatorParam
                    {
                        Name = "expectedBatchCount",
                        DisplayName = "期望批次数",
                        DefaultValue = "0",
                        Description = "仅 separate_batch：>0 时若实际批次数不符则写日志警告（如 16 格模板填 16）；0=不检查"
                    },
                    new OperatorParam
                    {
                        Name = "barGvarLayout",
                        DisplayName = "分批地址布局",
                        DefaultValue = "overwrite",
                        Description = "仅 separate_batch：overwrite=每批都从 gvarStart 写；stack=按批依次 D30000、D30000+28×n…",
                        Options = new List<string> { "overwrite", "stack" }
                    },
                    new OperatorParam
                    {
                        Name = "usePlcPageGvar",
                        DisplayName = "使用PLC页GVAR表",
                        DefaultValue = "false",
                        Description = "true=下发 PLC 页表格中与 Write All 相同的数据（须先在 PLC 页填好/读取 GVAR）",
                        Options = new List<string> { "true", "false" }
                    },
                    new OperatorParam { Name = "gvarStartRegister", DisplayName = "GVAR起始地址", DefaultValue = "D30000", Description = "第 i 条从 gvarStart + i×28 起；与 GvarList.StartAddress 一致" },
                    new OperatorParam { Name = "baseRegister", DisplayName = "起始地址(兼容)", DefaultValue = "", Description = "已废弃：仅当未填 gvarStartRegister 时作为 GVAR 起始地址" },
                    new OperatorParam { Name = "gvarType", DisplayName = "GVAR类型", DefaultValue = "1", Description = "类型值（1=线段，与 PLC 程序约定一致）" },
                    new OperatorParam { Name = "zDefault", DisplayName = "默认Z(mm)", DefaultValue = "0", Description = "仅对 Points(Point2D[]) 生效：写入 GVAR 的 p0.z/p1.z/z0/z1；Points3D 仍用各点 Z。单位与轨迹世界坐标一致（mm）。" },
                    new OperatorParam
                    {
                        Name = "closePolyline",
                        DisplayName = "闭合折线",
                        DefaultValue = "true",
                        Description = "true=相邻点连成线段，并补最后一点→第一点闭合段（n 点闭合轮廓得 n 段）；false=开折线仅 n-1 段。首尾已重合(≤0.01mm)时不重复补段",
                        Options = new List<string> { "true", "false" }
                    },
                    new OperatorParam { Name = "maxSegmentCount", DisplayName = "最大线段数", DefaultValue = "1024", Description = "超过则中止，防止越界写 PLC" },
                    new OperatorParam
                    {
                        Name = "skipCountWrite",
                        DisplayName = "跳过写线段数",
                        DefaultValue = "true",
                        Description = "默认 true：单次下发不写 D800（可用「PLC设置线段数量」）；separate_batch 时仍每批写 D800=当批段数",
                        Options = new List<string> { "true", "false" }
                    },
                    new OperatorParam { Name = "countRegister", DisplayName = "线段数量寄存器", DefaultValue = "D800", Description = "单次下发且 skipCountWrite=false 时写入；separate_batch 时每批写入当批段数" },
                    new OperatorParam
                    {
                        Name = "runDownstreamPerBatch",
                        DisplayName = "每批执行下游",
                        DefaultValue = "true",
                        Description = "仅 separate_batch：每批写完 GVAR 后按拓扑序执行 GvarSent 连线的下游子节点（如 POU 使能、等待焊接完成）；主流程中这些节点不再重复执行",
                        Options = new List<string> { "true", "false" }
                    }
                },
                Ports =
                {
                    new PortDef { Name = "GvarList", Direction = PortDirection.Input, DataType = typeof(GVAR[]), ColorHex = "#FF9800", IsOptional = true },
                    new PortDef { Name = "Points", Direction = PortDirection.Input, DataType = typeof(Point2D[]), ColorHex = "#2196F3", IsOptional = true },
                    new PortDef { Name = "Points3D", Direction = PortDirection.Input, DataType = typeof(CalibPoint3D[]), ColorHex = "#2196F3", IsOptional = true },
                    new PortDef { Name = "BarIds", Direction = PortDirection.Input, DataType = typeof(int[]), ColorHex = "#FFC107", IsOptional = true },
                    new PortDef { Name = "GvarSent", Direction = PortDirection.Output, DataType = typeof(bool), ColorHex = "#FF9800", IsOptional = true },
                    new PortDef { Name = "BatchIndex", Direction = PortDirection.Output, DataType = typeof(int), ColorHex = "#FFC107", IsOptional = true },
                    new PortDef { Name = "BatchCount", Direction = PortDirection.Output, DataType = typeof(int), ColorHex = "#FFC107", IsOptional = true },
                    new PortDef { Name = "BatchBarId", Direction = PortDirection.Output, DataType = typeof(int), ColorHex = "#FFC107", IsOptional = true },
                    new PortDef { Name = "BatchSegmentCount", Direction = PortDirection.Output, DataType = typeof(int), ColorHex = "#FFC107", IsOptional = true }
                }
            },
            new OperatorDef
            {
                TypeId = "send_plc_point",
                DisplayName = "发送PLC(每点一点)",
                Description = "与「发送PLC」相同下发逻辑（须先 PLC连接）。默认每点一条退化 GVAR(p0=p1)。asPolylineSegments=true 时改为相邻点连成线段并可闭合。下发完成通知请使用「PLC通知轨迹已下发」。",
                Category = "输出",
                Params =
                {
                    new OperatorParam
                    {
                        Name = "splitByBar",
                        DisplayName = "按条号下发",
                        DefaultValue = "none",
                        Description = "none=整列；break_segment=同条号内成段；separate_batch=按 BarId 分批",
                        Options = new List<string> { "none", "break_segment", "separate_batch" }
                    },
                    new OperatorParam { Name = "expectedBatchCount", DisplayName = "期望批次数", DefaultValue = "0", Description = "仅 separate_batch：不符时写日志警告；0=不检查" },
                    new OperatorParam
                    {
                        Name = "barGvarLayout",
                        DisplayName = "分批地址布局",
                        DefaultValue = "overwrite",
                        Description = "仅 separate_batch：overwrite 或 stack",
                        Options = new List<string> { "overwrite", "stack" }
                    },
                    new OperatorParam
                    {
                        Name = "usePlcPageGvar",
                        DisplayName = "使用PLC页GVAR表",
                        DefaultValue = "false",
                        Description = "true=下发 PLC 页表格数据",
                        Options = new List<string> { "true", "false" }
                    },
                    new OperatorParam { Name = "gvarStartRegister", DisplayName = "GVAR起始地址", DefaultValue = "D30000", Description = "与 GvarList.StartAddress 一致" },
                    new OperatorParam { Name = "baseRegister", DisplayName = "起始地址(兼容)", DefaultValue = "", Description = "未填 gvarStartRegister 时使用" },
                    new OperatorParam { Name = "gvarType", DisplayName = "GVAR类型", DefaultValue = "1", Description = "与 PLC 程序约定一致" },
                    new OperatorParam { Name = "zDefault", DisplayName = "默认Z(mm)", DefaultValue = "0", Description = "仅对 Points(Point2D[]) 生效：每点 GVAR 的 Z；Points3D 仍用各点 Z（mm）。" },
                    new OperatorParam
                    {
                        Name = "asPolylineSegments",
                        DisplayName = "连成线段",
                        DefaultValue = "false",
                        Description = "false=每点一条退化 GVAR(p0=p1，与算子名一致)；true=按折线生成 p0→p1 线段（闭合轮廓可开 closePolyline）",
                        Options = new List<string> { "true", "false" }
                    },
                    new OperatorParam
                    {
                        Name = "closePolyline",
                        DisplayName = "闭合折线",
                        DefaultValue = "true",
                        Description = "仅 asPolylineSegments=true 时有效：补末点→首点闭合段",
                        Options = new List<string> { "true", "false" }
                    },
                    new OperatorParam { Name = "maxSegmentCount", DisplayName = "最大线段数", DefaultValue = "1024", Description = "超过则中止" },
                    new OperatorParam
                    {
                        Name = "skipCountWrite",
                        DisplayName = "跳过写线段数",
                        DefaultValue = "true",
                        Description = "单次不下写 D800；separate_batch 仍每批写当批段数",
                        Options = new List<string> { "true", "false" }
                    },
                    new OperatorParam { Name = "countRegister", DisplayName = "线段数量寄存器", DefaultValue = "D800", Description = "D800 等" },
                    new OperatorParam { Name = "runDownstreamPerBatch", DisplayName = "每批执行下游", DefaultValue = "true", Options = new List<string> { "true", "false" } }
                },
                Ports =
                {
                    new PortDef { Name = "GvarList", Direction = PortDirection.Input, DataType = typeof(GVAR[]), ColorHex = "#FF9800", IsOptional = true },
                    new PortDef { Name = "Points", Direction = PortDirection.Input, DataType = typeof(Point2D[]), ColorHex = "#2196F3", IsOptional = true },
                    new PortDef { Name = "Points3D", Direction = PortDirection.Input, DataType = typeof(CalibPoint3D[]), ColorHex = "#2196F3", IsOptional = true },
                    new PortDef { Name = "BarIds", Direction = PortDirection.Input, DataType = typeof(int[]), ColorHex = "#FFC107", IsOptional = true },
                    new PortDef { Name = "GvarSent", Direction = PortDirection.Output, DataType = typeof(bool), ColorHex = "#FF9800", IsOptional = true },
                    new PortDef { Name = "BatchIndex", Direction = PortDirection.Output, DataType = typeof(int), ColorHex = "#FFC107", IsOptional = true },
                    new PortDef { Name = "BatchCount", Direction = PortDirection.Output, DataType = typeof(int), ColorHex = "#FFC107", IsOptional = true },
                    new PortDef { Name = "BatchBarId", Direction = PortDirection.Output, DataType = typeof(int), ColorHex = "#FFC107", IsOptional = true },
                    new PortDef { Name = "BatchSegmentCount", Direction = PortDirection.Output, DataType = typeof(int), ColorHex = "#FFC107", IsOptional = true }
                }
            },
            new OperatorDef
            {
                TypeId = "flow_loop",
                DisplayName = "循环",
                Description = "按次数或无限重复执行本算子下游连线上的全部算子（前置只执行一次）。支持多路 List 输入按轮展开（如 InList/CoarseRowList/CoarseColumnList），每轮输出当前项并驱动下游。填写 stepValues 时每轮输出 StepValue。count≤0/inf/无限=无限循环。须用「运行」托管执行。",
                Category = "流程",
                Params =
                {
                    new OperatorParam { Name = "count", DisplayName = "重复次数", DefaultValue = "3", Description = "正整数=固定次数；0/inf/无限=无限循环。填写 stepValues 时以列表长度为准，忽略 count" },
                    new OperatorParam { Name = "stepValues", DisplayName = "每轮数值列表", DefaultValue = "", Description = "固定次数时可选；分号/逗号/换行分隔，如 100;200;150。每轮输出 StepValue 一个值，长度=循环次数" },
                    new OperatorParam { Name = "itemPorts", DisplayName = "列表端口名", DefaultValue = "In,In2,In3,In4,CoarseRow,CoarseColumn,CoarseAngle,CoarseScale,CoarseScore", Description = "可配置多路列表展开端口；逗号/分号/换行分隔，如 In,CoarseRow,CoarseColumn。将自动生成输入 XList 与输出 X 成对端口；可按需删减到任意数量。" },
                    new OperatorParam { Name = "intervalMs", DisplayName = "轮次间隔(ms)", DefaultValue = "0", Description = "每轮下游执行完后的等待时间，0=不等待" }
                },
                Ports =
                {
                    new PortDef { Name = "After", Direction = PortDirection.Input, DataType = typeof(object), ColorHex = "#607D8B", IsOptional = true },
                    new PortDef { Name = "Out", Direction = PortDirection.Output, DataType = typeof(object), ColorHex = "#607D8B", IsOptional = true },
                    new PortDef { Name = "Index", Direction = PortDirection.Output, DataType = typeof(int), ColorHex = "#607D8B" },
                    new PortDef { Name = "Count", Direction = PortDirection.Output, DataType = typeof(int), ColorHex = "#607D8B" },
                    new PortDef { Name = "StepValue", Direction = PortDirection.Output, DataType = typeof(double), ColorHex = "#FFC107" }
                }
            },
            new OperatorDef
            {
                TypeId = "list_pick",
                DisplayName = "列表取项",
                Description = "多路列表输入，按索引同时输出每路当前项（InList→In）。itemPorts 配置端口名，自动生成 XList 输入与 X 输出；可与 flow_loop 的 Index 或 AtIndex 输入联动取对应轮次数据。",
                Category = "流程",
                Params =
                {
                    new OperatorParam { Name = "itemPorts", DisplayName = "列表端口名", DefaultValue = "In,In2,In3", Description = "逗号/分号/换行分隔，如 In,CoarseRow,CoarseColumn。每路生成输入 XList 与输出 X 成对端口" },
                    new OperatorParam { Name = "index", DisplayName = "索引", DefaultValue = "0", Description = "取第几项（0 起）；连接 AtIndex 输入时以输入为准" }
                },
                Ports =
                {
                    new PortDef { Name = "AtIndex", Direction = PortDirection.Input, DataType = typeof(int), ColorHex = "#607D8B", IsOptional = true },
                    new PortDef { Name = "Index", Direction = PortDirection.Output, DataType = typeof(int), ColorHex = "#607D8B" },
                    new PortDef { Name = "Count", Direction = PortDirection.Output, DataType = typeof(int), ColorHex = "#607D8B" }
                }
            },
            new OperatorDef
            {
                TypeId = "flow_sink",
                DisplayName = "循环收集",
                Description = "接在循环下游：每轮将 In 追加到列表（图像、点列等）。List/PointsList 供下游使用；纯图像列表可接 Exposure Fusion 的 Images。循环结束后才执行仅依赖收集结果的算子。须用「运行」托管执行。",
                Category = "流程",
                Params =
                {
                    new OperatorParam { Name = "acceptNull", DisplayName = "接受空输入", DefaultValue = "false", Description = "true=In 为空也占一轮；false=跳过空输入" }
                },
                Ports =
                {
                    new PortDef { Name = "In", Direction = PortDirection.Input, DataType = typeof(object), ColorHex = "#607D8B" },
                    new PortDef { Name = "After", Direction = PortDirection.Input, DataType = typeof(object), ColorHex = "#607D8B", IsOptional = true },
                    new PortDef { Name = "Out", Direction = PortDirection.Output, DataType = typeof(object), ColorHex = "#607D8B", IsOptional = true },
                    new PortDef { Name = "List", Direction = PortDirection.Output, DataType = typeof(object), ColorHex = "#FF9800" },
                    new PortDef { Name = "PointsList", Direction = PortDirection.Output, DataType = typeof(object), ColorHex = "#64B5F6", IsOptional = true },
                    new PortDef { Name = "Count", Direction = PortDirection.Output, DataType = typeof(int), ColorHex = "#607D8B" }
                }
            },
            new OperatorDef
            {
                TypeId = "points_sink",
                DisplayName = "轨迹收集",
                Description = "接在循环下游（flow_loop / 粗形状Mask循环）：每轮将 Points 追加合并，输出 GroupBarIds 供「轮廓点简化」。默认 groupIdMode=round 每轮一条焊道号(0,1,2…)。须放在循环体内；简化等算子放在收集之后、由引擎循环结束后执行。",
                Category = "流程",
                Params =
                {
                    new OperatorParam
                    {
                        Name = "acceptEmpty",
                        DisplayName = "接受空点列",
                        DefaultValue = "true",
                        Description = "true=本轮 0 点也占一轮序号；false=跳过空输入",
                        Options = new List<string> { "true", "false" }
                    },
                    new OperatorParam
                    {
                        Name = "groupIdMode",
                        DisplayName = "分组条号",
                        DefaultValue = "round",
                        Description = "round=每轮统一条号(0..N-1)；preserve=沿用输入 GroupBarIds/BarIds；offset=在已有最大条号上偏移",
                        Options = new List<string> { "round", "preserve", "offset" }
                    }
                },
                Ports =
                {
                    new PortDef { Name = "Points", Direction = PortDirection.Input, DataType = typeof(Point2D[]), ColorHex = "#2196F3" },
                    new PortDef { Name = "GroupBarIds", Direction = PortDirection.Input, DataType = typeof(int[]), ColorHex = "#FFEB3B", IsOptional = true },
                    new PortDef { Name = "BarIds", Direction = PortDirection.Input, DataType = typeof(int[]), ColorHex = "#FFC107", IsOptional = true },
                    new PortDef { Name = "After", Direction = PortDirection.Input, DataType = typeof(object), ColorHex = "#607D8B", IsOptional = true },
                    new PortDef { Name = "Out", Direction = PortDirection.Output, DataType = typeof(object), ColorHex = "#607D8B", IsOptional = true },
                    new PortDef { Name = "MergedPoints", Direction = PortDirection.Output, DataType = typeof(Point2D[]), ColorHex = "#2196F3" },
                    new PortDef { Name = "MergedGroupBarIds", Direction = PortDirection.Output, DataType = typeof(int[]), ColorHex = "#FFEB3B" },
                    new PortDef { Name = "BarIds", Direction = PortDirection.Output, DataType = typeof(int[]), ColorHex = "#FFC107", IsOptional = true },
                    new PortDef { Name = "Count", Direction = PortDirection.Output, DataType = typeof(int), ColorHex = "#607D8B" }
                }
            },
            new OperatorDef
            {
                TypeId = "if_gate",
                DisplayName = "条件判断",
                Description = "根据输入值与阈值比较输出 True/False",
                Category = "流程",
                Params =
                {
                    new OperatorParam
                    {
                        Name = "op",
                        DisplayName = "比较运算",
                        DefaultValue = ">",
                        Description = "> | >= | < | <= | == | !=",
                        Options = new List<string> { ">", ">=", "<", "<=", "==", "!=" }
                    },
                    new OperatorParam { Name = "threshold", DisplayName = "阈值", DefaultValue = "0", Description = "比较阈值" },
                    new OperatorParam { Name = "epsilon", DisplayName = "相等容差", DefaultValue = "1e-6", Description = "==/!= 的容差" }
                },
                Ports =
                {
                    new PortDef { Name = "Value", Direction = PortDirection.Input, DataType = typeof(object), ColorHex = "#607D8B" },
                    new PortDef { Name = "True", Direction = PortDirection.Output, DataType = typeof(bool), ColorHex = "#4CAF50" },
                    new PortDef { Name = "False", Direction = PortDirection.Output, DataType = typeof(bool), ColorHex = "#F44336" }
                }
            },
            new OperatorDef
            {
                TypeId = "route_true",
                DisplayName = "真分支放行",
                Description = "条件为 True 时放行输入数据",
                Category = "流程",
                Ports =
                {
                    new PortDef { Name = "Condition", Direction = PortDirection.Input, DataType = typeof(bool), ColorHex = "#4CAF50" },
                    new PortDef { Name = "In", Direction = PortDirection.Input, DataType = typeof(object), ColorHex = "#607D8B" },
                    new PortDef { Name = "Out", Direction = PortDirection.Output, DataType = typeof(object), ColorHex = "#607D8B" }
                }
            },
            new OperatorDef
            {
                TypeId = "route_false",
                DisplayName = "假分支放行",
                Description = "条件为 False 时放行输入数据",
                Category = "流程",
                Ports =
                {
                    new PortDef { Name = "Condition", Direction = PortDirection.Input, DataType = typeof(bool), ColorHex = "#F44336" },
                    new PortDef { Name = "In", Direction = PortDirection.Input, DataType = typeof(object), ColorHex = "#607D8B" },
                    new PortDef { Name = "Out", Direction = PortDirection.Output, DataType = typeof(object), ColorHex = "#607D8B" }
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
                TypeId = "sam_onnx_segment",
                DisplayName = "SAM 图像分割",
                Description = "Segment Anything ONNX：点提示或文本提示（OWLv2 ONNX→多框→SAM）。OWLv2 经 NMS 保留的目标数由 textMaxDetections 决定（见 GroundingJson）。仅 1 目标时 Mask～Mask4 为 SAM 多掩码候选；多目标时 encoder 一次、decoder 次数为 min(检出数, maskMergeMax)。MaskAll 为 3 通道图：黑底上各路掩码用不同颜色、固定 alpha 叠加以模拟透明。Mask～Mask4 仍为前 4 实例单通道掩码。Flow 参数上限见 FlowSamMaskMergeParamUpperBound。需 OWLv2 ONNX + tokenizer.json。",
                Category = "AI模型",
                Params =
                {
                    new OperatorParam { Name = "encoderPath", DisplayName = "Encoder ONNX", DefaultValue = SamOnnxSegmentation.DefaultEncoderRepoRelative, Description = "相对 flow 目录，否则向上查找仓库根 models/onnx；可为绝对路径" },
                    new OperatorParam { Name = "decoderPath", DisplayName = "Decoder ONNX", DefaultValue = SamOnnxSegmentation.DefaultDecoderRepoRelative, Description = "相对 flow 目录，否则向上查找仓库根 models/onnx；可为绝对路径" },
                    new OperatorParam { Name = "textPrompt", DisplayName = "文本选物体", DefaultValue = "", Description = "非空时调用 OWLv2 生成框再 SAM；优先于 Points。模型单次查询约 16 英文词元，脚本会自动截断；中文建议尽量短或 textRawQuery=true" },
                    new OperatorParam { Name = "textThreshold", DisplayName = "文本检测阈值", DefaultValue = "0.25", Description = "OWLv2 post_process_object_detection threshold；无框时可调低" },
                    new OperatorParam { Name = "textRawQuery", DisplayName = "文本不加前缀", DefaultValue = "false", Description = "true 时直接把 textPrompt 送入模型；false 时使用「a photo of …」模板" },
                    new OperatorParam { Name = "owlv2OnnxPath", DisplayName = "OWLv2 ONNX", DefaultValue = Owlv2OnnxTextToBox.DefaultOnnxRepoRelative, Description = "optimum 导出的零样本检测 ONNX；路径规则同 SAM 模型（ResolveModelPath）" },
                    new OperatorParam { Name = "owlv2TokenizerJson", DisplayName = "tokenizer.json", DefaultValue = Owlv2OnnxTextToBox.DefaultTokenizerJsonRelative, Description = "CLIP tokenizer.json（可与 openai/clip-vit-base-patch32 一致），供 Tokenizers.DotNet 编码文本" },
                    new OperatorParam { Name = "textMaxDetections", DisplayName = "文本最大目标数", DefaultValue = "16", Description = "OWLv2 NMS 后保留的实例上限（写入 GroundingJson）；与 maskMergeMax、SAM decoder 次数配合，Flow 解析上限见 FlowSamTextMaxDetectionsUpperBound" },
                    new OperatorParam { Name = "textNmsIou", DisplayName = "文本NMS IoU", DefaultValue = "0.5", Description = "合并重叠框的 NMS IoU（0～1）；略升高可保留更多邻近实例" },
                    new OperatorParam { Name = "clickX", DisplayName = "默认点击 X", DefaultValue = "512", Description = "未连接 Points 且无文本时使用，原图像素坐标" },
                    new OperatorParam { Name = "clickY", DisplayName = "默认点击 Y", DefaultValue = "512", Description = "未连接 Points 且无文本时使用，原图像素坐标" },
                    new OperatorParam { Name = "maskThreshold", DisplayName = "掩码 logit 阈值", DefaultValue = "0", Description = "decoder 输出 logits，大于阈值视为前景（通常 0）" },
                    new OperatorParam { Name = "maskMergeMax", DisplayName = "MaskAll合并路数", DefaultValue = "4", Description = "MaskAll：多路掩码合成为 3 通道彩色图（黑底、各路不同色相、固定透明度叠加）；多实例时 SAM decoder 至多 min(检出数, N) 次。路数上限见 FlowSamMaskMergeParamUpperBound。单实例 SAM 仍最多 4 路 decoder 候选" },
                    new OperatorParam { Name = "useGpu", DisplayName = "尝试 CUDA", DefaultValue = "false", Description = "需要 onnxruntime GPU 与 CUDA；失败则自动用 CPU" }
                },
                Ports =
                {
                    new PortDef { Name = "Image", Direction = PortDirection.Input, DataType = typeof(CalibImage), ColorHex = "#4CAF50" },
                    new PortDef { Name = "Points", Direction = PortDirection.Input, DataType = typeof(Point2D[]), ColorHex = "#2196F3" },
                    new PortDef { Name = "Mask", Direction = PortDirection.Output, DataType = typeof(CalibImage), ColorHex = "#4CAF50" },
                    new PortDef { Name = "Mask2", Direction = PortDirection.Output, DataType = typeof(CalibImage), ColorHex = "#66BB6A" },
                    new PortDef { Name = "Mask3", Direction = PortDirection.Output, DataType = typeof(CalibImage), ColorHex = "#81C784" },
                    new PortDef { Name = "Mask4", Direction = PortDirection.Output, DataType = typeof(CalibImage), ColorHex = "#A5D6A7" },
                    new PortDef { Name = "MaskAll", Direction = PortDirection.Output, DataType = typeof(CalibImage), ColorHex = "#2E7D32" },
                    new PortDef { Name = "Vis", Direction = PortDirection.Output, DataType = typeof(CalibImage), ColorHex = "#4CAF50" },
                    new PortDef { Name = "GroundingJson", Direction = PortDirection.Output, DataType = typeof(string), ColorHex = "#607D8B" }
                }
            },
            new OperatorDef
            {
                TypeId = "chessboard_find_corners",
                DisplayName = "棋盘格角点",
                Description = "OpenCV 棋盘格内侧角点检测与可视化",
                Category = "标定",
                Params =
                {
                    new OperatorParam { Name = "cols", DisplayName = "内侧列角点数", DefaultValue = "9", Description = "棋盘格内侧角点列数（宽方向）" },
                    new OperatorParam { Name = "rows", DisplayName = "内侧行角点数", DefaultValue = "6", Description = "棋盘格内侧角点行数（高方向）" },
                    new OperatorParam { Name = "refine", DisplayName = "亚像素细化", DefaultValue = "true", Description = "cornerSubPix 细化" },
                    new OperatorParam { Name = "fastCheck", DisplayName = "快速检测", DefaultValue = "true", Description = "CALIB_CB_FAST_CHECK，失败时可改为 false" }
                },
                Ports =
                {
                    new PortDef { Name = "Image", Direction = PortDirection.Input, DataType = typeof(CalibImage), ColorHex = "#4CAF50" },
                    new PortDef { Name = "Points", Direction = PortDirection.Output, DataType = typeof(Point2D[]), ColorHex = "#2196F3" },
                    new PortDef { Name = "Vis", Direction = PortDirection.Output, DataType = typeof(CalibImage), ColorHex = "#4CAF50" },
                    new PortDef { Name = "Found", Direction = PortDirection.Output, DataType = typeof(bool), ColorHex = "#607D8B" }
                }
            },
            new OperatorDef
            {
                TypeId = "chessboard_calibrate_intrinsics",
                DisplayName = "棋盘格内参标定",
                Description = "由多张棋盘格图像通过 OpenCV calibrateCamera 最小化重投影误差，同时优化求解：相机内参 fx/fy/cx/cy、畸变系数，以及每张成功视图的外参 rvec/tvec（棋盘坐标系→相机坐标系）。输出均为标定计算结果。至少需 3 张成功检出棋盘的视图。完整结果见 CalibrationJson。帮助：flows/chessboard/README.md",
                Category = "标定",
                Params =
                {
                    new OperatorParam { Name = "imageDirectory", DisplayName = "图像目录", DefaultValue = "", Description = "批量标定：扫描目录内图像（默认 Image_ 前缀 + .bmp）；与 imagePaths 可同时使用（合并去重）" },
                    new OperatorParam { Name = "extensions", DisplayName = "目录扩展名", DefaultValue = ".bmp", Description = "目录模式下匹配扩展名，分号分隔，如 .bmp 或 .bmp;.png" },
                    new OperatorParam { Name = "namePrefix", DisplayName = "文件名前缀", DefaultValue = "Image_", Description = "目录模式下仅保留文件名此前缀的图像；留空则不过滤" },
                    new OperatorParam { Name = "imagePaths", DisplayName = "图像路径列表", DefaultValue = "", Description = "可选：分号分隔单张路径；相对路径相对流程文件目录" },
                    new OperatorParam { Name = "cols", DisplayName = "内侧列角点数", DefaultValue = "9", Description = "与检测算子一致，用于构造已知三维棋盘角点" },
                    new OperatorParam { Name = "rows", DisplayName = "内侧行角点数", DefaultValue = "6", Description = "与检测算子一致，用于构造已知三维棋盘角点" },
                    new OperatorParam { Name = "squareSizeMm", DisplayName = "方格边长(mm)", DefaultValue = "25", Description = "棋盘方格物理边长(mm)，与世界坐标尺度一致，参与内参求解" }
                },
                Ports =
                {
                    new PortDef { Name = "Intrinsics", Direction = PortDirection.Output, DataType = typeof(CameraIntrinsics), ColorHex = "#E91E63" },
                    new PortDef { Name = "IntrinsicsJson", Direction = PortDirection.Output, DataType = typeof(string), ColorHex = "#607D8B" },
                    new PortDef { Name = "CalibrationJson", Direction = PortDirection.Output, DataType = typeof(string), ColorHex = "#607D8B" }
                }
            },
            new OperatorDef
            {
                TypeId = "chessboard_pixels_to_world",
                DisplayName = "棋盘像素→世界(mm)",
                Description = "将像素轨迹投影到标定棋盘平面 Z=0：先 undistort，再按选定视图外参求射线与平面交点，输出 XY 与 squareSizeMm 同单位。CalibrationJson 来自「棋盘格内参标定」；viewIndex 对应 extrinsicsPerView 顺序（与成功标定图像顺序一致）。轨迹须与该视图成像几何一致（如同机位、或静止场景下同 pose）。0 个点时跳过（空 World）。",
                Category = "标定",
                Params =
                {
                    new OperatorParam { Name = "viewIndex", DisplayName = "外参视图序号", DefaultValue = "0", Description = "从 0 开始，对应 CalibrationJson.extrinsicsPerView[i]" }
                },
                Ports =
                {
                    new PortDef { Name = "Points", Direction = PortDirection.Input, DataType = typeof(Point2D[]), ColorHex = "#2196F3" },
                    new PortDef { Name = "CalibrationJson", Direction = PortDirection.Input, DataType = typeof(string), ColorHex = "#607D8B" },
                    new PortDef { Name = "World", Direction = PortDirection.Output, DataType = typeof(Point2D[]), ColorHex = "#2196F3" }
                }
            },
            new OperatorDef
            {
                TypeId = "calibration_correct_image",
                DisplayName = "标定图像矫正",
                Description = "对输入图像按参数做内参去畸变与/或棋盘透视展开（原 load_image / camera_snap 内置选项已拆出）。须连接 CalibrationJson 或填写 calibrationJsonFile。帮助：flows/chessboard/README.md",
                Category = "标定",
                Params = FlowCameraCorrectionOperatorParams.CoreOnly(),
                Ports =
                {
                    new PortDef { Name = "Image", Direction = PortDirection.Input, DataType = typeof(CalibImage), ColorHex = "#4CAF50" },
                    new PortDef { Name = "CalibrationJson", Direction = PortDirection.Input, DataType = typeof(string), ColorHex = "#607D8B", IsOptional = true },
                    new PortDef { Name = "Out", Direction = PortDirection.Output, DataType = typeof(CalibImage), ColorHex = "#4CAF50" }
                }
            },
            new OperatorDef
            {
                TypeId = "intrinsics_undistort_image",
                DisplayName = "内参畸变矫正",
                Description = "使用棋盘格标定得到的针孔内参 fx/fy/cx/cy 与畸变系数 k1..k3/p1/p2，对输入图像做 OpenCV cv::undistort 去畸变。连接 Intrinsics 或 CalibrationJson（来自「棋盘格内参标定」）。",
                Category = "标定",
                Params =
                {
                    new OperatorParam { Name = "alpha", DisplayName = "裁剪系数", DefaultValue = "-1", Description = "-1=保持原分辨率与 K；0..1= getOptimalNewCameraMatrix 裁剪黑边（0 裁最多，1 保留全部像素）" },
                    new OperatorParam { Name = "calibrationJsonFile", DisplayName = "标定 JSON 文件", DefaultValue = "", Description = "可选；未接 CalibrationJson 端口时从文件读取（完整包，含 extrinsicsPerView）" }
                },
                Ports =
                {
                    new PortDef { Name = "Image", Direction = PortDirection.Input, DataType = typeof(CalibImage), ColorHex = "#4CAF50" },
                    new PortDef { Name = "Intrinsics", Direction = PortDirection.Input, DataType = typeof(CameraIntrinsics), ColorHex = "#E91E63", IsOptional = true },
                    new PortDef { Name = "CalibrationJson", Direction = PortDirection.Input, DataType = typeof(string), ColorHex = "#607D8B", IsOptional = true },
                    new PortDef { Name = "Out", Direction = PortDirection.Output, DataType = typeof(CalibImage), ColorHex = "#4CAF50" }
                }
            },
            new OperatorDef
            {
                TypeId = "chessboard_perspective_warp_image",
                DisplayName = "棋盘透视展开",
                Description = "整图透视展开到标定棋盘平面（鸟瞰）：使用 CalibrationJson 内参 + extrinsicsPerView[viewIndex]。输出尺度 metric 时各角度尺寸一致。常与「内参畸变矫正」串联。帮助：flows/chessboard/README.md",
                Category = "标定",
                Params =
                {
                    new OperatorParam { Name = "calibrationJsonFile", DisplayName = "标定 JSON 文件", DefaultValue = "", Description = "可选；未接端口时读取完整标定 JSON（须含 extrinsicsPerView，如 chessboard_calibration_from_dir.json）" },
                    new OperatorParam { Name = "viewIndex", DisplayName = "外参视图序号", DefaultValue = "0", Description = "extrinsicsPerView 下标，从 0 开始；须与当前图像位姿接近" },
                    new OperatorParam { Name = "cols", DisplayName = "内侧列角点数", DefaultValue = "9", Description = "与棋盘格标定一致" },
                    new OperatorParam { Name = "rows", DisplayName = "内侧行角点数", DefaultValue = "6", Description = "与棋盘格标定一致" },
                    new OperatorParam { Name = "squareSizeMm", DisplayName = "方格边长(mm)", DefaultValue = "25", Description = "与标定 squareSizeMm 一致" },
                    new OperatorParam { Name = "pxPerMm", DisplayName = "mm/像素", DefaultValue = "1", Description = "输出鸟瞰图尺度，1=1像素1mm" },
                    new OperatorParam
                    {
                        Name = "perspectiveOutputFrame",
                        DisplayName = "透视输出范围",
                        DefaultValue = "board",
                        Description = "board=仅标定板；local=原图仅板内；plane=整图平面透视(共面)",
                        Options = new List<string> { "board", "local", "plane" }
                    },
                    new OperatorParam
                    {
                        Name = "perspectiveOutputScale",
                        DisplayName = "输出尺度",
                        DefaultValue = "metric",
                        Description = "metric=固定 (cols-1)×squareSizeMm×pxPerMm，各 viewIndex/角度输出尺寸一致；board_pixels=按图中棋盘边长（随距离/倾角变化）",
                        Options = new List<string> { "metric", "board_pixels" }
                    },
                    new OperatorParam
                    {
                        Name = "assumeUndistorted",
                        DisplayName = "输入已去畸变",
                        DefaultValue = "false",
                        Description = "上游已 undistort 时选 true，避免角点错位",
                        Options = new List<string> { "true", "false" }
                    }
                },
                Ports =
                {
                    new PortDef { Name = "Image", Direction = PortDirection.Input, DataType = typeof(CalibImage), ColorHex = "#4CAF50" },
                    new PortDef { Name = "CalibrationJson", Direction = PortDirection.Input, DataType = typeof(string), ColorHex = "#607D8B" },
                    new PortDef { Name = "Out", Direction = PortDirection.Output, DataType = typeof(CalibImage), ColorHex = "#4CAF50" }
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
                TypeId = "halcon_rgb1_to_gray",
                DisplayName = "HALCON 转灰度",
                Description = "Rgb1ToGray：三通道则转灰度，单通道则直通（需本机 HALCON 与许可证）",
                Category = "HALCON",
                Ports =
                {
                    new PortDef { Name = "In", Direction = PortDirection.Input, DataType = typeof(CalibImage), ColorHex = "#FF9800" },
                    new PortDef { Name = "Out", Direction = PortDirection.Output, DataType = typeof(CalibImage), ColorHex = "#FF9800" }
                }
            },
            new OperatorDef
            {
                TypeId = "halcon_threshold_bin",
                DisplayName = "HALCON 阈值二值",
                Description = "Threshold + RegionToBin → 单通道二值图（与流程里「查找轮廓」等衔接）",
                Category = "HALCON",
                Params =
                {
                    new OperatorParam { Name = "minGray", DisplayName = "MinGray", DefaultValue = "128", Description = "灰度下阈" },
                    new OperatorParam { Name = "maxGray", DisplayName = "MaxGray", DefaultValue = "255", Description = "灰度上阈" }
                },
                Ports =
                {
                    new PortDef { Name = "In", Direction = PortDirection.Input, DataType = typeof(CalibImage), ColorHex = "#FF9800" },
                    new PortDef { Name = "Out", Direction = PortDirection.Output, DataType = typeof(CalibImage), ColorHex = "#FF9800" }
                }
            },
            new OperatorDef
            {
                TypeId = "halcon_emphasize",
                DisplayName = "HALCON Emphasize",
                Description = "灰度强调（Emphasize），利于边缘/纹理对比",
                Category = "HALCON",
                Params =
                {
                    new OperatorParam { Name = "maskWidth", DisplayName = "MaskWidth", DefaultValue = "7", Description = "滤波宽度（奇数更佳）" },
                    new OperatorParam { Name = "maskHeight", DisplayName = "MaskHeight", DefaultValue = "7", Description = "滤波高度（奇数更佳）" },
                    new OperatorParam { Name = "factor", DisplayName = "Factor", DefaultValue = "1.0", Description = "强调系数" }
                },
                Ports =
                {
                    new PortDef { Name = "In", Direction = PortDirection.Input, DataType = typeof(CalibImage), ColorHex = "#FF9800" },
                    new PortDef { Name = "Out", Direction = PortDirection.Output, DataType = typeof(CalibImage), ColorHex = "#FF9800" }
                }
            },
            new OperatorDef
            {
                TypeId = "halcon_gray_opening_rect",
                DisplayName = "HALCON 灰度开运算",
                Description = "GrayOpeningRect：矩形结构元灰度开运算，抑制亮噪点",
                Category = "HALCON",
                Params =
                {
                    new OperatorParam { Name = "maskHeight", DisplayName = "MaskHeight", DefaultValue = "3", Description = "结构元高度" },
                    new OperatorParam { Name = "maskWidth", DisplayName = "MaskWidth", DefaultValue = "3", Description = "结构元宽度" }
                },
                Ports =
                {
                    new PortDef { Name = "In", Direction = PortDirection.Input, DataType = typeof(CalibImage), ColorHex = "#FF9800" },
                    new PortDef { Name = "Out", Direction = PortDirection.Output, DataType = typeof(CalibImage), ColorHex = "#FF9800" }
                }
            },
            new OperatorDef
            {
                TypeId = "halcon_scale_image",
                DisplayName = "HALCON ScaleImage",
                Description = "线性缩放 Gray' = Mult×Gray + Add，调节对比度与亮度",
                Category = "HALCON",
                Params =
                {
                    new OperatorParam { Name = "mult", DisplayName = "Mult", DefaultValue = "1.0", Description = "乘因子" },
                    new OperatorParam { Name = "add", DisplayName = "Add", DefaultValue = "0", Description = "加偏置" }
                },
                Ports =
                {
                    new PortDef { Name = "In", Direction = PortDirection.Input, DataType = typeof(CalibImage), ColorHex = "#FF9800" },
                    new PortDef { Name = "Out", Direction = PortDirection.Output, DataType = typeof(CalibImage), ColorHex = "#FF9800" }
                }
            },
            new OperatorDef
            {
                TypeId = "halcon_scale_image_max",
                DisplayName = "HALCON ScaleImageMax",
                Description = "按图像最大值拉伸动态范围（整幅归一化）",
                Category = "HALCON",
                Ports =
                {
                    new PortDef { Name = "In", Direction = PortDirection.Input, DataType = typeof(CalibImage), ColorHex = "#FF9800" },
                    new PortDef { Name = "Out", Direction = PortDirection.Output, DataType = typeof(CalibImage), ColorHex = "#FF9800" }
                }
            },
            new OperatorDef
            {
                TypeId = "halcon_illuminate",
                DisplayName = "HALCON Illuminate",
                Description = "局部亮度校正（大视场光照不均）",
                Category = "HALCON",
                Params =
                {
                    new OperatorParam { Name = "maskWidth", DisplayName = "MaskWidth", DefaultValue = "41", Description = "掩模宽（奇数）" },
                    new OperatorParam { Name = "maskHeight", DisplayName = "MaskHeight", DefaultValue = "41", Description = "掩模高（奇数）" },
                    new OperatorParam { Name = "factor", DisplayName = "Factor", DefaultValue = "0.7", Description = "校正强度" }
                },
                Ports =
                {
                    new PortDef { Name = "In", Direction = PortDirection.Input, DataType = typeof(CalibImage), ColorHex = "#FF9800" },
                    new PortDef { Name = "Out", Direction = PortDirection.Output, DataType = typeof(CalibImage), ColorHex = "#FF9800" }
                }
            },
            new OperatorDef
            {
                TypeId = "halcon_mean_image",
                DisplayName = "HALCON MeanImage",
                Description = "均值平滑；输出常作为 DynThreshold 的参考图",
                Category = "HALCON",
                Params =
                {
                    new OperatorParam { Name = "maskWidth", DisplayName = "MaskWidth", DefaultValue = "15", Description = "滤波宽" },
                    new OperatorParam { Name = "maskHeight", DisplayName = "MaskHeight", DefaultValue = "15", Description = "滤波高" }
                },
                Ports =
                {
                    new PortDef { Name = "In", Direction = PortDirection.Input, DataType = typeof(CalibImage), ColorHex = "#FF9800" },
                    new PortDef { Name = "Out", Direction = PortDirection.Output, DataType = typeof(CalibImage), ColorHex = "#FF9800" }
                }
            },
            new OperatorDef
            {
                TypeId = "halcon_gauss_filter",
                DisplayName = "HALCON GaussFilter",
                Description = "高斯平滑（Size 为奇数）",
                Category = "HALCON",
                Params =
                {
                    new OperatorParam { Name = "size", DisplayName = "Size", DefaultValue = "5", Description = "滤波尺寸" }
                },
                Ports =
                {
                    new PortDef { Name = "In", Direction = PortDirection.Input, DataType = typeof(CalibImage), ColorHex = "#FF9800" },
                    new PortDef { Name = "Out", Direction = PortDirection.Output, DataType = typeof(CalibImage), ColorHex = "#FF9800" }
                }
            },
            new OperatorDef
            {
                TypeId = "halcon_gray_closing_rect",
                DisplayName = "HALCON 灰度闭运算",
                Description = "GrayClosingRect：连接暗条、填小缝",
                Category = "HALCON",
                Params =
                {
                    new OperatorParam { Name = "maskHeight", DisplayName = "MaskHeight", DefaultValue = "5", Description = "结构元高度" },
                    new OperatorParam { Name = "maskWidth", DisplayName = "MaskWidth", DefaultValue = "5", Description = "结构元宽度" }
                },
                Ports =
                {
                    new PortDef { Name = "In", Direction = PortDirection.Input, DataType = typeof(CalibImage), ColorHex = "#FF9800" },
                    new PortDef { Name = "Out", Direction = PortDirection.Output, DataType = typeof(CalibImage), ColorHex = "#FF9800" }
                }
            },
            new OperatorDef
            {
                TypeId = "halcon_binary_threshold",
                DisplayName = "HALCON BinaryThreshold",
                Description = "自动全局阈值（如 max_separability）→ 二值图",
                Category = "HALCON",
                Params =
                {
                    new OperatorParam { Name = "method", DisplayName = "Method", DefaultValue = "max_separability", Description = "Halcon 方法名，如 max_separability、smooth_histo" },
                    new OperatorParam { Name = "lightDark", DisplayName = "LightDark", DefaultValue = "dark", Description = "dark / light" }
                },
                Ports =
                {
                    new PortDef { Name = "In", Direction = PortDirection.Input, DataType = typeof(CalibImage), ColorHex = "#FF9800" },
                    new PortDef { Name = "Out", Direction = PortDirection.Output, DataType = typeof(CalibImage), ColorHex = "#FF9800" }
                }
            },
            new OperatorDef
            {
                TypeId = "halcon_dyn_threshold",
                DisplayName = "HALCON DynThreshold",
                Description = "局部自适应：原图 In 与参考图 Ref（建议接 MeanImage），offset 为灰度差阈值",
                Category = "HALCON",
                Params =
                {
                    new OperatorParam { Name = "offset", DisplayName = "Offset", DefaultValue = "15", Description = "与参考图的差分阈值" },
                    new OperatorParam { Name = "lightDark", DisplayName = "LightDark", DefaultValue = "dark", Description = "dark / light" }
                },
                Ports =
                {
                    new PortDef { Name = "In", Direction = PortDirection.Input, DataType = typeof(CalibImage), ColorHex = "#FF9800" },
                    new PortDef { Name = "Ref", Direction = PortDirection.Input, DataType = typeof(CalibImage), ColorHex = "#FFB74D" },
                    new PortDef { Name = "Out", Direction = PortDirection.Output, DataType = typeof(CalibImage), ColorHex = "#FF9800" }
                }
            },
            new OperatorDef
            {
                TypeId = "halcon_var_threshold",
                DisplayName = "HALCON VarThreshold",
                Description = "基于局部均值与标准差的自适应阈值（单幅输入）",
                Category = "HALCON",
                Params =
                {
                    new OperatorParam { Name = "maskWidth", DisplayName = "MaskWidth", DefaultValue = "15", Description = "分析窗宽" },
                    new OperatorParam { Name = "maskHeight", DisplayName = "MaskHeight", DefaultValue = "15", Description = "分析窗高" },
                    new OperatorParam { Name = "stdDevScale", DisplayName = "StdDevScale", DefaultValue = "0.2", Description = "标准差权重" },
                    new OperatorParam { Name = "absThreshold", DisplayName = "AbsThreshold", DefaultValue = "40", Description = "绝对阈值项" },
                    new OperatorParam { Name = "lightDark", DisplayName = "LightDark", DefaultValue = "dark", Description = "dark / light" }
                },
                Ports =
                {
                    new PortDef { Name = "In", Direction = PortDirection.Input, DataType = typeof(CalibImage), ColorHex = "#FF9800" },
                    new PortDef { Name = "Out", Direction = PortDirection.Output, DataType = typeof(CalibImage), ColorHex = "#FF9800" }
                }
            },
            new OperatorDef
            {
                TypeId = "halcon_median_image",
                DisplayName = "HALCON MedianImage",
                Description = "中值滤波，抑制椒盐噪声",
                Category = "HALCON",
                Params =
                {
                    new OperatorParam { Name = "maskType", DisplayName = "MaskType", DefaultValue = "circle", Description = "circle / square …" },
                    new OperatorParam { Name = "radius", DisplayName = "Radius", DefaultValue = "3", Description = "半径" },
                    new OperatorParam { Name = "margin", DisplayName = "Margin", DefaultValue = "mirrored", Description = "边界处理" }
                },
                Ports =
                {
                    new PortDef { Name = "In", Direction = PortDirection.Input, DataType = typeof(CalibImage), ColorHex = "#FF9800" },
                    new PortDef { Name = "Out", Direction = PortDirection.Output, DataType = typeof(CalibImage), ColorHex = "#FF9800" }
                }
            },
            new OperatorDef
            {
                TypeId = "halcon_invert_image",
                DisplayName = "HALCON InvertImage",
                Description = "灰度取反（InvertImage）",
                Category = "HALCON",
                Ports =
                {
                    new PortDef { Name = "In", Direction = PortDirection.Input, DataType = typeof(CalibImage), ColorHex = "#FF9800" },
                    new PortDef { Name = "Out", Direction = PortDirection.Output, DataType = typeof(CalibImage), ColorHex = "#FF9800" }
                }
            },
            new OperatorDef
            {
                TypeId = "halcon_abs_diff",
                DisplayName = "HALCON AbsDiffImage",
                Description = "逐像素绝对差 |In−In2|×mult（须同尺寸）",
                Category = "HALCON",
                Params =
                {
                    new OperatorParam { Name = "mult", DisplayName = "Mult", DefaultValue = "1.0", Description = "输出缩放" }
                },
                Ports =
                {
                    new PortDef { Name = "In", Direction = PortDirection.Input, DataType = typeof(CalibImage), ColorHex = "#FF9800" },
                    new PortDef { Name = "In2", Direction = PortDirection.Input, DataType = typeof(CalibImage), ColorHex = "#FFB74D" },
                    new PortDef { Name = "Out", Direction = PortDirection.Output, DataType = typeof(CalibImage), ColorHex = "#FF9800" }
                }
            },
            new OperatorDef
            {
                TypeId = "halcon_sub_image",
                DisplayName = "HALCON SubImage",
                Description = "Out = (In−In2)×mult + add（须同尺寸）",
                Category = "HALCON",
                Params =
                {
                    new OperatorParam { Name = "mult", DisplayName = "Mult", DefaultValue = "1.0", Description = "差分缩放" },
                    new OperatorParam { Name = "add", DisplayName = "Add", DefaultValue = "0", Description = "加偏置" }
                },
                Ports =
                {
                    new PortDef { Name = "In", Direction = PortDirection.Input, DataType = typeof(CalibImage), ColorHex = "#FF9800" },
                    new PortDef { Name = "In2", Direction = PortDirection.Input, DataType = typeof(CalibImage), ColorHex = "#FFB74D" },
                    new PortDef { Name = "Out", Direction = PortDirection.Output, DataType = typeof(CalibImage), ColorHex = "#FF9800" }
                }
            },
            new OperatorDef
            {
                TypeId = "halcon_add_image",
                DisplayName = "HALCON AddImage",
                Description = "Out = mult×In + In2 + add（须同尺寸）",
                Category = "HALCON",
                Params =
                {
                    new OperatorParam { Name = "mult", DisplayName = "Mult", DefaultValue = "1.0", Description = "In 的系数" },
                    new OperatorParam { Name = "add", DisplayName = "Add", DefaultValue = "0", Description = "加偏置" }
                },
                Ports =
                {
                    new PortDef { Name = "In", Direction = PortDirection.Input, DataType = typeof(CalibImage), ColorHex = "#FF9800" },
                    new PortDef { Name = "In2", Direction = PortDirection.Input, DataType = typeof(CalibImage), ColorHex = "#FFB74D" },
                    new PortDef { Name = "Out", Direction = PortDirection.Output, DataType = typeof(CalibImage), ColorHex = "#FF9800" }
                }
            },
            new OperatorDef
            {
                TypeId = "halcon_mult_image",
                DisplayName = "HALCON MultImage",
                Description = "Out = In×In2×mult + add（须同尺寸）",
                Category = "HALCON",
                Params =
                {
                    new OperatorParam { Name = "mult", DisplayName = "Mult", DefaultValue = "0.007843", Description = "乘因子（约 1/127 防溢出）" },
                    new OperatorParam { Name = "add", DisplayName = "Add", DefaultValue = "0", Description = "加偏置" }
                },
                Ports =
                {
                    new PortDef { Name = "In", Direction = PortDirection.Input, DataType = typeof(CalibImage), ColorHex = "#FF9800" },
                    new PortDef { Name = "In2", Direction = PortDirection.Input, DataType = typeof(CalibImage), ColorHex = "#FFB74D" },
                    new PortDef { Name = "Out", Direction = PortDirection.Output, DataType = typeof(CalibImage), ColorHex = "#FF9800" }
                }
            },
            new OperatorDef
            {
                TypeId = "halcon_min_image",
                DisplayName = "HALCON MinImage",
                Description = "逐像素取 min(In, In2)（须同尺寸）",
                Category = "HALCON",
                Ports =
                {
                    new PortDef { Name = "In", Direction = PortDirection.Input, DataType = typeof(CalibImage), ColorHex = "#FF9800" },
                    new PortDef { Name = "In2", Direction = PortDirection.Input, DataType = typeof(CalibImage), ColorHex = "#FFB74D" },
                    new PortDef { Name = "Out", Direction = PortDirection.Output, DataType = typeof(CalibImage), ColorHex = "#FF9800" }
                }
            },
            new OperatorDef
            {
                TypeId = "halcon_max_image",
                DisplayName = "HALCON MaxImage",
                Description = "逐像素取 max(In, In2)（须同尺寸）",
                Category = "HALCON",
                Ports =
                {
                    new PortDef { Name = "In", Direction = PortDirection.Input, DataType = typeof(CalibImage), ColorHex = "#FF9800" },
                    new PortDef { Name = "In2", Direction = PortDirection.Input, DataType = typeof(CalibImage), ColorHex = "#FFB74D" },
                    new PortDef { Name = "Out", Direction = PortDirection.Output, DataType = typeof(CalibImage), ColorHex = "#FF9800" }
                }
            },
            new OperatorDef
            {
                TypeId = "halcon_sobel_amp",
                DisplayName = "HALCON SobelAmp",
                Description = "Sobel 边缘幅值（如 sum_abs）",
                Category = "HALCON",
                Params =
                {
                    new OperatorParam { Name = "filterType", DisplayName = "FilterType", DefaultValue = "sum_abs", Description = "如 sum_abs、sum_sqrt、thin_max_abs 等" },
                    new OperatorParam { Name = "size", DisplayName = "Size", DefaultValue = "3", Description = "滤波尺寸（奇数）" }
                },
                Ports =
                {
                    new PortDef { Name = "In", Direction = PortDirection.Input, DataType = typeof(CalibImage), ColorHex = "#FF9800" },
                    new PortDef { Name = "Out", Direction = PortDirection.Output, DataType = typeof(CalibImage), ColorHex = "#FF9800" }
                }
            },
            new OperatorDef
            {
                TypeId = "halcon_smooth_image",
                DisplayName = "HALCON SmoothImage",
                Description = "平滑（如 gauss），Alpha 控制平滑强度",
                Category = "HALCON",
                Params =
                {
                    new OperatorParam { Name = "filter", DisplayName = "Filter", DefaultValue = "gauss", Description = "如 gauss" },
                    new OperatorParam { Name = "alpha", DisplayName = "Alpha", DefaultValue = "3.0", Description = "平滑参数" }
                },
                Ports =
                {
                    new PortDef { Name = "In", Direction = PortDirection.Input, DataType = typeof(CalibImage), ColorHex = "#FF9800" },
                    new PortDef { Name = "Out", Direction = PortDirection.Output, DataType = typeof(CalibImage), ColorHex = "#FF9800" }
                }
            },
            new OperatorDef
            {
                TypeId = "halcon_gray_erosion_rect",
                DisplayName = "HALCON GrayErosionRect",
                Description = "灰度矩形腐蚀（暗细节扩张）",
                Category = "HALCON",
                Params =
                {
                    new OperatorParam { Name = "maskHeight", DisplayName = "MaskHeight", DefaultValue = "3", Description = "结构元高" },
                    new OperatorParam { Name = "maskWidth", DisplayName = "MaskWidth", DefaultValue = "3", Description = "结构元宽" }
                },
                Ports =
                {
                    new PortDef { Name = "In", Direction = PortDirection.Input, DataType = typeof(CalibImage), ColorHex = "#FF9800" },
                    new PortDef { Name = "Out", Direction = PortDirection.Output, DataType = typeof(CalibImage), ColorHex = "#FF9800" }
                }
            },
            new OperatorDef
            {
                TypeId = "halcon_gray_dilation_rect",
                DisplayName = "HALCON GrayDilationRect",
                Description = "灰度矩形膨胀（亮细节扩张）",
                Category = "HALCON",
                Params =
                {
                    new OperatorParam { Name = "maskHeight", DisplayName = "MaskHeight", DefaultValue = "3", Description = "结构元高" },
                    new OperatorParam { Name = "maskWidth", DisplayName = "MaskWidth", DefaultValue = "3", Description = "结构元宽" }
                },
                Ports =
                {
                    new PortDef { Name = "In", Direction = PortDirection.Input, DataType = typeof(CalibImage), ColorHex = "#FF9800" },
                    new PortDef { Name = "Out", Direction = PortDirection.Output, DataType = typeof(CalibImage), ColorHex = "#FF9800" }
                }
            },
            new OperatorDef
            {
                TypeId = "halcon_auto_threshold",
                DisplayName = "HALCON AutoThreshold",
                Description = "直方图自动多类分割后取面积最大类的二值 Mask（Sigma 为直方图平滑）",
                Category = "HALCON",
                Params =
                {
                    new OperatorParam { Name = "sigma", DisplayName = "Sigma", DefaultValue = "2.0", Description = "直方图平滑（≥0）" }
                },
                Ports =
                {
                    new PortDef { Name = "In", Direction = PortDirection.Input, DataType = typeof(CalibImage), ColorHex = "#FF9800" },
                    new PortDef { Name = "Out", Direction = PortDirection.Output, DataType = typeof(CalibImage), ColorHex = "#FF9800" }
                }
            },
            new OperatorDef
            {
                TypeId = "halcon_binary_morph_rect",
                DisplayName = "HALCON 矩形形态学",
                Description = "二值 Region：OpeningRectangle1 / ClosingRectangle1 / Erosion / Dilation",
                Category = "HALCON",
                Params =
                {
                    new OperatorParam { Name = "op", DisplayName = "运算", DefaultValue = "open", Description = "open / close / erode / dilate" },
                    new OperatorParam { Name = "width", DisplayName = "宽", DefaultValue = "3", Description = "矩形结构元宽" },
                    new OperatorParam { Name = "height", DisplayName = "高", DefaultValue = "3", Description = "矩形结构元高" },
                    new OperatorParam { Name = "iterations", DisplayName = "迭代次数", DefaultValue = "1", Description = "重复次数" }
                },
                Ports =
                {
                    new PortDef { Name = "In", Direction = PortDirection.Input, DataType = typeof(CalibImage), ColorHex = "#FF9800" },
                    new PortDef { Name = "Out", Direction = PortDirection.Output, DataType = typeof(CalibImage), ColorHex = "#FF9800" }
                }
            },
            new OperatorDef
            {
                TypeId = "halcon_binary_to_xld",
                DisplayName = "HALCON 二值→XLD",
                Description = "Threshold → Connection → GenContourRegionXld；每个连通域一条闭合轮廓（HalconXldContourBundle.Contours 条数≈连通域个数）。与 find_contours(RETR_EXTERNAL) 类似：二值破碎则条数增多，可上游用 Closing/填洞或下游 maxBars+周长排序控制条数。",
                Category = "HALCON",
                Params =
                {
                    new OperatorParam { Name = "minGray", DisplayName = "MinGray", DefaultValue = "1", Description = "进入区域的灰度下阈" },
                    new OperatorParam { Name = "maxGray", DisplayName = "MaxGray", DefaultValue = "255", Description = "进入区域的灰度上阈" },
                    new OperatorParam { Name = "genContourMode", DisplayName = "GenContourMode", DefaultValue = "border", Description = "GenContourRegionXld 的 Mode，如 border / center" },
                    new OperatorParam { Name = "minContourPoints", DisplayName = "Min点数", DefaultValue = "3", Description = "丢弃点数少于此值的轮廓" }
                },
                Ports =
                {
                    new PortDef { Name = "In", Direction = PortDirection.Input, DataType = typeof(CalibImage), ColorHex = "#FF9800" },
                    new PortDef { Name = "Xld", Direction = PortDirection.Output, DataType = typeof(HalconXldContourBundle), ColorHex = "#E65100" }
                }
            },
            new OperatorDef
            {
                TypeId = "halcon_segment_xld",
                DisplayName = "HALCON XLD 分格",
                Description = "GenContourPolygonXld + SegmentContoursXld：将每条轮廓拆成多段直线/圆弧，输出 Xld 中 Contours 条数会显著增加（例如 16 条整周界 → 数十段）。若下游按「每条轮廓一个 BarId」焊道/采样，请勿在本算子后再采样；应对「二值→Xld」的原始包直接接 halcon_xld_sample_points。仅在做几何分段、折线化分析时使用。",
                Category = "HALCON",
                Params =
                {
                    new OperatorParam { Name = "mode", DisplayName = "Mode", DefaultValue = "lines_circles", Description = "SegmentContoursXld 的 Mode" },
                    new OperatorParam { Name = "smoothCont", DisplayName = "SmoothCont", DefaultValue = "5", Description = "平滑控制" },
                    new OperatorParam { Name = "maxLineDist1", DisplayName = "MaxLineDist1", DefaultValue = "4.0", Description = "分段距离参数 1" },
                    new OperatorParam { Name = "maxLineDist2", DisplayName = "MaxLineDist2", DefaultValue = "2.0", Description = "分段距离参数 2" }
                },
                Ports =
                {
                    new PortDef { Name = "Xld", Direction = PortDirection.Input, DataType = typeof(HalconXldContourBundle), ColorHex = "#E65100" },
                    new PortDef { Name = "XldOut", Direction = PortDirection.Output, DataType = typeof(HalconXldContourBundle), ColorHex = "#FF6E40" }
                }
            },
            new OperatorDef
            {
                TypeId = "halcon_largest_blob_mask",
                DisplayName = "HALCON 最大连通域Mask",
                Description = "Gauss → Threshold → Connection → 取面积最大的 Region → Mask 二值图（对应 binarize+find_contours+create_mask 思路）",
                Category = "HALCON",
                Params =
                {
                    new OperatorParam { Name = "gaussSize", DisplayName = "Gauss尺寸", DefaultValue = "9", Description = "≥3 奇数；预先平滑，模拟 binarize 中去噪" },
                    new OperatorParam { Name = "minGray", DisplayName = "MinGray", DefaultValue = "40", Description = "阈值分割灰度下界（需在平滑图上分出工件区域）" },
                    new OperatorParam { Name = "maxGray", DisplayName = "MaxGray", DefaultValue = "255", Description = "阈值分割灰度上界" }
                },
                Ports =
                {
                    new PortDef { Name = "In", Direction = PortDirection.Input, DataType = typeof(CalibImage), ColorHex = "#FF9800" },
                    new PortDef { Name = "Out", Direction = PortDirection.Output, DataType = typeof(CalibImage), ColorHex = "#FF9800" }
                }
            },
            new OperatorDef
            {
                TypeId = "halcon_largest_contour_mask",
                DisplayName = "HALCON 最大轮廓Mask",
                Description = "Gauss → Threshold → Connection → 按 rankBy 选最大域 → FillUp（填满内部空洞）→ 实心二值 Mask；默认按面积最大",
                Category = "HALCON",
                Params =
                {
                    new OperatorParam { Name = "gaussSize", DisplayName = "Gauss尺寸", DefaultValue = "9", Description = "≥3 奇数；0 或省略表示不平滑" },
                    new OperatorParam { Name = "minGray", DisplayName = "MinGray", DefaultValue = "40", Description = "阈值分割灰度下界" },
                    new OperatorParam { Name = "maxGray", DisplayName = "MaxGray", DefaultValue = "255", Description = "阈值分割灰度上界" },
                    new OperatorParam { Name = "genContourMode", DisplayName = "GenContourMode", DefaultValue = "border", Description = "rankBy=perimeter 时 GenContourRegionXld 的 Mode" },
                    new OperatorParam { Name = "rankBy", DisplayName = "排序依据", DefaultValue = "area", Description = "area＝区域面积最大；perimeter＝外轮廓周长最大（输出均经 FillUp，无空洞）" },
                    new OperatorParam { Name = "minContourPoints", DisplayName = "最少轮廓点数", DefaultValue = "3", Description = "perimeter 模式下少于该点数的 XLD 不参与比较" }
                },
                Ports =
                {
                    new PortDef { Name = "In", Direction = PortDirection.Input, DataType = typeof(CalibImage), ColorHex = "#FF9800" },
                    new PortDef { Name = "Out", Direction = PortDirection.Output, DataType = typeof(CalibImage), ColorHex = "#FF9800" }
                }
            },
            new OperatorDef
            {
                TypeId = "halcon_local_contrast",
                DisplayName = "HALCON 局部对比度",
                Description = "Illuminate + Emphasize，取代 CLAHE（HALCON 无同名算子时的典型替代）",
                Category = "HALCON",
                Params =
                {
                    new OperatorParam { Name = "illumMaskWidth", DisplayName = "Illuminate宽", DefaultValue = "41", Description = "Illuminate 掩模宽（奇数）" },
                    new OperatorParam { Name = "illumMaskHeight", DisplayName = "Illuminate高", DefaultValue = "41", Description = "Illuminate 掩模高（奇数）" },
                    new OperatorParam { Name = "illumFactor", DisplayName = "Illuminate因子", DefaultValue = "0.7", Description = "光照校正强度" },
                    new OperatorParam { Name = "emphasizeWidth", DisplayName = "Emphasize宽", DefaultValue = "7", Description = "Emphasize 掩模宽" },
                    new OperatorParam { Name = "emphasizeHeight", DisplayName = "Emphasize高", DefaultValue = "7", Description = "Emphasize 掩模高" },
                    new OperatorParam { Name = "emphasizeFactor", DisplayName = "Emphasize因子", DefaultValue = "1.0", Description = "细节强调系数" }
                },
                Ports =
                {
                    new PortDef { Name = "In", Direction = PortDirection.Input, DataType = typeof(CalibImage), ColorHex = "#FF9800" },
                    new PortDef { Name = "Out", Direction = PortDirection.Output, DataType = typeof(CalibImage), ColorHex = "#FF9800" }
                }
            },
            new OperatorDef
            {
                TypeId = "halcon_gray_mask",
                DisplayName = "HALCON GrayMask",
                Description = "Mask 灰度在区间内保留 Image，否则置 0（语义同 apply_mask）",
                Category = "HALCON",
                Params =
                {
                    new OperatorParam { Name = "maskMin", DisplayName = "MaskMin", DefaultValue = "1", Description = "Mask 有效像素下界" },
                    new OperatorParam { Name = "maskMax", DisplayName = "MaskMax", DefaultValue = "255", Description = "Mask 有效像素上界" }
                },
                Ports =
                {
                    new PortDef { Name = "Image", Direction = PortDirection.Input, DataType = typeof(CalibImage), ColorHex = "#FF9800" },
                    new PortDef { Name = "Mask", Direction = PortDirection.Input, DataType = typeof(CalibImage), ColorHex = "#FFB74D" },
                    new PortDef { Name = "Out", Direction = PortDirection.Output, DataType = typeof(CalibImage), ColorHex = "#FF9800" }
                }
            },
            new OperatorDef
            {
                TypeId = "halcon_binary_morph_circle",
                DisplayName = "HALCON 圆形态学",
                Description = "二值 Region 上 OpeningCircle / ClosingCircle（对应 morphology 清理）",
                Category = "HALCON",
                Params =
                {
                    new OperatorParam { Name = "op", DisplayName = "运算", DefaultValue = "open", Description = "open / close" },
                    new OperatorParam { Name = "radius", DisplayName = "半径", DefaultValue = "2.5", Description = "圆形结构元半径" },
                    new OperatorParam { Name = "iterations", DisplayName = "迭代次数", DefaultValue = "2", Description = "重复次数" }
                },
                Ports =
                {
                    new PortDef { Name = "In", Direction = PortDirection.Input, DataType = typeof(CalibImage), ColorHex = "#FF9800" },
                    new PortDef { Name = "Out", Direction = PortDirection.Output, DataType = typeof(CalibImage), ColorHex = "#FF9800" }
                }
            },
            new OperatorDef
            {
                TypeId = "halcon_xld_sample_points",
                DisplayName = "HALCON XLD 采样点",
                Description = "沿 Xld.Contours 中每条折线按弧长间距采样为 Point2D[]，BarIds 与轮廓一一对应。轮廓条数等于输入包中折线条数：若前级接 halcon_segment_xld，条数会按分段暴涨；需要「每条暗条一条轮廓」时请对 GenContour 的原始 Xld 直接采样。maxBars：只保留前 N 条（配合 contourOrder=length_desc 时常取周长最长的 N 条）；0 表示不限制。",
                Category = "HALCON",
                Params =
                {
                    new OperatorParam { Name = "spacing", DisplayName = "间距", DefaultValue = "4", Description = "沿轮廓弧长采样步长（像素）" },
                    new OperatorParam { Name = "maxBars", DisplayName = "最多条数", DefaultValue = "16", Description = "只处理前 N 条轮廓（在「轮廓顺序」下的前 N 条）；0 表示不限制" },
                    new OperatorParam
                    {
                        Name = "contourOrder",
                        DisplayName = "轮廓顺序",
                        DefaultValue = "list",
                        Description = "list=与 Xld.Contours 列表顺序一致（推荐）；length_desc=按周长从长到短（旧版行为）",
                        Options = new List<string> { "list", "length_desc" }
                    }
                },
                Ports =
                {
                    new PortDef { Name = "Xld", Direction = PortDirection.Input, DataType = typeof(HalconXldContourBundle), ColorHex = "#E65100" },
                    new PortDef { Name = "Points", Direction = PortDirection.Output, DataType = typeof(Point2D[]), ColorHex = "#2196F3" },
                    new PortDef { Name = "BarIds", Direction = PortDirection.Output, DataType = typeof(int[]), ColorHex = "#FFC107" }
                }
            },
            // ================================================================
            // HALCON 形状模板匹配
            // ================================================================
            new OperatorDef
            {
                TypeId = "halcon_create_shape_model",
                DisplayName = "HALCON 创建形状模板",
                Description = "CreateShapeModel(XLD/ROI 灰度)：基于 XLD 或 ROI 图像创建形状/缩放模板，返回 ModelId。流程中可接 halcon_binary_to_xld；形状模板页支持阈值/Canny/多边形 XLD 与矩形/多边形灰度 ROI。",
                Category = "HALCON",
                Params =
                {
                    new OperatorParam { Name = "numLevels", DisplayName = "NumLevels", DefaultValue = "4", Description = "金字塔层数，0=自动" },
                    new OperatorParam { Name = "angleStart", DisplayName = "AngleStart(°)", DefaultValue = "-30", Description = "起始角度(度)" },
                    new OperatorParam { Name = "angleExtent", DisplayName = "AngleExtent(°)", DefaultValue = "60", Description = "角度范围(度)" },
                    new OperatorParam { Name = "angleStep", DisplayName = "AngleStep(°)", DefaultValue = "0.5", Description = "角度步长(度)" },
                    new OperatorParam { Name = "optimization", DisplayName = "Optimization", DefaultValue = "auto", Description = "优化方式：auto / no_pregeneration / no_recurse" },
                    new OperatorParam { Name = "metric", DisplayName = "Metric", DefaultValue = "ignore_local_polarity", Description = "XLD 匹配度量；阈值轮廓请用 ignore_local_polarity（use_polarity 需 edge_direction）" },
                    new OperatorParam { Name = "contrast", DisplayName = "Contrast", DefaultValue = "30", Description = "已弃用(XLD 无 Contrast)，未设 MinContrast 时作回退" },
                    new OperatorParam { Name = "minContrast", DisplayName = "MinContrast", DefaultValue = "5", Description = "搜索图最小边缘对比度(create_shape_model_xld)" }
                },
                Ports =
                {
                    new PortDef { Name = "Xld", Direction = PortDirection.Input, DataType = typeof(HalconXldContourBundle), ColorHex = "#E65100", IsOptional = true },
                    new PortDef { Name = "ModelId", Direction = PortDirection.Output, DataType = typeof(long), ColorHex = "#9C27B0" }
                }
            },
            new OperatorDef
            {
                TypeId = "halcon_find_shape_model",
                DisplayName = "HALCON 查找形状模板",
                Description = "FindShapeModel：在图像中查找已创建的形状模板，返回匹配位置。需先有 halcon_create_shape_model。",
                Category = "HALCON",
                Params =
                {
                    new OperatorParam { Name = "angleStart", DisplayName = "AngleStart(°)", DefaultValue = "-30", Description = "起始角度(度)" },
                    new OperatorParam { Name = "angleExtent", DisplayName = "AngleExtent(°)", DefaultValue = "60", Description = "角度范围(度)" },
                    new OperatorParam { Name = "minScore", DisplayName = "MinScore", DefaultValue = "0.4", Description = "最小匹配得分；漏检降低、误检提高" },
                    new OperatorParam { Name = "numMatches", DisplayName = "NumMatches", DefaultValue = "0", Description = NumMatchesLatticeHint },
                    new OperatorParam { Name = "maxOverlap", DisplayName = "MaxOverlap", DefaultValue = "0.5", Description = "最大重叠度" },
                    new OperatorParam { Name = "subPixel", DisplayName = "SubPixel", DefaultValue = "interpolation", Description = "亚像素：none / interpolation / least_squares / least_squares_high" },
                    new OperatorParam { Name = "numLevels", DisplayName = "NumLevels", DefaultValue = "0", Description = "金字塔层数，0=创建模型时使用" },
                    new OperatorParam { Name = "greediness", DisplayName = "Greediness", DefaultValue = "0.75", Description = "贪心系数 0-1；漏检时降到 0.65~0.7" },
                    new OperatorParam { Name = "endScoreWeight", DisplayName = "端部得分权重", DefaultValue = "0.8", Description = "端部边缘对齐在分数中的权重，0=不修正" },
                    new OperatorParam { Name = "endArcFraction", DisplayName = "端部弧长占比", DefaultValue = "0.12", Description = "轮廓端部分段占比" }
                },
                Ports =
                {
                    new PortDef { Name = "In", Direction = PortDirection.Input, DataType = typeof(CalibImage), ColorHex = "#FF9800" },
                    new PortDef { Name = "ModelId", Direction = PortDirection.Input, DataType = typeof(long), ColorHex = "#9C27B0" },
                    new PortDef { Name = "Row", Direction = PortDirection.Output, DataType = typeof(double[]), ColorHex = "#4CAF50" },
                    new PortDef { Name = "Column", Direction = PortDirection.Output, DataType = typeof(double[]), ColorHex = "#2196F3" },
                    new PortDef { Name = "Angle", Direction = PortDirection.Output, DataType = typeof(double[]), ColorHex = "#FF5722" },
                    new PortDef { Name = "Score", Direction = PortDirection.Output, DataType = typeof(double[]), ColorHex = "#FFC107" }
                }
            },
            new OperatorDef
            {
                TypeId = "halcon_load_shape_model",
                DisplayName = "HALCON 加载形状模板",
                Description = "从 .shm 加载形状模型（形状模板页导出；文件名可含 xv_ 编码的创建参数）。filePath 相对当前流程 .flow.json 所在目录。",
                Category = "HALCON",
                Params =
                {
                    new OperatorParam { Name = "filePath", DisplayName = "模型文件", DefaultValue = "shape_model.shm", Description = ".shm 路径；推荐 xv_ 前缀编码参数的文件名，相对流程目录或绝对路径" }
                },
                Ports =
                {
                    new PortDef { Name = "ModelId", Direction = PortDirection.Output, DataType = typeof(long), ColorHex = "#9C27B0" }
                }
            },
            new OperatorDef
            {
                TypeId = "halcon_create_deformable_model",
                DisplayName = "HALCON 创建可变形模板",
                Description = "基于 XLD 创建 .dfm。deformableKind=planar 为透视(平面未标定)；local 为局部可变形。粗定位请用 halcon_create_shape_model。",
                Category = "HALCON",
                Params =
                {
                    new OperatorParam { Name = "deformableKind", DisplayName = "可变形类型", DefaultValue = "planar", Description = "planar/透视=CreatePlanarUncalib；local=CreateLocal" },
                    new OperatorParam { Name = "numLevels", DisplayName = "NumLevels", DefaultValue = "4", Description = "金字塔层数，0=自动" },
                    new OperatorParam { Name = "angleStart", DisplayName = "AngleStart(°)", DefaultValue = "-30", Description = "起始角度(度)" },
                    new OperatorParam { Name = "angleExtent", DisplayName = "AngleExtent(°)", DefaultValue = "60", Description = "角度范围(度)" },
                    new OperatorParam { Name = "angleStep", DisplayName = "AngleStep(°)", DefaultValue = "0.5", Description = "角度步长(度)" },
                    new OperatorParam { Name = "optimization", DisplayName = "Optimization", DefaultValue = "auto", Description = "auto / none / point_reduction_*" },
                    new OperatorParam { Name = "metric", DisplayName = "Metric", DefaultValue = "ignore_local_polarity", Description = "匹配度量" },
                    new OperatorParam { Name = "minContrast", DisplayName = "MinContrast", DefaultValue = "5", Description = "搜索图最小边缘对比度" },
                    new OperatorParam { Name = "scaleMin", DisplayName = "ScaleMin", DefaultValue = "0.97", Description = "可变形缩放下限" },
                    new OperatorParam { Name = "scaleMax", DisplayName = "ScaleMax", DefaultValue = "1.03", Description = "可变形缩放上限" }
                },
                Ports =
                {
                    new PortDef { Name = "Xld", Direction = PortDirection.Input, DataType = typeof(HalconXldContourBundle), ColorHex = "#E65100" },
                    new PortDef { Name = "ModelId", Direction = PortDirection.Output, DataType = typeof(long), ColorHex = "#9C27B0" }
                }
            },
            new OperatorDef
            {
                TypeId = "halcon_load_deformable_model",
                DisplayName = "HALCON 加载可变形模板",
                Description = "从 .dfm 加载可变形模型（文件名可含 xv_ 编码的创建参数）。filePath 相对流程 .flow.json 目录。",
                Category = "HALCON",
                Params =
                {
                    new OperatorParam { Name = "filePath", DisplayName = "模型文件", DefaultValue = "deformable_model.dfm", Description = ".dfm 路径；推荐 xv_ 前缀编码参数的文件名" }
                },
                Ports =
                {
                    new PortDef { Name = "ModelId", Direction = PortDirection.Output, DataType = typeof(long), ColorHex = "#9C27B0" }
                }
            },
            new OperatorDef
            {
                TypeId = "halcon_coarse_shape_match",
                DisplayName = "HALCON 粗定位(刚性/缩放)",
                Description = "全图粗定位。默认刚性搜索；当 coarseScaleMin/Max ≠ 1 时使用缩放搜索（需 ScaledShape 模型）。输出接 halcon_fine_deformable_match 的 Coarse* 端口。",
                Category = "HALCON",
                Params =
                {
                    new OperatorParam { Name = "angleStart", DisplayName = "AngleStart(°)", DefaultValue = "-30", Description = "相对 CoarseAngle 输入的起始偏移(度)；未连接时参考 0°" },
                    new OperatorParam { Name = "angleExtent", DisplayName = "AngleExtent(°)", DefaultValue = "60", Description = "相对 CoarseAngle 输入的角度搜索范围(度)" },
                    new OperatorParam { Name = "minScore", DisplayName = "MinScore", DefaultValue = "0.4", Description = "最低匹配分" },
                    new OperatorParam { Name = "numMatches", DisplayName = "NumMatches", DefaultValue = "0", Description = NumMatchesLatticeHint },
                    new OperatorParam { Name = "maxOverlap", DisplayName = "MaxOverlap", DefaultValue = "0.5", Description = "最大重叠度" },
                    new OperatorParam { Name = "subPixel", DisplayName = "SubPixel", DefaultValue = "none", Description = "none 最快；interpolation 更准" },
                    new OperatorParam { Name = "numLevels", DisplayName = "NumLevels", DefaultValue = "0", Description = "金字塔层数，0=模型默认" },
                    new OperatorParam { Name = "greediness", DisplayName = "Greediness", DefaultValue = "0.85", Description = "贪心系数" },
                    new OperatorParam { Name = "allowRetry", DisplayName = "失败重试", DefaultValue = "false", Description = "无结果时降分再搜" },
                    new OperatorParam { Name = "coarseScaleMin", DisplayName = "粗 ScaleMin", DefaultValue = "1.0", Description = "粗匹配缩放下限；=1 时不缩放搜索（需 ScaledShape 模型才支持缩放）" },
                    new OperatorParam { Name = "coarseScaleMax", DisplayName = "粗 ScaleMax", DefaultValue = "1.0", Description = "粗匹配缩放上限；=1 时不缩放搜索（需 ScaledShape 模型才支持缩放）" },
                    new OperatorParam { Name = "endScoreWeight", DisplayName = "端部得分权重", DefaultValue = "0.8", Description = "端部边缘对齐在分数中的权重，0=不修正；1=完全由端部决定。修正公式：score×(1-w+w×端部因子)" },
                    new OperatorParam { Name = "endArcFraction", DisplayName = "端部弧长占比", DefaultValue = "0.12", Description = "轮廓总长中两端各取的比例，用于端部边缘采样" }
                },
                Ports =
                {
                    new PortDef { Name = "In", Direction = PortDirection.Input, DataType = typeof(CalibImage), ColorHex = "#FF9800" },
                    new PortDef { Name = "ModelId", Direction = PortDirection.Input, DataType = typeof(long), ColorHex = "#9C27B0" },
                    new PortDef { Name = "CoarseAngle", Direction = PortDirection.Input, DataType = typeof(double), ColorHex = "#FF9800", IsOptional = true },
                    new PortDef { Name = "Row", Direction = PortDirection.Output, DataType = typeof(double[]), ColorHex = "#8BC34A" },
                    new PortDef { Name = "Column", Direction = PortDirection.Output, DataType = typeof(double[]), ColorHex = "#03A9F4" },
                    new PortDef { Name = "Angle", Direction = PortDirection.Output, DataType = typeof(double[]), ColorHex = "#FF9800" },
                    new PortDef { Name = "Scale", Direction = PortDirection.Output, DataType = typeof(double[]), ColorHex = "#26A69A" },
                    new PortDef { Name = "Score", Direction = PortDirection.Output, DataType = typeof(double[]), ColorHex = "#FFEB3B" }
                }
            },
            new OperatorDef
            {
                TypeId = "halcon_coarse_shape_reduce_domain",
                DisplayName = "HALCON 粗形状Mask",
                Description = "按粗位姿生成填充区域 Mask（非轮廓）。有下游时自动循环：每张 Mask 触发下游执行一次。",
                Category = "HALCON",
                Params =
                {
                    new OperatorParam { Name = "loopEmit", DisplayName = "循环输出Mask", DefaultValue = "true", Description = "true=每张粗候选循环输出 Mask 并驱动下游；false=仅输出 candidateIndex 指定的一张" },
                    new OperatorParam { Name = "candidateIndex", DisplayName = "单张索引", DefaultValue = "0", Description = "loopEmit=false 时输出的粗候选下标" },
                    new OperatorParam { Name = "maskErosionPx", DisplayName = "Mask内缩(px)", DefaultValue = "2", Description = "沿模板轮廓法向内缩（gen_parallel_contour_xld），非全向腐蚀；0=不内缩" },
                    new OperatorParam { Name = "maskFillDilatePx", DisplayName = "Mask填充膨胀(px)", DefaultValue = "0", Description = "0=凸包实心填充；>0=改用手动膨胀（凹形工件可试 30~80）" },
                    new OperatorParam { Name = "contourLevel", DisplayName = "模板层", DefaultValue = "1", Description = "生成填充区域用的形状模型层（输出为填充 Mask，不是轮廓 XLD）" },
                    new OperatorParam { Name = "maxCandidates", DisplayName = "最多Mask数", DefaultValue = "0", Description = "0=全部粗候选；N=仅前 N 个" }
                },
                Ports =
                {
                    new PortDef { Name = "In", Direction = PortDirection.Input, DataType = typeof(CalibImage), ColorHex = "#FF9800" },
                    new PortDef { Name = "ModelId", Direction = PortDirection.Input, DataType = typeof(long), ColorHex = "#9C27B0" },
                    new PortDef { Name = "CoarseRow", Direction = PortDirection.Input, DataType = typeof(double[]), ColorHex = "#8BC34A" },
                    new PortDef { Name = "CoarseColumn", Direction = PortDirection.Input, DataType = typeof(double[]), ColorHex = "#03A9F4" },
                    new PortDef { Name = "CoarseAngle", Direction = PortDirection.Input, DataType = typeof(double[]), ColorHex = "#FF9800", IsOptional = true },
                    new PortDef { Name = "CoarseScale", Direction = PortDirection.Input, DataType = typeof(double[]), ColorHex = "#26A69A", IsOptional = true },
                    new PortDef { Name = "CoarseScore", Direction = PortDirection.Input, DataType = typeof(double[]), ColorHex = "#FFEB3B", IsOptional = true },
                    new PortDef { Name = "Mask", Direction = PortDirection.Output, DataType = typeof(CalibImage), ColorHex = "#FFB74D" },
                    new PortDef { Name = "MaskIndex", Direction = PortDirection.Output, DataType = typeof(int), ColorHex = "#FF7043" },
                    new PortDef { Name = "MaskCount", Direction = PortDirection.Output, DataType = typeof(int), ColorHex = "#FF7043" },
                    new PortDef { Name = "CoarseRowOut", Direction = PortDirection.Output, DataType = typeof(double), ColorHex = "#8BC34A" },
                    new PortDef { Name = "CoarseColumnOut", Direction = PortDirection.Output, DataType = typeof(double), ColorHex = "#03A9F4" },
                    new PortDef { Name = "CoarseAngleOut", Direction = PortDirection.Output, DataType = typeof(double), ColorHex = "#FF9800" },
                    new PortDef { Name = "CoarseScaleOut", Direction = PortDirection.Output, DataType = typeof(double), ColorHex = "#26A69A" }
                }
            },
            new OperatorDef
            {
                TypeId = "halcon_reduce_domain_by_mask",
                DisplayName = "HALCON Mask域内图",
                Description = "原图 + 单张填充 Mask → reduce_domain 域内图（每次循环处理一张）。",
                Category = "HALCON",
                Params = { },
                Ports =
                {
                    new PortDef { Name = "Image", Direction = PortDirection.Input, DataType = typeof(CalibImage), ColorHex = "#FF9800" },
                    new PortDef { Name = "Mask", Direction = PortDirection.Input, DataType = typeof(CalibImage), ColorHex = "#FFB74D" },
                    new PortDef { Name = "Out", Direction = PortDirection.Output, DataType = typeof(CalibImage), ColorHex = "#FF9800" }
                }
            },
            new OperatorDef
            {
                TypeId = "halcon_fine_deformable_match",
                DisplayName = "HALCON 可变形精匹配",
                Description = "仅 .dfm + 单张域内图 In；CoarseRow/Column/Angle 接粗形状Mask 的 Coarse*Out（标量，每轮一个）。",
                Category = "HALCON",
                Params =
                {
                    new OperatorParam { Name = "fineAngleStart", DisplayName = "精 AngleStart(°)", DefaultValue = "", Description = "相对 CoarseAngle 输入的起始偏移(度)；留空时由「精角度余量」推导(=-余量)" },
                    new OperatorParam { Name = "fineAngleExtent", DisplayName = "精 AngleExtent(°)", DefaultValue = "", Description = "相对 CoarseAngle 输入的角度搜索范围(度)；留空时由「精角度余量」推导(=2×余量)" },
                    new OperatorParam { Name = "fineAngleMargin", DisplayName = "精角度余量(°)", DefaultValue = "5", Description = "未设 fineAngleStart/Extent 时：以 CoarseAngle 为中心 ± 该值(度)；粗 SubPixel=none 时建议 ≥5" },
                    new OperatorParam { Name = "fineMinScore", DisplayName = "精 MinScore", DefaultValue = "0.45", Description = "可变形最低分" },
                    new OperatorParam { Name = "fineNumLevels", DisplayName = "精 NumLevels", DefaultValue = "0", Description = "可变形金字塔层数，0=与建模一致" },
                    new OperatorParam { Name = "fineGreediness", DisplayName = "精 Greediness", DefaultValue = "0.75", Description = "可变形贪心系数" },
                    new OperatorParam { Name = "fineScaleMin", DisplayName = "精 ScaleMin", DefaultValue = "0.97", Description = "精匹配缩放下限" },
                    new OperatorParam { Name = "fineScaleMax", DisplayName = "精 ScaleMax", DefaultValue = "1.03", Description = "精匹配缩放上限" },
                    new OperatorParam { Name = "deformedContourMode", DisplayName = "变形轮廓", DefaultValue = "first", Description = "none=不输出(最快)；first=输出变形轮廓" },
                    new OperatorParam { Name = "fineAllowFallback", DisplayName = "精失败放宽重试", DefaultValue = "false", Description = "精匹配失败时降分/扩角再搜" },
                    new OperatorParam { Name = "roiMarginPx", DisplayName = "ROI边距(px)", DefaultValue = "12", Description = "粗位姿周围 CropRectangle2 的额外边距（透视 .dfm 必用）" },
                    new OperatorParam { Name = "maxRoiHalfPx", DisplayName = "ROI半长上限(px)", DefaultValue = "120", Description = "限制精匹配裁剪区半长，0=不限制" },
                    new OperatorParam { Name = "fineEndScoreWeight", DisplayName = "精端部得分权重", DefaultValue = "0.8", Description = "精匹配 Score 端部修正，0=关闭；优先用变形轮廓，否则 RigidModelId/ModelId 或 .dfm 模板" },
                    new OperatorParam { Name = "fineEndArcFraction", DisplayName = "精端部弧长占比", DefaultValue = "0.12", Description = "刚性模板轮廓端部分段占比" }
                },
                Ports =
                {
                    new PortDef { Name = "In", Direction = PortDirection.Input, DataType = typeof(CalibImage), ColorHex = "#FF9800" },
                    new PortDef { Name = "DeformableModelId", Direction = PortDirection.Input, DataType = typeof(long), ColorHex = "#7B1FA2" },
                    new PortDef { Name = "RigidModelId", Direction = PortDirection.Input, DataType = typeof(long), ColorHex = "#9C27B0", IsOptional = true },
                    new PortDef { Name = "FullImage", Direction = PortDirection.Input, DataType = typeof(CalibImage), ColorHex = "#FF9800", IsOptional = true },
                    new PortDef { Name = "CoarseRow", Direction = PortDirection.Input, DataType = typeof(double), ColorHex = "#8BC34A", IsOptional = true },
                    new PortDef { Name = "CoarseColumn", Direction = PortDirection.Input, DataType = typeof(double), ColorHex = "#03A9F4", IsOptional = true },
                    new PortDef { Name = "CoarseAngle", Direction = PortDirection.Input, DataType = typeof(double), ColorHex = "#FF9800", IsOptional = true },
                    new PortDef { Name = "Row", Direction = PortDirection.Output, DataType = typeof(double[]), ColorHex = "#4CAF50" },
                    new PortDef { Name = "Column", Direction = PortDirection.Output, DataType = typeof(double[]), ColorHex = "#2196F3" },
                    new PortDef { Name = "Angle", Direction = PortDirection.Output, DataType = typeof(double[]), ColorHex = "#FF5722" },
                    new PortDef { Name = "Score", Direction = PortDirection.Output, DataType = typeof(double[]), ColorHex = "#FFC107" },
                    new PortDef { Name = "DeformedXld", Direction = PortDirection.Output, DataType = typeof(HalconXldContourBundle), ColorHex = "#E65100", IsOptional = true }
                }
            },
            new OperatorDef
            {
                TypeId = "halcon_fine_scaled_shape_match",
                DisplayName = "HALCON 缩放形状精匹配",
                Description = "仅 ScaledShape .shm + 单张域内图 In；在粗位姿 ROI 内精定位。CoarseRow/Column/Angle/Scale 任一端口有输入则该自由度固定为粗值、不再搜索；未接的 DOF 仍按 fineAngleMargin / fineScaleMin/Max 搜索。",
                Category = "HALCON",
                Params =
                {
                    new OperatorParam { Name = "fineAngleStart", DisplayName = "精 AngleStart(°)", DefaultValue = "", Description = "相对 CoarseAngle 输入的起始偏移(度)；留空时由「精角度余量」推导(=-余量)" },
                    new OperatorParam { Name = "fineAngleExtent", DisplayName = "精 AngleExtent(°)", DefaultValue = "", Description = "相对 CoarseAngle 输入的角度搜索范围(度)；留空时由「精角度余量」推导(=2×余量)" },
                    new OperatorParam { Name = "fineAngleMargin", DisplayName = "精角度余量(°)", DefaultValue = "5", Description = "未设 fineAngleStart/Extent 且未接 CoarseAngle 时：以 CoarseAngle 为中心 ± 该值(度)" },
                    new OperatorParam { Name = "fineMinScore", DisplayName = "精 MinScore", DefaultValue = "0.45", Description = "缩放形状最低分" },
                    new OperatorParam { Name = "fineNumLevels", DisplayName = "精 NumLevels", DefaultValue = "0", Description = "金字塔层数，0=与建模一致" },
                    new OperatorParam { Name = "fineGreediness", DisplayName = "精 Greediness", DefaultValue = "0.75", Description = "贪心系数" },
                    new OperatorParam { Name = "fineScaleMin", DisplayName = "精 ScaleMin", DefaultValue = "0.97", Description = "未接 CoarseScale 时的缩放下限；已接 CoarseScale 时忽略" },
                    new OperatorParam { Name = "fineScaleMax", DisplayName = "精 ScaleMax", DefaultValue = "1.03", Description = "未接 CoarseScale 时的缩放上限；已接 CoarseScale 时忽略" },
                    new OperatorParam { Name = "shapeContourMode", DisplayName = "模板轮廓", DefaultValue = "first", Description = "none=不输出；first=输出缩放后的模板轮廓" },
                    new OperatorParam { Name = "roiMarginPx", DisplayName = "ROI边距(px)", DefaultValue = "12", Description = "粗位姿周围 CropRectangle2 的额外边距" },
                    new OperatorParam { Name = "maxRoiHalfPx", DisplayName = "ROI半长上限(px)", DefaultValue = "120", Description = "限制精匹配裁剪区半长，0=不限制" },
                    new OperatorParam { Name = "fineEndScoreWeight", DisplayName = "精端部得分权重", DefaultValue = "0.8", Description = "精匹配 Score 端部修正，0=关闭" },
                    new OperatorParam { Name = "fineEndArcFraction", DisplayName = "精端部弧长占比", DefaultValue = "0.12", Description = "模板轮廓端部分段占比" }
                },
                Ports =
                {
                    new PortDef { Name = "In", Direction = PortDirection.Input, DataType = typeof(CalibImage), ColorHex = "#FF9800" },
                    new PortDef { Name = "ModelId", Direction = PortDirection.Input, DataType = typeof(long), ColorHex = "#9C27B0" },
                    new PortDef { Name = "FullImage", Direction = PortDirection.Input, DataType = typeof(CalibImage), ColorHex = "#FF9800", IsOptional = true },
                    new PortDef { Name = "CoarseRow", Direction = PortDirection.Input, DataType = typeof(double), ColorHex = "#8BC34A", IsOptional = true },
                    new PortDef { Name = "CoarseColumn", Direction = PortDirection.Input, DataType = typeof(double), ColorHex = "#03A9F4", IsOptional = true },
                    new PortDef { Name = "CoarseAngle", Direction = PortDirection.Input, DataType = typeof(double), ColorHex = "#FF9800", IsOptional = true },
                    new PortDef { Name = "CoarseScale", Direction = PortDirection.Input, DataType = typeof(double), ColorHex = "#26A69A", IsOptional = true },
                    new PortDef { Name = "Row", Direction = PortDirection.Output, DataType = typeof(double[]), ColorHex = "#4CAF50" },
                    new PortDef { Name = "Column", Direction = PortDirection.Output, DataType = typeof(double[]), ColorHex = "#2196F3" },
                    new PortDef { Name = "Angle", Direction = PortDirection.Output, DataType = typeof(double[]), ColorHex = "#FF5722" },
                    new PortDef { Name = "Scale", Direction = PortDirection.Output, DataType = typeof(double[]), ColorHex = "#26A69A" },
                    new PortDef { Name = "Score", Direction = PortDirection.Output, DataType = typeof(double[]), ColorHex = "#FFC107" },
                    new PortDef { Name = "ShapeXld", Direction = PortDirection.Output, DataType = typeof(HalconXldContourBundle), ColorHex = "#E65100", IsOptional = true }
                }
            },
            new OperatorDef
            {
                TypeId = "halcon_coarse_fine_shape_match",
                DisplayName = "HALCON 粗定位+可变形精匹配(组合)",
                Description = "单节点完成粗+精（内部调用 halcon_coarse_shape_match + halcon_fine_deformable_match）。拆分时请用两个独立算子串联。",
                Category = "HALCON",
                Params =
                {
                    new OperatorParam { Name = "coarseAngleStart", DisplayName = "粗 AngleStart(°)", DefaultValue = "-30", Description = "全图粗搜起始角(度)，相对图像 0°" },
                    new OperatorParam { Name = "coarseAngleExtent", DisplayName = "粗 AngleExtent(°)", DefaultValue = "60", Description = "全图粗搜角度范围(度)" },
                    new OperatorParam { Name = "coarseMinScore", DisplayName = "粗 MinScore", DefaultValue = "0.4", Description = "粗匹配最低分" },
                    new OperatorParam { Name = "coarseNumMatches", DisplayName = "粗 NumMatches", DefaultValue = "0", Description = NumMatchesLatticeHint },
                    new OperatorParam { Name = "coarseGreediness", DisplayName = "粗 Greediness", DefaultValue = "0.85", Description = "粗定位贪心系数，略高可加速" },
                    new OperatorParam { Name = "coarseSubPixel", DisplayName = "粗 SubPixel", DefaultValue = "none", Description = "粗定位亚像素：none 最快，interpolation 更准" },
                    new OperatorParam { Name = "coarseNumLevels", DisplayName = "粗 NumLevels", DefaultValue = "0", Description = "粗金字塔层数，0=用模型默认" },
                    new OperatorParam { Name = "coarseAllowRetry", DisplayName = "粗失败重试", DefaultValue = "false", Description = "无结果时是否降分再搜一次（会拖慢）" },
                    new OperatorParam { Name = "coarseScaleMin", DisplayName = "粗 ScaleMin", DefaultValue = "1.0", Description = "粗匹配缩放下限；=1 时不缩放搜索（需 ScaledShape 模型）" },
                    new OperatorParam { Name = "coarseScaleMax", DisplayName = "粗 ScaleMax", DefaultValue = "1.0", Description = "粗匹配缩放上限；=1 时不缩放搜索（需 ScaledShape 模型）" },
                    new OperatorParam { Name = "endScoreWeight", DisplayName = "粗端部得分权重", DefaultValue = "0.8", Description = "粗定位 CoarseScore 端部修正权重，0=不修正" },
                    new OperatorParam { Name = "endArcFraction", DisplayName = "粗端部弧长占比", DefaultValue = "0.12", Description = "粗定位轮廓端部分段占比" },
                    new OperatorParam { Name = "fineEndScoreWeight", DisplayName = "精端部得分权重", DefaultValue = "0.8", Description = "精匹配 Score 端部修正权重，0=不修正；留空则与粗相同" },
                    new OperatorParam { Name = "fineEndArcFraction", DisplayName = "精端部弧长占比", DefaultValue = "0.12", Description = "精匹配端部分段占比；留空则与粗相同" },
                    new OperatorParam { Name = "fineAngleStart", DisplayName = "精 AngleStart(°)", DefaultValue = "", Description = "相对各粗候选角度的起始偏移(度)；留空时由「精角度余量」推导" },
                    new OperatorParam { Name = "fineAngleExtent", DisplayName = "精 AngleExtent(°)", DefaultValue = "", Description = "相对各粗候选角度的搜索范围(度)；留空时由「精角度余量」推导" },
                    new OperatorParam { Name = "fineAngleMargin", DisplayName = "精角度余量(°)", DefaultValue = "5", Description = "未设 fineAngleStart/Extent 时：以各粗候选 CoarseAngle 为中心 ± 该值(度)" },
                    new OperatorParam { Name = "fineMinScore", DisplayName = "精 MinScore", DefaultValue = "0.45", Description = "可变形最低分" },
                    new OperatorParam { Name = "fineNumLevels", DisplayName = "精 NumLevels", DefaultValue = "0", Description = "可变形金字塔层数，0=与建模一致" },
                    new OperatorParam { Name = "fineGreediness", DisplayName = "精 Greediness", DefaultValue = "0.75", Description = "可变形贪心系数" },
                    new OperatorParam { Name = "fineScaleMin", DisplayName = "精 ScaleMin", DefaultValue = "0.97", Description = "精匹配缩放下限" },
                    new OperatorParam { Name = "fineScaleMax", DisplayName = "精 ScaleMax", DefaultValue = "1.03", Description = "精匹配缩放上限" },
                    new OperatorParam { Name = "roiMarginPx", DisplayName = "ROI边距(px)", DefaultValue = "12", Description = "粗结果周围可变形搜索矩形额外边距" },
                    new OperatorParam { Name = "maxRoiHalfPx", DisplayName = "ROI半长上限(px)", DefaultValue = "120", Description = "限制精匹配裁剪区，0=不限制" },
                    new OperatorParam { Name = "maxFineMatches", DisplayName = "最多精匹配数", DefaultValue = "2", Description = "对分数最高的前 N 个粗候选做精匹配" },
                    new OperatorParam { Name = "deformedContourMode", DisplayName = "变形轮廓", DefaultValue = "first", Description = "none=不输出(最快)；first=仅首个精匹配；all=全部" },
                    new OperatorParam { Name = "fineAllowFallback", DisplayName = "精失败放宽重试", DefaultValue = "false", Description = "精匹配失败时降分/扩角再搜；默认关" }
                },
                Ports =
                {
                    new PortDef { Name = "In", Direction = PortDirection.Input, DataType = typeof(CalibImage), ColorHex = "#FF9800" },
                    new PortDef { Name = "RigidModelId", Direction = PortDirection.Input, DataType = typeof(long), ColorHex = "#9C27B0" },
                    new PortDef { Name = "DeformableModelId", Direction = PortDirection.Input, DataType = typeof(long), ColorHex = "#7B1FA2" },
                    new PortDef { Name = "Row", Direction = PortDirection.Output, DataType = typeof(double[]), ColorHex = "#4CAF50" },
                    new PortDef { Name = "Column", Direction = PortDirection.Output, DataType = typeof(double[]), ColorHex = "#2196F3" },
                    new PortDef { Name = "Angle", Direction = PortDirection.Output, DataType = typeof(double[]), ColorHex = "#FF5722" },
                    new PortDef { Name = "Score", Direction = PortDirection.Output, DataType = typeof(double[]), ColorHex = "#FFC107" },
                    new PortDef { Name = "CoarseRow", Direction = PortDirection.Output, DataType = typeof(double[]), ColorHex = "#8BC34A" },
                    new PortDef { Name = "CoarseColumn", Direction = PortDirection.Output, DataType = typeof(double[]), ColorHex = "#03A9F4" },
                    new PortDef { Name = "CoarseAngle", Direction = PortDirection.Output, DataType = typeof(double[]), ColorHex = "#FF9800" },
                    new PortDef { Name = "CoarseScale", Direction = PortDirection.Output, DataType = typeof(double[]), ColorHex = "#26A69A" },
                    new PortDef { Name = "CoarseScore", Direction = PortDirection.Output, DataType = typeof(double[]), ColorHex = "#FFEB3B" },
                    new PortDef { Name = "DeformedXld", Direction = PortDirection.Output, DataType = typeof(HalconXldContourBundle), ColorHex = "#E65100", IsOptional = true }
                }
            },
            new OperatorDef
            {
                TypeId = "halcon_shape_match_centers",
                DisplayName = "HALCON 匹配中心→点列",
                Description = "将 FindShapeModel 的 Row/Column 转为 Point2D[]（X=列,Y=行），并按参数排序。默认 yx=先行后列（与九点世界坐标行优先一致）；接阵列过滤时可设 grid 并按 GridRow/GridCol 排序。",
                Category = "HALCON",
                Params =
                {
                    new OperatorParam
                    {
                        Name = "sortMode",
                        DisplayName = "排序方式",
                        DefaultValue = "yx",
                        Description = "yx=按图像行Y再列X；xy=先列X再行Y；grid=按 GridRow/GridCol（须连接）；none=保持输入顺序",
                        Options = new List<string> { "yx", "xy", "grid", "none" }
                    }
                },
                Ports =
                {
                    new PortDef { Name = "Row", Direction = PortDirection.Input, DataType = typeof(double[]), ColorHex = "#4CAF50" },
                    new PortDef { Name = "Column", Direction = PortDirection.Input, DataType = typeof(double[]), ColorHex = "#2196F3" },
                    new PortDef { Name = "GridRow", Direction = PortDirection.Input, DataType = typeof(int[]), ColorHex = "#8BC34A", IsOptional = true },
                    new PortDef { Name = "GridCol", Direction = PortDirection.Input, DataType = typeof(int[]), ColorHex = "#CDDC39", IsOptional = true },
                    new PortDef { Name = "Points", Direction = PortDirection.Output, DataType = typeof(Point2D[]), ColorHex = "#2196F3" }
                }
            },
            new OperatorDef
            {
                TypeId = "halcon_shape_match_grid_to_trajectory",
                DisplayName = "HALCON 落格匹配轮廓→轨迹",
                Description =
                    "按落格顺序将每个匹配的 GetShapeModelContours 轮廓变换到图像坐标并拼接为点列（非匹配中心）。需 ModelId 与落格相同的 .shm；Angle 用于旋转变换。输出 Points+BarIds 可接「轮廓点简化」；SamplePts 可接「轮廓转焊道路径」。",
                Category = "HALCON",
                Params =
                {
                    new OperatorParam { Name = "contourLevel", DisplayName = "轮廓层", DefaultValue = "1", Description = "GetShapeModelContours 金字塔层" },
                    new OperatorParam
                    {
                        Name = "contourMode",
                        DisplayName = "轮廓选取",
                        DefaultValue = "outer",
                        Description = "outer=每格取面积最大外形；all=模板全部子轮廓",
                        Options = new List<string> { "outer", "all" }
                    },
                    new OperatorParam
                    {
                        Name = "connectOrder",
                        DisplayName = "匹配顺序",
                        DefaultValue = "col_major",
                        Description = "落格实例的拼接顺序",
                        Options = new List<string> { "row_major", "col_major", "snake_row", "snake_col" }
                    },
                    new OperatorParam
                    {
                        Name = "barIdSource",
                        DisplayName = "分组条号",
                        DefaultValue = "per_match",
                        Description = "写入 GroupBarIds（PLC separate_batch 用）；per_match=0..N-1；grid_cell=行×列格号(须 gridCols)；BarIds 为轮廓段号",
                        Options = new List<string> { "per_match", "grid_cell", "grid_col", "grid_row", "single" }
                    },
                    new OperatorParam { Name = "gridRows", DisplayName = "落格行数", DefaultValue = "8", Description = $"barIdSource=grid_cell 时 gr×cols+gc；0=从 GridRow/Col 推断；{LatticeGridMetaHint}" },
                    new OperatorParam { Name = "gridCols", DisplayName = "落格列数", DefaultValue = "2", Description = $"barIdSource=grid_cell 时列宽；须与落格算子一致；{LatticeGridMetaHint}" },
                    new OperatorParam { Name = "closeTolPx", DisplayName = "闭合容差(px)", DefaultValue = "0.5", Description = "仅当首尾几乎重合(≤容差)时不补点；否则强制在末尾补起点闭合" },
                    new OperatorParam { Name = "defaultZ", DisplayName = "SamplePts Z", DefaultValue = "0", Description = "SamplePts 的 Z" }
                },
                Ports =
                {
                    new PortDef { Name = "ModelId", Direction = PortDirection.Input, DataType = typeof(long), ColorHex = "#9C27B0" },
                    new PortDef { Name = "Row", Direction = PortDirection.Input, DataType = typeof(double[]), ColorHex = "#4CAF50" },
                    new PortDef { Name = "Column", Direction = PortDirection.Input, DataType = typeof(double[]), ColorHex = "#2196F3" },
                    new PortDef { Name = "Angle", Direction = PortDirection.Input, DataType = typeof(double[]), ColorHex = "#FF5722", IsOptional = true },
                    new PortDef { Name = "GridRow", Direction = PortDirection.Input, DataType = typeof(int[]), ColorHex = "#8BC34A", IsOptional = true },
                    new PortDef { Name = "GridCol", Direction = PortDirection.Input, DataType = typeof(int[]), ColorHex = "#CDDC39", IsOptional = true },
                    new PortDef { Name = "Points", Direction = PortDirection.Output, DataType = typeof(Point2D[]), ColorHex = "#2196F3" },
                    new PortDef { Name = "BarIds", Direction = PortDirection.Output, DataType = typeof(int[]), ColorHex = "#FFC107" },
                    new PortDef { Name = "GroupBarIds", Direction = PortDirection.Output, DataType = typeof(int[]), ColorHex = "#FFEB3B" },
                    new PortDef { Name = "SamplePts", Direction = PortDirection.Output, DataType = typeof(CalibPoint3D[]), ColorHex = "#00BCD4" }
                }
            },
            new OperatorDef
            {
                TypeId = "halcon_ransac_pick_shape_match_lattice",
                DisplayName = "HALCON RANSAC 阵列落格",
                Description = "鲁棒拟合/RANSAC：输入 FindShapeModel 的 Row/Column/Angle/Score，估计 u/v 格网并每格取最高分，直接输出 16 点及 GridRow/GridCol，可接「显示形状匹配」。",
                Category = "HALCON",
                Params =
                {
                    new OperatorParam { Name = "gridRows", DisplayName = "行数", DefaultValue = "8", Description = $"阵列行数；{LatticeGridMetaHint}" },
                    new OperatorParam { Name = "gridCols", DisplayName = "列数", DefaultValue = "2", Description = $"阵列列数；{LatticeGridMetaHint}" },
                    new OperatorParam { Name = "minScoreKeep", DisplayName = "最低得分", DefaultValue = "0", Description = "0=关闭；每格保留匹配的最低 Score" },
                    new OperatorParam { Name = "snapTolerancePx", DisplayName = "吸附容差(px)", DefaultValue = "0", Description = "0=自动；u/v 落格吸附半径" },
                    new OperatorParam { Name = "ransacIterations", DisplayName = "RANSAC 迭代", DefaultValue = "500", Description = "假设采样次数（含 θ 扰动与三点最小集）" },
                    new OperatorParam { Name = "inlierSnapFactor", DisplayName = "内点吸附系数", DefaultValue = "0.45", Description = "评分阶段 snap 相对自动容差的比例" },
                    new OperatorParam { Name = "uvProjectionSvg", DisplayName = "u/v 投影图", DefaultValue = "", Description = "相对流程目录的 SVG 路径，如 uv-projection.svg" },
                    new OperatorParam { Name = "debugLog", DisplayName = "诊断日志", DefaultValue = "auto", Description = "写入 *.grid-filter.log" }
                },
                Ports =
                {
                    new PortDef { Name = "Row", Direction = PortDirection.Input, DataType = typeof(double[]), ColorHex = "#4CAF50" },
                    new PortDef { Name = "Column", Direction = PortDirection.Input, DataType = typeof(double[]), ColorHex = "#2196F3" },
                    new PortDef { Name = "Angle", Direction = PortDirection.Input, DataType = typeof(double[]), ColorHex = "#FF5722", IsOptional = true },
                    new PortDef { Name = "Score", Direction = PortDirection.Input, DataType = typeof(double[]), ColorHex = "#FFC107", IsOptional = true },
                    new PortDef { Name = "Row", Direction = PortDirection.Output, DataType = typeof(double[]), ColorHex = "#4CAF50" },
                    new PortDef { Name = "Column", Direction = PortDirection.Output, DataType = typeof(double[]), ColorHex = "#2196F3" },
                    new PortDef { Name = "Angle", Direction = PortDirection.Output, DataType = typeof(double[]), ColorHex = "#FF5722" },
                    new PortDef { Name = "Score", Direction = PortDirection.Output, DataType = typeof(double[]), ColorHex = "#FFC107" },
                    new PortDef { Name = "GridRow", Direction = PortDirection.Output, DataType = typeof(int[]), ColorHex = "#8BC34A" },
                    new PortDef { Name = "GridCol", Direction = PortDirection.Output, DataType = typeof(int[]), ColorHex = "#CDDC39" },
                    new PortDef { Name = "LatticeAngle", Direction = PortDirection.Output, DataType = typeof(double), ColorHex = "#C5E1A5" },
                    new PortDef { Name = "ChainDirectionAngle", Direction = PortDirection.Output, DataType = typeof(double), ColorHex = "#FF9800" },
                    new PortDef { Name = "ConsensusMatchAngle", Direction = PortDirection.Output, DataType = typeof(double), ColorHex = "#FF5722" },
                    new PortDef { Name = "ConsensusPickIndices", Direction = PortDirection.Output, DataType = typeof(int[]), ColorHex = "#8BC34A" },
                    new PortDef { Name = "LatticeRows", Direction = PortDirection.Output, DataType = typeof(int), ColorHex = "#8BC34A" },
                    new PortDef { Name = "LatticeCols", Direction = PortDirection.Output, DataType = typeof(int), ColorHex = "#CDDC39" },
                    new PortDef { Name = "AxesSwapped", Direction = PortDirection.Output, DataType = typeof(int), ColorHex = "#9E9E9E" },
                    new PortDef { Name = "TwoColumnDeltaU", Direction = PortDirection.Output, DataType = typeof(double), ColorHex = "#9E9E9E", IsOptional = true },
                    new PortDef { Name = "TwoColumnDeltaV", Direction = PortDirection.Output, DataType = typeof(double), ColorHex = "#9E9E9E", IsOptional = true },
                    new PortDef { Name = "TwoColumnDeltaSummary", Direction = PortDirection.Output, DataType = typeof(string), ColorHex = "#9E9E9E", IsOptional = true }
                }
            },
            new OperatorDef
            {
                TypeId = "halcon_pick_shape_match_lattice",
                DisplayName = "HALCON 阵列格点筛选",
                Description = "链向定向 + u/v 落格聚类：2 列×8 行每格取最高分，直接输出 rows×cols 个模板（如 16 个）及 GridRow/GridCol。链向角由输出点集估计。可接阵列聚类拟合/显示。",
                Category = "HALCON",
                Params =
                {
                    new OperatorParam { Name = "gridRows", DisplayName = "行数", DefaultValue = "8", Description = $"阵列行数（2 列×8 行 → 行=8、列=2；内部可自动 swap）；{LatticeGridMetaHint}" },
                    new OperatorParam { Name = "gridCols", DisplayName = "列数", DefaultValue = "2", Description = $"阵列列数；{LatticeGridMetaHint}" },
                    new OperatorParam { Name = "minScoreKeep", DisplayName = "最低得分", DefaultValue = "0", Description = "0=关闭；每格保留匹配的最低 Score" },
                    new OperatorParam { Name = "debugLog", DisplayName = "诊断日志", DefaultValue = "auto", Description = "写入 *.grid-filter.log" }
                },
                Ports =
                {
                    new PortDef { Name = "Row", Direction = PortDirection.Input, DataType = typeof(double[]), ColorHex = "#4CAF50" },
                    new PortDef { Name = "Column", Direction = PortDirection.Input, DataType = typeof(double[]), ColorHex = "#2196F3" },
                    new PortDef { Name = "Angle", Direction = PortDirection.Input, DataType = typeof(double[]), ColorHex = "#FF5722", IsOptional = true },
                    new PortDef { Name = "Score", Direction = PortDirection.Input, DataType = typeof(double[]), ColorHex = "#FFC107", IsOptional = true },
                    new PortDef { Name = "Row", Direction = PortDirection.Output, DataType = typeof(double[]), ColorHex = "#4CAF50" },
                    new PortDef { Name = "Column", Direction = PortDirection.Output, DataType = typeof(double[]), ColorHex = "#2196F3" },
                    new PortDef { Name = "Angle", Direction = PortDirection.Output, DataType = typeof(double[]), ColorHex = "#FF5722" },
                    new PortDef { Name = "Score", Direction = PortDirection.Output, DataType = typeof(double[]), ColorHex = "#FFC107" },
                    new PortDef { Name = "GridRow", Direction = PortDirection.Output, DataType = typeof(int[]), ColorHex = "#8BC34A" },
                    new PortDef { Name = "GridCol", Direction = PortDirection.Output, DataType = typeof(int[]), ColorHex = "#CDDC39" },
                    new PortDef { Name = "LatticeAngle", Direction = PortDirection.Output, DataType = typeof(double), ColorHex = "#C5E1A5" },
                    new PortDef { Name = "ChainDirectionAngle", Direction = PortDirection.Output, DataType = typeof(double), ColorHex = "#FF9800" },
                    new PortDef { Name = "ConsensusMatchAngle", Direction = PortDirection.Output, DataType = typeof(double), ColorHex = "#FF5722" },
                    new PortDef { Name = "ConsensusPickIndices", Direction = PortDirection.Output, DataType = typeof(int[]), ColorHex = "#8BC34A" },
                    new PortDef { Name = "LatticeRows", Direction = PortDirection.Output, DataType = typeof(int), ColorHex = "#8BC34A" },
                    new PortDef { Name = "LatticeCols", Direction = PortDirection.Output, DataType = typeof(int), ColorHex = "#CDDC39" },
                    new PortDef { Name = "AxesSwapped", Direction = PortDirection.Output, DataType = typeof(int), ColorHex = "#9E9E9E" }
                }
            },
            new OperatorDef
            {
                TypeId = "halcon_chain_strip_bootstrap",
                DisplayName = "HALCON 链向·引导",
                Description = "条带第 1 步：Score 最高 K 点（2×8 为 K=8）估链向连线，θ_u=链向−90° 作格网 u 轴；另输出 PCA/HALCON 角对照。",
                Category = "HALCON",
                Params =
                {
                    new OperatorParam { Name = "gridRows", DisplayName = "行数", DefaultValue = "8", Description = $"阵列行数；{LatticeGridMetaHint}" },
                    new OperatorParam { Name = "gridCols", DisplayName = "列数", DefaultValue = "2", Description = $"阵列列数；{LatticeGridMetaHint}" },
                    new OperatorParam { Name = "debugLog", DisplayName = "诊断日志", DefaultValue = "auto", Description = "写入 *.grid-filter.log" }
                },
                Ports =
                {
                    new PortDef { Name = "Row", Direction = PortDirection.Input, DataType = typeof(double[]), ColorHex = "#4CAF50" },
                    new PortDef { Name = "Column", Direction = PortDirection.Input, DataType = typeof(double[]), ColorHex = "#2196F3" },
                    new PortDef { Name = "Angle", Direction = PortDirection.Input, DataType = typeof(double[]), ColorHex = "#FF5722", IsOptional = true },
                    new PortDef { Name = "Score", Direction = PortDirection.Input, DataType = typeof(double[]), ColorHex = "#FFC107", IsOptional = true },
                    new PortDef { Name = "BootstrapPickIndices", Direction = PortDirection.Output, DataType = typeof(int[]), ColorHex = "#8BC34A" },
                    new PortDef { Name = "ChainBootstrapAngle", Direction = PortDirection.Output, DataType = typeof(double), ColorHex = "#C5E1A5" },
                    new PortDef { Name = "LatticeAngle", Direction = PortDirection.Output, DataType = typeof(double), ColorHex = "#C5E1A5" },
                    new PortDef { Name = "PcaChainAngle", Direction = PortDirection.Output, DataType = typeof(double), ColorHex = "#FF9800" },
                    new PortDef { Name = "VAxisImageAngle", Direction = PortDirection.Output, DataType = typeof(double), ColorHex = "#FF9800" },
                    new PortDef { Name = "ConsensusMatchAngle", Direction = PortDirection.Output, DataType = typeof(double), ColorHex = "#FF5722" },
                    new PortDef { Name = "MatchConcentration", Direction = PortDirection.Output, DataType = typeof(double), ColorHex = "#9E9E9E" }
                }
            },
            new OperatorDef
            {
                TypeId = "halcon_chain_strip_pick_uv_grid",
                DisplayName = "HALCON 链向·u/v 落格16",
                Description = "接「链向·引导」：沿用 ChainBootstrapAngle（格网 θ）投影 u/v，左右池聚类行心，26 点落 16 格。不按 Score 筛选（请在前级 FindShapeModel 卡分）；每格在 snap 候选内分数优先、其次距格心。",
                Category = "HALCON",
                Params =
                {
                    new OperatorParam { Name = "gridRows", DisplayName = "行数", DefaultValue = "8", Description = LatticeGridMetaHint },
                    new OperatorParam { Name = "gridCols", DisplayName = "列数", DefaultValue = "2", Description = LatticeGridMetaHint },
                    new OperatorParam { Name = "minScoreKeep", DisplayName = "最低得分(已忽略)", DefaultValue = "0", Description = "保留参数兼容；落格不再按分过滤，请在前级匹配设 minScore" },
                    new OperatorParam { Name = "snapTolerancePx", DisplayName = "格点容差(px)", DefaultValue = "0", Description = "0=自动" },
                    new OperatorParam { Name = "uvProjectionSvg", DisplayName = "u/v投影SVG", DefaultValue = "", Description = "相对 flow 目录或绝对路径；留空不写 SVG" },
                    new OperatorParam { Name = "debugLog", DisplayName = "诊断日志", DefaultValue = "auto" }
                },
                Ports =
                {
                    new PortDef { Name = "Row", Direction = PortDirection.Input, DataType = typeof(double[]), ColorHex = "#4CAF50" },
                    new PortDef { Name = "Column", Direction = PortDirection.Input, DataType = typeof(double[]), ColorHex = "#2196F3" },
                    new PortDef { Name = "Angle", Direction = PortDirection.Input, DataType = typeof(double[]), ColorHex = "#FF5722", IsOptional = true },
                    new PortDef { Name = "Score", Direction = PortDirection.Input, DataType = typeof(double[]), ColorHex = "#FFC107", IsOptional = true },
                    new PortDef { Name = "ChainBootstrapAngle", Direction = PortDirection.Input, DataType = typeof(double), ColorHex = "#C5E1A5", IsOptional = true },
                    new PortDef { Name = "PcaChainAngle", Direction = PortDirection.Input, DataType = typeof(double), ColorHex = "#FF9800", IsOptional = true },
                    new PortDef { Name = "BootstrapPickIndices", Direction = PortDirection.Input, DataType = typeof(int[]), ColorHex = "#8BC34A", IsOptional = true },
                    new PortDef { Name = "Row", Direction = PortDirection.Output, DataType = typeof(double[]), ColorHex = "#4CAF50" },
                    new PortDef { Name = "Column", Direction = PortDirection.Output, DataType = typeof(double[]), ColorHex = "#2196F3" },
                    new PortDef { Name = "Angle", Direction = PortDirection.Output, DataType = typeof(double[]), ColorHex = "#FF5722" },
                    new PortDef { Name = "Score", Direction = PortDirection.Output, DataType = typeof(double[]), ColorHex = "#FFC107" },
                    new PortDef { Name = "GridRow", Direction = PortDirection.Output, DataType = typeof(int[]), ColorHex = "#8BC34A" },
                    new PortDef { Name = "GridCol", Direction = PortDirection.Output, DataType = typeof(int[]), ColorHex = "#CDDC39" },
                    new PortDef { Name = "LatticeAngle", Direction = PortDirection.Output, DataType = typeof(double), ColorHex = "#C5E1A5" },
                    new PortDef { Name = "ChainDirectionAngle", Direction = PortDirection.Output, DataType = typeof(double), ColorHex = "#FF9800" },
                    new PortDef { Name = "ConsensusMatchAngle", Direction = PortDirection.Output, DataType = typeof(double), ColorHex = "#FF5722" },
                    new PortDef { Name = "ConsensusPickIndices", Direction = PortDirection.Output, DataType = typeof(int[]), ColorHex = "#8BC34A" },
                    new PortDef { Name = "LatticeRows", Direction = PortDirection.Output, DataType = typeof(int), ColorHex = "#8BC34A" },
                    new PortDef { Name = "LatticeCols", Direction = PortDirection.Output, DataType = typeof(int), ColorHex = "#CDDC39" },
                    new PortDef { Name = "AxesSwapped", Direction = PortDirection.Output, DataType = typeof(int), ColorHex = "#9E9E9E" },
                    new PortDef { Name = "TwoColumnDeltaU", Direction = PortDirection.Output, DataType = typeof(double), ColorHex = "#FF9800" },
                    new PortDef { Name = "TwoColumnDeltaV", Direction = PortDirection.Output, DataType = typeof(double), ColorHex = "#FF9800" },
                    new PortDef { Name = "TwoColumnDeltaSummary", Direction = PortDirection.Output, DataType = typeof(string), ColorHex = "#FFE082" }
                }
            },
            new OperatorDef
            {
                TypeId = "halcon_chain_strip_orient",
                DisplayName = "HALCON 链向·定向落格",
                Description = "条带第 2 步：沿用引导格网 θ 建 u/v 与列/行中心线（StripContext）；无引导时几何分列估角。",
                Category = "HALCON",
                Params =
                {
                    new OperatorParam { Name = "gridRows", DisplayName = "行数", DefaultValue = "8", Description = LatticeGridMetaHint },
                    new OperatorParam { Name = "gridCols", DisplayName = "列数", DefaultValue = "2", Description = LatticeGridMetaHint },
                    new OperatorParam { Name = "debugLog", DisplayName = "诊断日志", DefaultValue = "auto" }
                },
                Ports =
                {
                    new PortDef { Name = "Row", Direction = PortDirection.Input, DataType = typeof(double[]), ColorHex = "#4CAF50" },
                    new PortDef { Name = "Column", Direction = PortDirection.Input, DataType = typeof(double[]), ColorHex = "#2196F3" },
                    new PortDef { Name = "Angle", Direction = PortDirection.Input, DataType = typeof(double[]), ColorHex = "#FF5722", IsOptional = true },
                    new PortDef { Name = "Score", Direction = PortDirection.Input, DataType = typeof(double[]), ColorHex = "#FFC107", IsOptional = true },
                    new PortDef { Name = "BootstrapPickIndices", Direction = PortDirection.Input, DataType = typeof(int[]), ColorHex = "#8BC34A", IsOptional = true },
                    new PortDef { Name = "ChainBootstrapAngle", Direction = PortDirection.Input, DataType = typeof(double), ColorHex = "#C5E1A5", IsOptional = true },
                    new PortDef { Name = "StripContext", Direction = PortDirection.Output, DataType = typeof(HalconLatticeStripContext), ColorHex = "#795548" },
                    new PortDef { Name = "LatticeAngle", Direction = PortDirection.Output, DataType = typeof(double), ColorHex = "#C5E1A5" },
                    new PortDef { Name = "PcaAxisAngle", Direction = PortDirection.Output, DataType = typeof(double), ColorHex = "#FF9800" },
                    new PortDef { Name = "PitchRow", Direction = PortDirection.Output, DataType = typeof(double), ColorHex = "#8BC34A" },
                    new PortDef { Name = "PitchCol", Direction = PortDirection.Output, DataType = typeof(double), ColorHex = "#CDDC39" },
                    new PortDef { Name = "LatticeRows", Direction = PortDirection.Output, DataType = typeof(int), ColorHex = "#8BC34A" },
                    new PortDef { Name = "LatticeCols", Direction = PortDirection.Output, DataType = typeof(int), ColorHex = "#CDDC39" },
                    new PortDef { Name = "AxesSwapped", Direction = PortDirection.Output, DataType = typeof(int), ColorHex = "#9E9E9E" }
                }
            },
            new OperatorDef
            {
                TypeId = "halcon_chain_strip_pick_fill",
                DisplayName = "HALCON 链向·列0+条带输出",
                Description = "条带第3–4步合并：列0 N 连链选 + 列1落格/同行对齐/共线筛选。须接上游 StripContext。",
                Category = "HALCON",
                Params =
                {
                    new OperatorParam { Name = "debugLog", DisplayName = "诊断日志", DefaultValue = "auto" }
                },
                Ports =
                {
                    new PortDef { Name = "Row", Direction = PortDirection.Input, DataType = typeof(double[]), ColorHex = "#4CAF50" },
                    new PortDef { Name = "Column", Direction = PortDirection.Input, DataType = typeof(double[]), ColorHex = "#2196F3" },
                    new PortDef { Name = "Angle", Direction = PortDirection.Input, DataType = typeof(double[]), ColorHex = "#FF5722", IsOptional = true },
                    new PortDef { Name = "Score", Direction = PortDirection.Input, DataType = typeof(double[]), ColorHex = "#FFC107", IsOptional = true },
                    new PortDef { Name = "StripContext", Direction = PortDirection.Input, DataType = typeof(HalconLatticeStripContext), ColorHex = "#795548" },
                    new PortDef { Name = "StripContext", Direction = PortDirection.Output, DataType = typeof(HalconLatticeStripContext), ColorHex = "#795548" },
                    new PortDef { Name = "Column0PickIndices", Direction = PortDirection.Output, DataType = typeof(int[]), ColorHex = "#8BC34A" },
                    new PortDef { Name = "ChainDirectionAngle", Direction = PortDirection.Output, DataType = typeof(double), ColorHex = "#FF9800" },
                    new PortDef { Name = "ConsensusMatchAngle", Direction = PortDirection.Output, DataType = typeof(double), ColorHex = "#FF5722" },
                    new PortDef { Name = "MatchConcentration", Direction = PortDirection.Output, DataType = typeof(double), ColorHex = "#9E9E9E" },
                    new PortDef { Name = "ConsensusPickIndices", Direction = PortDirection.Output, DataType = typeof(int[]), ColorHex = "#8BC34A" },
                    new PortDef { Name = "GridRow", Direction = PortDirection.Output, DataType = typeof(int[]), ColorHex = "#8BC34A" },
                    new PortDef { Name = "GridCol", Direction = PortDirection.Output, DataType = typeof(int[]), ColorHex = "#CDDC39" },
                    new PortDef { Name = "CollinearRow", Direction = PortDirection.Output, DataType = typeof(double[]), ColorHex = "#4CAF50" },
                    new PortDef { Name = "CollinearColumn", Direction = PortDirection.Output, DataType = typeof(double[]), ColorHex = "#2196F3" }
                }
            },
            new OperatorDef
            {
                TypeId = "halcon_estimate_shape_match_chain",
                DisplayName = "HALCON 链向角估计",
                Description = "一步完成条带筛选（=引导+定向+列0+条带输出）。CollinearRow/Column 与 Find 同坐标。",
                Category = "HALCON",
                Params =
                {
                    new OperatorParam { Name = "gridRows", DisplayName = "行数", DefaultValue = "8", Description = $"阵列行数（2 列×8 行 → 行=8、列=2）；{LatticeGridMetaHint}" },
                    new OperatorParam { Name = "gridCols", DisplayName = "列数", DefaultValue = "2", Description = $"阵列列数；{LatticeGridMetaHint}" },
                    new OperatorParam { Name = "debugLog", DisplayName = "诊断日志", DefaultValue = "auto", Description = "写入 *.grid-filter.log" }
                },
                Ports =
                {
                    new PortDef { Name = "Row", Direction = PortDirection.Input, DataType = typeof(double[]), ColorHex = "#4CAF50" },
                    new PortDef { Name = "Column", Direction = PortDirection.Input, DataType = typeof(double[]), ColorHex = "#2196F3" },
                    new PortDef { Name = "Angle", Direction = PortDirection.Input, DataType = typeof(double[]), ColorHex = "#FF5722", IsOptional = true },
                    new PortDef { Name = "Score", Direction = PortDirection.Input, DataType = typeof(double[]), ColorHex = "#FFC107", IsOptional = true },
                    new PortDef { Name = "ChainDirectionAngle", Direction = PortDirection.Output, DataType = typeof(double), ColorHex = "#FF9800" },
                    new PortDef { Name = "ConsensusMatchAngle", Direction = PortDirection.Output, DataType = typeof(double), ColorHex = "#FF5722" },
                    new PortDef { Name = "MatchConcentration", Direction = PortDirection.Output, DataType = typeof(double), ColorHex = "#9E9E9E" },
                    new PortDef { Name = "ConsensusPickIndices", Direction = PortDirection.Output, DataType = typeof(int[]), ColorHex = "#8BC34A" },
                    new PortDef { Name = "GridRow", Direction = PortDirection.Output, DataType = typeof(int[]), ColorHex = "#8BC34A" },
                    new PortDef { Name = "GridCol", Direction = PortDirection.Output, DataType = typeof(int[]), ColorHex = "#CDDC39" },
                    new PortDef { Name = "CollinearRow", Direction = PortDirection.Output, DataType = typeof(double[]), ColorHex = "#4CAF50" },
                    new PortDef { Name = "CollinearColumn", Direction = PortDirection.Output, DataType = typeof(double[]), ColorHex = "#2196F3" }
                }
            },
            new OperatorDef
            {
                TypeId = "halcon_fit_shape_match_lattice",
                DisplayName = "HALCON 阵列聚类拟合",
                Description = "对匹配点做 u/v 投影与列/行中心线聚类（共识高分点贴合行/列中心）。可接上游「链向角估计」的 ChainDirectionAngle；未连接时在内部估计链向。输出列/行中心与理论格心，可接「阵列过滤匹配」。",
                Category = "HALCON",
                Params =
                {
                    new OperatorParam { Name = "gridRows", DisplayName = "行数", DefaultValue = "3", Description = $"阵列行数（沿副轴 v）；{LatticeGridMetaHint}" },
                    new OperatorParam { Name = "gridCols", DisplayName = "列数", DefaultValue = "3", Description = $"阵列列数（沿主轴 u）；{LatticeGridMetaHint}" },
                    new OperatorParam { Name = "pitchRow", DisplayName = "行间距(px)", DefaultValue = "0", Description = "0=自动估计" },
                    new OperatorParam { Name = "pitchCol", DisplayName = "列间距(px)", DefaultValue = "0", Description = "0=自动估计" },
                    new OperatorParam { Name = "gridAngleDeg", DisplayName = "阵列角度(°)", DefaultValue = "auto", Description = "auto=模板角聚类+PCA 消歧；或固定角度" },
                    new OperatorParam { Name = "snapTolerancePx", DisplayName = "格点容差(px)", DefaultValue = "0", Description = "0=自动(约 0.35×min间距)，供下游过滤落格" },
                    new OperatorParam { Name = "debugLog", DisplayName = "诊断日志", DefaultValue = "auto", Description = "同阵列过滤算子；写入 *.grid-filter.log" }
                },
                Ports =
                {
                    new PortDef { Name = "Row", Direction = PortDirection.Input, DataType = typeof(double[]), ColorHex = "#4CAF50" },
                    new PortDef { Name = "Column", Direction = PortDirection.Input, DataType = typeof(double[]), ColorHex = "#2196F3" },
                    new PortDef { Name = "Angle", Direction = PortDirection.Input, DataType = typeof(double[]), ColorHex = "#FF5722", IsOptional = true },
                    new PortDef { Name = "Score", Direction = PortDirection.Input, DataType = typeof(double[]), ColorHex = "#FFC107", IsOptional = true },
                    new PortDef { Name = "LatticeAngle", Direction = PortDirection.Input, DataType = typeof(double), ColorHex = "#C5E1A5", IsOptional = true },
                    new PortDef { Name = "ChainDirectionAngle", Direction = PortDirection.Input, DataType = typeof(double), ColorHex = "#FF9800", IsOptional = true },
                    new PortDef { Name = "ConsensusMatchAngle", Direction = PortDirection.Input, DataType = typeof(double), ColorHex = "#FF5722", IsOptional = true },
                    new PortDef { Name = "ConsensusPickIndices", Direction = PortDirection.Input, DataType = typeof(int[]), ColorHex = "#8BC34A", IsOptional = true },
                    new PortDef { Name = "ColCenterU", Direction = PortDirection.Output, DataType = typeof(double[]), ColorHex = "#2196F3" },
                    new PortDef { Name = "RowCenterV", Direction = PortDirection.Output, DataType = typeof(double[]), ColorHex = "#4CAF50" },
                    new PortDef { Name = "LatticeAngle", Direction = PortDirection.Output, DataType = typeof(double), ColorHex = "#C5E1A5" },
                    new PortDef { Name = "ConsensusMatchAngle", Direction = PortDirection.Output, DataType = typeof(double), ColorHex = "#FF5722" },
                    new PortDef { Name = "ChainDirectionAngle", Direction = PortDirection.Output, DataType = typeof(double), ColorHex = "#FF9800" },
                    new PortDef { Name = "PitchRow", Direction = PortDirection.Output, DataType = typeof(double), ColorHex = "#AED581" },
                    new PortDef { Name = "PitchCol", Direction = PortDirection.Output, DataType = typeof(double), ColorHex = "#DCE775" },
                    new PortDef { Name = "AxesSwapped", Direction = PortDirection.Output, DataType = typeof(int), ColorHex = "#9E9E9E" },
                    new PortDef { Name = "SnapU", Direction = PortDirection.Output, DataType = typeof(double), ColorHex = "#B0BEC5" },
                    new PortDef { Name = "SnapV", Direction = PortDirection.Output, DataType = typeof(double), ColorHex = "#B0BEC5" },
                    new PortDef { Name = "LatticeRows", Direction = PortDirection.Output, DataType = typeof(int), ColorHex = "#8BC34A" },
                    new PortDef { Name = "LatticeCols", Direction = PortDirection.Output, DataType = typeof(int), ColorHex = "#CDDC39" },
                    new PortDef { Name = "CellRow", Direction = PortDirection.Output, DataType = typeof(double[]), ColorHex = "#AED581" },
                    new PortDef { Name = "CellCol", Direction = PortDirection.Output, DataType = typeof(double[]), ColorHex = "#DCE775" },
                    new PortDef { Name = "CellFound", Direction = PortDirection.Output, DataType = typeof(bool[]), ColorHex = "#C5E1A5" },
                    new PortDef { Name = "CellAngle", Direction = PortDirection.Output, DataType = typeof(double[]), ColorHex = "#C5E1A5" },
                    new PortDef { Name = "PointGridRow", Direction = PortDirection.Output, DataType = typeof(int[]), ColorHex = "#8BC34A" },
                    new PortDef { Name = "PointGridCol", Direction = PortDirection.Output, DataType = typeof(int[]), ColorHex = "#CDDC39" }
                }
            },
            new OperatorDef
            {
                TypeId = "halcon_filter_shape_match_grid",
                DisplayName = "HALCON 阵列过滤匹配",
                Description = "在阵列聚类结果上落格、每格选最优匹配并过滤。可连接「阵列聚类拟合」输出，或本算子内嵌聚类（与旧流程兼容）。",
                Category = "HALCON",
                Params =
                {
                    new OperatorParam { Name = "gridRows", DisplayName = "行数", DefaultValue = "3", Description = $"阵列行数（沿副轴 v）；{LatticeGridMetaHint}" },
                    new OperatorParam { Name = "gridCols", DisplayName = "列数", DefaultValue = "3", Description = $"阵列列数（沿主轴 u）；{LatticeGridMetaHint}" },
                    new OperatorParam { Name = "pitchRow", DisplayName = "行间距(px)", DefaultValue = "0", Description = "0=自动估计（沿 v / 图像行方向间距）" },
                    new OperatorParam { Name = "pitchCol", DisplayName = "列间距(px)", DefaultValue = "0", Description = "0=自动估计（沿 u / 图像列方向间距）" },
                    new OperatorParam { Name = "gridAngleDeg", DisplayName = "阵列角度(°)", DefaultValue = "auto", Description = "auto=匹配角+PCA，并自动消歧 ±90°/行列对调；也可填固定角度(仍会自动试行列对调)" },
                    new OperatorParam { Name = "snapTolerancePx", DisplayName = "格点容差(px)", DefaultValue = "0", Description = "0=自动(约 0.35×min间距)" },
                    new OperatorParam { Name = "pitchToleranceRatio", DisplayName = "间距容差比", DefaultValue = "0.2", Description = "邻格一致性检查时的间距相对容差" },
                    new OperatorParam { Name = "minNeighborVotes", DisplayName = "最少邻格票", DefaultValue = "0", Description = "0=关闭；≥1 抑制孤立误检(可能增加漏检)" },
                    new OperatorParam { Name = "minScoreKeep", DisplayName = "最低得分", DefaultValue = "0", Description = "过滤后保留的最低 Score；误检多时可设 0.45~0.55" },
                    new OperatorParam { Name = "maxAngleDeviationDeg", DisplayName = "最大角度偏差°", DefaultValue = "0", Description = "0=关闭；相对模板匹配角的偏差上限。整板旋转时各点角相近可设 10~15°；对称模板或角度乱跳时请设 0" },
                    new OperatorParam { Name = "debugLog", DisplayName = "诊断日志", DefaultValue = "auto", Description = "auto=后台跑flow或设环境变量XV_GRID_FILTER_LOG时写日志；true=强制；false=关闭。日志见 flow同目录/*.grid-filter.log" }
                },
                Ports =
                {
                    new PortDef { Name = "Row", Direction = PortDirection.Input, DataType = typeof(double[]), ColorHex = "#4CAF50" },
                    new PortDef { Name = "Column", Direction = PortDirection.Input, DataType = typeof(double[]), ColorHex = "#2196F3" },
                    new PortDef { Name = "Angle", Direction = PortDirection.Input, DataType = typeof(double[]), ColorHex = "#FF5722", IsOptional = true },
                    new PortDef { Name = "Score", Direction = PortDirection.Input, DataType = typeof(double[]), ColorHex = "#FFC107", IsOptional = true },
                    new PortDef { Name = "ColCenterU", Direction = PortDirection.Input, DataType = typeof(double[]), ColorHex = "#2196F3", IsOptional = true },
                    new PortDef { Name = "RowCenterV", Direction = PortDirection.Input, DataType = typeof(double[]), ColorHex = "#4CAF50", IsOptional = true },
                    new PortDef { Name = "LatticeAngle", Direction = PortDirection.Input, DataType = typeof(double), ColorHex = "#C5E1A5", IsOptional = true },
                    new PortDef { Name = "ChainDirectionAngle", Direction = PortDirection.Input, DataType = typeof(double), ColorHex = "#FF9800", IsOptional = true },
                    new PortDef { Name = "ConsensusMatchAngle", Direction = PortDirection.Input, DataType = typeof(double), ColorHex = "#FF5722", IsOptional = true },
                    new PortDef { Name = "PitchRow", Direction = PortDirection.Input, DataType = typeof(double), ColorHex = "#AED581", IsOptional = true },
                    new PortDef { Name = "PitchCol", Direction = PortDirection.Input, DataType = typeof(double), ColorHex = "#DCE775", IsOptional = true },
                    new PortDef { Name = "AxesSwapped", Direction = PortDirection.Input, DataType = typeof(int), ColorHex = "#9E9E9E", IsOptional = true },
                    new PortDef { Name = "SnapU", Direction = PortDirection.Input, DataType = typeof(double), ColorHex = "#B0BEC5", IsOptional = true },
                    new PortDef { Name = "SnapV", Direction = PortDirection.Input, DataType = typeof(double), ColorHex = "#B0BEC5", IsOptional = true },
                    new PortDef { Name = "Row", Direction = PortDirection.Output, DataType = typeof(double[]), ColorHex = "#4CAF50" },
                    new PortDef { Name = "Column", Direction = PortDirection.Output, DataType = typeof(double[]), ColorHex = "#2196F3" },
                    new PortDef { Name = "Angle", Direction = PortDirection.Output, DataType = typeof(double[]), ColorHex = "#FF5722" },
                    new PortDef { Name = "Score", Direction = PortDirection.Output, DataType = typeof(double[]), ColorHex = "#FFC107" },
                    new PortDef { Name = "GridRow", Direction = PortDirection.Output, DataType = typeof(int[]), ColorHex = "#8BC34A" },
                    new PortDef { Name = "GridCol", Direction = PortDirection.Output, DataType = typeof(int[]), ColorHex = "#CDDC39" },
                    new PortDef { Name = "LatticeRows", Direction = PortDirection.Output, DataType = typeof(int), ColorHex = "#8BC34A" },
                    new PortDef { Name = "LatticeCols", Direction = PortDirection.Output, DataType = typeof(int), ColorHex = "#CDDC39" },
                    new PortDef { Name = "CellRow", Direction = PortDirection.Output, DataType = typeof(double[]), ColorHex = "#AED581" },
                    new PortDef { Name = "CellCol", Direction = PortDirection.Output, DataType = typeof(double[]), ColorHex = "#DCE775" },
                    new PortDef { Name = "CellFound", Direction = PortDirection.Output, DataType = typeof(bool[]), ColorHex = "#C5E1A5" },
                    new PortDef { Name = "PitchRow", Direction = PortDirection.Output, DataType = typeof(double), ColorHex = "#AED581" },
                    new PortDef { Name = "PitchCol", Direction = PortDirection.Output, DataType = typeof(double), ColorHex = "#DCE775" },
                    new PortDef { Name = "LatticeAngle", Direction = PortDirection.Output, DataType = typeof(double), ColorHex = "#C5E1A5" },
                    new PortDef { Name = "ChainDirectionAngle", Direction = PortDirection.Output, DataType = typeof(double), ColorHex = "#FF9800" },
                    new PortDef { Name = "ColCenterU", Direction = PortDirection.Output, DataType = typeof(double[]), ColorHex = "#2196F3" },
                    new PortDef { Name = "RowCenterV", Direction = PortDirection.Output, DataType = typeof(double[]), ColorHex = "#4CAF50" },
                    new PortDef { Name = "CellAngle", Direction = PortDirection.Output, DataType = typeof(double[]), ColorHex = "#C5E1A5" }
                }
            },
            new OperatorDef
            {
                TypeId = "halcon_mask_image_by_shape_match",
                DisplayName = "HALCON 形状匹配区域 Mask",
                Description = "输入原图 + FindShapeModel 结果 + ModelId：由模板轮廓变换得到填充区域，生成 Mask 并对原图掩膜。可保留或挖空匹配区域；可限制参与合并的匹配个数。",
                Category = "HALCON",
                Params =
                {
                    new OperatorParam { Name = "contourLevel", DisplayName = "轮廓层", DefaultValue = "1", Description = "GetShapeModelContours 金字塔层" },
                    new OperatorParam { Name = "insetPx", DisplayName = "区域内缩(px)", DefaultValue = "0", Description = "生成区域前腐蚀半径，缩小有效区" },
                    new OperatorParam
                    {
                        Name = "maskRegionMode",
                        DisplayName = "Mask区域处理",
                        DefaultValue = "保留mask区域",
                        Description = "保留=匹配区域内保留原图、区域外置0；去掉=挖空匹配区域、区域外保留",
                        Options = new List<string> { "保留mask区域", "去掉mask区域", "keep", "remove" }
                    },
                    new OperatorParam
                    {
                        Name = "maxMatchCount",
                        DisplayName = "参与匹配数",
                        DefaultValue = "0",
                        Description = "用于生成 Mask 的 Find 匹配个数：0=全部；N=仅取 Row/Column 前 N 个（与 Find 输出顺序一致）"
                    },
                    new OperatorParam { Name = "maskMin", DisplayName = "Mask有效下界", DefaultValue = "1", Description = "Mask 灰度在此区间内视为有效区域" },
                    new OperatorParam { Name = "maskMax", DisplayName = "Mask有效上界", DefaultValue = "255", Description = "Mask 灰度在此区间内视为有效区域" },
                    new OperatorParam { Name = "preserveColor", DisplayName = "保留彩色", DefaultValue = "true", Description = "true=原图为 BGR 时输出彩色；false=转灰度后掩膜" }
                },
                Ports =
                {
                    new PortDef { Name = "Image", Direction = PortDirection.Input, DataType = typeof(CalibImage), ColorHex = "#FF9800" },
                    new PortDef { Name = "Row", Direction = PortDirection.Input, DataType = typeof(double[]), ColorHex = "#4CAF50" },
                    new PortDef { Name = "Column", Direction = PortDirection.Input, DataType = typeof(double[]), ColorHex = "#2196F3" },
                    new PortDef { Name = "Angle", Direction = PortDirection.Input, DataType = typeof(double[]), ColorHex = "#FF5722", IsOptional = true },
                    new PortDef { Name = "ModelId", Direction = PortDirection.Input, DataType = typeof(long), ColorHex = "#9C27B0" },
                    new PortDef { Name = "Out", Direction = PortDirection.Output, DataType = typeof(CalibImage), ColorHex = "#FF9800" },
                    new PortDef { Name = "Mask", Direction = PortDirection.Output, DataType = typeof(CalibImage), ColorHex = "#FFB74D", IsOptional = true }
                }
            },
            new OperatorDef
            {
                TypeId = "halcon_filter_shape_match_inside_region",
                DisplayName = "HALCON 区域内匹配过滤",
                Description = "两路 FindShapeModel：用「区域匹配」的模板轮廓（变换到图像）作区域，过滤「待选匹配」中中心落在区域内、与区域边界及彼此不相碰的实例。须接两路 ModelId 与对应 Row/Column/Angle。",
                Category = "HALCON",
                Params =
                {
                    new OperatorParam { Name = "contourLevel", DisplayName = "轮廓层", DefaultValue = "1", Description = "GetShapeModelContours 金字塔层" },
                    new OperatorParam { Name = "insetPx", DisplayName = "区域内缩(px)", DefaultValue = "2", Description = "匹配中心须距区域轮廓边界≥该值，避免贴边" },
                    new OperatorParam { Name = "minSeparationPx", DisplayName = "最小间距(px)", DefaultValue = "0", Description = "0=仅区域内过滤，不做互斥；>0 时在同一区域实例内按得分贪心保留，中心间距≥该值" }
                },
                Ports =
                {
                    new PortDef { Name = "RegionRow", Direction = PortDirection.Input, DataType = typeof(double[]), ColorHex = "#4CAF50" },
                    new PortDef { Name = "RegionColumn", Direction = PortDirection.Input, DataType = typeof(double[]), ColorHex = "#2196F3" },
                    new PortDef { Name = "RegionAngle", Direction = PortDirection.Input, DataType = typeof(double[]), ColorHex = "#FF5722", IsOptional = true },
                    new PortDef { Name = "RegionModelId", Direction = PortDirection.Input, DataType = typeof(long), ColorHex = "#9C27B0" },
                    new PortDef { Name = "Row", Direction = PortDirection.Input, DataType = typeof(double[]), ColorHex = "#4CAF50" },
                    new PortDef { Name = "Column", Direction = PortDirection.Input, DataType = typeof(double[]), ColorHex = "#2196F3" },
                    new PortDef { Name = "Angle", Direction = PortDirection.Input, DataType = typeof(double[]), ColorHex = "#FF5722", IsOptional = true },
                    new PortDef { Name = "Score", Direction = PortDirection.Input, DataType = typeof(double[]), ColorHex = "#FFC107", IsOptional = true },
                    new PortDef { Name = "ModelId", Direction = PortDirection.Input, DataType = typeof(long), ColorHex = "#9C27B0" },
                    new PortDef { Name = "Row", Direction = PortDirection.Output, DataType = typeof(double[]), ColorHex = "#4CAF50" },
                    new PortDef { Name = "Column", Direction = PortDirection.Output, DataType = typeof(double[]), ColorHex = "#2196F3" },
                    new PortDef { Name = "Angle", Direction = PortDirection.Output, DataType = typeof(double[]), ColorHex = "#FF5722" },
                    new PortDef { Name = "Score", Direction = PortDirection.Output, DataType = typeof(double[]), ColorHex = "#FFC107" },
                    new PortDef { Name = "KeptIndices", Direction = PortDirection.Output, DataType = typeof(int[]), ColorHex = "#8BC34A", IsOptional = true }
                }
            },
            new OperatorDef
            {
                TypeId = "halcon_display_shape_match",
                DisplayName = "HALCON 显示形状匹配结果",
                Description = "叠加 FindShapeModel/FindScaledShapeModel 模板轮廓、十字与得分。可选接 Scale 显示缩放轮廓；可选接 ConsensusPickIndices（与 Row/Column 同源，一般为 Find 输出）仅显示格点筛选保留的匹配。",
                Category = "HALCON",
                Params =
                {
                    new OperatorParam { Name = "contourLevel", DisplayName = "ContourLevel", DefaultValue = "1", Description = "GetShapeModelContours 金字塔层" },
                    new OperatorParam { Name = "crossHalf", DisplayName = "十字半长(px)", DefaultValue = "14", Description = "匹配中心十字线半长" },
                    new OperatorParam { Name = "strokeWidth", DisplayName = "线宽", DefaultValue = "2.5", Description = "轮廓与十字笔画宽度" },
                    new OperatorParam { Name = "drawScores", DisplayName = "显示得分", DefaultValue = "true", Description = "true/false" }
                },
                Ports =
                {
                    new PortDef { Name = "In", Direction = PortDirection.Input, DataType = typeof(CalibImage), ColorHex = "#FF9800" },
                    new PortDef { Name = "ModelId", Direction = PortDirection.Input, DataType = typeof(long), ColorHex = "#9C27B0" },
                    new PortDef { Name = "Row", Direction = PortDirection.Input, DataType = typeof(double[]), ColorHex = "#4CAF50" },
                    new PortDef { Name = "Column", Direction = PortDirection.Input, DataType = typeof(double[]), ColorHex = "#2196F3" },
                    new PortDef { Name = "Angle", Direction = PortDirection.Input, DataType = typeof(double[]), ColorHex = "#FF5722", IsOptional = true },
                    new PortDef { Name = "Scale", Direction = PortDirection.Input, DataType = typeof(double[]), ColorHex = "#26A69A", IsOptional = true },
                    new PortDef { Name = "Score", Direction = PortDirection.Input, DataType = typeof(double[]), ColorHex = "#FFC107", IsOptional = true },
                    new PortDef { Name = "ConsensusPickIndices", Direction = PortDirection.Input, DataType = typeof(int[]), ColorHex = "#8BC34A", IsOptional = true },
                    new PortDef { Name = "GridCol", Direction = PortDirection.Input, DataType = typeof(int[]), ColorHex = "#CDDC39", IsOptional = true },
                    new PortDef { Name = "CollinearRow", Direction = PortDirection.Input, DataType = typeof(double[]), ColorHex = "#4CAF50", IsOptional = true },
                    new PortDef { Name = "CollinearColumn", Direction = PortDirection.Input, DataType = typeof(double[]), ColorHex = "#2196F3", IsOptional = true },
                    new PortDef { Name = "Out", Direction = PortDirection.Output, DataType = typeof(CalibImage), ColorHex = "#FF9800" }
                }
            },
            new OperatorDef
            {
                TypeId = "composite",
                DisplayName = "组合算子",
                Description = "嵌入子流程(.flow.json)：innerFlowPath 或 innerFlowJson。bindingsJson 可选；子流程中可用「组合绑定入/出」经连线绑定父端口，此时未绑定的输入/输出不再自动处理",
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
                        Name = "inputPorts",
                        DisplayName = "输入端口列表",
                        DefaultValue = "In",
                        Description = "可选；组合算子输入端口名列表，逗号/分号/换行分隔，如 In,In2,Trigger"
                    },
                    new OperatorParam
                    {
                        Name = "outputPorts",
                        DisplayName = "输出端口列表",
                        DefaultValue = "Out,Out2",
                        Description = "可选；组合算子输出端口名列表，逗号/分号/换行分隔，如 Out,Out2,Debug"
                    },
                    new OperatorParam
                    {
                        Name = "innerFlowPath",
                        DisplayName = "子流程文件",
                        DefaultValue = "",
                        Description = "可选；*.flow.json；相对路径相对于当前 flow.json 所在目录；嵌套子流程内则相对于该层子流程文件所在目录。若填空则用 innerFlowJson"
                    },
                    new OperatorParam
                    {
                        Name = "innerFlowJson",
                        DisplayName = "子流程JSON",
                        DefaultValue = "",
                        Description = "可选；与 innerFlowPath 二选一；内容为与保存流程相同的 JSON（nodes+connections）"
                    },
                    new OperatorParam
                    {
                        Name = "bindingsJson",
                        DisplayName = "端口绑定JSON",
                        DefaultValue = "{}",
                        Description = "可选。inputs/outputs 数组：external=组合算子端口，nodeId+port=子节点。可留空 {}：未声明的输入按端口同名或 In→子图源节点 In/Image/Img 自动绑定；未声明的输出按子图末端未连线输出映射到 Out、Out2。子图中使用「组合绑定入/出」并通过连线绑定时，存在绑定入则关闭输入自动补全，存在绑定出则关闭输出自动映射（仍可与 JSON 混用）"
                    }
                }
            },
            new OperatorDef
            {
                TypeId = "composite_bind_in",
                DisplayName = "组合绑定入",
                Description = "仅用于「组合算子」子流程：填写父组合输入端口名，将 Out 连到子算子输入以绑定数据来源。子图中存在本算子时关闭未绑定输入的自动补全",
                Category = "流程",
                DefaultWidth = 200,
                DefaultHeight = 76,
                Params =
                {
                    new OperatorParam
                    {
                        Name = "externalPort",
                        DisplayName = "父组合输入端口名",
                        DefaultValue = "In",
                        Description = "与父组合算子上的输入端口名一致，如 In"
                    }
                },
                Ports =
                {
                    new PortDef { Name = "Out", Direction = PortDirection.Output, DataType = typeof(object), ColorHex = "#607D8B" }
                }
            },
            new OperatorDef
            {
                TypeId = "composite_bind_out",
                DisplayName = "组合绑定出",
                Description = "仅用于「组合算子」子流程：将上游输出连到 In，并填写父组合输出端口名。子图中存在本算子时关闭未绑定输出的自动映射",
                Category = "流程",
                DefaultWidth = 200,
                DefaultHeight = 76,
                Params =
                {
                    new OperatorParam
                    {
                        Name = "externalPort",
                        DisplayName = "父组合输出端口名",
                        DefaultValue = "Out",
                        Description = "与父组合算子上的输出端口名一致，如 Out、Out2"
                    }
                },
                Ports =
                {
                    new PortDef { Name = "In", Direction = PortDirection.Input, DataType = typeof(object), ColorHex = "#607D8B" },
                    new PortDef { Name = "Out", Direction = PortDirection.Output, DataType = typeof(object), ColorHex = "#607D8B" }
                }
            },
        };
    }

    /// <summary>内参+透视矫正参数，用于「标定图像矫正」算子。</summary>
    internal static class FlowCameraCorrectionOperatorParams
    {
        public static readonly FlowPage.OperatorParam[] Core =
        {
            new FlowPage.OperatorParam
            {
                Name = "enableUndistort",
                DisplayName = "内参畸变矫正",
                DefaultValue = "false",
                Description = "对 Image 做 cv::undistort；需 CalibrationJson 端口或 calibrationJsonFile 参数",
                Options = new List<string> { "false", "true" }
            },
            new FlowPage.OperatorParam
            {
                Name = "enablePerspective",
                DisplayName = "透视展开",
                DefaultValue = "false",
                Description = "取图后再做棋盘平面鸟瞰 warp；须完整标定 JSON（含 extrinsicsPerView）",
                Options = new List<string> { "false", "true" }
            },
            new FlowPage.OperatorParam
            {
                Name = "calibrationJsonFile",
                DisplayName = "标定 JSON 文件",
                DefaultValue = "",
                Description = "可选；未接 CalibrationJson 端口时使用；须为完整 CalibrationJson"
            },
            new FlowPage.OperatorParam { Name = "undistortAlpha", DisplayName = "去畸变裁剪α", DefaultValue = "-1", Description = "-1=保持原尺寸；0..1 裁剪黑边" },
            new FlowPage.OperatorParam { Name = "viewIndex", DisplayName = "外参视图序号", DefaultValue = "0", Description = "透视用 extrinsicsPerView 下标" },
            new FlowPage.OperatorParam { Name = "cols", DisplayName = "内侧列角点数", DefaultValue = "9", Description = "与棋盘标定一致" },
            new FlowPage.OperatorParam { Name = "rows", DisplayName = "内侧行角点数", DefaultValue = "6", Description = "与棋盘标定一致" },
            new FlowPage.OperatorParam { Name = "squareSizeMm", DisplayName = "方格边长(mm)", DefaultValue = "25", Description = "与标定一致" },
            new FlowPage.OperatorParam { Name = "pxPerMm", DisplayName = "mm/像素", DefaultValue = "1", Description = "透视输出缩放，1≈1像素1mm" },
            new FlowPage.OperatorParam
            {
                Name = "perspectiveOutputFrame",
                DisplayName = "透视输出范围",
                DefaultValue = "board",
                Description = "board=仅标定板；local=原图仅板内；plane=整图共面鸟瞰(原图尺寸、均匀缩放，须共面)",
                Options = new List<string> { "board", "local", "plane" }
            },
            new FlowPage.OperatorParam
            {
                Name = "perspectiveOutputScale",
                DisplayName = "输出尺度",
                DefaultValue = "metric",
                Description = "metric=固定物理尺寸，各角度一致；board_pixels=按图中棋盘边长（随距离/倾角变化）",
                Options = new List<string> { "metric", "board_pixels" }
            },
            new FlowPage.OperatorParam
            {
                Name = "assumeUndistorted",
                DisplayName = "输入已去畸变",
                DefaultValue = "auto",
                Description = "透视角点投影：auto=与同节点「内参畸变矫正」一致；true/false 手动",
                Options = new List<string> { "auto", "true", "false" }
            }
        };

        public static List<FlowPage.OperatorParam> With(params FlowPage.OperatorParam[] leading)
        {
            var list = new List<FlowPage.OperatorParam>(leading.Length + Core.Length);
            list.AddRange(leading);
            list.AddRange(Core);
            return list;
        }

        public static List<FlowPage.OperatorParam> CoreOnly() => new List<FlowPage.OperatorParam>(Core);
    }
}
