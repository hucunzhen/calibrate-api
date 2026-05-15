using System;
using System.Text.Json;

namespace CalibOperatorPInvoke
{
    /// <summary>
    /// 固定相机：平面坐标系 (Xp,Yp,Zp) 到手眼标定得到的机器人基座系 (Xb,Yb,Zb)。
    /// JSON 中 4×4 为行主序，与列向量乘法一致： [xb,yb,zb,wb]^T = M * [xp,yp,zp,1]^T。
    /// </summary>
    public static class HandEyePlaneToBaseTransform
    {
        /// <summary>
        /// 从 JSON 解析 4×4。支持 baseFromPlaneRowMajor（长度 16 的一维数组）或 baseFromPlane（4×4 二维数组）。
        /// </summary>
        public static double[] ParseBaseFromPlaneMatrix16(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
                throw new ArgumentException("手眼 JSON 为空。", nameof(json));

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (TryReadRowMajor16(root, "baseFromPlaneRowMajor", out var m))
                return m;
            if (TryReadRowMajor16(root, "BaseFromPlaneRowMajor", out m))
                return m;
            if (TryReadMatrix4x4(root, "baseFromPlane", out m))
                return m;
            if (TryReadMatrix4x4(root, "BaseFromPlane", out m))
                return m;

            throw new InvalidOperationException(
                "手眼 JSON 需包含 baseFromPlaneRowMajor（16 个数字的一维数组）或 baseFromPlane（4×4 二维数组）。");
        }

        public static CalibPoint3D TransformPoint(double[] m16, double xp, double yp, double zp)
        {
            if (m16 == null || m16.Length != 16)
                throw new ArgumentException("矩阵须为长度 16 的行主序数组。", nameof(m16));

            double xb = m16[0] * xp + m16[1] * yp + m16[2] * zp + m16[3];
            double yb = m16[4] * xp + m16[5] * yp + m16[6] * zp + m16[7];
            double zb = m16[8] * xp + m16[9] * yp + m16[10] * zp + m16[11];
            double wb = m16[12] * xp + m16[13] * yp + m16[14] * zp + m16[15];
            if (Math.Abs(wb) > 1e-9 && Math.Abs(wb - 1.0) > 1e-6)
            {
                xb /= wb;
                yb /= wb;
                zb /= wb;
            }

            return new CalibPoint3D(xb, yb, zb);
        }

        private static bool TryReadRowMajor16(JsonElement root, string propertyName, out double[] m)
        {
            m = Array.Empty<double>();
            if (!root.TryGetProperty(propertyName, out var arr) || arr.ValueKind != JsonValueKind.Array)
                return false;
            int n = arr.GetArrayLength();
            if (n != 16)
                throw new InvalidOperationException($"{propertyName} 须恰好包含 16 个数字（行主序 4×4）。");
            m = new double[16];
            int i = 0;
            foreach (var el in arr.EnumerateArray())
            {
                if (!el.TryGetDouble(out m[i]))
                    throw new InvalidOperationException($"{propertyName}[{i}] 不是有效数字。");
                i++;
            }

            return true;
        }

        private static bool TryReadMatrix4x4(JsonElement root, string propertyName, out double[] m)
        {
            m = Array.Empty<double>();
            if (!root.TryGetProperty(propertyName, out var rows) || rows.ValueKind != JsonValueKind.Array)
                return false;
            if (rows.GetArrayLength() != 4)
                throw new InvalidOperationException($"{propertyName} 须为 4 行。");
            m = new double[16];
            int r = 0;
            foreach (var row in rows.EnumerateArray())
            {
                if (row.ValueKind != JsonValueKind.Array || row.GetArrayLength() != 4)
                    throw new InvalidOperationException($"{propertyName} 每行须为 4 个数字。");
                int c = 0;
                foreach (var el in row.EnumerateArray())
                {
                    if (!el.TryGetDouble(out m[r * 4 + c]))
                        throw new InvalidOperationException($"{propertyName}[{r},{c}] 不是有效数字。");
                    c++;
                }

                r++;
            }

            return true;
        }
    }
}
