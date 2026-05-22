using System;

namespace CalibOperatorCLI_Example
{
    /// <summary>
    /// HD → Modbus 字地址（HdModbusOffset + 编号）。D 区请用 XinJETcpNet 信捷地址字符串，不在此换算。
    /// </summary>
    internal static class XinjePlcAddress
    {
        internal static class Series
        {
            public const string XC = "XC";
            /// <summary>XD / XD5 / XL / XDH 等大容量 D 区（D0~D20479 线性，≥20480 映射到 0x7000 段）。</summary>
            public const string XD = "XD";
            public const string XD5 = "XD5";
            public const string Linear = "Linear";
        }

        /// <summary>XC：D8000~D8511 → Modbus 0x4000 段。</summary>
        private const int D8000ModbusBase = 0x4000;

        /// <summary>XD/XL：D20480+ → SD 等扩展区，Modbus 从 0x7000 起（与 Hsl CriticalAddress=20480 一致）。</summary>
        private const int ExtendedModbusBase = 0x7000;

        private const int XcD8000PlcStart = 8000;
        private const int XcD8000PlcEnd = 8511;
        private const int XdLinearDEnd = 20479;
        private const int XdExtendedDStart = 20480;

        /// <summary>HD 地址 → Modbus 字地址；非 HD 返回 -1。</summary>
        public static int HdToModbus(string xinjeAddr, int hdModbusOffset = 0xA080)
        {
            string s = (xinjeAddr ?? "").Trim().ToUpperInvariant();
            if (s.EndsWith('L') || s.EndsWith('H'))
                s = s[..^1];
            if (s.StartsWith("HD", StringComparison.Ordinal) &&
                int.TryParse(s[2..], System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture, out int hdIdx))
                return hdModbusOffset + hdIdx;
            return -1;
        }

        public static bool IsHdAddress(string xinjeAddr)
        {
            string s = (xinjeAddr ?? "").Trim().ToUpperInvariant();
            if (s.EndsWith('L') || s.EndsWith('H'))
                s = s[..^1];
            return s.StartsWith("HD", StringComparison.Ordinal);
        }

        public static bool IsDAddress(string xinjeAddr)
        {
            string s = (xinjeAddr ?? "").Trim().ToUpperInvariant();
            if (s.EndsWith('L') || s.EndsWith('H'))
                s = s[..^1];
            return s.StartsWith("D", StringComparison.Ordinal) && !s.StartsWith("HD", StringComparison.Ordinal);
        }

        /// <summary>信捷地址 → Modbus（仅 HD/M 数字；D 请走 XinJETcpNet）。</summary>
        public static int ToModbus(string xinjeAddr, int hdModbusOffset = 0xA080, string? plcSeries = null)
        {
            string s = (xinjeAddr ?? "").Trim().ToUpperInvariant();
            if (string.IsNullOrEmpty(s))
                return -1;

            if (s.EndsWith('L'))
                s = s[..^1];
            else if (s.EndsWith('H'))
                s = s[..^1];

            if (s.StartsWith("HD", StringComparison.Ordinal) &&
                int.TryParse(s[2..], System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture, out int hdIdx))
                return hdModbusOffset + hdIdx;

            if (s.StartsWith("D", StringComparison.Ordinal) &&
                int.TryParse(s[1..], System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture, out int dIdx))
                return DToModbus(dIdx, plcSeries);

            if (int.TryParse(s, System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out int n))
                return n;

            return -1;
        }

        /// <summary>仅 D 编号 → Modbus（不含 HD/M）。</summary>
        public static int DToModbus(int dIndex, string? plcSeries = null)
        {
            if (dIndex < 0)
                return -1;

            string series = NormalizeSeries(plcSeries);
            if (series == Series.Linear)
                return dIndex;

            if (IsXdFamily(series))
                return DToModbusXd(dIndex);

            return DToModbusXc(dIndex);
        }

        private static string NormalizeSeries(string? plcSeries)
        {
            string s = (plcSeries ?? Series.XD).Trim().ToUpperInvariant();
            if (s == Series.XD5 || s == "XL" || s == "XL5" || s == "XDH" || s == "XLH" || s == "XDM")
                return Series.XD;
            return s;
        }

        private static bool IsXdFamily(string series)
            => series == Series.XD || series == Series.XD5;

        /// <summary>XC：D0~7999 直映；D8000~8511→0x4000 段；D≥20480→0x7000+(D-20480)。</summary>
        private static int DToModbusXc(int dIndex)
        {
            if (dIndex <= 7999)
                return dIndex;
            if (dIndex >= XcD8000PlcStart && dIndex <= XcD8000PlcEnd)
                return D8000ModbusBase + (dIndex - XcD8000PlcStart);
            if (dIndex >= XdExtendedDStart)
                return ExtendedModbusBase + (dIndex - XdExtendedDStart);
            return dIndex;
        }

        /// <summary>XD/XL：D0~20479 与 PLC 号相同；D20480+ 映射到 Modbus 0x7000 段（D30000→38192）。</summary>
        private static int DToModbusXd(int dIndex)
        {
            if (dIndex <= XdLinearDEnd)
                return dIndex;
            if (dIndex >= XdExtendedDStart)
                return ExtendedModbusBase + (dIndex - XdExtendedDStart);
            return dIndex;
        }

        public static string FormatMapping(string xinjeAddr, int modbusAddr, int hdModbusOffset = 0xA080, string? plcSeries = null)
        {
            if (modbusAddr < 0)
                return $"{xinjeAddr}→无效";
            int expected = ToModbus(xinjeAddr, hdModbusOffset, plcSeries);
            if (expected == modbusAddr)
                return $"{xinjeAddr}→Modbus {modbusAddr}";
            return $"{xinjeAddr}→Modbus {modbusAddr} (校验={expected})";
        }
    }
}
