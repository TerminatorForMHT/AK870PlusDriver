using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.Runtime.InteropServices;
using Ak870Pro.Services;

namespace Ak870Pro.Views
{
    public sealed partial class HomePage : Page
    {
        private bool Busy;
        private bool _suppressProfileEvent;

        public HomePage()
        {
            InitializeComponent();
            ReloadProfiles();
            // 事件一律在回填完成后挂接, 避免 InitializeComponent 期间同步触发
            ProfileCombo.SelectionChanged += ProfileCombo_SelectionChanged;
        }

        // ---------------- 我的专属配置 (方案管理) ----------------

        private void ReloadProfiles()
        {
            var s = SettingsService.Current;
            if (s.RemapProfiles.Count == 0)
            {
                // 迁移旧版单表数据到默认方案
                s.RemapProfiles.Add(new RemapProfile
                {
                    Name = "默认配置",
                    Entries = s.KeyRemaps.Select(e => new RemapEntry { Slot = e.Slot, Kind = e.Kind, Usage = e.Usage, Mod = e.Mod }).ToList()
                });
                SettingsService.Save();
            }
            var cur = SettingsService.CurrentProfile;
            _suppressProfileEvent = true;
            ProfileCombo.Items.Clear();
            foreach (var p in s.RemapProfiles)
                ProfileCombo.Items.Add(new ComboBoxItem { Content = p.Name, Tag = p.Id });
            for (int i = 0; i < ProfileCombo.Items.Count; i++)
                if (((ComboBoxItem)ProfileCombo.Items[i]!).Tag is string id && id == cur.Id)
                { ProfileCombo.SelectedIndex = i; break; }
            _suppressProfileEvent = false;
        }

        private RemapProfile CurrentProfile => SettingsService.CurrentProfile;

