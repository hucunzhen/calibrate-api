using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using System.Text.Json;
using HslCommunication.ModBus;
using HslCommunication.Profinet.XINJE;

namespace CalibOperatorCLI_Example
{
    public partial class PlcPage : Page
    {
        /// <summary>信捷 D / GVAR / D 位（XinJETcpNet，地址如 D30000）。</summary>
        private XinJETcpNet? _plcD;
        /// <summary>HD 浮点（ModbusTcpNet，HDnnnn → HdModbusOffset+nnnn）。</summary>
        private ModbusTcpNet? _plcHd;
        private bool _plcConnected = false;
        private DispatcherTimer? _readTimer;
        private PlcGvar3DPreviewControl? _gvar3D;
        private double _lastAxisX = double.NaN;
        private double _lastAxisY = double.NaN;
        private double _lastAxisZ = double.NaN;

        // ===== PLC 配置 =====
        private PlcConfig? _config;

        private int HdModbusOffset => _config?.HdModbusOffset ?? 0xA080;

        public PlcPage()
        {
            InitializeComponent();
            _gvar3D = new PlcGvar3DPreviewControl();
            Gvar3DHost.Content = _gvar3D;
            LoadConfig();
            ApplyConfigToUI();
            Log("PLC Communication Page Loaded (D=XinJETcpNet, HD=ModbusTcp)");
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


        /// <summary>寄存器地址仅来自 plc_config，写入控件 ToolTip（界面不展示地址）。</summary>
        private void ApplyRegToolTip(FrameworkElement? el, string regKey, string description)
        {
            if (el == null) return;
            try
            {
                string addr = Reg(regKey);
                el.ToolTip = string.IsNullOrWhiteSpace(description)
                    ? $"寄存器: {addr}"
                    : $"{description}\n寄存器: {addr}";
            }
            catch
            {
                el.ToolTip = description;
            }
        }

        private string ResolveGvarStartAddress()
        {
            var gvarCfg = _config?.GvarList;
            return PlcXinjeHelper.ResolveGvarStartAddress("", gvarCfg?.StartAddress ?? "D30000");
        }

        /// <summary>将 plc_config 中的寄存器地址写入 ToolTip，界面仅显示功能文案。</summary>
        private void ApplyConfigToUI()
        {
            if (_config == null) return;

            string posX = TryReg("PositionX", "HD2100");
            string posY = TryReg("PositionY", "HD2200");
            string posZ = TryReg("PositionZ", "HD2000");
            string posHint = $"位置读取（plc_config）\nX: {posX}\nY: {posY}\nZ: {posZ}\nXY速度: {TryReg("SpeedXY", "---")}";
            ChkAutoRead.ToolTip = posHint + "\n自动刷新时同步读 GVAR 3D";
            BtnReadOnce.ToolTip = posHint;
            TxtPosX.ToolTip = $"X 位置\n寄存器: {posX}";
            TxtPosY.ToolTip = $"Y 位置\n寄存器: {posY}";
            TxtPosZ.ToolTip = $"Z 位置\n寄存器: {posZ}";
            TxtSpeedXY.ToolTip = $"XY 合成速度\n寄存器: {TryReg("SpeedXY", "---")}";

            ApplyBitButton(BtnXForward, "XForward");
            ApplyBitButton(BtnXReverse, "XReverse");
            ApplyBitButton(BtnXZero, "XZero");
            ApplyBitButton(BtnYForward, "YForward");
            ApplyBitButton(BtnYReverse, "YReverse");
            ApplyBitButton(BtnYZero, "YZero");
            ApplyBitButton(BtnZForward, "ZForward");
            ApplyBitButton(BtnZReverse, "ZReverse");
            ApplyBitButton(BtnZZero, "ZZero");

            BtnManual.Content = "手动模式";
            ApplyRegToolTip(BtnManual, "ManualMode", "写 ON → 手动");
            BtnAuto.Content = "自动模式";
            ApplyRegToolTip(BtnAuto, "ManualMode", "写 OFF → 自动");
            BtnStart.Content = "启动";
            ApplyRegToolTip(BtnStart, "RunStartStop", "写 ON");
            BtnStop.Content = "停止";
            ApplyRegToolTip(BtnStop, "RunStartStop", "写 OFF");

            ApplyRegToolTip(TxtXHomeSpd, "XHomeSpeed", "X 复位速度");
            ApplyRegToolTip(TxtXManualSpd, "XManualSpeed", "X 手动速度");
            ApplyRegToolTip(TxtXPosLimit, "XPosLimit", "X 正限位");
            ApplyRegToolTip(TxtXNegLimit, "XNegLimit", "X 负限位");
            ApplyRegToolTip(BtnReadXHomeSpd, "XHomeSpeed", "读取");
            ApplyRegToolTip(BtnWriteXHomeSpd, "XHomeSpeed", "写入");

            ApplyRegToolTip(TxtYHomeSpd, "YHomeSpeed", "Y 复位速度");
            ApplyRegToolTip(TxtYManualSpd, "YManualSpeed", "Y 手动速度");
            ApplyRegToolTip(TxtYPosLimit, "YPosLimit", "Y 正限位");
            ApplyRegToolTip(TxtYNegLimit, "YNegLimit", "Y 负限位");

            ApplyRegToolTip(TxtZHomeSpd, "ZHomeSpeed", "Z 复位速度");
            ApplyRegToolTip(TxtZManualSpd, "ZManualSpeed", "Z 手动速度");
            ApplyRegToolTip(TxtZPosLimit, "ZPosLimit", "Z 正限位");
            ApplyRegToolTip(TxtZNegLimit, "ZNegLimit", "Z 负限位");

            ApplyRegToolTip(TxtLaserPower, "LaserPower", "激光功率");
            ApplyRegToolTip(TxtStopSafeX, "StopSafePosX", "停机安全位 X");
            ApplyRegToolTip(TxtStopSafeY, "StopSafePosY", "停机安全位 Y");
            ApplyRegToolTip(TxtStopSafeZ, "StopSafePosZ", "停机安全位 Z");
            ApplyRegToolTip(BtnReadStopSafePos, "StopSafePosX", "读取停机安全位 XYZ");
            ApplyRegToolTip(TxtPhotoPosX, "PhotoPosX", "拍照位 X");
            ApplyRegToolTip(TxtPhotoPosY, "PhotoPosY", "拍照位 Y");
            ApplyRegToolTip(TxtPhotoPosZ, "PhotoPosZ", "拍照位 Z");
            ApplyRegToolTip(TxtLaserRelX, "LaserRelPosX", "激光相对 X");
            ApplyRegToolTip(TxtLaserRelY, "LaserRelPosY", "激光相对 Y");
            ApplyRegToolTip(TxtLaserRelZ, "LaserRelPosZ", "激光相对 Z");
            ApplyRegToolTip(TxtWeldSpeed, "WeldSpeed", "焊接速度");
            ApplyRegToolTip(TxtReturnSafeSpeed, "ReturnSafeSpeed", "返回安全点速度");
            ApplyRegToolTip(TxtPhotoApproachSpeed, "PhotoApproachSpeed", "到拍照点速度");
            ApplyRegToolTip(TxtFastOffsetSpeed, "FastOffsetSpeed", "快速偏移速度");

            string gvarStart = ResolveGvarStartAddress();
            string segReg = TryReg("SegmentCount", "D800");
            string gvarHint = $"GVAR 起始: {gvarStart}\n线段数: {segReg}";
            BtnReadGvar.ToolTip = $"先读 {segReg} 线段数，再读 GVAR\n{gvarHint}";
            BtnWriteGvar.ToolTip = $"先写 {segReg}=表格条数，再写 GVAR\n{gvarHint}";
            BtnClearGvar.ToolTip = gvarHint;
            TxtGvarCount.ToolTip = $"当前线段数（读自 {segReg}）";

            ApplyEnableButton(BtnXEnable, "XEnableL");
            ApplyDisableButton(BtnXDisable, "XEnableH", "XEnableL");
            ApplyEnableButton(BtnYEnable, "YEnableL");
            ApplyDisableButton(BtnYDisable, "YEnableH", "YEnableL");
            ApplyEnableButton(BtnZEnable, "ZEnableL");
            ApplyDisableButton(BtnZDisable, "ZEnableH", "ZEnableL");
            ApplyEnableButton(BtnLaserEnable, "LaserEnable");
            ApplyRegToolTip(BtnLaserDisable, "LaserEnable", "激光使能 OFF");
            ApplyEnableButton(BtnRedLightEnable, "RedLightEnable");
            ApplyRegToolTip(BtnRedLightDisable, "RedLightEnable", "红光使能 OFF");
            ApplyEnableButton(BtnBlowEnable, "BlowEnable");
            ApplyRegToolTip(BtnBlowDisable, "BlowEnable", "吹气使能 OFF");
            ApplyRegToolTip(BtnCameraCaptureStart, "CameraCaptureStart", "相机拍照脉冲");

            ApplyRegToolTip(BtnReadWeldDone, "WeldDoneFlag", "读取焊接完成 (PLC→上位机)");
            ApplyRegToolTip(BtnClearWeldDone, "WeldDoneFlag", "清零，下一轮前须执行");
            ApplyRegToolTip(BtnSetWeldDoneHost, "WeldDoneHostFlag", "轨迹下发完成置 1");
            ApplyRegToolTip(BtnClearWeldDoneHost, "WeldDoneHostFlag", "清零");
        }

        private void ApplyEnableButton(Button btn, string regKey)
        {
            try
            {
                string addr = Reg(regKey);
                btn.ToolTip = $"使能: {addr}.bit0=ON";
            }
            catch { }
        }

        private void ApplyDisableButton(Button btn, string disableRegKey, string enableRegKey)
        {
            try
            {
                string disableAddr = Reg(disableRegKey);
                string enableAddr = Reg(enableRegKey);
                btn.ToolTip = $"禁用: {disableAddr}.bit0 脉冲，并清 {enableAddr}.bit0";
            }
            catch { }
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


        private int HdAddressToModbus(string xinjeAddr)
            => XinjePlcAddress.HdToModbus(xinjeAddr, HdModbusOffset);

        private void ReadAxisPositions()
        {
            if (!_plcConnected) return;

            try
            {
                string regX = TryReg("PositionX", "HD2100");
                string regY = TryReg("PositionY", "HD2200");
                string regZ = TryReg("PositionZ", "HD2000");

                double x = ReadPlcFloat(regX, "X");
                double y = ReadPlcFloat(regY, "Y");
                double z = ReadPlcFloat(regZ, "Z");
                double speedXY = ReadPlcFloat(Reg("SpeedXY"), "XY速度");

                _lastAxisX = x;
                _lastAxisY = y;
                _lastAxisZ = z;
                UpdatePositionDisplay(x, y, z, speedXY);
                RefreshGvar3DPreview();
            }
            catch (Exception ex)
            {
                Log($"[PLC] Read error: {ex.Message}");
            }
        }

        private void RefreshGvar3DPreview()
        {
            if (_gvar3D == null) return;
            bool hasAxis = !double.IsNaN(_lastAxisX) && !double.IsNaN(_lastAxisY) && !double.IsNaN(_lastAxisZ);
            _gvar3D.SetScene(_gvarList, _lastAxisX, _lastAxisY, _lastAxisZ, hasAxis);
        }

        /// <summary>Auto Read 周期：读轴位置后读 D800+GVAR 并刷新 3D（不刷新表格）。</summary>
        private void AutoRefreshPlcCycle()
        {
            ReadAxisPositions();
            UpdateRunStatus();
            ReadGvarListFor3D(silent: true);
        }

        /// <summary>读取 PLC 浮点：HD→Modbus；D→XinJETcpNet 信捷地址。</summary>
        private double ReadPlcFloat(string xinjeAddr, string label)
        {
            if (!_plcConnected) return double.NaN;

            if (XinjePlcAddress.IsHdAddress(xinjeAddr))
            {
                if (_plcHd == null) return double.NaN;
                int modbus = HdAddressToModbus(xinjeAddr);
                if (modbus < 0)
                {
                    Log($"[PLC] 无效 HD 地址: {xinjeAddr}");
                    return double.NaN;
                }
                var result = _plcHd.ReadFloat(modbus.ToString());
                if (result.IsSuccess)
                    return (double)result.Content;
                Log($"[PLC] {label}({xinjeAddr}→Modbus {modbus}) 读取失败: {result.Message}");
                return double.NaN;
            }

            if (_plcD == null) return double.NaN;
            string dAddr = PlcXinjeHelper.NormalizeWordAddress(xinjeAddr, out _);
            var dResult = _plcD.ReadFloat(dAddr);
            if (dResult.IsSuccess)
                return (double)dResult.Content;
            Log($"[PLC] {label}({dAddr}) 读取失败: {dResult.Message}");
            return double.NaN;
        }

        private void BtnReadOnce_Click(object sender, RoutedEventArgs e)
        {
            if (!CheckPlcConnected()) return;
            ReadAxisPositions();
            Log("[PLC] 手动读取位置完成");
        }

        /// <summary>写入 PLC 浮点：HD→Modbus；D→XinJETcpNet。</summary>
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

            if (XinjePlcAddress.IsHdAddress(xinjeAddr))
            {
                if (_plcHd == null) return false;
                int modbus = HdAddressToModbus(xinjeAddr);
                if (modbus < 0)
                {
                    Log($"[PLC] 无效 HD 地址: {xinjeAddr}");
                    return false;
                }
                var result = _plcHd.Write(modbus.ToString(), (float)value);
                if (result.IsSuccess)
                {
                    Log($"[PLC] {label}({xinjeAddr}→Modbus {modbus})={value:F3} 写入成功");
                    return true;
                }
                Log($"[PLC] {label}({xinjeAddr}→Modbus {modbus})={value:F3} 写入失败: {result.Message}");
                return false;
            }

            if (_plcD == null) return false;
            string dAddr = PlcXinjeHelper.NormalizeWordAddress(xinjeAddr, out _);
            var dResult = _plcD.Write(dAddr, (float)value);
            if (dResult.IsSuccess)
            {
                Log($"[PLC] {label}({dAddr})={value:F3} 写入成功");
                return true;
            }
            Log($"[PLC] {label}({dAddr})={value:F3} 写入失败: {dResult.Message}");
            return false;
        }

        private void WriteCoil(string mAddr, bool value)
        {
            if (_plcHd == null || !_plcConnected) return;
            string addr = mAddr.Trim().ToUpperInvariant();
            if (!addr.StartsWith("M", StringComparison.Ordinal) || !int.TryParse(addr[1..], out int coilAddr))
                return;

            var result = _plcHd.Write(coilAddr.ToString(), value);
            if (!result.IsSuccess)
                Log($"[PLC] Write {mAddr}={(value ? "ON" : "OFF")} failed: {result.Message}");
        }

        private bool ReadCoil(string mAddr)
        {
            if (_plcHd == null || !_plcConnected) return false;
            string addr = mAddr.Trim().ToUpperInvariant();
            if (!addr.StartsWith("M", StringComparison.Ordinal) || !int.TryParse(addr[1..], out int coilAddr))
                return false;

            var result = _plcHd.ReadBool(coilAddr.ToString());
            return result.IsSuccess && result.Content;
        }

        // ===== GVAR 列表读写 =====
        private GVAR[]? _gvarList;

        /// <summary>
        /// GVAR 列表在内存中的缓存
        /// </summary>
        public GVAR[] GvarList => _gvarList ?? Array.Empty<GVAR>();

        private string SegmentCountRegister => Reg("SegmentCount");

        private int GvarMaxCount()
            => _config?.GvarList?.MaxCount > 0 ? _config.GvarList.MaxCount : 1024;

        /// <summary>从 D800 读取线段数量。</summary>
        private bool TryReadSegmentCount(out int count, bool silent = false)
        {
            count = 0;
            if (_plcD == null || !_plcConnected) return false;

            string countReg = SegmentCountRegister;
            var r = PlcGvarModbus.ReadSegmentCount(_plcD, countReg);
            if (!r.IsSuccess)
            {
                if (!silent)
                    Log($"[PLC] 读取线段数失败({countReg}): {PlcGvarModbus.FormatOperateFailure(r)}");
                return false;
            }

            count = r.Content;
            int maxCount = GvarMaxCount();
            if (count <= 0 || count > maxCount)
            {
                if (!silent)
                    Log($"[PLC] {countReg}={count} 无效（允许 1-{maxCount}）");
                return false;
            }

            int displayCount = count;
            Dispatcher.Invoke(() => TxtGvarCount.Text = displayCount.ToString());
            if (!silent)
                Log($"[PLC] 读取线段数 {countReg}={count}");
            return true;
        }

        private bool TryWriteSegmentCount(int count)
        {
            if (_plcD == null || !_plcConnected) return false;

            string countReg = SegmentCountRegister;
            int maxCount = GvarMaxCount();
            if (count <= 0 || count > maxCount)
            {
                Log($"[PLC] 线段数 {count} 无效（允许 1-{maxCount}）");
                return false;
            }

            var wr = PlcGvarModbus.WriteSegmentCount(_plcD, countReg, (short)count);
            if (!wr.IsSuccess)
            {
                Log($"[PLC] 写入线段数失败({countReg}={count}): {PlcGvarModbus.FormatOperateFailure(wr)}");
                return false;
            }

            int displayCount = count;
            Dispatcher.Invoke(() => TxtGvarCount.Text = displayCount.ToString());
            Log($"[PLC] 写入线段数 {countReg}={count}");
            return true;
        }

        /// <summary>先读 D800，再按段数读 GVAR（起始地址见 GvarList.StartAddress）。</summary>
        private bool ReadGvarList(bool silent = false)
        {
            if (_plcD == null || !_plcConnected) return false;
            var gvarCfg = _config?.GvarList;
            if (gvarCfg == null)
            {
                if (!silent)
                    Log("[PLC] GvarList 未在配置中定义");
                return false;
            }

            if (!TryReadSegmentCount(out int count, silent))
                return false;

            string startD = ResolveGvarStartAddress();
            var result = PlcGvarModbus.ReadGvarListFromPlc(_plcD, startD, count);
            if (!result.IsSuccess)
            {
                if (!silent)
                    Log($"[PLC] 读取 GVAR 列表失败({startD}): {PlcGvarModbus.FormatOperateFailure(result)}");
                return false;
            }

            _gvarList = result.Content;
            int totalRegisters = count * GVAR.WORD_COUNT;
            if (!silent)
                Log($"[PLC] 读取 GVAR 成功：{count} 项 @{startD}，{totalRegisters} 寄存器（ReadFloat/{PlcXinjeHelper.ResolveFloatDataFormatString(_config)}）");
            PlcGvarDraft.Set(_gvarList, silent ? "AutoReadGvar" : "ReadGvar");
            return true;
        }

        /// <summary>自动刷新专用：读 GVAR 并更新 3D，失败时保留上一帧数据。</summary>
        private void ReadGvarListFor3D(bool silent)
        {
            var prev = _gvarList;
            if (ReadGvarList(silent))
                RefreshGvar3DPreview();
            else
                _gvarList = prev;
        }

        /// <summary>先写 D800=条数，再写 GVAR 列表。</summary>
        private bool WriteGvarList()
        {
            if (_plcD == null || !_plcConnected) return false;
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
            string startD = ResolveGvarStartAddress();
            string countReg = SegmentCountRegister;
            var wr = PlcGvarModbus.SendGvarListWithSegmentCount(_plcD, countReg, startD, _gvarList, out int totalRegisters);
            if (!wr.IsSuccess)
            {
                Log($"[PLC] 写入失败({countReg}/{startD}): {PlcGvarModbus.FormatOperateFailure(wr)}");
                return false;
            }

            Log($"[PLC] 写入成功：{countReg}={count}，GVAR {count} 项 @{startD}，{totalRegisters} 寄存器");
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
            if (ReadGvarList())
            {
                RefreshGvarGrid();
                RefreshGvar3DPreview();
                Log($"[PLC] GVAR 列表已刷新：{_gvarList?.Length ?? 0} 项");
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

            PlcGvarDraft.Set(_gvarList, "WriteAllGrid");
            if (WriteGvarList())
            {
                RefreshGvar3DPreview();
                Log($"[PLC] 已将 {items.Count} 条 GVAR 写入 PLC");
            }
        }

        private void BtnClearGvar_Click(object sender, RoutedEventArgs e)
        {
            _gvarList = Array.Empty<GVAR>();
            DgGvarList.ItemsSource = new List<GvarItemViewModel>();
            _gvar3D?.ClearScene();
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
            else if (btn == BtnYForward) actionKey = "YForward";
            else if (btn == BtnYReverse) actionKey = "YReverse";
            else if (btn == BtnYZero) actionKey = "YZero";
            else if (btn == BtnZForward) actionKey = "ZForward";
            else if (btn == BtnZReverse) actionKey = "ZReverse";
            else if (btn == BtnZZero) actionKey = "ZZero";
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
        /// L后缀 → bit 0, H后缀 → bit 8
        /// </summary>
        private bool ReadBit(string xinjeAddr, int bitIndex)
        {
            if (_plcD == null || !_plcConnected) return false;
            string word = PlcXinjeHelper.NormalizeWordAddress(xinjeAddr, out int extraBitOffset);
            int finalBit = bitIndex + extraBitOffset;
            var readResult = _plcD.ReadUInt16(word);
            if (!readResult.IsSuccess)
            {
                Log($"[PLC] ReadBit {xinjeAddr} failed: {readResult.Message}");
                return false;
            }

            return (readResult.Content & (1 << finalBit)) != 0;
        }

        private void WriteBit(string xinjeAddr, int bitIndex, bool set)
        {
            if (_plcD == null || !_plcConnected) return;
            string word = PlcXinjeHelper.NormalizeWordAddress(xinjeAddr, out int extraBitOffset);
            int finalBit = bitIndex + extraBitOffset;

            var readResult = _plcD.ReadUInt16(word);
            if (!readResult.IsSuccess)
            {
                Log($"[PLC] WriteBit read {xinjeAddr} failed: {readResult.Message}");
                return;
            }
            ushort val = readResult.Content;
            if (set)
                val |= (ushort)(1 << finalBit);
            else
                val &= (ushort)~(1 << finalBit);

            var writeResult = _plcD.Write(word, val);
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
            if (!_plcConnected) return;
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

        private void BtnReadLaserPower_Click(object sender, RoutedEventArgs e)
        {
            if (!CheckPlcConnected()) return;
            double v = ReadPlcFloat(Reg("LaserPower"), "激光功率");
            TxtLaserPower.Text = double.IsNaN(v) ? "ERR" : v.ToString("F3");
            Log($"[PLC] 激光功率 {Reg("LaserPower")}={TxtLaserPower.Text}");
        }

        private void BtnWriteLaserPower_Click(object sender, RoutedEventArgs e) =>
            WritePlcFloat(Reg("LaserPower"), "TxtLaserPower", "激光功率");

        private void BtnLaserEnable_Click(object sender, RoutedEventArgs e)
        {
            if (!CheckPlcConnected()) return;
            WriteBit(Reg("LaserEnable"), 0, true);
            TxtLaserEnableStatus.Text = "ON";
            TxtLaserEnableStatus.Foreground = new SolidColorBrush(Colors.Green);
            Log($"[PLC] 激光使能 {Reg("LaserEnable")} = ON");
        }

        private void BtnLaserDisable_Click(object sender, RoutedEventArgs e)
        {
            if (!CheckPlcConnected()) return;
            WriteBit(Reg("LaserEnable"), 0, false);
            TxtLaserEnableStatus.Text = "OFF";
            TxtLaserEnableStatus.Foreground = new SolidColorBrush(Colors.Gray);
            Log($"[PLC] 激光使能 {Reg("LaserEnable")} = OFF");
        }

        private void BtnRedLightEnable_Click(object sender, RoutedEventArgs e)
        {
            if (!CheckPlcConnected()) return;
            WriteBit(Reg("RedLightEnable"), 0, true);
            TxtRedLightEnableStatus.Text = "ON";
            TxtRedLightEnableStatus.Foreground = new SolidColorBrush(Colors.Green);
            Log($"[PLC] 红光使能 {Reg("RedLightEnable")} = ON");
        }

        private void BtnRedLightDisable_Click(object sender, RoutedEventArgs e)
        {
            if (!CheckPlcConnected()) return;
            WriteBit(Reg("RedLightEnable"), 0, false);
            TxtRedLightEnableStatus.Text = "OFF";
            TxtRedLightEnableStatus.Foreground = new SolidColorBrush(Colors.Gray);
            Log($"[PLC] 红光使能 {Reg("RedLightEnable")} = OFF");
        }

        private void BtnBlowEnable_Click(object sender, RoutedEventArgs e)
        {
            if (!CheckPlcConnected()) return;
            WriteBit(Reg("BlowEnable"), 0, true);
            TxtBlowEnableStatus.Text = "ON";
            TxtBlowEnableStatus.Foreground = new SolidColorBrush(Colors.Green);
            Log($"[PLC] 吹气使能 {Reg("BlowEnable")} = ON");
        }

        private void BtnBlowDisable_Click(object sender, RoutedEventArgs e)
        {
            if (!CheckPlcConnected()) return;
            WriteBit(Reg("BlowEnable"), 0, false);
            TxtBlowEnableStatus.Text = "OFF";
            TxtBlowEnableStatus.Foreground = new SolidColorBrush(Colors.Gray);
            Log($"[PLC] 吹气使能 {Reg("BlowEnable")} = OFF");
        }

        private void BtnCameraCaptureStart_Click(object sender, RoutedEventArgs e)
        {
            if (!CheckPlcConnected()) return;
            string addr = Reg("CameraCaptureStart");
            WriteBit(addr, 0, true);
            Log($"[PLC] 相机开始拍照 {addr} 触发");
            var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            timer.Tick += (_, _) => { WriteBit(addr, 0, false); timer.Stop(); };
            timer.Start();
        }

        private void UpdateWeldDoneStatusDisplay(bool done)
        {
            TxtWeldDoneStatus.Text = done ? "完成(1)" : "未完成(0)";
            TxtWeldDoneStatus.Foreground = new SolidColorBrush(done ? Colors.Green : Colors.Gray);
        }

        private void BtnReadWeldDone_Click(object sender, RoutedEventArgs e)
        {
            if (!CheckPlcConnected()) return;
            string addr = Reg("WeldDoneFlag");
            bool done = ReadBit(addr, 0);
            UpdateWeldDoneStatusDisplay(done);
            Log($"[PLC] 焊接完成标志 {addr} = {(done ? "ON(1)" : "OFF(0)")}");
        }

        private void BtnClearWeldDone_Click(object sender, RoutedEventArgs e)
        {
            if (!CheckPlcConnected()) return;
            string addr = Reg("WeldDoneFlag");
            WriteBit(addr, 0, false);
            UpdateWeldDoneStatusDisplay(false);
            Log($"[PLC] 焊接完成标志 {addr} 已清零");
        }

        private void BtnSetWeldDoneHost_Click(object sender, RoutedEventArgs e)
        {
            if (!CheckPlcConnected()) return;
            string addr = Reg("WeldDoneHostFlag");
            WriteBit(addr, 0, true);
            TxtWeldDoneHostStatus.Text = "已通知(1)";
            TxtWeldDoneHostStatus.Foreground = new SolidColorBrush(Colors.Green);
            Log($"[PLC] 轨迹下发完成 {addr} = 1 (上位机→PLC)");
        }

        private void BtnClearWeldDoneHost_Click(object sender, RoutedEventArgs e)
        {
            if (!CheckPlcConnected()) return;
            string addr = Reg("WeldDoneHostFlag");
            WriteBit(addr, 0, false);
            TxtWeldDoneHostStatus.Text = "未通知(0)";
            TxtWeldDoneHostStatus.Foreground = new SolidColorBrush(Colors.Gray);
            Log($"[PLC] 轨迹下发完成 {addr} 已清零");
        }

        private void BtnReadStopSafePos_Click(object sender, RoutedEventArgs e)
        {
            if (!CheckPlcConnected()) return;
            double x = ReadPlcFloat(Reg("StopSafePosX"), "停机安全位X");
            double y = ReadPlcFloat(Reg("StopSafePosY"), "停机安全位Y");
            double z = ReadPlcFloat(Reg("StopSafePosZ"), "停机安全位Z");
            TxtStopSafeX.Text = double.IsNaN(x) ? "ERR" : x.ToString("F3");
            TxtStopSafeY.Text = double.IsNaN(y) ? "ERR" : y.ToString("F3");
            TxtStopSafeZ.Text = double.IsNaN(z) ? "ERR" : z.ToString("F3");
            Log($"[PLC] 停机安全位 X={TxtStopSafeX.Text} Y={TxtStopSafeY.Text} Z={TxtStopSafeZ.Text}");
        }

        private void BtnWriteStopSafePos_Click(object sender, RoutedEventArgs e)
        {
            if (!CheckPlcConnected()) return;
            bool ok = WritePlcFloat(Reg("StopSafePosX"), "TxtStopSafeX", "停机安全位X")
                & WritePlcFloat(Reg("StopSafePosY"), "TxtStopSafeY", "停机安全位Y")
                & WritePlcFloat(Reg("StopSafePosZ"), "TxtStopSafeZ", "停机安全位Z");
            if (ok)
                Log($"[PLC] 停机安全位已写入 X={TxtStopSafeX.Text} Y={TxtStopSafeY.Text} Z={TxtStopSafeZ.Text}");
        }

        private void BtnReadPhotoPos_Click(object sender, RoutedEventArgs e) =>
            ReadCoordTriple("PhotoPosX", "PhotoPosY", "PhotoPosZ", "拍照位",
                TxtPhotoPosX, TxtPhotoPosY, TxtPhotoPosZ);

        private void BtnWritePhotoPos_Click(object sender, RoutedEventArgs e) =>
            WriteCoordTriple("PhotoPosX", "PhotoPosY", "PhotoPosZ", "拍照位",
                "TxtPhotoPosX", "TxtPhotoPosY", "TxtPhotoPosZ");

        private void BtnReadLaserRelPos_Click(object sender, RoutedEventArgs e) =>
            ReadCoordTriple("LaserRelPosX", "LaserRelPosY", "LaserRelPosZ", "激光相对位",
                TxtLaserRelX, TxtLaserRelY, TxtLaserRelZ);

        private void BtnWriteLaserRelPos_Click(object sender, RoutedEventArgs e) =>
            WriteCoordTriple("LaserRelPosX", "LaserRelPosY", "LaserRelPosZ", "激光相对位",
                "TxtLaserRelX", "TxtLaserRelY", "TxtLaserRelZ");

        private void BtnReadMotionSpeed_Click(object sender, RoutedEventArgs e)
        {
            if (!CheckPlcConnected()) return;
            SetTextBoxFloat(TxtWeldSpeed, ReadPlcFloat(Reg("WeldSpeed"), "焊接速度"));
            SetTextBoxFloat(TxtReturnSafeSpeed, ReadPlcFloat(Reg("ReturnSafeSpeed"), "返回安全点速度"));
            SetTextBoxFloat(TxtPhotoApproachSpeed, ReadPlcFloat(Reg("PhotoApproachSpeed"), "到拍照点速度"));
            SetTextBoxFloat(TxtFastOffsetSpeed, ReadPlcFloat(Reg("FastOffsetSpeed"), "快速偏移速度"));
            Log($"[PLC] 速度 焊接={TxtWeldSpeed.Text} 返安全={TxtReturnSafeSpeed.Text} 到拍照={TxtPhotoApproachSpeed.Text} 快偏={TxtFastOffsetSpeed.Text}");
        }

        private void BtnWriteMotionSpeed_Click(object sender, RoutedEventArgs e)
        {
            if (!CheckPlcConnected()) return;
            bool ok = WritePlcFloat(Reg("WeldSpeed"), "TxtWeldSpeed", "焊接速度")
                & WritePlcFloat(Reg("ReturnSafeSpeed"), "TxtReturnSafeSpeed", "返回安全点速度")
                & WritePlcFloat(Reg("PhotoApproachSpeed"), "TxtPhotoApproachSpeed", "到拍照点速度")
                & WritePlcFloat(Reg("FastOffsetSpeed"), "TxtFastOffsetSpeed", "快速偏移速度");
            if (ok)
                Log($"[PLC] 速度已写入 焊接={TxtWeldSpeed.Text} 返安全={TxtReturnSafeSpeed.Text} 到拍照={TxtPhotoApproachSpeed.Text} 快偏={TxtFastOffsetSpeed.Text}");
        }

        private static void SetTextBoxFloat(TextBox box, double v) =>
            box.Text = double.IsNaN(v) ? "ERR" : v.ToString("F3");

        private void ReadCoordTriple(string keyX, string keyY, string keyZ, string label,
            TextBox txtX, TextBox txtY, TextBox txtZ)
        {
            if (!CheckPlcConnected()) return;
            SetTextBoxFloat(txtX, ReadPlcFloat(Reg(keyX), label + "X"));
            SetTextBoxFloat(txtY, ReadPlcFloat(Reg(keyY), label + "Y"));
            SetTextBoxFloat(txtZ, ReadPlcFloat(Reg(keyZ), label + "Z"));
            Log($"[PLC] {label} X={txtX.Text} Y={txtY.Text} Z={txtZ.Text}");
        }

        private void WriteCoordTriple(string keyX, string keyY, string keyZ, string label,
            string txtXName, string txtYName, string txtZName)
        {
            if (!CheckPlcConnected()) return;
            bool ok = WritePlcFloat(Reg(keyX), txtXName, label + "X")
                & WritePlcFloat(Reg(keyY), txtYName, label + "Y")
                & WritePlcFloat(Reg(keyZ), txtZName, label + "Z");
            if (ok)
            {
                var tx = (TextBox)FindName(txtXName)!;
                var ty = (TextBox)FindName(txtYName)!;
                var tz = (TextBox)FindName(txtZName)!;
                Log($"[PLC] {label}已写入 X={tx.Text} Y={ty.Text} Z={tz.Text}");
            }
        }

        private bool CheckPlcConnected()
        {
            if (_plcConnected) return true;
            MessageBox.Show("请先连接 PLC", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        // ===== 轴使能控制 =====
        private void BtnXEnable_Click(object sender, RoutedEventArgs e)
        {
            if (!CheckPlcConnected()) return;
            WriteBit(Reg("XEnableL"), 0, true);
            TxtXEnableStatus.Text = "ON";
            TxtXEnableStatus.Foreground = new SolidColorBrush(Colors.Green);
            Log("[PLC] X使能 = ON");
        }

        private void BtnYEnable_Click(object sender, RoutedEventArgs e)
        {
            if (!CheckPlcConnected()) return;
            WriteBit(Reg("YEnableL"), 0, true);
            TxtYEnableStatus.Text = "ON";
            TxtYEnableStatus.Foreground = new SolidColorBrush(Colors.Green);
            Log("[PLC] Y使能 = ON");
        }

        private void BtnZEnable_Click(object sender, RoutedEventArgs e)
        {
            if (!CheckPlcConnected()) return;
            WriteBit(Reg("ZEnableL"), 0, true);
            TxtZEnableStatus.Text = "ON";
            TxtZEnableStatus.Foreground = new SolidColorBrush(Colors.Green);
            Log("[PLC] Z使能 = ON");
        }

        private void BtnXDisable_Click(object sender, RoutedEventArgs e)
            => PulseAxisDisable("X", Reg("XEnableH"), Reg("XEnableL"), TxtXEnableStatus);

        private void BtnYDisable_Click(object sender, RoutedEventArgs e)
            => PulseAxisDisable("Y", Reg("YEnableH"), Reg("YEnableL"), TxtYEnableStatus);

        private void BtnZDisable_Click(object sender, RoutedEventArgs e)
            => PulseAxisDisable("Z", Reg("ZEnableH"), Reg("ZEnableL"), TxtZEnableStatus);

        /// <summary>轴禁用：清使能位 + 高字节禁用位脉冲（与 plc_config 中 *EnableH 对应）。</summary>
        private void PulseAxisDisable(string axisLabel, string disableAddr, string enableAddr, TextBlock statusText)
        {
            if (!CheckPlcConnected()) return;
            WriteBit(enableAddr, 0, false);
            WriteBit(disableAddr, 0, true);
            statusText.Text = "OFF";
            statusText.Foreground = new SolidColorBrush(Colors.Gray);
            Log($"[PLC] {axisLabel}轴禁用 {disableAddr}.bit0 脉冲，{enableAddr}.bit0=OFF");
            var timer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(500)
            };
            timer.Tick += (_, _) =>
            {
                WriteBit(disableAddr, 0, false);
                timer.Stop();
            };
            timer.Start();
        }

        // ===== 报警清除控制 =====
        private void BtnXAlarmClear_Click(object sender, RoutedEventArgs e)
        {
            if (!CheckPlcConnected()) return;
            WriteBit(Reg("XAlarmClear"), 0, true);
            Log("[PLC] X报警清除");
            var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            timer.Tick += (_, _) => { WriteBit(Reg("XAlarmClear"), 0, false); timer.Stop(); };
            timer.Start();
        }

        private void BtnYAlarmClear_Click(object sender, RoutedEventArgs e)
        {
            if (!CheckPlcConnected()) return;
            WriteBit(Reg("YAlarmClear"), 0, true);
            Log("[PLC] Y报警清除");
            var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            timer.Tick += (_, _) => { WriteBit(Reg("YAlarmClear"), 0, false); timer.Stop(); };
            timer.Start();
        }

        private void BtnZAlarmClear_Click(object sender, RoutedEventArgs e)
        {
            if (!CheckPlcConnected()) return;
            WriteBit(Reg("ZAlarmClear"), 0, true);
            Log("[PLC] Z报警清除");
            var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            timer.Tick += (_, _) => { WriteBit(Reg("ZAlarmClear"), 0, false); timer.Stop(); };
            timer.Start();
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
            _readTimer.Tick += (_, _) => AutoRefreshPlcCycle();
            _readTimer.Start();
            Log($"[PLC] 自动读取已启动（XYZ + GVAR 3D），间隔 {ms}ms");
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

                ClosePlcConnections();

                byte station = (byte)(_config?.ModbusStation ?? 0);
                string series = _config?.GvarList?.PlcSeries ?? "XD";

                string floatFmt = PlcXinjeHelper.ResolveFloatDataFormatString(_config);
                var dataFormat = PlcXinjeHelper.ParseFloatDataFormat(floatFmt);

                _plcHd = new ModbusTcpNet(ip, port);
                _plcHd.Station = station;
                _plcHd.DataFormat = dataFormat;
                var hdConn = _plcHd.ConnectServer();

                _plcD = PlcXinjeHelper.CreateClient(series, ip, port, station, floatFmt);
                var dConn = _plcD.ConnectServer();

                if (hdConn.IsSuccess && dConn.IsSuccess)
                {
                    _plcConnected = true;
                    PlcXinjeSession.Register(_plcD, "PlcPage");
                    Log($"[PLC] Connected {ip}:{port} (D=XinJE/{series}, HD=Modbus offset={HdModbusOffset}, float={floatFmt})");
                    BtnPlcConnect.IsEnabled = false;
                    BtnPlcDisconnect.IsEnabled = true;
                    TxtPlcIp.IsEnabled = false;
                    TxtPlcPort.IsEnabled = false;
                    TxtPlcStatus.Text = "[已连接]";
                    TxtPlcStatus.Foreground = new SolidColorBrush(Colors.Green);
                    _lastAxisX = _lastAxisY = _lastAxisZ = double.NaN;
                    UpdatePositionDisplay(double.NaN, double.NaN, double.NaN, double.NaN);
                    _gvar3D?.ClearScene();
                }
                else
                {
                    ClosePlcConnections();
                    string err = !hdConn.IsSuccess ? $"HD: {hdConn.Message}" : $"D: {dConn.Message}";
                    Log($"[PLC] Failed: {err}");
                    MessageBox.Show($"PLC connection failed!\n\n{err}", "PLC Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            catch (Exception ex)
            {
                ClosePlcConnections();
                Log($"[PLC] Error: {ex.Message}");
                MessageBox.Show(ex.Message, "PLC Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void ClosePlcConnections()
        {
            if (_plcHd != null)
            {
                try { if (_plcConnected) _plcHd.ConnectClose(); } catch { }
                _plcHd = null;
            }
            if (_plcD != null)
            {
                PlcXinjeSession.ClearIfOwnedBy(_plcD);
                try { if (_plcConnected) _plcD.ConnectClose(); } catch { }
                _plcD = null;
            }
            _plcConnected = false;
        }

        private void BtnPlcDisconnect_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Log("[PLC] Disconnecting...");

                _readTimer?.Stop();
                _readTimer = null;
                ChkAutoRead.IsChecked = false;

                ClosePlcConnections();

                BtnPlcConnect.IsEnabled = true;
                BtnPlcDisconnect.IsEnabled = false;
                TxtPlcIp.IsEnabled = true;
                TxtPlcPort.IsEnabled = true;
                TxtPlcStatus.Text = "[未连接]";
                TxtPlcStatus.Foreground = new SolidColorBrush(Colors.Gray);
                _lastAxisX = _lastAxisY = _lastAxisZ = double.NaN;
                UpdatePositionDisplay(double.NaN, double.NaN, double.NaN, double.NaN);
                _gvar3D?.ClearScene();
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
        /// <summary>HD / D 区 float 字节序（Hsl DataFormat），默认 CDAB。</summary>
        public string FloatDataFormat { get; set; } = "CDAB";
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
            /// <summary>信捷系列：XC / XD(XD5/XL)；D30000 等请用 XD。仍不对时在 ModbusStartAddress 填 PLC 文档地址。</summary>
            public string PlcSeries { get; set; } = XinjePlcAddress.Series.XD;
            /// <summary>强制 Modbus 起始字地址；-1 表示按 StartAddress 换算。</summary>
            public int ModbusStartAddress { get; set; } = -1;
            /// <summary>GVAR 浮点字节序；未填则用 PlcConfig.FloatDataFormat。</summary>
            public string FloatDataFormat { get; set; } = "CDAB";
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
            regs[offset + 1] = 0; // pad
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
            // CDAB（与 Hsl DataFormat.CDAB / plc_config FloatDataFormat 一致）
            regs[idx] = (ushort)((bytes[1] << 8) | bytes[0]);
            regs[idx + 1] = (ushort)((bytes[3] << 8) | bytes[2]);
        }
    }
}
