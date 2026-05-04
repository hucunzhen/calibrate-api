using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using System.Text.Json;
using CalibOperatorPInvoke;
using HslCommunication.ModBus;

namespace CalibOperatorCLI_Example
{
    public partial class PlcPage : Page
    {
        private ModbusTcpNet? _plc;
        private bool _plcConnected = false;
        private DispatcherTimer? _readTimer;

        // ===== PLC 配置 =====
        private PlcConfig? _config;

        private int HdModbusOffset => _config?.HdModbusOffset ?? 0xA080;

        /// <summary>
        /// 由 MainWindow 注入：获取轨迹检测最新结果的委托
        /// </summary>
        public Func<TrajectoryResult?>? GetTrajectoryResult { get; set; }

        public PlcPage()
        {
            InitializeComponent();
            LoadConfig();
            ApplyConfigToUI();
            Log("PLC Communication Page Loaded (ModbusTcp)");
        }

        private void LoadConfig()
        {
            try
            {
                string configPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "plc_config.json");
                if (!File.Exists(configPath))
                {
                    Log("[PLC] 配置文件不存在: " + configPath);
                    _config = new PlcConfig();
                    return;
                }
                string json = File.ReadAllText(configPath);
                _config = JsonSerializer.Deserialize<PlcConfig>(json);
                Log("[PLC] 配置已加载: plc_config.json");
            }
            catch (Exception ex)
            {
                Log($"[PLC] 加载配置失败: {ex.Message}");
                _config = new PlcConfig();
            }
        }


        /// <summary>
        /// 将配置中的地址动态显示到 UI 上，保持一致性
        /// </summary>
        private void ApplyConfigToUI()
        {
            if (_config == null) return;

            // 位置寄存器 TextBox 默认值
            TxtRegX.Text = TryReg("PositionX", "HD2100");
            TxtRegY.Text = TryReg("PositionY", "HD2200");
            TxtRegZ.Text = TryReg("PositionZ", "HD2000");

            // 位操作按钮
            ApplyBitButton(BtnXForward, "XForward");
            ApplyBitButton(BtnXReverse, "XReverse");
            ApplyBitButton(BtnXZero, "XZero");
            ApplyBitButton(BtnXHome, "XHome");
            ApplyBitButton(BtnYForward, "YForward");
            ApplyBitButton(BtnYReverse, "YReverse");
            ApplyBitButton(BtnYZero, "YZero");
            ApplyBitButton(BtnYHome, "YHome");
            ApplyBitButton(BtnZForward, "ZForward");
            ApplyBitButton(BtnZReverse, "ZReverse");
            ApplyBitButton(BtnZZero, "ZZero");
            ApplyBitButton(BtnZHome, "ZHome");

            // 模式/启停按钮
            BtnManual.Content = $"Manual ({TryReg("ManualMode", "M509")}=ON)";
            BtnManual.ToolTip = $"手动模式：写 {TryReg("ManualMode", "M509")} = ON";
            BtnAuto.Content = $"Auto ({TryReg("ManualMode", "M509")}=OFF)";
            BtnAuto.ToolTip = $"自动模式：写 {TryReg("ManualMode", "M509")} = OFF";

            string runAddr = TryReg("RunStartStop", "M500");
            BtnStart.Content = $"Start ({runAddr}=ON)";
            BtnStart.ToolTip = $"启动：写 {runAddr} = ON";
            BtnStop.Content = $"Stop ({runAddr}=OFF)";
            BtnStop.ToolTip = $"停止：写 {runAddr} = OFF";

            // 参数标签
            LblXHomeSpd.Text = $"HomeSpd {TryReg("XHomeSpeed", "---")}:";
            LblXManualSpd.Text = $"ManualSpd {TryReg("XManualSpeed", "---")}:";
            LblXPosLimit.Text = $"+Limit {TryReg("XPosLimit", "---")}:";
            LblXNegLimit.Text = $"-Limit {TryReg("XNegLimit", "---")}:";

            LblYHomeSpd.Text = $"HomeSpd {TryReg("YHomeSpeed", "---")}:";
            LblYManualSpd.Text = $"ManualSpd {TryReg("YManualSpeed", "---")}:";
            LblYPosLimit.Text = $"+Limit {TryReg("YPosLimit", "---")}:";
            LblYNegLimit.Text = $"-Limit {TryReg("YNegLimit", "---")}:";

            LblZHomeSpd.Text = $"HomeSpd {TryReg("ZHomeSpeed", "---")}:";
            LblZManualSpd.Text = $"ManualSpd {TryReg("ZManualSpeed", "---")}:";
            LblZPosLimit.Text = $"+Limit {TryReg("ZPosLimit", "---")}:";
            LblZNegLimit.Text = $"-Limit {TryReg("ZNegLimit", "---")}:";

            // GVAR 列表地址
            TxtGvarAddr.Text = _config?.GvarList?.StartAddress ?? "D5000";
        }

        private string TryReg(string key, string fallback)
        {
            try { return Reg(key); }
            catch { return fallback; }
        }

        private void ApplyBitButton(Button btn, string actionKey)
        {
            var action = BitAction(actionKey);
            if (action == null) return;
            btn.ToolTip = $"{actionKey}: {action.Register}.bit{action.Bit}={(action.Value ? 1 : 0)}";
        }

        /// <summary>
        /// 获取寄存器地址，从配置文件读取
        /// </summary>
        private string Reg(string key) => _config?.Registers?.GetValueOrDefault(key) ?? throw new KeyNotFoundException($"未配置寄存器: {key}");

        /// <summary>
        /// 获取位操作配置
        /// </summary>
        private PlcConfig.BitActionConfig? BitAction(string key) => _config?.BitActions?.GetValueOrDefault(key);


