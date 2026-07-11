using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace AdbForwarder
{
    internal sealed class ForwardDeviceEntry
    {
        public ForwardDeviceEntry(string deviceId, int port)
        {
            DeviceId = deviceId;
            Port = port;
        }

        public string DeviceId { get; }

        public int Port { get; }
    }

    internal sealed class WifiAdbDeviceEntry
    {
        public WifiAdbDeviceEntry(string name, bool enabled, string usbSerial, string host, int port, int intervalSeconds)
        {
            Name = name;
            Enabled = enabled;
            UsbSerial = usbSerial;
            Host = host;
            Port = port;
            IntervalSeconds = intervalSeconds;
        }

        public string Name { get; }

        public bool Enabled { get; }

        public string UsbSerial { get; }

        public string Host { get; }

        public int Port { get; }

        public int IntervalSeconds { get; }
    }

    internal sealed class ForwarderConfiguration
    {
        public ForwarderConfiguration(
            string configPath,
            IList<ForwardDeviceEntry> forwardDevices,
            string wakeupPadDeviceId,
            IList<WifiAdbDeviceEntry> wifiAdbDevices)
        {
            ConfigPath = configPath;
            ForwardDevices = forwardDevices ?? throw new ArgumentNullException(nameof(forwardDevices));
            WakeupPadDeviceId = wakeupPadDeviceId ?? string.Empty;
            WifiAdbDevices = wifiAdbDevices ?? throw new ArgumentNullException(nameof(wifiAdbDevices));
        }

        public string ConfigPath { get; }

        public IList<ForwardDeviceEntry> ForwardDevices { get; }

        public string WakeupPadDeviceId { get; }

        public IList<WifiAdbDeviceEntry> WifiAdbDevices { get; }
    }

    internal sealed class ConfigurationLoadResult
    {
        public ConfigurationLoadResult(ForwarderConfiguration configuration, IList<string> messages)
        {
            Configuration = configuration;
            Messages = messages ?? throw new ArgumentNullException(nameof(messages));
        }

        public ForwarderConfiguration Configuration { get; }

        public IList<string> Messages { get; }
    }

    internal static class ForwarderConfigurationLoader
    {
        private const string AppDirectoryName = "AdbForwarder";
        private const string ConfigFileName = "devices.ini";
        private const string WifiAdbSectionPrefix = "WifiAdb:";
        private const string LegacyWifiRecoverySectionName = "WifiRecovery";
        private const string LegacyWifiRecoveryDeviceName = "Pixel 9a";

        public static ConfigurationLoadResult Load()
        {
            string configPath = GetConfigPath();
            var messages = new List<string>
            {
                $"Configuration path: {configPath}"
            };

            ForwarderConfiguration defaultConfiguration = CreateDefaultConfiguration(configPath);

            try
            {
                string configDirectory = Path.GetDirectoryName(configPath);
                if (string.IsNullOrWhiteSpace(configDirectory))
                {
                    throw new InvalidOperationException("Unable to resolve the configuration directory.");
                }

                Directory.CreateDirectory(configDirectory);

                if (!File.Exists(configPath))
                {
                    WriteConfiguration(defaultConfiguration);
                    messages.Add("Configuration file not found. Created the default devices.ini in LocalAppData.");
                    return new ConfigurationLoadResult(defaultConfiguration, messages);
                }

                string[] lines = File.ReadAllLines(configPath, Encoding.UTF8);
                ForwarderConfiguration configuration = Parse(lines, configPath);
                messages.Add("Loaded configuration from LocalAppData.");
                messages.Add(BuildWifiAdbDeviceSummary(configuration));
                return new ConfigurationLoadResult(configuration, messages);
            }
            catch (Exception ex)
            {
                messages.Add($"Failed to load devices.ini. Falling back to built-in defaults. {ex.Message}");
                messages.Add(BuildWifiAdbDeviceSummary(defaultConfiguration));
                return new ConfigurationLoadResult(defaultConfiguration, messages);
            }
        }

        public static void EnsureConfigFileExists(ForwarderConfiguration configuration)
        {
            if (configuration == null)
            {
                throw new ArgumentNullException(nameof(configuration));
            }

            if (!File.Exists(configuration.ConfigPath))
            {
                WriteConfiguration(configuration);
            }
        }

        public static void WriteConfiguration(ForwarderConfiguration configuration)
        {
            if (configuration == null)
            {
                throw new ArgumentNullException(nameof(configuration));
            }

            string configDirectory = Path.GetDirectoryName(configuration.ConfigPath);
            if (string.IsNullOrWhiteSpace(configDirectory))
            {
                throw new InvalidOperationException("Unable to resolve the configuration directory.");
            }

            Directory.CreateDirectory(configDirectory);
            File.WriteAllText(configuration.ConfigPath, BuildIni(configuration), Encoding.UTF8);
        }

        private static string GetConfigPath()
        {
            string baseDirectory = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(baseDirectory, AppDirectoryName, ConfigFileName);
        }

        private static ForwarderConfiguration CreateDefaultConfiguration(string configPath)
        {
            return new ForwarderConfiguration(
                configPath,
                new List<ForwardDeviceEntry>
                {
                    new ForwardDeviceEntry("0B101JEC203155", 15555),
                    new ForwardDeviceEntry("14141JEC207961", 15556),
                    new ForwardDeviceEntry("08191JEC213478", 15557),
                    new ForwardDeviceEntry("08111JEC207803", 15558),
                },
                "T81164GB23224415932",
                new List<WifiAdbDeviceEntry>
                {
                    new WifiAdbDeviceEntry("Pixel 9a", true, "45221FDAQ0007L", "10.33.0.243", 5555, 30)
                });
        }

        private static ForwarderConfiguration Parse(string[] lines, string configPath)
        {
            var forwardDevices = new List<ForwardDeviceEntry>();
            var forwardDeviceIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var forwardPorts = new HashSet<int>();
            var wifiAdbDevices = new List<WifiAdbDeviceEntry>();
            var wifiDeviceNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string wakeupPadDeviceId = null;
            string currentSection = string.Empty;
            Dictionary<string, string> currentWifiSectionValues = null;

            for (int i = 0; i < lines.Length; i++)
            {
                string rawLine = lines[i];
                string line = rawLine.Trim();

                if (string.IsNullOrWhiteSpace(line) || line.StartsWith(";") || line.StartsWith("#"))
                {
                    continue;
                }

                if (line.StartsWith("[") && line.EndsWith("]"))
                {
                    FinalizeWifiSection(currentSection, currentWifiSectionValues, wifiAdbDevices, wifiDeviceNames);

                    currentSection = line.Substring(1, line.Length - 2).Trim();
                    currentWifiSectionValues = IsWifiSection(currentSection)
                        ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                        : null;
                    continue;
                }

                int separatorIndex = line.IndexOf('=');
                if (separatorIndex <= 0)
                {
                    throw new FormatException($"Invalid INI entry on line {i + 1}: '{rawLine}'.");
                }

                string key = line.Substring(0, separatorIndex).Trim();
                string value = line.Substring(separatorIndex + 1).Trim();

                if (string.IsNullOrWhiteSpace(currentSection))
                {
                    throw new FormatException($"Key '{key}' on line {i + 1} is not inside a section.");
                }

                if (currentSection.Equals("ForwardDevices", StringComparison.OrdinalIgnoreCase))
                {
                    ParseForwardDevice(key, value, forwardDevices, forwardDeviceIds, forwardPorts, i + 1);
                    continue;
                }

                if (currentSection.Equals("WakeupPad", StringComparison.OrdinalIgnoreCase))
                {
                    if (!key.Equals("deviceId", StringComparison.OrdinalIgnoreCase))
                    {
                        throw new FormatException($"Unknown key '{key}' in [WakeupPad] on line {i + 1}.");
                    }

                    wakeupPadDeviceId = value;
                    continue;
                }

                if (IsWifiSection(currentSection))
                {
                    if (currentWifiSectionValues.ContainsKey(key))
                    {
                        throw new FormatException($"Duplicate key '{key}' in [{currentSection}] on line {i + 1}.");
                    }

                    currentWifiSectionValues[key] = value;
                    continue;
                }

                throw new FormatException($"Unknown section [{currentSection}] on line {i + 1}.");
            }

            FinalizeWifiSection(currentSection, currentWifiSectionValues, wifiAdbDevices, wifiDeviceNames);

            if (wakeupPadDeviceId == null)
            {
                throw new FormatException("Missing required key 'deviceId' in [WakeupPad].");
            }

            return new ForwarderConfiguration(configPath, forwardDevices, wakeupPadDeviceId, wifiAdbDevices);
        }

        private static void ParseForwardDevice(
            string deviceId,
            string value,
            IList<ForwardDeviceEntry> forwardDevices,
            ISet<string> forwardDeviceIds,
            ISet<int> forwardPorts,
            int lineNumber)
        {
            if (string.IsNullOrWhiteSpace(deviceId))
            {
                throw new FormatException($"Forward device id cannot be empty on line {lineNumber}.");
            }

            if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int port) ||
                port <= 0 ||
                port > 65535)
            {
                throw new FormatException($"Invalid port '{value}' for device '{deviceId}' on line {lineNumber}.");
            }

            if (!forwardDeviceIds.Add(deviceId))
            {
                throw new FormatException($"Duplicate forward device '{deviceId}' on line {lineNumber}.");
            }

            if (!forwardPorts.Add(port))
            {
                throw new FormatException($"Duplicate forward port '{port}' on line {lineNumber}.");
            }

            forwardDevices.Add(new ForwardDeviceEntry(deviceId, port));
        }

        private static bool IsWifiSection(string sectionName)
        {
            return IsNamedWifiAdbSection(sectionName) || IsLegacyWifiRecoverySection(sectionName);
        }

        private static bool IsNamedWifiAdbSection(string sectionName)
        {
            return sectionName.StartsWith(WifiAdbSectionPrefix, StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsLegacyWifiRecoverySection(string sectionName)
        {
            return sectionName.Equals(LegacyWifiRecoverySectionName, StringComparison.OrdinalIgnoreCase);
        }

        private static void FinalizeWifiSection(
            string currentSection,
            IDictionary<string, string> sectionValues,
            IList<WifiAdbDeviceEntry> wifiAdbDevices,
            ISet<string> wifiDeviceNames)
        {
            if (!IsWifiSection(currentSection))
            {
                return;
            }

            if (sectionValues == null)
            {
                throw new FormatException($"Section [{currentSection}] is malformed.");
            }

            string name = GetWifiDeviceName(currentSection, sectionValues);
            if (string.IsNullOrWhiteSpace(name))
            {
                throw new FormatException($"Wi-Fi ADB section name cannot be empty in [{currentSection}].");
            }

            if (!wifiDeviceNames.Add(name))
            {
                throw new FormatException($"Duplicate Wi-Fi ADB device '{name}'.");
            }

            wifiAdbDevices.Add(ParseWifiDevice(name, sectionValues));
        }

        private static string GetWifiDeviceName(string sectionName, IDictionary<string, string> sectionValues)
        {
            if (IsNamedWifiAdbSection(sectionName))
            {
                return sectionName.Substring(WifiAdbSectionPrefix.Length).Trim();
            }

            if (IsLegacyWifiRecoverySection(sectionName))
            {
                if (sectionValues.TryGetValue("name", out string configuredName) &&
                    !string.IsNullOrWhiteSpace(configuredName))
                {
                    return configuredName.Trim();
                }

                return LegacyWifiRecoveryDeviceName;
            }

            return string.Empty;
        }

        private static WifiAdbDeviceEntry ParseWifiDevice(string name, IDictionary<string, string> values)
        {
            bool enabled = ParseBoolean(GetRequiredValue(values, "enabled", name), 0);
            string usbSerial = GetRequiredValue(values, "usbSerial", name);
            string host = GetRequiredValue(values, "host", name);
            int port = ParsePort(GetRequiredValue(values, "port", name), 0, $"{name}.port");
            int intervalSeconds = ParsePositiveInteger(GetRequiredValue(values, "intervalSeconds", name), 0, $"{name}.intervalSeconds");

            if (string.IsNullOrWhiteSpace(usbSerial))
            {
                throw new FormatException($"{name}.usbSerial cannot be empty.");
            }

            if (string.IsNullOrWhiteSpace(host))
            {
                throw new FormatException($"{name}.host cannot be empty.");
            }

            return new WifiAdbDeviceEntry(name, enabled, usbSerial, host, port, intervalSeconds);
        }

        private static string GetRequiredValue(IDictionary<string, string> values, string key, string deviceName)
        {
            if (!values.TryGetValue(key, out string value))
            {
                throw new FormatException($"Missing key '{key}' in [WifiAdb:{deviceName}].");
            }

            return value;
        }

        private static bool ParseBoolean(string value, int lineNumber)
        {
            if (value.Equals("true", StringComparison.OrdinalIgnoreCase) ||
                value.Equals("1", StringComparison.OrdinalIgnoreCase) ||
                value.Equals("yes", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (value.Equals("false", StringComparison.OrdinalIgnoreCase) ||
                value.Equals("0", StringComparison.OrdinalIgnoreCase) ||
                value.Equals("no", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (lineNumber > 0)
            {
                throw new FormatException($"Invalid boolean value '{value}' on line {lineNumber}.");
            }

            throw new FormatException($"Invalid boolean value '{value}'.");
        }

        private static int ParsePort(string value, int lineNumber, string settingName)
        {
            if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int port) ||
                port <= 0 ||
                port > 65535)
            {
                if (lineNumber > 0)
                {
                    throw new FormatException($"Invalid port '{value}' for {settingName} on line {lineNumber}.");
                }

                throw new FormatException($"Invalid port '{value}' for {settingName}.");
            }

            return port;
        }

        private static int ParsePositiveInteger(string value, int lineNumber, string settingName)
        {
            if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedValue) ||
                parsedValue <= 0)
            {
                if (lineNumber > 0)
                {
                    throw new FormatException($"Invalid positive integer '{value}' for {settingName} on line {lineNumber}.");
                }

                throw new FormatException($"Invalid positive integer '{value}' for {settingName}.");
            }

            return parsedValue;
        }

        private static string BuildIni(ForwarderConfiguration configuration)
        {
            var builder = new StringBuilder();
            builder.AppendLine("; AdbForwarder device configuration");
            builder.AppendLine("; Stored in %LocalAppData%\\AdbForwarder\\devices.ini");
            builder.AppendLine();
            builder.AppendLine("[ForwardDevices]");
            foreach (ForwardDeviceEntry device in configuration.ForwardDevices)
            {
                builder.AppendLine($"{device.DeviceId}={device.Port.ToString(CultureInfo.InvariantCulture)}");
            }

            builder.AppendLine();
            builder.AppendLine("[WakeupPad]");
            builder.AppendLine($"deviceId={configuration.WakeupPadDeviceId}");

            foreach (WifiAdbDeviceEntry device in configuration.WifiAdbDevices)
            {
                builder.AppendLine();
                builder.AppendLine($"[WifiAdb:{device.Name}]");
                builder.AppendLine($"enabled={device.Enabled.ToString().ToLowerInvariant()}");
                builder.AppendLine($"usbSerial={device.UsbSerial}");
                builder.AppendLine($"host={device.Host}");
                builder.AppendLine($"port={device.Port.ToString(CultureInfo.InvariantCulture)}");
                builder.AppendLine($"intervalSeconds={device.IntervalSeconds.ToString(CultureInfo.InvariantCulture)}");
            }

            return builder.ToString();
        }

        private static string BuildWifiAdbDeviceSummary(ForwarderConfiguration configuration)
        {
            if (configuration.WifiAdbDevices.Count == 0)
            {
                return "Wi-Fi ADB devices loaded: none.";
            }

            var builder = new StringBuilder("Wi-Fi ADB devices loaded: ");
            for (int i = 0; i < configuration.WifiAdbDevices.Count; i++)
            {
                WifiAdbDeviceEntry device = configuration.WifiAdbDevices[i];
                if (i > 0)
                {
                    builder.Append(", ");
                }

                builder.Append(device.Name);
                builder.Append('=');
                builder.Append(device.Host);
                builder.Append(':');
                builder.Append(device.Port.ToString(CultureInfo.InvariantCulture));
            }

            builder.Append('.');
            return builder.ToString();
        }
    }
}
