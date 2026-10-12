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
            // 主页功能设置
            public bool FnAltF4 { get; set; }
            public bool FnAltTab { get; set; }
            public int FnSwitch { get; set; }
            public int SleepIndex { get; set; }
            public int RespondLevel { get; set; } = 3;
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
