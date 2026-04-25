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
                    TxtPlcStatus.Text = "[Connected]";
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
                TxtPlcStatus.Text = "[Disconnected]";
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

        internal class BitActionConfig
        {
            public string Register { get; set; } = "";
            public int Bit { get; set; }
            public bool Value { get; set; }
        }
    }
}
