using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Media.Imaging;

namespace CopyDesk
{
    public sealed class Clip
    {
        public long Id { get; set; }
        public string Kind { get; set; }
        public string Content { get; set; }
        public string Preview { get; set; }
        public string Hash { get; set; }
        public string Created { get; set; }
        public bool Pinned { get; set; }
        public long Size { get; set; }
        public byte[] ImageBytes { get; set; }
        public byte[] ThumbBytes { get; set; }
        public string Label { get { return Kind == "image" ? "图片" : Kind == "files" ? "文件" : "文字"; } }
        public string PinLabel { get { return Pinned ? "取消置顶" : "置顶"; } }
        public Visibility ImageVisibility { get { return Kind == "image" ? Visibility.Visible : Visibility.Collapsed; } }
        public Visibility PinVisibility { get { return Pinned ? Visibility.Visible : Visibility.Collapsed; } }
        public string TimeLabel
        {
            get
            {
                DateTime value;
                if (!DateTime.TryParse(Created, null, DateTimeStyles.RoundtripKind, out value)) return "";
                value = value.ToLocalTime();
                return value.Date == DateTime.Today ? "今天 " + value.ToString("HH:mm") : value.ToString("MM-dd HH:mm");
            }
        }
        public BitmapSource Thumbnail
        {
            get
            {
                if (ThumbBytes == null || ThumbBytes.Length == 0) return null;
                try { return Decode(ThumbBytes); } catch { return null; }
            }
        }
        public static BitmapSource Decode(byte[] bytes)
        {
            using (var ms = new MemoryStream(bytes))
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.StreamSource = ms; bitmap.EndInit(); bitmap.Freeze(); return bitmap;
            }
        }
        public void Prepare()
        {
            Content = Content ?? "";
            Preview = Preview ?? Content;
            if (Preview.Length > 240) Preview = Preview.Substring(0, 240) + "…";
            Created = DateTime.UtcNow.ToString("o");
            byte[] text = Encoding.UTF8.GetBytes(Kind + "\0" + Content);
            using (var sha = SHA256.Create())
            using (var ms = new MemoryStream())
            {
                ms.Write(text, 0, text.Length);
                if (ImageBytes != null) ms.Write(ImageBytes, 0, ImageBytes.Length);
                Hash = Convert.ToBase64String(sha.ComputeHash(ms.ToArray()));
            }
            Size = text.Length + (ImageBytes == null ? 0 : ImageBytes.Length) + (ThumbBytes == null ? 0 : ThumbBytes.Length);
        }
    }

    // Uses the SQLite library shipped with Windows 10/11; no downloaded runtime.
    internal static class Sqlite
    {
        const string Dll = "winsqlite3.dll";
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)] internal static extern int sqlite3_open16(string path, out IntPtr db);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern int sqlite3_close(IntPtr db);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern int sqlite3_busy_timeout(IntPtr db, int ms);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr sqlite3_errmsg16(IntPtr db);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)] internal static extern int sqlite3_prepare16_v2(IntPtr db, string sql, int length, out IntPtr statement, IntPtr tail);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern int sqlite3_finalize(IntPtr statement);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern int sqlite3_step(IntPtr statement);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)] internal static extern int sqlite3_bind_text16(IntPtr statement, int index, string value, int bytes, IntPtr destructor);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern int sqlite3_bind_blob(IntPtr statement, int index, byte[] value, int bytes, IntPtr destructor);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern int sqlite3_bind_int64(IntPtr statement, int index, long value);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern int sqlite3_bind_null(IntPtr statement, int index);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern long sqlite3_column_int64(IntPtr statement, int column);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr sqlite3_column_text16(IntPtr statement, int column);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern int sqlite3_column_bytes16(IntPtr statement, int column);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr sqlite3_column_blob(IntPtr statement, int column);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern int sqlite3_column_bytes(IntPtr statement, int column);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern long sqlite3_last_insert_rowid(IntPtr db);
    }

    internal sealed class Statement : IDisposable
    {
        private IntPtr statement;
        private readonly IntPtr db;
        public Statement(IntPtr database, string sql, params object[] values)
        {
            db = database;
            Check(Sqlite.sqlite3_prepare16_v2(db, sql, -1, out statement, IntPtr.Zero));
            try
            {
                for (int i = 0; i < values.Length; i++)
                {
                    object value = values[i]; int index = i + 1;
                    if (value == null) Check(Sqlite.sqlite3_bind_null(statement, index));
                    else if (value is byte[]) { byte[] bytes = (byte[])value; Check(Sqlite.sqlite3_bind_blob(statement, index, bytes, bytes.Length, new IntPtr(-1))); }
                    else if (value is string) { string s = (string)value; Check(Sqlite.sqlite3_bind_text16(statement, index, s, s.Length * 2, new IntPtr(-1))); }
                    else Check(Sqlite.sqlite3_bind_int64(statement, index, Convert.ToInt64(value)));
                }
            }
            catch { Dispose(); throw; }
        }
        private void Check(int result) { if (result != 0) throw new IOException("本地数据库操作失败：" + Marshal.PtrToStringUni(Sqlite.sqlite3_errmsg16(db))); }
        public bool Read() { int rc = Sqlite.sqlite3_step(statement); if (rc == 100) return true; if (rc != 101) Check(rc); return false; }
        public long Number(int column) { return Sqlite.sqlite3_column_int64(statement, column); }
        public string Text(int column) { IntPtr p = Sqlite.sqlite3_column_text16(statement, column); return p == IntPtr.Zero ? "" : Marshal.PtrToStringUni(p, Sqlite.sqlite3_column_bytes16(statement, column) / 2); }
        public byte[] Blob(int column)
        {
            int length = Sqlite.sqlite3_column_bytes(statement, column); if (length == 0) return null;
            byte[] result = new byte[length]; Marshal.Copy(Sqlite.sqlite3_column_blob(statement, column), result, 0, length); return result;
        }
        public void Dispose() { if (statement != IntPtr.Zero) { Sqlite.sqlite3_finalize(statement); statement = IntPtr.Zero; } }
    }

    public sealed class HistoryStore : IDisposable
    {
        private IntPtr db;
        readonly int recordLimit;
        readonly long byteLimit;
        public HistoryStore(string folder, int countLimit = 1000, long capacity = 200L * 1024 * 1024)
        {
            recordLimit = countLimit; byteLimit = capacity;
            Directory.CreateDirectory(folder);
            int rc = Sqlite.sqlite3_open16(Path.Combine(folder, "history.db"), out db);
            if (rc != 0) { Dispose(); throw new IOException("无法打开历史数据库，请检查文件夹权限和磁盘空间。"); }
            Sqlite.sqlite3_busy_timeout(db, 2000);
            Exec("PRAGMA auto_vacuum=INCREMENTAL");
            Exec("PRAGMA secure_delete=ON");
            Exec("CREATE TABLE IF NOT EXISTS clips (id INTEGER PRIMARY KEY AUTOINCREMENT, kind TEXT NOT NULL, content TEXT NOT NULL, preview TEXT NOT NULL, created TEXT NOT NULL, pinned INTEGER NOT NULL DEFAULT 0, hash TEXT NOT NULL, size INTEGER NOT NULL, image BLOB, thumb BLOB)");
            Exec("CREATE INDEX IF NOT EXISTS clips_order ON clips(pinned DESC,id DESC)");
        }
        void Exec(string sql, params object[] values) { using (var s = new Statement(db, sql, values)) { while (s.Read()) { } } }
        long Scalar(string sql, params object[] values) { using (var s = new Statement(db, sql, values)) return s.Read() ? s.Number(0) : 0; }
        public int Count { get { return (int)Scalar("SELECT COUNT(*) FROM clips"); } }
        public long Bytes { get { return Scalar("SELECT COALESCE(SUM(size),0) FROM clips"); } }
        public bool Add(Clip clip)
        {
            clip.Prepare();
            using (var last = new Statement(db, "SELECT hash FROM clips ORDER BY id DESC LIMIT 1"))
                if (last.Read() && last.Text(0) == clip.Hash) return false;
            if (clip.Size > byteLimit || Scalar("SELECT COALESCE(SUM(size),0) FROM clips WHERE pinned=1") + clip.Size > byteLimit)
                throw new IOException("历史容量已满，请删除部分置顶记录后再复制。");
            Exec("BEGIN IMMEDIATE");
            try
            {
                Exec("INSERT INTO clips(kind,content,preview,created,pinned,hash,size,image,thumb) VALUES(?,?,?,?,0,?,?,?,?)", clip.Kind, clip.Content, clip.Preview, clip.Created, clip.Hash, clip.Size, clip.ImageBytes, clip.ThumbBytes);
                clip.Id = Sqlite.sqlite3_last_insert_rowid(db);
                Exec("DELETE FROM clips WHERE pinned=0 AND id NOT IN (SELECT id FROM clips WHERE pinned=0 ORDER BY id DESC LIMIT ?)", recordLimit);
                long total = Bytes;
                if (total > byteLimit)
                {
                    var old = new List<KeyValuePair<long, long>>();
                    using (var s = new Statement(db, "SELECT id,size FROM clips WHERE pinned=0 ORDER BY id"))
                        while (s.Read()) old.Add(new KeyValuePair<long, long>(s.Number(0), s.Number(1)));
                    foreach (var item in old) { if (total <= byteLimit) break; Exec("DELETE FROM clips WHERE id=?", item.Key); total -= item.Value; }
                }
                Exec("COMMIT");
            }
            catch { Exec("ROLLBACK"); throw; }
            Exec("PRAGMA incremental_vacuum(128)");
            return true;
        }
        public List<Clip> List(string search = "", string kind = "all")
        {
            // Literal search: %, _, quotes and embedded NULs are data, never SQL.
            string sql = "SELECT id,kind,content,preview,created,pinned,hash,size,thumb FROM clips WHERE (?='' OR instr(lower(content),lower(?))>0 OR instr(lower(preview),lower(?))>0) AND (?='all' OR kind=? OR (?='pinned' AND pinned=1)) ORDER BY pinned DESC,id DESC";
            var items = new List<Clip>();
            using (var s = new Statement(db, sql, search, search, search, kind, kind, kind))
                while (s.Read()) items.Add(new Clip { Id = s.Number(0), Kind = s.Text(1), Content = s.Text(2), Preview = s.Text(3), Created = s.Text(4), Pinned = s.Number(5) != 0, Hash = s.Text(6), Size = s.Number(7), ThumbBytes = s.Blob(8) });
            return items;
        }
        public byte[] GetImage(long id) { using (var s = new Statement(db, "SELECT image FROM clips WHERE id=?", id)) return s.Read() ? s.Blob(0) : null; }
        public void UpgradeImageFiles()
        {
            foreach (Clip old in List("", "files"))
            {
                Clip image;
                try { image = ClipboardService.ImageFile(ClipboardService.FilePaths(old)); }
                catch (ArgumentException) { continue; }
                if (image == null) continue;
                image.Prepare();
                if (Bytes - old.Size + image.Size > byteLimit) continue;
                // Update in place: original order, timestamp and pin state survive.
                Exec("UPDATE clips SET kind=?,content=?,preview=?,hash=?,size=?,image=?,thumb=? WHERE id=?", image.Kind, image.Content, image.Preview, image.Hash, image.Size, image.ImageBytes, image.ThumbBytes, old.Id);
            }
        }
        public void Pin(long id, bool value) { Exec("UPDATE clips SET pinned=? WHERE id=?", value ? 1 : 0, id); }
        public void Delete(long id) { Exec("DELETE FROM clips WHERE id=?", id); Exec("PRAGMA incremental_vacuum"); }
        public void Clear(bool includePinned) { Exec(includePinned ? "DELETE FROM clips" : "DELETE FROM clips WHERE pinned=0"); Exec("VACUUM"); }
        public void Dispose() { if (db != IntPtr.Zero) { Sqlite.sqlite3_close(db); db = IntPtr.Zero; } }
    }
}
