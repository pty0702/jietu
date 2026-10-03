// QuickSnap.cs — 极简截图工具
//   · 常驻任务栏托盘，双击托盘图标或按 Alt+A 开始截图
//   · 鼠标拖拽框选区域，松开即复制到剪贴板
//   · Esc 或右键取消；托盘菜单可将上次截图另存为文件，或开启“自动保存”
//
// 编译（无需安装任何东西，用的是 Windows 自带的 .NET Framework 编译器）：
//   build.bat
//   或: csc -codepage:65001 -target:winexe -out:QuickSnap.exe QuickSnap.cs
//       -r:System.dll -r:System.Drawing.dll -r:System.Windows.Forms.dll
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace QuickSnap
{
    internal static class Program
    {
        [DllImport("user32.dll")]
        private static extern bool SetProcessDPIAware();

        [STAThread]
        private static void Main()
        {
            bool createdNew;
            using (Mutex mutex = new Mutex(true, "QuickSnap_SingleInstance", out createdNew))
            {
                if (!createdNew)
                {
                    MessageBox.Show("QuickSnap 已在运行，请查看任务栏右下角托盘图标。", "QuickSnap");
                    return;
                }
                SetProcessDPIAware();
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new TrayContext());
                GC.KeepAlive(mutex);
            }
        }
    }

    /// <summary>不可见的消息窗口，用来接收全局热键 WM_HOTKEY。</summary>
    internal class HotkeyWindow : NativeWindow
    {
        public const int WM_HOTKEY = 0x0312;
        private readonly TrayContext owner;

        public HotkeyWindow(TrayContext owner)
        {
            this.owner = owner;
            CreateParams cp = new CreateParams();
            cp.Caption = "QuickSnapHotkeyWindow";
            CreateHandle(cp);
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_HOTKEY) owner.OnHotkey();
            base.WndProc(ref m);
        }
    }

    internal class TrayContext : ApplicationContext
    {
        private const int HOTKEY_ID = 0xB00F;
        private const uint MOD_ALT = 0x0001;

        [DllImport("user32.dll")]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
        [DllImport("user32.dll")]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        private readonly HotkeyWindow hotkeyWindow;
        private readonly NotifyIcon trayIcon;
        private readonly MenuItem saveMenuItem;
        private MenuItem autoSaveMenuItem;
        private Bitmap lastShot;
        private CaptureOverlay activeOverlay;
        private bool autoSave;
        private bool balloonSaves;

        public TrayContext()
        {
            hotkeyWindow = new HotkeyWindow(this);

            ContextMenu menu = new ContextMenu();
            menu.MenuItems.Add("区域截图  (Alt+A)", delegate { StartCapture(); });

            saveMenuItem = new MenuItem("保存上次截图…");
            saveMenuItem.Click += delegate { SaveLastShot(); };
            saveMenuItem.Enabled = false;
            menu.MenuItems.Add(saveMenuItem);

            autoSaveMenuItem = new MenuItem("截图后自动保存到“图片\\ScreenShots”");
            autoSaveMenuItem.Click += delegate
            {
                autoSave = !autoSave;
                autoSaveMenuItem.Checked = autoSave;
            };
            menu.MenuItems.Add(autoSaveMenuItem);

            menu.MenuItems.Add("-");
            menu.MenuItems.Add("退出", delegate { ExitApp(); });

            trayIcon = new NotifyIcon();
            trayIcon.Icon = MakeIcon();
            trayIcon.Text = "QuickSnap 截图 (Alt+A)";
            trayIcon.ContextMenu = menu;
            trayIcon.Visible = true;
            trayIcon.DoubleClick += delegate { StartCapture(); };
            trayIcon.BalloonTipClicked += delegate
            {
                if (balloonSaves) SaveLastShot();
                balloonSaves = false;
            };

            if (!RegisterHotKey(hotkeyWindow.Handle, HOTKEY_ID, MOD_ALT, (uint)'A'))
            {
                trayIcon.ShowBalloonTip(4000, "QuickSnap",
                    "Alt+A 热键注册失败（可能被其他软件占用），仍可通过托盘菜单截图。",
                    ToolTipIcon.Warning);
            }
        }

        public void OnHotkey()
        {
            StartCapture();
        }

        private void StartCapture()
        {
            if (activeOverlay != null) return; // 正在截图中

            Rectangle vs = SystemInformation.VirtualScreen;
            Bitmap frozen;
            try
            {
                frozen = new Bitmap(vs.Width, vs.Height, PixelFormat.Format32bppPArgb);
                using (Graphics g = Graphics.FromImage(frozen))
                {
                    g.CopyFromScreen(vs.X, vs.Y, 0, 0, vs.Size);
                }
            }
            catch (Exception ex)
            {
                trayIcon.ShowBalloonTip(3000, "QuickSnap", "抓屏失败：" + ex.Message, ToolTipIcon.Error);
                return;
            }

            CaptureOverlay overlay = new CaptureOverlay(frozen, OnCaptured, null);
            overlay.FormClosed += delegate { if (activeOverlay == overlay) activeOverlay = null; };
            activeOverlay = overlay;
            overlay.Show();
            overlay.Activate();
        }

        private void OnCaptured(Bitmap shot)
        {
            if (lastShot != null) lastShot.Dispose();
            lastShot = shot;
            saveMenuItem.Enabled = true;

            CopyToClipboard(shot);

            if (autoSave)
            {
                string dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "ScreenShots");
                try
                {
                    Directory.CreateDirectory(dir);
                    string path = Path.Combine(dir,
                        "截图_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".png");
                    shot.Save(path, ImageFormat.Png);
                    balloonSaves = false;
                    trayIcon.ShowBalloonTip(2000, "QuickSnap",
                        string.Format("已复制到剪贴板并保存：{0}", path), ToolTipIcon.Info);
                    return;
                }
                catch (Exception ex)
                {
                    trayIcon.ShowBalloonTip(3000, "QuickSnap", "自动保存失败：" + ex.Message, ToolTipIcon.Warning);
                }
            }

            balloonSaves = true;
            trayIcon.ShowBalloonTip(2000, "QuickSnap",
                string.Format("已复制到剪贴板 ({0} × {1})。点击此气泡可另存为文件。",
                    shot.Width, shot.Height), ToolTipIcon.Info);
        }

        private static void CopyToClipboard(Bitmap bmp)
        {
            // 同时放 DIB（Bitmap）和 PNG 两种格式，兼容老程序和新的办公软件
            DataObject data = new DataObject();
            data.SetImage(bmp);
            MemoryStream png = new MemoryStream();
            bmp.Save(png, ImageFormat.Png);
            data.SetData("PNG", false, png);

            for (int i = 0; i < 10; i++)
            {
                try
                {
                    Clipboard.SetDataObject(data, true);
                    return;
                }
                catch (ExternalException)
                {
                    Thread.Sleep(60); // 剪贴板被其他程序短暂占用，稍等重试
                }
            }
        }

        private void SaveLastShot()
        {
            if (lastShot == null) return;
            using (SaveFileDialog dlg = new SaveFileDialog())
            {
                dlg.Title = "保存截图";
                dlg.Filter = "PNG 图片 (*.png)|*.png|JPEG 图片 (*.jpg)|*.jpg|位图 (*.bmp)|*.bmp";
                dlg.FileName = "截图_" + DateTime.Now.ToString("yyyyMMdd_HHmmss");
                dlg.DefaultExt = "png";
                dlg.AddExtension = true;
                dlg.RestoreDirectory = true;
                dlg.InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
                if (dlg.ShowDialog() == DialogResult.OK)
                {
                    ImageFormat fmt = ImageFormat.Png;
                    string ext = Path.GetExtension(dlg.FileName).ToLowerInvariant();
                    if (ext == ".jpg" || ext == ".jpeg") fmt = ImageFormat.Jpeg;
                    else if (ext == ".bmp") fmt = ImageFormat.Bmp;
                    lastShot.Save(dlg.FileName, fmt);
                    balloonSaves = false;
                    trayIcon.ShowBalloonTip(2000, "QuickSnap", "已保存：" + dlg.FileName, ToolTipIcon.Info);
                }
            }
        }

        private void ExitApp()
        {
            UnregisterHotKey(hotkeyWindow.Handle, HOTKEY_ID);
            trayIcon.Visible = false;
            trayIcon.Dispose();
            if (lastShot != null) lastShot.Dispose();
            Application.Exit();
        }

        private static Icon MakeIcon()
        {
            using (Bitmap bmp = new Bitmap(32, 32))
            {
                using (Graphics g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    using (Pen p = new Pen(Color.FromArgb(0, 120, 215), 3f))
                    {
                        g.DrawRectangle(p, 3, 7, 26, 20);   // 机身
                        g.DrawEllipse(p, 11, 12, 10, 10);   // 镜头
                        g.DrawRectangle(p, 11, 3, 10, 4);   // 顶部取景器
                    }
                }
                return Icon.FromHandle(bmp.GetHicon());
            }
        }
    }

    /// <summary>全屏半透明遮罩 + 拖拽框选。坐标与虚拟桌面 1:1 对齐。</summary>
    internal class CaptureOverlay : Form
    {
        private readonly Bitmap frozen;              // 打开遮罩前抓好的全屏画面
        private readonly Action<Bitmap> onCaptured;
        private readonly Action onCancelled;

        private readonly Pen borderPen = new Pen(Color.FromArgb(0, 120, 215), 1.6f);
        private readonly Brush dimBrush = new SolidBrush(Color.FromArgb(90, 0, 0, 0));
        private readonly Brush infoBack = new SolidBrush(Color.FromArgb(200, 10, 10, 10));
        private readonly Font infoFont = new Font("Microsoft YaHei UI", 9f);

        private Point startPos;
        private Point curPos;
        private bool dragging;

        public CaptureOverlay(Bitmap frozenImage, Action<Bitmap> captured, Action cancelled)
        {
            frozen = frozenImage;
            onCaptured = captured;
            onCancelled = cancelled;

            Rectangle vs = SystemInformation.VirtualScreen;
            StartPosition = FormStartPosition.Manual;
            Location = vs.Location;
            Size = vs.Size;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            Cursor = Cursors.Cross;
            KeyPreview = true;
            AutoScaleMode = AutoScaleMode.None;
            BackColor = Color.Black;
            DoubleBuffered = true;
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == Keys.Escape) { Cancel(); return true; }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Right) { Cancel(); return; }
            if (e.Button == MouseButtons.Left)
            {
                dragging = true;
                startPos = e.Location;
                curPos = e.Location;
            }
            base.OnMouseDown(e);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (dragging) { curPos = e.Location; Invalidate(); }
            base.OnMouseMove(e);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left && dragging)
            {
                dragging = false;
                Rectangle r = GetSelection();
                if (r.Width < 4 || r.Height < 4) { Invalidate(); return; } // 太小视为误触
                Finish(r);
                return;
            }
            base.OnMouseUp(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics g = e.Graphics;
            g.InterpolationMode = InterpolationMode.NearestNeighbor;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            g.DrawImage(frozen, 0, 0, frozen.Width, frozen.Height);
            g.FillRectangle(dimBrush, ClientRectangle);

            if (!dragging)
            {
                DrawTip(g, "QuickSnap：按住鼠标左键拖拽框选区域   |   Esc / 右键 取消");
                return;
            }

            Rectangle r = GetSelection();
            if (r.Width > 0 && r.Height > 0)
            {
                g.DrawImage(frozen, r, r, GraphicsUnit.Pixel);   // 选区内还原为未遮罩画面
                g.DrawRectangle(borderPen, r);

                string label = r.Width + " × " + r.Height;
                SizeF ts = g.MeasureString(label, infoFont);
                int lx = r.X;
                int ly = r.Y - (int)ts.Height - 10;
                if (ly < 2) ly = r.Y + r.Height + 10;
                if (lx + ts.Width + 12 > ClientSize.Width) lx = ClientSize.Width - (int)ts.Width - 12;
                if (lx < 0) lx = 0;
                RectangleF back = new RectangleF(lx, ly, ts.Width + 10, ts.Height + 4);
                g.FillRectangle(infoBack, back);
                g.DrawString(label, infoFont, Brushes.White, lx + 5, ly + 2);
            }
        }

        private void DrawTip(Graphics g, string text)
        {
            SizeF ts = g.MeasureString(text, infoFont);
            int w = (int)ts.Width + 24;
            int h = (int)ts.Height + 10;
            int x = (ClientSize.Width - w) / 2;
            int y = 14;
            g.FillRectangle(infoBack, new Rectangle(x, y, w, h));
            g.DrawString(text, infoFont, Brushes.White, x + 12, y + 5);
        }

        private Rectangle GetSelection()
        {
            return new Rectangle(
                Math.Min(startPos.X, curPos.X),
                Math.Min(startPos.Y, curPos.Y),
                Math.Abs(startPos.X - curPos.X),
                Math.Abs(startPos.Y - curPos.Y));
        }

        private void Finish(Rectangle r)
        {
            r.Intersect(new Rectangle(0, 0, frozen.Width, frozen.Height));
            Bitmap shot;
            try
            {
                shot = frozen.Clone(r, frozen.PixelFormat);
            }
            catch
            {
                Cancel();
                return;
            }
            Close();
            if (onCaptured != null) onCaptured(shot);
        }

        private void Cancel()
        {
            Close();
            if (onCancelled != null) onCancelled();
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            base.OnFormClosed(e);
            frozen.Dispose();
            borderPen.Dispose();
            dimBrush.Dispose();
            infoBack.Dispose();
            infoFont.Dispose();
        }
    }
}
