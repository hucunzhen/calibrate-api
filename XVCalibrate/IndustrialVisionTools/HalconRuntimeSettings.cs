#if HALCON_ENABLED
using System;
using System.Threading;
using HalconDotNet;

namespace CalibOperatorCLI_Example
{
    /// <summary>
    /// 进程级 HALCON 多线程与全局算子串行门闩。
    /// 所有 HalconDotNet 调用须经 <see cref="RunExclusive"/>（通常由 <see cref="HalconComputeRunner"/> 调用）。
    /// </summary>
    internal static class HalconRuntimeSettings
    {
        private static int _applyState;
        private static int _configuredThreadNum = 1;
        private static int _effectiveThreadNum = 1;
        private static bool _parallelizeOperators;
        private static bool _configuredParallelizeOperators;

        private static readonly object OperatorGate = new();

        [ThreadStatic]
        private static int _operatorGateDepth;

        /// <summary>最近一次 ParallelFind 作用域内实际使用的 thread_num（便于日志核对）。</summary>
        public static int LastParallelFindThreadNum { get; private set; }

        public static bool LastParallelFindParallelize { get; private set; }

        public static int ConfiguredThreadNum => _configuredThreadNum;

        public static int EffectiveThreadNum => _effectiveThreadNum;

        public static bool ParallelizeOperators => _parallelizeOperators;

        public static bool IsApplied => Volatile.Read(ref _applyState) == 2;

        public static T RunExclusive<T>(Func<T> work, HalconThreadPolicy policy = HalconThreadPolicy.Geometry)
        {
            EnsureApplied();
            ArgumentNullException.ThrowIfNull(work);

            lock (OperatorGate)
            {
                _operatorGateDepth++;
                try
                {
                    return RunInPolicyScope(policy, work);
                }
                finally
                {
                    _operatorGateDepth--;
                }
            }
        }

        public static void RunExclusive(Action work, HalconThreadPolicy policy = HalconThreadPolicy.Geometry) =>
            RunExclusive(() =>
            {
                work();
                return 0;
            }, policy);

        public static T RunGeometrySafe<T>(Func<T> work)
        {
            EnsureApplied();
            if (_configuredThreadNum <= 1 && !_configuredParallelizeOperators)
                return work();

            if (_operatorGateDepth > 0)
                return RunInPolicyScope(HalconThreadPolicy.Geometry, work);

            return RunExclusive(work, HalconThreadPolicy.Geometry);
        }

        public static void RunGeometrySafe(Action work) =>
            RunGeometrySafe(() =>
            {
                work();
                return 0;
            });

        public static T RunSingleThreadedIfNeeded<T>(Func<T> work) => RunGeometrySafe(work);

        public static void RunSingleThreadedIfNeeded(Action work) => RunGeometrySafe(work);

        private static T RunInPolicyScope<T>(HalconThreadPolicy policy, Func<T> work)
        {
            int restoreThreads = _effectiveThreadNum;
            bool restoreParallel = _parallelizeOperators;

            int targetThreads = policy == HalconThreadPolicy.ParallelFind
                ? Math.Max(1, _configuredThreadNum)
                : 1;
            bool targetParallel = policy == HalconThreadPolicy.ParallelFind
                ? ResolveParallelFindParallelize()
                : false;

            try
            {
                ApplyExecutionContext(targetThreads, targetParallel, setTspThreadNum: policy == HalconThreadPolicy.ParallelFind);

                if (policy == HalconThreadPolicy.ParallelFind)
                {
                    LastParallelFindThreadNum = targetThreads;
                    LastParallelFindParallelize = targetParallel;
                    TracePolicyScope("ParallelFind", targetThreads, targetParallel);
                }

                return work();
            }
            finally
            {
                ApplyExecutionContext(restoreThreads, restoreParallel, setTspThreadNum: false);
                ResetTspThreadNum();
            }
        }

        /// <summary>
        /// ParallelFind 时默认开启 <c>parallelize_operators</c>（Find 类算子需要）；
        /// 可用 <c>XV_HALCON_PARALLELIZE=false</c> 全局关闭。
        /// </summary>
        private static bool ResolveParallelFindParallelize()
        {
            if (!_configuredParallelizeOperators)
            {
                string? raw = Environment.GetEnvironmentVariable("XV_HALCON_PARALLELIZE");
                if (!string.IsNullOrWhiteSpace(raw))
                {
                    raw = raw.Trim();
                    return raw is not ("0" or "false" or "off" or "no");
                }

                return true;
            }

            return true;
        }