        private void Log(string message)
        {
            string timestamp = DateTime.Now.ToString("HH:mm:ss");
            string logLine = $"[{timestamp}] {message}";
            Dispatcher.Invoke(() =>
            {
                LogTextBox.AppendText(logLine + Environment.NewLine);
                LogTextBox.ScrollToEnd();
            });
            UpdateStatus(message);
        }

        private void UpdateStatus(string message)
        {
            Dispatcher.Invoke(() => StatusText.Text = message);
        }

        private void UpdatePositionDisplay(double x, double y, double z, double speedXY)
        {
            Dispatcher.Invoke(() =>
            {
                TxtPosX.Text = double.IsNaN(x) ? "---" : x.ToString("F3");
                TxtPosY.Text = double.IsNaN(y) ? "---" : y.ToString("F3");
                TxtPosZ.Text = double.IsNaN(z) ? "---" : z.ToString("F3");
                TxtSpeedXY.Text = double.IsNaN(speedXY) ? "---" : speedXY.ToString("F3");
            });
        }

        /// <summary>
        /// 将信捷地址字符串转为 Modbus 寄存器地址
        /// HD2100 → 41088 + 2100 = 43188
        /// D2000  → 2000
        /// </summary>
        private int XinjeAddressToModbus(string addr)
        {
            addr = addr.Trim().ToUpper();
            if (addr.StartsWith("HD") && int.TryParse(addr[2..], out int hdIdx))
                return HdModbusOffset + hdIdx;
            if (addr.StartsWith("D") && int.TryParse(addr[1..], out int dIdx))
                return dIdx;
            return -1;
        }

        private void ReadAxisPositions()
        {
            if (_plc == null || !_plcConnected) return;

            try
            {
                string regX = TxtRegX.Text.Trim();
                string regY = TxtRegY.Text.Trim();
                string regZ = TxtRegZ.Text.Trim();

                double x = ReadPlcFloat(regX, "X");
                double y = ReadPlcFloat(regY, "Y");
                double z = ReadPlcFloat(regZ, "Z");
                double speedXY = ReadPlcFloat(Reg("SpeedXY"), "XY速度");

                UpdatePositionDisplay(x, y, z, speedXY);
            }
            catch (Exception ex)
            {
                Log($"[PLC] Read error: {ex.Message}");
            }
        }

        /// <summary>
        /// 读取 PLC 浮点值（32位，占2个连续寄存器）
        /// 通过 ModbusTcpNet.ReadFloat 直接读取，地址为 Modbus 寄存器地址。
        /// </summary>
        private double ReadPlcFloat(string xinjeAddr, string label)
        {
            if (_plc == null || !_plcConnected) return double.NaN;

            int addr = XinjeAddressToModbus(xinjeAddr);
            if (addr < 0)
            {
                Log($"[PLC] 无效地址: {xinjeAddr}");
                return double.NaN;
            }

            var result = _plc.ReadFloat(addr.ToString());
            if (result.IsSuccess)
                return (double)result.Content;

            Log($"[PLC] {label}({xinjeAddr}→{addr}) 读取失败: {result.Message}");
            return double.NaN;
        }

        private void BtnReadOnce_Click(object sender, RoutedEventArgs e)
        {
            if (!CheckPlcConnected()) return;
            ReadAxisPositions();
            Log("[PLC] 手动读取位置完成");
        }

