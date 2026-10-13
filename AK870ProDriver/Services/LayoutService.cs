using System.Xml.Linq;

namespace Ak870Pro.Models
{
    /// <summary>单个键位（来自 layouts/rgb-keyboard.xml）。</summary>
    public sealed class KeyInfo
    {
        public int Code;          // HID usage (0x29 Esc ...)
        public string Name = "";
        public int KeyIndex;      // 协议键索引
        public int LightIndex;    // LED 索引 = 协议槽位
        public int Row, Col;      // row_col "r#c"
        public int Left, Top, Width, Height;
        public byte R, G, B;      // 当前分配的颜色 (0,0,0=未设置)
        public bool UserOn;       // 用户灯光模式下的开关

        public bool HasColor => R != 0 || G != 0 || B != 0;
    }
}

namespace Ak870Pro.Services
{
    /// <summary>解析 rgb-keyboard.xml（87 键布局 + 灯效配置）。</summary>
    public static class LayoutService
    {
        /// <summary>灯效模式名（下拉顺序）。UI 索引 ≠ 协议 mode：索引 0..18 → 协议 mode 1..19，索引 19（关闭背光）→ 协议 mode 0。</summary>
        public static readonly string[] LightModes =
        {
            "静态恒亮","单点亮","单熄灭","繁星点点","漫天飞雪",
            "百花争艳","动态呼吸","光谱循环","彩泉涌动","多彩纵横",
            "随波逐流","峰回路转","一触即发","一石二鸟","涟漪扩散",
            "川流不息","重峦叠嶂","斜风细雨","来回穿梭","关闭背光"
        };

        public static (int mode, int bright, int speed) Defaults = (0, 5, 3);

        /// <summary>UI 下拉索引 → 协议 mode 号（mode 1..19 为灯效, 0 为关闭背光）。</summary>
        public static int ProtocolModeOf(int uiIndex)
            => uiIndex < 0 ? 1 : (uiIndex >= LightModes.Length - 1 ? 0 : uiIndex + 1);

        /// <summary>音乐律动"显示模式"下拉顺序。
        /// 注意: UI 顺序索引 ≠ 协议 data — 蓝色/绿色与直觉顺序相反,
        /// 屏光联动=100 且仅 USB 通道存在。
        /// 青/无/紫 不在前景下拉。进帧必须用 MusicModeData 映射, 严禁 SelectedIndex+1。</summary>
        public static readonly string[] MusicModes =
        {
            "绿色/黄色/红色","彩虹色","彩虹色反向","色谱","频谱循环",
            "白色","红色","橙色","黄色","蓝色","绿色","屏光联动"
        };

        /// <summary>MusicModes 各项的协议 data 值。</summary>
        public static readonly int[] MusicModeData = { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 100 };

        /// <summary>音乐律动"背景灯光显示模式"下拉 (12 项, 无=0 开头)。</summary>
        public static readonly string[] MusicBgModes =
        {
            "无","白色","红色","橙色","黄色","蓝色","绿色",
            "绿色/黄色/红色","彩虹色","彩虹色反向","色谱","频谱循环"
        };

        /// <summary>MusicBgModes 各项的协议 data 值。</summary>
        public static readonly int[] MusicBgModeData = { 0, 6, 7, 8, 9, 10, 11, 1, 2, 3, 4, 5 };

        public static List<Models.KeyInfo> LoadKeys()
        {
            var list = new List<Models.KeyInfo>();
            try
            {
                var path = Path.Combine(AppContext.BaseDirectory, "Assets", "Layouts", "rgb-keyboard.xml");
                var doc = XDocument.Load(path);
                foreach (var el in doc.Descendants("key"))
                {
                    var k = new Models.KeyInfo
                    {
                        Code = ParseHex((string?)el.Attribute("code")),
                        Name = (string?)el.Attribute("name") ?? "",
                        KeyIndex = (int?)el.Attribute("key_index") ?? 0,
                        LightIndex = (int?)el.Attribute("light_index") ?? 0,
                        Left = (int?)el.Attribute("left") ?? 0,
                        Top = (int?)el.Attribute("top") ?? 0,
                        Width = (int?)el.Attribute("width") ?? 0,
                        Height = (int?)el.Attribute("height") ?? 0,
                        Row = (int?)el.Attribute("row") ?? 0,
                        Col = (int?)el.Attribute("col") ?? 0,
                    };
                    list.Add(k);
                }
            }
            catch (Exception ex)
            {
                AppLog.Log($"加载键位布局失败: {ex.Message}");
            }
            return list;
        }

        private static int ParseHex(string? s)
        {
            if (string.IsNullOrEmpty(s)) return 0;
            s = s.Trim();
            if (s.StartsWith("0x") || s.StartsWith("0X")) return Convert.ToInt32(s[2..], 16);
            return int.TryParse(s, out var v) ? v : 0;
        }
    }
}
