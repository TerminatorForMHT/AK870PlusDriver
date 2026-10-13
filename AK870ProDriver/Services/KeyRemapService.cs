using System.Text.Json.Serialization;

namespace Ak870Pro.Services
{
    public enum RemapKind { Default = 0, Disable = 1, Key = 2, Mouse = 3, Media = 4, WinShortcut = 5 }

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

    /// <summary>专属配置方案: PC 端方案管理 ("我的专属配置"), 应用时上传改键表到键盘。</summary>
    public sealed class RemapProfile
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string Name { get; set; } = "未命名配置";
        public List<RemapEntry> Entries { get; set; } = new();

        /// <summary>导出/导入文件格式 (独立于持久化, version 便于将来扩展)。</summary>
        public string ToJson() =>
            System.Text.Json.JsonSerializer.Serialize(new
            {
                version = 1,
                name = Name,
                entries = Entries.Select(e => new { slot = e.Slot, kind = (int)e.Kind, usage = e.Usage, mod = e.Mod })
            }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });

        public static RemapProfile? FromJson(string json)
        {
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(json);
                var root = doc.RootElement;
                var p = new RemapProfile { Name = root.TryGetProperty("name", out var n) ? n.GetString() ?? "导入配置" : "导入配置" };
                if (root.TryGetProperty("entries", out var arr))
                    foreach (var e in arr.EnumerateArray())
                        p.Entries.Add(new RemapEntry
                        {
                            Slot = e.GetProperty("slot").GetInt32(),
                            Kind = (RemapKind)e.GetProperty("kind").GetInt32(),
                            Usage = e.GetProperty("usage").GetByte(),
                            Mod = e.GetProperty("mod").GetByte()
                        });
                return p;
            }
            catch { return null; }
        }
    }

    /// <summary>
    /// 专属配置改键表编码:
    /// 576B = 144 槽 × 4B, 槽号 = key_index; 记录: 默认 [05 03 00 00], 禁用/未设置 [00 00 00 00],
    /// 键盘按键 [02 mod usage 00]; AA55@0x23E/0x23F (末槽高半字)。
    /// </summary>
    public static class KeyRemapService
    {
        public const int SlotCount = 144;
        public const int TableSize = SlotCount * 4; // 576 = 0x240

        // type 5 (鼠标): param 1..8 → record[1..2] LE 词值
        // (左键单击/中键单击/右键单击/双击/向上滚动/向下滚动/鼠标按钮4/鼠标按钮5)
        public static readonly int[] MouseWords = { 0x0101, 0x0401, 0x0201, 0x0301, 0x0103, 0x03ff, 0x0801, 0x1001 };
        // type 6 (多媒体): param 1..7 → record[1] consumer 码
        // (播放暂停/下一首/上一首/停止/音量+/音量-/静音)
        public static readonly byte[] MediaCodes = { 0xCD, 0xB7, 0xB6, 0xB5, 0xE9, 0xEA, 0xE2 };
        // type 7 (Windows快捷): param → record[1..2] LE 词值
        // (显示桌面/我的电脑/锁定计算机/关闭窗口/切换Windows/注销/睡眠/关机/重启)
        public static readonly Dictionary<int, int> WinWords = new()
        {
            [1] = 0x0807, [2] = 0x0808, [3] = 0x080F, [4] = 0x011A, [5] = 0x042B,
            [6] = 0x0106, [7] = 0x0119, [8] = 0x011B, [10] = 0x0201
        };

        public static byte[] BuildTable(IReadOnlyCollection<RemapEntry> entries)
        {
            var t = new byte[TableSize];
            // 语义: 全 0 = 禁用 (type 4 不写即保持 0), 默认透传 = [05 03] (type 1)。
            // 未配置槽必须先填默认, 否则整键盘会被禁用。
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
                    case RemapKind.Mouse: // [01 lo hi]
                        {
                            if (e.Usage < 1 || e.Usage > MouseWords.Length) break;
                            int w = MouseWords[e.Usage - 1];
                            t[off] = 0x01; t[off + 1] = (byte)(w & 0xFF); t[off + 2] = (byte)(w >> 8);
                        }
                        break;
                    case RemapKind.Media: // [03 code]
                        if (e.Usage < 1 || e.Usage > MediaCodes.Length) break;
                        t[off] = 0x03; t[off + 1] = MediaCodes[e.Usage - 1];
                        break;
                    case RemapKind.WinShortcut: // [02 lo hi]
                        {
                            if (!WinWords.TryGetValue(e.Usage, out var w)) break;
                            t[off] = 0x02; t[off + 1] = (byte)(w & 0xFF); t[off + 2] = (byte)(w >> 8);
                        }
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
                bool valid = e.Kind switch
                {
                    RemapKind.Disable => true,
                    RemapKind.Key => e.Usage != 0,
                    RemapKind.Mouse => e.Usage >= 1 && e.Usage <= MouseWords.Length,
                    RemapKind.Media => e.Usage >= 1 && e.Usage <= MediaCodes.Length,
                    RemapKind.WinShortcut => WinWords.ContainsKey(e.Usage),
                    _ => false
                };
                if (valid)
                    list.Add(new RemapEntry { Slot = e.Slot, Kind = e.Kind, Usage = e.Usage, Mod = e.Mod });
            }
            return list;
        }
    }
}
