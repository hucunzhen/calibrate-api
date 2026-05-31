#if HALCON_ENABLED
namespace CalibOperatorCLI_Example
{
    /// <summary>
    /// HALCON 算子执行时的线程策略（进程级 <c>thread_num</c> 仅在持锁期间切换）。
    /// </summary>
    internal enum HalconThreadPolicy
    {
        /// <summary>单线程：<c>thread_num=1</c>，Region/XLD/Mask/预处理等。</summary>
        Geometry = 0,

        /// <summary>多线程：<c>thread_num=配置值</c>，FindShapeModel / 粗定位等。</summary>
        ParallelFind = 1,
    }
}
#else
namespace CalibOperatorCLI_Example
{
    internal enum HalconThreadPolicy
    {
        Geometry = 0,
        ParallelFind = 1,
    }
}
#endif
