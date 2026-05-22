using HslCommunication.Profinet.XINJE;

namespace CalibOperatorCLI_Example
{
    /// <summary>
    /// 共享信捷 D 区连接：PLC 页连接后流程 send_plc 可复用，避免两套连接站号不一致。
    /// </summary>
    internal static class PlcXinjeSession
    {
        private static XinJETcpNet? _active;
        private static string _owner = "";

        public static XinJETcpNet? Active => _active;

        public static bool HasActive => _active != null;

        public static void Register(XinJETcpNet? client, string owner)
        {
            _active = client;
            _owner = client != null ? owner : "";
        }

        public static void ClearIfOwnedBy(XinJETcpNet? client)
        {
            if (ReferenceEquals(_active, client))
            {
                _active = null;
                _owner = "";
            }
        }

        public static string DescribeActive()
            => _active == null ? "无" : _owner;
    }
}
