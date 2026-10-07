using System;
using System.IO;
using System.Security.Cryptography;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;

namespace Huaxiazi.Services;

public sealed class DpapiSecretStore : ISecretStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Huaxiazi:secrets:v1");
    private readonly string _directory;
    private readonly Action<DirectoryInfo, DirectorySecurity> _accessControlWriter;
    private bool _directoryReady;

    public DpapiSecretStore(string directory)
        : this(directory, static (info, security) => info.SetAccessControl(security))
    {
    }

    internal DpapiSecretStore(string directory, Action<DirectoryInfo, DirectorySecurity> accessControlWriter)
    {
        _directory = directory ?? throw new ArgumentNullException(nameof(directory));
        _accessControlWriter = accessControlWriter ?? throw new ArgumentNullException(nameof(accessControlWriter));
        EnsureHardened();
    }

    public void Save(string id, string secret)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentNullException.ThrowIfNull(secret);
        EnsureHardened();

        var encrypted = ProtectedData.Protect(
            Encoding.UTF8.GetBytes(secret), Entropy, DataProtectionScope.CurrentUser);
        var target = GetPath(id);
        var temporary = target + ".tmp";
        File.WriteAllBytes(temporary, encrypted);
        File.Move(temporary, target, true);
    }

    public string? Read(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        EnsureHardened();
        var path = GetPath(id);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var decrypted = ProtectedData.Unprotect(
                File.ReadAllBytes(path), Entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(decrypted);
        }
        catch (CryptographicException)
        {
            // A secret can become unreadable after profile migration or file corruption.
            // Treat it as missing so callers can guide the user to re-enter it.
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    public bool Exists(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        EnsureHardened();
        return File.Exists(GetPath(id));
    }

    public void Delete(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        EnsureHardened();
        var path = GetPath(id);
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private string GetPath(string id)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(id)));
        return Path.Combine(_directory, hash + ".secret");
    }

    private void EnsureHardened()
    {
        if (_directoryReady) return;

        var existed = Directory.Exists(_directory);
        Directory.CreateDirectory(_directory);
        try
        {
            HardenDirectoryAccess();
        }
        catch (UnauthorizedAccessException) when (existed)
        {
            // Existing installations can have ACLs owned by an earlier token or
            // endpoint-security policy. Do not fail startup solely on ACL repair.
        }
        catch (IOException) when (existed)
        {
            // Same compatibility rule for filesystem/ACL races on existing data.
        }

        _directoryReady = true;
    }

    private void HardenDirectoryAccess()
    {
        if (!OperatingSystem.IsWindows()) return;
        var currentUser = WindowsIdentity.GetCurrent().User
            ?? throw new InvalidOperationException("无法识别当前 Windows 用户。");
        var inheritance = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(
            currentUser, FileSystemRights.FullControl, inheritance, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), FileSystemRights.FullControl,
            inheritance, PropagationFlags.None, AccessControlType.Allow));
        _accessControlWriter(new DirectoryInfo(_directory), security);
    }
}
