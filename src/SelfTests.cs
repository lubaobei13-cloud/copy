using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Windows.Controls;
using System.Windows.Input;

namespace CopyDesk
{
    internal static class SelfTests
    {
        static readonly List<string> results = new List<string>();
        static int assertions;
        static void Check(bool condition, string label) { if (!condition) throw new Exception("FAIL: " + label); assertions++; results.Add("PASS: " + label); }
        static Clip Text(string text) { return new Clip { Kind = "text", Content = text }; }
        static void Pump()
        {
            var frame = new DispatcherFrame();
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
            timer.Tick += delegate { timer.Stop(); frame.Continue = false; }; timer.Start(); Dispatcher.PushFrame(frame);
        }
        static DataObject Fixture(string text)
        {
            var data = new DataObject(); data.SetText(text); data.SetData("CopyDesk.Test.Fixture", new MemoryStream(new byte[] { 1 })); return data;
        }
        static DataObject Snapshot()
        {
            var original = Clipboard.GetDataObject();
            var snapshot = new DataObject();
            if (original == null) return snapshot;
            foreach (string format in original.GetFormats(false))
            {
                try
                {
                    object value = original.GetData(format, false);
                    var ms = value as MemoryStream;
                    if (ms != null) value = new MemoryStream(ms.ToArray());
                    if (value != null) snapshot.SetData(format, value, false);
                }
                catch { }
            }
            return snapshot;
        }
        internal static int Run()
        {
            string testRoot = Path.Combine(Program.Root, "tests", "run-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
            Directory.CreateDirectory(testRoot);
            int code = 0;
            try
            {
                string unicode = "中文 😀 café\r\n100%_ 'quoted'\0tail";
                string core = Path.Combine(testRoot, "core");
                long pinnedId;
                using (var db = new HistoryStore(core, 3, 100000))
                {
                    var a = Text(unicode); Check(db.Add(a), "insert Unicode + multiline + NUL + quotes"); pinnedId = a.Id;
                    Check(!db.Add(Text(unicode)), "adjacent duplicate skipped");
                    Check(db.Count == 1 && db.List()[0].Content == unicode, "lossless text round trip");
                    Check(db.List("100%_").Count == 1 && db.List("不存在").Count == 0, "literal search and no match");
                    db.Pin(pinnedId, true);
                    for (int i = 0; i < 6; i++) db.Add(Text("record " + i));
                    Check(db.Count == 4, "bounded ordinary history");
                    Check(db.List()[0].Id == pinnedId && db.List("", "pinned").Count == 1, "pinned survives pruning and sorts first");
                    Check(db.List().Any(x => x.Content == "record 5") && !db.List().Any(x => x.Content == "record 0"), "oldest ordinary rows pruned");
                }
                using (var db = new HistoryStore(core))
                {
                    Check(db.Count == 4 && db.List()[0].Content == unicode, "history and pins survive reopen");
                    db.Clear(false); Check(db.Count == 1 && db.List()[0].Pinned, "clear ordinary preserves pins");
                    db.Delete(pinnedId); Check(db.Count == 0, "delete a pinned row");
                    db.Add(Text(unicode)); Check(db.Count == 1, "deleted latest content can be captured again");
                    db.Clear(true); Check(db.Count == 0, "clear all");
                }
                using (var db = new HistoryStore(Path.Combine(testRoot, "capacity"), 100, 100))
                {
                    var a = Text(new string('a', 35)); db.Add(a); db.Pin(a.Id, true);
                    db.Add(Text(new string('b', 35))); db.Add(Text(new string('c', 35)));
                    Check(db.Bytes <= 100 && db.Count == 2 && db.List()[0].Id == a.Id, "capacity pruning preserves pins");
                    bool rejected = false;
                    try { db.Add(Text(new string('d', 70))); } catch (IOException) { rejected = true; }
                    Check(rejected && db.Count == 2, "full pinned capacity rejects without data loss");
                }
                var pixels = new byte[4 * 24 * 16];
                for (int i = 0; i < pixels.Length; i += 4) { pixels[i] = 230; pixels[i + 1] = 110; pixels[i + 2] = 50; pixels[i + 3] = 255; }
                var bitmap = BitmapSource.Create(24, 16, 96, 96, PixelFormats.Bgra32, null, pixels, 24 * 4); bitmap.Freeze();
                byte[] image = ClipboardService.Encode(bitmap);
                string pngPath = Path.Combine(testRoot, "微信图片.png"); File.WriteAllBytes(pngPath, image);
                var fileData = new DataObject(); var imageFiles = new System.Collections.Specialized.StringCollection(); imageFiles.Add(pngPath); fileData.SetFileDropList(imageFiles);
                Clip fromFile = ClipboardService.ReadData(fileData);
                Check(fromFile.Kind == "image" && Clip.Decode(fromFile.ImageBytes).PixelWidth == 24, "single PNG file drop is captured as pixels, not a path");
                fileData.SetData("PNG", new MemoryStream(image));
                Check(ClipboardService.ReadData(fileData).Kind == "image", "PNG wins over single-file fallback");
                imageFiles.Add(testRoot); fileData.SetFileDropList(imageFiles);
                Check(ClipboardService.ReadData(fileData).Kind == "files", "mixed file selection preserves all files even with image fallback");
                Check(ClipboardService.ImageFile(new string[] { @"\\server\share\image.png" }) == null, "network path is not fetched for preview");
                string broken = Path.Combine(testRoot, "broken.png"); File.WriteAllText(broken, "not an image");
                Check(ClipboardService.ImageFile(new string[] { broken }) == null, "invalid image falls back safely");
                using (var db = new HistoryStore(Path.Combine(testRoot, "migration")))
                {
                    var legacy = new Clip { Kind = "files", Content = new System.Web.Script.Serialization.JavaScriptSerializer().Serialize(new string[] { pngPath }) };
                    db.Add(legacy); db.Pin(legacy.Id, true); string stamp = db.List()[0].Created;
                    db.UpgradeImageFiles(); var migrated = db.List()[0];
                    Check(db.Count == 1 && migrated.Kind == "image" && migrated.Id == legacy.Id && migrated.Pinned && migrated.Created == stamp, "image migration preserves identity, time and pin");
                    db.UpgradeImageFiles(); Check(db.Count == 1 && db.List()[0].Kind == "image", "migration is idempotent");
                }
                using (var db = new HistoryStore(Path.Combine(testRoot, "images")))
                {
                    var clip = new Clip { Kind = "image", Content = "24 × 16", ImageBytes = image, ThumbBytes = image }; db.Add(clip);
                    Check(db.GetImage(clip.Id).SequenceEqual(image) && db.List("", "image")[0].Thumbnail.PixelWidth == 24, "image blob and thumbnail round trip");
                }
                var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                Program.DataFolder = Path.Combine(testRoot, "listener");
                var original = Snapshot();
                try
                {
                    using (var controller = new Controller(true, true))
                    using (var db = new HistoryStore(Program.DataFolder))
                    {
                        Clipboard.SetDataObject(Fixture("COPY 自动测试 · 文字 😀\n第二行"), true); Pump();
                        Check(db.Count == 1 && db.List()[0].Content.Contains("第二行"), "native clipboard notification captures multiline Unicode");
                        Clipboard.SetDataObject(Fixture("COPY 自动测试 · 文字 😀\n第二行"), true); Pump();
                        Check(db.Count == 1, "listener deduplicates consecutive events");
                        var pic = Fixture("image fallback"); pic.SetImage(bitmap); pic.SetData("PNG", new MemoryStream(image));
                        Clipboard.SetDataObject(pic, true); Pump();
                        Check(db.Count == 2 && db.List()[0].Kind == "image" && Clip.Decode(db.GetImage(db.List()[0].Id)).PixelWidth == 24, "native image + PNG capture, image wins over fallback text");
                        var files = Fixture("file fallback"); var collection = new System.Collections.Specialized.StringCollection();
                        string sample = Path.Combine(testRoot, "示例文件.txt"); File.WriteAllText(sample, "fixture"); collection.Add(sample); collection.Add(testRoot); files.SetFileDropList(collection);
                        Clipboard.SetDataObject(files, true); Pump();
                        Check(db.Count == 3 && ClipboardService.FilePaths(db.List()[0]).Length == 2, "native multiple file + folder capture");
                        Clip fileClip = db.List()[0]; ClipboardService.Write(fileClip, null); Pump();
                        Check(db.Count == 3 && Clipboard.GetFileDropList().Count == 2, "replay file paths without history duplication");
                        Clip imageClip = db.List("", "image")[0]; ClipboardService.Write(imageClip, db.GetImage(imageClip.Id)); Pump();
                        Check(Clipboard.ContainsImage() && Clipboard.GetImage().PixelWidth == 24 && db.Count == 3, "replay image without history duplication");
                        Clip textClip = db.List("", "text")[0]; ClipboardService.Write(textClip, null); Pump();
                        Check(Clipboard.GetText() == textClip.Content && db.Count == 3, "replay text without history duplication");
                        var secret = Fixture("excluded fixture"); secret.SetData("ExcludeClipboardContentFromMonitorProcessing", new MemoryStream(new byte[] { 1 }));
                        Clipboard.SetDataObject(secret, true); Pump(); Check(db.Count == 3, "respects excluded clipboard format");
                        secret = Fixture("history disabled fixture"); secret.SetData("CanIncludeInClipboardHistory", new MemoryStream(new byte[4]));
                        Clipboard.SetDataObject(secret, true); Pump(); Check(db.Count == 3, "respects zero CanIncludeInClipboardHistory");
                        bool missing = false;
                        try { ClipboardService.Write(new Clip { Kind = "files", Content = "[\"Z:\\\\missing-copy-test-file\"]" }, null); }
                        catch (IOException) { missing = true; }
                        Check(missing, "missing original file yields clear failure");
                        for (int i = 0; i < 30; i++) db.Add(Text("滚动测试 " + i + "\n第二行\n第三行"));
                        Window panel = app.MainWindow;
                        panel.ShowActivated = false; panel.ShowInTaskbar = false; panel.Left = -10000; panel.Top = -10000; panel.Show();
                        var search = (TextBox)panel.FindName("SearchBox"); search.Text = "滚动测试"; Pump(); panel.UpdateLayout();
                        var list = (ListBox)panel.FindName("HistoryList");
                        var scroll = Controller.FindChild<ScrollViewer>(list);
                        Check(panel.Height == 340 && panel.MinHeight == 300, "compact window height is 340, minimum 300");
                        Check(scroll != null && scroll.ScrollableHeight > 42 && list.Items.Count == 30, "compact list exposes scrollable middle records");
                        var wheel = new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, -120) { RoutedEvent = UIElement.PreviewMouseWheelEvent };
                        list.RaiseEvent(wheel); Pump(); panel.UpdateLayout();
                        Check(wheel.Handled && scroll.VerticalOffset > 0 && scroll.VerticalOffset <= 43 && scroll.VerticalOffset < scroll.ScrollableHeight, "one wheel notch scrolls 42 pixels, never jumps to bottom");
                        list.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, -120) { RoutedEvent = UIElement.PreviewMouseWheelEvent }); Pump();
                        Check(scroll.VerticalOffset >= 83 && scroll.VerticalOffset <= 85, "second wheel notch reaches intermediate content");
                        panel.Hide();
                    }
                }
                finally
                {
                    var current = Clipboard.GetDataObject();
                    if (current != null && (current.GetDataPresent("CopyDesk.Test.Fixture") || current.GetDataPresent(ClipboardService.OwnFormat))) Clipboard.SetDataObject(original, true);
                    app.Shutdown();
                }
            }
            catch (Exception ex) { results.Add(ex.ToString()); code = 1; }
            results.Add("Assertions: " + assertions + "; result: " + (code == 0 ? "PASS" : "FAIL"));
            File.WriteAllLines(Path.Combine(Program.Root, "tests", "latest-results.txt"), results, Encoding.UTF8);
            return code;
        }
    }
}
