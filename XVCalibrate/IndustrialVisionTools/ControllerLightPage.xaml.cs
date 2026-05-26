using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;

namespace CalibOperatorCLI_Example
{
    public partial class ControllerLightPage : Page
    {
        private readonly ControllerSdkSession _session = new();
        private List<AdapterInfo> _adapters = new();
        private List<HostInfo> _hosts = new();

        public ControllerLightPage()
        {
            InitializeComponent();
            Loaded += OnLoaded;
            Unloaded += (_, _) => _session.Dispose();
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            ControllerSdkNativeLoader.EnsureLoaded();
            if (ControllerSdkSession.SdkPresent)
            {
                BorderSdkMissing.Visibility = Visibility.Collapsed;
                ScrollMain.Visibility = Visibility.Visible;
                Log(ControllerSdkNativeLoader.LoadDetail ?? "Controller_SDK 已加载。");
            }
            else
            {
                BorderSdkMissing.Visibility = Visibility.Visible;
                ScrollMain.Visibility = Visibility.Collapsed;
                TxtSdkHint.Text = ControllerSdkNativeLoader.LoadDetail
                    ?? "未找到 Controller_SDK 原生 DLL。请重新生成 IndustrialVisionTools（x64）。";
            }
        }

        private void BtnEnumAdapter_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                _adapters = ControllerSdkMarshaling.EnumerateAdapters();
                CmbAdapterSn.Items.Clear();
                CmbAdapterIp.Items.Clear();
                foreach (AdapterInfo a in _adapters)
                {
                    CmbAdapterSn.Items.Add(a.Sn);
                    CmbAdapterIp.Items.Add(a.Ip);
                }

                if (_adapters.Count > 0)
                {
                    CmbAdapterSn.SelectedIndex = 0;
                    CmbAdapterIp.SelectedIndex = 0;
                }

