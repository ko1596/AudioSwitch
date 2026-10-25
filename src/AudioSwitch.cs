// AudioSwitch - 切換 Windows 預設音訊裝置 (播放 / 錄音)
// 用法:
//   AudioSwitch.exe out [名稱關鍵字...]   循環切換預設播放裝置 (喇叭/耳機)
//   AudioSwitch.exe in  [名稱關鍵字...]   循環切換預設錄音裝置 (麥克風)
//   AudioSwitch.exe list                  列出目前所有可用裝置
// 有給關鍵字時, 只在名稱符合的裝置之間輪流切換; 沒給則輪流切換所有啟用中的裝置。
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Windows.Forms;

[assembly: System.Reflection.AssemblyTitle("AudioSwitch")]
[assembly: System.Reflection.AssemblyVersion("1.0.0.0")]
[assembly: System.Reflection.AssemblyFileVersion("1.0.0.0")]

[ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
class MMDeviceEnumerator { }

[ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IMMDeviceEnumerator
{
    void EnumAudioEndpoints(int dataFlow, int stateMask, out IMMDeviceCollection devices);
    void GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
}

[ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IMMDeviceCollection
{
    void GetCount(out uint count);
    void Item(uint index, out IMMDevice device);
}

[ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IMMDevice
{
    void Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
    void OpenPropertyStore(int access, out IPropertyStore store);
    void GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
    void GetState(out int state);
}

[StructLayout(LayoutKind.Sequential)]
struct PropertyKey { public Guid fmtid; public int pid; }

[StructLayout(LayoutKind.Sequential)]
struct PropVariant { public ushort vt; public ushort r1, r2, r3; public IntPtr p; public IntPtr p2; }

[ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IPropertyStore
{
    void GetCount(out uint count);
    void GetAt(uint index, out PropertyKey key);
    void GetValue(ref PropertyKey key, out PropVariant value);
    void SetValue(ref PropertyKey key, ref PropVariant value);
    void Commit();
}

[ComImport, Guid("870AF99C-171D-4F9E-AF0D-E63DF40C2BC9")]
class PolicyConfigClient { }

// 未公開但穩定的 IPolicyConfig, 方法順序必須完全一致
[ComImport, Guid("F8679F50-850A-41CF-9C72-430F290290C8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IPolicyConfig
{
    [PreserveSig] int GetMixFormat([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr fmt);
    [PreserveSig] int GetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string id, int def, IntPtr fmt);
    [PreserveSig] int ResetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string id);
    [PreserveSig] int SetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr a, IntPtr b);
    [PreserveSig] int GetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string id, int def, IntPtr a, IntPtr b);
    [PreserveSig] int SetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr a);
    [PreserveSig] int GetShareMode([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr a);
    [PreserveSig] int SetShareMode([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr a);
    [PreserveSig] int GetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr key, IntPtr v);
    [PreserveSig] int SetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr key, IntPtr v);
    [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string id, int role);
    [PreserveSig] int SetEndpointVisibility([MarshalAs(UnmanagedType.LPWStr)] string id, int visible);
}

class Dev { public string Id, Name; }

// 系統匣小工具: 點圖示列出播放/錄音裝置, 點選即切換預設
class TrayApp : ApplicationContext
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string RunName = "AudioSwitchTray";

    readonly NotifyIcon ni = new NotifyIcon();
    readonly ContextMenuStrip menu = new ContextMenuStrip();
    readonly IMMDeviceEnumerator en = (IMMDeviceEnumerator)new MMDeviceEnumerator();

    public TrayApp()
    {
        menu.Opening += delegate { Rebuild(); };
        ni.ContextMenuStrip = menu;
        ni.Icon = MakeIcon();
        ni.Visible = true;
        UpdateTip();
        ni.MouseUp += delegate(object s, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            // 左鍵也開同一個選單 (NotifyIcon 沒公開這個方法, 正確處理點外面關閉)
            typeof(NotifyIcon).GetMethod("ShowContextMenu",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).Invoke(ni, null);
        };
    }

    internal static Icon MakeIcon()
    {
        using (var bmp = new Bitmap(32, 32))
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.FillEllipse(new SolidBrush(Color.FromArgb(0, 120, 212)), 0, 0, 31, 31);
            g.FillPolygon(Brushes.White, new[] {
                new Point(7, 12), new Point(12, 12), new Point(18, 7), new Point(18, 25), new Point(12, 20), new Point(7, 20) });
            using (var pen = new Pen(Color.White, 2f))
            {
                g.DrawArc(pen, 15, 10, 8, 12, -60, 120);
                g.DrawArc(pen, 12, 6, 14, 20, -60, 120);
            }
            return Icon.FromHandle(bmp.GetHicon());
        }
    }

    string NameOf(int flow)
    {
        string id = Program.GetDefaultId(en, flow);
        foreach (var d in Program.GetDevices(en, flow)) if (d.Id == id) return d.Name;
        return "(無)";
    }

    void UpdateTip()
    {
        string t = "播放: " + NameOf(0);
        ni.Text = t.Length > 63 ? t.Substring(0, 63) : t;
    }

    void AddSection(string title, int flow)
    {
        var head = new ToolStripMenuItem(title) { Enabled = false };
        menu.Items.Add(head);
        string cur = Program.GetDefaultId(en, flow);
        foreach (var d in Program.GetDevices(en, flow))
        {
            var dev = d;
            var item = new ToolStripMenuItem(dev.Name.Replace("&", "&&")) { Checked = dev.Id == cur };
            item.Click += delegate { Program.SetDefault(dev.Id); UpdateTip(); };
            menu.Items.Add(item);
        }
    }

    void Rebuild()
    {
        menu.Items.Clear();
        menu.Items.Add(new ToolStripMenuItem("🎛 設定檔") { Enabled = false });
        var ps = Program.LoadProfiles();
        int cur = Program.CurrentProfile(en, ps);
        for (int k = 0; k < ps.Count; k++)
        {
            int idx = k;
            var p = ps[k];
            var item = new ToolStripMenuItem(p.Name.Replace("&", "&&")) { Checked = k == cur };
            item.Click += delegate
            {
                string on, inn;
                if (Program.ApplyProfile(en, p, out on, out inn))
                {
                    UpdateTip();
                    new OsdForm(p.Icon, p.Name, idx, ps.Count).Show();
                }
                else ni.ShowBalloonTip(2000, "AudioSwitch", "找不到「" + p.Name + "」的裝置", ToolTipIcon.Warning);
            };
            menu.Items.Add(item);
        }
        var edit = new ToolStripMenuItem("編輯設定檔…");
        edit.Click += delegate
        {
            string editor = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(Application.ExecutablePath), "AudioSwitch-Editor.exe");
            if (System.IO.File.Exists(editor)) System.Diagnostics.Process.Start(editor);
            else System.Diagnostics.Process.Start("notepad.exe", Program.ProfilePath);
        };
        menu.Items.Add(edit);
        menu.Items.Add(new ToolStripSeparator());
        AddSection("🔊 播放裝置", 0);
        menu.Items.Add(new ToolStripSeparator());
        AddSection("🎤 錄音裝置", 1);
        menu.Items.Add(new ToolStripSeparator());

        var auto = new ToolStripMenuItem("開機自動啟動") { Checked = IsAutoStart() };
        auto.Click += delegate { SetAutoStart(!IsAutoStart()); };
        menu.Items.Add(auto);

        var sound = new ToolStripMenuItem("開啟 Windows 音效設定");
        sound.Click += delegate { System.Diagnostics.Process.Start("ms-settings:sound"); };
        menu.Items.Add(sound);

        var quit = new ToolStripMenuItem("結束");
        quit.Click += delegate { ni.Visible = false; ExitThread(); };
        menu.Items.Add(quit);
    }

    static bool IsAutoStart()
    {
        using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey))
            return k != null && k.GetValue(RunName) != null;
    }

    static void SetAutoStart(bool on)
    {
        using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey, true))
        {
            if (on) k.SetValue(RunName, "\"" + Application.ExecutablePath + "\"");
            else k.DeleteValue(RunName, false);
        }
    }
}

