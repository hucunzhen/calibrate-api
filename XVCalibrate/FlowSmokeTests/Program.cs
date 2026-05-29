using System;
using CalibOperatorCLI_Example;

namespace FlowSmokeTests
{
    internal static class Program
    {
        public static int Main(string[] args)
        {
            string? root = null;
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--repo" && i + 1 < args.Length)
                    root = args[++i];
            }

            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--halcon-coarse-fine")
                {
                    Console.WriteLine("=== HalconCoarseFineSmokeTest ===");
                    return HalconCoarseFineSmokeTest.Run(root);
                }
            }

            Console.WriteLine("=== FlowCalibrationSmokeTest ===");
            return FlowCalibrationSmokeTest.Run(root);
        }
    }
}
