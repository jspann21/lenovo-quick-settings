using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace LenovoQuickSettings
{
    internal enum LenovoPowerMode
    {
        Auto,
        BatterySaver,
        Performance
    }

    internal sealed class LenovoState
    {
        public LenovoPowerMode PowerMode { get; set; }
        public bool ConservationMode { get; set; }
        public bool ZeroTouchLogin { get; set; }
        public bool ZeroTouchLock { get; set; }
    }

    internal sealed class LenovoController
    {
        private readonly object syncRoot = new object();
        private readonly string addinDirectory;
        private readonly string presenceAddinDirectory;
        private readonly Assembly powerContract;
        private readonly Assembly batteryContract;
        private readonly object powerAgent;
        private readonly object batteryAgent;
        private readonly object presenceAgent;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool SetDllDirectory(string lpPathName);

        public LenovoController()
        {
            addinDirectory = FindAddin(
                "IdeaNotebookAddin",
                new[]
                {
                    "PowerContract.dll",
                    "BatteryManagementContract.dll",
                    "IdeaPowerAgent.dll",
                    "IdeaBatteryAgent.dll",
                    "PowerBattery.dll"
                }
            );
            presenceAddinDirectory = FindAddin(
                "SmartInteractAddin",
                new[] { "Lenovo.Vantage.SmartSenseRpcClient.dll", "SmartSenseRpcClient.dll" }
            );
            AppDomain.CurrentDomain.AssemblyResolve += ResolveVendorAssembly;

            UseVendorDirectory(addinDirectory);

            powerContract = Assembly.LoadFrom(Path.Combine(addinDirectory, "PowerContract.dll"));
            batteryContract = Assembly.LoadFrom(Path.Combine(addinDirectory, "BatteryManagementContract.dll"));

            Assembly powerAgentAssembly = Assembly.LoadFrom(Path.Combine(addinDirectory, "IdeaPowerAgent.dll"));
            Assembly batteryAgentAssembly = Assembly.LoadFrom(Path.Combine(addinDirectory, "IdeaBatteryAgent.dll"));

            powerAgent = GetSingleton(powerAgentAssembly, "IdeaNotebookAddin.PowerAgent");
            batteryAgent = GetSingleton(batteryAgentAssembly, "IdeaNotebookAddin.BatteryAgent");

            UseVendorDirectory(presenceAddinDirectory);
            Assembly presenceAssembly = Assembly.LoadFrom(
                Path.Combine(presenceAddinDirectory, "Lenovo.Vantage.SmartSenseRpcClient.dll")
            );
            Type presenceType = presenceAssembly.GetType("SmartSenseHsaRpcClient.HumanPresenceDetection", true);
            presenceAgent = Activator.CreateInstance(presenceType);

            long capabilities = Convert.ToInt64(GetProperty(presenceAgent, "Capability"));
            const long requiredCapabilities = 2L | 16L;
            if ((capabilities & requiredCapabilities) != requiredCapabilities)
            {
                throw new NotSupportedException("This Lenovo presence sensor does not expose both approach and leave detection.");
            }
        }

        public string AddinDirectory
        {
            get { return addinDirectory; }
        }

        public LenovoState GetState()
        {
            lock (syncRoot)
            {
                UseVendorDirectory(addinDirectory);
                LenovoPowerMode powerMode = ReadPowerMode();
                bool conservationMode = ReadConservationMode();

                UseVendorDirectory(presenceAddinDirectory);
                object presence = ReadPresenceSettings();
                return new LenovoState
                {
                    PowerMode = powerMode,
                    ConservationMode = conservationMode,
                    ZeroTouchLogin = GetBooleanField(presence, "ApproachEnabled"),
                    ZeroTouchLock = GetBooleanField(presence, "PresenceLeaveEnabled")
                };
            }
        }

        public void SetPowerMode(LenovoPowerMode mode)
        {
            lock (syncRoot)
            {
                UseVendorDirectory(addinDirectory);
                string vendorMode = ToVendorPowerMode(mode);
                Type requestType = powerContract.GetType("Lenovo.Modern.Contracts.Power.PowerSettingsRequest", true);
                Type enumType = powerContract.GetType("Lenovo.Modern.Contracts.Power.ItsModeType", true);
                object request = Activator.CreateInstance(requestType);
                requestType.GetProperty("ItsMode").SetValue(request, Enum.Parse(enumType, vendorMode), null);

                object response = Invoke(powerAgent, "SetITSMode", request);
                int errorCode = GetNullableInt(response, "ErrorCode");
                if (errorCode != 0)
                {
                    throw new InvalidOperationException("Lenovo returned error code " + errorCode + " while setting the power mode.");
                }

                Verify(
                    delegate { return ReadPowerMode() == mode; },
                    "Lenovo accepted the power-mode request, but the driver did not report the new mode."
                );
            }
        }

        public void SetConservationMode(bool enabled)
        {
            lock (syncRoot)
            {
                UseVendorDirectory(addinDirectory);
                Type requestType = batteryContract.GetType("Lenovo.Modern.Contracts.BatteryManagement.BatteryMgmtRequest", true);
                Type enumType = batteryContract.GetType("Lenovo.Modern.Contracts.BatteryManagement.BatteryChargeModeType", true);
                object request = Activator.CreateInstance(requestType);
                string vendorMode = enabled ? "Storage" : "Normal";
                requestType.GetProperty("BatteryChargeMode").SetValue(request, Enum.Parse(enumType, vendorMode), null);

                Invoke(batteryAgent, "SetBatteryChargeMode", request);
                Verify(
                    delegate { return ReadConservationMode() == enabled; },
                    "Lenovo accepted the charging request, but the battery controller did not report the new mode."
                );
            }
        }

        public void SetZeroTouchLogin(bool enabled)
        {
            SetPresenceSetting(
                "SetApproachEnabled",
                "ApproachEnabled",
                enabled,
                "Lenovo accepted the zero-touch login request, but the presence sensor did not report the new state."
            );
        }

        public void SetZeroTouchLock(bool enabled)
        {
            SetPresenceSetting(
                "SetPresenceLeaveEnabled",
                "PresenceLeaveEnabled",
                enabled,
                "Lenovo accepted the zero-touch lock request, but the presence sensor did not report the new state."
            );
        }

        private void SetPresenceSetting(string methodName, string fieldName, bool enabled, string failureMessage)
        {
            lock (syncRoot)
            {
                UseVendorDirectory(presenceAddinDirectory);
                Invoke(presenceAgent, methodName, enabled);
                Verify(delegate { return ReadPresenceBoolean(fieldName) == enabled; }, failureMessage);
            }
        }

        private LenovoPowerMode ReadPowerMode()
        {
            Type requestType = powerContract.GetType("Lenovo.Modern.Contracts.Power.PowerSettingsRequest", true);
            object response = Invoke(powerAgent, "GetITSMode", Activator.CreateInstance(requestType));
            int errorCode = GetNullableInt(response, "ErrorCode");
            if (errorCode != 0)
            {
                throw new InvalidOperationException("Lenovo returned error code " + errorCode + " while reading the power mode.");
            }

            object value = GetProperty(response, "ItsMode");
            if (value == null)
            {
                throw new InvalidOperationException("The Lenovo ITS driver did not return a power mode.");
            }

            string name = value.ToString();
            if (name == "ItsAuto") return LenovoPowerMode.Auto;
            if (name == "MmcCool") return LenovoPowerMode.BatterySaver;
            if (name == "MmcPerformance") return LenovoPowerMode.Performance;
            throw new InvalidOperationException("Unsupported Lenovo power mode: " + name);
        }

        private bool ReadConservationMode()
        {
            object response = Invoke(batteryAgent, "GetBatteryChargeMode");
            object value = GetProperty(response, "BatteryChargeMode");
            if (value == null)
            {
                throw new InvalidOperationException("The Lenovo battery controller did not return a charging mode.");
            }

            string name = value.ToString();
            if (name == "Storage") return true;
            if (name == "Normal" || name == "Quick") return false;
            throw new InvalidOperationException("Unsupported Lenovo charging mode: " + name);
        }

        private object ReadPresenceSettings()
        {
            object settings = Invoke(presenceAgent, "GetAllSetting");
            if (settings == null || !GetBooleanField(settings, "Capacity"))
            {
                throw new InvalidOperationException("The Lenovo human-presence sensor did not return a usable state.");
            }
            return settings;
        }

        private bool ReadPresenceBoolean(string fieldName)
        {
            return GetBooleanField(ReadPresenceSettings(), fieldName);
        }

        private static string ToVendorPowerMode(LenovoPowerMode mode)
        {
            if (mode == LenovoPowerMode.Auto) return "ItsAuto";
            if (mode == LenovoPowerMode.BatterySaver) return "MmcCool";
            if (mode == LenovoPowerMode.Performance) return "MmcPerformance";
            throw new ArgumentOutOfRangeException("mode");
        }

        private static void Verify(Func<bool> predicate, string message)
        {
            for (int attempt = 0; attempt < 12; attempt++)
            {
                Thread.Sleep(100);
                if (predicate()) return;
            }
            throw new InvalidOperationException(message);
        }

        private static int GetNullableInt(object target, string propertyName)
        {
            object value = GetProperty(target, propertyName);
            return value == null ? 0 : Convert.ToInt32(value);
        }

        private static object GetProperty(object target, string propertyName)
        {
            return target.GetType().GetProperty(propertyName).GetValue(target, null);
        }

        private static bool GetBooleanField(object target, string fieldName)
        {
            FieldInfo field = target.GetType().GetField(fieldName, BindingFlags.Public | BindingFlags.Instance);
            object value = field == null ? null : field.GetValue(target);
            if (value == null)
            {
                throw new InvalidOperationException("The Lenovo presence sensor did not return " + fieldName + ".");
            }
            return Convert.ToBoolean(value);
        }

        private static object Invoke(object target, string methodName, params object[] arguments)
        {
            try
            {
                return target.GetType().InvokeMember(
                    methodName,
                    BindingFlags.Public | BindingFlags.Instance | BindingFlags.InvokeMethod,
                    null,
                    target,
                    arguments
                );
            }
            catch (TargetInvocationException ex)
            {
                throw ex.InnerException ?? ex;
            }
        }

        private static object GetSingleton(Assembly assembly, string typeName)
        {
            Type type = assembly.GetType(typeName, true);
            return type.GetMethod("GetInstance", BindingFlags.Public | BindingFlags.Static).Invoke(null, null);
        }

        private Assembly ResolveVendorAssembly(object sender, ResolveEventArgs args)
        {
            string name = new AssemblyName(args.Name).Name + ".dll";
            string path = Path.Combine(addinDirectory, name);
            if (File.Exists(path)) return Assembly.LoadFrom(path);
            path = Path.Combine(presenceAddinDirectory, name);
            return File.Exists(path) ? Assembly.LoadFrom(path) : null;
        }

        private static void UseVendorDirectory(string directory)
        {
            if (!SetDllDirectory(directory))
            {
                throw new InvalidOperationException("Windows could not activate Lenovo's native controller directory.");
            }
            Environment.CurrentDirectory = directory;
        }

        private static string FindAddin(string addinName, string[] required)
        {
            string root = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "Lenovo", "Vantage", "Addins", addinName
            );

            if (!Directory.Exists(root))
            {
                throw new DirectoryNotFoundException("Lenovo Vantage's " + addinName + " is not installed at " + root);
            }

            DirectoryInfo selected = new DirectoryInfo(root)
                .GetDirectories()
                .Where(delegate(DirectoryInfo directory)
                {
                    return required.All(delegate(string file) { return File.Exists(Path.Combine(directory.FullName, file)); });
                })
                .OrderByDescending(delegate(DirectoryInfo directory) { return ParseVersion(directory.Name); })
                .ThenByDescending(delegate(DirectoryInfo directory) { return directory.LastWriteTimeUtc; })
                .FirstOrDefault();

            if (selected == null)
            {
                throw new FileNotFoundException("No complete Lenovo " + addinName + " installation was found under " + root);
            }

            return selected.FullName;
        }

        private static Version ParseVersion(string text)
        {
            Version version;
            return Version.TryParse(text, out version) ? version : new Version(0, 0);
        }
    }

    internal static class StartupManager
    {
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string ValueName = "Lenovo Quick Settings";

        public static bool IsEnabled()
        {
            using (Microsoft.Win32.RegistryKey key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey, false))
            {
                return key != null && key.GetValue(ValueName) != null;
            }
        }

        public static void SetEnabled(bool enabled)
        {
            using (Microsoft.Win32.RegistryKey key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(RunKey))
            {
                if (enabled)
                {
                    key.SetValue(ValueName, "\"" + Application.ExecutablePath + "\" --tray", Microsoft.Win32.RegistryValueKind.String);
                }
                else
                {
                    key.DeleteValue(ValueName, false);
                }
            }
        }
    }

    internal sealed class ToggleSwitch : CheckBox
    {
        public Color ActiveColor { get; set; }

        public ToggleSwitch()
        {
            ActiveColor = Color.FromArgb(108, 145, 255);
            Size = new Size(42, 22);
            Cursor = Cursors.Hand;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            e.Graphics.Clear(BackColor);
            Rectangle track = new Rectangle(0, 2, Width - 1, Height - 4);
            using (GraphicsPath path = RoundedRectangle(track, track.Height / 2))
            using (SolidBrush brush = new SolidBrush(Checked ? ActiveColor : Color.FromArgb(57, 60, 67)))
            {
                e.Graphics.FillPath(brush, path);
            }

            int diameter = Height - 8;
            int x = Checked ? Width - diameter - 4 : 4;
            using (SolidBrush knob = new SolidBrush(Checked ? Color.White : Color.FromArgb(176, 180, 188)))
            {
                e.Graphics.FillEllipse(knob, x, 4, diameter, diameter);
            }
        }

        private static GraphicsPath RoundedRectangle(Rectangle rectangle, int radius)
        {
            int diameter = radius * 2;
            GraphicsPath path = new GraphicsPath();
            path.AddArc(rectangle.Left, rectangle.Top, diameter, diameter, 180, 90);
            path.AddArc(rectangle.Right - diameter, rectangle.Top, diameter, diameter, 270, 90);
            path.AddArc(rectangle.Right - diameter, rectangle.Bottom - diameter, diameter, diameter, 0, 90);
            path.AddArc(rectangle.Left, rectangle.Bottom - diameter, diameter, diameter, 90, 90);
            path.CloseFigure();
            return path;
        }
    }

    internal sealed class DarkColorTable : ProfessionalColorTable
    {
        private static readonly Color Background = Color.FromArgb(19, 21, 25);
        private static readonly Color Selected = Color.FromArgb(39, 42, 49);

        public override Color ToolStripDropDownBackground { get { return Background; } }
        public override Color ImageMarginGradientBegin { get { return Background; } }
        public override Color ImageMarginGradientMiddle { get { return Background; } }
        public override Color ImageMarginGradientEnd { get { return Background; } }
        public override Color MenuItemSelected { get { return Selected; } }
        public override Color MenuItemBorder { get { return Selected; } }
        public override Color MenuBorder { get { return Color.FromArgb(48, 51, 58); } }
        public override Color SeparatorDark { get { return Color.FromArgb(48, 51, 58); } }
        public override Color SeparatorLight { get { return Color.FromArgb(48, 51, 58); } }
    }

    internal sealed class RoundedButton : Button
    {
        private bool hovering;

        public int CornerRadius { get; set; }
        public Color OutlineColor { get; set; }
        public Color HoverFillColor { get; set; }

        public RoundedButton()
        {
            CornerRadius = 8;
            OutlineColor = Color.FromArgb(43, 46, 53);
            HoverFillColor = Color.FromArgb(29, 32, 38);
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        }

        protected override void OnMouseEnter(EventArgs e)
        {
            hovering = true;
            Invalidate();
            base.OnMouseEnter(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            hovering = false;
            Invalidate();
            base.OnMouseLeave(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            e.Graphics.Clear(Parent == null ? Color.Transparent : Parent.BackColor);
            Rectangle rectangle = new Rectangle(1, 1, Width - 3, Height - 3);
            using (GraphicsPath path = CreatePath(rectangle, CornerRadius))
            using (SolidBrush fill = new SolidBrush(hovering && Enabled ? HoverFillColor : BackColor))
            using (Pen outline = new Pen(OutlineColor, 1F))
            using (StringFormat format = new StringFormat())
            {
                e.Graphics.FillPath(fill, path);
                e.Graphics.DrawPath(outline, path);
                format.Alignment = StringAlignment.Center;
                format.LineAlignment = StringAlignment.Center;
                using (SolidBrush text = new SolidBrush(Enabled ? ForeColor : Color.FromArgb(100, 104, 112)))
                {
                    e.Graphics.DrawString(Text, Font, text, rectangle, format);
                }
            }
        }

        private static GraphicsPath CreatePath(Rectangle rectangle, int radius)
        {
            int diameter = radius * 2;
            GraphicsPath path = new GraphicsPath();
            path.AddArc(rectangle.Left, rectangle.Top, diameter, diameter, 180, 90);
            path.AddArc(rectangle.Right - diameter, rectangle.Top, diameter, diameter, 270, 90);
            path.AddArc(rectangle.Right - diameter, rectangle.Bottom - diameter, diameter, diameter, 0, 90);
            path.AddArc(rectangle.Left, rectangle.Bottom - diameter, diameter, diameter, 90, 90);
            path.CloseFigure();
            return path;
        }
    }

    internal sealed class RoundedPanel : Panel
    {
        public int CornerRadius { get; set; }
        public Color FillColor { get; set; }

        public RoundedPanel()
        {
            CornerRadius = 8;
            FillColor = Color.FromArgb(22, 24, 28);
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            e.Graphics.Clear(Parent == null ? Color.Transparent : Parent.BackColor);
            Rectangle rectangle = new Rectangle(0, 0, Width - 1, Height - 1);
            using (GraphicsPath path = CreatePath(rectangle, CornerRadius))
            using (SolidBrush fill = new SolidBrush(FillColor))
            {
                e.Graphics.FillPath(fill, path);
            }
        }

        private static GraphicsPath CreatePath(Rectangle rectangle, int radius)
        {
            int diameter = radius * 2;
            GraphicsPath path = new GraphicsPath();
            path.AddArc(rectangle.Left, rectangle.Top, diameter, diameter, 180, 90);
            path.AddArc(rectangle.Right - diameter, rectangle.Top, diameter, diameter, 270, 90);
            path.AddArc(rectangle.Right - diameter, rectangle.Bottom - diameter, diameter, diameter, 0, 90);
            path.AddArc(rectangle.Left, rectangle.Bottom - diameter, diameter, diameter, 90, 90);
            path.CloseFigure();
            return path;
        }
    }

    internal sealed class MainForm : Form
    {
        private readonly LenovoController controller;
        private readonly bool startHidden;
        private readonly Button autoButton;
        private readonly Button saverButton;
        private readonly Button performanceButton;
        private readonly ToggleSwitch conservationCheckBox;
        private readonly ToggleSwitch zeroTouchLoginCheckBox;
        private readonly ToggleSwitch zeroTouchLockCheckBox;
        private readonly ToggleSwitch startupCheckBox;
        private readonly Label statusLabel;
        private readonly NotifyIcon trayIcon;
        private readonly ToolStripMenuItem trayAuto;
        private readonly ToolStripMenuItem traySaver;
        private readonly ToolStripMenuItem trayPerformance;
        private readonly ToolStripMenuItem trayConservation;
        private readonly ToolStripMenuItem trayZeroTouchLogin;
        private readonly ToolStripMenuItem trayZeroTouchLock;
        private readonly ToolStripMenuItem trayStartup;
        private bool updating;
        private bool exiting;
        private bool balloonShown;

        private static readonly Color Accent = Color.FromArgb(108, 145, 255);
        private static readonly Color Background = Color.FromArgb(10, 11, 13);
        private static readonly Color Surface = Color.FromArgb(22, 24, 28);
        private static readonly Color Border = Color.FromArgb(43, 46, 53);
        private static readonly Color Muted = Color.FromArgb(139, 144, 154);

        [DllImport("user32.dll")]
        private static extern bool ReleaseCapture();

        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr window, int message, int parameter, int value);

        [DllImport("user32.dll")]
        private static extern bool DestroyIcon(IntPtr handle);

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int valueSize);

        public MainForm(bool startHidden)
        {
            this.startHidden = startHidden;
            balloonShown = startHidden;
            Text = Program.WindowTitle;
            ClientSize = new Size(432, 454);
            FormBorderStyle = FormBorderStyle.None;
            MaximizeBox = false;
            StartPosition = FormStartPosition.Manual;
            BackColor = Background;
            ForeColor = Color.White;
            Font = new Font("Segoe UI", 9F);
            Icon = CreateAppIcon();
            ShowInTaskbar = false;
            if (startHidden)
            {
                WindowState = FormWindowState.Minimized;
            }
            else
            {
                WindowState = FormWindowState.Normal;
            }

            Panel titleBar = new Panel();
            titleBar.BackColor = Color.FromArgb(13, 14, 17);
            titleBar.Location = new Point(1, 1);
            titleBar.Size = new Size(430, 49);
            titleBar.MouseDown += DragWindow;
            Controls.Add(titleBar);

            Label heading = new Label();
            heading.Text = "LENOVO POWER";
            heading.Font = new Font("Segoe UI Semibold", 11F);
            heading.Location = new Point(15, 8);
            heading.AutoSize = true;
            heading.MouseDown += DragWindow;
            titleBar.Controls.Add(heading);

            Label model = new Label();
            model.Text = "SLIM 7  ·  83MC";
            model.Font = new Font("Segoe UI", 7F);
            model.ForeColor = Muted;
            model.Location = new Point(17, 29);
            model.AutoSize = true;
            model.MouseDown += DragWindow;
            titleBar.Controls.Add(model);

            Label connected = new Label();
            connected.Text = "●  CONNECTED";
            connected.Font = new Font("Segoe UI Semibold", 7F);
            connected.ForeColor = Color.FromArgb(91, 210, 145);
            connected.Location = new Point(254, 19);
            connected.AutoSize = true;
            titleBar.Controls.Add(connected);

            Button hideButton = CreateWindowButton("—", 356);
            hideButton.Click += delegate { HideToTray(); };
            titleBar.Controls.Add(hideButton);

            Button closeButton = CreateWindowButton("×", 392);
            closeButton.Font = new Font("Segoe UI", 15F);
            closeButton.Click += delegate { HideToTray(); };
            titleBar.Controls.Add(closeButton);

            Label powerLabel = CreateSectionLabel("POWER MODE", 18, 65);
            Controls.Add(powerLabel);

            autoButton = CreateModeButton("ADAPTIVE\nAUTO", LenovoPowerMode.Auto, 18);
            saverButton = CreateModeButton("BATTERY\nSAVER", LenovoPowerMode.BatterySaver, 148);
            performanceButton = CreateModeButton("PERFORMANCE", LenovoPowerMode.Performance, 278);
            Controls.Add(autoButton);
            Controls.Add(saverButton);
            Controls.Add(performanceButton);

            Panel chargingPanel = CreateSettingPanel(18, 164);
            Controls.Add(chargingPanel);
            chargingPanel.Controls.Add(CreateSettingTitle("CONSERVATION MODE", 16, 10));
            chargingPanel.Controls.Add(CreateSettingSubtitle("Charge 75%  ·  stop 80%", 16, 31));

            conservationCheckBox = new ToggleSwitch();
            conservationCheckBox.BackColor = Surface;
            conservationCheckBox.Location = new Point(334, 16);
            conservationCheckBox.CheckedChanged += ConservationChanged;
            chargingPanel.Controls.Add(conservationCheckBox);

            Panel loginPanel = CreateSettingPanel(18, 228);
            Controls.Add(loginPanel);
            loginPanel.Controls.Add(CreateSettingTitle("ZERO-TOUCH LOGIN", 16, 10));
            loginPanel.Controls.Add(CreateSettingSubtitle("Wake and sign in when you approach", 16, 31));

            zeroTouchLoginCheckBox = new ToggleSwitch();
            zeroTouchLoginCheckBox.BackColor = Surface;
            zeroTouchLoginCheckBox.Location = new Point(334, 16);
            zeroTouchLoginCheckBox.CheckedChanged += ZeroTouchLoginChanged;
            loginPanel.Controls.Add(zeroTouchLoginCheckBox);

            Panel lockPanel = CreateSettingPanel(18, 292);
            Controls.Add(lockPanel);
            lockPanel.Controls.Add(CreateSettingTitle("ZERO-TOUCH LOCK", 16, 10));
            lockPanel.Controls.Add(CreateSettingSubtitle("Lock automatically when you walk away", 16, 31));

            zeroTouchLockCheckBox = new ToggleSwitch();
            zeroTouchLockCheckBox.BackColor = Surface;
            zeroTouchLockCheckBox.Location = new Point(334, 16);
            zeroTouchLockCheckBox.CheckedChanged += ZeroTouchLockChanged;
            lockPanel.Controls.Add(zeroTouchLockCheckBox);

            Panel startupPanel = CreateSettingPanel(18, 356);
            Controls.Add(startupPanel);
            startupPanel.Controls.Add(CreateSettingTitle("START WITH WINDOWS", 16, 10));
            startupPanel.Controls.Add(CreateSettingSubtitle("Launch silently in the notification tray", 16, 31));

            startupCheckBox = new ToggleSwitch();
            startupCheckBox.BackColor = Surface;
            startupCheckBox.Location = new Point(334, 16);
            startupCheckBox.CheckedChanged += StartupChanged;
            startupPanel.Controls.Add(startupCheckBox);

            statusLabel = new Label();
            statusLabel.TextAlign = ContentAlignment.MiddleLeft;
            statusLabel.Font = new Font("Segoe UI", 7.5F);
            statusLabel.ForeColor = Muted;
            statusLabel.Location = new Point(20, 425);
            statusLabel.Size = new Size(392, 18);
            Controls.Add(statusLabel);

            trayAuto = new ToolStripMenuItem("Adaptive (Auto)", null, delegate { ApplyPowerMode(LenovoPowerMode.Auto); });
            traySaver = new ToolStripMenuItem("Battery saver", null, delegate { ApplyPowerMode(LenovoPowerMode.BatterySaver); });
            trayPerformance = new ToolStripMenuItem("Performance", null, delegate { ApplyPowerMode(LenovoPowerMode.Performance); });
            trayConservation = new ToolStripMenuItem("Conservation mode", null, delegate { ApplyConservation(!trayConservation.Checked); });
            trayZeroTouchLogin = new ToolStripMenuItem("Zero-touch login", null, delegate { ApplyZeroTouchLogin(!trayZeroTouchLogin.Checked); });
            trayZeroTouchLock = new ToolStripMenuItem("Zero-touch lock", null, delegate { ApplyZeroTouchLock(!trayZeroTouchLock.Checked); });
            trayStartup = new ToolStripMenuItem("Start with Windows", null, delegate { ApplyStartup(!trayStartup.Checked); });
            ToolStripMenuItem powerMenu = new ToolStripMenuItem("Power mode");
            powerMenu.DropDownItems.AddRange(new ToolStripItem[] { trayAuto, traySaver, trayPerformance });

            ContextMenuStrip trayMenu = new ContextMenuStrip();
            trayMenu.BackColor = Color.FromArgb(19, 21, 25);
            trayMenu.ForeColor = Color.White;
            trayMenu.Renderer = new ToolStripProfessionalRenderer(new DarkColorTable());
            trayMenu.ShowImageMargin = false;
            trayMenu.Items.Add("Open power panel", null, delegate { ShowFromTray(); });
            trayMenu.Items.Add(new ToolStripSeparator());
            trayMenu.Items.Add(powerMenu);
            trayMenu.Items.Add(trayConservation);
            trayMenu.Items.Add(new ToolStripSeparator());
            trayMenu.Items.Add(trayZeroTouchLogin);
            trayMenu.Items.Add(trayZeroTouchLock);
            trayMenu.Items.Add(trayStartup);
            trayMenu.Items.Add(new ToolStripSeparator());
            trayMenu.Items.Add("Refresh", null, delegate { RefreshState(true); });
            trayMenu.Items.Add("Exit", null, delegate { ExitApplication(); });

            trayIcon = new NotifyIcon();
            trayIcon.Text = "Lenovo Quick Settings";
            trayIcon.Icon = Icon;
            trayIcon.ContextMenuStrip = trayMenu;
            trayIcon.Visible = true;
            trayIcon.MouseClick += delegate(object sender, MouseEventArgs args)
            {
                if (args.Button == MouseButtons.Left)
                {
                    if (Visible) HideToTray(); else ShowFromTray();
                }
            };

            FormClosing += OnFormClosing;
            Resize += delegate
            {
                if (WindowState == FormWindowState.Minimized) HideToTray();
            };
            Shown += delegate
            {
                PositionNearTaskbar();
                RefreshState(false);
                if (this.startHidden)
                {
                    BeginInvoke((MethodInvoker)delegate { HideToTray(); });
                }
                else
                {
                    WindowState = FormWindowState.Normal;
                    ShowInTaskbar = false;
                    Show();
                    Activate();
                }
            };

            try
            {
                controller = new LenovoController();
            }
            catch (Exception ex)
            {
                SetControlsEnabled(false);
                statusLabel.Text = "Lenovo control unavailable";
                if (!startHidden)
                {
                    MessageBox.Show(this, ex.Message, "Lenovo Quick Settings", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }

        private Button CreateModeButton(string text, LenovoPowerMode mode, int left)
        {
            RoundedButton button = new RoundedButton();
            button.Text = text;
            button.Tag = mode;
            button.Location = new Point(left, 88);
            button.Size = new Size(120, 62);
            button.Font = new Font("Segoe UI Semibold", 8.5F);
            button.FlatStyle = FlatStyle.Flat;
            button.FlatAppearance.BorderSize = 1;
            button.FlatAppearance.BorderColor = Border;
            button.BackColor = Surface;
            button.OutlineColor = Border;
            button.HoverFillColor = Color.FromArgb(29, 32, 38);
            button.ForeColor = Color.FromArgb(222, 225, 232);
            button.Cursor = Cursors.Hand;
            button.Click += delegate { ApplyPowerMode((LenovoPowerMode)button.Tag); };
            return button;
        }

        private static Button CreateWindowButton(string text, int left)
        {
            Button button = new Button();
            button.Text = text;
            button.Location = new Point(left, 7);
            button.Size = new Size(34, 34);
            button.Font = new Font("Segoe UI", 11F);
            button.FlatStyle = FlatStyle.Flat;
            button.FlatAppearance.BorderSize = 0;
            button.FlatAppearance.MouseOverBackColor = Color.FromArgb(40, 42, 48);
            button.BackColor = Color.FromArgb(13, 14, 17);
            button.ForeColor = Color.FromArgb(184, 188, 196);
            button.Cursor = Cursors.Hand;
            button.TabStop = false;
            return button;
        }

        private static Label CreateSectionLabel(string text, int left, int top)
        {
            Label label = new Label();
            label.Text = text;
            label.Location = new Point(left, top);
            label.AutoSize = true;
            label.Font = new Font("Segoe UI Semibold", 7.5F);
            label.ForeColor = Muted;
            return label;
        }

        private static Panel CreateSettingPanel(int left, int top)
        {
            RoundedPanel panel = new RoundedPanel();
            panel.Location = new Point(left, top);
            panel.Size = new Size(396, 54);
            panel.BackColor = Background;
            panel.FillColor = Surface;
            return panel;
        }

        private static Label CreateSettingTitle(string text, int left, int top)
        {
            Label label = new Label();
            label.Text = text;
            label.Location = new Point(left, top);
            label.AutoSize = true;
            label.Font = new Font("Segoe UI Semibold", 8.5F);
            label.ForeColor = Color.FromArgb(229, 231, 236);
            label.BackColor = Surface;
            return label;
        }

        private static Label CreateSettingSubtitle(string text, int left, int top)
        {
            Label label = new Label();
            label.Text = text;
            label.Location = new Point(left, top);
            label.AutoSize = true;
            label.Font = new Font("Segoe UI", 7.5F);
            label.ForeColor = Muted;
            label.BackColor = Surface;
            return label;
        }

        private void DragWindow(object sender, MouseEventArgs args)
        {
            if (args.Button != MouseButtons.Left) return;
            ReleaseCapture();
            SendMessage(Handle, 0xA1, 0x2, 0);
        }

        private static Icon CreateAppIcon()
        {
            using (Bitmap bitmap = new Bitmap(32, 32))
            using (Graphics graphics = Graphics.FromImage(bitmap))
            {
                graphics.SmoothingMode = SmoothingMode.AntiAlias;
                graphics.Clear(Color.Transparent);
                using (SolidBrush background = new SolidBrush(Color.FromArgb(10, 11, 13)))
                using (GraphicsPath shape = CreateRoundedPath(new Rectangle(1, 1, 30, 30), 7))
                {
                    graphics.FillPath(background, shape);
                }
                using (Pen accent = new Pen(Accent, 2F))
                using (GraphicsPath outline = CreateRoundedPath(new Rectangle(2, 2, 28, 28), 6))
                {
                    graphics.DrawPath(accent, outline);
                }
                using (Font font = new Font("Segoe UI Semibold", 14F, FontStyle.Bold, GraphicsUnit.Pixel))
                using (SolidBrush foreground = new SolidBrush(Color.White))
                {
                    graphics.DrawString("L", font, foreground, new PointF(10F, 7F));
                }

                IntPtr handle = bitmap.GetHicon();
                try
                {
                    using (Icon temporary = Icon.FromHandle(handle))
                    {
                        return (Icon)temporary.Clone();
                    }
                }
                finally
                {
                    DestroyIcon(handle);
                }
            }
        }

        private static GraphicsPath CreateRoundedPath(Rectangle rectangle, int radius)
        {
            int diameter = radius * 2;
            GraphicsPath path = new GraphicsPath();
            path.AddArc(rectangle.Left, rectangle.Top, diameter, diameter, 180, 90);
            path.AddArc(rectangle.Right - diameter, rectangle.Top, diameter, diameter, 270, 90);
            path.AddArc(rectangle.Right - diameter, rectangle.Bottom - diameter, diameter, diameter, 0, 90);
            path.AddArc(rectangle.Left, rectangle.Bottom - diameter, diameter, diameter, 90, 90);
            path.CloseFigure();
            return path;
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            int roundedCorners = 2;
            DwmSetWindowAttribute(Handle, 33, ref roundedCorners, sizeof(int));
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (Pen pen = new Pen(Border))
            using (GraphicsPath outline = CreateRoundedPath(new Rectangle(0, 0, ClientSize.Width - 1, ClientSize.Height - 1), 9))
            {
                e.Graphics.DrawPath(pen, outline);
            }
        }

        private void RefreshState(bool showErrors)
        {
            if (controller == null || updating) return;
            try
            {
                updating = true;
                SetControlsEnabled(false);
                statusLabel.Text = "Reading Lenovo controller...";
                Application.DoEvents();

                LenovoState state = controller.GetState();
                UpdateModeButtons(state.PowerMode);
                conservationCheckBox.Checked = state.ConservationMode;
                trayConservation.Checked = state.ConservationMode;
                zeroTouchLoginCheckBox.Checked = state.ZeroTouchLogin;
                trayZeroTouchLogin.Checked = state.ZeroTouchLogin;
                zeroTouchLockCheckBox.Checked = state.ZeroTouchLock;
                trayZeroTouchLock.Checked = state.ZeroTouchLock;
                bool startupEnabled = StartupManager.IsEnabled();
                startupCheckBox.Checked = startupEnabled;
                trayStartup.Checked = startupEnabled;
                statusLabel.Text = "●  HARDWARE STATE VERIFIED";
            }
            catch (Exception ex)
            {
                statusLabel.Text = "Read failed";
                if (showErrors) ShowError(ex);
            }
            finally
            {
                SetControlsEnabled(true);
                updating = false;
            }
        }

        private void ApplyPowerMode(LenovoPowerMode mode)
        {
            if (controller == null || updating) return;
            try
            {
                updating = true;
                SetControlsEnabled(false);
                statusLabel.Text = "Applying " + DisplayName(mode) + "...";
                Application.DoEvents();
                controller.SetPowerMode(mode);
                UpdateModeButtons(mode);
                statusLabel.Text = DisplayName(mode) + " verified";
            }
            catch (Exception ex)
            {
                statusLabel.Text = "Change failed";
                ShowError(ex);
                RefreshState(false);
            }
            finally
            {
                SetControlsEnabled(true);
                updating = false;
            }
        }

        private void ConservationChanged(object sender, EventArgs args)
        {
            if (!updating) ApplyConservation(conservationCheckBox.Checked);
        }

        private void StartupChanged(object sender, EventArgs args)
        {
            if (!updating) ApplyStartup(startupCheckBox.Checked);
        }

        private void ZeroTouchLoginChanged(object sender, EventArgs args)
        {
            if (!updating) ApplyZeroTouchLogin(zeroTouchLoginCheckBox.Checked);
        }

        private void ZeroTouchLockChanged(object sender, EventArgs args)
        {
            if (!updating) ApplyZeroTouchLock(zeroTouchLockCheckBox.Checked);
        }

        private void ApplyStartup(bool enabled)
        {
            if (updating) return;
            try
            {
                updating = true;
                StartupManager.SetEnabled(enabled);
                startupCheckBox.Checked = enabled;
                trayStartup.Checked = enabled;
                statusLabel.Text = enabled ? "●  STARTUP ENABLED  ·  TRAY ONLY" : "●  STARTUP DISABLED";
            }
            catch (Exception ex)
            {
                bool actual = StartupManager.IsEnabled();
                startupCheckBox.Checked = actual;
                trayStartup.Checked = actual;
                ShowError(ex);
            }
            finally
            {
                updating = false;
            }
        }

        private void ApplyConservation(bool enabled)
        {
            if (controller == null || updating) return;
            try
            {
                updating = true;
                SetControlsEnabled(false);
                statusLabel.Text = "Updating charging mode...";
                Application.DoEvents();
                controller.SetConservationMode(enabled);
                conservationCheckBox.Checked = enabled;
                trayConservation.Checked = enabled;
                statusLabel.Text = enabled ? "Conservation verified" : "Normal charging verified";
            }
            catch (Exception ex)
            {
                statusLabel.Text = "Change failed";
                ShowError(ex);
                RefreshState(false);
            }
            finally
            {
                SetControlsEnabled(true);
                updating = false;
            }
        }

        private void ApplyZeroTouchLogin(bool enabled)
        {
            ApplyPresenceSetting(
                enabled,
                "Updating zero-touch login...",
                delegate { controller.SetZeroTouchLogin(enabled); },
                zeroTouchLoginCheckBox,
                trayZeroTouchLogin,
                enabled ? "Zero-touch login verified" : "Zero-touch login disabled"
            );
        }

        private void ApplyZeroTouchLock(bool enabled)
        {
            ApplyPresenceSetting(
                enabled,
                "Updating zero-touch lock...",
                delegate { controller.SetZeroTouchLock(enabled); },
                zeroTouchLockCheckBox,
                trayZeroTouchLock,
                enabled ? "Zero-touch lock verified" : "Zero-touch lock disabled"
            );
        }

        private void ApplyPresenceSetting(
            bool enabled,
            string progress,
            Action apply,
            ToggleSwitch toggle,
            ToolStripMenuItem menuItem,
            string success
        )
        {
            if (controller == null || updating) return;
            try
            {
                updating = true;
                SetControlsEnabled(false);
                statusLabel.Text = progress;
                Application.DoEvents();
                apply();
                toggle.Checked = enabled;
                menuItem.Checked = enabled;
                statusLabel.Text = success;
            }
            catch (Exception ex)
            {
                statusLabel.Text = "Change failed";
                ShowError(ex);
                RefreshState(false);
            }
            finally
            {
                SetControlsEnabled(true);
                updating = false;
            }
        }

        private void UpdateModeButtons(LenovoPowerMode selected)
        {
            SetSelected(autoButton, selected == LenovoPowerMode.Auto);
            SetSelected(saverButton, selected == LenovoPowerMode.BatterySaver);
            SetSelected(performanceButton, selected == LenovoPowerMode.Performance);
            trayAuto.Checked = selected == LenovoPowerMode.Auto;
            traySaver.Checked = selected == LenovoPowerMode.BatterySaver;
            trayPerformance.Checked = selected == LenovoPowerMode.Performance;
        }

        private static void SetSelected(Button button, bool selected)
        {
            button.BackColor = selected ? Color.FromArgb(31, 43, 74) : Surface;
            button.ForeColor = selected ? Color.White : Color.FromArgb(198, 202, 210);
            button.FlatAppearance.BorderColor = selected ? Accent : Border;
            RoundedButton rounded = button as RoundedButton;
            if (rounded != null)
            {
                rounded.OutlineColor = selected ? Accent : Border;
                rounded.HoverFillColor = selected ? Color.FromArgb(37, 51, 86) : Color.FromArgb(29, 32, 38);
                rounded.Invalidate();
            }
        }

        private void SetControlsEnabled(bool enabled)
        {
            autoButton.Enabled = enabled;
            saverButton.Enabled = enabled;
            performanceButton.Enabled = enabled;
            conservationCheckBox.Enabled = enabled;
            zeroTouchLoginCheckBox.Enabled = enabled;
            zeroTouchLockCheckBox.Enabled = enabled;
            startupCheckBox.Enabled = enabled;
        }

        private static string DisplayName(LenovoPowerMode mode)
        {
            if (mode == LenovoPowerMode.Auto) return "Adaptive Auto";
            if (mode == LenovoPowerMode.BatterySaver) return "Battery saver";
            return "Performance";
        }

        private void PositionNearTaskbar()
        {
            Rectangle area = Screen.FromControl(this).WorkingArea;
            Location = new Point(area.Right - Width - 16, area.Bottom - Height - 16);
        }

        private void HideToTray()
        {
            if (WindowState == FormWindowState.Minimized)
            {
                WindowState = FormWindowState.Normal;
            }
            ShowInTaskbar = false;
            Hide();
            if (!balloonShown)
            {
                trayIcon.ShowBalloonTip(1800, "Lenovo Quick Settings", "Click the tray icon for power, charging, and presence controls.", ToolTipIcon.Info);
                balloonShown = true;
            }
        }

        private void ShowFromTray()
        {
            ShowInTaskbar = false;
            Show();
            WindowState = FormWindowState.Normal;
            PositionNearTaskbar();
            Activate();
            BringToFront();
            RefreshState(false);
        }

        private void ExitApplication()
        {
            exiting = true;
            trayIcon.Visible = false;
            trayIcon.Dispose();
            Close();
        }

        private void OnFormClosing(object sender, FormClosingEventArgs args)
        {
            if (!exiting && args.CloseReason == CloseReason.UserClosing)
            {
                args.Cancel = true;
                HideToTray();
            }
        }

        private void ShowError(Exception exception)
        {
            MessageBox.Show(this, exception.Message, "Lenovo Quick Settings", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    internal static class Program
    {
        public const string WindowTitle = "Lenovo Quick Settings — 83MC";
        private const string MutexName = "Local\\LenovoQuickSettings-83MC";

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr FindWindow(string className, string windowName);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr window, int command);

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr window);

        [STAThread]
        private static int Main(string[] args)
        {
            if (args.Length >= 2 && args[0].Equals("--status-file", StringComparison.OrdinalIgnoreCase))
            {
                return WriteStatusFile(args[1]);
            }

            if (args.Length >= 2 && args[0].Equals("--verification-cycle", StringComparison.OrdinalIgnoreCase))
            {
                return RunVerificationCycle(args[1]);
            }

            bool startHidden = args.Any(delegate(string arg) { return arg.Equals("--tray", StringComparison.OrdinalIgnoreCase); });
            bool createdNew;
            using (Mutex mutex = new Mutex(true, MutexName, out createdNew))
            {
                if (!createdNew)
                {
                    if (!startHidden)
                    {
                        IntPtr existing = FindWindow(null, WindowTitle);
                        if (existing != IntPtr.Zero)
                        {
                            ShowWindow(existing, 9);
                            SetForegroundWindow(existing);
                        }
                    }
                    return 0;
                }

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new MainForm(startHidden));
                GC.KeepAlive(mutex);
            }
            return 0;
        }

        private static int WriteStatusFile(string path)
        {
            try
            {
                LenovoController controller = new LenovoController();
                LenovoState state = controller.GetState();
                string report =
                    "PowerMode=" + state.PowerMode + Environment.NewLine +
                    "ConservationMode=" + state.ConservationMode + Environment.NewLine +
                    "ZeroTouchLogin=" + state.ZeroTouchLogin + Environment.NewLine +
                    "ZeroTouchLock=" + state.ZeroTouchLock + Environment.NewLine +
                    "AddinDirectory=" + controller.AddinDirectory + Environment.NewLine;
                File.WriteAllText(path, report);
                return 0;
            }
            catch (Exception ex)
            {
                File.WriteAllText(path, "ERROR=" + ex + Environment.NewLine);
                return 1;
            }
        }

        private static int RunVerificationCycle(string path)
        {
            StringBuilder report = new StringBuilder();
            LenovoController controller = null;
            LenovoState original = null;
            try
            {
                controller = new LenovoController();
                original = controller.GetState();
                report.AppendLine("OriginalPowerMode=" + original.PowerMode);
                report.AppendLine("OriginalConservationMode=" + original.ConservationMode);
                report.AppendLine("OriginalZeroTouchLogin=" + original.ZeroTouchLogin);
                report.AppendLine("OriginalZeroTouchLock=" + original.ZeroTouchLock);

                LenovoPowerMode testPower = original.PowerMode == LenovoPowerMode.BatterySaver
                    ? LenovoPowerMode.Auto
                    : LenovoPowerMode.BatterySaver;
                controller.SetPowerMode(testPower);
                report.AppendLine("TestPowerModeVerified=" + controller.GetState().PowerMode);
                controller.SetPowerMode(original.PowerMode);
                report.AppendLine("RestoredPowerMode=" + controller.GetState().PowerMode);

                controller.SetConservationMode(!original.ConservationMode);
                report.AppendLine("TestConservationModeVerified=" + controller.GetState().ConservationMode);
                controller.SetConservationMode(original.ConservationMode);
                report.AppendLine("RestoredConservationMode=" + controller.GetState().ConservationMode);
                report.AppendLine("RESULT=PASS");
                File.WriteAllText(path, report.ToString());
                return 0;
            }
            catch (Exception ex)
            {
                report.AppendLine("ERROR=" + ex);
                if (controller != null && original != null)
                {
                    try { controller.SetPowerMode(original.PowerMode); } catch (Exception restoreEx) { report.AppendLine("POWER_RESTORE_ERROR=" + restoreEx); }
                    try { controller.SetConservationMode(original.ConservationMode); } catch (Exception restoreEx) { report.AppendLine("CHARGING_RESTORE_ERROR=" + restoreEx); }
                }
                File.WriteAllText(path, report.ToString());
                return 1;
            }
        }
    }
}