// 設定檔編輯器: 左邊清單 (新增/刪除/排序), 右邊編輯名稱、輸出/輸入裝置、圖示, 並即時預覽提示卡
class EditorForm : Form
{
    class IconItem
    {
        public string Key, Label;
        public override string ToString() { return Label; }
    }

    static readonly IconItem[] Icons = {
        new IconItem { Key = "headset", Label = "耳機  (headset)" },
        new IconItem { Key = "speaker", Label = "喇叭  (speaker)" },
        new IconItem { Key = "mic", Label = "麥克風  (mic)" },
        new IconItem { Key = "usbmic", Label = "喇叭 + 麥克風  (usbmic)" },
        new IconItem { Key = "amp", Label = "擴大機  (amp)" },
    };

    readonly IMMDeviceEnumerator en = (IMMDeviceEnumerator)new MMDeviceEnumerator();
    readonly List<Program.Profile> ps;
    List<Dev> outs, ins;
    bool loading, dirty;

    ListBox lst;
    TextBox txtName;
    ComboBox cbOut, cbIn, cbIcon;
    Label stOut, stIn, lblApplied;
    PictureBox pic;
    Button btnDel, btnUp, btnDown, btnApply;

    Program.Profile Cur { get { int i = lst.SelectedIndex; return i >= 0 && i < ps.Count ? ps[i] : null; } }

