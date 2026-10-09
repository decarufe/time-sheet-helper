using System.Runtime.InteropServices;
using System.Text;
using TimeSheetHelper.Core.Secrets;

namespace TimeSheetHelper.Collectors.Windows;

/// <summary>Generic credentials in the Windows Credential Manager, scoped to the current user.</summary>
public sealed class CredentialManagerSecretStore : ISecretStore
{
    private const int CRED_TYPE_GENERIC = 1;
    private const int CRED_PERSIST_LOCAL_MACHINE = 2;
    private const int ERROR_NOT_FOUND = 1168;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CREDENTIAL
    {
        public int Flags;
        public int Type;
        public string TargetName;
        public string? Comment;
        public long LastWritten;
        public int CredentialBlobSize;
        public IntPtr CredentialBlob;
        public int Persist;
        public int AttributeCount;
        public IntPtr Attributes;
        public string? TargetAlias;
        public string? UserName;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredRead(string target, int type, int flags, out IntPtr credential);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredWrite(ref CREDENTIAL credential, int flags);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredDelete(string target, int type, int flags);

    [DllImport("advapi32.dll")]
    private static extern void CredFree(IntPtr buffer);

    public string? Get(string name)
    {
        if (!CredRead(name, CRED_TYPE_GENERIC, 0, out var ptr))
            return null;
        try
        {
            var credential = Marshal.PtrToStructure<CREDENTIAL>(ptr);
            return credential.CredentialBlobSize == 0
                ? string.Empty
                : Marshal.PtrToStringUni(credential.CredentialBlob, credential.CredentialBlobSize / 2);
        }
        finally
        {
            CredFree(ptr);
        }
    }

    public void Set(string name, string value)
    {
        var bytes = Encoding.Unicode.GetBytes(value);
        var blob = Marshal.AllocHGlobal(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, blob, bytes.Length);
            var credential = new CREDENTIAL
            {
                Type = CRED_TYPE_GENERIC,
                TargetName = name,
                CredentialBlob = blob,
                CredentialBlobSize = bytes.Length,
                Persist = CRED_PERSIST_LOCAL_MACHINE,
                UserName = Environment.UserName,
            };
            if (!CredWrite(ref credential, 0))
                throw new InvalidOperationException($"CredWrite failed: {Marshal.GetLastWin32Error()}");
        }
        finally
        {
            Marshal.FreeHGlobal(blob);
        }
    }

    public void Delete(string name)
    {
        if (!CredDelete(name, CRED_TYPE_GENERIC, 0) && Marshal.GetLastWin32Error() != ERROR_NOT_FOUND)
            throw new InvalidOperationException($"CredDelete failed: {Marshal.GetLastWin32Error()}");
    }
}
