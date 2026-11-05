// Launcher for the Steel Ocean server at Linsoft. It finds the Steam installation, applies the signed patch, updates itself, shows the patch versions and starts the game.
// The whole window is painted by hand, and the artwork is drawn in code, so it is a single file with no dependencies.
// It builds with the csc.exe included in Windows (build.py), which only supports C# 5.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: AssemblyTitle("Steel Ocean - Linsoft")]
[assembly: AssemblyProduct("Steel Ocean - Linsoft")]
[assembly: AssemblyVersion("1.1.2.0")]

// The interface follows the Windows language: Spanish on a Spanish system, English on any other.
static class Tr
{
    public static bool Spanish = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "es";
    public static string S(string english, string spanish) { return Spanish ? spanish : english; }
}

class PatchFile { public string Path, Sha; public long Size, GzSize; }
class HistoryEntry { public string Version, Date; public List<string> Notes = new List<string>(); }
class LauncherInfo { public string Version, Sha; public long Size; }

class Manifest
{
    public string Version, Date, Exe, Arguments;
    public List<PatchFile> Files = new List<PatchFile>();
    public List<string> Delete = new List<string>(), Folders = new List<string>();
    public List<HistoryEntry> History = new List<HistoryEntry>();
    public LauncherInfo Launcher;
}

static class Patch
{
    public const string LauncherVersion = "1.1.2";  // Raise it whenever this file changes: build.py requires it, and it is what makes players update.
    // build.py replaces this with keys/patch_public.xml.
    const string PublicKey = "__PUBLIC_KEY__";
    public const string GameFolder = "Steel Ocean Wolves of Deep Sea";
    public const string GameExe = @"Binaries\win32\SeaGame.exe";
    public const string Website = "https://steelocean.linsoft.pe/";
    public static string BaseUrl = Website + "downloads/";
    const int Attempts = 3;

