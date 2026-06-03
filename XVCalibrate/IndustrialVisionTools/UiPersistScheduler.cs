using System;
using System.Windows.Threading;

namespace CalibOperatorCLI_Example
{
    internal sealed class UiPersistScheduler
    {
        private readonly DispatcherTimer _timer;
        private readonly Action _persist;

        public UiPersistScheduler(Action persist, int delayMs = 450)
        {
            _persist = persist;
            _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(delayMs) };
            _timer.Tick += (_, _) =>
            {
                _timer.Stop();
                _persist();
            };
        }

        public void Schedule()
        {
            _timer.Stop();
            _timer.Start();
        }

        public void Flush()
        {
            _timer.Stop();
            _persist();
        }
    }
}