    public EditorForm()
    {
        ps = Program.LoadProfiles();
        Text = "AudioSwitch 設定檔編輯器";
        Font = new Font("Microsoft JhengHei UI", 10f);
        AutoScaleDimensions = new SizeF(96f, 96f);
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(800, 490);
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;

        lst = new ListBox { Location = new Point(16, 16), Size = new Size(230, 372), IntegralHeight = false };
        lst.SelectedIndexChanged += delegate { if (!loading) ShowCurrent(); };
        Controls.Add(lst);

        var btnAdd = Btn("新增", 16, 396, 70, 32); btnAdd.Click += delegate { AddProfile(); };
        btnDel = Btn("刪除", 92, 396, 70, 32); btnDel.Click += delegate { DeleteProfile(); };
        btnUp = Btn("▲", 168, 396, 36, 32); btnUp.Click += delegate { MoveItem(-1); };
        btnDown = Btn("▼", 210, 396, 36, 32); btnDown.Click += delegate { MoveItem(1); };

        Controls.Add(Lbl("名稱", 272, 24));
        txtName = new TextBox { Location = new Point(352, 20), Size = new Size(430, 28) };
        txtName.TextChanged += delegate { Edited(); };
        Controls.Add(txtName);

        Controls.Add(Lbl("輸出 (播放)", 272, 70));
        cbOut = new ComboBox { Location = new Point(352, 66), Size = new Size(430, 28), DropDownStyle = ComboBoxStyle.DropDown };
        cbOut.TextChanged += delegate { Edited(); };
        Controls.Add(cbOut);
        stOut = Status(352, 96);

        Controls.Add(Lbl("輸入 (錄音)", 272, 126));
        cbIn = new ComboBox { Location = new Point(352, 122), Size = new Size(430, 28), DropDownStyle = ComboBoxStyle.DropDown };
        cbIn.TextChanged += delegate { Edited(); };
        Controls.Add(cbIn);
        stIn = Status(352, 152);

        Controls.Add(Lbl("圖示", 272, 182));
        cbIcon = new ComboBox { Location = new Point(352, 178), Size = new Size(430, 28), DropDownStyle = ComboBoxStyle.DropDownList };
        cbIcon.Items.AddRange(Icons);
        cbIcon.SelectedIndexChanged += delegate { Edited(); };
        Controls.Add(cbIcon);

        var panel = new Panel { Location = new Point(272, 224), Size = new Size(510, 170), BackColor = Color.FromArgb(24, 26, 32) };
        pic = new PictureBox { Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.CenterImage };
        panel.Controls.Add(pic);
        Controls.Add(panel);

        var hint = Lbl("輸出/輸入可從清單選，也可直接輸入裝置名稱的一部分；留空代表不更動。\r\n按切換鍵時依左邊清單的順序輪流，找不到裝置的設定檔會自動跳過。", 272, 402);
        hint.AutoSize = false; hint.Size = new Size(510, 44); hint.ForeColor = Color.Gray; hint.Font = new Font(Font.FontFamily, 9f);
        Controls.Add(hint);

        var btnSave = Btn("儲存", 272, 446, 100, 34); btnSave.Click += delegate { Save(); };
        btnApply = Btn("立即切到這組", 380, 446, 140, 34); btnApply.Click += delegate { ApplyNow(); };
        var btnRefresh = Btn("重新讀取裝置", 528, 446, 130, 34); btnRefresh.Click += delegate { LoadDevices(); ShowCurrent(); };
        var btnClose = Btn("關閉", 682, 446, 100, 34); btnClose.Click += delegate { Close(); };
        lblApplied = new Label { Location = new Point(16, 452), Size = new Size(240, 24), ForeColor = Color.Gray, Text = "" };
        Controls.Add(lblApplied);

        FormClosing += OnClosing;
        LoadDevices();
        RefreshList(ps.Count > 0 ? 0 : -1);
    }

    Button Btn(string t, int x, int y, int w, int h)
    {
        var b = new Button { Text = t, Location = new Point(x, y), Size = new Size(w, h) };
        Controls.Add(b);
        return b;
    }

    static Label Lbl(string t, int x, int y) { return new Label { Text = t, Location = new Point(x, y), AutoSize = true }; }

    Label Status(int x, int y)
    {
        var l = new Label { Location = new Point(x, y), Size = new Size(430, 20), Font = new Font(Font.FontFamily, 8.5f) };
        Controls.Add(l);
        return l;
    }

    void LoadDevices()
    {
        outs = Program.GetDevices(en, 0);
        ins = Program.GetDevices(en, 1);
        bool was = loading; loading = true;
        string o = cbOut.Text, i = cbIn.Text;
        cbOut.Items.Clear(); foreach (var d in outs) cbOut.Items.Add(d.Name);
        cbIn.Items.Clear(); foreach (var d in ins) cbIn.Items.Add(d.Name);
        cbOut.Text = o; cbIn.Text = i;
        loading = was;
    }

    string ListText(int i) { return (i + 1) + ".  " + ps[i].Name; }

    void RefreshList(int sel)
    {
        loading = true;
        lst.Items.Clear();
        for (int i = 0; i < ps.Count; i++) lst.Items.Add(ListText(i));
        if (sel >= ps.Count) sel = ps.Count - 1;
        lst.SelectedIndex = sel;
        loading = false;
        ShowCurrent();
    }

    void ShowCurrent()
    {
        var p = Cur;
        bool on = p != null;
        foreach (Control c in new Control[] { txtName, cbOut, cbIn, cbIcon, btnDel, btnUp, btnDown, btnApply }) c.Enabled = on;
        loading = true;
        txtName.Text = on ? p.Name : "";
        cbOut.Text = on ? p.Out : "";
        cbIn.Text = on ? p.In : "";
        cbIcon.SelectedIndex = -1;
        if (on) foreach (var it in Icons) if (it.Key == p.Icon) cbIcon.SelectedItem = it;
        loading = false;
        Update_();
    }