        private void ProfileCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressProfileEvent) return;
            if (ProfileCombo.SelectedItem is ComboBoxItem it && it.Tag is string id)
            {
                var s = SettingsService.Current;
                if (s.RemapProfiles.Any(p => p.Id == id))
                {
                    s.CurrentProfileId = id;
                    SettingsService.Save();
                }
            }
        }

        private async void ProfileApply_Click(object sender, RoutedEventArgs e)
        {
            if (!RequireConnected()) return;
            if (Busy) return;
            Busy = true; ProfileApplyBtn.IsEnabled = false;
            try
            {
                var p = CurrentProfile;
                var table = KeyRemapService.BuildTable(p.Entries);
                bool ok = await AppState.Device.UploadKeyRemapAsync(table, fnLayer: false);
                ToastService.Show(ok ? $"已应用配置「{p.Name}」" : "应用失败，详见协议日志", ok);
            }
            finally { Busy = false; ProfileApplyBtn.IsEnabled = true; }
        }

        private async void ProfileNew_Click(object sender, RoutedEventArgs e)
        {
            var name = await InputNameDialog("新建专属配置", "输入配置名称", "新建配置");
            if (name == null) return;
            var s = SettingsService.Current;
            s.RemapProfiles.Add(new RemapProfile { Name = name });
            s.CurrentProfileId = s.RemapProfiles[^1].Id;
            SettingsService.Save();
            ReloadProfiles();
            ToastService.Show($"已新建「{name}」", true);
        }

        private async void ProfileRename_Click(object sender, RoutedEventArgs e)
        {
            var p = CurrentProfile;
            var name = await InputNameDialog("重命名专属配置", "输入新名称", p.Name);
            if (name == null || name == p.Name) return;
            p.Name = name;
            SettingsService.Save();
            ReloadProfiles();
            ToastService.Show("已重命名", true);
        }

        private async void ProfileDelete_Click(object sender, RoutedEventArgs e)
        {
            var s = SettingsService.Current;
            if (s.RemapProfiles.Count <= 1) { ToastService.Show("至少保留一个配置", false); return; }
            var p = CurrentProfile;
            var dlg = new ContentDialog
            {
                Title = "删除专属配置",
                Content = $"确定删除「{p.Name}」吗？该操作不可恢复。",
                PrimaryButtonText = "删除",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = XamlRoot
            };
            if (await dlg.ShowAsync() != ContentDialogResult.Primary) return;
            s.RemapProfiles.Remove(p);
            s.CurrentProfileId = s.RemapProfiles[0].Id;
            SettingsService.Save();
            ReloadProfiles();
            ToastService.Show($"已删除「{p.Name}」", true);
        }

        private async void ProfileImport_Click(object sender, RoutedEventArgs e)
        {
            var path = PickFile(save: false);
            if (path == null) return;
            try
            {
                var p = RemapProfile.FromJson(File.ReadAllText(path));
                if (p == null) { ToastService.Show("导入失败：文件格式无效", false); return; }
                var s = SettingsService.Current;
                if (s.RemapProfiles.Any(x => x.Name == p.Name)) p.Name += " (导入)";
                s.RemapProfiles.Add(p);
                s.CurrentProfileId = p.Id;
                SettingsService.Save();
                ReloadProfiles();
                ToastService.Show($"已导入「{p.Name}」({p.Entries.Count} 条改键)", true);
            }
            catch (Exception ex)
            {
                AppLog.Log($"导入配置失败: {ex.Message}");
                ToastService.Show("导入失败，详见协议日志", false);
            }
            await Task.CompletedTask;
        }

        private async void ProfileExport_Click(object sender, RoutedEventArgs e)
        {
            var p = CurrentProfile;
            var path = PickFile(save: true);
            if (path == null) return;
            try
            {
                if (!path.EndsWith(".ak870profile", StringComparison.OrdinalIgnoreCase)) path += ".ak870profile";
                File.WriteAllText(path, p.ToJson());
                ToastService.Show($"已导出「{p.Name}」", true);
            }
            catch (Exception ex)
            {
                AppLog.Log($"导出配置失败: {ex.Message}");
                ToastService.Show("导出失败，详见协议日志", false);
            }
            await Task.CompletedTask;
        }

        private async Task<string?> InputNameDialog(string title, string placeholder, string initial)
        {
            var box = new TextBox { PlaceholderText = placeholder, Text = initial };
            var dlg = new ContentDialog
            {
                Title = title,
                Content = box,
                PrimaryButtonText = "确定",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = XamlRoot
            };
            if (await dlg.ShowAsync() != ContentDialogResult.Primary) return null;
            var name = box.Text.Trim();
            return name.Length == 0 ? null : name;
        }

        // ---------------- 文件选择 (comdlg32, unpackaged 下稳定) ----------------

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private class OPENFILENAME
        {
            public int lStructSize;
            public IntPtr hwndOwner;
            public IntPtr hInstance;
            public string lpstrFilter = "";
            public IntPtr lpstrCustomFilter;
            public int nMaxCustFilter;
            public int nFilterIndex;
            public IntPtr lpstrFile;
            public int nMaxFile = 520;
            public IntPtr lpstrFileTitle;
            public int nMaxFileTitle;
            public IntPtr lpstrInitialDir;
            public IntPtr lpstrTitle;
            public int Flags;
            public short nFileOffset;
            public short nFileExtension;
            public IntPtr lpstrDefExt;
            public IntPtr lCustData;
            public IntPtr lpfnHook;
            public IntPtr lpTemplateName;
            public IntPtr pvReserved;
            public int dwReserved;
            public int FlagsEx;
        }

        [DllImport("comdlg32.dll", CharSet = CharSet.Unicode)]
        private static extern bool GetOpenFileNameW(OPENFILENAME ofn);

        [DllImport("comdlg32.dll", CharSet = CharSet.Unicode)]
        private static extern bool GetSaveFileNameW(OPENFILENAME ofn);

        private static string? PickFile(bool save)
        {
            var buf = Marshal.AllocCoTaskMem(1042);
            try
            {
                var ofn = new OPENFILENAME
                {
                    lpstrFilter = "AK870 配置文件 (*.ak870profile)\0*.ak870profile\0所有文件 (*.*)\0*.*\0",
                    lpstrFile = buf,
                    nMaxFile = 520,
                    Flags = save ? 0x02 /*OFN_OVERWRITEPROMPT*/ : 0
                };
                ofn.lStructSize = Marshal.SizeOf(ofn);
                bool ok = save ? GetSaveFileNameW(ofn) : GetOpenFileNameW(ofn);
                return ok ? Marshal.PtrToStringUni(buf) : null;
            }
            finally { Marshal.FreeCoTaskMem(buf); }
        }

        // ---------------- 连接 ----------------

        private async void Connect_Click(object sender, RoutedEventArgs e)
        {
            if (Busy) return;
            Busy = true;
            var dev = AppState.Device;
            ConnectBtn.IsEnabled = false;
            AppState.NotifyStateChanged();
            try
            {
                AppLog.Log("用户点击重新扫描连接。");
                bool ok = await dev.ConnectAsync();
                ToastService.Show(ok ? "连接成功" : "未找到设备，请确认键盘已连接（无线模式请按一下按键唤醒）", ok);
            }
            catch (Exception ex)
            {
                AppLog.Log($"连接异常: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
                App.Log($"连接异常: {ex}");
                ToastService.Show("连接出现异常，详见协议日志", false);
            }
            finally
            {
                Busy = false;
                ConnectBtn.IsEnabled = true;
                DisconnectBtn.IsEnabled = dev.State == ConnState.Connected;
                AppState.NotifyStateChanged();
            }
        }

        private void Disconnect_Click(object sender, RoutedEventArgs e)
        {
            AppState.Device.Disconnect();
            DisconnectBtn.IsEnabled = false;
            AppState.NotifyStateChanged();
        }

        private async void Layer_Click(object sender, RoutedEventArgs e)
        {
            if (!RequireConnected()) return;
            if (Busy) return;
            Busy = true;
            try
            {
                int layer = int.Parse(((Button)sender).Tag.ToString()!);
                bool ok = await AppState.Device.SelectLayerAsync(layer);
                ToastService.Show(ok ? $"已选择层 {layer}" : "选择层失败", ok);
            }
            finally { Busy = false; }
        }

        private bool RequireConnected()
        {
            if (AppState.State == ConnState.Connected) return true;
            ToastService.Show("请先在主页连接设备", false);
            return false;
        }
    }
}
