using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace CalibOperatorCLI_Example
{
    /// <summary>
    /// 单 STA 线程上的 <see cref="TaskScheduler"/>，供 Flow 节点执行使用。
    /// System.Drawing / GDI+（SAM 预处理等）在 MTA 线程池上易出现卡住、CPU 空闲的假死。
    /// </summary>
    internal sealed class FlowStaTaskScheduler : TaskScheduler
    {
        public static readonly FlowStaTaskScheduler Default = new FlowStaTaskScheduler();

        private readonly BlockingCollection<Task> _queue = new BlockingCollection<Task>();

        private FlowStaTaskScheduler()
        {
            var thread = new Thread(Run)
            {
                IsBackground = true,
                Name = "FlowSTA"
            };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
        }

        private void Run()
        {
            try
            {
                foreach (Task task in _queue.GetConsumingEnumerable())
                    TryExecuteTask(task);
            }
            catch
            {
                // ignored：进程退出时队列可能中止
            }
        }

        protected override IEnumerable<Task>? GetScheduledTasks() => _queue.ToArray();

        protected override void QueueTask(Task task) => _queue.Add(task);

        protected override bool TryExecuteTaskInline(Task task, bool taskWasPreviouslyQueued) => false;
    }
}