    void Update_()
    {
        SetStatus(stOut, cbOut.Text, outs);
        SetStatus(stIn, cbIn.Text, ins);
        var old = pic.Image;
        var p = Cur;
        pic.Image = p == null ? null : OsdForm.Render(p.Icon, p.Name, lst.SelectedIndex, ps.Count, DpiScale());
        if (old != null) old.Dispose();
    }

    static float DpiScale() { using (var g = Graphics.FromHwnd(IntPtr.Zero)) return g.DpiX / 96f; }

    static void SetStatus(Label l, string kw, List<Dev> devs)
    {
        kw = kw.Trim();
        if (kw.Length == 0) { l.Text = "不更動"; l.ForeColor = Color.Gray; return; }
        var d = Program.Find(devs, kw);
        if (d != null) { l.Text = "● 對應到: " + d.Name; l.ForeColor = Color.FromArgb(0, 140, 70); }
        else { l.Text = "● 目前找不到符合的裝置 (沒連線或沒開機?)"; l.ForeColor = Color.FromArgb(200, 60, 40); }
    }

    void Edited()
    {
        var p = Cur;
        if (loading || p == null) return;
        var it = cbIcon.SelectedItem as IconItem;
        string icon = it != null ? it.Key : p.Icon;
        // 下拉選單在視窗建立時會補發文字變更事件, 內容沒真的變就不算編輯
        if (p.Name == txtName.Text && p.Out == cbOut.Text && p.In == cbIn.Text && p.Icon == icon) return;
        p.Name = txtName.Text;
        p.Out = cbOut.Text;
        p.In = cbIn.Text;
        p.Icon = icon;
        SetDirty(true);
        int i = lst.SelectedIndex;
        loading = true; lst.Items[i] = ListText(i); lst.SelectedIndex = i; loading = false;
        Update_();
    }

    void SetDirty(bool d)
    {
        dirty = d;
        Text = "AudioSwitch 設定檔編輯器" + (d ? " *" : "");
    }

    void AddProfile()
    {
        // 新設定檔預設帶入「目前正在用」的輸出/輸入裝置, 之後再改
        string curOut = "", curIn = "";
        string oid = Program.GetDefaultId(en, 0), iid = Program.GetDefaultId(en, 1);
        foreach (var d in outs) if (d.Id == oid) curOut = d.Name;
        foreach (var d in ins) if (d.Id == iid) curIn = d.Name;
        ps.Add(new Program.Profile { Name = "新設定檔", Out = curOut, In = curIn, Icon = "speaker" });
        SetDirty(true);
        RefreshList(ps.Count - 1);
        txtName.Focus(); txtName.SelectAll();
    }

    void DeleteProfile()
    {
        int i = lst.SelectedIndex;
        if (i < 0) return;
        if (MessageBox.Show("刪除「" + ps[i].Name + "」?", "AudioSwitch", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        ps.RemoveAt(i);
        SetDirty(true);
        RefreshList(i);
    }

    void MoveItem(int d)
    {
        int i = lst.SelectedIndex, j = i + d;
        if (i < 0 || j < 0 || j >= ps.Count) return;
        var t = ps[i]; ps[i] = ps[j]; ps[j] = t;
        SetDirty(true);
        RefreshList(j);
    }

    bool Save()
    {
        var sb = new System.Text.StringBuilder(Program.Header);
        foreach (var p in ps)
        {
            string name = p.Name.Replace("|", " ").Trim();
            if (name.Length == 0) name = "(未命名)";
            sb.Append(name).Append(" | ").Append(p.Out.Replace("|", " ").Trim()).Append(" | ")
              .Append(p.In.Replace("|", " ").Trim()).Append(" | ").Append(p.Icon).Append("\r\n");
        }
        try { System.IO.File.WriteAllText(Program.ProfilePath, sb.ToString(), new System.Text.UTF8Encoding(true)); }
        catch (Exception ex) { MessageBox.Show("儲存失敗: " + ex.Message, "AudioSwitch"); return false; }
        SetDirty(false);
        lblApplied.Text = "已儲存";
        return true;
    }

    void ApplyNow()
    {
        var p = Cur;
        if (p == null) return;
        var t = new Program.Profile { Name = p.Name, Out = p.Out.Trim(), In = p.In.Trim(), Icon = p.Icon };
        string on, inn;
        if (Program.ApplyProfile(en, t, out on, out inn))
        {
            new OsdForm(t.Icon, t.Name, lst.SelectedIndex, ps.Count).Show();
            lblApplied.Text = "已切換";
        }
        else lblApplied.Text = "找不到這組的裝置";
    }

    void OnClosing(object s, FormClosingEventArgs e)
    {
        if (!dirty) return;
        var r = MessageBox.Show("有尚未儲存的變更，要儲存嗎?", "AudioSwitch", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
        if (r == DialogResult.Cancel || (r == DialogResult.Yes && !Save())) e.Cancel = true;
    }
}

// 右上角提示卡: 向量圖示 + 設定檔名稱, 滑入 → 停留 → 淡出 (每像素 alpha 的分層視窗)
class OsdForm : Form
{
    const int BW = 340, BH = 88, M = 18; // 卡片寬高與外圍光暈邊距 (96 DPI 下的邏輯像素)

    [StructLayout(LayoutKind.Sequential)]
    struct BLEND { public byte Op, Flags, Alpha, Format; }
    [DllImport("user32.dll", SetLastError = true)] static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, ref Point pptDst, ref Size psize, IntPtr hdcSrc, ref Point pptSrc, int crKey, ref BLEND pblend, int dwFlags);
    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr obj);

    readonly Bitmap bmp;
    readonly Point basePos;
    readonly float s;
    readonly Stopwatch sw = new Stopwatch();
    readonly Timer timer = new Timer();

    public OsdForm(string key, string text, int idx, int count)
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        using (var g = Graphics.FromHwnd(IntPtr.Zero)) s = g.DpiX / 96f;
        bmp = Render(key, text, idx, count, s);
        Size = bmp.Size;
        var area = Screen.PrimaryScreen.WorkingArea;
        basePos = new Point(area.Right - bmp.Width + (int)((M - 14) * s), area.Top + (int)((14 - M) * s));
        timer.Interval = 15;
        timer.Tick += delegate { Tick(); };
    }