    static HttpWebRequest Request(string url)
    {
        var q = (HttpWebRequest)WebRequest.Create(url);
        q.UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) SteelOceanLinsoft/" + LauncherVersion;
        q.Timeout = 30000;
        q.ReadWriteTimeout = 30000;
        return q;
    }

    static byte[] Fetch(string url)
    {
        using (var r = Request(url).GetResponse())
        using (var s = r.GetResponseStream())
        using (var m = new MemoryStream())
        {
            s.CopyTo(m);
            return m.ToArray();
        }
    }

    // To disk and with retries: the large files are tens of MB.
    static void Download(string url, string target, Action<long> progress)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                using (var r = Request(url).GetResponse())
                using (var s = r.GetResponseStream())
                using (var f = File.Create(target))
                {
                    var chunk = new byte[65536];
                    int n;
                    long done = 0;
                    while ((n = s.Read(chunk, 0, chunk.Length)) > 0)
                    {
                        f.Write(chunk, 0, n);
                        done += n;
                        progress(done);
                    }
                }
                return;
            }
            catch (WebException)
            {
                if (attempt >= Attempts) throw;
            }
            catch (IOException)
            {
                if (attempt >= Attempts) throw;
            }
            progress(0);
            Thread.Sleep(1000 * attempt);
        }
    }

    static string NoCache(string name) { return BaseUrl + name + "?t=" + DateTime.UtcNow.Ticks; }

    public static Manifest ReadManifest()
    {
        byte[] text = Fetch(NoCache("patch.json")), signature = Fetch(NoCache("patch.sig"));
        using (var rsa = new RSACryptoServiceProvider())
        {
            rsa.FromXmlString(PublicKey);
            if (!rsa.VerifyData(text, CryptoConfig.MapNameToOID("SHA256"), signature))
                throw new InvalidDataException(Tr.S("The patch signature is not valid: nothing was applied.", "La firma del parche no es válida: no se aplica nada."));
        }
        return Parse(Encoding.UTF8.GetString(text));
    }

    public static Manifest Parse(string text)
    {
        var d = (Dictionary<string, object>)new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.DeserializeObject(text);
        var m = new Manifest { Version = (string)d["version"], Exe = (string)d["exe"], Arguments = (string)d["arguments"] };
        if (d.ContainsKey("date")) m.Date = (string)d["date"];
        foreach (Dictionary<string, object> f in (object[])d["files"])
        {
            string path = (string)f["path"];
            if (!IsSafePath(path)) throw new InvalidDataException(path);
            m.Files.Add(new PatchFile { Path = path, Sha = (string)f["sha256"], Size = Convert.ToInt64(f["size"]), GzSize = Convert.ToInt64(f["gz_size"]) });
        }
        foreach (string r in (object[])d["delete"]) { if (!IsSafePath(r)) throw new InvalidDataException(r); m.Delete.Add(r); }
        foreach (string r in (object[])d["folders"]) { if (!IsSafePath(r)) throw new InvalidDataException(r); m.Folders.Add(r); }
        if (d.ContainsKey("history"))
            foreach (Dictionary<string, object> h in (object[])d["history"])
            {
                var e = new HistoryEntry { Version = (string)h["version"], Date = h.ContainsKey("date") ? (string)h["date"] : "" };
                string notes = Tr.Spanish && h.ContainsKey("notes_es") ? "notes_es" : "notes";
                if (h.ContainsKey(notes)) foreach (string n in (object[])h[notes]) e.Notes.Add(n);
                m.History.Add(e);
            }
        if (d.ContainsKey("launcher"))
        {
            var l = (Dictionary<string, object>)d["launcher"];
            m.Launcher = new LauncherInfo { Version = (string)l["version"], Sha = (string)l["sha256"], Size = Convert.ToInt64(l["size"]) };
        }
        return m;
    }

    static bool IsSafePath(string r)
    {
        return !string.IsNullOrEmpty(r) && !r.Contains("..") && !r.Contains(":") && !r.StartsWith("/") && !r.StartsWith("\\");
    }

    public static string Sha(string path)
    {
        using (var s = File.OpenRead(path))
        using (var h = SHA256.Create())
            return BitConverter.ToString(h.ComputeHash(s)).Replace("-", "").ToLowerInvariant();
    }

    public static List<PatchFile> Pending(string game, Manifest m, Action<int, int> progress)
    {
        var missing = new List<PatchFile>();
        for (int i = 0; i < m.Files.Count; i++)
        {
            progress(i, m.Files.Count);
            string path = Path.Combine(game, m.Files[i].Path.Replace('/', '\\'));
            if (!File.Exists(path) || new FileInfo(path).Length != m.Files[i].Size || Sha(path) != m.Files[i].Sha) missing.Add(m.Files[i]);
        }
        return missing;
    }

    public static bool HasFilesToDelete(string game, Manifest m)
    {
        foreach (string r in m.Delete) if (File.Exists(Path.Combine(game, r.Replace('/', '\\')))) return true;
        return false;
    }

    static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    public static void Apply(string game, Manifest m, List<PatchFile> missing, Action<long, long, string> progress)
    {
        long total = 0, done = 0;
        foreach (var f in missing) total += f.GzSize;
        foreach (var f in missing)
        {
            string target = Path.Combine(game, f.Path.Replace('/', '\\'));
            string compressed = target + ".gz.new", temporary = target + ".new";
            Directory.CreateDirectory(Path.GetDirectoryName(target));
            long before = done;
            string shown = f.Path;
            progress(done, total, shown);
            try
            {
                Download(BaseUrl + f.Sha + ".gz", compressed, delegate(long v) { progress(before + v, total, shown); });
                using (var input = new GZipStream(File.OpenRead(compressed), CompressionMode.Decompress))
                using (var output = File.Create(temporary))
                    input.CopyTo(output);
                if (Sha(temporary) != f.Sha) throw new InvalidDataException(Tr.S("Corrupted download: ", "Descarga dañada: ") + f.Path);
                if (File.Exists(target)) { File.SetAttributes(target, FileAttributes.Normal); File.Delete(target); }
                File.Move(temporary, target);
            }
            finally
            {
                TryDelete(compressed);
                TryDelete(temporary);
            }
            done += f.GzSize;
        }
        foreach (string r in m.Delete)
        {
            string path = Path.Combine(game, r.Replace('/', '\\'));
            if (File.Exists(path)) { File.SetAttributes(path, FileAttributes.Normal); File.Delete(path); }
        }
        foreach (string r in m.Folders) Directory.CreateDirectory(Path.Combine(game, r.Replace('/', '\\')));
    }

    // --- The launcher itself ---

    public static int CompareVersions(string a, string b)
    {
        string[] x = a.Split('.'), y = b.Split('.');
        for (int i = 0; i < Math.Max(x.Length, y.Length); i++)
        {
            int p = 0, q = 0;
            if (i < x.Length) int.TryParse(x[i], out p);
            if (i < y.Length) int.TryParse(y[i], out q);
            if (p != q) return p < q ? -1 : 1;
        }
        return 0;
    }

    public static bool HasNewerLauncher(Manifest m) { return m.Launcher != null && CompareVersions(m.Launcher.Version, LauncherVersion) > 0; }

    // Windows lets you rename a running .exe but not overwrite it: move it aside, put the new one in place, and the next start deletes the old one.
    public static void ReplaceLauncher(Manifest m, Action<long> progress)
    {
        string self = Application.ExecutablePath, updated = self + ".new", old = self + ".old";
        try
        {
            Download(NoCache("SteelOceanLinsoft.exe"), updated, progress);
            if (new FileInfo(updated).Length != m.Launcher.Size || Sha(updated) != m.Launcher.Sha)
                throw new InvalidDataException(Tr.S("The downloaded launcher does not match the signed patch.", "El lanzador descargado no coincide con el parche firmado."));
            if (File.Exists(old)) File.Delete(old);
            File.Move(self, old);
            try
            {
                File.Move(updated, self);
            }
            catch
            {
                File.Move(old, self);
                throw;
            }
        }
        finally
        {
            TryDelete(updated);
        }
    }

    public static void CleanUpOldLauncher()
    {
        string old = Application.ExecutablePath + ".old";
        for (int i = 0; i < 25 && File.Exists(old); i++)
        {
            TryDelete(old);
            if (File.Exists(old)) Thread.Sleep(200);
        }
    }

    public static int PlayersOnline()
    {
        var d = (Dictionary<string, object>)new JavaScriptSerializer().DeserializeObject(Encoding.UTF8.GetString(Fetch(Website + "api/status")));
        return Convert.ToInt32(d["players"]);
    }

    public static string FindGame()
    {
        var libraries = new List<string>();
        foreach (var key in new[] { @"HKEY_CURRENT_USER\Software\Valve\Steam", @"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Valve\Steam", @"HKEY_LOCAL_MACHINE\SOFTWARE\Valve\Steam" })
        {
            foreach (var value in new[] { "SteamPath", "InstallPath" })
            {
                string steam = Registry.GetValue(key, value, null) as string;
                if (string.IsNullOrEmpty(steam)) continue;
                steam = steam.Replace('/', '\\');
                libraries.Add(steam);
                string vdf = Path.Combine(steam, @"steamapps\libraryfolders.vdf");
                if (File.Exists(vdf))
                    foreach (Match x in Regex.Matches(File.ReadAllText(vdf), "\"path\"\\s+\"([^\"]+)\"|\"\\d+\"\\s+\"([A-Za-z]:[^\"]+)\""))
                        libraries.Add((x.Groups[1].Success ? x.Groups[1].Value : x.Groups[2].Value).Replace(@"\\", @"\"));
            }
        }
        foreach (var drive in DriveInfo.GetDrives())
            if (drive.DriveType == DriveType.Fixed) libraries.Add(drive.Name + "SteamLibrary");
        foreach (var b in libraries)
        {
            string path = Path.Combine(b, @"steamapps\common", GameFolder);
            if (File.Exists(Path.Combine(path, GameExe))) return path;
        }
        return null;
    }
}

// Night at sea, with signal yellow for whatever can be clicked.
static class Theme
{
    public static readonly Color Text = Color.FromArgb(236, 243, 248), Muted = Color.FromArgb(146, 166, 182), Dim = Color.FromArgb(96, 116, 132);
    public static readonly Color Signal = Color.FromArgb(240, 176, 78), SignalLight = Color.FromArgb(250, 198, 110), SignalDark = Color.FromArgb(214, 142, 52);
    public static readonly Color Sea = Color.FromArgb(88, 186, 208), Good = Color.FromArgb(94, 214, 150), Bad = Color.FromArgb(238, 106, 96);
    public static readonly Color Panel = Color.FromArgb(214, 9, 20, 31), Bar = Color.FromArgb(250, 8, 18, 28), Line = Color.FromArgb(38, 255, 255, 255), Track = Color.FromArgb(30, 49, 66);
    public const string Font = "Segoe UI", BoldFont = "Segoe UI Semibold";
}

// For automated tests only: the real window, with its threads and downloads, but off-screen, without focus, without touching the desktop, and with the game in a temporary folder.
static class TestMode
{
    public static string DataFolder, Game, FinalCapture, Trace;
    public static bool Active, Accept, Decline;
    public static double QuitAfter = 60;
}

class HotZone { public RectangleF Rect; public Action Click; public string Name; }

class Dialog
{
    public string Title, Body, Yes, No;
    public Action<bool> Answer;
}

class NoteLine { public string Text, Tag; public Font Font; public Color Color; public float Y, Height, X; public bool Bullet; }

class MainWindow : Form
{
    const int LogicalWidth = 960, LogicalHeight = 600;
    enum Phase { Checking, Failed, NoGame, NeedsInstall, Installing, Ready }

    readonly string dataFolder = TestMode.DataFolder ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SteelOceanLinsoft");
    readonly float scale;
    readonly bool justUpdated;
    readonly Dictionary<string, Font> fonts = new Dictionary<string, Font>();
    readonly List<HotZone> zones = new List<HotZone>();
    readonly StringFormat typographic = new StringFormat(StringFormat.GenericTypographic) { FormatFlags = StringFormatFlags.MeasureTrailingSpaces };
    readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer { Interval = 50 };
    readonly Stopwatch clock = Stopwatch.StartNew();
    Bitmap background;

    Phase phase = Phase.Checking;
    string title = "", detail = "", figures = "";
    double progress = -2;  // 0 to 1; -1 no bar; -2 unknown
    Manifest manifest;
    string game, installed;
    List<PatchFile> pendingFiles;  // what "Not now" left without installing
    long pendingMb;
    int? players;
    bool serverKnown, busy;
    Dialog dialog;
    PointF mouse = new PointF(-1, -1);
    bool pressed;
    int focused = -1;
    float scroll, notesHeight;
    List<NoteLine> noteLines;
    double lastCheck;

    public MainWindow(float givenScale, bool updated)
    {
        scale = givenScale;
        justUpdated = updated;
        Text = "Steel Ocean - Linsoft";
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size((int)(LogicalWidth * scale), (int)(LogicalHeight * scale));
        DoubleBuffered = true;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        BackColor = Color.FromArgb(8, 18, 28);
        KeyPreview = true;
        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch (ArgumentException) { }
        if (TestMode.Active)
        {
            StartPosition = FormStartPosition.Manual;
            Location = new Point(-5000, -5000);
            ShowInTaskbar = false;
        }
        installed = ReadData("version.txt");
        timer.Tick += delegate { OnTick(); };
        Shown += delegate
        {
            timer.Start();
            new Thread(Check) { IsBackground = true }.Start();
            new Thread(CheckServer) { IsBackground = true }.Start();
            if (justUpdated) new Thread(Patch.CleanUpOldLauncher) { IsBackground = true }.Start();
        };
    }

    // --- A window without a frame: it still moves, minimizes and has a shadow ---

    [DllImport("user32.dll")] static extern bool ReleaseCapture();
    [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr h, int m, IntPtr w, IntPtr l);
    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr h, int a, ref int v, int t);

    protected override bool ShowWithoutActivation { get { return TestMode.Active; } }

    protected override CreateParams CreateParams
    {
        get
        {
            var p = base.CreateParams;
            p.Style |= 0x20000;  // WS_MINIMIZEBOX: the taskbar can minimize it
            p.ClassStyle |= 0x20000;  // CS_DROPSHADOW
            return p;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        try { int rounded = 2; DwmSetWindowAttribute(Handle, 33, ref rounded, 4); }  // rounded corners, on Windows 11
        catch (DllNotFoundException) { }
        catch (EntryPointNotFoundException) { }
    }

    protected override bool ProcessCmdKey(ref Message m, Keys key)
    {
        if (key == Keys.Tab || key == (Keys.Tab | Keys.Shift))
        {
            if (zones.Count > 0) focused = (focused + (key == Keys.Tab ? 1 : zones.Count - 1)) % zones.Count;
            Invalidate();
            return true;
        }
        if (key == Keys.Return || key == Keys.Space)
        {
            if (focused >= 0 && focused < zones.Count) zones[focused].Click();
            else if (dialog != null) Answer(true);
            else if (CanPressMain()) PressMain();
            return true;
        }
        if (key == Keys.Escape && dialog != null) { Answer(false); return true; }
        return base.ProcessCmdKey(ref m, key);
    }

    PointF Logical(Point p) { return new PointF(p.X / scale, p.Y / scale); }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        mouse = Logical(e.Location);
        bool hand = false;
        foreach (var z in zones) if (z.Rect.Contains(mouse)) hand = true;
        Cursor = hand ? Cursors.Hand : Cursors.Default;
    }

    protected override void OnMouseLeave(EventArgs e) { mouse = new PointF(-1, -1); }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        var p = Logical(e.Location);
        foreach (var z in zones) if (z.Rect.Contains(p)) { pressed = true; return; }
        if (dialog == null && p.Y < 44)
        {
            ReleaseCapture();
            SendMessage(Handle, 0xA1, (IntPtr)2, IntPtr.Zero);  // WM_NCLBUTTONDOWN, HTCAPTION
        }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        if (!pressed) return;
        pressed = false;
        var p = Logical(e.Location);
        foreach (var z in zones.ToArray()) if (z.Rect.Contains(p)) { z.Click(); return; }
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        scroll -= e.Delta / 120f * 48;
        Invalidate();
    }

    // --- Painting ---

    Font GetFont(string name, float px)
    {
        string key = name + px;
        Font f;
        if (!fonts.TryGetValue(key, out f))
        {
            f = new Font(name, px, FontStyle.Regular, GraphicsUnit.Pixel);
            fonts[key] = f;
        }
        return f;
    }

    static GraphicsPath Rounded(RectangleF r, float radius)
    {
        var p = new GraphicsPath();
        float d = radius * 2;
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    void Fill(Graphics g, RectangleF r, float radius, Color c, Color? border = null)
    {
        using (var p = Rounded(r, radius))
        {
            using (var b = new SolidBrush(c)) g.FillPath(b, p);
            if (border.HasValue) using (var l = new Pen(border.Value)) g.DrawPath(l, p);
        }
    }

    void Write(Graphics g, string text, Font f, Color c, float x, float y)
    {
        using (var b = new SolidBrush(c)) g.DrawString(text, f, b, x, y, typographic);
    }

    float WriteSpaced(Graphics g, string text, Font f, Color c, float x, float y, float gap)
    {
        using (var b = new SolidBrush(c))
            foreach (char ch in text)
            {
                string s = ch.ToString();
                g.DrawString(s, f, b, x, y, typographic);
                x += g.MeasureString(s, f, PointF.Empty, typographic).Width + gap;
            }
        return x;
    }

    float SpacedWidth(Graphics g, string text, Font f, float gap)
    {
        float x = 0;
        foreach (char ch in text) x += g.MeasureString(ch.ToString(), f, PointF.Empty, typographic).Width + gap;
        return x;
    }

    void Paragraph(Graphics g, string text, Font f, Color c, RectangleF r, StringTrimming trimming = StringTrimming.Word)
    {
        using (var b = new SolidBrush(c))
        using (var s = new StringFormat(StringFormat.GenericTypographic) { Trimming = trimming })
            g.DrawString(text, f, b, r, s);
    }

    bool IsOver(RectangleF r, bool inDialog) { return (dialog == null || inDialog) && r.Contains(mouse); }

    void AddZone(RectangleF r, Action click, string name, Graphics g)
    {
        zones.Add(new HotZone { Rect = r, Click = click, Name = name });
        if (focused == zones.Count - 1)
            using (var l = new Pen(Color.FromArgb(200, Theme.Sea), 1.5f) { DashStyle = DashStyle.Dot })
            using (var p = Rounded(RectangleF.Inflate(r, 3, 3), 8))
                g.DrawPath(l, p);
    }

    public void PaintAll(Graphics g)
    {
        if (background == null) background = DrawScene();
        g.DrawImageUnscaled(background, 0, 0);
        g.ScaleTransform(scale, scale);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.AntiAlias;
        zones.Clear();
        DrawWaves(g);
        DrawTitle(g);
        DrawStats(g);
        DrawNotes(g);
        DrawBottomBar(g);
        DrawWindowButtons(g);
        using (var l = new Pen(Color.FromArgb(46, 255, 255, 255))) g.DrawRectangle(l, 0, 0, LogicalWidth - 1, LogicalHeight - 1);
        if (dialog != null) { zones.Clear(); DrawDialog(g); }
    }

    protected override void OnPaint(PaintEventArgs e) { PaintAll(e.Graphics); }

    // The sky, the ship and the still sea are drawn once.
    Bitmap DrawScene()
    {
        var b = new Bitmap((int)(LogicalWidth * scale), (int)(LogicalHeight * scale), PixelFormat.Format32bppPArgb);
        using (var g = Graphics.FromImage(b))
        {
            g.ScaleTransform(scale, scale);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            const float horizon = 344;
            using (var sky = new LinearGradientBrush(new RectangleF(0, 0, LogicalWidth, horizon + 1), Color.Black, Color.Black, 90f))
            {
                sky.InterpolationColors = new ColorBlend
                {
                    Colors = new[] { Color.FromArgb(6, 13, 23), Color.FromArgb(11, 28, 44), Color.FromArgb(24, 62, 84), Color.FromArgb(92, 104, 104), Color.FromArgb(226, 158, 88) },
                    Positions = new[] { 0f, 0.42f, 0.72f, 0.92f, 1f }
                };
                g.FillRectangle(sky, 0, 0, LogicalWidth, horizon + 1);
            }
            var random = new Random(1943);  // always the same stars
            for (int i = 0; i < 130; i++)
            {
                float x = (float)random.NextDouble() * LogicalWidth, y = (float)random.NextDouble() * 190, size = 0.6f + (float)random.NextDouble() * 1.1f;
                using (var e = new SolidBrush(Color.FromArgb(40 + random.Next(130), 214, 228, 240))) g.FillEllipse(e, x, y, size, size);
            }
            using (var glow = new GraphicsPath())  // the sun has just set
            {
                glow.AddEllipse(-60, horizon - 150, 860, 300);
                using (var p = new PathGradientBrush(glow) { CenterColor = Color.FromArgb(150, 250, 186, 110), SurroundColors = new[] { Color.FromArgb(0, 250, 186, 110) }, CenterPoint = new PointF(370, horizon) })
                    g.FillPath(p, glow);
            }
            using (var sea = new LinearGradientBrush(new RectangleF(0, horizon, LogicalWidth, LogicalHeight - horizon), Color.FromArgb(40, 78, 92), Color.FromArgb(5, 15, 24), 90f))
                g.FillRectangle(sea, 0, horizon, LogicalWidth, LogicalHeight - horizon);
            using (var reflection = new GraphicsPath())
            {
                reflection.AddEllipse(140, horizon - 26, 460, 170);
                using (var p = new PathGradientBrush(reflection) { CenterColor = Color.FromArgb(120, 244, 178, 104), SurroundColors = new[] { Color.FromArgb(0, 244, 178, 104) }, CenterPoint = new PointF(370, horizon + 6) })
                    g.FillPath(p, reflection);
            }
            DrawShip(g, 52, horizon + 8, 1.06f);
        }
        return b;
    }

    void DrawShip(Graphics g, float x0, float y0, float k)
    {
        var state = g.Save();
        g.TranslateTransform(x0, y0);
        g.ScaleTransform(k, k);
        Color hull = Color.FromArgb(7, 15, 24);
        using (var b = new SolidBrush(hull))
        using (var l = new Pen(hull, 1.6f))
        using (var thin = new Pen(hull, 1f))
        using (var barrel = new Pen(hull, 2.6f))
        {
            g.FillPolygon(b, new[] { new PointF(0, -15), new PointF(14, -17), new PointF(120, -19), new PointF(222, -21), new PointF(372, -22), new PointF(460, -31), new PointF(506, -38), new PointF(490, 2), new PointF(14, 2) });
            g.FillPolygon(b, new[] { new PointF(150, -19), new PointF(150, -36), new PointF(322, -36), new PointF(330, -21) });
            g.FillPolygon(b, new[] { new PointF(180, -36), new PointF(180, -48), new PointF(296, -48), new PointF(296, -36) });
            g.FillPolygon(b, new[] { new PointF(262, -48), new PointF(264, -72), new PointF(272, -92), new PointF(292, -92), new PointF(296, -72), new PointF(298, -48) });
            g.FillRectangle(b, 268, -100, 28, 8);
            g.FillRectangle(b, 274, -108, 14, 8);
            g.DrawLine(l, 281, -108, 281, -136);
            g.DrawLine(thin, 272, -124, 290, -124);
            g.DrawLine(thin, 281, -136, 200, -68);
            g.FillPolygon(b, new[] { new PointF(222, -48), new PointF(226, -84), new PointF(246, -84), new PointF(250, -48) });
            g.FillRectangle(b, 224, -88, 24, 4);
            g.FillPolygon(b, new[] { new PointF(190, -48), new PointF(192, -66), new PointF(210, -66), new PointF(212, -48) });
            g.FillRectangle(b, 156, -56, 18, 20);
            g.DrawLine(l, 165, -56, 165, -92);
            g.DrawLine(thin, 156, -82, 174, -82);
            // Turrets: two forward, one above the other, and one aft
            g.FillPolygon(b, new[] { new PointF(344, -22), new PointF(346, -33), new PointF(388, -33), new PointF(394, -24) });
            g.DrawLine(barrel, 388, -29, 446, -35);
            g.FillPolygon(b, new[] { new PointF(300, -36), new PointF(302, -48), new PointF(344, -48), new PointF(350, -38) });
            g.DrawLine(barrel, 344, -44, 402, -50);
            g.FillPolygon(b, new[] { new PointF(92, -19), new PointF(98, -31), new PointF(140, -31), new PointF(142, -19) });
            g.DrawLine(barrel, 98, -27, 42, -30);
        }
        using (var light = new SolidBrush(Color.FromArgb(220, 252, 196, 120)))
            foreach (var p in new[] { new PointF(276, -96), new PointF(286, -96), new PointF(274, -66), new PointF(286, -66), new PointF(236, -42), new PointF(206, -42) })
                g.FillRectangle(light, p.X, p.Y, 2.4f, 1.6f);
        g.Restore(state);
    }

    void DrawWaves(Graphics g)
    {
        float t = (float)clock.Elapsed.TotalSeconds;
        var layers = new[]
        {
            new { Y = 352f, A = 1.6f, K = 0.024f, V = 0.5f, C = Color.FromArgb(120, 26, 58, 74) },
            new { Y = 372f, A = 2.6f, K = 0.017f, V = -0.38f, C = Color.FromArgb(150, 16, 44, 60) },
            new { Y = 400f, A = 4.0f, K = 0.012f, V = 0.3f, C = Color.FromArgb(190, 10, 30, 44) },
            new { Y = 436f, A = 5.0f, K = 0.009f, V = -0.22f, C = Color.FromArgb(225, 7, 21, 33) }
        };
        foreach (var c in layers)
        {
            var points = new List<PointF> { new PointF(0, LogicalHeight) };
            for (float x = 0; x <= LogicalWidth + 12; x += 12)
                points.Add(new PointF(x, c.Y + c.A * (float)Math.Sin(x * c.K + t * c.V) + c.A * 0.45f * (float)Math.Sin(x * c.K * 2.3f - t * c.V * 1.4f)));
            points.Add(new PointF(LogicalWidth, LogicalHeight));
            using (var b = new SolidBrush(c.C)) g.FillPolygon(b, points.ToArray());
        }
    }

    void DrawTitle(Graphics g)
    {
        var big = GetFont(Theme.BoldFont, 50);
        WriteSpaced(g, "STEEL OCEAN", big, Color.FromArgb(120, 0, 0, 0), 34, 54, 4f);
        WriteSpaced(g, "STEEL OCEAN", big, Theme.Text, 32, 52, 4f);
        using (var b = new SolidBrush(Theme.Signal)) g.FillRectangle(b, 34, 118, 46, 3);
        WriteSpaced(g, Tr.S("LINSOFT SERVER", "SERVIDOR LINSOFT"), GetFont(Theme.BoldFont, 13), Color.FromArgb(214, 226, 236), 34, 130, 2.6f);
    }

    void DrawStat(Graphics g, float x, string label, string value, Color color)
    {
        WriteSpaced(g, label, GetFont(Theme.BoldFont, 10.5f), Theme.Muted, x, 410, 1.1f);
        Write(g, value, GetFont(Theme.BoldFont, 16), color, x, 427);
    }

    void DrawStats(Graphics g)
    {
        string latest = manifest != null ? manifest.Version : "—";
        bool upToDate = manifest != null && installed == manifest.Version;
        DrawStat(g, 34, Tr.S("INSTALLED PATCH", "PARCHE INSTALADO"), installed ?? Tr.S("None", "Ninguno"), installed == null ? Theme.Muted : upToDate ? Theme.Good : Theme.Signal);
        DrawStat(g, 188, Tr.S("LATEST PATCH", "ÚLTIMO PARCHE"), latest, Theme.Text);
        DrawStat(g, 318, Tr.S("LAUNCHER", "LANZADOR"), Patch.LauncherVersion, Theme.Text);
        string server = !serverKnown ? "…" : players.HasValue ? Tr.S("Online · ", "En línea · ") + players.Value : Tr.S("Offline", "Sin conexión");
        float x = 432;
        WriteSpaced(g, Tr.S("SERVER · PLAYERS", "SERVIDOR · JUGADORES"), GetFont(Theme.BoldFont, 10.5f), Theme.Muted, x, 410, 1.1f);
        using (var b = new SolidBrush(!serverKnown ? Theme.Dim : players.HasValue ? Theme.Good : Theme.Bad)) g.FillEllipse(b, x, 433, 9, 9);
        Write(g, server, GetFont(Theme.BoldFont, 16), Theme.Text, x + 15, 427);
    }

    void MeasureNotes(Graphics g, float width)
    {
        noteLines = new List<NoteLine>();
        float y = 0;
        var history = manifest != null ? manifest.History : new List<HistoryEntry>();
        foreach (var e in history)
        {
            string tag = e.Version == installed ? Tr.S("INSTALLED", "INSTALADO") : Patch.CompareVersions(e.Version, installed ?? "0") > 0 ? Tr.S("NEW", "NUEVO") : null;
            noteLines.Add(new NoteLine { Text = Tr.S("Patch ", "Parche ") + e.Version, Font = GetFont(Theme.BoldFont, 15), Color = Theme.Text, Y = y, Height = 22, Tag = tag });
            y += 21;
            if (!string.IsNullOrEmpty(e.Date))
            {
                noteLines.Add(new NoteLine { Text = e.Date, Font = GetFont(Theme.Font, 12), Color = Theme.Muted, Y = y, Height = 17 });
                y += 20;
            }
            foreach (string n in e.Notes)
            {
                var f = GetFont(Theme.Font, 13);
                float height = (float)Math.Ceiling(g.MeasureString(n, f, new SizeF(width - 14, 4000), StringFormat.GenericTypographic).Height) + 1;
                noteLines.Add(new NoteLine { Text = n, Font = f, Color = Color.FromArgb(206, 218, 228), Y = y, Height = height, X = 14, Bullet = true });
                y += height + 5;
            }
            y += 14;
        }
        notesHeight = y;
    }

    void DrawNotes(Graphics g)
    {
        var box = new RectangleF(620, 52, 308, 402);
        Fill(g, box, 10, Theme.Panel, Theme.Line);
        WriteSpaced(g, Tr.S("PATCH NOTES", "NOVEDADES"), GetFont(Theme.BoldFont, 12), Theme.Signal, box.X + 20, box.Y + 17, 1.8f);
        var inner = new RectangleF(box.X + 20, box.Y + 44, box.Width - 40, box.Height - 56);
        if (noteLines == null) MeasureNotes(g, inner.Width);
        if (noteLines.Count == 0)
        {
            Paragraph(g, manifest == null ? Tr.S("Patch notes will appear once connected to the server.", "Las notas aparecerán en cuanto se conecte con el servidor.") : Tr.S("This patch has no notes.", "Este parche no tiene notas."), GetFont(Theme.Font, 13), Theme.Muted, inner);
            return;
        }
        float maximum = Math.Max(0, notesHeight - inner.Height);
        scroll = Math.Max(0, Math.Min(scroll, maximum));
        var clip = g.Clip;
        g.SetClip(inner);
        foreach (var l in noteLines)
        {
            float y = inner.Y + l.Y - scroll;
            if (y + l.Height < inner.Y || y > inner.Bottom) continue;
            if (l.Bullet) using (var b = new SolidBrush(Theme.Sea)) g.FillEllipse(b, inner.X + 2, y + 6, 4, 4);
            if (l.X > 0) Paragraph(g, l.Text, l.Font, l.Color, new RectangleF(inner.X + l.X, y, inner.Width - l.X, l.Height + 2));
            else Write(g, l.Text, l.Font, l.Color, inner.X, y);
            if (l.Tag != null)
            {
                var f = GetFont(Theme.BoldFont, 10);
                float a = SpacedWidth(g, l.Tag, f, 0.8f) + 14;
                var r = new RectangleF(inner.Right - a, y + 2, a, 17);
                Color c = l.Tag == Tr.S("INSTALLED", "INSTALADO") ? Theme.Good : Theme.Signal;
                Fill(g, r, 8.5f, Color.FromArgb(36, c), Color.FromArgb(120, c));
                WriteSpaced(g, l.Tag, f, c, r.X + 7, r.Y + 2.6f, 0.8f);
            }
        }
        g.Clip = clip;
        if (maximum > 0)
        {
            float height = Math.Max(26, inner.Height * inner.Height / notesHeight);
            float y = inner.Y + (inner.Height - height) * scroll / maximum;
            Fill(g, new RectangleF(box.Right - 9, y, 3, height), 1.5f, Color.FromArgb(90, 255, 255, 255));
        }
    }

    void DrawBottomBar(Graphics g)
    {
        using (var b = new SolidBrush(Theme.Bar)) g.FillRectangle(b, 0, 470, LogicalWidth, LogicalHeight - 470);
        using (var l = new Pen(Theme.Line)) g.DrawLine(l, 0, 470, LogicalWidth, 470);
        const float x = 32, width = 650;
        Write(g, title, GetFont(Theme.BoldFont, 19), Theme.Text, x, 484);
        Paragraph(g, detail, GetFont(Theme.Font, 13), Theme.Muted, new RectangleF(x, 511, width - 190, 18), StringTrimming.EllipsisPath);
        if (figures.Length > 0)
        {
            var f = GetFont(Theme.BoldFont, 12.5f);
            float a = g.MeasureString(figures, f, PointF.Empty, typographic).Width;
            Write(g, figures, f, Theme.Text, x + width - a, 512);
        }
        var track = new RectangleF(x, 534, width, 7);
        if (progress != -1) Fill(g, track, 3.5f, Theme.Track);
        if (progress >= 0)
        {
            float a = Math.Max(7, (float)(width * Math.Min(1, progress)));
            var r = new RectangleF(x, 534, a, 7);
            using (var p = Rounded(r, 3.5f))
            using (var b = new LinearGradientBrush(new RectangleF(x, 534, width, 7), Theme.Sea, Theme.Signal, 0f))
                g.FillPath(b, p);
        }
        else if (progress == -2)
        {
            double cycle = clock.Elapsed.TotalSeconds % 1.6 / 1.6;
            float a = width * 0.24f, start = (float)(cycle * (width + a)) - a;
            var state = g.Save();
            g.SetClip(track);
            using (var p = Rounded(new RectangleF(x + start, 534, a, 7), 3.5f))
            using (var b = new SolidBrush(Theme.Sea))
                g.FillPath(b, p);
            g.Restore(state);
        }
        bool enabled = !busy && phase != Phase.Checking && phase != Phase.Installing;
        float next = DrawLink(g, x, 556, Tr.S("Game folder", "Carpeta del juego"), enabled, ChooseFolder);
        next = DrawLink(g, next, 556, Tr.S("Verify files", "Comprobar ficheros"), enabled, delegate { Start(Check); });
        next = DrawLink(g, next, 556, Tr.S("Check for updates", "Buscar actualizaciones"), enabled, delegate { Start(CheckManually); });
        DrawLink(g, next, 556, Tr.S("Linsoft website", "Web de Linsoft"), true, delegate { Process.Start(Patch.Website); });
        DrawMainButton(g, new RectangleF(712, 488, 216, 62));
        var small = GetFont(Theme.Font, 12);
        string text = manifest != null && manifest.Date != null ? Tr.S("Released ", "Publicado el ") + manifest.Date : "";
        float w = g.MeasureString(text, small, PointF.Empty, typographic).Width;
        Write(g, text, small, Theme.Dim, 712 + (216 - w) / 2, 558);
    }

    float DrawLink(Graphics g, float x, float y, string text, bool enabled, Action click)
    {
        var f = GetFont(Theme.Font, 12.5f);
        float a = g.MeasureString(text, f, PointF.Empty, typographic).Width;
        var r = new RectangleF(x, y - 3, a, 22);
        bool over = enabled && IsOver(r, false);
        Write(g, text, f, !enabled ? Theme.Dim : over ? Theme.Text : Theme.Muted, x, y);
        if (over) using (var l = new Pen(Theme.Muted)) g.DrawLine(l, x, y + 16, x + a, y + 16);
        if (enabled) AddZone(r, click, text, g);
        return x + a + 26;
    }

    bool CanPressMain() { return !busy && dialog == null && (phase == Phase.Ready || phase == Phase.Failed || phase == Phase.NoGame || phase == Phase.NeedsInstall); }

    string MainButtonText()
    {
        switch (phase)
        {
            case Phase.Ready: return Tr.S("PLAY", "JUGAR");
            case Phase.Failed: return Tr.S("RETRY", "REINTENTAR");
            case Phase.NoGame: return Tr.S("CHOOSE FOLDER", "ELEGIR CARPETA");
            case Phase.NeedsInstall: return Tr.S("INSTALL", "INSTALAR");
            case Phase.Installing: return Tr.S("UPDATING", "ACTUALIZANDO");
            default: return Tr.S("CHECKING", "COMPROBANDO");
        }
    }

    void DrawMainButton(Graphics g, RectangleF r)
    {
        bool enabled = CanPressMain();
        bool over = enabled && IsOver(r, false);
        Color top = !enabled ? Color.FromArgb(44, 60, 74) : over && pressed ? Theme.SignalDark : over ? Theme.SignalLight : Theme.Signal;
        Color bottom = !enabled ? Color.FromArgb(34, 47, 59) : Theme.SignalDark;
        using (var p = Rounded(r, 9))
        using (var b = new LinearGradientBrush(r, top, bottom, 90f))
            g.FillPath(b, p);
        string text = MainButtonText();
        float px = text.Length > 9 ? 17 : 23, gap = text.Length > 9 ? 1.4f : 3f;
        var f = GetFont(Theme.BoldFont, px);
        float a = SpacedWidth(g, text, f, gap) - gap;
        WriteSpaced(g, text, f, enabled ? Color.FromArgb(18, 28, 38) : Theme.Muted, r.X + (r.Width - a) / 2, r.Y + (r.Height - px * 1.2f) / 2, gap);
        if (enabled) AddZone(r, PressMain, text, g);
    }

    void DrawWindowButtons(Graphics g)
    {
        var minimize = new RectangleF(LogicalWidth - 92, 0, 46, 34);
        var close = new RectangleF(LogicalWidth - 46, 0, 46, 34);
        if (IsOver(minimize, false)) using (var b = new SolidBrush(Color.FromArgb(30, 255, 255, 255))) g.FillRectangle(b, minimize);
        if (IsOver(close, false)) using (var b = new SolidBrush(Color.FromArgb(220, 196, 60, 52))) g.FillRectangle(b, close);
        using (var l = new Pen(Color.FromArgb(220, 255, 255, 255), 1.2f))
        {
            g.DrawLine(l, minimize.X + 18, 18, minimize.X + 28, 18);
            g.DrawLine(l, close.X + 18, 12, close.X + 28, 22);
            g.DrawLine(l, close.X + 28, 12, close.X + 18, 22);
        }
        AddZone(minimize, delegate { WindowState = FormWindowState.Minimized; }, Tr.S("Minimize", "Minimizar"), g);
        AddZone(close, Close, Tr.S("Close", "Cerrar"), g);
    }

    void DrawDialog(Graphics g)
    {
        using (var b = new SolidBrush(Color.FromArgb(170, 3, 8, 14))) g.FillRectangle(b, 0, 0, LogicalWidth, LogicalHeight);
        const float width = 600, padding = 30;
        var bodyFont = GetFont(Theme.Font, 14);
        float textHeight = g.MeasureString(dialog.Body, bodyFont, new SizeF(width - padding * 2, 4000), StringFormat.GenericTypographic).Height;
        float height = padding + 34 + textHeight + 24 + 46 + padding;
        var r = new RectangleF((LogicalWidth - width) / 2, (LogicalHeight - height) / 2, width, height);
        Fill(g, r, 12, Color.FromArgb(255, 12, 25, 37), Color.FromArgb(60, 255, 255, 255));
        Write(g, dialog.Title, GetFont(Theme.BoldFont, 21), Theme.Text, r.X + padding, r.Y + padding - 4);
        Paragraph(g, dialog.Body, bodyFont, Color.FromArgb(206, 218, 228), new RectangleF(r.X + padding, r.Y + padding + 34, width - padding * 2, textHeight + 4));
        float y = r.Bottom - padding - 46, x = r.Right - padding;
        if (dialog.Yes != null)
        {
            var f = GetFont(Theme.BoldFont, 15);
            float a = g.MeasureString(dialog.Yes, f, PointF.Empty, typographic).Width + 44;
            var yes = new RectangleF(x - a, y, a, 46);
            bool over = IsOver(yes, true);
            using (var p = Rounded(yes, 8))
            using (var b = new LinearGradientBrush(yes, over ? Theme.SignalLight : Theme.Signal, Theme.SignalDark, 90f))
                g.FillPath(b, p);
            Write(g, dialog.Yes, f, Color.FromArgb(18, 28, 38), yes.X + 22, yes.Y + 12);
            AddZone(yes, delegate { Answer(true); }, dialog.Yes, g);
            x = yes.X - 12;
        }
        if (dialog.No != null)
        {
            var f = GetFont(Theme.BoldFont, 15);
            float a = g.MeasureString(dialog.No, f, PointF.Empty, typographic).Width + 44;
            var no = new RectangleF(x - a, y, a, 46);
            bool over = IsOver(no, true);
            Fill(g, no, 8, Color.FromArgb(over ? 34 : 14, 255, 255, 255), Color.FromArgb(70, 255, 255, 255));
            Write(g, dialog.No, f, Theme.Text, no.X + 22, no.Y + 12);
            AddZone(no, delegate { Answer(false); }, dialog.No, g);
        }
    }

    void Answer(bool yes)
    {
        var a = dialog;
        dialog = null;
        focused = -1;
        Invalidate();
        if (a != null && a.Answer != null) a.Answer(yes);
    }

    public void Simulate(string state, Manifest m, string givenInstalled, int? online)
    {
        manifest = m;
        installed = givenInstalled;
        players = online;
        serverKnown = true;
        noteLines = null;
        switch (state)
        {
            case "ready": SetState(Phase.Ready, Tr.S("Ready to play", "Listo para jugar"), Tr.S("Your game is up to date.", "Tu juego está al día."), 1, ""); break;
            case "updating": SetState(Phase.Installing, Tr.S("Updating to patch ", "Actualizando al parche ") + m.Version, "SeaGame/CookedPC/SeaGame.u", 0.42, "79 / 189 MB · 8.4 MB/s · 00:13"); break;
            case "first":
                SetState(Phase.Checking, Tr.S("Checking game files", "Comprobando los ficheros del juego"), "", -2, "");
                AskToInstall(@"E:\SteamLibrary\steamapps\common\Steel Ocean Wolves of Deep Sea", 37, 189);
                break;
            case "notinstalled": ShowNotInstalled(null, 37, 189); break;
            case "offline":
                players = null;
                SetState(Phase.Failed, Tr.S("Cannot reach Linsoft", "Sin conexión con Linsoft"), Tr.S("Check your internet connection and try again.", "Comprueba tu conexión a internet y vuelve a intentarlo."), -1, "");
                break;
            default: throw new ArgumentException(state);
        }
    }

    public void SaveCapture(string path)
    {
        using (var b = new Bitmap((int)(LogicalWidth * scale), (int)(LogicalHeight * scale)))
        using (var g = Graphics.FromImage(b))
        {
            PaintAll(g);
            b.Save(path, ImageFormat.Png);
        }
    }

    // --- What it does ---

    void OnUi(Action a) { if (IsHandleCreated && !IsDisposed) BeginInvoke(a); }

    void SetState(Phase p, string t, string d, double value, string numbers)
    {
        phase = p;
        title = t;
        detail = d;
        progress = value;
        figures = numbers;
        Invalidate();
    }

    void Report(Phase p, string t, string d, double value, string numbers = "") { OnUi(delegate { SetState(p, t, d, value, numbers); }); }

    string lastTrace;

    void WatchForTest(double now)
    {
        string line = phase + " | " + title;
        if (line != lastTrace)
        {
            lastTrace = line;
            File.AppendAllText(TestMode.Trace, line + " | installed " + installed + " | latest " + (manifest != null ? manifest.Version : "-") + Environment.NewLine);
        }
        if (TestMode.Accept && dialog != null) Answer(true);
        if (TestMode.Decline && dialog != null) Answer(false);
        bool finished = !busy && (phase == Phase.Ready || phase == Phase.Failed || phase == Phase.NoGame || phase == Phase.NeedsInstall) && now > 1;
        if (finished || now > TestMode.QuitAfter)
        {
            if (TestMode.FinalCapture != null) SaveCapture(TestMode.FinalCapture);
            Close();
        }
    }

    void OnTick()
    {
        Invalidate();
        double now = clock.Elapsed.TotalSeconds;
        if (TestMode.Active) { WatchForTest(now); return; }
        if (now - lastCheck > 600 && phase == Phase.Ready && dialog == null && !busy)
        {
            lastCheck = now;
            new Thread(CheckForUpdates) { IsBackground = true }.Start();
            new Thread(CheckServer) { IsBackground = true }.Start();
        }
    }

    void CheckServer()
    {
        int? n = null;
        try { n = Patch.PlayersOnline(); }
        catch (WebException) { }
        catch (IOException) { }
        catch (ArgumentException) { }
        catch (InvalidCastException) { }
        catch (KeyNotFoundException) { }
        OnUi(delegate { players = n; serverKnown = true; });
    }

    // While the window is open: a new patch is applied on its own.
    void CheckForUpdates()
    {
        try
        {
            var m = Patch.ReadManifest();
            if (m.Version != manifest.Version || Patch.HasNewerLauncher(m)) Check();
        }
        catch (Exception) { }  // no network right now: the next check will see it
    }

    void Start(ThreadStart work)
    {
        if (busy) return;
        new Thread(work) { IsBackground = true }.Start();
    }

    string ReadData(string file)
    {
        try { return File.ReadAllText(Path.Combine(dataFolder, file)).Trim(); }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    void WriteData(string file, string text)
    {
        Directory.CreateDirectory(dataFolder);
        File.WriteAllText(Path.Combine(dataFolder, file), text);
    }

    void Check()
    {
        busy = true;
        try
        {
            Report(Phase.Checking, Tr.S("Checking for updates", "Buscando actualizaciones"), Tr.S("Connecting to Linsoft…", "Conectando con Linsoft…"), -2);
            var m = Patch.ReadManifest();
            OnUi(delegate { manifest = m; noteLines = null; });
            manifest = m;
            if (!justUpdated && Patch.HasNewerLauncher(m) && UpdateLauncher(m)) return;
            game = TestMode.Game ?? ReadData("game.txt");
            if (game == null || !File.Exists(Path.Combine(game, Patch.GameExe))) game = TestMode.Active ? null : Patch.FindGame();  // a test never touches the real game
            if (game == null)
            {
                Report(Phase.NoGame, Tr.S("Steel Ocean was not found", "No encuentro Steel Ocean"), Tr.S("Install it from Steam, or choose the folder it is in.", "Instálalo desde Steam, o indica en qué carpeta está."), -1);
                return;
            }
            Report(Phase.Checking, Tr.S("Checking game files", "Comprobando los ficheros del juego"), game, 0);
            var missing = Patch.Pending(game, m, delegate(int i, int n) { Report(Phase.Checking, Tr.S("Checking game files", "Comprobando los ficheros del juego"), game, (double)i / n); });
            if (missing.Count == 0 && !Patch.HasFilesToDelete(game, m)) { MarkReady(); return; }
            long total = 0;
            foreach (var f in missing) total += f.GzSize;
            if (File.Exists(Path.Combine(dataFolder, "accepted.txt"))) { Install(missing); return; }
            OnUi(delegate { AskToInstall(game, missing.Count, total / 1048576, missing); });
        }
        catch (WebException)
        {
            Report(Phase.Failed, Tr.S("Cannot reach Linsoft", "Sin conexión con Linsoft"), Tr.S("Check your internet connection and try again.", "Comprueba tu conexión a internet y vuelve a intentarlo."), -1);
        }
        catch (Exception e)
        {
            Report(Phase.Failed, Tr.S("Could not check the patch", "No se puede comprobar el parche"), e.Message, -1);
        }
        finally
        {
            busy = false;
        }
    }

    // Tr.S("Check for updates", "Buscar actualizaciones"): answers right away, and only looks at the files if there is a new patch.
    void CheckManually()
    {
        busy = true;
        try
        {
            Report(Phase.Checking, Tr.S("Checking for updates", "Buscando actualizaciones"), Tr.S("Connecting to Linsoft…", "Conectando con Linsoft…"), -2);
            var m = Patch.ReadManifest();
            OnUi(delegate { manifest = m; noteLines = null; });
            manifest = m;
            string failed = null;
            if (Patch.HasNewerLauncher(m))
            {
                if (UpdateLauncher(m)) return;
                failed = string.Format(Tr.S("Launcher {0} could not be installed. Download it from the website.", "No se pudo instalar el lanzador {0}. Descárgalo de la web."), m.Launcher.Version);
            }
            if (game == null || m.Version != installed) { Check(); return; }
            string checkedAt = DateTime.Now.ToString("HH:mm");
            Report(Phase.Ready, failed == null ? Tr.S("Everything is up to date", "Todo está al día") : Tr.S("Your game is up to date", "Tu juego está al día"),
                   failed ?? string.Format(Tr.S("Patch {0} and launcher {1} are the latest. Checked at {2}.", "El parche {0} y el lanzador {1} son los últimos. Comprobado a las {2}."), m.Version, Patch.LauncherVersion, checkedAt), 1);
        }
        catch (WebException)
        {
            Report(Phase.Failed, Tr.S("Cannot reach Linsoft", "Sin conexión con Linsoft"), Tr.S("Check your internet connection and try again.", "Comprueba tu conexión a internet y vuelve a intentarlo."), -1);
        }
        catch (Exception e)
        {
            Report(Phase.Failed, Tr.S("Could not check the patch", "No se puede comprobar el parche"), e.Message, -1);
        }
        finally
        {
            busy = false;
        }
    }

    bool UpdateLauncher(Manifest m)
    {
        try
        {
            long total = m.Launcher.Size;
            Patch.ReplaceLauncher(m, delegate(long v)
            {
                Report(Phase.Installing, Tr.S("Updating the launcher to version ", "Actualizando el lanzador a la versión ") + m.Launcher.Version, "", total > 0 ? (double)v / total : -2);
            });
            Process.Start(Application.ExecutablePath, "--updated");
            OnUi(Close);
            return true;
        }
        catch (Exception)
        {
            return false;  // no rights on that folder, or no network: carry on with this one; the patch matters more
        }
    }

    void AskToInstall(string folder, int files, long mb, List<PatchFile> missing = null)
    {
        dialog = new Dialog
        {
            Title = Tr.S("Install the Linsoft patch", "Instalar el parche de Linsoft"),
            Body = string.Format(Tr.S("This will modify your Steel Ocean installation in:\n{0}\n\nDownload: {1} MB. It changes {2} files and removes unused Steam and platform components.\n\nThe game will connect to the Linsoft server. If it crashes, it sends that server a report with the game log and computer details (Windows, graphics card, MAC address and disk serial number).\n\nFuture updates install automatically when you open this program. To restore the original game: Steam > Properties > Installed Files > Verify integrity.",
                                      "Se va a modificar tu instalación de Steel Ocean en:\n{0}\n\nDescarga: {1} MB. Cambia {2} ficheros y elimina los componentes de Steam y plataformas que no se usan.\n\nEl juego quedará conectado al servidor de Linsoft. Si falla, envía un informe a ese servidor con el registro del juego y datos del equipo (Windows, tarjeta gráfica, MAC y número de serie del disco).\n\nLas siguientes actualizaciones se instalarán solas al abrir este programa. Para volver al juego original: Steam > Propiedades > Archivos instalados > Verificar integridad."), folder, mb, files),
            Yes = Tr.S("Accept and install", "Aceptar e instalar"),
            No = Tr.S("Not now", "Ahora no"),
            Answer = delegate(bool yes)
            {
                if (!yes) { ShowNotInstalled(missing, files, mb); return; }
                WriteData("accepted.txt", DateTime.UtcNow.ToString("o"));
                Start(delegate { busy = true; try { Install(missing); } finally { busy = false; } });
            }
        };
        Invalidate();
    }

    // "Not now": back to the main screen, with the main button ready to ask again.
    void ShowNotInstalled(List<PatchFile> missing, int files, long mb)
    {
        pendingFiles = missing;
        pendingMb = mb;
        SetState(Phase.NeedsInstall, Tr.S("The patch is not installed", "El parche no está instalado"),
                 string.Format(Tr.S("It changes {0} files and downloads {1} MB. Press Install when you are ready.", "Cambia {0} ficheros y descarga {1} MB. Pulsa Instalar cuando quieras."), files, mb), -1, "");
    }

    void Install(List<PatchFile> missing)
    {
        var m = manifest;
        string name = Tr.S("Updating to patch ", "Actualizando al parche ") + m.Version;
        try
        {
            if (!TestMode.Active && Process.GetProcessesByName("SeaGame").Length > 0)
            {
                Report(Phase.Failed, Tr.S("Close the game", "Cierra el juego"), Tr.S("There is an update, and it cannot be installed while the game is open.", "Hay una actualización, y no se puede instalar con el juego abierto."), -1);
                return;
            }
            var watch = Stopwatch.StartNew();
            long last = 0;
            Patch.Apply(game, m, missing, delegate(long v, long total, string f)
            {
                if (watch.ElapsedMilliseconds - last < 100 && v < total) return;
                last = watch.ElapsedMilliseconds;
                double seconds = Math.Max(0.1, watch.Elapsed.TotalSeconds), speed = v / seconds;
                string left = speed > 1024 ? TimeSpan.FromSeconds((total - v) / speed).ToString(@"mm\:ss") : "--:--";
                Report(Phase.Installing, name, f, total > 0 ? (double)v / total : 0, string.Format(CultureInfo.InvariantCulture, "{0} / {1} MB · {2:0.0} MB/s · {3}", v / 1048576, total / 1048576, speed / 1048576, left));
            });
            MarkReady();
        }
        catch (UnauthorizedAccessException)
        {
            OnUi(delegate
            {
                dialog = new Dialog
                {
                    Title = Tr.S("Permission needed", "Hacen falta permisos"),
                    Body = Tr.S("Administrator rights are needed to write to the game folder.", "Hacen falta permisos de administrador para escribir en la carpeta del juego."),
                    Yes = Tr.S("Reopen with rights", "Abrir con permisos"),
                    No = Tr.S("Cancel", "Cancelar"),
                    Answer = delegate(bool yes)
                    {
                        if (!yes) { SetState(Phase.Failed, Tr.S("Could not install", "No se pudo instalar"), Tr.S("Missing rights on the game folder.", "Faltan permisos en la carpeta del juego."), -1, ""); return; }
                        try { Process.Start(new ProcessStartInfo(Application.ExecutablePath) { UseShellExecute = true, Verb = "runas" }); Close(); }
                        catch (System.ComponentModel.Win32Exception) { SetState(Phase.Failed, Tr.S("Could not install", "No se pudo instalar"), Tr.S("Missing rights on the game folder.", "Faltan permisos en la carpeta del juego."), -1, ""); }
                    }
                };
            });
        }
        catch (WebException)
        {
            Report(Phase.Failed, Tr.S("The download was interrupted", "Se ha cortado la descarga"), Tr.S("Try again: files already finished are not downloaded twice.", "Vuelve a intentarlo: lo ya descargado no se repite."), -1);
        }
        catch (Exception e)
        {
            Report(Phase.Failed, Tr.S("Could not apply the patch", "No se pudo aplicar el parche"), e.Message, -1);
        }
    }

    void MarkReady()
    {
        WriteData("game.txt", game);
        WriteData("version.txt", manifest.Version);
        try { if (!TestMode.Active) CreateShortcut(); }
        catch (Exception) { }  // the game works without a shortcut
        string version = manifest.Version;
        OnUi(delegate
        {
            installed = version;
            noteLines = null;
            SetState(Phase.Ready, Tr.S("Ready to play", "Listo para jugar"), Tr.S("Your game is up to date.", "Tu juego está al día."), 1, "");
            focused = -1;
        });
    }

    // The shortcut points to a fixed copy of the launcher, so it looks for updates every time the game is opened.
    void CreateShortcut()
    {
        Directory.CreateDirectory(dataFolder);
        string copy = Path.Combine(dataFolder, "SteelOceanLinsoft.exe");
        if (!string.Equals(Application.ExecutablePath, copy, StringComparison.OrdinalIgnoreCase))
            File.Copy(Application.ExecutablePath, copy, true);
        string lnk = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Steel Ocean (Linsoft).lnk");
        dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell"));
        dynamic a = shell.CreateShortcut(lnk);
        a.TargetPath = copy;
        a.WorkingDirectory = dataFolder;
        a.IconLocation = copy + ",0";
        a.Description = "Steel Ocean - Linsoft";
        a.Save();
    }

    void ChooseFolder()
    {
        using (var d = new FolderBrowserDialog { Description = Tr.S("Steel Ocean installation folder", "Carpeta de instalación de Steel Ocean") })
        {
            if (d.ShowDialog(this) != DialogResult.OK) return;
            if (!File.Exists(Path.Combine(d.SelectedPath, Patch.GameExe)))
            {
                dialog = new Dialog { Title = Tr.S("Wrong folder", "Esa carpeta no sirve"), Body = Tr.S("That folder does not contain the game.", "Esa carpeta no tiene el juego."), Yes = Tr.S("OK", "Entendido") };
                Invalidate();
                return;
            }
            WriteData("game.txt", d.SelectedPath);
            Start(Check);
        }
    }

    void PressMain()
    {
        if (!CanPressMain()) return;
        switch (phase)
        {
            case Phase.NoGame: ChooseFolder(); return;
            case Phase.NeedsInstall: AskToInstall(game, pendingFiles.Count, pendingMb, pendingFiles); return;
            case Phase.Failed: Start(Check); return;
        }
        if (Process.GetProcessesByName("SeaGame").Length > 0)
        {
            dialog = new Dialog { Title = Tr.S("The game is already open", "El juego ya está abierto"), Body = Tr.S("Close it before opening it again.", "Ciérralo antes de abrirlo otra vez."), Yes = Tr.S("OK", "Entendido") };
            Invalidate();
            return;
        }
        string exe = Path.Combine(game, manifest.Exe.Replace('/', '\\'));
        Process.Start(new ProcessStartInfo(exe, manifest.Arguments + " -server=steelocean.linsoft.pe/dumplog/zj") { WorkingDirectory = Path.GetDirectoryName(exe), UseShellExecute = false });
        Close();
    }
}

static class Program
{
    [DllImport("user32.dll")] static extern bool SetProcessDPIAware();

    static string Value(string[] a, string name)
    {
        int p = Array.IndexOf(a, name);
        return p >= 0 && p + 1 < a.Length ? a[p + 1] : null;
    }

    static bool Has(string[] a, string name) { return Array.IndexOf(a, name) >= 0; }

    // What does not open a window, for automated tests (never with the real game):
    //   --version; --test-apply --dir PATH --url URL: applies the patch; --test-self-update --url URL: replaces itself if there is a newer launcher;
    //   --test-window --url URL --dir PATH --data PATH --trace FILE [--accept|--decline] [--final FILE.png]: the real window, off-screen;
    //   --capture FILE.png --state ready|updating|first|notinstalled|offline --manifest FILE [--installed V] [--players N] [--scale X]: paints without showing anything on screen;
    //   --language es|en: instead of the Windows language.
    [STAThread]
    static int Main(string[] a)
    {
        ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072;
        if (Value(a, "--url") != null) Patch.BaseUrl = Value(a, "--url");
        if (Has(a, "--version")) { Console.WriteLine(Patch.LauncherVersion); return 0; }
        if (Value(a, "--language") != null) Tr.Spanish = Value(a, "--language") == "es";
        if (Has(a, "--test-apply") || Has(a, "--test-self-update"))
        {
            try
            {
                var m = Patch.ReadManifest();
                if (Has(a, "--test-self-update"))
                {
                    Patch.CleanUpOldLauncher();
                    if (!Patch.HasNewerLauncher(m)) { Console.WriteLine("no newer launcher"); return 0; }
                    Patch.ReplaceLauncher(m, delegate(long v) { });
                    Console.WriteLine("replaced with " + m.Launcher.Version);
                    return 0;
                }
                string game = Value(a, "--dir");
                var missing = Patch.Pending(game, m, delegate(int i, int n) { });
                Console.WriteLine("pending " + missing.Count + (Patch.HasFilesToDelete(game, m) ? " and files to delete" : ""));
                Patch.Apply(game, m, missing, delegate(long v, long t, string f) { });
                Console.WriteLine("after " + Patch.Pending(game, m, delegate(int i, int n) { }).Count + (Patch.HasFilesToDelete(game, m) ? " not deleted" : ""));
                return 0;
            }
            catch (Exception e) { Console.WriteLine("ERROR " + e.GetType().Name + ": " + e.Message); return 2; }
        }
        if (Value(a, "--capture") != null)
        {
            float captureScale = Value(a, "--scale") != null ? float.Parse(Value(a, "--scale"), CultureInfo.InvariantCulture) : 1f;
            using (var w = new MainWindow(captureScale, false))
            {
                string online = Value(a, "--players");
                w.Simulate(Value(a, "--state") ?? "ready", Patch.Parse(File.ReadAllText(Value(a, "--manifest"), Encoding.UTF8)), Value(a, "--installed"), online != null ? (int?)int.Parse(online) : null);
                w.SaveCapture(Value(a, "--capture"));
            }
            return 0;
        }
        if (Has(a, "--test-window"))
        {
            TestMode.Active = true;
            TestMode.DataFolder = Value(a, "--data");
            TestMode.Game = Value(a, "--dir");
            TestMode.FinalCapture = Value(a, "--final");
            TestMode.Trace = Value(a, "--trace");
            TestMode.Accept = Has(a, "--accept");
            TestMode.Decline = Has(a, "--decline");
            Application.EnableVisualStyles();
            Application.Run(new MainWindow(1f, false));
            return 0;
        }
        bool updated = Has(a, "--updated");
        bool created;
        using (var single = new Mutex(true, @"Local\SteelOceanLinsoftLauncher", out created))
        {
            bool mine = created;
            if (!mine && updated)  // the one that started us is exiting
            {
                try { mine = single.WaitOne(5000); }
                catch (AbandonedMutexException) { mine = true; }
            }
            if (!mine) return 0;  // already open
            try { SetProcessDPIAware(); } catch (EntryPointNotFoundException) { }
            Application.EnableVisualStyles();
            float scale;
            using (var g = Graphics.FromHwnd(IntPtr.Zero)) scale = g.DpiX / 96f;
            var screen = Screen.PrimaryScreen.WorkingArea;
            scale = Math.Max(0.6f, Math.Min(scale, Math.Min(screen.Width / 980f, screen.Height / 620f)));
            Application.Run(new MainWindow(scale, updated));
        }
        return 0;
    }
}
