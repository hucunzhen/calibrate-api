namespace HalconLatticePickTest;

/// <summary>main2.flow.grid-filter.log θ≈-57° 会话的 26 点 Find 复现（2026-05-20 14:51:25）。</summary>
internal static class Main2FlowFixture
{
    public static readonly double[] Rows =
    {
        1146.4, 1359.7, 784.1, 1253.4, 1465.7, 1571.7, 1278.1, 1490.2, 1655.0, 1383.2,
        1041.6, 828.3, 850.7, 773.0, 1063.1, 1478.3, 1170.1, 1595.0, 934.6, 1371.4,
        1583.3, 945.5, 1052.8, 956.8, 1158.8, 1639.3
    };

    public static readonly double[] Cols =
    {
        938.1, 931.5, 1546.4, 934.6, 927.5, 924.2, 1536.6, 1533.7, 1506.2, 1539.2,
        942.7, 950.4, 1542.2, 1124.3, 1543.5, 1231.6, 1539.1, 1524.3, 946.3, 1220.7,
        1203.0, 1245.1, 1241.2, 1547.7, 1243.1, 933.9
    };

    public static readonly double[] Scores =
    {
        0.939, 0.936, 0.925, 0.918, 0.917, 0.914, 0.911, 0.910, 0.903, 0.900,
        0.891, 0.890, 0.889, 0.852, 0.849, 0.848, 0.848, 0.837, 0.833, 0.831,
        0.804, 0.797, 0.797, 0.797, 0.783, 0.769
    };

    public static int[] BootstrapTop16() =>
        Enumerable.Range(0, 26)
            .OrderByDescending(i => Scores[i])
            .ThenBy(i => i)
            .Take(16)
            .ToArray();
}