    protected override bool ShowWithoutActivation { get { return true; } }
    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x08000000 | 0x80 | 0x20 | 0x80000 | 0x8; // NOACTIVATE|TOOLWINDOW|TRANSPARENT|LAYERED|TOPMOST
            return cp;
        }
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        Apply(0, 0);
        sw.Start();
        timer.Start();
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        timer.Dispose();
        bmp.Dispose();
        base.OnFormClosed(e);
    }

    void Tick()
    {
        long ms = sw.ElapsedMilliseconds;
        if (ms < 220)
        {
            float p = ms / 220f, e = 1 - (1 - p) * (1 - p) * (1 - p);
            Apply((byte)(255 * e), (int)((1 - e) * 60 * s));
        }
        else if (ms < 1720) Apply(255, 0);
        else if (ms < 2020)
        {
            float p = (ms - 1720) / 300f;
            Apply((byte)(255 * (1 - p)), (int)(p * 20 * s));
        }
        else Close();
    }

    void Apply(byte alpha, int dx)
    {
        IntPtr screen = GetDC(IntPtr.Zero), mem = CreateCompatibleDC(screen);
        IntPtr hb = bmp.GetHbitmap(Color.FromArgb(0)), old = SelectObject(mem, hb);
        var size = bmp.Size; var src = new Point(0, 0); var dst = new Point(basePos.X + dx, basePos.Y);
        var bf = new BLEND { Op = 0, Flags = 0, Alpha = alpha, Format = 1 }; // AC_SRC_OVER, AC_SRC_ALPHA
        UpdateLayeredWindow(Handle, screen, ref dst, ref size, mem, ref src, 0, ref bf, 2);
        SelectObject(mem, old); DeleteObject(hb); DeleteDC(mem); ReleaseDC(IntPtr.Zero, screen);
    }

    internal static Color Accent(string key)
    {
        switch (key)
        {
            case "headset": return Color.FromArgb(0, 200, 255);
            case "usbmic":  return Color.FromArgb(190, 100, 255);
            case "amp":     return Color.FromArgb(255, 170, 40);
            case "mic":     return Color.FromArgb(255, 90, 130);
            default:        return Color.FromArgb(60, 220, 140);
        }
    }

    static GraphicsPath Round(RectangleF r, float rad)
    {
        var p = new GraphicsPath(); float d = rad * 2;
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    internal static Bitmap Render(string key, string text, int idx, int count, float s)
    {
        var bmp = new Bitmap((int)Math.Ceiling((BW + 2 * M) * s), (int)Math.Ceiling((BH + 2 * M) * s), PixelFormat.Format32bppPArgb);
        Color ac = Accent(key);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAlias;
            g.ScaleTransform(s, s);
            var card = new RectangleF(M, M, BW, BH);

            for (int i = 1; i <= 8; i++) // 外圍光暈
                using (var path = Round(RectangleF.Inflate(card, i * 1.6f, i * 1.6f), 22 + i * 1.6f))
                using (var pen = new Pen(Color.FromArgb((int)(36 * (1 - i / 9f)), ac), 2.4f))
                    g.DrawPath(pen, path);

            using (var path = Round(card, 22))
            {
                using (var b = new LinearGradientBrush(card, Color.FromArgb(242, 14, 16, 24), Color.FromArgb(242, 36, 40, 58), 45f)) g.FillPath(b, path);
                using (var b = new LinearGradientBrush(card, Color.FromArgb(70, ac), Color.FromArgb(0, ac), 0f)) g.FillPath(b, path);
                using (var pen = new Pen(Color.FromArgb(170, ac), 1.5f)) g.DrawPath(pen, path);
            }

            float cx = M + 54, cy = M + BH / 2f;
            using (var b = new SolidBrush(Color.FromArgb(40, ac))) g.FillEllipse(b, cx - 32, cy - 32, 64, 64);
            using (var pen = new Pen(Color.FromArgb(200, ac), 2f)) g.DrawEllipse(pen, cx - 32, cy - 32, 64, 64);
            DrawIcon(g, key, cx, cy, ac);

            using (var f = new Font("Microsoft JhengHei UI", 20, FontStyle.Bold, GraphicsUnit.Pixel))
            using (var sf = new StringFormat { LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap })
                g.DrawString(text, f, Brushes.White, new RectangleF(M + 100, M + 14, BW - 100 - 16, 38), sf);

            float dx = M + 100, dy = M + BH - 24;
            for (int i = 0; i < count; i++) // 目前是第幾組
            {
                bool on = i == idx;
                float w = on ? 22 : 6;
                using (var path = Round(new RectangleF(dx, dy, w, 6), 3))
                using (var b = new SolidBrush(on ? ac : Color.FromArgb(90, 255, 255, 255)))
                    g.FillPath(b, path);
                dx += w + 6;
            }
        }
        return bmp;
    }

    static void DrawIcon(Graphics g, string key, float cx, float cy, Color ac)
    {
        using (var pen = new Pen(Color.White, 2.4f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
        using (var fill = new SolidBrush(Color.FromArgb(230, ac)))
        {
            switch (key)
            {
                case "headset": Headset(g, pen, fill, cx, cy, 1f); break;
                case "amp": Amp(g, pen, fill, cx, cy); break;
                case "mic": Mic(g, pen, fill, cx, cy, 1f); break;
                case "usbmic": Speaker(g, pen, cx - 10, cy, 0.8f); Mic(g, pen, fill, cx + 12, cy, 0.8f); break;
                default: Speaker(g, pen, cx - 3, cy, 1.1f); break;
            }
        }
    }

    static void Headset(Graphics g, Pen pen, Brush fill, float cx, float cy, float sc)
    {
        var st = g.Save(); g.TranslateTransform(cx, cy); g.ScaleTransform(sc, sc);
        g.DrawArc(pen, -14, -19, 28, 28, 180, 180);
        foreach (float x in new[] { -19f, 10f })
            using (var p = Round(new RectangleF(x, -6, 9, 16), 3.5f)) { g.FillPath(fill, p); g.DrawPath(pen, p); }
        g.DrawCurve(pen, new[] { new PointF(-14, 10), new PointF(-13, 16), new PointF(-5, 17) });
        g.FillEllipse(Brushes.White, -6.5f, 14.5f, 4, 4);
        g.Restore(st);
    }

    static void Speaker(Graphics g, Pen pen, float cx, float cy, float sc)
    {
        var st = g.Save(); g.TranslateTransform(cx, cy); g.ScaleTransform(sc, sc);
        g.FillPolygon(Brushes.White, new[] { new PointF(-14, -5), new PointF(-8, -5), new PointF(-1, -12), new PointF(-1, 12), new PointF(-8, 5), new PointF(-14, 5) });
        g.DrawArc(pen, 0, -7, 12, 14, -55, 110);
        g.DrawArc(pen, -3, -13, 22, 26, -55, 110);
        g.Restore(st);
    }

    static void Mic(Graphics g, Pen pen, Brush fill, float cx, float cy, float sc)
    {
        var st = g.Save(); g.TranslateTransform(cx, cy); g.ScaleTransform(sc, sc);
        using (var p = Round(new RectangleF(-6, -17, 12, 20), 6)) { g.FillPath(fill, p); g.DrawPath(pen, p); }
        g.DrawArc(pen, -11, -9, 22, 20, 0, 180);
        g.DrawLine(pen, 0, 11, 0, 17);
        g.DrawLine(pen, -6, 17, 6, 17);
        g.Restore(st);
    }

    static void Amp(Graphics g, Pen pen, Brush fill, float cx, float cy)
    {
        var st = g.Save(); g.TranslateTransform(cx, cy);
        using (var p = Round(new RectangleF(-18, -10, 36, 21), 4)) g.DrawPath(pen, p);
        g.FillRectangle(fill, -14, -6, 12, 5);
        g.DrawEllipse(pen, 2f, -7f, 6, 6);
        g.DrawEllipse(pen, 10f, -7f, 6, 6);
        g.DrawLine(pen, -14, 5, 14, 5);
        g.DrawLine(pen, -12, 11, -12, 15);
        g.DrawLine(pen, 12, 11, 12, 15);
        g.Restore(st);
    }
}

static class Program
{
    [DllImport("user32.dll")] static extern bool SetProcessDPIAware();

    static readonly PropertyKey FriendlyName =
        new PropertyKey { fmtid = new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"), pid = 14 };

    internal static string GetDefaultId(IMMDeviceEnumerator en, int flow)
    {
        try { IMMDevice cur; en.GetDefaultAudioEndpoint(flow, 0, out cur); string id; cur.GetId(out id); return id; }
        catch { return null; }
    }

    internal static void SetDefault(string id)
    {
        var pc = (IPolicyConfig)new PolicyConfigClient();
        for (int role = 0; role < 3; role++) // console / multimedia / communications
            pc.SetDefaultEndpoint(id, role);
    }

    static string GetName(IMMDevice d)
    {
        IPropertyStore ps;
        d.OpenPropertyStore(0, out ps);
        var key = FriendlyName;
        PropVariant v;
        ps.GetValue(ref key, out v);
        return Marshal.PtrToStringUni(v.p) ?? "(unknown)";
    }

    internal static List<Dev> GetDevices(IMMDeviceEnumerator en, int flow)
    {
        IMMDeviceCollection col;
        en.EnumAudioEndpoints(flow, 1 /* ACTIVE */, out col);
        uint n; col.GetCount(out n);
        var list = new List<Dev>();
        for (uint i = 0; i < n; i++)
        {
            IMMDevice d; col.Item(i, out d);
            string id; d.GetId(out id);
            list.Add(new Dev { Id = id, Name = GetName(d) });
        }
        return list;
    }

    // ---- 設定檔: 一組「輸出 + 輸入」 ----
    internal class Profile { public string Name, Out, In, Icon; }

    // 沒指定圖示時, 從設定檔名稱猜
    static string GuessIcon(string name)
    {
        string n = name.ToLowerInvariant();
        if (n.Contains("耳機") || n.Contains("headset") || n.Contains("arctis")) return "headset";
        if (n.Contains("sony") || n.Contains("擴大機") || n.Contains("amp")) return "amp";
        if (n.Contains("usb")) return "usbmic";
        return "speaker";
    }

    // 設定檔放在 %APPDATA%\AudioSwitch (安裝在 Program Files 時程式資料夾不可寫)
    internal static string ProfilePath
    {
        get
        {
            string dir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AudioSwitch");
            System.IO.Directory.CreateDirectory(dir);
            return System.IO.Path.Combine(dir, "profiles.txt");
        }
    }

    internal static string ExeDir { get { return System.IO.Path.GetDirectoryName(Application.ExecutablePath); } }

    internal const string Header =
        "# AudioSwitch 設定檔 (建議用 AudioSwitch-Editor.exe 編輯)\r\n" +
        "# 格式:  名稱 | 輸出裝置關鍵字 | 輸入裝置關鍵字 | 圖示\r\n" +
        "# 關鍵字只要是裝置名稱的一部分即可 (不分大小寫); 留空代表不更動。\r\n" +
        "# 圖示: headset / speaker / mic / usbmic / amp\r\n" +
        "# 切換時依下列順序輪流; 找不到裝置的設定檔會自動跳過。\r\n";

    internal static List<Profile> LoadProfiles()
    {
        if (!System.IO.File.Exists(ProfilePath))
        {
            // 舊版把 profiles.txt 放在程式旁邊, 有的話搬過來; 否則建立空白設定檔
            string legacy = System.IO.Path.Combine(ExeDir, "profiles.txt");
            if (System.IO.File.Exists(legacy)) System.IO.File.Copy(legacy, ProfilePath);
            else System.IO.File.WriteAllText(ProfilePath, Header, new System.Text.UTF8Encoding(true));
        }
        var list = new List<Profile>();
        foreach (var raw in System.IO.File.ReadAllLines(ProfilePath))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("#")) continue;
            var parts = line.Split('|');
            if (parts.Length < 3) continue;
            string name = parts[0].Trim();
            string icon = parts.Length > 3 && parts[3].Trim().Length > 0 ? parts[3].Trim().ToLowerInvariant() : GuessIcon(name);
            list.Add(new Profile { Name = name, Out = parts[1].Trim(), In = parts[2].Trim(), Icon = icon });
        }
        return list;
    }

    internal static Dev Find(List<Dev> devs, string kw)
    {
        foreach (var d in devs)
            if (d.Name.IndexOf(kw, StringComparison.OrdinalIgnoreCase) >= 0) return d;
        return null;
    }

    // 目前預設裝置符合哪個設定檔; 都不符合回傳 -1
    internal static int CurrentProfile(IMMDeviceEnumerator en, List<Profile> ps)
    {
        string o = GetDefaultId(en, 0), i = GetDefaultId(en, 1);
        var outs = GetDevices(en, 0); var ins = GetDevices(en, 1);
        for (int k = 0; k < ps.Count; k++)
        {
            var po = ps[k].Out.Length > 0 ? Find(outs, ps[k].Out) : null;
            var pi = ps[k].In.Length > 0 ? Find(ins, ps[k].In) : null;
            bool okO = ps[k].Out.Length == 0 || (po != null && po.Id == o);
            bool okI = ps[k].In.Length == 0 || (pi != null && pi.Id == i);
            if (okO && okI) return k;
        }
        return -1;
    }

    // 套用設定檔; 輸出/輸入裝置有任何一個找不到就不動並回傳 false
    internal static bool ApplyProfile(IMMDeviceEnumerator en, Profile p, out string outName, out string inName)
    {
        outName = inName = null;
        Dev o = null, i = null;
        if (p.Out.Length > 0 && (o = Find(GetDevices(en, 0), p.Out)) == null) return false;
        if (p.In.Length > 0 && (i = Find(GetDevices(en, 1), p.In)) == null) return false;
        if (o != null) { SetDefault(o.Id); outName = o.Name; }
        if (i != null) { SetDefault(i.Id); inName = i.Name; }
        return true;
    }

    [STAThread]
    static int Main(string[] args)
    {
        SetProcessDPIAware(); // 高 DPI 螢幕上提示卡才不會被放大變模糊
        if (args.Length == 0)
        {
            // 沒給參數時, 依執行檔檔名決定模式 (給只能選檔案、不能帶參數的啟動器用)
            string exe = System.IO.Path.GetFileNameWithoutExtension(Application.ExecutablePath).ToLowerInvariant();
            if (exe.EndsWith("-tray"))
            {
                bool first;
                using (new System.Threading.Mutex(true, "AudioSwitchTraySingleton", out first))
                {
                    if (!first) return 0; // 已經在執行了
                    Application.EnableVisualStyles();
                    Application.Run(new TrayApp());
                }
                return 0;
            }
            if (exe.EndsWith("-editor"))
            {
                Application.EnableVisualStyles();
                Application.Run(new EditorForm());
                return 0;
            }
            if (exe.EndsWith("-profile")) args = new[] { "profile" };
            else if (exe.EndsWith("-out")) args = new[] { "out" };
            else if (exe.EndsWith("-in")) args = new[] { "in" };
            else { Msg("用法: AudioSwitch out|in|list [名稱關鍵字...]"); return 1; }
        }
        string mode = args[0].ToLowerInvariant();
        var en = (IMMDeviceEnumerator)new MMDeviceEnumerator();

        if (mode == "list")
        {
            string s = "【播放】\n";
            foreach (var d in GetDevices(en, 0)) s += "  " + d.Name + "\n";
            s += "【錄音】\n";
            foreach (var d in GetDevices(en, 1)) s += "  " + d.Name + "\n";
            Msg(s);
            return 0;
        }
        if (mode == "icon" && args.Length >= 2) // 輸出程式圖示 .ico (build 時用)
        {
            using (var fs = System.IO.File.Create(args[1])) TrayApp.MakeIcon().Save(fs);
            return 0;
        }
        if (mode == "render" && args.Length >= 6) // 把提示卡存成透明 PNG (做文件截圖用): render <圖示> <文字> <第幾組> <共幾組> <輸出路徑>
        {
            using (var b = OsdForm.Render(args[1], args[2], int.Parse(args[3]), int.Parse(args[4]), 1f)) b.Save(args[5], ImageFormat.Png);
            return 0;
        }
        if (mode == "osd" && args.Length >= 5) // 預覽提示卡, 不切換裝置: osd <圖示> <文字> <第幾組> <共幾組>
        {
            Application.EnableVisualStyles();
            Application.Run(new OsdForm(args[1], args[2], int.Parse(args[3]), int.Parse(args[4])));
            return 0;
        }
        if (mode == "profile")
        {
            var ps = LoadProfiles();
            if (ps.Count == 0) // 還沒建立任何設定檔: 直接開編輯器
            {
                string editor = System.IO.Path.Combine(ExeDir, "AudioSwitch-Editor.exe");
                if (System.IO.File.Exists(editor)) System.Diagnostics.Process.Start(editor);
                else Msg("還沒有設定檔: " + ProfilePath);
                return 1;
            }
            int cur = CurrentProfile(en, ps);
            for (int k = 1; k <= ps.Count; k++)
            {
                int pi = (cur + k) % ps.Count;
                var p = ps[pi];
                string on, inn;
                if (!ApplyProfile(en, p, out on, out inn)) continue; // 裝置不在就跳過
                Application.EnableVisualStyles();
                Application.Run(new OsdForm(p.Icon, p.Name, pi, ps.Count));
                return 0;
            }
            Msg("所有設定檔的裝置都找不到，請檢查 profiles.txt");
            return 2;
        }
        if (mode != "out" && mode != "in") { Msg("第一個參數必須是 out、in、profile 或 list"); return 1; }

        int flow = mode == "out" ? 0 : 1;
        var all = GetDevices(en, flow);

        // 有給關鍵字: 依關鍵字順序挑出符合的裝置
        var cands = new List<Dev>();
        if (args.Length > 1)
        {
            for (int i = 1; i < args.Length; i++)
                foreach (var d in all)
                    if (d.Name.IndexOf(args[i], StringComparison.OrdinalIgnoreCase) >= 0 && !cands.Contains(d))
                        cands.Add(d);
        }
        else cands = all;

        if (cands.Count == 0) { Msg("找不到符合的裝置，請用 list 查看名稱"); return 2; }

        string curId = null;
        try { IMMDevice cur; en.GetDefaultAudioEndpoint(flow, 0, out cur); cur.GetId(out curId); } catch { }

        int idx = cands.FindIndex(d => d.Id == curId);
        var next = cands[(idx + 1) % cands.Count];

        var pc = (IPolicyConfig)new PolicyConfigClient();
        for (int role = 0; role < 3; role++) // console / multimedia / communications
            pc.SetDefaultEndpoint(next.Id, role);

        Application.EnableVisualStyles();
        Application.Run(new OsdForm(mode == "out" ? "speaker" : "mic", next.Name, -1, 0));
        return 0;
    }

    static void Msg(string s)
    {
        MessageBox.Show(s, "AudioSwitch");
    }
}
