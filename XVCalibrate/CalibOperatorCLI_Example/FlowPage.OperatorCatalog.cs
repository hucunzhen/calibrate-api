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
                        Description = "托管执行：相对 exe 目录；Native：建议绝对路径（与 load_image 一致）。留空时在界面线程弹出选文件夹"
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
                    new OperatorParam { Name = "jitRepoRoot", DisplayName = "JiT仓库根目录", DefaultValue = "just-image-transformer", Description = "克隆的 just-image-transformer 根路径（相对 exe 或源码树）" },
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
                    new OperatorParam { Name = "scriptPath", DisplayName = "脚本路径", DefaultValue = "DIP_Inference/dip_denoise.py", Description = "相对仓库根或 exe 目录；解析规则同 ONNX 模型路径" },
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
                    new OperatorParam { Name = "scriptPath", DisplayName = "脚本路径", DefaultValue = "Swin_Inference/swin_infer.py", Description = "相对仓库根或 exe；解析规则同 ONNX" },
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
                    new OperatorParam { Name = "scriptPath", DisplayName = "脚本路径", DefaultValue = "YoloSeg_Tools/predict_seg.py", Description = "相对仓库根或 exe" },
                    new OperatorParam
                    {
                        Name = "weightsPath",
                        DisplayName = "权重 .pt",
                        DefaultValue = "yolo_data/runs/segment/train-2/weights/best.pt",
                        Description = "best.pt 或 last.pt；相对路径从 exe 向上查找仓库根"
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
                Description = "每个画布上的「显示图像」节点独占一个预览窗口（标题含短 Guid）；同一节点多次运行会刷新该窗口。可选端口 Xld：叠加 HALCON XLD 折线（橘色）。右键连线看图仍共用单个快捷预览窗口。",
                Category = "可视化",
                Params =
                {
                    new OperatorParam { Name = "dotRadius", DisplayName = "点位半径", DefaultValue = "3", Description = "叠加点位的圆点半径 (像素)" }
                },
                Ports =
                {
                    new PortDef { Name = "Img", Direction = PortDirection.Input, DataType = typeof(CalibImage), ColorHex = "#4CAF50" },
                    new PortDef { Name = "Image", Direction = PortDirection.Input, DataType = typeof(CalibImage), ColorHex = "#4CAF50" },
                    new PortDef { Name = "Points", Direction = PortDirection.Input, DataType = typeof(Point2D[]), ColorHex = "#2196F3" },
                    new PortDef { Name = "Xld", Direction = PortDirection.Input, DataType = typeof(HalconXldContourBundle), ColorHex = "#E65100" }
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
                        Description = "绝对路径或相对可执行目录"
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
                Description = "对有序采样点列做 Douglas–Peucker 多边形近似：在最大偏差 ε（像素）内用更少顶点保持形状。closed=true 时视为闭合轮廓（如沿边界等弧长采样）；false 时为开折线。若 In 为 0 个点则跳过（Out 输出空点列，不报错）。",
                Category = "预处理",
                Params =
                {
                    new OperatorParam { Name = "epsilon", DisplayName = "偏差阈值 ε", DefaultValue = "2.0", Description = "像素；越大顶点越少、形状越粗糙" },
                    new OperatorParam { Name = "closed", DisplayName = "闭合轮廓", DefaultValue = "true", Description = "true=首尾闭合；false=开折线" }
                },
                Ports =
                {
                    new PortDef { Name = "In", Direction = PortDirection.Input, DataType = typeof(Point2D[]), ColorHex = "#2196F3" },
                    new PortDef { Name = "Out", Direction = PortDirection.Output, DataType = typeof(Point2D[]), ColorHex = "#2196F3" }
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
                Description = "使用单应矩阵进行像素->世界坐标转换。0 个点时跳过（空 World）。",
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
                Description = "将 CalibrationJson（棋盘多视图）、Affine / Homography / Poly2D、Intrinsics 以 JSON 落盘（可同时写入多项）。至少连接一路输入。filePath 为空或未配置绝对路径时相对程序目录。",
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
                Description = "从 JSON 文件恢复标定数据（由「保存标定结果」生成）。仅连接需要的输出端口；若某输出已连线但文件中无对应数据将报错。",
                Category = "标定",
                Params =
                {
                    new OperatorParam { Name = "filePath", DisplayName = "文件路径", DefaultValue = "", Description = "可选；填写后自动读取（相对路径相对程序目录）；留空则弹窗选择" }
                },
                Ports =
                {
                    new PortDef { Name = "CalibrationJson", Direction = PortDirection.Output, DataType = typeof(string), ColorHex = "#607D8B" },
                    new PortDef { Name = "Transform", Direction = PortDirection.Output, DataType = typeof(AffineTransform), ColorHex = "#E91E63" },
                    new PortDef { Name = "H", Direction = PortDirection.Output, DataType = typeof(HomographyTransform), ColorHex = "#E91E63" },
                    new PortDef { Name = "Poly", Direction = PortDirection.Output, DataType = typeof(Poly2DTransform), ColorHex = "#E91E63" },
                    new PortDef { Name = "Intrinsics", Direction = PortDirection.Output, DataType = typeof(CameraIntrinsics), ColorHex = "#E91E63" }
                }
            },
            new OperatorDef
            {
                TypeId = "plc_connect",
                DisplayName = "PLC连接",
                Description = "连接 PLC (Modbus TCP)",
                Category = "输出",
                Params =
                {
                    new OperatorParam { Name = "ip", DisplayName = "IP", DefaultValue = "192.168.6.6", Description = "PLC IP 地址" },
                    new OperatorParam { Name = "port", DisplayName = "端口", DefaultValue = "502", Description = "Modbus TCP 端口" },
                    new OperatorParam { Name = "station", DisplayName = "站号", DefaultValue = "1", Description = "站号 (1~247)" }
                },
                Ports =
                {
                    new PortDef { Name = "Connected", Direction = PortDirection.Output, DataType = typeof(bool), ColorHex = "#607D8B" }
                }
            },
            new OperatorDef
            {
                TypeId = "plc_disconnect",
                DisplayName = "PLC断开",
                Description = "断开 PLC 连接",
                Category = "输出",
                Ports =
                {
                    new PortDef { Name = "Disconnected", Direction = PortDirection.Output, DataType = typeof(bool), ColorHex = "#607D8B" }
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
                    new OperatorParam { Name = "encoderPath", DisplayName = "Encoder ONNX", DefaultValue = SamOnnxSegmentation.DefaultEncoderRepoRelative, Description = "相对源码树 models/onnx（或 exe 目录）；不存在时自动向上查找仓库根；可为绝对路径" },
                    new OperatorParam { Name = "decoderPath", DisplayName = "Decoder ONNX", DefaultValue = SamOnnxSegmentation.DefaultDecoderRepoRelative, Description = "相对源码树 models/onnx（或 exe 目录）；不存在时自动向上查找仓库根；可为绝对路径" },
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
                Description = "由多张棋盘格图像通过 OpenCV calibrateCamera 最小化重投影误差，同时优化求解：相机内参 fx/fy/cx/cy、畸变系数，以及每张成功视图的外参 rvec/tvec（棋盘坐标系→相机坐标系）。输出均为标定计算结果。至少需 3 张成功检出棋盘的视图。完整结果见 CalibrationJson；内参/外参如何用见 test_images/intrinsics_extrinsics_usage.txt 与 chessboard_intrinsics_extrinsics_usage.flow.json。",
                Category = "标定",
                Params =
                {
                    new OperatorParam { Name = "imagePaths", DisplayName = "图像路径列表", DefaultValue = "", Description = "分号分隔；相对路径相对程序目录；用于标定求解（bmp/png 等 OpenCV 可读格式）" },
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
                Description = "Threshold → Connection → GenContourRegionXld；输出独立数据结构 HalconXldContourBundle（不接原生 find_contours）",
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
                Description = "GenContourPolygonXld + SegmentContoursXld，将轮廓拆成直线/圆弧段（输出仍为 HalconXldContourBundle）",
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
                Description = "沿 XLD 折线按间距采样为 Point2D[]（对应 sample 思路，独立数据结构）",
                Category = "HALCON",
                Params =
                {
                    new OperatorParam { Name = "spacing", DisplayName = "间距", DefaultValue = "4", Description = "沿轮廓弧长采样步长（像素）" },
                    new OperatorParam { Name = "maxBars", DisplayName = "最多条数", DefaultValue = "16", Description = "按轮廓长度降序只取前 N 条；0 表示不限制" }
                },
                Ports =
                {
                    new PortDef { Name = "Xld", Direction = PortDirection.Input, DataType = typeof(HalconXldContourBundle), ColorHex = "#E65100" },
                    new PortDef { Name = "Points", Direction = PortDirection.Output, DataType = typeof(Point2D[]), ColorHex = "#2196F3" }
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
                        Description = "可选；与 innerFlowPath 二选一；内容为与保存组态相同的 JSON（nodes+connections）"
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
}
