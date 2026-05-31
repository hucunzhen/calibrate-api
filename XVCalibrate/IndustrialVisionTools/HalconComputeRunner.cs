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
            return RunAsync(work, policy, cancellationToken).GetAwaiter().GetResult();
        }

        public static void Run(
            Action work,
            HalconThreadPolicy policy = HalconThreadPolicy.Geometry,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(work);
            RunAsync(work, policy, cancellationToken).GetAwaiter().GetResult();
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
