using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.Text.Json;

namespace Ak870Pro.Views
{
    public sealed class MacroItem
    {
        public string Name { get; set; } = "";
        public List<string> Steps { get; set; } = new(); // "KEY,delayMs"
    }

    public sealed partial class MacroPage : Page
    {
        private static string StorePath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Ak870Pro", "macros.json");

        private readonly List<MacroItem> _macros = new();

        public MacroPage()
        {
            InitializeComponent();
            Load();
            MacroList.SelectionChanged += (_, _) => EditSelected();
        }

        private void Load()
        {
            try
            {
                if (File.Exists(StorePath))
                {
                    var items = JsonSerializer.Deserialize<List<MacroItem>>(File.ReadAllText(StorePath));
                    if (items != null) _macros.AddRange(items);
                }
            }
            catch (Exception ex) { Services.AppLog.Log($"宏加载失败: {ex.Message}"); }
            Refresh();
        }

        private void Refresh()
        {
            MacroList.Items.Clear();
            foreach (var m in _macros) MacroList.Items.Add(m.Name);
        }

        private void EditSelected()
        {
            if (MacroList.SelectedIndex is int i && i >= 0 && i < _macros.Count)
            {
                NewNameBox.Text = _macros[i].Name;
                StepsBox.Text = string.Join(Environment.NewLine, _macros[i].Steps);
            }
        }

        private void Add_Click(object sender, RoutedEventArgs e)
        {
            var name = NewNameBox.Text.Trim();
            if (name.Length == 0) return;
            _macros.Add(new MacroItem { Name = name });
            Refresh();
            MacroList.SelectedIndex = _macros.Count - 1;
        }

        private void Delete_Click(object sender, RoutedEventArgs e)
        {
            if (MacroList.SelectedIndex is int i && i >= 0 && i < _macros.Count)
            {
                _macros.RemoveAt(i);
                Refresh();
            }
        }

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            if (MacroList.SelectedIndex is int i && i >= 0 && i < _macros.Count)
            {
                _macros[i].Name = NewNameBox.Text.Trim();
                _macros[i].Steps = StepsBox.Text
                    .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
            }
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(StorePath)!);
                File.WriteAllText(StorePath, JsonSerializer.Serialize(_macros, new JsonSerializerOptions { WriteIndented = true }));
                Refresh();
            }
            catch (Exception ex) { Services.AppLog.Log($"宏保存失败: {ex.Message}"); }
        }
    }
}
