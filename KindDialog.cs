using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;

namespace EdenInstaller
{
    /// <summary>Rückfrage, wenn eine Datei weder am Namen noch am Inhalt zu erkennen ist.</summary>
    sealed class KindDialog : Window
    {
        public GameKind? Answer { get; private set; }

        public KindDialog(Window owner, string fileName)
        {
            Owner = owner;
            Title = "Spiel hinzufügen";
            SizeToContent = SizeToContent.WidthAndHeight;
            ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ShowInTaskbar = false;
            FontFamily = new FontFamily("Segoe UI");
            UseLayoutRounding = true;
            Background = (Brush)FindResource("BgBrush");
            SourceInitialized += (s, e) => Look.DarkTitleBar(this);

            var panel = new StackPanel { Width = 470 };
            panel.Children.Add(Text("Was ist diese Datei?", 16, "TextBrush", FontWeights.SemiBold, 0));
            panel.Children.Add(Text(fileName, 13, "SubTextBrush", FontWeights.Normal, 6));
            panel.Children.Add(Text("Weder am Namen noch am Inhalt war zu erkennen, ob es ein Spiel, ein Update oder ein DLC ist. "
                + "Wähle selbst, in welchen Ordner sie kommt.", 12, "MutedBrush", FontWeights.Normal, 10));

            var buttons = new UniformGrid { Columns = 4, Margin = new Thickness(-3, 18, -3, 0) };
            buttons.Children.Add(Choice("Spiel", GameKind.Base, "PrimaryButton"));
            buttons.Children.Add(Choice("Update", GameKind.Update, "SecondaryButton"));
            buttons.Children.Add(Choice("DLC", GameKind.Dlc, "SecondaryButton"));
            buttons.Children.Add(Choice("Überspringen", null, "SecondaryButton"));
            panel.Children.Add(buttons);

            Content = new Border { Background = Background, Padding = new Thickness(22, 18, 22, 20), Child = panel };
        }

        /// <summary>Schließt mit dieser Antwort. null heißt überspringen, so wie das X oben rechts.</summary>
        public void Choose(GameKind? kind)
        {
            Answer = kind;
            DialogResult = true;
        }

        TextBlock Text(string text, double size, string brush, FontWeight weight, double top) => new TextBlock
        {
            Text = text,
            FontSize = size,
            FontWeight = weight,
            Foreground = (Brush)FindResource(brush),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, top, 0, 0),
        };

        Button Choice(string label, GameKind? kind, string style)
        {
            var button = new Button
            {
                Content = label,
                Style = (Style)FindResource(style),
                FontSize = 13,
                MinWidth = 0,
                Padding = new Thickness(8, 8, 8, 8),
                Margin = new Thickness(3, 0, 3, 0),
            };
            button.Click += (s, e) => Choose(kind);
            return button;
        }
    }

    static class Look
    {
        /// <summary>Dunkle Titelleiste wie der Rest, unter Windows 11 auch in der Fensterfarbe.</summary>
        public static void DarkTitleBar(Window window)
        {
            IntPtr hwnd = new WindowInteropHelper(window).Handle;
            int on = 1;
            if (Native.DwmSetWindowAttribute(hwnd, 20, ref on, sizeof(int)) != 0)
                Native.DwmSetWindowAttribute(hwnd, 19, ref on, sizeof(int));
            int caption = 0x00161012;   // #121016 als COLORREF (BGR)
            Native.DwmSetWindowAttribute(hwnd, 35, ref caption, sizeof(int));
        }
    }
}
