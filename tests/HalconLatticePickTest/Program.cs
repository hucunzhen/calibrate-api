using System.Globalization;
using CalibOperatorCLI_Example;
using HalconLatticePickTest;

const int gridRows = 8;
const int gridCols = 2;

string logPath = Path.Combine(AppContext.BaseDirectory, "halcon-lattice-pick-test.grid-filter.log");
string svgPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "flows", "halcon", "uv-projection.svg"));
Directory.CreateDirectory(Path.GetDirectoryName(svgPath)!);
string svgPathLocal = Path.Combine(AppContext.BaseDirectory, "uv-projection.svg");
Environment.SetEnvironmentVariable("XV_GRID_FILTER_LOG", logPath);

var rows = Main2FlowFixture.Rows;
var cols = Main2FlowFixture.Cols;
var scores = Main2FlowFixture.Scores;
double[] angles = Enumerable.Repeat(-56.9, rows.Length).ToArray();

var stripBoot = HalconShapeMatchLatticePick.StripBootstrap(
    rows, cols, angles, scores, gridRows, gridCols, "TestBoot");
Console.WriteLine("=== HalconLatticePickTest (引导θ + u/v落格, 26 Find) ===");
Console.WriteLine($"引导: θ≈{stripBoot.LatticeAngleDeg:F1}°, v≈{stripBoot.VAxisImageAngleDeg:F1}°, PCA链向≈{stripBoot.PcaChainAngleDeg:F1}°");
Console.WriteLine($"Bootstrap K={stripBoot.BootstrapPickIndices.Length}: [{string.Join(",", stripBoot.BootstrapPickIndices)}]");
Console.WriteLine();

var uv = HalconShapeMatchLatticePick.PickUvGridAfterBootstrap(
    rows, cols, angles, scores, gridRows, gridCols,
    stripBoot.LatticeAngleDeg, stripBoot.BootstrapPickIndices, 0, 0, "TestUvGrid", svgPath,
    stripBoot.PcaChainAngleDeg);
if (File.Exists(svgPath))
    File.Copy(svgPath, svgPathLocal, overwrite: true);

PrintPick("u/v 落格16", uv);
PrintTwoColumnDelta(uv);

var ransac = HalconShapeMatchLatticePick.PickRansac(
    rows, cols, angles, scores, gridRows, gridCols, 0, 0, "TestRansac", svgPath, 500, 0.45);
PrintPick("RANSAC 落格16", ransac);
PrintTwoColumnDelta(ransac);

int fail = 0;
fail += AssertContains(uv, 1, 0.936, "#1");
// #3 与 #1 在细化 u 轴后常争同一 u 行格，由更高分 #1 占格
fail += AssertFullGrid(uv, 16);
fail += AssertLatticeAngle(uv, 0.0, 4.0);
fail += AssertImageChainAngle(uv, 90.0, 10.0);
fail += AssertCol1Count(uv, 8);
fail += AssertFullGrid(ransac, 16);
fail += AssertLatticeAngle(ransac, 0.0, 5.0);
fail += AssertCol1Count(ransac, 8);

fail += AssertNotContains(uv, 13, "Find#13 列间(Col≈1124)");
fail += AssertContains(uv, 11, 0.890, "Find#11 近列0格心");

var scores864 = (double[])scores.Clone();
scores864[13] = 0.864;
var uv864 = HalconShapeMatchLatticePick.PickUvGridAfterBootstrap(
    rows, cols, angles, scores864, gridRows, gridCols,
    stripBoot.LatticeAngleDeg, stripBoot.BootstrapPickIndices, 0, 0, "Test864");
fail += AssertNotContains(uv864, 13, "Find#13 Score=0.864 离列远");
fail += AssertFullGrid(uv864, 16);

const double rotateDeg = 42.0;
RotatePoints(Main2FlowFixture.Rows, Main2FlowFixture.Cols, rotateDeg, out var rotRows, out var rotCols);
var rotAngles = angles.Select(a => a + rotateDeg).ToArray();
var rotBoot = HalconShapeMatchLatticePick.StripBootstrap(
    rotRows, rotCols, rotAngles, scores, gridRows, gridCols, "TestBootRot");
var rotUv = HalconShapeMatchLatticePick.PickUvGridAfterBootstrap(
    rotRows, rotCols, rotAngles, scores, gridRows, gridCols,
    rotBoot.LatticeAngleDeg, rotBoot.BootstrapPickIndices, 0, 0, "TestUvGridRot",
    pcaChainAngleDeg: rotBoot.PcaChainAngleDeg);
Console.WriteLine($"=== 大角度旋转 +{rotateDeg:F0}° ===");
Console.WriteLine($"引导: θ≈{rotBoot.LatticeAngleDeg:F1}°, v≈{rotBoot.VAxisImageAngleDeg:F1}°");
PrintPick("u/v 落格16 (旋转)", rotUv);
fail += AssertFullGrid(rotUv, 16);
fail += AssertLatticeAngle(rotUv, 2.0 + rotateDeg, 8.0);
fail += AssertImageChainAngle(rotUv, 90.0 + rotateDeg, 12.0);
fail += AssertCol1Count(rotUv, 8);

Console.WriteLine($"诊断日志: {logPath}");
Console.WriteLine(File.Exists(svgPath)
    ? $"u/v 投影图: {svgPath}"
    : "u/v 投影图: 未生成");
if (File.Exists(svgPathLocal))
    Console.WriteLine($"  副本: {svgPathLocal}");
