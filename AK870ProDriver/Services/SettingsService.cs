using System.Text.Json;

namespace Ak870Pro.Services
{
    /// <summary>设置持久化（JSON 存 %LOCALAPPDATA%\Ak870Pro\settings.json），供页面恢复上次值。</summary>
    public static class SettingsService
    {
        private static string StorePath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Ak870Pro", "settings.json");

        public sealed class Settings
        {
            // 灯光页
            public int LightMode { get; set; }
            public int LightBright { get; set; } = 5;
            public int LightSpeed { get; set; } = 3;
            public int MusicMode { get; set; }
            public int MusicAmp { get; set; } = 60;
            public int MusicBgMode { get; set; }
            public int MusicBgBright { get; set; } = 100;
            // 功能设置 (设置页): 0x00 设置包 payload[2]=禁ALT+TAB, [3]=禁ALT+F4, [4]=禁Win键
            public bool FnAltF4 { get; set; }
            public bool FnAltTab { get; set; }
            public bool FnWinKey { get; set; }
            public int SleepIndex { get; set; }
            public int RespondLevel { get; set; } = 3;
            // DEBUG 模式: 控制协议日志页可见性 (设置页连点 7 次版本号切换)
            public bool DebugMode { get; set; }
            // 专属配置方案 (主页管理, 按键页编辑当前方案)
            public List<RemapProfile> RemapProfiles { get; set; } = new();
            public string CurrentProfileId { get; set; } = "";
            // 旧字段 (单表) 保留兼容旧 settings.json, 不再使用
            public List<RemapEntry> KeyRemaps { get; set; } = new();
        }

        /// <summary>当前方案 (无则创建默认方案)。</summary>
        public static RemapProfile CurrentProfile
        {
            get
            {
                var s = Current;
                if (s.RemapProfiles.Count == 0)
                    s.RemapProfiles.Add(new RemapProfile { Name = "默认配置" });
                var p = s.RemapProfiles.FirstOrDefault(x => x.Id == s.CurrentProfileId)
                        ?? s.RemapProfiles[0];
                s.CurrentProfileId = p.Id;
                return p;
            }
        }

        /// <summary>DEBUG 模式切换通知 (UI 线程触发)。</summary>
        public static event Action<bool>? DebugModeChanged;

        public static void SetDebugMode(bool on)
        {
            if (Current.DebugMode == on) return;
            Current.DebugMode = on;
            Save();
            DebugModeChanged?.Invoke(on);
        }

        private static Settings? _cache;
        private static readonly object Gate = new();

        public static Settings Current
        {
            get
            {
                lock (Gate) { _cache ??= LoadInternal(); return _cache; }
            }
        }

        private static Settings LoadInternal()
        {
            try
            {
                if (File.Exists(StorePath))
                {
                    var s = JsonSerializer.Deserialize<Settings>(File.ReadAllText(StorePath));
                    if (s != null) return s;
                }
            }
            catch (Exception ex) { AppLog.Log($"设置加载失败: {ex.Message}"); }
            return new Settings();
        }

        public static void Save()
        {
            lock (Gate)
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(StorePath)!);
                    File.WriteAllText(StorePath, JsonSerializer.Serialize(Current, new JsonSerializerOptions { WriteIndented = true }));
                }
                catch (Exception ex) { AppLog.Log($"设置保存失败: {ex.Message}"); }
            }
        }
    }
}
