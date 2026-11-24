using System.Runtime.InteropServices;
using System.Text;

namespace SpeechToText.Config;

/// <summary>API keys live in Windows Credential Manager, never in settings.json.</summary>
public static class SecretStore
{
    const int CRED_TYPE_GENERIC = 1;
    const int CRED_PERSIST_LOCAL_MACHINE = 2;

    static string Target(string name) => $"SpeechToText:{name}";

    public static string? Get(string name)
    {
        if (!CredRead(Target(name), CRED_TYPE_GENERIC, 0, out var ptr)) return null;
        try
        {
            var cred = Marshal.PtrToStructure<CREDENTIAL>(ptr);
            if (cred.CredentialBlobSize == 0) return null;
            return Marshal.PtrToStringUni(cred.CredentialBlob, (int)cred.CredentialBlobSize / 2);
        }
        finally { CredFree(ptr); }
    }

    public static void Set(string name, string? value)
    {
        if (string.IsNullOrEmpty(value)) { CredDelete(Target(name), CRED_TYPE_GENERIC, 0); return; }

        var bytes = Encoding.Unicode.GetBytes(value);
        var blob = Marshal.AllocHGlobal(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, blob, bytes.Length);
            var cred = new CREDENTIAL
            {
                Type = CRED_TYPE_GENERIC,
                TargetName = Target(name),
                CredentialBlob = blob,
                CredentialBlobSize = (uint)bytes.Length,
                Persist = CRED_PERSIST_LOCAL_MACHINE,
                UserName = Environment.UserName,
            };
            if (!CredWrite(ref cred, 0))
                throw new InvalidOperationException($"Could not save credential (error {Marshal.GetLastWin32Error()}).");
        }
        finally { Marshal.FreeHGlobal(blob); }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct CREDENTIAL
    {
        public uint Flags;
        public int Type;
        public string TargetName;
        public string? Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public int Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public string? TargetAlias;
        public string? UserName;
    }

    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool CredRead(string target, int type, int flags, out IntPtr credential);

    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool CredWrite(ref CREDENTIAL credential, uint flags);

    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool CredDelete(string target, int type, int flags);

    [DllImport("advapi32.dll")]
    static extern void CredFree(IntPtr buffer);
}
