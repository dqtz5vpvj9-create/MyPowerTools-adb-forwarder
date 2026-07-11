using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net.Sockets;
using System.Security.Principal;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace AdbForwarder
{
    public partial class Form1 : Form
    {
        private readonly CancellationTokenSource cancellationTokenSource = new CancellationTokenSource();
        private readonly Dictionary<string, DeviceStatus> deviceStatusMap = new Dictionary<string, DeviceStatus>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, DateTime> deviceLastSeen = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, WifiAdbRuntimeState> wifiAdbStateMap = new Dictionary<string, WifiAdbRuntimeState>(StringComparer.OrdinalIgnoreCase);

        private ForwarderConfiguration configuration;
        private bool isExiting;

        private enum DeviceStatus { Unknown, Online, Offline, Disconnected }
        private enum WifiAdbUiState { Disabled, Checking, Reachable, Recovering, Error }

        private sealed class WifiAdbRuntimeState
        {
            public WifiAdbRuntimeState(WifiAdbDeviceEntry device)
            {
                Device = device;
                State = device.Enabled ? WifiAdbUiState.Checking : WifiAdbUiState.Disabled;
                LastAction = device.Enabled ? "Waiting for first check" : "Disabled";
            }

            public WifiAdbDeviceEntry Device { get; }

            public WifiAdbUiState State { get; set; }

            public string LastAction { get; set; }

            public bool? Reachable { get; set; }
        }

        public Form1()
        {
            InitializeComponent();
        }

        private void Form1_Load(object sender, EventArgs e)
        {
            AppendText("Program started.");
            FormClosing += Form1_FormClosing;

            ConfigurationLoadResult loadResult = ForwarderConfigurationLoader.Load();
            configuration = loadResult.Configuration;
            foreach (string message in loadResult.Messages)
            {
                AppendText(message);
            }

            bool isAdmin = new WindowsPrincipal(WindowsIdentity.GetCurrent())
                .IsInRole(WindowsBuiltInRole.Administrator);
            lblAdminStatus.Text = isAdmin ? "✔ Admin" : "⚠ Not Admin";
            lblAdminStatus.ForeColor = isAdmin ? Color.Green : Color.OrangeRed;

            InitializeForwardDeviceList();
            InitializeWifiAdbList();

            HideToTray();
            notifyIcon1.DoubleClick += (s, ev) => ShowMainForm();
            notifyIcon1.ContextMenuStrip = new ContextMenuStrip();
            notifyIcon1.ContextMenuStrip.Items.Add("显示主窗口", null, (s, ev) => ShowMainForm());
            notifyIcon1.ContextMenuStrip.Items.Add("编辑配置", null, (s, ev) => EditConfigFile());
            notifyIcon1.ContextMenuStrip.Items.Add("打开配置目录", null, (s, ev) => OpenConfigDirectory());
            notifyIcon1.ContextMenuStrip.Items.Add("重载配置", null, (s, ev) => ReloadConfigurationFromUi());
            notifyIcon1.ContextMenuStrip.Items.Add(new ToolStripSeparator());
            notifyIcon1.ContextMenuStrip.Items.Add("退出", null, (s, ev) => ExitApp());

            EnsureScheduledTask();

            foreach (ForwardDeviceEntry entry in configuration.ForwardDevices)
            {
                SetupPortProxy(entry.Port);
            }

            foreach (ForwardDeviceEntry entry in configuration.ForwardDevices)
            {
                StartForwardThread(entry.DeviceId, entry.Port);
                CheckDeviceAlive(entry.DeviceId);
            }

            StartWakeupPad();
            StartWifiAdbRecoveryLoops();
        }

        private void InitializeForwardDeviceList()
        {
            deviceStatusMap.Clear();
            deviceLastSeen.Clear();
            forwardDeviceListView.Items.Clear();

            foreach (ForwardDeviceEntry entry in configuration.ForwardDevices)
            {
                deviceStatusMap[entry.DeviceId] = DeviceStatus.Unknown;
                deviceLastSeen[entry.DeviceId] = DateTime.MinValue;

                var item = new ListViewItem(new[] { entry.DeviceId, entry.Port.ToString(), "○ Unknown", string.Empty });
                item.Name = entry.DeviceId;
                item.UseItemStyleForSubItems = false;
                item.SubItems[2].ForeColor = Color.Gray;
                forwardDeviceListView.Items.Add(item);
            }

            UpdateStatusSummary();
        }

        private void InitializeWifiAdbList()
        {
            wifiAdbStateMap.Clear();
            wifiAdbListView.Items.Clear();

            foreach (WifiAdbDeviceEntry device in configuration.WifiAdbDevices)
            {
                var runtimeState = new WifiAdbRuntimeState(device);
                wifiAdbStateMap[device.Name] = runtimeState;

                var item = new ListViewItem(new[]
                {
                    device.Name,
                    $"{device.Host}:{device.Port}",
                    device.UsbSerial,
                    GetWifiStateText(runtimeState.State),
                    runtimeState.LastAction
                });
                item.Name = device.Name;
                item.UseItemStyleForSubItems = false;
                item.SubItems[3].ForeColor = GetWifiStateColor(runtimeState.State);
                wifiAdbListView.Items.Add(item);
            }

            UpdateStatusSummary();
        }

        private void ExitApp()
        {
            isExiting = true;
            cancellationTokenSource.Cancel();

            if (configuration != null)
            {
                foreach (ForwardDeviceEntry entry in configuration.ForwardDevices)
                {
                    CleanupPortProxy(entry.Port);
                }
            }

            notifyIcon1.Visible = false;
            Close();
        }

        private void SetupPortProxy(int port)
        {
            int internalPort = port + 15000;
            string cmd = $"netsh interface portproxy add v4tov4 listenport={port} listenaddress=0.0.0.0 connectport={internalPort} connectaddress=127.0.0.1";
            string output = ExecuteCommand(cmd);
            AppendText($"PortProxy setup: 0.0.0.0:{port} -> 127.0.0.1:{internalPort}: {TrimCommandOutput(output)}");
        }

        private void CleanupPortProxy(int port)
        {
            string cmd = $"netsh interface portproxy delete v4tov4 listenport={port} listenaddress=0.0.0.0";
            ExecuteCommand(cmd);
        }

        private void EnsureScheduledTask()
        {
            try
            {
                string exePath = Application.ExecutablePath;
                string cmd = $"schtasks /create /tn \"AdbForwarder\" /tr \"\\\"{exePath}\\\"\" /sc onlogon /rl highest /f";
                string output = ExecuteCommand(cmd);
                AppendText($"Scheduled task: {TrimCommandOutput(output)}");
            }
            catch (Exception ex)
            {
                AppendText($"Failed to create scheduled task: {ex.Message}");
            }
        }

        private void Form1_FormClosing(object sender, FormClosingEventArgs e)
        {
            if (isExiting)
            {
                e.Cancel = false;
                return;
            }

            e.Cancel = true;
            HideToTray();
        }

        private void HideToTray()
        {
            ShowInTaskbar = false;
            WindowState = FormWindowState.Minimized;
            Hide();
        }

        private void StartForwardThread(string deviceId, int port)
        {
            Task.Run(async () =>
            {
                while (!cancellationTokenSource.Token.IsCancellationRequested)
                {
                    try
                    {
                        int internalPort = port + 15000;
                        string cmd = $"adb -s {deviceId} wait-for-device forward tcp:{internalPort} tcp:5555";
                        string output = ExecuteCommand(cmd);
                        AppendText($"Device: {deviceId} -> 127.0.0.1:{internalPort} (exposed on 0.0.0.0:{port}) - {TrimCommandOutput(output)}");

                        if (IsPortInUse(port))
                        {
                            AppendText($"Port {port} is in use.");
                        }
                        else
                        {
                            AppendText($"Port {port} is not in use.");
                        }

                        await Task.Delay(5000, cancellationTokenSource.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        AppendText($"Error in StartThread for {deviceId}: {ex.Message}");
                        await Task.Delay(5000, cancellationTokenSource.Token);
                    }
                }
            }, cancellationTokenSource.Token);
        }

        private void CheckDeviceAlive(string deviceId)
        {
            Task.Run(async () =>
            {
                DateTime lastSeenOnline = DateTime.Now;
                Regex deviceRegex = new Regex($"{Regex.Escape(deviceId)}\\s+device");
                Regex offlineRegex = new Regex($"{Regex.Escape(deviceId)}\\s+offline");

                while (!cancellationTokenSource.Token.IsCancellationRequested)
                {
                    try
                    {
                        string output = ExecuteCommand("adb devices -l");

                        if (deviceRegex.IsMatch(output))
                        {
                            lastSeenOnline = DateTime.Now;
                            UpdateDeviceStatus(deviceId, DeviceStatus.Online, lastSeenOnline);
                        }
                        else if (offlineRegex.IsMatch(output))
                        {
                            UpdateDeviceStatus(deviceId, DeviceStatus.Offline, lastSeenOnline);
                            if ((DateTime.Now - lastSeenOnline).TotalMinutes >= 2)
                            {
                                ExecuteCommand($"adb disconnect {deviceId}");
                                await Task.Delay(60000, cancellationTokenSource.Token);
                            }
                        }
                        else if ((DateTime.Now - lastSeenOnline).TotalMinutes >= 5)
                        {
                            AppendText($"Device {deviceId} has been disconnected for more than 5 minutes.");
                            UpdateDeviceStatus(deviceId, DeviceStatus.Disconnected, lastSeenOnline);
                            lastSeenOnline = DateTime.Now;
                        }

                        await Task.Delay(5000, cancellationTokenSource.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        AppendText($"Error in CheckDeviceAlive for {deviceId}: {ex.Message}");
                        await Task.Delay(5000, cancellationTokenSource.Token);
                    }
                }
            }, cancellationTokenSource.Token);
        }

        private void StartWakeupPad()
        {
            if (string.IsNullOrWhiteSpace(configuration.WakeupPadDeviceId))
            {
                AppendText("Wakeup Pad is disabled because WakeupPad.deviceId is empty.");
                return;
            }

            Task.Run(async () =>
            {
                while (!cancellationTokenSource.Token.IsCancellationRequested)
                {
                    try
                    {
                        string cmd = $"adb -s {configuration.WakeupPadDeviceId} wait-for-device shell input keyevent KEYCODE_ENTER";
                        string output = ExecuteCommand(cmd);
                        AppendText($"Wakeup Pad ({configuration.WakeupPadDeviceId}) - {TrimCommandOutput(output)}");

                        await Task.Delay(30000, cancellationTokenSource.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        AppendText($"Error in WakeupPad: {ex.Message}");
                        await Task.Delay(30000, cancellationTokenSource.Token);
                    }
                }
            }, cancellationTokenSource.Token);
        }

        private void StartWifiAdbRecoveryLoops()
        {
            foreach (WifiAdbDeviceEntry device in configuration.WifiAdbDevices)
            {
                StartWifiAdbRecoveryLoop(device);
            }
        }

        private void StartWifiAdbRecoveryLoop(WifiAdbDeviceEntry device)
        {
            if (!device.Enabled)
            {
                AppendText($"Wi-Fi ADB device {device.Name} is disabled in devices.ini.");
                UpdateWifiAdbState(device.Name, WifiAdbUiState.Disabled, "Disabled");
                return;
            }

            AppendText($"Wi-Fi ADB recovery enabled for {device.Name} at {device.Host}:{device.Port} via USB {device.UsbSerial}.");
            UpdateWifiAdbState(device.Name, WifiAdbUiState.Checking, $"Checking {device.Host}:{device.Port}");

            Task.Run(async () =>
            {
                while (!cancellationTokenSource.Token.IsCancellationRequested)
                {
                    try
                    {
                        bool reachable = await CanConnectTcpAsync(device.Host, device.Port, 2000, cancellationTokenSource.Token);

                        if (reachable)
                        {
                            UpdateWifiAdbState(device.Name, WifiAdbUiState.Reachable, $"Reachable {device.Host}:{device.Port}");
                        }
                        else
                        {
                            UpdateWifiAdbState(device.Name, WifiAdbUiState.Recovering, $"Recovering via {device.UsbSerial}");
                            string cmd = $"adb -s {device.UsbSerial} tcp {device.Port}";
                            string output = ExecuteCommand(cmd);
                            string action = $"Ran adb tcp {device.Port}: {TrimCommandOutput(output)}";
                            UpdateWifiAdbState(device.Name, WifiAdbUiState.Recovering, action);
                            AppendText($"Wi-Fi ADB recovery executed for {device.Name}: {cmd} -> {TrimCommandOutput(output)}");
                        }

                        await Task.Delay(TimeSpan.FromSeconds(device.IntervalSeconds), cancellationTokenSource.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        UpdateWifiAdbState(device.Name, WifiAdbUiState.Error, ex.Message);
                        AppendText($"Error in Wi-Fi ADB recovery for {device.Name}: {ex.Message}");
                        await Task.Delay(TimeSpan.FromSeconds(device.IntervalSeconds), cancellationTokenSource.Token);
                    }
                }
            }, cancellationTokenSource.Token);
        }

        private async Task<bool> CanConnectTcpAsync(string host, int port, int timeoutMilliseconds, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                using (var client = new TcpClient())
                {
                    Task connectTask = client.ConnectAsync(host, port);
                    Task timeoutTask = Task.Delay(timeoutMilliseconds, cancellationToken);
                    Task completedTask = await Task.WhenAny(connectTask, timeoutTask);

                    if (completedTask != connectTask)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        return false;
                    }

                    await connectTask;
                    return client.Connected;
                }
            }
            catch (SocketException)
            {
                return false;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }

        private bool IsPortInUse(int port)
        {
            string cmd = $"netstat -an | find \"{port}\"";
            string output = ExecuteCommand(cmd);
            return !string.IsNullOrEmpty(output);
        }

        private string ExecuteCommand(string cmd)
        {
            try
            {
                using (var process = new Process())
                {
                    process.StartInfo.FileName = "cmd.exe";
                    process.StartInfo.Arguments = $"/C {cmd}";
                    process.StartInfo.RedirectStandardOutput = true;
                    process.StartInfo.RedirectStandardError = true;
                    process.StartInfo.UseShellExecute = false;
                    process.StartInfo.CreateNoWindow = true;
                    process.Start();

                    string output = process.StandardOutput.ReadToEnd();
                    string error = process.StandardError.ReadToEnd();
                    process.WaitForExit();

                    return string.IsNullOrEmpty(error) ? output : $"Error: {error}";
                }
            }
            catch (InvalidOperationException ex)
            {
                return $"InvalidOperationException: {ex.Message}";
            }
            catch (Win32Exception ex)
            {
                return $"Win32Exception: {ex.Message}";
            }
            catch (Exception ex)
            {
                return $"Exception: {ex.Message}";
            }
        }

        private static string TrimCommandOutput(string output)
        {
            return string.IsNullOrWhiteSpace(output) ? "(no output)" : output.Trim();
        }

        private void AppendText(string text)
        {
            if (InvokeRequired)
            {
                Invoke(new Action<string>(AppendText), text);
            }
            else
            {
                richTextBox1.AppendText($"{DateTime.Now}: {text}{Environment.NewLine}");

                if (richTextBox1.Lines.Length > 1000)
                {
                    string[] lines = richTextBox1.Lines;
                    var newLines = new string[500];
                    Array.Copy(lines, lines.Length - 500, newLines, 0, 500);
                    richTextBox1.Lines = newLines;
                    richTextBox1.SelectionStart = richTextBox1.Text.Length;
                    richTextBox1.ScrollToCaret();
                }
            }
        }

        private void ClearText()
        {
            if (InvokeRequired)
            {
                Invoke(new Action(ClearText));
            }
            else
            {
                richTextBox1.Clear();
            }
        }

        private void ShowMainForm()
        {
            ShowInTaskbar = true;
            WindowState = FormWindowState.Normal;
            Show();
            BringToFront();
            Activate();
        }

        private void btnEditConfig_Click(object sender, EventArgs e)
        {
            EditConfigFile();
        }

        private void btnOpenConfigDir_Click(object sender, EventArgs e)
        {
            OpenConfigDirectory();
        }

        private void btnReloadConfig_Click(object sender, EventArgs e)
        {
            ReloadConfigurationFromUi();
        }

        private void btnClearLog_Click(object sender, EventArgs e)
        {
            ClearText();
        }

        private void EditConfigFile()
        {
            try
            {
                ForwarderConfigurationLoader.EnsureConfigFileExists(configuration);
                Process.Start(new ProcessStartInfo
                {
                    FileName = configuration.ConfigPath,
                    UseShellExecute = true
                });
                AppendText($"Opened config file: {configuration.ConfigPath}");
            }
            catch (Exception ex)
            {
                AppendText($"Failed to open config file: {ex.Message}");
                MessageBox.Show(this, $"Failed to open config file.{Environment.NewLine}{ex.Message}", "ADB Forwarder", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void OpenConfigDirectory()
        {
            try
            {
                ForwarderConfigurationLoader.EnsureConfigFileExists(configuration);
                string configDirectory = Path.GetDirectoryName(configuration.ConfigPath);
                if (string.IsNullOrWhiteSpace(configDirectory))
                {
                    throw new InvalidOperationException("Unable to resolve configuration directory.");
                }

                Process.Start(new ProcessStartInfo
                {
                    FileName = configDirectory,
                    UseShellExecute = true
                });
                AppendText($"Opened config directory: {configDirectory}");
            }
            catch (Exception ex)
            {
                AppendText($"Failed to open config directory: {ex.Message}");
                MessageBox.Show(this, $"Failed to open config directory.{Environment.NewLine}{ex.Message}", "ADB Forwarder", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void ReloadConfigurationFromUi()
        {
            try
            {
                ConfigurationLoadResult loadResult = ForwarderConfigurationLoader.Load();
                configuration = loadResult.Configuration;
                foreach (string message in loadResult.Messages)
                {
                    AppendText(message);
                }

                InitializeForwardDeviceList();
                InitializeWifiAdbList();

                MessageBox.Show(
                    this,
                    "Configuration reloaded in the UI. Running forward and Wi-Fi recovery background tasks are not hot-swapped; restart the app to fully apply device list changes and intervals.",
                    "ADB Forwarder",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                AppendText($"Failed to reload config: {ex.Message}");
                MessageBox.Show(this, $"Failed to reload config.{Environment.NewLine}{ex.Message}", "ADB Forwarder", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void UpdateDeviceStatus(string deviceId, DeviceStatus status, DateTime lastSeen)
        {
            if (InvokeRequired)
            {
                Invoke(new Action<string, DeviceStatus, DateTime>(UpdateDeviceStatus), deviceId, status, lastSeen);
                return;
            }

            deviceStatusMap[deviceId] = status;
            deviceLastSeen[deviceId] = lastSeen;

            ListView.ListViewItemCollection items = forwardDeviceListView.Items;
            for (int i = 0; i < items.Count; i++)
            {
                if (items[i].Name == deviceId)
                {
                    string statusText;
                    Color statusColor;
                    switch (status)
                    {
                        case DeviceStatus.Online:
                            statusText = "● Online";
                            statusColor = Color.Green;
                            break;
                        case DeviceStatus.Offline:
                            statusText = "● Offline";
                            statusColor = Color.OrangeRed;
                            break;
                        case DeviceStatus.Disconnected:
                            statusText = "● Disconnected";
                            statusColor = Color.Red;
                            break;
                        default:
                            statusText = "○ Unknown";
                            statusColor = Color.Gray;
                            break;
                    }

                    items[i].SubItems[2].Text = statusText;
                    items[i].SubItems[2].ForeColor = statusColor;
                    items[i].SubItems[3].Text = lastSeen == DateTime.MinValue ? string.Empty : lastSeen.ToString("yyyy-MM-dd HH:mm:ss");
                    break;
                }
            }

            UpdateStatusSummary();
        }

        private void UpdateWifiAdbState(string deviceName, WifiAdbUiState state, string lastAction)
        {
            if (InvokeRequired)
            {
                Invoke(new Action<string, WifiAdbUiState, string>(UpdateWifiAdbState), deviceName, state, lastAction);
                return;
            }

            if (!wifiAdbStateMap.TryGetValue(deviceName, out WifiAdbRuntimeState runtimeState))
            {
                return;
            }

            runtimeState.State = state;
            runtimeState.LastAction = lastAction;

            ListView.ListViewItemCollection items = wifiAdbListView.Items;
            for (int i = 0; i < items.Count; i++)
            {
                if (items[i].Name == deviceName)
                {
                    items[i].SubItems[3].Text = GetWifiStateText(state);
                    items[i].SubItems[3].ForeColor = GetWifiStateColor(state);
                    items[i].SubItems[4].Text = lastAction;
                    break;
                }
            }

            UpdateStatusSummary();
        }

        private void UpdateStatusSummary()
        {
            int forwardOnline = 0;
            foreach (DeviceStatus status in deviceStatusMap.Values)
            {
                if (status == DeviceStatus.Online)
                {
                    forwardOnline++;
                }
            }

            int wifiEnabled = 0;
            int wifiReachable = 0;
            int wifiRecovering = 0;
            int wifiError = 0;
            foreach (WifiAdbRuntimeState runtimeState in wifiAdbStateMap.Values)
            {
                if (!runtimeState.Device.Enabled)
                {
                    continue;
                }

                wifiEnabled++;
                if (runtimeState.State == WifiAdbUiState.Reachable)
                {
                    wifiReachable++;
                }
                else if (runtimeState.State == WifiAdbUiState.Recovering)
                {
                    wifiRecovering++;
                }
                else if (runtimeState.State == WifiAdbUiState.Error)
                {
                    wifiError++;
                }
            }

            int forwardTotal = configuration == null ? 0 : configuration.ForwardDevices.Count;
            int wifiTotal = configuration == null ? 0 : configuration.WifiAdbDevices.Count;
            lblDeviceCount.Text = $"Forward: {forwardOnline}/{forwardTotal} online";
            lblWifiDeviceCount.Text = $"Wi-Fi ADB: {wifiReachable}/{wifiEnabled} reachable, {wifiRecovering} recovering, {wifiError} error, {wifiTotal} total";
            notifyIcon1.Text = BuildTrayText(forwardOnline, forwardTotal, wifiReachable, wifiRecovering, wifiError, wifiTotal);
        }

        private static string BuildTrayText(int forwardOnline, int forwardTotal, int wifiReachable, int wifiRecovering, int wifiError, int wifiTotal)
        {
            string text = $"ADB Forwarder F:{forwardOnline}/{forwardTotal} W:{wifiReachable}/{wifiTotal} R:{wifiRecovering} E:{wifiError}";
            return text.Length <= 63 ? text : $"ADB F:{forwardOnline}/{forwardTotal} W:{wifiReachable}/{wifiTotal}";
        }

        private static string GetWifiStateText(WifiAdbUiState state)
        {
            switch (state)
            {
                case WifiAdbUiState.Reachable:
                    return "● Reachable";
                case WifiAdbUiState.Recovering:
                    return "● Recovering";
                case WifiAdbUiState.Error:
                    return "● Error";
                case WifiAdbUiState.Checking:
                    return "○ Checking";
                default:
                    return "○ Disabled";
            }
        }

        private static Color GetWifiStateColor(WifiAdbUiState state)
        {
            switch (state)
            {
                case WifiAdbUiState.Reachable:
                    return Color.Green;
                case WifiAdbUiState.Recovering:
                    return Color.OrangeRed;
                case WifiAdbUiState.Error:
                    return Color.Red;
                case WifiAdbUiState.Checking:
                    return Color.DodgerBlue;
                default:
                    return Color.Gray;
            }
        }
    }
}
