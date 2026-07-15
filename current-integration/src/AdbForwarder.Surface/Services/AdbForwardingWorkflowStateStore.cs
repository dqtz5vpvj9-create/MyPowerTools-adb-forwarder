using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AdbForwarder.Surface.Services;

public sealed record AdbForwarderPersistedWorkflowState(
    AdbForwardRequest Request,
    AdbForwardCleanupState Cleanup,
    bool ApprovalRequired,
    DateTimeOffset UpdatedAt);

public interface IAdbForwarderWorkflowStateStore
{
    AdbForwarderPersistedWorkflowState? Load();
    void Save(AdbForwarderPersistedWorkflowState state);
    void Clear();
}

public sealed class AdbForwarderWorkflowStateStore : IAdbForwarderWorkflowStateStore
{
    private const int CurrentSchemaVersion = 2;
    private const string WindowsProtection = "dpapi-current-user";
    private const string PortableProtection = "user-bound-aes-gcm";
    private const uint CryptProtectUiForbidden = 0x1;
    private static readonly byte[] AdditionalEntropy = Encoding.UTF8.GetBytes(
        "MyPowerTools/adb-forwarder/workflow-state/v2");
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly object _gate = new();
    private readonly string _path;

    public AdbForwarderWorkflowStateStore(string? path = null)
    {
        _path = path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MyPowerTools",
            "adb-forwarder",
            "workflow-state.json");
    }

    public AdbForwarderPersistedWorkflowState? Load()
    {
        lock (_gate)
        {
            try
            {
                if (!File.Exists(_path))
                {
                    return null;
                }

                var json = File.ReadAllText(_path, Encoding.UTF8);
                var envelope = JsonSerializer.Deserialize<ProtectedStateEnvelope>(json, JsonOptions);
                if (envelope is { SchemaVersion: CurrentSchemaVersion, Payload.Length: > 0 })
                {
                    var protectedPayload = Convert.FromBase64String(envelope.Payload);
                    var plaintext = envelope.Protection switch
                    {
                        WindowsProtection when OperatingSystem.IsWindows() => UnprotectForCurrentWindowsUser(protectedPayload),
                        PortableProtection => UnprotectPortable(protectedPayload),
                        _ => throw new CryptographicException("ADB 工作流状态使用了未知保护格式。")
                    };
                    return JsonSerializer.Deserialize<AdbForwarderPersistedWorkflowState>(plaintext, JsonOptions);
                }

                // Migrate the former plaintext format immediately after a successful read.
                var legacy = JsonSerializer.Deserialize<AdbForwarderPersistedWorkflowState>(json, JsonOptions);
                if (legacy is not null)
                {
                    WriteProtectedState(legacy);
                }
                return legacy;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or
                                       CryptographicException or FormatException or Win32Exception)
            {
                return null;
            }
        }
    }

    public void Save(AdbForwarderPersistedWorkflowState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        lock (_gate)
        {
            WriteProtectedState(state);
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            try
            {
                File.Delete(_path);
            }
            catch (IOException)
            {
            }
        }
    }

    private void WriteProtectedState(AdbForwarderPersistedWorkflowState state)
    {
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(state, JsonOptions);
        var protection = OperatingSystem.IsWindows() ? WindowsProtection : PortableProtection;
        var protectedPayload = OperatingSystem.IsWindows()
            ? ProtectForCurrentWindowsUser(plaintext)
            : ProtectPortable(plaintext);
        var envelope = new ProtectedStateEnvelope(
            CurrentSchemaVersion,
            protection,
            Convert.ToBase64String(protectedPayload));

        var directory = Path.GetDirectoryName(_path)
            ?? throw new InvalidOperationException("ADB 工作流状态路径缺少目录。");
        Directory.CreateDirectory(directory);
        var temporary = _path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(
                temporary,
                JsonSerializer.Serialize(envelope, JsonOptions),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.Move(temporary, _path, overwrite: true);
        }
        finally
        {
            try
            {
                File.Delete(temporary);
            }
            catch (IOException)
            {
            }
        }
    }

    private static byte[] ProtectForCurrentWindowsUser(byte[] plaintext)
    {
        return TransformWithDpapi(plaintext, protect: true);
    }

    private static byte[] UnprotectForCurrentWindowsUser(byte[] protectedPayload)
    {
        return TransformWithDpapi(protectedPayload, protect: false);
    }

    private static byte[] TransformWithDpapi(byte[] input, bool protect)
    {
        var inputBlob = CreateBlob(input);
        var entropyBlob = CreateBlob(AdditionalEntropy);
        DataBlob outputBlob = default;
        IntPtr description = IntPtr.Zero;
        try
        {
            var succeeded = protect
                ? CryptProtectData(
                    ref inputBlob,
                    "MyPowerTools ADB forwarding workflow state",
                    ref entropyBlob,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    CryptProtectUiForbidden,
                    out outputBlob)
                : CryptUnprotectData(
                    ref inputBlob,
                    out description,
                    ref entropyBlob,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    CryptProtectUiForbidden,
                    out outputBlob);
            if (!succeeded)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            var output = new byte[outputBlob.Size];
            Marshal.Copy(outputBlob.Data, output, 0, output.Length);
            return output;
        }
        finally
        {
            FreeBlob(inputBlob, localFree: false);
            FreeBlob(entropyBlob, localFree: false);
            FreeBlob(outputBlob, localFree: true);
            if (description != IntPtr.Zero)
            {
                LocalFree(description);
            }
        }
    }

    private static byte[] ProtectPortable(byte[] plaintext)
    {
        var nonce = RandomNumberGenerator.GetBytes(12);
        var tag = new byte[16];
        var ciphertext = new byte[plaintext.Length];
        using var aes = new AesGcm(DerivePortableKey(), tagSizeInBytes: tag.Length);
        aes.Encrypt(nonce, plaintext, ciphertext, tag, AdditionalEntropy);
        return [.. nonce, .. tag, .. ciphertext];
    }

    private static byte[] UnprotectPortable(byte[] protectedPayload)
    {
        if (protectedPayload.Length < 28)
        {
            throw new CryptographicException("ADB 工作流状态密文长度无效。");
        }
        var nonce = protectedPayload.AsSpan(0, 12);
        var tag = protectedPayload.AsSpan(12, 16);
        var ciphertext = protectedPayload.AsSpan(28);
        var plaintext = new byte[ciphertext.Length];
        using var aes = new AesGcm(DerivePortableKey(), tagSizeInBytes: tag.Length);
        aes.Decrypt(nonce, ciphertext, tag, plaintext, AdditionalEntropy);
        return plaintext;
    }

    private static byte[] DerivePortableKey()
    {
        var identity = string.Join(
            '\n',
            Environment.UserName,
            Environment.MachineName,
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            Convert.ToHexString(AdditionalEntropy));
        return SHA256.HashData(Encoding.UTF8.GetBytes(identity));
    }

    private static DataBlob CreateBlob(byte[] bytes)
    {
        var pointer = Marshal.AllocHGlobal(bytes.Length);
        Marshal.Copy(bytes, 0, pointer, bytes.Length);
        return new DataBlob(bytes.Length, pointer);
    }

    private static void FreeBlob(DataBlob blob, bool localFree)
    {
        if (blob.Data == IntPtr.Zero)
        {
            return;
        }
        if (localFree)
        {
            LocalFree(blob.Data);
        }
        else
        {
            Marshal.FreeHGlobal(blob.Data);
        }
    }

    private sealed record ProtectedStateEnvelope(int SchemaVersion, string Protection, string Payload);

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob(int size, IntPtr data)
    {
        public int Size = size;
        public IntPtr Data = data;
    }

    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(
        ref DataBlob dataIn,
        string? dataDescription,
        ref DataBlob optionalEntropy,
        IntPtr reserved,
        IntPtr promptStruct,
        uint flags,
        out DataBlob dataOut);

    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(
        ref DataBlob dataIn,
        out IntPtr dataDescription,
        ref DataBlob optionalEntropy,
        IntPtr reserved,
        IntPtr promptStruct,
        uint flags,
        out DataBlob dataOut);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr LocalFree(IntPtr memory);
}

internal sealed class InMemoryAdbForwarderWorkflowStateStore : IAdbForwarderWorkflowStateStore
{
    private AdbForwarderPersistedWorkflowState? _state;
    public AdbForwarderPersistedWorkflowState? Load() => _state;
    public void Save(AdbForwarderPersistedWorkflowState state) => _state = state;
    public void Clear() => _state = null;
}