        /// <summary>
        /// 写入浮点值到指定地址（32位，占2个连续寄存器）
        /// 通过 ModbusTcpNet.Write 直接写入 float。
        /// </summary>
        private bool WritePlcFloat(string xinjeAddr, string textBoxName, string label)
        {
            var txt = this.FindName(textBoxName) as TextBox;
            if (txt == null) return false;
            if (!double.TryParse(txt.Text.Trim(), out double value))
            {
                MessageBox.Show($"{label}：请输入有效数值", "输入错误", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }
            if (!CheckPlcConnected()) return false;

            int addr = XinjeAddressToModbus(xinjeAddr);
            if (addr < 0)
            {
                Log($"[PLC] 无效地址: {xinjeAddr}");
                return false;
            }

            var result = _plc.Write(addr.ToString(), (float)value);
            if (result.IsSuccess)
            {
                Log($"[PLC] {label}({xinjeAddr}→{addr})={value:F3} 写入成功");
                return true;
            }
            Log($"[PLC] {label}({xinjeAddr}→{addr})={value:F3} 写入失败: {result.Message}");
            return false;
        }

        /// <summary>
        /// 写入线圈（M寄存器）bool 值
        /// ModbusTcpNet 通过 Write(address, bool) 写线圈
        /// </summary>
        private void WriteCoil(string mAddr, bool value)
        {
            if (_plc == null || !_plcConnected) return;
            string addr = mAddr.Trim().ToUpper();
            if (!addr.StartsWith("M") || !int.TryParse(addr[1..], out int coilAddr)) return;

            var result = _plc.Write(coilAddr.ToString(), value);
            if (!result.IsSuccess)
                Log($"[PLC] Write {mAddr}={(value ? "ON" : "OFF")} failed: {result.Message}");
        }

        /// <summary>
        /// 读取线圈（M寄存器）bool 值
        /// </summary>
        private bool ReadCoil(string mAddr)
        {
            if (_plc == null || !_plcConnected) return false;
            string addr = mAddr.Trim().ToUpper();
            if (!addr.StartsWith("M") || !int.TryParse(addr[1..], out int coilAddr)) return false;

            var result = _plc.ReadBool(coilAddr.ToString());
            return result.IsSuccess && result.Content;
        }

        // ===== GVAR 列表读写 =====
        private GVAR[]? _gvarList;

        /// <summary>
        /// GVAR 列表在内存中的缓存
        /// </summary>
        public GVAR[] GvarList => _gvarList ?? Array.Empty<GVAR>();

        /// <summary>
        /// 读取 GVAR 列表（从 D5000 开始，批量读取所有寄存器后解析）
        /// </summary>
        private bool ReadGvarList(int count)
        {
            if (_plc == null || !_plcConnected) return false;
            var gvarCfg = _config?.GvarList;
            if (gvarCfg == null)
            {
                Log("[PLC] GvarList 未在配置中定义");
                return false;
            }

            int totalRegisters = count * GVAR.WORD_COUNT;
            if (totalRegisters <= 0 || totalRegisters > 2000)
            {
                Log($"[PLC] GVAR count={count} 无效（寄存器总数={totalRegisters}）");
                return false;
            }

            int startAddr = XinjeAddressToModbus(gvarCfg.StartAddress);
            if (startAddr < 0)
            {
                Log($"[PLC] GVAR 起始地址无效: {gvarCfg.StartAddress}");
                return false;
            }

            var result = _plc.ReadUInt16(startAddr.ToString(), (ushort)totalRegisters);
            if (!result.IsSuccess)
            {
                Log($"[PLC] 读取 GVAR 列表失败: {result.Message}");
                return false;
            }

            ushort[] regs = result.Content;
            _gvarList = new GVAR[count];
            for (int i = 0; i < count; i++)
            {
                int offset = i * GVAR.WORD_COUNT;
                if (offset + GVAR.WORD_COUNT <= regs.Length)
                    _gvarList[i] = GVAR.FromRegisters(regs, offset);
            }

            Log($"[PLC] 读取 GVAR 列表成功：{count} 项，起始 {gvarCfg.StartAddress}，共 {totalRegisters} 寄存器");
            return true;
        }

        /// <summary>
        /// 写入 GVAR 列表（将缓存中的数据批量写入 PLC）
        /// </summary>
        private bool WriteGvarList()
        {
            if (_plc == null || !_plcConnected) return false;
            if (_gvarList == null || _gvarList.Length == 0)
            {
                Log("[PLC] GVAR 列表为空，无数据可写入");
                return false;
            }

            var gvarCfg = _config?.GvarList;
            if (gvarCfg == null)
            {
                Log("[PLC] GvarList 未在配置中定义");
                return false;
            }

            int count = _gvarList.Length;
            int totalRegisters = count * GVAR.WORD_COUNT;
            int startAddr = XinjeAddressToModbus(gvarCfg.StartAddress);
            if (startAddr < 0)
            {
                Log($"[PLC] GVAR 起始地址无效: {gvarCfg.StartAddress}");
                return false;
            }

            // 分批写入，每条 GVAR 写 2 次：type1(short) + 连续 float 数组
            // 让 HslCommunication 自动处理信捷 PLC 的字节序
            int totalItems = count;
            int written = 0;
            for (int i = 0; i < totalItems; i++)
            {
                var g = _gvarList[i];
                int itemBase = startAddr + i * GVAR.WORD_COUNT;

                try
                {
                    // 写 type1 (short) 到寄存器 0
                    var r = _plc.Write(itemBase.ToString(), (short)g.type1);
                    if (!r.IsSuccess) throw new Exception(r.Message);

                    // 写 13 个连续 float: 从寄存器 2 开始（跳过 pad 寄存器 1）
                    // 寄存器 2..27 = p0.x, p0.y, p0.z, p1.x, p1.y, p1.z, cx, cy, r, start_deg, end_deg, z0, z1
                    float[] floats = new float[]
                    {
                        g.spVec3_p0.x, g.spVec3_p0.y, g.spVec3_p0.z,
                        g.spVec3_p1.x, g.spVec3_p1.y, g.spVec3_p1.z,
                        g.cx, g.cy, g.r,
                        g.start_deg, g.end_deg,
                        g.z0, g.z1
                    };
                    r = _plc.Write((itemBase + 2).ToString(), floats);
                    if (!r.IsSuccess) throw new Exception(r.Message);
                }
                catch (Exception ex)
                {
                    Log($"[PLC] 写入 GVAR 第 {i + 1}/{totalItems} 条失败: {ex.Message}");
                    Log($"[PLC] 已写入 {written}/{totalItems} 条");
                    return false;
                }
                written++;
                if (totalItems > 10 && (written % 100 == 0 || written == totalItems))
                    Log($"[PLC] 写入进度: {written}/{totalItems} 条");
            }

            Log($"[PLC] 写入 GVAR 列表成功：{count} 项，起始 {gvarCfg.StartAddress}，共 {totalRegisters} 寄存器");
            return true;
        }

        private void RefreshGvarGrid()
        {
            if (_gvarList == null) return;
            var items = new List<GvarItemViewModel>();
            for (int i = 0; i < _gvarList.Length; i++)
                items.Add(GvarItemViewModel.FromGVAR(i, _gvarList[i]));
            DgGvarList.ItemsSource = items;
        }

        private void BtnReadGvar_Click(object sender, RoutedEventArgs e)
        {
            if (!CheckPlcConnected()) return;
            if (!int.TryParse(TxtGvarCount.Text.Trim(), out int count) || count <= 0 || count > 200)
            {
                MessageBox.Show("条数应为 1-200 的整数", "输入错误", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (ReadGvarList(count))
            {
                RefreshGvarGrid();
                Log($"[PLC] GVAR 列表已刷新：{count} 项");
            }
        }

        private void BtnWriteGvar_Click(object sender, RoutedEventArgs e)
        {
            if (!CheckPlcConnected()) return;

            // 从 DataGrid 收集数据写回 _gvarList
            var items = DgGvarList.ItemsSource as List<GvarItemViewModel>;
            if (items == null || items.Count == 0)
            {
                MessageBox.Show("列表为空，无数据可写入", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            _gvarList = new GVAR[items.Count];
            for (int i = 0; i < items.Count; i++)
                _gvarList[i] = items[i].ToGVAR();

            if (WriteGvarList())
                Log($"[PLC] 已将 {items.Count} 条 GVAR 写入 PLC");
        }

        private void BtnClearGvar_Click(object sender, RoutedEventArgs e)
        {
            _gvarList = Array.Empty<GVAR>();
            DgGvarList.ItemsSource = new List<GvarItemViewModel>();
            Log("[PLC] GVAR 列表已清空");
        }

        private void BtnAddGvar_Click(object sender, RoutedEventArgs e)
        {
            var items = DgGvarList.ItemsSource as List<GvarItemViewModel> ?? new List<GvarItemViewModel>();
            items.Add(GvarItemViewModel.FromGVAR(items.Count, new GVAR()));
            DgGvarList.ItemsSource = null;
            DgGvarList.ItemsSource = items;
        }

        private void BtnRemoveGvar_Click(object sender, RoutedEventArgs e)
        {
            var items = DgGvarList.ItemsSource as List<GvarItemViewModel>;
            if (items == null || items.Count == 0) return;
            var selected = DgGvarList.SelectedItem as GvarItemViewModel;
            if (selected != null && items.Remove(selected))
            {
                // 重新编号
                for (int i = 0; i < items.Count; i++)
                    items[i].Index = i;
                DgGvarList.ItemsSource = null;
                DgGvarList.ItemsSource = items;
                Log($"[PLC] 已删除第 {selected.Index} 条 GVAR");
            }
        }

        /// <summary>
        /// 将轨迹检测得到的点序列转换为 GVAR 直线段列表，并刷新 DataGrid
        /// 每两个相邻轨迹点构成一条 GVAR 线段（type1=0，p0/p1 填 x/y，z=0，圆弧参数置零）
        /// </summary>
        private void BtnImportTrajectoryToGvar_Click(object sender, RoutedEventArgs e)
        {
            if (GetTrajectoryResult == null)
            {
                MessageBox.Show("未连接轨迹检测模块，请联系开发人员检查初始化代码。", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            var trajResult = GetTrajectoryResult();
            if (trajResult == null || !trajResult.Success || trajResult.Points == null || trajResult.Count == 0)
            {
                MessageBox.Show("当前没有可用的轨迹检测结果。\n请先在「轨迹检测」页面完成检测（Step10 或 AutoDetect）。",
                    "无轨迹数据", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var pts = trajResult.Points;
            int n = trajResult.Count;

            // 每两个相邻点构成一条直线段 GVAR（type1 = 0）
            // 最后一段：n-1 → 0（闭合）
            var gvarList = new List<GVAR>();
            for (int i = 0; i < n; i++)
            {
                var p0 = pts[i];
                var p1 = pts[(i + 1) % n];
                gvarList.Add(new GVAR
                {
                    type1 = 0,
                    spVec3_p0 = new SpVec3((float)p0.X, (float)p0.Y, 0f),
                    spVec3_p1 = new SpVec3((float)p1.X, (float)p1.Y, 0f),
                    cx = 0f, cy = 0f, r = 0f,
                    start_deg = 0f, end_deg = 0f,
                    z0 = 0f, z1 = 0f
                });
            }

            _gvarList = gvarList.ToArray();
            RefreshGvarGrid();
            Log($"[PLC] 已从轨迹导入 {gvarList.Count} 条 GVAR 直线段（{n} 个轨迹点）");
        }

        private DispatcherTimer? _holdTimer;
        private string? _holdAddr;
        private int _holdBitIndex = -1;
        private bool _holdValue;

        /// <summary>
        /// 按住写入：PreviewMouseDown 时启动定时器持续写入，PreviewMouseUp 时清零并停止
        /// </summary>
        private void HoldButton_PreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (!CheckPlcConnected()) return;
            var btn = sender as Button;
            if (btn == null) return;

            // 从配置文件查找对应的位操作
            string? actionKey = null;
            if (btn == BtnXForward) actionKey = "XForward";
            else if (btn == BtnXReverse) actionKey = "XReverse";
            else if (btn == BtnXZero) actionKey = "XZero";
            else if (btn == BtnXHome) actionKey = "XHome";
            else if (btn == BtnYForward) actionKey = "YForward";
            else if (btn == BtnYReverse) actionKey = "YReverse";
            else if (btn == BtnYZero) actionKey = "YZero";
            else if (btn == BtnYHome) actionKey = "YHome";
            else if (btn == BtnZForward) actionKey = "ZForward";
            else if (btn == BtnZReverse) actionKey = "ZReverse";
            else if (btn == BtnZZero) actionKey = "ZZero";
            else if (btn == BtnZHome) actionKey = "ZHome";
            if (actionKey == null) return;

            var action = BitAction(actionKey);
            if (action == null) { Log($"[PLC] 位操作未配置: {actionKey}"); return; }

            // 立即写入一次
            WriteBit(action.Register, action.Bit, action.Value);
            _holdAddr = action.Register;
            _holdBitIndex = action.Bit;
            _holdValue = action.Value;

            // 启动持续写入定时器（每200ms写一次，防止PLC自动复位）
            _holdTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
            _holdTimer.Tick += (_, _) => WriteBit(_holdAddr!, _holdBitIndex, _holdValue);
            _holdTimer.Start();
        }

        private void HoldButton_PreviewMouseUp(object sender, MouseButtonEventArgs e)
        {
            StopHoldTimer();
        }

        private void HoldButton_LostMouseCapture(object sender, MouseEventArgs e)
        {
            StopHoldTimer();
        }

        private void StopHoldTimer()
        {
            if (_holdTimer != null)
            {
                _holdTimer.Stop();
                _holdTimer = null;
            }
            if (_holdAddr != null)
            {
                // 抬起时清零：复位该位
                WriteBit(_holdAddr, _holdBitIndex, false);
                Log($"[PLC] {_holdAddr}.bit{_holdBitIndex} = 0 (释放)");
                _holdAddr = null;
            }
        }

        /// <summary>
        /// 操作 D 寄存器的单个 bit：读取当前值 → 置位/复位 → 写回
        /// </summary>
        private void WriteBit(string xinjeAddr, int bitIndex, bool set)
        {
            if (_plc == null || !_plcConnected) return;
            int addr = XinjeAddressToModbus(xinjeAddr);
            if (addr < 0) return;

            var readResult = _plc.ReadUInt16(addr.ToString());
            if (!readResult.IsSuccess)
            {
                Log($"[PLC] WriteBit read {xinjeAddr} failed: {readResult.Message}");
                return;
            }
            ushort val = readResult.Content;
            if (set)
                val |= (ushort)(1 << bitIndex);
            else
                val &= (ushort)~(1 << bitIndex);

            var writeResult = _plc.Write(addr.ToString(), val);
            if (!writeResult.IsSuccess)
                Log($"[PLC] WriteBit write {xinjeAddr} failed: {writeResult.Message}");
        }


        private void BtnManual_Click(object sender, RoutedEventArgs e)
        {
            if (!CheckPlcConnected()) return;
            WriteCoil(Reg("ManualMode"), true);
            TxtModeStatus.Text = "Manual";
            TxtModeStatus.Foreground = new SolidColorBrush(Colors.Purple);
            Log($"[PLC] 手动模式 {Reg("ManualMode")}=ON");
        }

        private void BtnAuto_Click(object sender, RoutedEventArgs e)
        {
            if (!CheckPlcConnected()) return;
            WriteCoil(Reg("ManualMode"), false);
            TxtModeStatus.Text = "Auto";
            TxtModeStatus.Foreground = new SolidColorBrush(Colors.Green);
            Log($"[PLC] 自动模式 {Reg("ManualMode")}=OFF");
        }

        private void BtnStart_Click(object sender, RoutedEventArgs e)
        {
            if (!CheckPlcConnected()) return;
            WriteCoil(Reg("RunStartStop"), true);
            Log($"[PLC] 启动 {Reg("RunStartStop")}=ON");
            UpdateRunStatus();
        }

        private void BtnStop_Click(object sender, RoutedEventArgs e)
        {
            if (!CheckPlcConnected()) return;
            WriteCoil(Reg("RunStartStop"), false);
            Log($"[PLC] 停止 {Reg("RunStartStop")}=OFF");
            UpdateRunStatus();
        }

        private void UpdateRunStatus()
        {
            if (_plc == null || !_plcConnected) return;
            bool running = ReadCoil(Reg("RunStatus"));
            Dispatcher.Invoke(() =>
            {
                if (running)
                {
                    TxtRunStatus.Text = "Running";
                    TxtRunStatus.Foreground = new SolidColorBrush(Colors.OrangeRed);
                }
                else
                {
                    TxtRunStatus.Text = "Stopped";
                    TxtRunStatus.Foreground = new SolidColorBrush(Colors.Gray);
                }
            });
        }

        private void BtnReadXHomeSpd_Click(object sender, RoutedEventArgs e)
        {
            if (!CheckPlcConnected()) return;
            double v = ReadPlcFloat(Reg("XHomeSpeed"), "X复位速度");
            TxtXHomeSpd.Text = double.IsNaN(v) ? "ERR" : v.ToString("F3");
            Log($"[PLC] X复位速度 {Reg("XHomeSpeed")}={TxtXHomeSpd.Text}");
        }

        private void BtnReadXManualSpd_Click(object sender, RoutedEventArgs e)
        {
            if (!CheckPlcConnected()) return;
            double v = ReadPlcFloat(Reg("XManualSpeed"), "X手动速度");
            TxtXManualSpd.Text = double.IsNaN(v) ? "ERR" : v.ToString("F3");
            Log($"[PLC] X手动速度 {Reg("XManualSpeed")}={TxtXManualSpd.Text}");
        }

        private void BtnReadXPosLimit_Click(object sender, RoutedEventArgs e)
        {
            if (!CheckPlcConnected()) return;
            double v = ReadPlcFloat(Reg("XPosLimit"), "X正限位");
            TxtXPosLimit.Text = double.IsNaN(v) ? "ERR" : v.ToString("F3");
            Log($"[PLC] X正限位 {Reg("XPosLimit")}={TxtXPosLimit.Text}");
        }

        private void BtnReadXNegLimit_Click(object sender, RoutedEventArgs e)
        {
            if (!CheckPlcConnected()) return;
            double v = ReadPlcFloat(Reg("XNegLimit"), "X负限位");
            TxtXNegLimit.Text = double.IsNaN(v) ? "ERR" : v.ToString("F3");
            Log($"[PLC] X负限位 {Reg("XNegLimit")}={TxtXNegLimit.Text}");
        }

        private void BtnWriteXHomeSpd_Click(object sender, RoutedEventArgs e) => WritePlcFloat(Reg("XHomeSpeed"), "TxtXHomeSpd", "X复位速度");
        private void BtnWriteXManualSpd_Click(object sender, RoutedEventArgs e) => WritePlcFloat(Reg("XManualSpeed"), "TxtXManualSpd", "X手动速度");
        private void BtnWriteXPosLimit_Click(object sender, RoutedEventArgs e) => WritePlcFloat(Reg("XPosLimit"), "TxtXPosLimit", "X正限位");
        private void BtnWriteXNegLimit_Click(object sender, RoutedEventArgs e) => WritePlcFloat(Reg("XNegLimit"), "TxtXNegLimit", "X负限位");

        private void BtnReadYHomeSpd_Click(object sender, RoutedEventArgs e)
        {
            if (!CheckPlcConnected()) return;
            double v = ReadPlcFloat(Reg("YHomeSpeed"), "Y复位速度");
            TxtYHomeSpd.Text = double.IsNaN(v) ? "ERR" : v.ToString("F3");
            Log($"[PLC] Y复位速度 {Reg("YHomeSpeed")}={TxtYHomeSpd.Text}");
        }

        private void BtnReadYManualSpd_Click(object sender, RoutedEventArgs e)
        {
            if (!CheckPlcConnected()) return;
            double v = ReadPlcFloat(Reg("YManualSpeed"), "Y手动速度");
            TxtYManualSpd.Text = double.IsNaN(v) ? "ERR" : v.ToString("F3");
            Log($"[PLC] Y手动速度 {Reg("YManualSpeed")}={TxtYManualSpd.Text}");
        }

        private void BtnReadYPosLimit_Click(object sender, RoutedEventArgs e)
        {
            if (!CheckPlcConnected()) return;
            double v = ReadPlcFloat(Reg("YPosLimit"), "Y正限位");
            TxtYPosLimit.Text = double.IsNaN(v) ? "ERR" : v.ToString("F3");
            Log($"[PLC] Y正限位 {Reg("YPosLimit")}={TxtYPosLimit.Text}");
        }

        private void BtnReadYNegLimit_Click(object sender, RoutedEventArgs e)
        {
            if (!CheckPlcConnected()) return;
            double v = ReadPlcFloat(Reg("YNegLimit"), "Y负限位");
            TxtYNegLimit.Text = double.IsNaN(v) ? "ERR" : v.ToString("F3");
            Log($"[PLC] Y负限位 {Reg("YNegLimit")}={TxtYNegLimit.Text}");
        }

        private void BtnWriteYHomeSpd_Click(object sender, RoutedEventArgs e) => WritePlcFloat(Reg("YHomeSpeed"), "TxtYHomeSpd", "Y复位速度");
        private void BtnWriteYManualSpd_Click(object sender, RoutedEventArgs e) => WritePlcFloat(Reg("YManualSpeed"), "TxtYManualSpd", "Y手动速度");
        private void BtnWriteYPosLimit_Click(object sender, RoutedEventArgs e) => WritePlcFloat(Reg("YPosLimit"), "TxtYPosLimit", "Y正限位");
        private void BtnWriteYNegLimit_Click(object sender, RoutedEventArgs e) => WritePlcFloat(Reg("YNegLimit"), "TxtYNegLimit", "Y负限位");

        private void BtnReadZHomeSpd_Click(object sender, RoutedEventArgs e)
        {
            if (!CheckPlcConnected()) return;
            double v = ReadPlcFloat(Reg("ZHomeSpeed"), "Z复位速度");
            TxtZHomeSpd.Text = double.IsNaN(v) ? "ERR" : v.ToString("F3");
            Log($"[PLC] Z复位速度 {Reg("ZHomeSpeed")}={TxtZHomeSpd.Text}");
        }

        private void BtnReadZManualSpd_Click(object sender, RoutedEventArgs e)
        {
            if (!CheckPlcConnected()) return;
            double v = ReadPlcFloat(Reg("ZManualSpeed"), "Z手动速度");
            TxtZManualSpd.Text = double.IsNaN(v) ? "ERR" : v.ToString("F3");
            Log($"[PLC] Z手动速度 {Reg("ZManualSpeed")}={TxtZManualSpd.Text}");
        }

        private void BtnReadZPosLimit_Click(object sender, RoutedEventArgs e)
        {
            if (!CheckPlcConnected()) return;
            double v = ReadPlcFloat(Reg("ZPosLimit"), "Z正限位");
            TxtZPosLimit.Text = double.IsNaN(v) ? "ERR" : v.ToString("F3");
            Log($"[PLC] Z正限位 {Reg("ZPosLimit")}={TxtZPosLimit.Text}");
        }

        private void BtnReadZNegLimit_Click(object sender, RoutedEventArgs e)
        {
            if (!CheckPlcConnected()) return;
            double v = ReadPlcFloat(Reg("ZNegLimit"), "Z负限位");
            TxtZNegLimit.Text = double.IsNaN(v) ? "ERR" : v.ToString("F3");
            Log($"[PLC] Z负限位 {Reg("ZNegLimit")}={TxtZNegLimit.Text}");
        }

        private void BtnWriteZHomeSpd_Click(object sender, RoutedEventArgs e) => WritePlcFloat(Reg("ZHomeSpeed"), "TxtZHomeSpd", "Z复位速度");
        private void BtnWriteZManualSpd_Click(object sender, RoutedEventArgs e) => WritePlcFloat(Reg("ZManualSpeed"), "TxtZManualSpd", "Z手动速度");
        private void BtnWriteZPosLimit_Click(object sender, RoutedEventArgs e) => WritePlcFloat(Reg("ZPosLimit"), "TxtZPosLimit", "Z正限位");
        private void BtnWriteZNegLimit_Click(object sender, RoutedEventArgs e) => WritePlcFloat(Reg("ZNegLimit"), "TxtZNegLimit", "Z负限位");

        private bool CheckPlcConnected()
        {
            if (_plcConnected) return true;
            MessageBox.Show("请先连接 PLC", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        private void ChkAutoRead_Checked(object sender, RoutedEventArgs e)
        {
            if (!_plcConnected)
            {
                ChkAutoRead.IsChecked = false;
                MessageBox.Show("请先连接 PLC", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (!int.TryParse(TxtReadInterval.Text.Trim(), out int ms) || ms < 50)
                ms = 500;

            _readTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ms) };
            _readTimer.Tick += (s, args) => { ReadAxisPositions(); UpdateRunStatus(); };
            _readTimer.Start();
            Log($"[PLC] 自动读取已启动，间隔 {ms}ms");
        }

        private void ChkAutoRead_Unchecked(object sender, RoutedEventArgs e)
        {
            _readTimer?.Stop();
            _readTimer = null;
            Log("[PLC] 自动读取已停止");
        }

        private void TxtReadInterval_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_readTimer == null) return; // auto read 未开启，不需要更新
            if (!int.TryParse(TxtReadInterval.Text.Trim(), out int ms) || ms < 50)
                return;
            _readTimer.Interval = TimeSpan.FromMilliseconds(ms);
            Log($"[PLC] 自动读取间隔已更新为 {ms}ms");
        }

        private void BtnPlcConnect_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string ip = TxtPlcIp.Text.Trim();
                if (!int.TryParse(TxtPlcPort.Text.Trim(), out int port) || port <= 0 || port > 65535)
                {
                    MessageBox.Show("Invalid port (1-65535).", "Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                if (string.IsNullOrEmpty(ip))
                {
                    MessageBox.Show("Invalid IP address.", "Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                Log($"[PLC] Connecting to {ip}:{port}...");

                if (_plc != null)
                {
                    if (_plcConnected) _plc.ConnectClose();
                    _plc = null;
                    _plcConnected = false;
                }

                _plc = new ModbusTcpNet(ip, port);
                _plc.Station = (byte)(_config?.ModbusStation ?? 0);

                var result = _plc.ConnectServer();
                if (result.IsSuccess)
                {
                    _plcConnected = true;
                    Log($"[PLC] Connected to {ip}:{port} (ModbusTcp)");
                    BtnPlcConnect.IsEnabled = false;
                    BtnPlcDisconnect.IsEnabled = true;
                    TxtPlcIp.IsEnabled = false;
                    TxtPlcPort.IsEnabled = false;
                    TxtPlcStatus.Text = "[已连接]";
                    TxtPlcStatus.Foreground = new SolidColorBrush(Colors.Green);
                    UpdatePositionDisplay(double.NaN, double.NaN, double.NaN, double.NaN);
                }
                else
                {
                    _plcConnected = false;
                    _plc = null;
                    Log($"[PLC] Failed: {result.Message}");
                    MessageBox.Show($"PLC connection failed!\n\nError: {result.Message}", "PLC Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            catch (Exception ex)
            {
                _plcConnected = false;
                _plc = null;
                Log($"[PLC] Error: {ex.Message}");
                MessageBox.Show(ex.Message, "PLC Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void BtnPlcDisconnect_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Log("[PLC] Disconnecting...");

                // 停止自动读取
                _readTimer?.Stop();
                _readTimer = null;
                ChkAutoRead.IsChecked = false;

                if (_plc != null && _plcConnected)
                    _plc.ConnectClose();

                _plc = null;
                _plcConnected = false;

                BtnPlcConnect.IsEnabled = true;
                BtnPlcDisconnect.IsEnabled = false;
                TxtPlcIp.IsEnabled = true;
                TxtPlcPort.IsEnabled = true;
                TxtPlcStatus.Text = "[未连接]";
                TxtPlcStatus.Foreground = new SolidColorBrush(Colors.Gray);
                UpdatePositionDisplay(double.NaN, double.NaN, double.NaN, double.NaN);
                Log("[PLC] Disconnected");
            }
            catch (Exception ex)
            {
                Log($"[PLC] Error: {ex.Message}");
                MessageBox.Show(ex.Message, "PLC Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }

    /// <summary>
    /// PLC 配置模型
    /// </summary>
    internal class PlcConfig
    {
        public int ModbusStation { get; set; } = 0;
        public int HdModbusOffset { get; set; } = 0xA080;
        public Dictionary<string, string>? Registers { get; set; }
        public Dictionary<string, BitActionConfig>? BitActions { get; set; }
        public GvarListConfig? GvarList { get; set; }

        internal class BitActionConfig
        {
            public string Register { get; set; } = "";
            public int Bit { get; set; }
            public bool Value { get; set; }
        }

        internal class GvarListConfig
        {
            public string StartAddress { get; set; } = "D5000";
            public int MaxCount { get; set; } = 50;
            /// <summary>
            /// 每个 GVAR 占用的寄存器数量
            /// short(1) + pad(1) + SpVec3(6) + SpVec3(6) + 7*float(14) = 28
            /// </summary>
            public int RegistersPerItem { get; set; } = 28;
        }
    }

    /// <summary>
    /// 三维向量（float）
    /// </summary>
    public struct SpVec3
    {
        public float x;
        public float y;
        public float z;

        public SpVec3(float x, float y, float z)
        {
            this.x = x;
            this.y = y;
            this.z = z;
        }
    }

    /// <summary>
    /// GVAR DataGrid 行绑定的 ViewModel（struct 字段展开为属性，支持双向绑定）
    /// </summary>
    public class GvarItemViewModel
    {
        public int Index { get; set; }
        public short type1 { get; set; }
        public float p0x { get; set; }
        public float p0y { get; set; }
        public float p0z { get; set; }
        public float p1x { get; set; }
        public float p1y { get; set; }
        public float p1z { get; set; }
        public float cx { get; set; }
        public float cy { get; set; }
        public float r { get; set; }
        public float start_deg { get; set; }
        public float end_deg { get; set; }
        public float z0 { get; set; }
        public float z1 { get; set; }

        public GVAR ToGVAR()
        {
            return new GVAR
            {
                type1 = type1,
                spVec3_p0 = new SpVec3(p0x, p0y, p0z),
                spVec3_p1 = new SpVec3(p1x, p1y, p1z),
                cx = cx, cy = cy, r = r,
                start_deg = start_deg, end_deg = end_deg,
                z0 = z0, z1 = z1
            };
        }

        public static GvarItemViewModel FromGVAR(int index, GVAR g)
        {
            return new GvarItemViewModel
            {
                Index = index,
                type1 = g.type1,
                p0x = g.spVec3_p0.x, p0y = g.spVec3_p0.y, p0z = g.spVec3_p0.z,
                p1x = g.spVec3_p1.x, p1y = g.spVec3_p1.y, p1z = g.spVec3_p1.z,
                cx = g.cx, cy = g.cy, r = g.r,
                start_deg = g.start_deg, end_deg = g.end_deg,
                z0 = g.z0, z1 = g.z1
            };
        }
    }

    /// <summary>
    /// 线段结构体 GVAR
    /// PLC 内存布局（每项 28 个寄存器）：
    ///   [0]      type1 (short) — 线段类型
    ///   [1]      pad — 对齐填充
    ///   [2..7]   spVec3_p0 (3×float) — 起点 x,y,z
    ///   [8..13]  spVec3_p1 (3×float) — 终点 x,y,z
    ///   [14..15] cx (float) — 圆心 X
    ///   [16..17] cy (float) — 圆心 Y
    ///   [18..19] r (float) — 半径
    ///   [20..21] start_deg (float) — 起始角度
    ///   [22..23] end_deg (float) — 终止角度
    ///   [24..25] z0 (float)
    ///   [26..27] z1 (float)
    /// </summary>
    public struct GVAR
    {
        /// <summary>每个 GVAR 占用的寄存器数</summary>
        public const int WORD_COUNT = 28;

        public short type1;
        public SpVec3 spVec3_p0;
        public SpVec3 spVec3_p1;
        public float cx;
        public float cy;
        public float r;
        public float start_deg;
        public float end_deg;
        public float z0;
        public float z1;

        /// <summary>
        /// 从 ushort 寄存器数组解析 GVAR（从 offset 位置开始，读取 28 个寄存器）
        /// 寄存器布局：[type1(1w)][pad(1w)][spVec3_p0(6w)][spVec3_p1(6w)][cx~z1(14w)]
        /// </summary>
        public static GVAR FromRegisters(ushort[] regs, int offset)
        {
            return new GVAR
            {
                type1 = (short)regs[offset + 0],
                // offset+1: pad
                spVec3_p0 = new SpVec3(
                    ReadFloat(regs, offset + 2),
                    ReadFloat(regs, offset + 4),
                    ReadFloat(regs, offset + 6)),
                spVec3_p1 = new SpVec3(
                    ReadFloat(regs, offset + 8),
                    ReadFloat(regs, offset + 10),
                    ReadFloat(regs, offset + 12)),
                cx = ReadFloat(regs, offset + 14),
                cy = ReadFloat(regs, offset + 16),
                r = ReadFloat(regs, offset + 18),
                start_deg = ReadFloat(regs, offset + 20),
                end_deg = ReadFloat(regs, offset + 22),
                z0 = ReadFloat(regs, offset + 24),
                z1 = ReadFloat(regs, offset + 26)
            };
        }

        /// <summary>
        /// 将 GVAR 写入 ushort 寄存器数组（从 offset 位置开始，写入 28 个寄存器）
        /// </summary>
        public void ToRegisters(ushort[] regs, int offset)
        {
            regs[offset + 0] = (ushort)type1;
            // offset+1: pad
            WriteFloat(regs, offset + 2, spVec3_p0.x);
            WriteFloat(regs, offset + 4, spVec3_p0.y);
            WriteFloat(regs, offset + 6, spVec3_p0.z);
            WriteFloat(regs, offset + 8, spVec3_p1.x);
            WriteFloat(regs, offset + 10, spVec3_p1.y);
            WriteFloat(regs, offset + 12, spVec3_p1.z);
            WriteFloat(regs, offset + 14, cx);
            WriteFloat(regs, offset + 16, cy);
            WriteFloat(regs, offset + 18, r);
            WriteFloat(regs, offset + 20, start_deg);
            WriteFloat(regs, offset + 22, end_deg);
            WriteFloat(regs, offset + 24, z0);
            WriteFloat(regs, offset + 26, z1);
        }

        private static float ReadFloat(ushort[] regs, int idx)
            => BitConverter.ToSingle(BitConverter.GetBytes((regs[idx + 1] << 16) | regs[idx]), 0);

        private static void WriteFloat(ushort[] regs, int idx, float value)
        {
            byte[] bytes = BitConverter.GetBytes(value);
            // BitConverter on little-endian: bytes = [D, C, B, A] (A=MSB, D=LSB)
            // 信捷 Modbus float ABCD 大端: 寄存器0=[A,B], 寄存器1=[C,D]
            regs[idx] = (ushort)((bytes[3] << 8) | bytes[2]);     // 寄存器0 = A,B (高字)
            regs[idx + 1] = (ushort)((bytes[1] << 8) | bytes[0]); // 寄存器1 = C,D (低字)
        }
    }
}