                Log($"枚举网卡: {_adapters.Count} 个");
            }
            catch (Exception ex)
            {
                LogError("枚举网卡", ex);
            }
        }

        private void BtnSearchHost_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string adapterIp = CmbAdapterIp.Text?.Trim() ?? "";
                if (string.IsNullOrEmpty(adapterIp))
                {
                    Log("请先选择网卡 IP。");
                    return;
                }

                _hosts = ControllerSdkMarshaling.EnumerateHosts(adapterIp);
                CmbHostSn.Items.Clear();
                foreach (HostInfo h in _hosts)
                    CmbHostSn.Items.Add(h.Sn);

                if (_hosts.Count > 0)
                {
                    CmbHostSn.SelectedIndex = 0;
                    LoadHostConfig(0);
                }
                else
                {
                    Log($"网卡 {adapterIp} 下未发现控制器。");
                }

                Log($"搜索控制器: {_hosts.Count} 台（网卡 {adapterIp}）");
            }
            catch (Exception ex)
            {
                LogError("搜索控制器", ex);
            }
        }

        private void CmbAdapterSn_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            int i = CmbAdapterSn.SelectedIndex;
            if (i >= 0 && i < CmbAdapterIp.Items.Count)
                CmbAdapterIp.SelectedIndex = i;
        }

        private void CmbAdapterIp_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            int i = CmbAdapterIp.SelectedIndex;
            if (i >= 0 && i < CmbAdapterSn.Items.Count)
                CmbAdapterSn.SelectedIndex = i;
        }

        private void CmbHostSn_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            int i = CmbHostSn.SelectedIndex;
            if (i >= 0)
                LoadHostConfig(i);
        }

        private void LoadHostConfig(int hostIndex)
        {
            if (hostIndex < 0 || hostIndex >= _hosts.Count)
                return;

            string adapterIp = CmbAdapterIp.Text?.Trim() ?? "";
            var cfg = ControllerSdkMarshaling.TryGetConfigure(_hosts[hostIndex].Mac, adapterIp);
            if (cfg == null)
            {
                Log("读取控制器网络参数失败。");
                return;
            }

            TxtCfgIp.Text = cfg.Ip;
            TxtCfgSm.Text = cfg.SubnetMask;
            TxtCfgGw.Text = cfg.Gateway;
            TxtCfgSn.Text = cfg.Sn;
            ChkCfgDhcp.IsChecked = cfg.Dhcp;
            TxtConnectIp.Text = cfg.Ip;
            ApplyDhcpUi(cfg.Dhcp);
            Log($"已加载控制器 {cfg.Sn} 网络参数。");
        }

        private void ChkCfgDhcp_Changed(object sender, RoutedEventArgs e)
            => ApplyDhcpUi(ChkCfgDhcp.IsChecked == true);

        private void ApplyDhcpUi(bool dhcp)
        {
            bool manual = !dhcp;
            TxtCfgIp.IsEnabled = manual;
            TxtCfgSm.IsEnabled = manual;
            TxtCfgGw.IsEnabled = manual;
            TxtCfgSn.IsEnabled = manual;
        }

        private void BtnSetConfigure_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                int i = CmbHostSn.SelectedIndex;
                if (i < 0 || i >= _hosts.Count)
                {
                    Log("请先选择要配置的控制器。");
                    return;
                }

                var cfg = new ControllerNetConfig(
                    TxtCfgSn.Text.Trim(),
                    TxtCfgIp.Text.Trim(),
                    TxtCfgSm.Text.Trim(),
                    TxtCfgGw.Text.Trim(),
                    ChkCfgDhcp.IsChecked == true);

                string adapterIp = CmbAdapterIp.Text?.Trim() ?? "";
                int rc = ControllerSdkMarshaling.TrySetConfigure(_hosts[i].Mac, cfg, adapterIp);
                if (ControllerSdkMarshaling.IsSuccess(rc))
                    Log("写入控制器网络参数成功。");
                else
                    Log($"写入失败: {ControllerSdkMarshaling.FormatError(rc)}");
            }
            catch (Exception ex)
            {
                LogError("设置网络", ex);
            }
        }

        private void BtnConnectIp_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string ip = TxtConnectIp.Text.Trim();
                if (string.IsNullOrEmpty(ip))
                {
                    Log("请输入 IP。");
                    return;
                }

                int timeout = ParseInt(TxtConnectTimeout.Text, 1);
                _session.ConnectIp(ip, timeout);
                UpdateConnStatus();
                Log($"IP 已连接 {ip}（超时 {timeout}s）");
            }
            catch (Exception ex)
            {
                LogError("IP 连接", ex);
                UpdateConnStatus();
            }
        }

        private void BtnConnectCom_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                int com = ParseInt(TxtComPort.Text, 1);
                _session.ConnectSerial(com);
                UpdateConnStatus();
                Log($"串口 COM{com} 已连接");
            }
            catch (Exception ex)
            {
                LogError("串口连接", ex);
                UpdateConnStatus();
            }
        }

        private void BtnDisconnect_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                _session.Disconnect();
                UpdateConnStatus();
                Log("已断开连接。");
            }
            catch (Exception ex)
            {
                LogError("断开", ex);
            }
        }

        private void BtnKeepAlive_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                _session.KeepAlive();
                Log("心跳成功。");
            }
            catch (Exception ex)
            {
                LogError("心跳", ex);
            }
        }

        private void BtnGetIntensity_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                int ch = ParseInt(TxtChannel.Text, 1);
                _session.GetDigitalValue(ch, out int v);
                TxtIntensity.Text = v.ToString(CultureInfo.InvariantCulture);
                Log($"通道 {ch} 亮度 = {v}");
            }
            catch (Exception ex) { LogError("读亮度", ex); }
        }

        private void BtnSetIntensity_Click(object sender, RoutedEventArgs e) => RunChannelWrite(
            (ch, v) => _session.SetDigitalValue(ch, v),
            TxtIntensity.Text,
            "亮度");

        private void BtnGetStrobe_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                int ch = ParseInt(TxtChannel.Text, 1);
                _session.GetStrobeValue(ch, out int v);
                TxtStrobe.Text = v.ToString(CultureInfo.InvariantCulture);
                Log($"通道 {ch} 脉宽 = {v}");
            }
            catch (Exception ex) { LogError("读脉宽", ex); }
        }

        private void BtnSetStrobe_Click(object sender, RoutedEventArgs e) => RunChannelWrite(
            (ch, v) => _session.SetStrobeValue(ch, v),
            TxtStrobe.Text,
            "脉宽");

        private void BtnGetIntCycle_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                _session.GetIntCycle(out int v);
                TxtIntCycle.Text = v.ToString(CultureInfo.InvariantCulture);
                Log($"内触发周期 = {v}");
            }
            catch (Exception ex) { LogError("读内触发周期", ex); }
        }

        private void BtnSetIntCycle_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                int v = ParseInt(TxtIntCycle.Text, 0);
                _session.SetIntCycle(v);
                Log($"内触发周期 → {v}");
            }
            catch (Exception ex) { LogError("写内触发周期", ex); }
        }

        private void BtnGetTriMode_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                _session.GetLightTriMode(out int v);
                TxtTriMode.Text = v.ToString(CultureInfo.InvariantCulture);
                Log($"内外触发模式 = {v}");
            }
            catch (Exception ex) { LogError("读触发模式", ex); }
        }

        private void BtnSetTriMode_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                int v = ParseInt(TxtTriMode.Text, 0);
                _session.SetLightTriMode(v);
                Log($"内外触发模式 → {v}");
            }
            catch (Exception ex) { LogError("写触发模式", ex); }
        }

        private void BtnGetLightState_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                _session.GetLightState(out int v);
                TxtLightState.Text = v.ToString(CultureInfo.InvariantCulture);
                Log($"常亮/长灭 = {v}");
            }
            catch (Exception ex) { LogError("读常亮/长灭", ex); }
        }

        private void BtnSetLightState_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                int v = ParseInt(TxtLightState.Text, 0);
                _session.SetLightState(v);
                Log($"常亮/长灭 → {v}");
            }
            catch (Exception ex) { LogError("写常亮/长灭", ex); }
        }

        private void RunChannelWrite(Action<int, int> write, string text, string label)
        {
            try
            {
                int ch = ParseInt(TxtChannel.Text, 1);
                int v = ParseInt(text, 0);
                write(ch, v);
                Log($"通道 {ch} {label} → {v}");
            }
            catch (Exception ex)
            {
                LogError($"写{label}", ex);
            }
        }

        private void UpdateConnStatus()
        {
            if (!_session.IsConnected)
            {
                TxtConnStatus.Text = "[未连接]";
                return;
            }

            string mode = _session.ConnectType == ControllerSdkSession.ConnectEthernet ? "以太网" : "RS232";
            string detail = _session.LastIp ?? (_session.LastComPort.HasValue ? $"COM{_session.LastComPort}" : "");
            TxtConnStatus.Text = $"[已连接] {mode} {detail} handle={_session.Handle}";
        }

        private static int ParseInt(string? text, int fallback)
            => int.TryParse(text?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : fallback;

        private void Log(string msg)
        {
            string line = $"[{DateTime.Now:HH:mm:ss}] {msg}";
            TxtLog.AppendText(line + Environment.NewLine);
            TxtLog.ScrollToEnd();
        }

        private void LogError(string action, Exception ex) => Log($"{action} 失败: {ex.Message}");
    }
}
