// Game Focus Guard - Windows 10/11, .NET Framework 4.x, C# 5 compatible.
// No code injection, process patching, global keyboard hook, or driver.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace GameFocusGuard
{
    internal static class Native
    {
        internal const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
        internal const uint WINEVENT_OUTOFCONTEXT = 0x0000;
        internal const uint WINEVENT_SKIPOWNPROCESS = 0x0002;
        internal const int WM_HOTKEY = 0x0312;
        internal const uint MOD_ALT = 0x0001;
        internal const uint MOD_CONTROL = 0x0002;
        internal const uint MOD_NOREPEAT = 0x4000;
        internal const uint VK_F10 = 0x79;
        internal const int SW_RESTORE = 9;

        internal delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr param);
        internal delegate void WinEventProc(IntPtr hook, uint evt, IntPtr hwnd,
            int objectId, int childId, uint eventThread, uint eventTime);

        [StructLayout(LayoutKind.Sequential)]
        internal struct RECT { public int Left, Top, Right, Bottom; }

        [DllImport("user32.dll")]
        internal static extern bool EnumWindows(EnumWindowsProc callback, IntPtr param);

        [DllImport("user32.dll")]
        internal static extern bool IsWindowVisible(IntPtr hwnd);

        [DllImport("user32.dll")]
        internal static extern bool IsWindow(IntPtr hwnd);

        [DllImport("user32.dll")]
        internal static extern bool IsIconic(IntPtr hwnd);

        [DllImport("user32.dll")]
        internal static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        internal static extern int GetWindowTextLength(IntPtr hwnd);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        internal static extern int GetWindowText(IntPtr hwnd, StringBuilder buffer, int length);

        [DllImport("user32.dll")]
        internal static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);

        [DllImport("user32.dll")]
        internal static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetForegroundWindow(IntPtr hwnd);

        [DllImport("user32.dll")]
        internal static extern bool BringWindowToTop(IntPtr hwnd);

        [DllImport("user32.dll")]
        internal static extern bool ShowWindowAsync(IntPtr hwnd, int command);

        [DllImport("kernel32.dll")]
        internal static extern uint GetCurrentThreadId();

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool AttachThreadInput(uint threadA, uint threadB,
            [MarshalAs(UnmanagedType.Bool)] bool attach);

        [DllImport("user32.dll")]
        internal static extern short GetAsyncKeyState(int key);

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax,
            IntPtr module, WinEventProc callback, uint processId, uint threadId, uint flags);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool UnhookWinEvent(IntPtr hook);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint key);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool UnregisterHotKey(IntPtr hwnd, int id);

        internal static uint PidOf(IntPtr hwnd)
        {
            uint pid;
            if (hwnd == IntPtr.Zero) return 0;
            GetWindowThreadProcessId(hwnd, out pid);
            return pid;
        }

        internal static string TitleOf(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return "(无窗口)";
            int length = Math.Min(GetWindowTextLength(hwnd), 300);
            StringBuilder builder = new StringBuilder(length + 2);
            GetWindowText(hwnd, builder, builder.Capacity);
            string title = builder.ToString().Replace('\r', ' ').Replace('\n', ' ').Trim();
            return title.Length == 0 ? "(无标题)" : title;
        }

        internal static string ProcessName(uint pid)
        {
            if (pid == 0) return "System";
            try { return Process.GetProcessById((int)pid).ProcessName + ".exe"; }
            catch { return "PID " + pid; }
        }

        internal static bool IsDown(int key)
        {
            return (GetAsyncKeyState(key) & 0x8000) != 0;
        }
    }

    internal sealed class GameWindow
    {
        internal IntPtr Handle;
        internal uint Pid;
        internal string Process;
        internal string Title;

        public override string ToString()
        {
            return Process + "  [PID " + Pid + "]  —  " + Title;
        }
    }

    internal sealed class GuardForm : Form
    {
        private const int HotkeyId = 0x6A31;
        private readonly ComboBox gameList = new ComboBox();
        private readonly Button refreshButton = new Button();
        private readonly Button startButton = new Button();
        private readonly Button pauseButton = new Button();
        private readonly Button stopButton = new Button();
        private readonly CheckBox monitorOnlyBox = new CheckBox();
        private readonly CheckBox restoreBox = new CheckBox();
        private readonly CheckBox enhancedBox = new CheckBox();
        private readonly Label stateLabel = new Label();
        private readonly Label pathLabel = new Label();
        private readonly TextBox logBox = new TextBox();
        private readonly Timer timer = new Timer();
        private readonly NotifyIcon tray = new NotifyIcon();
        private readonly ToolStripMenuItem trayPauseItem = new ToolStripMenuItem();

        private Native.WinEventProc hookCallback; // Must stay rooted while registered.
        private IntPtr eventHook = IntPtr.Zero;
        private bool hotkeyRegistered;
        private bool running;
        private bool manualPause;
        private bool intentionalPause;
        private bool sawGame;
        private bool wasGameLastTick;
        private uint gamePid;
        private IntPtr gameHandle;
        private IntPtr lastForeground = IntPtr.Zero;
        private int attempts;
        private DateTime lastAttemptUtc = DateTime.MinValue;
        private DateTime lastGameUtc = DateTime.MinValue;
        private DateTime lastShortcutUtc = DateTime.MinValue;
        private readonly uint selfPid = (uint)Process.GetCurrentProcess().Id;
        private readonly string logPath;

        internal GuardForm()
        {
            Text = "Game Focus Guard 1.0 — 全屏游戏焦点守卫";
            ClientSize = new Size(760, 575);
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            Icon = SystemIcons.Shield;
            AutoScaleMode = AutoScaleMode.Font;
            Font = new Font("Microsoft YaHei UI", 9F);

            Label description = new Label();
            description.Text = "选择正在运行的游戏窗口。点击“开始守护”后，回到游戏即可自动启用。";
            description.SetBounds(16, 14, 720, 30);
            Controls.Add(description);

            gameList.DropDownStyle = ComboBoxStyle.DropDownList;
            gameList.SetBounds(16, 47, 622, 30);
            Controls.Add(gameList);

            refreshButton.Text = "刷新窗口";
            refreshButton.SetBounds(648, 46, 96, 30);
            refreshButton.Click += delegate { RefreshGameList(); };
            Controls.Add(refreshButton);

            startButton.Text = "开始守护";
            startButton.SetBounds(16, 96, 138, 39);
            startButton.Click += delegate { StartGuard(); };
            Controls.Add(startButton);

            pauseButton.Text = "暂停 / 继续";
            pauseButton.SetBounds(165, 96, 138, 39);
            pauseButton.Click += delegate { TogglePause(); };
            Controls.Add(pauseButton);

            stopButton.Text = "停止守护";
            stopButton.SetBounds(314, 96, 138, 39);
            stopButton.Click += delegate { StopGuard("已手动停止守护"); };
            Controls.Add(stopButton);

            Button logButton = new Button();
            logButton.Text = "查看日志文件";
            logButton.SetBounds(605, 96, 139, 39);
            logButton.Click += delegate { OpenLog(); };
            Controls.Add(logButton);

            monitorOnlyBox.Text = "仅观察，不自动抢回焦点（先排查时使用）";
            monitorOnlyBox.SetBounds(16, 153, 440, 25);
            Controls.Add(monitorOnlyBox);

            restoreBox.Text = "如果游戏最小化，尝试恢复窗口";
            restoreBox.SetBounds(16, 185, 440, 25);
            restoreBox.Checked = true;
            Controls.Add(restoreBox);

            enhancedBox.Text = "增强恢复：短暂连接输入线程（反作弊游戏慎用）";
            enhancedBox.SetBounds(16, 217, 630, 25);
            enhancedBox.Checked = false;
            Controls.Add(enhancedBox);

            stateLabel.Text = "状态：未启动。Ctrl + Alt + F10 可暂停或继续。";
            stateLabel.SetBounds(16, 254, 728, 28);
            stateLabel.ForeColor = Color.DarkBlue;
            Controls.Add(stateLabel);

            Label logCaption = new Label();
            logCaption.Text = "事件记录（谁抢走了游戏焦点）";
            logCaption.SetBounds(16, 292, 690, 25);
            Controls.Add(logCaption);

            logBox.SetBounds(16, 319, 728, 201);
            logBox.Multiline = true;
            logBox.ReadOnly = true;
            logBox.ScrollBars = ScrollBars.Vertical;
            logBox.Font = new Font("Consolas", 9F);
            Controls.Add(logBox);

            pathLabel.Text = "日志保存位置：";
            pathLabel.SetBounds(16, 531, 728, 28);
            pathLabel.AutoEllipsis = true;
            Controls.Add(pathLabel);

            string logDir = Path.Combine(Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData), "GameFocusGuard");
            logPath = Path.Combine(logDir, "focus-events.log");
            pathLabel.Text = "日志：" + logPath;

            ContextMenuStrip menu = new ContextMenuStrip();
            trayPauseItem.Text = "暂停 / 继续守护";
            trayPauseItem.Click += delegate { TogglePause(); };
            menu.Items.Add(trayPauseItem);
            ToolStripMenuItem showItem = new ToolStripMenuItem("显示主窗口");
            showItem.Click += delegate { RestoreMainWindow(); };
            menu.Items.Add(showItem);
            ToolStripMenuItem exitItem = new ToolStripMenuItem("退出工具");
            exitItem.Click += delegate { Close(); };
            menu.Items.Add(exitItem);
            tray.Icon = SystemIcons.Shield;
            tray.Text = "Game Focus Guard";
            tray.ContextMenuStrip = menu;
            tray.DoubleClick += delegate { RestoreMainWindow(); };
            tray.Visible = true;

            timer.Interval = 45;
            timer.Tick += delegate { OnTick(); };
            timer.Start();

            Shown += delegate
            {
                hookCallback = new Native.WinEventProc(WinEventChanged);
                eventHook = Native.SetWinEventHook(Native.EVENT_SYSTEM_FOREGROUND,
                    Native.EVENT_SYSTEM_FOREGROUND, IntPtr.Zero, hookCallback, 0, 0,
                    Native.WINEVENT_OUTOFCONTEXT | Native.WINEVENT_SKIPOWNPROCESS);
                hotkeyRegistered = Native.RegisterHotKey(Handle, HotkeyId,
                    Native.MOD_CONTROL | Native.MOD_ALT | Native.MOD_NOREPEAT, Native.VK_F10);
                RefreshGameList();
                Log("工具已启动；窗口事件监视：" + (eventHook != IntPtr.Zero ? "正常" : "轮询备用") +
                    "；快捷键：" + (hotkeyRegistered ? "可用" : "冲突或不可用"));
            };

            Resize += delegate
            {
                if (WindowState == FormWindowState.Minimized)
                {
                    Hide();
                    ShowInTaskbar = false;
                }
            };
            UpdateButtons();
        }

        protected override void WndProc(ref Message message)
        {
            if (message.Msg == Native.WM_HOTKEY && message.WParam.ToInt32() == HotkeyId)
            {
                TogglePause();
                return;
            }
            base.WndProc(ref message);
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            timer.Stop();
            if (eventHook != IntPtr.Zero)
                Native.UnhookWinEvent(eventHook);
            if (hotkeyRegistered)
                Native.UnregisterHotKey(Handle, HotkeyId);
            tray.Visible = false;
            tray.Dispose();
            base.OnFormClosed(e);
        }

        private void RestoreMainWindow()
        {
            Show();
            ShowInTaskbar = true;
            WindowState = FormWindowState.Normal;
            Activate();
        }

        private static List<GameWindow> VisibleWindows()
        {
            List<GameWindow> windows = new List<GameWindow>();
            Native.EnumWindows(delegate(IntPtr hwnd, IntPtr unused)
            {
                if (!Native.IsWindowVisible(hwnd)) return true;
                Native.RECT bounds;
                if (!Native.GetWindowRect(hwnd, out bounds)) return true;
                if (bounds.Right - bounds.Left < 300 || bounds.Bottom - bounds.Top < 180)
                    return true;
                uint pid = Native.PidOf(hwnd);
                if (pid == 0 || pid == (uint)Process.GetCurrentProcess().Id)
                    return true;
                string title = Native.TitleOf(hwnd);
                // Some full-screen games use an untitled top-level window.
                // Keep sizable candidates without titles, but filter tiny overlays.
                if (title == "(无标题)" &&
                    (bounds.Right - bounds.Left < 800 || bounds.Bottom - bounds.Top < 600))
                    return true;
                GameWindow item = new GameWindow();
                item.Handle = hwnd;
                item.Pid = pid;
                item.Process = Native.ProcessName(pid);
                item.Title = title;
                windows.Add(item);
                return true;
            }, IntPtr.Zero);
            windows.Sort(delegate(GameWindow a, GameWindow b)
            {
                return String.Compare(a.Process, b.Process, StringComparison.OrdinalIgnoreCase);
            });
            return windows;
        }

        private void RefreshGameList()
        {
            uint selectedPid = 0;
            GameWindow previous = gameList.SelectedItem as GameWindow;
            if (previous != null) selectedPid = previous.Pid;
            gameList.Items.Clear();
            int selectedIndex = -1;
            foreach (GameWindow item in VisibleWindows())
            {
                int index = gameList.Items.Add(item);
                if (item.Pid == selectedPid) selectedIndex = index;
            }
            if (gameList.Items.Count > 0)
                gameList.SelectedIndex = selectedIndex >= 0 ? selectedIndex : 0;
            Log("已发现 " + gameList.Items.Count + " 个可选择的窗口。");
        }

        private void StartGuard()
        {
            GameWindow selected = gameList.SelectedItem as GameWindow;
            if (selected == null)
            {
                MessageBox.Show(this, "先运行游戏，再刷新并选择游戏窗口。", "Game Focus Guard",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            gamePid = selected.Pid;
            gameHandle = selected.Handle;
            if (!Native.IsWindow(gameHandle))
            {
                MessageBox.Show(this, "窗口已关闭或重建，请刷新列表。", "Game Focus Guard",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            running = true;
            manualPause = false;
            intentionalPause = false;
            sawGame = false;
            attempts = 0;
            lastForeground = IntPtr.Zero;
            lastShortcutUtc = DateTime.MinValue;
            lastGameUtc = DateTime.MinValue;
            Log("开始守护：" + selected.Process + " [PID " + gamePid + "] " + selected.Title);
            Log("回到游戏一次后生效；切换到其他程序会尝试恢复。" +
                (enhancedBox.Checked ? " 增强模式已启用。" : " 标准模式。"));
            SetState("等待游戏进入前台（现在切回游戏即可）", Color.DarkBlue);
            UpdateButtons();
        }

        private void StopGuard(string reason)
        {
            if (!running) return;
            running = false;
            manualPause = false;
            intentionalPause = false;
            sawGame = false;
            attempts = 0;
            Log(reason);
            SetState("未守护", Color.DarkBlue);
            UpdateButtons();
        }

        private void TogglePause()
        {
            if (!running) return;
            manualPause = !manualPause;
            if (manualPause)
            {
                SetState("已手动暂停；不会抢回焦点", Color.DarkGoldenrod);
                Log("手动暂停");
            }
            else
            {
                intentionalPause = false;
                sawGame = false; // Never steal immediately from the tool or another app.
                attempts = 0;
                SetState("等待游戏进入前台后重新守护", Color.DarkBlue);
                Log("手动继续；等待游戏回到前台后重新启用");
            }
            UpdateButtons();
        }

        private void UpdateButtons()
        {
            startButton.Enabled = !running;
            stopButton.Enabled = running;
            pauseButton.Enabled = running;
            gameList.Enabled = !running;
            refreshButton.Enabled = !running;
            monitorOnlyBox.Enabled = !running;
            restoreBox.Enabled = !running;
            enhancedBox.Enabled = !running;
            trayPauseItem.Enabled = running;
            pauseButton.Text = manualPause ? "继续守护" : "暂停 / 继续";
        }

        private void SetState(string description, Color color)
        {
            if (stateLabel.Text == "状态：" + description) return;
            stateLabel.Text = "状态：" + description;
            stateLabel.ForeColor = color;
        }

        private void Log(string message)
        {
            string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + "  " +
                message.Replace('\r', ' ').Replace('\n', ' ') + Environment.NewLine;
            logBox.AppendText(line);
            if (logBox.TextLength > 30000)
                logBox.Text = logBox.Text.Substring(logBox.TextLength - 20000);
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(logPath));
                File.AppendAllText(logPath, line, Encoding.UTF8);
            }
            catch { /* The guard still works if the log directory is unwritable. */ }
        }

        private void OpenLog()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(logPath));
                if (!File.Exists(logPath)) File.WriteAllText(logPath, "", Encoding.UTF8);
                Process.Start("notepad.exe", "\"" + logPath + "\"");
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "无法打开日志：" + ex.Message, "Game Focus Guard");
            }
        }

        private void WinEventChanged(IntPtr hook, uint evt, IntPtr hwnd,
            int objectId, int childId, uint eventThread, uint eventTime)
        {
            if (evt != Native.EVENT_SYSTEM_FOREGROUND || objectId != 0 || hwnd == IntPtr.Zero)
                return;
            try
            {
                if (IsDisposed) return;
                if (InvokeRequired)
                    BeginInvoke(new Action<IntPtr>(RecordForeground), new object[] { hwnd });
                else
                    RecordForeground(hwnd);
            }
            catch (InvalidOperationException) { }
            catch (ObjectDisposedException) { }
        }

        private void RecordForeground(IntPtr hwnd)
        {
            if (!running || hwnd == IntPtr.Zero || lastForeground == hwnd) return;
            lastForeground = hwnd;
            uint pid = Native.PidOf(hwnd);
            if (pid == gamePid)
            {
                attempts = 0;
                lastGameUtc = DateTime.UtcNow;
                return;
            }
            if (sawGame && !manualPause && !intentionalPause)
                Log("前台被切换 → " + Native.ProcessName(pid) + " [PID " + pid + "] " + Native.TitleOf(hwnd));
        }

        private bool TryReacquireGameWindow()
        {
            if (gameHandle != IntPtr.Zero && Native.IsWindow(gameHandle) &&
                Native.PidOf(gameHandle) == gamePid) return true;

            foreach (GameWindow candidate in VisibleWindows())
            {
                if (candidate.Pid == gamePid)
                {
                    gameHandle = candidate.Handle;
                    Log("游戏窗口句柄改变，已自动重新关联：" + candidate.Title);
                    return true;
                }
            }
            return false;
        }

        private static bool CriticalWindow(uint pid)
        {
            string proc = Native.ProcessName(pid).ToLowerInvariant();
            return proc == "consent.exe" || proc == "logonui.exe" ||
                proc == "credentialuibroker.exe" || proc == "taskmgr.exe";
        }

        private void OnTick()
        {
            if (!running || manualPause) return;
            if (!TryReacquireGameWindow())
            {
                StopGuard("游戏窗口已关闭，守护自动停止");
                return;
            }

            IntPtr current = Native.GetForegroundWindow();
            if (current == IntPtr.Zero) return; // e.g. desktop/session transition
            uint pid = Native.PidOf(current);
            DateTime now = DateTime.UtcNow;
            bool gameIsForeground = pid == gamePid;

            // Observe common deliberate switch shortcuts. This is sampling, not a
            // global keyboard hook; unusually quick shortcuts may be missed.
            bool altTab = Native.IsDown(0x12) && Native.IsDown(0x09);
            bool winTab = (Native.IsDown(0x5B) || Native.IsDown(0x5C)) && Native.IsDown(0x09);
            bool ctrlEsc = Native.IsDown(0x11) && Native.IsDown(0x1B);
            if ((altTab || winTab || ctrlEsc) && sawGame &&
                (wasGameLastTick || gameIsForeground ||
                 (now - lastGameUtc).TotalMilliseconds < 350))
                lastShortcutUtc = now;

            if (gameIsForeground)
            {
                if (!sawGame || intentionalPause)
                    Log(intentionalPause ? "已回到游戏；自动恢复守护" : "游戏已进入前台；守护开始生效");
                sawGame = true;
                intentionalPause = false;
                wasGameLastTick = true;
                lastGameUtc = now;
                attempts = 0;
                RecordForeground(current);
                SetState(monitorOnlyBox.Checked ? "观察中（不抢焦点）" :
                    "保护中（检测到抢焦点时自动尝试恢复）", Color.DarkGreen);
                return;
            }

            wasGameLastTick = false;
            RecordForeground(current);
            if (!sawGame)
            {
                SetState("等待游戏进入前台", Color.DarkBlue);
                return;
            }

            if ((now - lastShortcutUtc).TotalMilliseconds >= 0 &&
                (now - lastShortcutUtc).TotalMilliseconds < 850 && !intentionalPause)
            {
                intentionalPause = true;
                Log("检测到主动切换快捷键，暂停恢复；回到游戏会自动继续");
            }

            // Clicking the guard itself should never make it fight its own UI.
            if (pid == selfPid && !intentionalPause)
            {
                intentionalPause = true;
                Log("用户正在操作守护工具，暂停自动恢复；回到游戏会自动继续");
            }

            if (intentionalPause)
            {
                SetState("主动切出，恢复暂停；回游戏后自动继续", Color.DarkGoldenrod);
                return;
            }

            if (CriticalWindow(pid))
            {
                intentionalPause = true;
                Log("系统安全/任务管理器窗口获得前台，不执行焦点恢复");
                SetState("系统重要窗口活动中，恢复已暂停", Color.DarkGoldenrod);
                return;
            }

            if (monitorOnlyBox.Checked)
            {
                SetState("已记录焦点切换（观察模式）", Color.DarkBlue);
                return;
            }

            if (attempts >= 4)
            {
                SetState("本轮恢复未成功（已停止重试，以免反复闪屏）", Color.Firebrick);
                return;
            }

            int[] delayMs = { 70, 180, 380, 750 };
            if (attempts == 0 || (now - lastAttemptUtc).TotalMilliseconds >= delayMs[attempts])
                Recover(current);
        }

        private void Recover(IntPtr previousForeground)
        {
            attempts++;
            lastAttemptUtc = DateTime.UtcNow;
            bool restoredMinimized = false;
            if (restoreBox.Checked && Native.IsIconic(gameHandle))
            {
                Native.ShowWindowAsync(gameHandle, Native.SW_RESTORE);
                restoredMinimized = true;
            }

            bool apiAccepted = Native.SetForegroundWindow(gameHandle);
            bool success = Native.PidOf(Native.GetForegroundWindow()) == gamePid;

            if (!success && enhancedBox.Checked)
            {
                EnhancedRecover(previousForeground);
                success = Native.PidOf(Native.GetForegroundWindow()) == gamePid;
            }

            Log("恢复尝试 " + attempts + "/4：" +
                (success ? "已回到游戏" : "未能立即恢复") +
                "（普通调用=" + (apiAccepted ? "接受" : "拒绝") +
                (restoredMinimized ? "，已请求恢复最小化窗口" : "") + "）");
            if (success)
            {
                attempts = 0;
                SetState("保护中（已恢复游戏焦点）", Color.DarkGreen);
            }
        }

        private void EnhancedRecover(IntPtr previousForeground)
        {
            uint ourThread = Native.GetCurrentThreadId();
            uint foregroundPid;
            uint foregroundThread = Native.GetWindowThreadProcessId(previousForeground,
                out foregroundPid);
            uint targetPid;
            uint targetThread = Native.GetWindowThreadProcessId(gameHandle, out targetPid);
            bool attachedForeground = false;
            bool attachedTarget = false;

            try
            {
                if (foregroundThread != 0 && foregroundThread != ourThread)
                    attachedForeground = Native.AttachThreadInput(ourThread, foregroundThread, true);
                if (targetThread != 0 && targetThread != ourThread && targetThread != foregroundThread)
                    attachedTarget = Native.AttachThreadInput(ourThread, targetThread, true);
                Native.BringWindowToTop(gameHandle);
                Native.SetForegroundWindow(gameHandle);
            }
            finally
            {
                if (attachedTarget)
                    Native.AttachThreadInput(ourThread, targetThread, false);
                if (attachedForeground)
                    Native.AttachThreadInput(ourThread, foregroundThread, false);
            }
        }
    }

    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new GuardForm());
        }
    }
}
