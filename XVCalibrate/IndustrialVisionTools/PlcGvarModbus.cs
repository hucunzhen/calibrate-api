using System;
using HslCommunication;
using HslCommunication.Profinet.XINJE;

namespace CalibOperatorCLI_Example
{
    /// <summary>
    /// GVAR 列表读写（XinJETcpNet 信捷地址，如 D30000）。浮点 ReadFloat/WriteFloat，DataFormat 须为 CDAB（plc_config FloatDataFormat）。
    /// </summary>
    internal static class PlcGvarModbus
    {
        public const int MaxReadChunkRegisters = 120;
        public const int MaxWriteChunkRegisters = 120;

        private static OperateResult CopyOperateFailure(OperateResult source)
            => new OperateResult
            {
                IsSuccess = false,
                Message = source?.Message ?? "",
                ErrorCode = source?.ErrorCode ?? 0
            };

        private static string WordAddr(string itemBase, int wordOffset)
            => PlcXinjeHelper.OffsetDAddress(itemBase, wordOffset);

        public static string FormatOperateFailure(OperateResult result)
        {
            if (result == null)
                return "无响应";
            string msg = string.IsNullOrWhiteSpace(result.Message)
                ? "无详细消息（Modbus 超时/越界/连接断开）"
                : result.Message.Trim();
            if (result.ErrorCode != 0)
                msg += $" (ErrorCode={result.ErrorCode})";
            return msg;
        }

        /// <summary>单条 GVAR：按字段 ReadFloat（与连接时 DataFormat=CDAB 一致）。</summary>
        public static OperateResult<GVAR> ReadGvarItemFromPlc(XinJETcpNet plc, string itemBase)
        {
            var tr = plc.ReadInt16(WordAddr(itemBase, 0));
            if (!tr.IsSuccess)
                return OperateResult.CreateFailedResult<GVAR>(tr);

            var p0x = plc.ReadFloat(WordAddr(itemBase, 2));
            if (!p0x.IsSuccess) return OperateResult.CreateFailedResult<GVAR>(p0x);
            var p0y = plc.ReadFloat(WordAddr(itemBase, 4));
            if (!p0y.IsSuccess) return OperateResult.CreateFailedResult<GVAR>(p0y);
            var p0z = plc.ReadFloat(WordAddr(itemBase, 6));
            if (!p0z.IsSuccess) return OperateResult.CreateFailedResult<GVAR>(p0z);
            var p1x = plc.ReadFloat(WordAddr(itemBase, 8));
            if (!p1x.IsSuccess) return OperateResult.CreateFailedResult<GVAR>(p1x);
            var p1y = plc.ReadFloat(WordAddr(itemBase, 10));
            if (!p1y.IsSuccess) return OperateResult.CreateFailedResult<GVAR>(p1y);
            var p1z = plc.ReadFloat(WordAddr(itemBase, 12));
            if (!p1z.IsSuccess) return OperateResult.CreateFailedResult<GVAR>(p1z);
            var cx = plc.ReadFloat(WordAddr(itemBase, 14));
            if (!cx.IsSuccess) return OperateResult.CreateFailedResult<GVAR>(cx);
            var cy = plc.ReadFloat(WordAddr(itemBase, 16));
            if (!cy.IsSuccess) return OperateResult.CreateFailedResult<GVAR>(cy);
            var r = plc.ReadFloat(WordAddr(itemBase, 18));
            if (!r.IsSuccess) return OperateResult.CreateFailedResult<GVAR>(r);
            var sd = plc.ReadFloat(WordAddr(itemBase, 20));
            if (!sd.IsSuccess) return OperateResult.CreateFailedResult<GVAR>(sd);
            var ed = plc.ReadFloat(WordAddr(itemBase, 22));
            if (!ed.IsSuccess) return OperateResult.CreateFailedResult<GVAR>(ed);
            var z0 = plc.ReadFloat(WordAddr(itemBase, 24));
            if (!z0.IsSuccess) return OperateResult.CreateFailedResult<GVAR>(z0);
            var z1 = plc.ReadFloat(WordAddr(itemBase, 26));
            if (!z1.IsSuccess) return OperateResult.CreateFailedResult<GVAR>(z1);

            var g = new GVAR
            {
                type1 = tr.Content,
                spVec3_p0 = new SpVec3(p0x.Content, p0y.Content, p0z.Content),
                spVec3_p1 = new SpVec3(p1x.Content, p1y.Content, p1z.Content),
                cx = cx.Content,
                cy = cy.Content,
                r = r.Content,
                start_deg = sd.Content,
                end_deg = ed.Content,
                z0 = z0.Content,
                z1 = z1.Content
            };
            return OperateResult.CreateSuccessResult(g);
        }

