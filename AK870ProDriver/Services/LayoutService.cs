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
        /// <summary>灯效模式名（language/2052.lan 521-540，下拉顺序）。UI 索引 ≠ 协议 mode：索引 0..18 → 协议 mode 1..19，索引 19（关闭背光）→ 协议 mode 0。</summary>
        public static readonly string[] LightModes =
        {
            "静态恒亮","单点亮","单熄灭","繁星点点","漫天飞雪",
            "百花争艳","动态呼吸","光谱循环","彩泉涌动","多彩纵横",
            "随波逐流","峰回路转","一触即发","一石二鸟","涟漪扩散",
            "川流不息","重峦叠嶂","斜风细雨","来回穿梭","关闭背光"
        };

        public static (int mode, int bright, int speed) Defaults = (0, 5, 3);

        /// <summary>UI 下拉索引 → 协议 mode 号（官方 t_light_data: mode 1..19 为灯效, 0 为关闭背光）。</summary>
        public static int ProtocolModeOf(int uiIndex)
            => uiIndex < 0 ? 1 : (uiIndex >= LightModes.Length - 1 ? 0 : uiIndex + 1);

        /// <summary>音乐律动显示模式（language/2052.lan 106-120，MComboBox AddItem data=1..15；
        /// 设备命令为单字节 foremode，05 01 帧 / 0x4346B0 USB 序列，键盘本地拾音）。索引 0..14 → foremode 1..15。</summary>
        public static readonly string[] MusicModes =
        {
            "绿色/黄色/红色","彩虹色","彩虹色反向","色谱","频谱循环",
            "白色","红色","橙色","黄色","绿色",
            "青色","无","蓝色","紫色","屏光联动"
        };

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
                        Left = (int?)el.Attribute("rect_left") ?? 0,
                        Top = (int?)el.Attribute("rect_top") ?? 0,
                        Width = (int?)el.Attribute("rect_width") ?? 0,
                        Height = (int?)el.Attribute("rect_height") ?? 0,
                    };
                    var rc = ((string?)el.Attribute("row_col") ?? "0#0").Split('#');
                    if (rc.Length == 2) { k.Row = int.Parse(rc[0]); k.Col = int.Parse(rc[1]); }
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
