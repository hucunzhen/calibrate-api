using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

#if CONTROLLER_SDK_ENABLED
using Sdk = ControllerDllCSharp.ClassLibControllerDll;
#endif

namespace CalibOperatorCLI_Example
{
    /// <summary>Controller_SDK-V4.0.0.1 网卡/控制器结构体枚举（与 demo Form1 一致）。</summary>
    internal static class ControllerSdkMarshaling
    {
        public const int MaxAdapterSlots = 16;
        public const int MaxHostSlots = 50;

#if CONTROLLER_SDK_ENABLED
        public static bool IsSuccess(int code) => code == Sdk.SUCCESS;

        public static string FormatError(int code)
        {
            var buf = new byte[256];
            if (Sdk.GetErrMsg(code, buf) == Sdk.SUCCESS)
            {
                string msg = Encoding.Default.GetString(buf).TrimEnd('\0');
                if (!string.IsNullOrWhiteSpace(msg))
                    return $"{code}: {msg}";
            }

            return code.ToString();
        }

        public static List<AdapterInfo> EnumerateAdapters()
        {
            var list = new List<AdapterInfo>();
            int count = 0;
            int size = Marshal.SizeOf(typeof(Sdk.Adapter_prm));
            IntPtr ptr = Marshal.AllocHGlobal(size * MaxAdapterSlots);
            try
            {
                if (!IsSuccess(Sdk.GetAdapter(ref count, ptr)) || count <= 0)
                    return list;

                count = Math.Min(count, MaxAdapterSlots);
                for (int i = 0; i < count; i++)
                {
                    IntPtr itemPtr = ptr + i * size;
                    var prm = Marshal.PtrToStructure<Sdk.Adapter_prm>(itemPtr)!;
                    list.Add(new AdapterInfo(
                        CStr(prm.cSn),
                        CStr(prm.cIp)));
                }
            }
            finally
            {
                Marshal.FreeHGlobal(ptr);
            }

            return list;
        }

        public static List<HostInfo> EnumerateHosts(string adapterIp)
        {
            var list = new List<HostInfo>();
            if (string.IsNullOrWhiteSpace(adapterIp))
                return list;

            int count = 0;
            int size = Marshal.SizeOf(typeof(Sdk.Host_prm));
            IntPtr ptr = Marshal.AllocHGlobal(size * MaxHostSlots);
            try
            {
                if (!IsSuccess(Sdk.GetHost(ref count, ptr, adapterIp)) || count <= 0)
                    return list;

                count = Math.Min(count, MaxHostSlots);
                for (int i = 0; i < count; i++)
                {
                    IntPtr itemPtr = ptr + i * size;
                    var prm = Marshal.PtrToStructure<Sdk.Host_prm>(itemPtr)!;
                    list.Add(new HostInfo(
                        CStr(prm.cSn),
                        CStr(prm.cIp),
                        (byte[])prm.cMac.Clone()));
                }
            }
            finally
            {
                Marshal.FreeHGlobal(ptr);
            }

            return list;
        }

        public static ControllerNetConfig? TryGetConfigure(byte[] mac, string adapterIp)
        {
            if (mac == null || mac.Length < 6)
                return null;

            int size = Marshal.SizeOf(typeof(Sdk.Controller_prm));
            IntPtr ptr = Marshal.AllocHGlobal(size);
            try
            {
                if (!IsSuccess(Sdk.GetConfigure(mac, ptr, adapterIp)))
                    return null;

                var prm = Marshal.PtrToStructure<Sdk.Controller_prm>(ptr)!;
                return new ControllerNetConfig(
                    CStr(prm.cSn),
                    CStr(prm.cIp),
                    CStr(prm.cSm),
                    CStr(prm.cGw),
                    prm.DHCP != 0);
            }
            finally
            {
                Marshal.FreeHGlobal(ptr);
            }
        }

        public static int TrySetConfigure(byte[] mac, ControllerNetConfig cfg, string adapterIp)
        {
            var prm = new Sdk.Controller_prm
            {
                cSn = PadCharArray(cfg.Sn, 21),
                cIp = PadCharArray(cfg.Ip, 16),
                cSm = PadCharArray(cfg.SubnetMask, 16),
                cGw = PadCharArray(cfg.Gateway, 16),
                DHCP = (char)(cfg.Dhcp ? 1 : 0)
            };
            return Sdk.SetConfigure(mac, ref prm, adapterIp);
        }

        private static char[] PadCharArray(string? text, int len)
        {
            var arr = new char[len];
            if (!string.IsNullOrEmpty(text))
            {
                char[] src = text.ToCharArray();
                Array.Copy(src, arr, Math.Min(src.Length, len - 1));
            }

            return arr;
        }

        private static string CStr(char[] chars)
        {
            if (chars == null || chars.Length == 0)
                return string.Empty;
            int n = Array.IndexOf(chars, '\0');
            if (n < 0)
                n = chars.Length;
            return new string(chars, 0, n).Trim();
        }
#else
        public static bool IsSuccess(int code) => false;
        public static string FormatError(int code) => code.ToString();
        public static List<AdapterInfo> EnumerateAdapters() => new();
        public static List<HostInfo> EnumerateHosts(string adapterIp) => new();
        public static ControllerNetConfig? TryGetConfigure(byte[] mac, string adapterIp) => null;
        public static int TrySetConfigure(byte[] mac, ControllerNetConfig cfg, string adapterIp) => -1;
#endif
    }

    internal sealed record AdapterInfo(string Sn, string Ip);

    internal sealed record HostInfo(string Sn, string Ip, byte[] Mac);

    internal sealed record ControllerNetConfig(string Sn, string Ip, string SubnetMask, string Gateway, bool Dhcp);
}
