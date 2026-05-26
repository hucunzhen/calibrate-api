using System;

#if CONTROLLER_SDK_ENABLED
using Sdk = ControllerDllCSharp.ClassLibControllerDll;
#endif

namespace CalibOperatorCLI_Example
{
    /// <summary>光源控制器 SDK 连接会话（IP / 串口）。</summary>
    internal sealed class ControllerSdkSession : IDisposable
    {
#if CONTROLLER_SDK_ENABLED
        public const int ConnectEthernet = Sdk.EthernetMode;
        public const int ConnectRs232 = Sdk.Rs232Mode;
#else
        public const int ConnectEthernet = 1;
        public const int ConnectRs232 = 0;
#endif

        public static bool SdkPresent =>
#if CONTROLLER_SDK_ENABLED
            ControllerSdkNativeLoader.IsReady;
#else
            false;
#endif

#if CONTROLLER_SDK_ENABLED
        public int ConnectType { get; private set; } = ConnectEthernet;
        public long Handle { get; private set; }
        public bool IsConnected => Handle != 0;

        public string? LastIp { get; private set; }
        public int? LastComPort { get; private set; }

        public int ConnectIp(string ip, int timeoutSeconds = 1)
        {
            EnsureSdk();
            Disconnect();
            long handle = 0;
            int rc = Sdk.ConnectIP(ip, timeoutSeconds, ref handle);
            if (!ControllerSdkMarshaling.IsSuccess(rc))
                throw new InvalidOperationException($"IP 连接失败: {ControllerSdkMarshaling.FormatError(rc)}");

            Handle = handle;
            ConnectType = ConnectEthernet;
            LastIp = ip;
            LastComPort = null;
            return rc;
        }

        public void DisconnectIp()
        {
            if (Handle == 0 || ConnectType != ConnectEthernet)
                return;

            int rc = Sdk.DestroyIpConnection(Handle);
            Handle = 0;
            if (!ControllerSdkMarshaling.IsSuccess(rc))
                throw new InvalidOperationException($"断开 IP 失败: {ControllerSdkMarshaling.FormatError(rc)}");
        }

        public int ConnectSerial(int comPort)
        {
            EnsureSdk();
            Disconnect();
            long handle = 0;
            int rc = Sdk.CreateSerialPort(comPort, ref handle);
            if (!ControllerSdkMarshaling.IsSuccess(rc))
                throw new InvalidOperationException($"打开串口 COM{comPort} 失败: {ControllerSdkMarshaling.FormatError(rc)}");

            Handle = handle;
            ConnectType = ConnectRs232;
            LastComPort = comPort;
            LastIp = null;
            return rc;
        }

        public void DisconnectSerial()
        {
            if (Handle == 0 || ConnectType != ConnectRs232)
                return;

            int rc = Sdk.ReleaseSerialPort(Handle);
            Handle = 0;
            if (!ControllerSdkMarshaling.IsSuccess(rc))
                throw new InvalidOperationException($"关闭串口失败: {ControllerSdkMarshaling.FormatError(rc)}");
        }

        public void Disconnect()
        {
            if (Handle == 0)
                return;

            if (ConnectType == ConnectEthernet)
                DisconnectIp();
            else
                DisconnectSerial();
        }

        public int GetDigitalValue(int channel, out int value)
        {
            EnsureConnected();
            value = 0;
            int rc = Sdk.GetDigitalValue(ConnectType, ref value, channel, Handle);
            if (!ControllerSdkMarshaling.IsSuccess(rc))
                throw new InvalidOperationException($"读取亮度失败: {ControllerSdkMarshaling.FormatError(rc)}");
            return rc;
        }

        public int SetDigitalValue(int channel, int value)
        {
            EnsureConnected();
            int rc = Sdk.SetDigitalValue(ConnectType, channel, value, Handle);
            if (!ControllerSdkMarshaling.IsSuccess(rc))
                throw new InvalidOperationException($"设置亮度失败: {ControllerSdkMarshaling.FormatError(rc)}");
            return rc;
        }

        public int GetStrobeValue(int channel, out int value)
        {
            EnsureConnected();
            value = 0;
            int rc = Sdk.GetStrobeValue(ConnectType, ref value, channel, Handle);
            if (!ControllerSdkMarshaling.IsSuccess(rc))
                throw new InvalidOperationException($"读取脉宽失败: {ControllerSdkMarshaling.FormatError(rc)}");
            return rc;
        }

        public int SetStrobeValue(int channel, int value)
        {
            EnsureConnected();
            int rc = Sdk.SetStrobeValue(ConnectType, channel, value, Handle);
            if (!ControllerSdkMarshaling.IsSuccess(rc))
                throw new InvalidOperationException($"设置脉宽失败: {ControllerSdkMarshaling.FormatError(rc)}");
            return rc;
        }

