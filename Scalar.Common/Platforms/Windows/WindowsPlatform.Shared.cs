using Scalar.Common;
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace Scalar.Platform.Windows
{
    public partial class WindowsPlatform
    {
        private const int StillActive = 259; /* from Win32 STILL_ACTIVE */

        private enum StdHandle
        {
            Stdin = -10,
            Stdout = -11,
            Stderr = -12
        }

        private enum FileType : uint
        {
            Unknown = 0x0000,
            Disk = 0x0001,
            Char = 0x0002,
            Pipe = 0x0003,
            Remote = 0x8000,
        }

        public static bool IsElevatedImplementation()
        {
            using (WindowsIdentity id = WindowsIdentity.GetCurrent())
            {
                return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
            }
        }

        public static string GetCommonAppDataRootForScalarImplementation()
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData, Environment.SpecialFolderOption.Create),
                "Scalar");
        }

        public static string GetCommonAppDataRootForScalarComponentImplementation(string componentName)
        {
            return Path.Combine(GetCommonAppDataRootForScalarImplementation(), componentName);
        }

        public static string GetSecureDataRootForScalarImplementation()
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolderOption.Create),
                "Scalar",
                "ProgramData");
        }

        public static string GetSecureDataRootForScalarComponentImplementation(string componentName)
        {
            return Path.Combine(GetSecureDataRootForScalarImplementation(), componentName);
        }

        public static string GetLogsDirectoryForGVFSComponentImplementation(string componentName)
        {
            return Path.Combine(
                GetCommonAppDataRootForScalarComponentImplementation(componentName),
                "Logs");
        }

        public static bool IsConsoleOutputRedirectedToFileImplementation()
        {
            return FileType.Disk == GetFileType(GetStdHandle(StdHandle.Stdout));
        }

        public static string GetUpgradeProtectedDataDirectoryImplementation()
        {
            return Path.Combine(GetCommonAppDataRootForScalarImplementation(), ProductUpgraderInfo.UpgradeDirectoryName);
        }

        public static string GetUpgradeHighestAvailableVersionDirectoryImplementation()
        {
            return GetUpgradeProtectedDataDirectoryImplementation();
        }

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetStdHandle(StdHandle std);

        [DllImport("kernel32.dll")]
        private static extern FileType GetFileType(IntPtr hdl);
    }
}
