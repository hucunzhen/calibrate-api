using System;

namespace CalibOperatorCLI_Example
{
    /// <summary>
    /// PLC 页 GVAR 表格草稿，供「Write All」与流程 send_plc（usePlcPageGvar）共用同一份数据。
    /// </summary>
    internal static class PlcGvarDraft
    {
        private static GVAR[]? _items;
        private static string _source = "";

        public static void Set(GVAR[] items, string source)
        {
            _items = items ?? Array.Empty<GVAR>();
            _source = source ?? "";
        }

        public static bool TryGet(out GVAR[] items, out string source)
        {
            if (_items == null || _items.Length == 0)
            {
                items = Array.Empty<GVAR>();
                source = "";
                return false;
            }

            items = _items;
            source = _source;
            return true;
        }
    }
}
