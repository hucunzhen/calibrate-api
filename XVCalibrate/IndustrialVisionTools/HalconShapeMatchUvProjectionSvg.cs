using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace CalibOperatorCLI_Example
{
    /// <summary>将 Find 点在 u/v 轴上的投影与 8×2 格网导出为 SVG，便于对照日志。</summary>
    internal static class HalconShapeMatchUvProjectionSvg
    {
        public static void Write(
            string path,
            string tag,
            double[] rows,
            double[] cols,
            double[]? scores,
            double[] u,
            double[] v,
            HalconShapeMatchGridFilter.RowColumnLattice lattice,
            int effRows,
            int effCols,
            Dictionary<(int ir, int ic), int> cellBest,
            IReadOnlyList<int> kept,
            double latticeAngleDeg,
            double imageChainColToRowDeg,
            double bootstrapPcaDeg,
            double deltaU,
            double deltaV,
            string? deltaSummary)
        {
            int n = Math.Min(u.Length, v.Length);
            if (n == 0 || lattice.ColU.Length == 0)
                return;

            var pickedSet = new HashSet<int>(kept);
            var idxToCell = cellBest.ToDictionary(kv => kv.Value, kv => kv.Key);
            double uMid = effCols >= 2
                ? 0.5 * (lattice.ColU[0] + lattice.ColU[1])
                : lattice.ColU[0];

            double uMin = lattice.ColU.Min();
            double uMax = lattice.ColU.Max();
            double vMin = lattice.RowV.Min();
            double vMax = lattice.RowV.Max();
            for (int i = 0; i < n; i++)
            {
                uMin = Math.Min(uMin, u[i]);
                uMax = Math.Max(uMax, u[i]);
                vMin = Math.Min(vMin, v[i]);
                vMax = Math.Max(vMax, v[i]);
            }

            double padU = Math.Max(40, 0.12 * (uMax - uMin + 1));
            double padV = Math.Max(40, 0.08 * (vMax - vMin + 1));
            uMin -= padU;
            uMax += padU;
            vMin -= padV;
            vMax += padV;

            const double plotW = 920;
            const double plotH = 720;
            const double imgW = 420;
            const double imgH = 720;
            const double marginL = 90;
            const double marginT = 70;
            const double gap = 28;
            const double imgLeft = marginL + plotW + gap;
            const double marginR = 280;
            const double marginB = 80;
            double sx = plotW / Math.Max(1, uMax - uMin);
            double sy = plotH / Math.Max(1, vMax - vMin);

            double MapX(double uVal) => marginL + (uVal - uMin) * sx;
            double MapY(double vVal) => marginT + (vMax - vVal) * sy;

            var sb = new StringBuilder();
            sb.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
            double totalW = imgLeft + imgW + marginR;
            double totalH = marginT + plotH + marginB;
            sb.Append(CultureInfo.InvariantCulture,
                $"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{totalW}\" height=\"{totalH}\" viewBox=\"0 0 {totalW} {totalH}\">\n");
            sb.AppendLine("<rect width=\"100%\" height=\"100%\" fill=\"#f5f5f5\"/>");
            sb.AppendLine("<style>text{font-family:Segoe UI,Arial,sans-serif;fill:#212121}.axis{font-size:13px;fill:#616161}.title{font-size:17px;font-weight:600}.sub{font-size:12px;fill:#424242}.leg{font-size:12px}</style>");
            sb.AppendLine("<defs><marker id=\"arrU\" markerWidth=\"8\" markerHeight=\"8\" refX=\"6\" refY=\"3\" orient=\"auto\"><path d=\"M0,0 L6,3 L0,6 Z\" fill=\"#1565c0\"/></marker>" +
                          "<marker id=\"arrV\" markerWidth=\"8\" markerHeight=\"8\" refX=\"6\" refY=\"3\" orient=\"auto\"><path d=\"M0,0 L6,3 L0,6 Z\" fill=\"#e65100\"/></marker></defs>");

            string title = string.IsNullOrEmpty(tag) ? "u/v 投影" : tag;
            sb.Append(CultureInfo.InvariantCulture,
                $"<text class=\"title\" x=\"{marginL}\" y=\"28\">{Esc(title)} — u/v 投影</text>\n");
            sb.Append(CultureInfo.InvariantCulture,
                $"<text class=\"sub\" x=\"{marginL}\" y=\"48\">u轴 θ≈{latticeAngleDeg:F1}°（分列）  v轴图像≈{imageChainColToRowDeg:F1}°（链向）  格 {effRows}×{effCols}</text>\n");
            string rowVList = string.Join(", ", lattice.RowV.Select(x => x.ToString("F0", CultureInfo.InvariantCulture)));
            sb.Append(CultureInfo.InvariantCulture,
                $"<text class=\"sub\" x=\"{marginL}\" y=\"64\">ColU=[{string.Join(", ", lattice.ColU.Select(x => x.ToString("F0", CultureInfo.InvariantCulture)))}]  RowV=[{rowVList}]  pitchV≈{lattice.PitchV:F0}</text>\n");
            if (!double.IsNaN(bootstrapPcaDeg))
            {
                sb.Append(CultureInfo.InvariantCulture,
                    $"<text class=\"sub\" x=\"{marginL}\" y=\"80\">PCA链向≈{bootstrapPcaDeg:F1}°（HALCON/点云，≠ 格网 u 轴 θ）  入选 {pickedSet.Count}/{n}</text>\n");
            }
            else
            {
                sb.Append(CultureInfo.InvariantCulture,
                    $"<text class=\"sub\" x=\"{marginL}\" y=\"80\">入选 {pickedSet.Count}/{n}</text>\n");
            }
            if (effCols == 2 && !double.IsNaN(deltaU))
            {
                sb.Append(CultureInfo.InvariantCulture,
                    $"<text class=\"sub\" x=\"{marginL}\" y=\"96\">ΔU={deltaU:F1}  ΔV={deltaV:F1}  {Esc(deltaSummary ?? "")}</text>\n");
            }

            sb.AppendLine($"<g id=\"plot\">");
            sb.Append(CultureInfo.InvariantCulture,
                $"<rect x=\"{marginL}\" y=\"{marginT}\" width=\"{plotW}\" height=\"{plotH}\" fill=\"#fff\" stroke=\"#bdbdbd\"/>\n");

            // 格网线
            for (int ic = 0; ic < effCols; ic++)
            {
                double x = MapX(lattice.ColU[ic]);
                sb.Append(CultureInfo.InvariantCulture,
                    $"<line x1=\"{x:F1}\" y1=\"{marginT}\" x2=\"{x:F1}\" y2=\"{marginT + plotH}\" stroke=\"#757575\" stroke-width=\"1.5\"/>\n");
            }
            for (int ir = 0; ir < effRows; ir++)
            {
                double y = MapY(lattice.RowV[ir]);
                sb.Append(CultureInfo.InvariantCulture,
                    $"<line x1=\"{marginL}\" y1=\"{y:F1}\" x2=\"{marginL + plotW}\" y2=\"{y:F1}\" stroke=\"#9e9e9e\" stroke-width=\"1\" stroke-dasharray=\"6,4\"/>\n");
            }

            // 格心
            for (int ic = 0; ic < effCols; ic++)
            {
                for (int ir = 0; ir < effRows; ir++)
                {
                    double cx = MapX(lattice.IdealU(ir, ic));
                    double cy = MapY(lattice.IdealV(ir));
                    bool filled = cellBest.TryGetValue((ir, ic), out int winIdx);
                    string stroke = filled ? "#2e7d32" : "#a5d6a7";
                    sb.Append(CultureInfo.InvariantCulture,
                        $"<circle cx=\"{cx:F1}\" cy=\"{cy:F1}\" r=\"5\" fill=\"none\" stroke=\"{stroke}\" stroke-width=\"1.5\"/>\n");
                    if (filled)
                    {
                        sb.Append(CultureInfo.InvariantCulture,
                            $"<text x=\"{cx + 7:F1}\" y=\"{cy - 7:F1}\" font-size=\"9\" fill=\"#388e3c\">[{ir},{ic}]</text>\n");
                    }
                }
            }

            // Find 点
            for (int i = 0; i < n; i++)
            {
                double px = MapX(u[i]);
                double py = MapY(v[i]);
                bool picked = pickedSet.Contains(i);
                bool latticeCol1 = u[i] >= uMid;
                string fill = picked
                    ? (latticeCol1 ? "#e65100" : "#1565c0")
                    : (latticeCol1 ? "#ffcc80" : "#90caf9");
                string stroke = picked ? "#212121" : "#9e9e9e";
                double rad = picked ? 9 : 6;
                sb.Append(CultureInfo.InvariantCulture,
                    $"<circle cx=\"{px:F1}\" cy=\"{py:F1}\" r=\"{rad:F1}\" fill=\"{fill}\" fill-opacity=\"{(picked ? 0.85 : 0.55):F2}\" stroke=\"{stroke}\" stroke-width=\"{(picked ? 2 : 1)}\"/>\n");

                double sc = scores != null && i < scores.Length ? scores[i] : 0;
                string cellHint = idxToCell.TryGetValue(i, out var cell)
                    ? $" [{cell.ir},{cell.ic}]"
                    : "";
                sb.Append(CultureInfo.InvariantCulture,
                    $"<text x=\"{px + 10:F1}\" y=\"{py + 4:F1}\" font-size=\"9\" fill=\"#37474f\">#{i} {sc:F3}{Esc(cellHint)}</text>\n");
            }

            // 轴标注
            sb.Append(CultureInfo.InvariantCulture,
                $"<text class=\"axis\" x=\"{marginL + plotW * 0.5:F0}\" y=\"{marginT + plotH + 36}\" text-anchor=\"middle\">u（列向，px）</text>\n");
            sb.Append(CultureInfo.InvariantCulture,
                $"<text class=\"axis\" x=\"{marginL - 52}\" y=\"{marginT + plotH * 0.5:F0}\" text-anchor=\"middle\" transform=\"rotate(-90 {marginL - 52} {marginT + plotH * 0.5:F0})\">v（行向，px）</text>\n");
            sb.AppendLine("</g>");

            // 图像平面 (Col, Row)：点不应落在 u/v 轴线上，应沿两轴呈网格散布
            double cMin = cols.Min(), cMax = cols.Max();
            double rMin = rows.Min(), rMax = rows.Max();
            double padC = Math.Max(30, 0.06 * (cMax - cMin + 1));
            double padR = Math.Max(30, 0.06 * (rMax - rMin + 1));
            cMin -= padC; cMax += padC;
            rMin -= padR; rMax += padR;
            double scx = imgW / Math.Max(1, cMax - cMin);
            double scy = imgH / Math.Max(1, rMax - rMin);
            double MapCol(double c) => imgLeft + (c - cMin) * scx;
            double MapRow(double r) => marginT + (r - rMin) * scy;
            double mc = cols.Average();
            double mr = rows.Average();
            double th = latticeAngleDeg * Math.PI / 180.0;
            double udx = Math.Cos(th), udy = Math.Sin(th);
            double vdx = -Math.Sin(th), vdy = Math.Cos(th);
            double axisLen = Math.Max(cMax - cMin, rMax - rMin) * 0.55;
            double uEndC = mc + axisLen * udx, uEndR = mr + axisLen * udy;
            double vEndC = mc + axisLen * vdx, vEndR = mr + axisLen * vdy;

            sb.AppendLine("<g id=\"image\">");
            sb.Append(CultureInfo.InvariantCulture,
                $"<rect x=\"{imgLeft}\" y=\"{marginT}\" width=\"{imgW}\" height=\"{imgH}\" fill=\"#fff\" stroke=\"#bdbdbd\"/>\n");
            sb.Append(CultureInfo.InvariantCulture,
                $"<text class=\"axis\" x=\"{imgLeft + imgW * 0.5:F0}\" y=\"{marginT - 8}\" text-anchor=\"middle\">图像 Col / Row（u⊥分列，v沿链）</text>\n");
            sb.Append(CultureInfo.InvariantCulture,
                $"<line x1=\"{MapCol(mc):F1}\" y1=\"{MapRow(mr):F1}\" x2=\"{MapCol(uEndC):F1}\" y2=\"{MapRow(uEndR):F1}\" stroke=\"#1565c0\" stroke-width=\"2.5\" marker-end=\"url(#arrU)\"/>\n");
            sb.Append(CultureInfo.InvariantCulture,
                $"<line x1=\"{MapCol(mc):F1}\" y1=\"{MapRow(mr):F1}\" x2=\"{MapCol(vEndC):F1}\" y2=\"{MapRow(vEndR):F1}\" stroke=\"#e65100\" stroke-width=\"2.5\" marker-end=\"url(#arrV)\"/>\n");
            sb.Append(CultureInfo.InvariantCulture,
                $"<text x=\"{MapCol(uEndC) + 6:F1}\" y=\"{MapRow(uEndR) - 4:F1}\" font-size=\"11\" fill=\"#1565c0\">u 分列</text>\n");
            sb.Append(CultureInfo.InvariantCulture,
                $"<text x=\"{MapCol(vEndC) + 6:F1}\" y=\"{MapRow(vEndR) + 4:F1}\" font-size=\"11\" fill=\"#e65100\">v 链向</text>\n");
            for (int i = 0; i < n; i++)
            {
                bool picked = pickedSet.Contains(i);
                string fill = picked ? "#43a047" : "#bdbdbd";
                sb.Append(CultureInfo.InvariantCulture,
                    $"<circle cx=\"{MapCol(cols[i]):F1}\" cy=\"{MapRow(rows[i]):F1}\" r=\"{(picked ? 7 : 5):F1}\" fill=\"{fill}\" fill-opacity=\"0.75\" stroke=\"#424242\" stroke-width=\"1\"/>\n");
            }
            sb.Append(CultureInfo.InvariantCulture,
                $"<text class=\"axis\" x=\"{imgLeft + imgW * 0.5:F0}\" y=\"{marginT + imgH + 28}\" text-anchor=\"middle\">Col</text>\n");
            sb.Append(CultureInfo.InvariantCulture,
                $"<text class=\"axis\" x=\"{imgLeft - 36}\" y=\"{marginT + imgH * 0.5:F0}\" text-anchor=\"middle\" transform=\"rotate(-90 {imgLeft - 36} {marginT + imgH * 0.5:F0})\">Row</text>\n");
            sb.AppendLine("</g>");

            // 图例
            double lx = imgLeft + imgW + 20;
            double ly = marginT + 20;
            sb.AppendLine("<g id=\"legend\" class=\"leg\">");
            sb.Append(CultureInfo.InvariantCulture, $"<text x=\"{lx}\" y=\"{ly}\" font-weight=\"600\" font-size=\"13\">图例</text>\n");
            ly += 22;
            LegendRow(sb, lx, ly, "#1565c0", "入选 · u 左列"); ly += 20;
            LegendRow(sb, lx, ly, "#e65100", "入选 · u 右列"); ly += 20;
            LegendRow(sb, lx, ly, "#90caf9", "落选 · u 左列"); ly += 20;
            LegendRow(sb, lx, ly, "#ffcc80", "落选 · u 右列"); ly += 20;
            sb.Append(CultureInfo.InvariantCulture, $"<line x1=\"{lx}\" y1=\"{ly + 6}\" x2=\"{lx + 28}\" y2=\"{ly + 6}\" stroke=\"#757575\" stroke-width=\"1.5\"/><text x=\"{lx + 34}\" y=\"{ly + 10}\">ColU 列中心</text>\n");
            ly += 20;
            sb.Append(CultureInfo.InvariantCulture, $"<line x1=\"{lx}\" y1=\"{ly + 6}\" x2=\"{lx + 28}\" y2=\"{ly + 6}\" stroke=\"#9e9e9e\" stroke-dasharray=\"6,4\"/><text x=\"{lx + 34}\" y=\"{ly + 10}\">RowV 行中心</text>\n");
            ly += 20;
            sb.Append(CultureInfo.InvariantCulture, $"<circle cx=\"{lx + 10}\" cy=\"{ly + 4}\" r=\"5\" fill=\"none\" stroke=\"#2e7d32\"/><text x=\"{lx + 34}\" y=\"{ly + 8}\">格心（绿=已占格）</text>\n");
            sb.AppendLine("</g>");

            sb.AppendLine("</svg>");

            string? dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
        }

        private static void LegendRow(StringBuilder sb, double x, double y, string color, string label)
        {
            sb.Append(CultureInfo.InvariantCulture,
                $"<circle cx=\"{x + 10}\" cy=\"{y - 3}\" r=\"7\" fill=\"{color}\" stroke=\"#616161\"/><text x=\"{x + 24}\" y=\"{y}\">{Esc(label)}</text>\n");
        }

        private static string Esc(string? s)
        {
            if (string.IsNullOrEmpty(s))
                return "";
            return s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
        }
    }
}