        public int KeepAlive()
        {
            EnsureConnected();
            int rc = Sdk.KeepAlive(ConnectType, Handle);
            if (!ControllerSdkMarshaling.IsSuccess(rc))
                throw new InvalidOperationException($"心跳失败: {ControllerSdkMarshaling.FormatError(rc)}");
            return rc;
        }

        public int GetIntCycle(out int value)
        {
            EnsureConnected();
            value = 0;
            int rc = Sdk.GetIntCycleValue(ConnectType, ref value, Handle);
            if (!ControllerSdkMarshaling.IsSuccess(rc))
                throw new InvalidOperationException($"读取内触发周期失败: {ControllerSdkMarshaling.FormatError(rc)}");
            return rc;
        }

        public int SetIntCycle(int value)
        {
            EnsureConnected();
            int rc = Sdk.SetIntCycleValue(ConnectType, value, Handle);
            if (!ControllerSdkMarshaling.IsSuccess(rc))
                throw new InvalidOperationException($"设置内触发周期失败: {ControllerSdkMarshaling.FormatError(rc)}");
            return rc;
        }

        public int GetLightTriMode(out int value)
        {
            EnsureConnected();
            value = 0;
            int rc = Sdk.GetLightTriMode(ConnectType, ref value, Handle);
            if (!ControllerSdkMarshaling.IsSuccess(rc))
                throw new InvalidOperationException($"读取触发模式失败: {ControllerSdkMarshaling.FormatError(rc)}");
            return rc;
        }

        public int SetLightTriMode(int value)
        {
            EnsureConnected();
            int rc = Sdk.SetLightTriMode(ConnectType, value, Handle);
            if (!ControllerSdkMarshaling.IsSuccess(rc))
                throw new InvalidOperationException($"设置触发模式失败: {ControllerSdkMarshaling.FormatError(rc)}");
            return rc;
        }

        public int GetLightState(out int value)
        {
            EnsureConnected();
            value = 0;
            int rc = Sdk.GetLightState(ConnectType, ref value, Handle);
            if (!ControllerSdkMarshaling.IsSuccess(rc))
                throw new InvalidOperationException($"读取常亮/长灭失败: {ControllerSdkMarshaling.FormatError(rc)}");
            return rc;
        }

        public int SetLightState(int value)
        {
            EnsureConnected();
            int rc = Sdk.SetLightState(ConnectType, value, Handle);
            if (!ControllerSdkMarshaling.IsSuccess(rc))
                throw new InvalidOperationException($"设置常亮/长灭失败: {ControllerSdkMarshaling.FormatError(rc)}");
            return rc;
        }

        public void Dispose() => Disconnect();

        private static void EnsureSdk()
        {
            ControllerSdkNativeLoader.EnsureLoaded();
            if (!SdkPresent)
            {
                string hint = ControllerSdkNativeLoader.LoadDetail
                    ?? "请确认 Controller_SDK-V4.0.0.1(demo)\\SDK\\C#_DLL\\x64 存在，并重新生成（CopyControllerSdkNative）。";
                throw new InvalidOperationException(hint);
            }
        }

        private void EnsureConnected()
        {
            EnsureSdk();
            if (Handle == 0)
                throw new InvalidOperationException("请先连接控制器（IP 或串口）。");
        }
#else
        public int ConnectType { get; private set; }
        public long Handle { get; private set; }
        public bool IsConnected => false;
        public string? LastIp { get; private set; }
        public int? LastComPort { get; private set; }

        public int ConnectIp(string ip, int timeoutSeconds = 1)
            => throw new NotSupportedException("编译时未启用 CONTROLLER_SDK（缺少 ControllerDllCSharp.dll）。");

        public void DisconnectIp() { }
        public int ConnectSerial(int comPort) => throw new NotSupportedException();
        public void DisconnectSerial() { }
        public void Disconnect() { }
        public void Dispose() { }
        public int GetDigitalValue(int channel, out int value) { value = 0; throw new NotSupportedException(); }
        public int SetDigitalValue(int channel, int value) => throw new NotSupportedException();
        public int GetStrobeValue(int channel, out int value) { value = 0; throw new NotSupportedException(); }
        public int SetStrobeValue(int channel, int value) => throw new NotSupportedException();
        public int KeepAlive() => throw new NotSupportedException();
        public int GetIntCycle(out int value) { value = 0; throw new NotSupportedException(); }
        public int SetIntCycle(int value) => throw new NotSupportedException();
        public int GetLightTriMode(out int value) { value = 0; throw new NotSupportedException(); }
        public int SetLightTriMode(int value) => throw new NotSupportedException();
        public int GetLightState(out int value) { value = 0; throw new NotSupportedException(); }
        public int SetLightState(int value) => throw new NotSupportedException();
#endif
    }
}
