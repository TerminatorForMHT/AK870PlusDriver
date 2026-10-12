namespace Ak870Pro.Services
{
    /// <summary>全局单例状态。</summary>
    public static class AppState
    {
        public static KeyboardDevice Device { get; } = new();
        public static List<Models.KeyInfo> Keys { get; } = LayoutService.LoadKeys();

        public static event Action<ConnState>? StateChanged;

        public static ConnState State => Device.State;

        public static void NotifyStateChanged() => StateChanged?.Invoke(Device.State);
    }
}