        private static void ApplyExecutionContext(int threadNum, bool parallelize, bool setTspThreadNum)
        {
            threadNum = Math.Max(1, threadNum);
            if (_effectiveThreadNum != threadNum)
            {
                HOperatorSet.SetSystem("thread_num", threadNum);
                _effectiveThreadNum = threadNum;
            }

            string parallelizeStr = parallelize ? "true" : "false";
            if (_parallelizeOperators != parallelize)
            {
                HOperatorSet.SetSystem("parallelize_operators", parallelizeStr);
                _parallelizeOperators = parallelize;
            }

            if (setTspThreadNum)
                HOperatorSet.SetSystem("tsp_thread_num", threadNum);
        }

        private static void ResetTspThreadNum()
        {
            try { HOperatorSet.SetSystem("tsp_thread_num", "default"); }
            catch { /* 旧版 HALCON 忽略 */ }
        }

        public static void EnsureApplied()
        {
            if (Volatile.Read(ref _applyState) == 2)
                return;

            if (Interlocked.CompareExchange(ref _applyState, 1, 0) != 0)
            {
                while (Volatile.Read(ref _applyState) == 1)
                    Thread.Sleep(0);
                return;
            }

            try
            {
                int threads = ResolveThreadNum();
                bool parallelize = ResolveParallelizeOperators();

                HOperatorSet.SetSystem("thread_num", threads);
                HOperatorSet.SetSystem("parallelize_operators", parallelize ? "true" : "false");

                _configuredThreadNum = threads;
                _effectiveThreadNum = threads;
                _parallelizeOperators = parallelize;
                _configuredParallelizeOperators = parallelize;
                Volatile.Write(ref _applyState, 2);
                TraceApplied(threads, parallelize);
            }
            catch (Exception ex)
            {
                Interlocked.Exchange(ref _applyState, 0);
                System.Diagnostics.Debug.WriteLine($"[HALCON] SetSystem(thread_num) failed: {ex.Message}");
            }
        }

        private static int ResolveThreadNum()
        {
            string? raw = Environment.GetEnvironmentVariable("XV_HALCON_THREAD_NUM");
            if (!string.IsNullOrWhiteSpace(raw)
                && int.TryParse(raw.Trim(), out int n))
            {
                if (n <= 0)
                    return Math.Max(1, Environment.ProcessorCount);
                return n;
            }

            return Math.Max(1, Environment.ProcessorCount);
        }

        private static bool ResolveParallelizeOperators()
        {
            string? raw = Environment.GetEnvironmentVariable("XV_HALCON_PARALLELIZE");
            if (string.IsNullOrWhiteSpace(raw))
                return false;

            raw = raw.Trim();
            return raw is not ("0" or "false" or "off" or "no");
        }

        private static void TraceApplied(int threads, bool parallelize)
        {
            string msg =
                $"[HALCON] 内部多线程: thread_num={threads}, parallelize_operators={parallelize} " +
                $"(ParallelFind 作用域内默认 parallelize=true+tsp_thread_num)";
            System.Diagnostics.Debug.WriteLine(msg);
            try { Console.WriteLine(msg); }
            catch { /* WinExe 无控制台时忽略 */ }
        }

        private static void TracePolicyScope(string scope, int threads, bool parallelize)
        {
            if (!IsTraceEnabled())
                return;

            string msg = $"[HALCON] {scope}: thread_num={threads}, parallelize_operators={parallelize}, tsp_thread_num={threads}";
            System.Diagnostics.Debug.WriteLine(msg);
            try { Console.WriteLine(msg); }
            catch { /* ignore */ }
        }

        public static bool IsTraceEnabled()
        {
            string? raw = Environment.GetEnvironmentVariable("XV_HALCON_TRACE");
            return !string.IsNullOrWhiteSpace(raw)
                && raw.Trim() is not ("0" or "false" or "off" or "no");
        }
    }
}
#else
namespace CalibOperatorCLI_Example
{
    internal static class HalconRuntimeSettings
    {
        public static int LastParallelFindThreadNum => 1;
        public static bool LastParallelFindParallelize => false;
        public static int ConfiguredThreadNum => 1;
        public static int EffectiveThreadNum => 1;
        public static bool ParallelizeOperators => false;
        public static bool IsApplied => false;
        public static bool IsTraceEnabled() => false;
        public static void EnsureApplied() { }
        public static T RunExclusive<T>(Func<T> work, HalconThreadPolicy policy = HalconThreadPolicy.Geometry) => work();
        public static void RunExclusive(Action work, HalconThreadPolicy policy = HalconThreadPolicy.Geometry) => work();
        public static T RunGeometrySafe<T>(Func<T> work) => work();
        public static void RunGeometrySafe(Action work) => work();
        public static T RunSingleThreadedIfNeeded<T>(Func<T> work) => work();
        public static void RunSingleThreadedIfNeeded(Action work) => work();
    }
}
#endif
