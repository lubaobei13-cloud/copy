using System;
using System.Collections.Specialized;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Web.Script.Serialization;

namespace CopyDesk
{
    internal static class Native
    {
        [DllImport("user32.dll", SetLastError = true)] internal static extern bool AddClipboardFormatListener(IntPtr hwnd);
        [DllImport("user32.dll")] internal static extern bool RemoveClipboardFormatListener(IntPtr hwnd);
        [DllImport("user32.dll", SetLastError = true)] internal static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint key);
        [DllImport("user32.dll")] internal static extern bool UnregisterHotKey(IntPtr hwnd, int id);
        [DllImport("user32.dll")] internal static extern uint GetClipboardSequenceNumber();
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern uint RegisterWindowMessage(string name);
        [DllImport("user32.dll")] internal static extern bool SendNotifyMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
    }

    internal static class ClipboardService
    {
        internal static readonly string OwnFormat = "CopyDesk.Internal." + System.Diagnostics.Process.GetCurrentProcess().Id;
        internal static string[] FilePaths(Clip clip) { return new JavaScriptSerializer().Deserialize<string[]>(clip.Content); }
        static byte[] Bytes(object data)
        {
            if (data is byte[]) return (byte[])data;
            var stream = data as MemoryStream;
            if (stream != null) return stream.ToArray();
            return null;
        }
        static bool Excluded(IDataObject data)
        {
            if (data.GetDataPresent("ExcludeClipboardContentFromMonitorProcessing", false)) return true;
            if (!data.GetDataPresent("CanIncludeInClipboardHistory", false)) return false;
            object flag = data.GetData("CanIncludeInClipboardHistory", false);
            if (flag is int) return (int)flag == 0;
            byte[] bytes = Bytes(flag);
            return bytes != null && bytes.Length >= 4 && BitConverter.ToUInt32(bytes, 0) == 0;
        }
        internal static Clip Read(bool ignoreOwn = true)
        {
            IDataObject data = Clipboard.GetDataObject();
            return ReadData(data, ignoreOwn);
        }
        internal static Clip ReadData(IDataObject data, bool ignoreOwn = true)
        {
            if (data == null || (ignoreOwn && data.GetDataPresent(OwnFormat, false)) || Excluded(data)) return null;
            string[] files = data.GetDataPresent(DataFormats.FileDrop) ? data.GetData(DataFormats.FileDrop) as string[] : null;
            // Multi-file selections must retain every file. A single image may also
            // expose FileDrop (WeChat); prefer its pixels over the temporary path.
            if (files != null && files.Length > 1) return FileClip(files);
            BitmapSource bitmap = null;
            if (data.GetDataPresent("PNG", false))
            {
                byte[] png = Bytes(data.GetData("PNG", false));
                if (png != null && png.Length > 20 * 1024 * 1024) throw new IOException("图片超过 20 MB，本次未保存。");
                if (png != null) { try { bitmap = Clip.Decode(png); } catch (NotSupportedException) { } catch (System.IO.FileFormatException) { } }
            }
            if (bitmap == null && data.GetDataPresent(DataFormats.Bitmap)) bitmap = data.GetData(DataFormats.Bitmap) as BitmapSource;
            if (bitmap == null && data.GetDataPresent(DataFormats.Bitmap))
            {
                // Some OLE sources expose System.Drawing.Bitmap rather than WPF pixels.
                var drawing = data.GetData(DataFormats.Bitmap) as System.Drawing.Bitmap;
                if (drawing != null) using (var ms = new MemoryStream()) { drawing.Save(ms, System.Drawing.Imaging.ImageFormat.Png); bitmap = Clip.Decode(ms.ToArray()); }
            }
            if (bitmap != null) return ImageClip(bitmap);
            if (files != null && files.Length > 0) return ImageFile(files) ?? FileClip(files);
            if (data.GetDataPresent(DataFormats.UnicodeText) || data.GetDataPresent(DataFormats.Text))
            {
                string text = data.GetData(DataFormats.UnicodeText) as string ?? data.GetData(DataFormats.Text) as string;
                if (String.IsNullOrEmpty(text)) return null;
                if (text.Length > 2 * 1024 * 1024) throw new IOException("文字超过 200 万字符，本次未保存。");
                return new Clip { Kind = "text", Content = text, Preview = text.Replace("\0", " ") };
            }
            return null;
        }
        static Clip FileClip(string[] files)
        {
            var preview = new StringBuilder();
            preview.Append(files.Length == 1 ? "" : files.Length + " 个文件 / 文件夹\n");
            for (int i = 0; i < Math.Min(files.Length, 3); i++) preview.AppendLine(files[i]);
            return new Clip { Kind = "files", Content = new JavaScriptSerializer().Serialize(files), Preview = preview.ToString().TrimEnd() };
        }
        internal static Clip ImageFile(string[] paths)
        {
            if (paths == null || paths.Length != 1) return null;
            try
            {
                string path = paths[0];
                // Never fetch URLs or network shares just to make a preview.
                if (!Path.IsPathRooted(path) || path.StartsWith(@"\\") || new DriveInfo(Path.GetPathRoot(path)).DriveType != DriveType.Fixed) return null;
                string ext = Path.GetExtension(path).ToLowerInvariant();
                if (ext != ".png" && ext != ".jpg" && ext != ".jpeg" && ext != ".bmp" && ext != ".webp" && ext != ".gif") return null;
                var info = new FileInfo(path);
                if (!info.Exists || info.Length > 20 * 1024 * 1024) return null;
                return ImageClip(Clip.Decode(File.ReadAllBytes(path)));
            }
            catch (IOException) { }
            catch (NotSupportedException) { }
            catch (UnauthorizedAccessException) { }
            catch (ArgumentException) { }
            return null;
        }
        internal static Clip ImageClip(BitmapSource bitmap)
        {
            if ((long)bitmap.PixelWidth * bitmap.PixelHeight > 40000000) throw new IOException("图片超过 4000 万像素，本次未保存。");
            var bytes = Encode(bitmap);
            if (bytes.Length > 20 * 1024 * 1024) throw new IOException("图片超过 20 MB，本次未保存。");
            double ratio = Math.Min(1, 280.0 / Math.Max(bitmap.PixelWidth, bitmap.PixelHeight));
            BitmapSource thumb = ratio < 1 ? new TransformedBitmap(bitmap, new ScaleTransform(ratio, ratio)) : bitmap;
            return new Clip { Kind = "image", Content = "图片 " + bitmap.PixelWidth + " × " + bitmap.PixelHeight,
                Preview = bitmap.PixelWidth + " × " + bitmap.PixelHeight + " 像素", ImageBytes = bytes, ThumbBytes = Encode(thumb) };
        }
        internal static byte[] Encode(BitmapSource bitmap)
        {
            using (var ms = new MemoryStream())
            {
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); encoder.Save(ms); return ms.ToArray();
            }
        }
        internal static void Write(Clip clip, byte[] image)
        {
            var data = new DataObject();
            data.SetData(OwnFormat, new MemoryStream(new byte[] { 1 }));
            if (clip.Kind == "image")
            {
                if (image == null) throw new IOException("图片数据不存在。");
                data.SetImage(Clip.Decode(image)); data.SetData("PNG", new MemoryStream(image));
            }
            else if (clip.Kind == "files")
            {
                string[] paths = FilePaths(clip);
                foreach (string path in paths)
                    if (!File.Exists(path) && !Directory.Exists(path)) throw new IOException("原文件已移动或删除，请展开记录查看路径。");
                var files = new StringCollection(); files.AddRange(paths); data.SetFileDropList(files);
                // Explicit COPY effect: restoring history must never repeat a previous cut operation.
                data.SetData("Preferred DropEffect", new MemoryStream(BitConverter.GetBytes(1)));
            }
            else data.SetText(clip.Content, TextDataFormat.UnicodeText);
            Clipboard.SetDataObject(data, true);
        }
    }
}
