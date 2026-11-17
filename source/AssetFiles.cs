using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace LovenseConnector
{
    // All application assets are bundled. Only user-created state is written to disk.
    internal static class AssetFiles
    {
        const string Prefix = "HapticBridge.Assets.";
        public static string DataDirectory
        {
            get
            {
                var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HapticBridge");
                Directory.CreateDirectory(path);
                return path;
            }
        }
        public static string PatternDirectory
        {
            get
            {
                var path = Path.Combine(DataDirectory, "patterns");
                Directory.CreateDirectory(path);
                return path;
            }
        }
        public static Stream Open(string name)
        {
            if (String.IsNullOrWhiteSpace(name) || name.Contains('/') || name.Contains('\\'))
                throw new ArgumentException("Invalid bundled asset name.");
            return Assembly.GetExecutingAssembly().GetManifestResourceStream(Prefix + name)
                ?? throw new FileNotFoundException("A required bundled asset is missing: " + name);
        }
        public static string ReadText(string name)
        {
            using (var reader = new StreamReader(Open(name), Encoding.UTF8)) return reader.ReadToEnd();
        }
        public static void ShowHelp(Window owner)
        {
            var names = Assembly.GetExecutingAssembly().GetManifestResourceNames()
                .Where(n => n.StartsWith(Prefix, StringComparison.Ordinal) && (n.EndsWith(".md", StringComparison.OrdinalIgnoreCase) || n.EndsWith(".txt", StringComparison.OrdinalIgnoreCase) || n.EndsWith(".rtf", StringComparison.OrdinalIgnoreCase) || n.EndsWith(".LICENSE", StringComparison.Ordinal)))
                .Select(n => n.Substring(Prefix.Length)).OrderBy(n => n == "HELP.md" ? "" : n).ToArray();
            var tabs = new TabControl { Margin = new Thickness(16) };
            foreach (var name in names)
            {
                if (name.EndsWith(".rtf", StringComparison.OrdinalIgnoreCase))
                {
                    var document = new FlowDocument();
                    var range = new TextRange(document.ContentStart, document.ContentEnd);
                    using (var stream = Open(name)) range.Load(stream, DataFormats.Rtf);
                    range.ApplyPropertyValue(TextElement.ForegroundProperty, Brushes.White);
                    var richText = new RichTextBox { Document = document, IsReadOnly = true,
                        VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(18),
                        Background = new SolidColorBrush(Color.FromRgb(27, 22, 38)), Foreground = Brushes.White };
                    tabs.Items.Add(new TabItem { Header = Path.GetFileNameWithoutExtension(name), Content = richText });
                    continue;
                }
                var text = new TextBox { Text = ReadText(name), IsReadOnly = true, TextWrapping = TextWrapping.Wrap,
                    AcceptsReturn = true, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    Padding = new Thickness(18), Background = new SolidColorBrush(Color.FromRgb(27, 22, 38)),
                    Foreground = Brushes.White, FontFamily = new FontFamily("Segoe UI"), FontSize = 14 };
                tabs.Items.Add(new TabItem { Header = name.Replace(".md", ""), Content = text });
            }
            if (tabs.Items.Count == 0) tabs.Items.Add(new TabItem { Header = "Help", Content = new TextBlock { Text = "Connect a device, choose a source, and use Stop all to end output." } });
            new Window { Owner = owner, Title = "HapticBridge help and licenses", Width = 840, Height = 650,
                MinWidth = 560, MinHeight = 400, WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Background = new SolidColorBrush(Color.FromRgb(13, 9, 24)), Content = tabs }.Show();
        }
    }
}
