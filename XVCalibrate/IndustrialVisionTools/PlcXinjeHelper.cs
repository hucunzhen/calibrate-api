using System;
using System.IO;
using HslCommunication.Core;
using HslCommunication.Profinet.XINJE;

namespace CalibOperatorCLI_Example
{
    /// <summary>
    /// 信捷 D 区与 XinJETcpNet（HD 仍用 ModbusTcpNet + HdModbusOffset，见 XinjePlcAddress.HdToModbus）。
    /// </summary>
    internal static class PlcXinjeHelper
    {
        public static XinJESeries ParseSeries(string? plcSeries)
        {
            string s = (plcSeries ?? "XD").Trim().ToUpperInvariant();
            return s switch
            {
                "XC" => XinJESeries.XC,
                "XL" => XinJESeries.XL,
                _ => XinJESeries.XD
            };
        }

        /// <summary>运行前确保 HslCommunication 已从 exe 目录加载（否则 PLC 算子会 FileNotFound）。</summary>
        public static void EnsureHslLoaded()
        {
            try
            {
                _ = typeof(XinJETcpNet).Assembly;
            }
            catch (Exception ex)
            {
                string dir = AppContext.BaseDirectory;
                string hslPath = Path.Combine(dir, "HslCommunication.dll");
                throw new InvalidOperationException(
                    "无法加载信捷 PLC 库 HslCommunication。请关闭「工业视觉工具」后重新生成（Rebuild），" +
                    "并确认与 IndustrialVisionTools.exe 同目录存在 HslCommunication.dll。\r\n" +
                    $"程序目录: {dir}\r\nHslCommunication.dll 存在: {File.Exists(hslPath)}\r\n" +
                    $"原始错误: {ex.Message}", ex);
            }
        }

        public static DataFormat ParseFloatDataFormat(string? format)
        {
            string s = (format ?? "CDAB").Trim().ToUpperInvariant();
            return s switch
            {
                "ABCD" => DataFormat.ABCD,
                "BADC" => DataFormat.BADC,
                "DCBA" => DataFormat.DCBA,
                _ => DataFormat.CDAB
            };
        }

        /// <summary>plc_config 根级 FloatDataFormat，否则 GvarList.FloatDataFormat。</summary>
        public static string ResolveFloatDataFormatString(PlcConfig? cfg)
        {
            if (!string.IsNullOrWhiteSpace(cfg?.FloatDataFormat))
                return cfg.FloatDataFormat.Trim();
            if (!string.IsNullOrWhiteSpace(cfg?.GvarList?.FloatDataFormat))
                return cfg.GvarList.FloatDataFormat.Trim();
            return "CDAB";
        }

        public static XinJETcpNet CreateClient(string? plcSeries, string ip, int port, byte station, string? floatDataFormat = null)
        {
            EnsureHslLoaded();
            var plc = new XinJETcpNet(ParseSeries(plcSeries), ip, port, station);
            plc.DataFormat = ParseFloatDataFormat(floatDataFormat);
            return plc;
        }

        /// <summary>D803L / D803H → (D803, 位偏移)。</summary>
        public static string NormalizeWordAddress(string xinjeAddr, out int extraBitOffset)
        {
            string s = (xinjeAddr ?? "").Trim().ToUpperInvariant();
            extraBitOffset = 0;
            if (s.EndsWith('L'))
            {
                extraBitOffset = 0;
                s = s[..^1];
            }
            else if (s.EndsWith('H'))
            {
                extraBitOffset = 8;
                s = s[..^1];
            }

            return s;
        }

        /// <summary>D 区起始地址 + 字偏移，如 D30000 + 28 → D30028。</summary>
        public static string OffsetDAddress(string startXinje, int wordOffset)
        {
            string s = (startXinje ?? "").Trim().ToUpperInvariant();
            if (wordOffset == 0)
                return s;
            if (s.StartsWith("D", StringComparison.Ordinal) &&
                int.TryParse(s[1..], System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture, out int d))
                return $"D{d + wordOffset}";
            return s;
        }

        public static string ResolveGvarStartAddress(string? paramLabel, string? configStart)
        {
            if (!string.IsNullOrWhiteSpace(paramLabel))
                return paramLabel.Trim();
            if (!string.IsNullOrWhiteSpace(configStart))
                return configStart.Trim();
            return "D30000";
        }
    }
}
