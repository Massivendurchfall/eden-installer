using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace EdenInstaller
{
    /// <summary>Asks what should stay when Eden is uninstalled. Both boxes start checked, so nothing is lost by accident.</summary>
    sealed class UninstallDialog : Window
    {
        readonly CheckBox keepGames, keepData;
        readonly TextBlock warning;

        public UninstallChoice Answer { get; private set; }

        public UninstallDialog(Window owner, SetupPaths p, long gamesBytes, long dataBytes)
        {
            Owner = owner;
            Title = L.T("Eden deinstallieren", "Uninstall Eden");
            SizeToContent = SizeToContent.WidthAndHeight;
            ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ShowInTaskbar = false;
            FontFamily = new FontFamily("Segoe UI");
            UseLayoutRounding = true;
            Background = (Brush)FindResource("BgBrush");
            SourceInitialized += (s, e) => Look.DarkTitleBar(this);

            string dlc = Path.GetFileName(p.DlcDir);
            string data = ShortData(p.DataDir);

            var panel = new StackPanel { Width = 470 };
            panel.Children.Add(Text(L.T("Eden deinstallieren?", "Uninstall Eden?"), 16, "TextBrush", FontWeights.SemiBold, 0));
            panel.Children.Add(Text(L.T($"Entfernt Eden aus {p.InstallDir} und die Verknüpfung auf dem Desktop.",
                $"Removes Eden from {p.InstallDir} and the shortcut on the desktop."), 12.5, "SubTextBrush", FontWeights.Normal, 6));

            keepGames = Check(L.T("Spiele, Updates und DLCs behalten", "Keep games, updates and DLCs"),
                L.T($"Die Ordner „Games“ und „{dlc}“ bleiben, zusammen {EdenSetup.Size(gamesBytes)}.",
                    $"The “Games” and “{dlc}” folders stay, {EdenSetup.Size(gamesBytes)} in total."), 18);
            keepData = Check(L.T("Spielstände und Einstellungen behalten", "Keep saves and settings"),
                L.T($"Bleiben in {data}, dazu Keys, Firmware und Mods, zusammen {EdenSetup.Size(dataBytes)}.",
                    $"They stay in {data}, along with keys, firmware and mods, {EdenSetup.Size(dataBytes)} in total."), 12);
            panel.Children.Add(keepGames);
            panel.Children.Add(keepData);

            warning = Text(L.T("Was nicht angehakt ist, wird endgültig gelöscht.", "Anything unchecked is deleted for good."),
                12.5, "ErrorBrush", FontWeights.SemiBold, 14);
            warning.Visibility = Visibility.Collapsed;
            panel.Children.Add(warning);

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
            var cancel = Button(L.T("Abbrechen", "Cancel"), "SecondaryButton");
            cancel.Click += (s, e) => Close();
            var uninstall = Button(L.T("Deinstallieren", "Uninstall"), "DangerButton");
            uninstall.Margin = new Thickness(10, 0, 0, 0);
            uninstall.Click += (s, e) => Confirm();
            buttons.Children.Add(cancel);
            buttons.Children.Add(uninstall);
            panel.Children.Add(buttons);

            Content = new Border { Background = Background, Padding = new Thickness(22, 18, 22, 20), Child = panel };
        }

        /// <summary>Sets both boxes, for the test mode.</summary>
        public void Preset(bool games, bool data)
        {
            keepGames.IsChecked = games;
            keepData.IsChecked = data;
        }

        public void Confirm()
        {
            Answer = new UninstallChoice { KeepGames = keepGames.IsChecked == true, KeepData = keepData.IsChecked == true };
            DialogResult = true;
        }

        void UpdateWarning() =>
            warning.Visibility = keepGames.IsChecked == true && keepData.IsChecked == true ? Visibility.Collapsed : Visibility.Visible;

        /// <summary>%APPDATA%\eden instead of the long path, when it is the usual place.</summary>
        static string ShortData(string dir)
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            return dir.StartsWith(appData + "\\", StringComparison.OrdinalIgnoreCase) ? "%APPDATA%" + dir.Substring(appData.Length) : dir;
        }

        CheckBox Check(string title, string detail, double top)
        {
            var content = new StackPanel();
            content.Children.Add(Text(title, 13.5, "TextBrush", FontWeights.Normal, 0));
            content.Children.Add(Text(detail, 12, "MutedBrush", FontWeights.Normal, 2));
            var box = new CheckBox { Style = (Style)FindResource("DarkCheck"), Content = content, IsChecked = true, Margin = new Thickness(0, top, 0, 0) };
            box.Checked += (s, e) => UpdateWarning();
            box.Unchecked += (s, e) => UpdateWarning();
            return box;
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

        Button Button(string label, string style) => new Button
        {
            Content = label,
            Style = (Style)FindResource(style),
            FontSize = 13.5,
            Padding = new Thickness(18, 8, 18, 8),
        };
    }
}
