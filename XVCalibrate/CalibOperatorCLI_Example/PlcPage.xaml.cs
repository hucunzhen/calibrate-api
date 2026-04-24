using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using CalibOperatorPInvoke;
using HslCommunication.Profinet.XINJE;

namespace CalibOperatorCLI_Example
{
    public partial class PlcPage : Page
    {
        private XinJETcpNet? _plc;
        private bool _plcConnected = false;

        public PlcPage()
        {
            InitializeComponent();
            Log("PLC Communication Page Loaded");
        }

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

                _plc = new XinJETcpNet();
                _plc.IpAddress = ip;
                _plc.Port = port;

                var result = _plc.ConnectServer();
                if (result.IsSuccess)
                {
                    _plcConnected = true;
                    Log($"[PLC] Connected to {ip}:{port}");
                    BtnPlcConnect.IsEnabled = false;
                    BtnPlcDisconnect.IsEnabled = true;
                    TxtPlcIp.IsEnabled = false;
                    TxtPlcPort.IsEnabled = false;
                    TxtPlcStatus.Text = "[Connected]";
                    TxtPlcStatus.Foreground = new SolidColorBrush(Colors.Green);
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
                Log("[PLC] Disconnected");
            }
            catch (Exception ex)
            {
                Log($"[PLC] Error: {ex.Message}");
                MessageBox.Show(ex.Message, "PLC Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }
}