        public static OperateResult<GVAR[]> ReadGvarListFromPlc(XinJETcpNet plc, string startXinje, int itemCount)
        {
            if (itemCount <= 0)
                return new OperateResult<GVAR[]> { IsSuccess = false, Message = "条数无效" };

            var items = new GVAR[itemCount];
            for (int i = 0; i < itemCount; i++)
            {
                string itemAddr = PlcXinjeHelper.OffsetDAddress(startXinje, i * GVAR.WORD_COUNT);
                var one = ReadGvarItemFromPlc(plc, itemAddr);
                if (!one.IsSuccess)
                    return OperateResult.CreateFailedResult<GVAR[]>(one);
                items[i] = one.Content;
            }

            return OperateResult.CreateSuccessResult(items);
        }

        public static OperateResult WriteGvarItemToPlc(XinJETcpNet plc, string itemBase, GVAR g)
        {
            var wr = plc.Write(WordAddr(itemBase, 0), g.type1);
            if (!wr.IsSuccess) return CopyOperateFailure(wr);
            wr = plc.Write(WordAddr(itemBase, 2), g.spVec3_p0.x);
            if (!wr.IsSuccess) return CopyOperateFailure(wr);
            wr = plc.Write(WordAddr(itemBase, 4), g.spVec3_p0.y);
            if (!wr.IsSuccess) return CopyOperateFailure(wr);
            wr = plc.Write(WordAddr(itemBase, 6), g.spVec3_p0.z);
            if (!wr.IsSuccess) return CopyOperateFailure(wr);
            wr = plc.Write(WordAddr(itemBase, 8), g.spVec3_p1.x);
            if (!wr.IsSuccess) return CopyOperateFailure(wr);
            wr = plc.Write(WordAddr(itemBase, 10), g.spVec3_p1.y);
            if (!wr.IsSuccess) return CopyOperateFailure(wr);
            wr = plc.Write(WordAddr(itemBase, 12), g.spVec3_p1.z);
            if (!wr.IsSuccess) return CopyOperateFailure(wr);
            wr = plc.Write(WordAddr(itemBase, 14), g.cx);
            if (!wr.IsSuccess) return CopyOperateFailure(wr);
            wr = plc.Write(WordAddr(itemBase, 16), g.cy);
            if (!wr.IsSuccess) return CopyOperateFailure(wr);
            wr = plc.Write(WordAddr(itemBase, 18), g.r);
            if (!wr.IsSuccess) return CopyOperateFailure(wr);
            wr = plc.Write(WordAddr(itemBase, 20), g.start_deg);
            if (!wr.IsSuccess) return CopyOperateFailure(wr);
            wr = plc.Write(WordAddr(itemBase, 22), g.end_deg);
            if (!wr.IsSuccess) return CopyOperateFailure(wr);
            wr = plc.Write(WordAddr(itemBase, 24), g.z0);
            if (!wr.IsSuccess) return CopyOperateFailure(wr);
            wr = plc.Write(WordAddr(itemBase, 26), g.z1);
            if (!wr.IsSuccess) return CopyOperateFailure(wr);
            return OperateResult.CreateSuccessResult();
        }

        /// <summary>逐条写入（每条 28 字）。</summary>
        public static OperateResult WriteGvarList(XinJETcpNet plc, string startXinje, GVAR[] items)
        {
            if (items == null || items.Length == 0)
                return new OperateResult { IsSuccess = false, Message = "GVAR 列表为空" };

            for (int i = 0; i < items.Length; i++)
            {
                string itemAddr = PlcXinjeHelper.OffsetDAddress(startXinje, i * GVAR.WORD_COUNT);
                var wr = WriteGvarItemToPlc(plc, itemAddr, items[i]);
                if (!wr.IsSuccess)
                    return CopyOperateFailure(wr);
            }

            return OperateResult.CreateSuccessResult();
        }

        public static OperateResult SendGvarList(XinJETcpNet plc, string startXinje, GVAR[] items, out int totalWords)
        {
            totalWords = (items?.Length ?? 0) * GVAR.WORD_COUNT;
            return WriteGvarList(plc, startXinje, items);
        }
    }
}
