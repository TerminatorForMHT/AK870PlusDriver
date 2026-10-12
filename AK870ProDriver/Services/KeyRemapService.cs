using System.Text.Json.Serialization;

namespace Ak870Pro.Services
{
    public enum RemapKind { Default = 0, Disable = 1, Key = 2 }

    /// <summary>单键改键条目。Slot = 布局 key_index (1..121)。</summary>
    public sealed class RemapEntry
    {
        public int Slot { get; set; }
        public RemapKind Kind { get; set; } = RemapKind.Default;
        /// <summary>目标键 HID usage (Kind=Key 时有效)。</summary>
        public byte Usage { get; set; }
        /// <summary>修饰键位掩码: LCtrl=1 LShift=2 LAlt=4 LWin=8 RCtrl=0x10 RShift=0x20 RAlt=0x40 RWin=0x80。</summary>
        public byte Mod { get; set; }

        [JsonIgnore]
        public bool IsEmpty => Kind == RemapKind.Default && Usage == 0 && Mod == 0;
    }

    /// <summary>
    /// 专属配置改键表编码 — 官方 0x418590 组包逻辑还原:
    /// 576B = 144 槽 × 4B, 槽号 = key_index; 记录: 默认 [05 03 00 00], 禁用/未设置 [00 00 00 00],
    /// 键盘按键 [02 mod usage 00]; AA55@0x23E/0x23F (末槽高半字)。
    /// </summary>
    public static class KeyRemapService
    {
        public const int SlotCount = 144;
        public const int TableSize = SlotCount * 4; // 576 = 0x240

        public static byte[] BuildTable(IReadOnlyCollection<RemapEntry> entries)
        {
            var t = new byte[TableSize];
            // 官方语义: 全 0 = 禁用 (type 4 不写即保持 0), 默认透传 = [05 03] (type 1)。
            // 官方 config 列出所有键, 故未配置槽必须先填默认, 否则整键盘会被禁用。
            for (int s = 1; s < SlotCount; s++)
            {
                t[s * 4] = 0x05;
                t[s * 4 + 1] = 0x03;
            }
            foreach (var e in entries)
            {
                if (e.Slot < 1 || e.Slot >= SlotCount) continue;
                int off = e.Slot * 4;
                switch (e.Kind)
                {
                    case RemapKind.Default:
                        t[off] = 0x05; t[off + 1] = 0x03;
                        t[off + 2] = 0; t[off + 3] = 0;
                        break;
                    case RemapKind.Disable:
                        t[off] = 0; t[off + 1] = 0; t[off + 2] = 0; t[off + 3] = 0;
                        break;
                    case RemapKind.Key:
                        t[off] = 0x02;
                        t[off + 1] = e.Mod;
                        t[off + 2] = e.Usage;
                        t[off + 3] = 0;
                        break;
                }
            }
            t[0x23E] = 0xAA; t[0x23F] = 0x55;
            return t;
        }

        /// <summary>与默认无差异的条目视为无改键 (上传时省略, 固件默认透传)。</summary>
        public static List<RemapEntry> Normalize(IEnumerable<RemapEntry> entries)
        {
            var list = new List<RemapEntry>();
            foreach (var e in entries)
            {
                if (e.Kind == RemapKind.Disable || (e.Kind == RemapKind.Key && e.Usage != 0))
                    list.Add(new RemapEntry { Slot = e.Slot, Kind = e.Kind, Usage = e.Usage, Mod = e.Mod });
            }
            return list;
        }
    }
}
