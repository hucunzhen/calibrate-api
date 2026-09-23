using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace CalibOperatorCLI_Example
{
    public partial class ChangeoverWizardDialog : Window
    {
        private static readonly string[] StageTitles =
        {
            "阶段 0：环境与文件准备",
            "阶段 1：棋盘格内参标定",
            "阶段 2：九点世界坐标（中心 + 步距）",
            "阶段 3：九点像素标定",
            "阶段 4：形状模板制作",
            "阶段 5：主流程联调",
            "阶段 6：验收与上线",
        };

        private static readonly string[] StageDetails =
        {
            """
            目标：标定和量产用同一套光学条件、同一套目录，避免后面阶段文件对不上。

            现场硬件
            · 相机固定，焦距和光圈锁定。标定与量产必须是同一光学条件。
            · 把光源亮度、曝光、增益记到成像参数表。之后改了这些，要重标或至少复验。
            · PLC 地址、GVAR 布局与 send_plc 参数一致。

            物理件与目录
            · 棋盘规格与流程里的 cols / rows / squareSizeMm 完全一致。
            · 九点板按 3×3 圆点检测；整板圆点更多时，只使用对应的 9 个。
            · 配方目录下准备好 models/、chessboard/、captured_chessboard/、roi/。

            分工
            · 视觉：阶段 1、3、4、5、6。
            · 工艺/PLC：阶段 2 的中心和步距。
            · 操作员：验收通过后才跑量产。
            """,
            """
            目标：得到棋盘内参、畸变和多视图外参，并确定透视用的 viewIndex。
            流程：flows/v4/chessboard_intrinsics_from_dir.flow.json。
            输出：chessboard/calibration_result.json。

            采图
            · 棋盘完整入画、共面，姿态要有俯仰和偏航变化。
            · 至少留 1 张较正、完整的图，专供后面的 viewIndex。
            · 建议不少于 15 张。cols、rows、squareSizeMm 与实物棋盘一致。

            运行
            · 跑到 save_calibration_result，看标定质检：重投影误差在阈值内，失败张剔除或重拍。

            viewIndex
            · extrinsicsPerView 的顺序与标定成功的图像一致，从 0 起。
            · 「较正」那一张的序号就是 viewIndex，写入工艺卡。
            · 之后所有透视展开、校正都用这一个序号。

            验收
            · JSON 能被去畸变和透视算子加载。
            · 目视没有严重拉伸或异常黑边。
            · viewIndex 已书面记下。
            """,
            """
            目标：用「一个中心 + 两个步距」生成 3×3 九点世界坐标，不是逐点示教 9 次。
            流程：calibSendContour.flow.json 里的 weld_trajectory_world。
            输出：models/world_pos.txt，供阶段 3 使用。

            PLC 顺序（必须遵守）
            · 连接 → 先开手动模式 → 再开标定模式。
            · 未开手动模式前，不要点动、使能或走拍照位。
            · 做完后先关标定模式，再关手动模式。

            中心与步距
            · 治具放在阶段 3 的拍照位，坐标系与量产一致。
            · 机台移到 3×3 的几何中心（中心圆点），从实时位置读 centerX/Y/Z（mm）。
            · stepXmm：相邻两列在 X 上的间距；stepYmm：相邻两行在 Y 上的间距。建议按实测填写，不要留空。

            生成文件
            · pattern 选九宫格，gridRows/gridCols = 3/3，无旋转则角度为 0。
            · 运行 weld_trajectory_world → points_to_text → save_text，得到 9 行 X,Y。
            · 顺序为行优先：从左到右，再下一行。正中间那个点是第 5 行，应对准 center。
            · caliNinePoint.flow.json 的 calibrate 指向 models\\world_pos.txt。
            · 若机台正方向和图像左右上下相反，改步距符号或 rotateDeg，不要只改文件行序。

            验收
            · center、step 与工艺尺寸、记录表一致。
            · world_pos.txt 正好 9 行，第 5 点是中心。
            """,
            """
            目标：在已矫正的标定板图像上检出 9 个圆点，建立像素到世界的仿射。
            流程：caliNinePoint.flow.json。
            输出：models/calibration_nine_point.json。

            链路
            · 取图（与量产同曝光）→ 去畸变（chessboard/calibration_result.json）→ 透视（viewIndex 用阶段 1 记录值）。
            · detect_calibration_dots：gridRows=3，gridCols=3。画面应是绿框加 9 个蓝圈，不要框住金属边。
            · calibrate：worldPointsFile = 阶段 2 的 world_pos.txt，confirmCorrespondence=true。
            · 对话框里核对 9 组像素与世界点的对应。需要时用 adjust_affine_calibration 微调。

            验收
            · 检出不少于 9 点，配对正确，重投影误差合格。
            · 满屏蓝圈多半是边框误检，先改检测再保存。
            · JSON 备份并登记版本。换了中心或步距后，必须重做本阶段。
            """,
            """
            目标：做出量产用的形状模板，并用与量产相同的矫正图制作。
            入口：高级功能 → 形状模板。
            保存：flows/v4/models/，在粗/精匹配子流程里用相对路径加载。

            推荐顺序
            · 外框模板（.shm，角度范围大）→ 粗匹配模板（.shm，给位置）→ 精匹配（.shm 或 .dfm，给轮廓）→ 需要时再做焊点/圆点模板。
            · 粗匹配不准，精匹配一定会偏。先调粗，再调精。

            操作
            · 用阶段 1 的去畸变和透视之后的图做模板，不要用原图。
            · 记下模板名、日期、产品型号、角度和缩放范围。
            · 九点 flow 不依赖标定板模板；工件模板仍按本节制作。

            验收
            · halcon_coarse_shape_match.flow.json 与 halcon_fine_shape_match.flow.json 单图匹配稳定后，再跑 main.flow.json。
            · 角度和位置要盖住量产时的波动。
            """,
            """
            目标：打通 采集 → 矫正 → 粗/精匹配 → 轮廓 → img_to_world → send_plc。
            流程：main.flow.json。

            标定与图像
            · load_calibration_result 指向阶段 3 的 models/calibration_nine_point.json。
            · 去畸变、透视仍用 chessboard/calibration_result.json，viewIndex 与阶段 1 相同。

            模板与子流程
            · 粗、精子流程里的模型路径指向阶段 4 的文件。
            · main 里组合算子的 innerFlowPath 指向同目录的 halcon_coarse_shape_match.flow.json 和 halcon_fine_shape_match.flow.json。
            · 粗匹配的 Row/Col/Angle 接到精匹配的 CoarseRow/CoarseColumn/CoarseAngle。ROI 用 roi/*.xvroi.json。

            PLC
            · plc_connect 与现场一致。send_plc 的分包、寄存器、GVAR 起点与电气文档一致。
            · 先只看 img_to_world 的坐标，确认后再允许下发。
            · 量产前：标定模式关、手动模式关。
            """,
            """
            目标：资料齐、变更规则明确之后，才勾选「允许操作员量产运行」。

            交付清单
            · chessboard/calibration_result.json，以及书面记录的 viewIndex。
            · models/calibration_nine_point.json，并说明 world_pos 从哪来。
            · 模板文件列表（.shm / .dfm）、主流程路径、成像参数表、PLC 地址表、标定记录表。

            什么情况必须重做
            · 相机、镜头、光圈、焦距变了：从阶段 1 重做。
            · 光源变化大：先复验，严重则重标。
            · 棋盘或标定板换了：重标。
            · 九点物理位置变了：重做阶段 2 和 3。
            · 只改了模板或 ROI：阶段 4、5 验证即可，不必重做九点。

            勾选本阶段并勾选「允许量产」后，操作员才能启动主流程。
            """,
        };

        private readonly List<CheckBox> _stageChecks = new();
        private ProductRecipeCard _card = new();
        private string _recipeDirectory = "";
        private bool _readOnly;

        public ChangeoverWizardDialog(string recipeName, bool readOnly)
        {
            InitializeComponent();
            _readOnly = readOnly;
            Title = $"换型向导 — {recipeName}";
            _recipeDirectory = FlowRecipeCatalog.TryGetRecipeDirectory(recipeName) ?? "";
            _card = ProductRecipeCard.LoadForRecipe(recipeName);

            for (int i = 0; i < StageTitles.Length; i++)
            {
                int stageIndex = i;
                var cb = new CheckBox
                {
                    Content = StageTitles[i],
                    IsChecked = _card.ChangeoverStages.TryGetValue(i.ToString(), out bool ok) && ok,
                    IsEnabled = !readOnly,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0),
                };
                var help = new Button
                {
                    Content = "说明",
                    MinWidth = 64,
                    Margin = new Thickness(8, 0, 0, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                    ToolTip = "查看本阶段操作说明",
                };
                if (TryFindResource("IndustrialToolbarButton") is Style helpStyle)
                    help.Style = helpStyle;
                help.Click += (_, _) => ShowStageDetail(stageIndex);

                var row = new Grid { Margin = new Thickness(0, 0, 0, 8) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                Grid.SetColumn(help, 1);
                row.Children.Add(cb);
                row.Children.Add(help);

                _stageChecks.Add(cb);
                StagePanel.Children.Add(row);
            }

            ChkProductionReleased.IsChecked = _card.ProductionReleased;
            ChkProductionReleased.IsEnabled = !readOnly;
            BtnSave.IsEnabled = !readOnly;
        }

        private void ShowStageDetail(int index)
        {
            if (index < 0 || index >= StageTitles.Length)
                return;

            var body = new TextBlock
            {
                Text = StageDetails[index],
                TextWrapping = TextWrapping.Wrap,
                FontSize = 13,
                LineHeight = 22,
                Margin = new Thickness(16),
            };
            if (TryFindResource("Ui.TextPrimary") is Brush fg)
                body.Foreground = fg;

            var scroll = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = body,
            };
            var close = new Button
            {
                Content = "关闭",
                MinWidth = 80,
                IsCancel = true,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(16, 0, 16, 16),
            };
            if (TryFindResource("IndustrialToolbarButton") is Style closeStyle)
                close.Style = closeStyle;

            var root = new DockPanel();
            DockPanel.SetDock(close, Dock.Bottom);
            root.Children.Add(close);
            root.Children.Add(scroll);

            var dlg = new Window
            {
                Title = StageTitles[index],
                Width = 560,
                Height = 480,
                MinWidth = 420,
                MinHeight = 320,
                Owner = this,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                ResizeMode = ResizeMode.CanResizeWithGrip,
                Content = root,
                ShowInTaskbar = false,
            };
            if (TryFindResource("Ui.WorkspaceBackground") is Brush bg)
                dlg.Background = bg;
            close.Click += (_, _) => dlg.Close();
            dlg.ShowDialog();
        }

        private void BtnSave_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(_recipeDirectory))
            {
                MessageBox.Show("未找到配方目录。", Title, MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            for (int i = 0; i < _stageChecks.Count; i++)
                _card.ChangeoverStages[i.ToString()] = _stageChecks[i].IsChecked == true;

            _card.ProductionReleased = ChkProductionReleased.IsChecked == true;
            _card.SaveToRecipeDirectory(_recipeDirectory);
            DialogResult = true;
        }
    }
}
