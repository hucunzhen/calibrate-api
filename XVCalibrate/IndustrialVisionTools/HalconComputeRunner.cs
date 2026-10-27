#if HALCON_ENABLED
using System;
using System.Threading;
using System.Threading.Tasks;

namespace CalibOperatorCLI_Example
{
    /// <summary>
    /// HALCON 算子统一入口：MTA 线程池 + 全局串行门闩 + 按策略切换 <c>thread_num</c>。
    /// </summary>
    internal static class HalconComputeRunner
    {
        public static T Run<T>(
            Func<T> work,
            HalconThreadPolicy policy = HalconThreadPolicy.Geometry,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(work);
            cancellationToken.ThrowIfCancellationRequested();
            // 已在门闩线程内：禁止再 Task.Run + GetResult，否则新线程抢同一 lock 会死锁
            // （典型：ExecuteNode 内再调 BuildCoarseShapeMaskBatchForNode）。
            if (HalconRuntimeSettings.IsInsideOperatorGate)
            {
                HalconRuntimeSettings.EnsureApplied();
                return HalconRuntimeSettings.RunExclusive(work, policy);
            }

            return RunAsync(work, policy, cancellationToken).GetAwaiter().GetResult();
        }

        public static void Run(
            Action work,
            HalconThreadPolicy policy = HalconThreadPolicy.Geometry,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(work);
            Run<object?>(() =>
            {
                work();
                return null;
            }, policy, cancellationToken);
        }

        public static Task<T> RunAsync<T>(
            Func<CancellationToken, T> work,
            CancellationToken cancellationToken = default) =>
            RunAsync(work, HalconThreadPolicy.ParallelFind, cancellationToken);

        public static Task<T> RunAsync<T>(Func<T> work, CancellationToken cancellationToken = default) =>
            RunAsync(work, HalconThreadPolicy.ParallelFind, cancellationToken);

        public static Task RunAsync(Action work, CancellationToken cancellationToken = default) =>
            RunAsync(work, HalconThreadPolicy.ParallelFind, cancellationToken);

        public static Task<T> RunAsync<T>(
            Func<CancellationToken, T> work,
            HalconThreadPolicy policy,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(work);
            if (HalconRuntimeSettings.IsInsideOperatorGate)
            {
                cancellationToken.ThrowIfCancellationRequested();
                HalconRuntimeSettings.EnsureApplied();
                return Task.FromResult(
                    HalconRuntimeSettings.RunExclusive(() => work(cancellationToken), policy));
            }

            return Task.Run(
                () =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    HalconRuntimeSettings.EnsureApplied();
                    return HalconRuntimeSettings.RunExclusive(() => work(cancellationToken), policy);
                },
                cancellationToken);
        }

        public static Task<T> RunAsync<T>(
            Func<T> work,
            HalconThreadPolicy policy,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(work);
            if (HalconRuntimeSettings.IsInsideOperatorGate)
            {
                cancellationToken.ThrowIfCancellationRequested();
                HalconRuntimeSettings.EnsureApplied();
                return Task.FromResult(HalconRuntimeSettings.RunExclusive(work, policy));
            }

            return Task.Run(
                () =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    HalconRuntimeSettings.EnsureApplied();
                    return HalconRuntimeSettings.RunExclusive(work, policy);
                },
                cancellationToken);
        }

        public static Task RunAsync(
            Action work,
            HalconThreadPolicy policy,
            CancellationToken cancellationToken = default) =>
            RunAsync<object?>(_ =>
            {
                work();
                return null;
            }, policy, cancellationToken);
    }
}
#else
using System;
using System.Threading;
using System.Threading.Tasks;

namespace CalibOperatorCLI_Example
{
    internal static class HalconComputeRunner
    {
        public static T Run<T>(
            Func<T> work,
            HalconThreadPolicy policy = HalconThreadPolicy.Geometry,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return work();
        }

        public static void Run(
            Action work,
            HalconThreadPolicy policy = HalconThreadPolicy.Geometry,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            work();
        }

        public static Task<T> RunAsync<T>(
            Func<CancellationToken, T> work,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(work(cancellationToken));
        }

        public static Task<T> RunAsync<T>(Func<T> work, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(work());
        }

        public static Task RunAsync(Action work, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            work();
            return Task.CompletedTask;
        }

        public static Task<T> RunAsync<T>(
            Func<CancellationToken, T> work,
            HalconThreadPolicy policy,
            CancellationToken cancellationToken = default) =>
            RunAsync(work, cancellationToken);

        public static Task<T> RunAsync<T>(
            Func<T> work,
            HalconThreadPolicy policy,
            CancellationToken cancellationToken = default) =>
            RunAsync(work, cancellationToken);

        public static Task RunAsync(
            Action work,
            HalconThreadPolicy policy,
            CancellationToken cancellationToken = default) =>
            RunAsync(work, cancellationToken);
    }
}
#endif
