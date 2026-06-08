using System;
using System.Runtime.InteropServices;
using CalibOperatorCLI_Example;
using CalibOperatorPInvoke;

namespace FlowSmokeTests
{
    /// <summary>CalibImageTransform 旋转/翻转与 OpenCV 约定一致。</summary>
    internal static class CalibImageTransformSmokeTest
    {
        public static int Run()
        {
            int fails = 0;
            fails += CheckRotate180IsNotVerticalFlipOnly();
            fails += CheckRotate90SizeAndCorner();
            fails += CheckRotate270SizeAndCorner();
            return fails;
        }

        /// <summary>4×3 非对称角点：180° 须同时交换左右与上下；仅上下翻转时左下角不会到右上角。</summary>
        private static int CheckRotate180IsNotVerticalFlipOnly()
        {
            const int w = 4, h = 3;
            using var src = CreateGray(w, h);
            SetPixel(src, 0, 0, 10);   // 左上
            SetPixel(src, w - 1, 0, 20); // 右上
            SetPixel(src, 0, h - 1, 30); // 左下
            SetPixel(src, w - 1, h - 1, 40); // 右下

            using var r180 = CalibImageTransform.Rotate(src, 180, expandCanvas: true);
            using var vFlip = CalibImageTransform.Flip(src, "vertical");

            byte tl180 = GetPixel(r180, 0, 0);
            byte tr180 = GetPixel(r180, w - 1, 0);
            byte tlV = GetPixel(vFlip, 0, 0);

            bool ok180 = tl180 == 40 && tr180 == 30; // 180: 右下→左上，左下→右上
            bool okDiff = tl180 != tlV; // 若 180 等同上下翻转，则左上均为 30

            if (ok180 && okDiff)
            {
                Console.WriteLine("  OK rotate 180 != vertical flip only");
                return 0;
            }

            Console.Error.WriteLine($"  FAIL rotate 180: tl={tl180} tr={tr180} (expect 40,30); vflip tl={tlV}");
            return 1;
        }

        private static int CheckRotate90SizeAndCorner()
        {
            const int w = 4, h = 3;
            using var src = CreateGray(w, h);
            SetPixel(src, 0, 0, 55);

            using var r90 = CalibImageTransform.Rotate(src, 90, expandCanvas: true);
            if (r90.Width != h || r90.Height != w)
            {
                Console.Error.WriteLine($"  FAIL rotate 90 size: got {r90.Width}x{r90.Height}, expect {h}x{w}");
                return 1;
            }

            // OpenCV ROTATE_90_CLOCKWISE: src(0,0) -> dst(w-1,0) i.e. (h-1,0) in dst coords when dst is h×w
            byte got = GetPixel(r90, h - 1, 0);
            if (got != 55)
            {
                Console.Error.WriteLine($"  FAIL rotate 90 corner: got {got}, expect 55");
                return 1;
            }

            Console.WriteLine("  OK rotate 90 size/corner");
            return 0;
        }

        private static int CheckRotate270SizeAndCorner()
        {
            const int w = 4, h = 3;
            using var src = CreateGray(w, h);
            SetPixel(src, 0, 0, 66);

            using var r270 = CalibImageTransform.Rotate(src, 270, expandCanvas: true);
            if (r270.Width != h || r270.Height != w)
            {
                Console.Error.WriteLine($"  FAIL rotate 270 size: got {r270.Width}x{r270.Height}, expect {h}x{w}");
                return 1;
            }

            // src(0,0) -> dst(0,w-1) = (0, h-1) wrong - derive: 270 CW -> dst(0, w-1) in dst h×w
            byte got = GetPixel(r270, 0, w - 1);
            if (got != 66)
            {
                Console.Error.WriteLine($"  FAIL rotate 270 corner: got {got}, expect 66");
                return 1;
            }

            Console.WriteLine("  OK rotate 270 size/corner");
            return 0;
        }

        private static CalibImage CreateGray(int w, int h)
        {
            var img = new CalibImage(w, h, 1);
            var n = img.GetNativeStruct();
            int bytes = w * h;
            var buf = new byte[bytes];
            Marshal.Copy(buf, 0, n.data, bytes);
            return img;
        }

        private static void SetPixel(CalibImage img, int x, int y, byte v)
        {
            var n = img.GetNativeStruct();
            Marshal.WriteByte(n.data, y * n.width + x, v);
        }

        private static byte GetPixel(CalibImage img, int x, int y)
        {
            var n = img.GetNativeStruct();
            return Marshal.ReadByte(n.data, y * n.width + x);
        }
    }
}