if (fail == 0)
{
    Console.WriteLine("PASS");
    return 0;
}

Console.WriteLine($"FAIL: {fail}");
return 1;

static void PrintTwoColumnDelta(HalconShapeMatchLatticePickResult pick)
{
    Console.WriteLine("--- 双列 ΔU / ΔV ---");
    if (double.IsNaN(pick.TwoColumnDeltaU) && double.IsNaN(pick.TwoColumnDeltaV))
    {
        Console.WriteLine("  (未计算，非 2 列格网)");
        Console.WriteLine();
        return;
    }
    Console.WriteLine($"  ΔU (ColU[1]-ColU[0]) = {pick.TwoColumnDeltaU:F1} px");
    Console.WriteLine($"  ΔV (median v|列1 - median v|列0) = {pick.TwoColumnDeltaV:F1} px");
    Console.WriteLine($"  摘要: {pick.TwoColumnDeltaSummary}");
    Console.WriteLine($"  详细日志: {Path.Combine(AppContext.BaseDirectory, "halcon-lattice-pick-test.grid-filter.log")}");
    Console.WriteLine();
}

static void PrintPick(string label, HalconShapeMatchLatticePickResult pick)
{
    Console.WriteLine($"--- {label} ---");
    Console.WriteLine($"Kept {pick.KeptCount}/16, θ≈{pick.LatticeAngleDeg:F1}°");
    Console.WriteLine("  序   #idx   Score   GridCol   Row       Col");
    for (int k = 0; k < pick.KeptIndices.Length; k++)
    {
        int idx = pick.KeptIndices[k];
        double sc = k < pick.Scores.Length ? pick.Scores[k] : 0;
        int gc = k < pick.GridCol.Length ? pick.GridCol[k] : -1;
        Console.WriteLine($"  {k,2}   {idx,3}   {sc:F3}      {gc}    {pick.Rows[k],8:F1}  {pick.Cols[k],8:F1}");
    }
    Console.WriteLine();
}

static int AssertNotContains(HalconShapeMatchLatticePickResult pick, int findIdx, string name)
{
    if (Array.IndexOf(pick.KeptIndices, findIdx) < 0)
    {
        Console.WriteLine($"  OK   {name}: not in 16");
        return 0;
    }
    Console.WriteLine($"  FAIL {name}: Find#{findIdx} should be excluded");
    return 1;
}

static int AssertContains(HalconShapeMatchLatticePickResult pick, int findIdx, double expectedScore, string name)
{
    int pos = Array.IndexOf(pick.KeptIndices, findIdx);
    if (pos < 0)
    {
        Console.WriteLine($"  FAIL {name}: Find#{findIdx} missing");
        return 1;
    }
    double sc = pos < pick.Scores.Length ? pick.Scores[pos] : 0;
    if (Math.Abs(sc - expectedScore) > 0.002)
    {
        Console.WriteLine($"  FAIL {name}: score {sc:F3}");
        return 1;
    }
    Console.WriteLine($"  OK   {name}");
    return 0;
}

static int AssertFullGrid(HalconShapeMatchLatticePickResult pick, int expected)
{
    if (pick.KeptCount == expected)
    {
        Console.WriteLine($"  OK   {expected}/16 cells");
        return 0;
    }
    Console.WriteLine($"  FAIL {pick.KeptCount}/{expected} cells");
    return 1;
}

static int AssertImageChainAngle(HalconShapeMatchLatticePickResult pick, double expectedDeg, double tol)
{
    double vAxis = pick.LatticeAngleDeg + 90.0;
    double d = Math.Abs((vAxis - expectedDeg + 180) % 360 - 180);
    if (d <= tol)
    {
        Console.WriteLine($"  OK   图像链向(v)≈{vAxis:F1}°");
        return 0;
    }
    Console.WriteLine($"  FAIL 图像链向(v)≈{vAxis:F1}° expected {expectedDeg:F1}°");
    return 1;
}

static int AssertLatticeAngle(HalconShapeMatchLatticePickResult pick, double expectedDeg, double tol)
{
    if (Math.Abs(pick.LatticeAngleDeg - expectedDeg) <= tol)
    {
        Console.WriteLine($"  OK   θ≈{pick.LatticeAngleDeg:F1}°");
        return 0;
    }
    Console.WriteLine($"  FAIL θ≈{pick.LatticeAngleDeg:F1}° expected {expectedDeg:F1}°");
    return 1;
}

static void RotatePoints(
    double[] rows, double[] cols, double deg, out double[] outRows, out double[] outCols)
{
    double rad = deg * Math.PI / 180.0;
    double cos = Math.Cos(rad);
    double sin = Math.Sin(rad);
    double cx = cols.Average();
    double cy = rows.Average();
    int n = rows.Length;
    outRows = new double[n];
    outCols = new double[n];
    for (int i = 0; i < n; i++)
    {
        double dc = cols[i] - cx;
        double dr = rows[i] - cy;
        outCols[i] = cx + dc * cos - dr * sin;
        outRows[i] = cy + dc * sin + dr * cos;
    }
}

static int AssertCol1Count(HalconShapeMatchLatticePickResult pick, int expected)
{
    int n = pick.GridCol.Count(g => g == 1);
    if (n == expected)
    {
        Console.WriteLine($"  OK   col1={n}");
        return 0;
    }
    Console.WriteLine($"  FAIL col1={n} expected {expected}");
    return 1;
}
