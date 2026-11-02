using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Xml.Linq;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

[assembly: AssemblyTitle("Copy · 剪贴板")]
[assembly: AssemblyDescription("轻量本地剪贴板历史")]
[assembly: AssemblyVersion("1.0.0.0")]

namespace CopyDesk
{
    internal static class Program
    {
        internal static readonly string Root = AppDomain.CurrentDomain.BaseDirectory;
        internal static string DataFolder = Path.Combine(Root, "data");
        internal static readonly uint ShowMessage = Native.RegisterWindowMessage("CopyDesk.Show.Local.v1");
        [STAThread]
        static int Main(string[] args)
        {
            if (args.Contains("--self-test")) return SelfTests.Run();
            bool test = args.Contains("--test-mode");
            if (test) DataFolder = Path.Combine(Root, "tests", "ui-data");
            bool first;
            using (var mutex = new Mutex(true, test ? "Local\\CopyDesk.Test.v1" : "Local\\CopyDesk.Main.v1", out first))
            {
                if (!first) { Native.SendNotifyMessage(new IntPtr(0xffff), ShowMessage, IntPtr.Zero, IntPtr.Zero); return 0; }
                try
                {
                    var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                    app.DispatcherUnhandledException += delegate(object s, DispatcherUnhandledExceptionEventArgs e)
                    {
                        Log(e.Exception); e.Handled = true;
                        MessageBox.Show("操作未完成：" + e.Exception.Message, "Copy", MessageBoxButton.OK, MessageBoxImage.Warning);
                    };
                    using (var controller = new Controller(test, args.Contains("--background"))) app.Run();
                    return 0;
                }
                catch (Exception ex) { Log(ex); MessageBox.Show("Copy 无法启动：" + ex.Message, "Copy", MessageBoxButton.OK, MessageBoxImage.Error); return 1; }
                finally { mutex.ReleaseMutex(); }
            }
        }
        internal static void Log(Exception ex)
        {
            try
            {
                Directory.CreateDirectory(DataFolder); string path = Path.Combine(DataFolder, "errors.log");
                if (File.Exists(path) && new FileInfo(path).Length > 1024 * 1024) File.WriteAllText(path, "");
                // Never log clipboard content or payloads.
                File.AppendAllText(path, DateTime.Now.ToString("s") + " " + ex.GetType().Name + " " + ex.StackTrace + Environment.NewLine);
            }
            catch { }
        }
    }

