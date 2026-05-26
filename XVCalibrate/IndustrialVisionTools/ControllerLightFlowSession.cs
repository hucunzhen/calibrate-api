using System;
using System.Collections.Generic;
using System.Globalization;

namespace CalibOperatorCLI_Example
{
    /// <summary>流程内共享的光源控制器连接（与 PLC 页、光源页独立，供 flow 算子使用）。</summary>
    internal static class ControllerLightFlowSession
    {
        private static ControllerSdkSession? _session;

        public static ControllerSdkSession Session => _session ??= new ControllerSdkSession();

        public static bool IsConnected => _session?.IsConnected ?? false;

        public static void Disconnect()
        {
            _session?.Disconnect();
        }

        public static void EnsureSdkAvailable()
        {
            ControllerSdkNativeLoader.EnsureLoaded();
            if (!ControllerSdkSession.SdkPresent)
            {
                throw new InvalidOperationException(
                    ControllerSdkNativeLoader.LoadDetail
                    ?? "光源控制器 SDK 不可用。请重新生成 IndustrialVisionTools（x64）并确认 Controller_SDK DLL 在 exe 目录。");
            }
        }

        public static void ConnectFromNodeParams(IReadOnlyDictionary<string, string> parameters)
        {
            EnsureSdkAvailable();
            var session = Session;
            if (session.IsConnected)
                session.Disconnect();

            string mode = (parameters.GetValueOrDefault("connectMode", "ip") ?? "ip").Trim().ToLowerInvariant();
            if (mode is "serial" or "rs232" or "com" or "串口")
            {
                int com = ParseInt(parameters.GetValueOrDefault("comPort"), 1);
                session.ConnectSerial(com);
                return;
            }

            string ip = (parameters.GetValueOrDefault("ip", "192.168.0.100") ?? "192.168.0.100").Trim();
            int timeout = Math.Clamp(ParseInt(parameters.GetValueOrDefault("timeoutSec"), 1), 1, 30);
            session.ConnectIp(ip, timeout);
        }

        public static void EnsureConnected(IReadOnlyDictionary<string, string> parameters)
        {
            EnsureSdkAvailable();
            if (!Session.IsConnected)
                ConnectFromNodeParams(parameters);
        }

        private static int ParseInt(string? raw, int fallback)
            => int.TryParse(raw?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : fallback;
    }
}
