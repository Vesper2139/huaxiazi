using System;
using System.Runtime.InteropServices;

namespace Huaxiazi.Services;

/// <summary>Invokes the Windows Authenticode policy provider with chain revocation enabled.</summary>
internal static class WinTrust
{
    private static readonly Guid GenericVerifyV2 = new("00AAC56B-CD44-11D0-8CC2-00C04FC295EE");
    private const uint WtdUiNone = 2;
    private const uint WtdRevokeWholeChain = 1;
    private const uint WtdChoiceFile = 1;
    private const uint WtdStateActionIgnore = 0;
    private const uint WtdRevocationCheckChain = 0x00000040;

    internal static bool VerifyEmbeddedSignature(string filePath)
    {
        if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(filePath)) return false;
        var fileInfo = new WinTrustFileInfo(filePath);
        var fileInfoPointer = Marshal.AllocHGlobal(Marshal.SizeOf<WinTrustFileInfo>());
        var trustDataPointer = IntPtr.Zero;
        try
        {
            Marshal.StructureToPtr(fileInfo, fileInfoPointer, fDeleteOld: false);
            var trustData = new WinTrustData(fileInfoPointer);
            trustDataPointer = Marshal.AllocHGlobal(Marshal.SizeOf<WinTrustData>());
            Marshal.StructureToPtr(trustData, trustDataPointer, fDeleteOld: false);
            var action = GenericVerifyV2;
            return WinVerifyTrust(IntPtr.Zero, ref action, trustDataPointer) == 0;
        }
        finally
        {
            if (trustDataPointer != IntPtr.Zero)
            {
                try { Marshal.DestroyStructure<WinTrustData>(trustDataPointer); }
                finally { Marshal.FreeHGlobal(trustDataPointer); }
            }

            try { Marshal.DestroyStructure<WinTrustFileInfo>(fileInfoPointer); }
            finally { Marshal.FreeHGlobal(fileInfoPointer); }
        }
    }

    [DllImport("wintrust.dll", ExactSpelling = true, SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern int WinVerifyTrust(IntPtr hwnd, ref Guid actionId, IntPtr trustData);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WinTrustFileInfo
    {
        internal WinTrustFileInfo(string filePath)
        {
            StructSize = (uint)Marshal.SizeOf<WinTrustFileInfo>();
            FilePath = filePath;
            FileHandle = IntPtr.Zero;
            KnownSubject = IntPtr.Zero;
        }

        private uint StructSize;
        [MarshalAs(UnmanagedType.LPWStr)] private string FilePath;
        private IntPtr FileHandle;
        private IntPtr KnownSubject;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WinTrustData
    {
        internal WinTrustData(IntPtr fileInfo)
        {
            StructSize = (uint)Marshal.SizeOf<WinTrustData>();
            PolicyCallbackData = IntPtr.Zero;
            SipClientData = IntPtr.Zero;
            UiChoice = WtdUiNone;
            RevocationChecks = WtdRevokeWholeChain;
            UnionChoice = WtdChoiceFile;
            FileInfo = fileInfo;
            StateAction = WtdStateActionIgnore;
            StateData = IntPtr.Zero;
            UrlReference = IntPtr.Zero;
            ProviderFlags = WtdRevocationCheckChain;
            UiContext = 0;
            SignatureSettings = IntPtr.Zero;
        }

        private uint StructSize;
        private IntPtr PolicyCallbackData;
        private IntPtr SipClientData;
        private uint UiChoice;
        private uint RevocationChecks;
        private uint UnionChoice;
        private IntPtr FileInfo;
        private uint StateAction;
        private IntPtr StateData;
        private IntPtr UrlReference;
        private uint ProviderFlags;
        private uint UiContext;
        private IntPtr SignatureSettings;
    }
}