    internal sealed class Controller : IDisposable
    {
        readonly Window window;
        readonly HistoryStore store;
        readonly TextBox search;
        readonly ListBox list;
        readonly TextBlock status, count;
        readonly DispatcherTimer retry = new DispatcherTimer(), debounce = new DispatcherTimer(), feedback = new DispatcherTimer();
        readonly Forms.NotifyIcon tray;
        readonly HwndSource source;
        readonly IntPtr hwnd;
        readonly bool test;
        bool paused, exiting, clipboardReady, hotkeyReady;
        uint sequence;
        int retries;
        string filter = "all";
        ContextMenu menu;
        public Controller(bool testMode, bool background)
        {
            test = testMode;
            store = new HistoryStore(Program.DataFolder);
            store.UpgradeImageFiles();
            using (Stream xaml = Assembly.GetExecutingAssembly().GetManifestResourceStream("Main.xaml")) window = (Window)XamlReader.Load(xaml);
            Application.Current.MainWindow = window;
            if (test) window.Title = "Copy · 测试窗口";
            using (Stream icon = Assembly.GetExecutingAssembly().GetManifestResourceStream("Copy.ico")) window.Icon = BitmapDecoder.Create(icon, BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames.OrderByDescending(frame => frame.PixelWidth).First();
            search = (TextBox)window.FindName("SearchBox"); list = (ListBox)window.FindName("HistoryList");
            status = (TextBlock)window.FindName("StatusText"); count = (TextBlock)window.FindName("CountText");
            LoadPosition();
            ((FrameworkElement)window.FindName("TitleBar")).MouseLeftButtonDown += delegate(object sender, MouseButtonEventArgs e)
            {
                if (FindButton(e.OriginalSource as DependencyObject) == null && e.LeftButton == MouseButtonState.Pressed) window.DragMove();
            };
            ((Button)window.FindName("CloseButton")).Click += delegate { window.Hide(); };
            ((Button)window.FindName("PauseButton")).Click += delegate { TogglePause(); };
            ((Button)window.FindName("MenuButton")).Click += delegate { OpenMenu(); };
            search.TextChanged += delegate { debounce.Stop(); debounce.Start(); };
            debounce.Interval = TimeSpan.FromMilliseconds(150); debounce.Tick += delegate { debounce.Stop(); Refresh(); };
            feedback.Interval = TimeSpan.FromSeconds(4); feedback.Tick += delegate { feedback.Stop(); ShowStatus(); };
            retry.Interval = TimeSpan.FromMilliseconds(90); retry.Tick += delegate { retry.Stop(); Capture(); };
            foreach (ToggleButton button in ((UniformGrid)window.FindName("Filters")).Children)
                button.Click += delegate(object sender, RoutedEventArgs e)
                {
                    filter = (string)((ToggleButton)sender).Tag;
                    foreach (ToggleButton other in ((UniformGrid)window.FindName("Filters")).Children) other.IsChecked = (string)other.Tag == filter;
                    Refresh();
                };
            list.AddHandler(Button.ClickEvent, new RoutedEventHandler(ItemAction));
            list.PreviewMouseWheel += delegate(object sender, MouseWheelEventArgs e)
            {
                var scroller = FindChild<ScrollViewer>(list);
                if (scroller == null) return;
                scroller.ScrollToVerticalOffset(scroller.VerticalOffset - e.Delta * 42.0 / 120.0);
                e.Handled = true;
            };
            window.PreviewKeyDown += KeyDown;
            window.Closing += delegate(object sender, CancelEventArgs e)
            {
                SavePosition(); if (!exiting) { e.Cancel = true; window.Hide(); }
            };
            window.StateChanged += delegate { if (window.WindowState == WindowState.Minimized) { window.Hide(); window.WindowState = WindowState.Normal; } };
            hwnd = new WindowInteropHelper(window).EnsureHandle();
            source = HwndSource.FromHwnd(hwnd); source.AddHook(WndProc);
            sequence = Native.GetClipboardSequenceNumber();
            clipboardReady = Native.AddClipboardFormatListener(hwnd);
            hotkeyReady = !test && Native.RegisterHotKey(hwnd, 1, 0x4000 | 0x0001 | 0x0002, 0x56);
            tray = new Forms.NotifyIcon();
            using (Stream icon = Assembly.GetExecutingAssembly().GetManifestResourceStream("Copy.ico")) tray.Icon = new System.Drawing.Icon(icon);
            tray.Text = test ? "Copy · 测试" : "Copy · 正在记录";
            var trayMenu = new Forms.ContextMenuStrip();
            trayMenu.Items.Add("打开剪贴板  Ctrl + Alt + V", null, delegate { Show(); });
            var pauseItem = new Forms.ToolStripMenuItem("暂停记录");
            pauseItem.Click += delegate { TogglePause(); };
            trayMenu.Items.Add(pauseItem);
            trayMenu.Opening += delegate { pauseItem.Text = paused ? "继续记录" : "暂停记录"; };
            trayMenu.Items.Add(new Forms.ToolStripSeparator());
            trayMenu.Items.Add("退出 Copy", null, delegate { Exit(); });
            tray.ContextMenuStrip = trayMenu; tray.Visible = true;
            tray.MouseClick += delegate(object sender, Forms.MouseEventArgs e) { if (e.Button == Forms.MouseButtons.Left) Show(); };
            Refresh(); ShowStatus();
            if (!background) Show();
            if (!clipboardReady) Tell("监听启动失败，请退出后重新打开", true);
            else if (!hotkeyReady && !test) Tell("快捷键被占用，可点击托盘图标打开", true);
        }
        static Button FindButton(DependencyObject obj)
        {
            while (obj != null)
            {
                if (obj is Button) return (Button)obj;
                if (obj is Visual || obj is System.Windows.Media.Media3D.Visual3D) obj = VisualTreeHelper.GetParent(obj);
                else obj = LogicalTreeHelper.GetParent(obj);
            }
            return null;
        }
        IntPtr WndProc(IntPtr handle, int message, IntPtr wp, IntPtr lp, ref bool handled)
        {
            if (message == 0x031D)
            {
                retries = 0;
                if (!paused) { retry.Stop(); window.Dispatcher.BeginInvoke(new Action(Capture), DispatcherPriority.Background); }
            }
            else if (message == 0x0312 || (uint)message == Program.ShowMessage) { if (window.IsVisible && window.IsActive && message == 0x0312) window.Hide(); else Show(); handled = true; }
            return IntPtr.Zero;
        }
        void Capture()
        {
            if (paused || exiting) return;
            uint before = Native.GetClipboardSequenceNumber();
            if (before == sequence) return;
            try
            {
                Clip item = ClipboardService.Read();
                if (before != Native.GetClipboardSequenceNumber()) { retry.Start(); return; }
                // Commit sequence only after successful persistence, so failures are visible.
                if (item != null && store.Add(item)) Refresh(true);
                sequence = before; retries = 0;
            }
            catch (COMException ex)
            {
                if (++retries < 8) retry.Start(); else { sequence = before; Program.Log(ex); Tell("剪贴板忙，本次未记录，请重新复制", true); }
            }
            catch (Exception ex) { sequence = before; Program.Log(ex); Tell(ex is IOException ? ex.Message : "这次内容未能读取，请重新复制", true); }
        }
        internal static T FindChild<T>(DependencyObject parent) where T : DependencyObject
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is T) return (T)child;
                var nested = FindChild<T>(child); if (nested != null) return nested;
            }
            return null;
        }
        void Refresh(bool preserveScroll = false)
        {
            var scroller = FindChild<ScrollViewer>(list);
            double offset = preserveScroll && scroller != null ? scroller.VerticalOffset : 0;
            var items = store.List(search.Text, filter);
            list.ItemsSource = items;
            if (offset > 0) window.Dispatcher.BeginInvoke(new Action(delegate { scroller.ScrollToVerticalOffset(offset); }), DispatcherPriority.Loaded);
            ((FrameworkElement)window.FindName("EmptyPanel")).Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            bool empty = store.Count == 0;
            ((TextBlock)window.FindName("EmptyTitle")).Text = empty ? "从下一次复制开始" : "没有找到相关记录";
            ((TextBlock)window.FindName("EmptyHint")).Text = empty ? "复制文字、图片或文件，它们会出现在这里" : "换个关键词，或切换上方分类试试";
            count.Text = (search.Text.Length > 0 || filter != "all" ? items.Count + " / " : "") + store.Count + " 条";
        }
        void ItemAction(object sender, RoutedEventArgs e)
        {
            var button = e.OriginalSource as Button ?? FindButton(e.OriginalSource as DependencyObject);
            if (button == null) return;
            var item = button.DataContext as Clip; if (item == null) return;
            switch (button.Tag as string)
            {
                case "copy": Copy(item); break;
                case "detail": Detail(item); break;
                case "pin": store.Pin(item.Id, !item.Pinned); Refresh(); break;
                case "delete": store.Delete(item.Id); Refresh(); Tell("已删除这条记录"); break;
            }
            e.Handled = true;
        }
        void Copy(Clip item)
        {
            try { ClipboardService.Write(item, item.Kind == "image" ? store.GetImage(item.Id) : null); Tell("已复制，回到目标窗口按 Ctrl + V"); }
            catch (Exception ex) { Program.Log(ex); Tell(ex is IOException ? ex.Message : "剪贴板忙，请再点一次", true); }
        }
        void KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape) { window.Hide(); e.Handled = true; }
            else if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control) { search.Focus(); search.SelectAll(); e.Handled = true; }
            else if (e.Key == Key.Down && search.IsKeyboardFocusWithin && list.Items.Count > 0) { list.SelectedIndex = 0; list.Focus(); e.Handled = true; }
            else if (e.Key == Key.Enter && list.IsKeyboardFocusWithin && list.SelectedItem is Clip) { Copy((Clip)list.SelectedItem); e.Handled = true; }
        }
        void Show()
        {
            ClampPosition(); window.Show(); window.WindowState = WindowState.Normal; window.Activate(); search.Focus();
        }
        void TogglePause()
        {
            paused = !paused; retry.Stop(); sequence = Native.GetClipboardSequenceNumber();
            ((Button)window.FindName("PauseButton")).Content = paused ? "继续" : "暂停";
            tray.Text = paused ? "Copy · 已暂停" : "Copy · 正在记录"; ShowStatus();
        }
        void ShowStatus()
        {
            status.Text = !clipboardReady ? "● 监听未启动" : paused ? "● 已暂停记录" : "● 正在记录 · 仅保存在本机";
            status.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(paused || !clipboardReady ? "#B1853C" : "#338A6B"));
        }
        void Tell(string text, bool error = false)
        {
            status.Text = text; status.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(error ? "#C16F33" : "#60796E"));
            feedback.Stop(); feedback.Interval = TimeSpan.FromSeconds(error ? 10 : 4); feedback.Start();
            if (error && !window.IsVisible) { tray.BalloonTipTitle = "Copy"; tray.BalloonTipText = text; tray.ShowBalloonTip(4000); }
        }
        MenuItem Menu(string title, Action action)
        {
            var item = new MenuItem { Header = title }; item.Click += delegate { action(); }; return item;
        }
        void OpenMenu()
        {
            menu = new ContextMenu { FontFamily = window.FontFamily, FontSize = 12 };
            var top = Menu("窗口始终置顶", delegate { window.Topmost = !window.Topmost; SavePosition(); }); top.IsCheckable = true; top.IsChecked = window.Topmost; menu.Items.Add(top);
            var startup = Menu("开机启动（收起到托盘）", ToggleStartup); startup.IsCheckable = true; startup.IsChecked = StartupEnabled(); startup.IsEnabled = !test; menu.Items.Add(startup);
            menu.Items.Add(new Separator());
            menu.Items.Add(Menu("清空普通记录（保留置顶）", delegate { Clear(false); }));
            menu.Items.Add(Menu("清空全部记录…", delegate { Clear(true); }));
            menu.Items.Add(new Separator());
            menu.Items.Add(Menu("打开数据文件夹", delegate { Process.Start(new ProcessStartInfo { FileName = Program.DataFolder, UseShellExecute = true }); }));
            menu.Items.Add(Menu("使用说明", delegate { Process.Start(new ProcessStartInfo { FileName = Path.Combine(Program.Root, "使用说明.txt"), UseShellExecute = true }); }));
            menu.Items.Add(Menu("退出 Copy", Exit));
            menu.PlacementTarget = (Button)window.FindName("MenuButton"); menu.Placement = PlacementMode.Top; menu.IsOpen = true;
        }
        void Clear(bool all)
        {
            string prompt = all ? "清空全部历史，包括置顶记录？此操作不能撤销。" : "清空普通历史记录？置顶记录会保留。";
            if (MessageBox.Show(window, prompt, "Copy", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
            store.Clear(all); Refresh(); Tell("历史记录已清空");
        }
        bool StartupEnabled()
        {
            using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run"))
                return key != null && String.Equals(key.GetValue("CopyClipboard") as string, StartupCommand(), StringComparison.OrdinalIgnoreCase);
        }
        string StartupCommand() { return "\"" + Assembly.GetExecutingAssembly().Location + "\" --background"; }
        void ToggleStartup()
        {
            bool enabled = StartupEnabled();
            using (var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run"))
            { if (enabled) key.DeleteValue("CopyClipboard", false); else key.SetValue("CopyClipboard", StartupCommand()); }
            Tell(enabled ? "已关闭开机启动" : "已开启开机启动");
        }
        void Detail(Clip item)
        {
            var dialog = new Window { Title = item.Label + " · 完整内容", Owner = window, Width = 560, Height = 460, MinWidth = 360, MinHeight = 300,
                WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = Brushes.White, FontFamily = window.FontFamily, FontSize = 13 };
            var panel = new DockPanel { Margin = new Thickness(18) };
            var footer = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
            var copy = new Button { Content = "复制内容", Padding = new Thickness(18, 8, 18, 8), Margin = new Thickness(8, 0, 0, 0) };
            var done = new Button { Content = "关闭", Padding = new Thickness(18, 8, 18, 8) };
            copy.Click += delegate { Copy(item); dialog.Close(); }; done.Click += delegate { dialog.Close(); };
            footer.Children.Add(done); footer.Children.Add(copy); DockPanel.SetDock(footer, Dock.Bottom); panel.Children.Add(footer);
            if (item.Kind == "image")
            {
                var image = new Image { Source = Clip.Decode(store.GetImage(item.Id)), Stretch = Stretch.Uniform };
                panel.Children.Add(image);
            }
            else
            {
                var content = item.Kind == "files" ? String.Join(Environment.NewLine, ClipboardService.FilePaths(item)) : item.Content;
                panel.Children.Add(new TextBox { Text = content, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, BorderThickness = new Thickness(0), Background = Brushes.Transparent });
            }
            dialog.Content = panel; dialog.PreviewKeyDown += delegate(object sender, KeyEventArgs e) { if (e.Key == Key.Escape) dialog.Close(); }; dialog.ShowDialog();
        }
        void LoadPosition()
        {
            window.Left = SystemParameters.WorkArea.Right - window.Width - 30; window.Top = SystemParameters.WorkArea.Bottom - window.Height - 30;
            try
            {
                string path = Path.Combine(Program.DataFolder, "settings.xml"); if (!File.Exists(path)) return;
                XElement settings = XElement.Load(path);
                window.Width = Math.Max(340, Math.Min(700, (double?)settings.Element("width") ?? 380));
                window.Height = (int?)settings.Element("layoutVersion") == 2 ? Math.Max(300, Math.Min(900, (double?)settings.Element("height") ?? 340)) : 340;
                window.Left = (double?)settings.Element("left") ?? window.Left; window.Top = (double?)settings.Element("top") ?? window.Top;
                window.Topmost = (bool?)settings.Element("topmost") ?? false;
            }
            catch (Exception ex) { Program.Log(ex); }
            ClampPosition();
        }
        void ClampPosition()
        {
            // Keep the panel reachable after monitor or display-scale changes.
            Rect area = SystemParameters.WorkArea;
            window.Width = Math.Min(window.Width, area.Width); window.Height = Math.Min(window.Height, area.Height);
            window.Left = Math.Max(area.Left, Math.Min(window.Left, area.Right - window.Width));
            window.Top = Math.Max(area.Top, Math.Min(window.Top, area.Bottom - window.Height));
        }
        void SavePosition()
        {
            try
            {
                new XElement("settings", new XElement("layoutVersion", 2), new XElement("width", window.Width), new XElement("height", window.Height), new XElement("left", window.Left), new XElement("top", window.Top), new XElement("topmost", window.Topmost)).Save(Path.Combine(Program.DataFolder, "settings.xml"));
            }
            catch (Exception ex) { Program.Log(ex); }
        }
        void Exit() { exiting = true; SavePosition(); Application.Current.Shutdown(); }
        public void Dispose()
        {
            exiting = true; retry.Stop(); debounce.Stop(); feedback.Stop();
            Native.RemoveClipboardFormatListener(hwnd); if (hotkeyReady) Native.UnregisterHotKey(hwnd, 1);
            source.RemoveHook(WndProc); tray.Visible = false; tray.Dispose(); store.Dispose();
        }
    }
}
