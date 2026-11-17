using System;
using System.Windows.Media;

namespace LovenseConnector
{
    internal static class UiFonts
    {
        // Explicit assembly pack URI also works when MainWindow is loaded from embedded XAML.
        public static FontFamily Body { get; } = new FontFamily(new Uri("pack://application:,,,/HapticBridge;component/"), "./fonts/#Outfit");
    }
}
