using Scalar.Common;
using Scalar.Common.FileSystem;
using Scalar.Common.Tracing;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;

namespace Scalar.Platform.Windows
{
    public class WindowsFileSystem : IPlatformFileSystem
    {
        public bool SupportsFileMode { get; } = false;

        public bool SupportsUntrackedCache { get; } = false;

        /// <summary>
        /// Adds a new FileSystemAccessRule granting read (and optionally modify) access for all users.
        /// </summary>
        /// <param name="directorySecurity">DirectorySecurity to which a FileSystemAccessRule will be added.</param>
        /// <param name="grantUsersModifyPermissions">
        /// True if all users should be given modify access, false if users should only be allowed read access
        /// </param>
        public static void AddUsersAccessRulesToDirectorySecurity(DirectorySecurity directorySecurity, bool grantUsersModifyPermissions)
        {
            SecurityIdentifier allUsers = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);
            FileSystemRights rights = FileSystemRights.Read;
            if (grantUsersModifyPermissions)
            {
                rights = rights | FileSystemRights.Modify;
            }

            // InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit -> ACE is inherited by child directories and files
            // PropagationFlags.None -> Standard propagation rules, settings are applied to the directory and its children
            // AccessControlType.Allow -> Rule is used to allow access to an object
            directorySecurity.AddAccessRule(
                new FileSystemAccessRule(
                    allUsers,
                    rights,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                    PropagationFlags.None,
                    AccessControlType.Allow));
        }

        /// <summary>
        /// Adds a new FileSystemAccessRule granting read/exceute/modify/delete access for administrators.
        /// </summary>
        /// <param name="directorySecurity">DirectorySecurity to which a FileSystemAccessRule will be added.</param>
        public static void AddAdminAccessRulesToDirectorySecurity(DirectorySecurity directorySecurity)
        {
            SecurityIdentifier administratorUsers = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);

            // InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit -> ACE is inherited by child directories and files
            // PropagationFlags.None -> Standard propagation rules, settings are applied to the directory and its children
            // AccessControlType.Allow -> Rule is used to allow access to an object
            directorySecurity.AddAccessRule(
                new FileSystemAccessRule(
                    administratorUsers,
                    FileSystemRights.ReadAndExecute | FileSystemRights.Modify | FileSystemRights.Delete,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                    PropagationFlags.None,
                    AccessControlType.Allow));
        }

        /// <summary>
        /// Removes all FileSystemAccessRules from specified DirectorySecurity
        /// </summary>
        /// <param name="directorySecurity">DirectorySecurity from which to remove FileSystemAccessRules</param>
        public static void RemoveAllFileSystemAccessRulesFromDirectorySecurity(DirectorySecurity directorySecurity)
        {
            AuthorizationRuleCollection currentRules = directorySecurity.GetAccessRules(includeExplicit: true, includeInherited: true, targetType: typeof(NTAccount));
            foreach (AuthorizationRule authorizationRule in currentRules)
            {
                FileSystemAccessRule fileSystemRule = authorizationRule as FileSystemAccessRule;
                if (fileSystemRule != null)
                {
                    directorySecurity.RemoveAccessRule(fileSystemRule);
                }
            }
        }

        public void FlushFileBuffers(string path)
        {
            NativeMethods.FlushFileBuffers(path);
        }

        public void MoveAndOverwriteFile(string sourceFileName, string destinationFilename)
        {
            NativeMethods.MoveFile(
                sourceFileName,
                destinationFilename,
                NativeMethods.MoveFileFlags.MoveFileReplaceExisting);
        }

        public bool TryGetNormalizedPath(string path, out string normalizedPath, out string errorMessage)
        {
            normalizedPath = null;
            errorMessage = null;
            try
            {
                // The folder might not be on disk yet, walk up the path until we find a folder that's on disk
                Stack<string> removedPathParts = new Stack<string>();
                string parentPath = path;
                while (!string.IsNullOrWhiteSpace(parentPath) && !Directory.Exists(parentPath))
                {
                    removedPathParts.Push(Path.GetFileName(parentPath));
                    parentPath = Path.GetDirectoryName(parentPath);
                }

                if (string.IsNullOrWhiteSpace(parentPath))
                {
                    errorMessage = "Could not get path root. Specified path does not exist and unable to find ancestor of path on disk";
                    return false;
                }

                normalizedPath = NativeMethods.GetFinalPathName(parentPath);

                // normalizedPath now consists of all parts of the path currently on disk, re-add any parts of the path that were popped off
                while (removedPathParts.Count > 0)
                {
                    normalizedPath = Path.Combine(normalizedPath, removedPathParts.Pop());
                }
            }
            catch (Win32Exception e)
            {
                errorMessage = "Could not get path root. Failed to determine volume: " + e.Message;
                return false;
            }

            return true;
        }

        public bool IsExecutable(string fileName)
        {
            string fileExtension = Path.GetExtension(fileName);
            return string.Equals(fileExtension, ".exe", StringComparison.OrdinalIgnoreCase);
        }

        public bool IsSocket(string fileName)
        {
            return false;
        }

        public bool TryCreateDirectoryWithAdminAndUserModifyPermissions(string directoryPath, out string error)
        {
            try
            {
                DirectorySecurity directorySecurity = new DirectorySecurity();

                // Protect the access rules from inheritance and remove any inherited rules
                directorySecurity.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);

                // Add new ACLs for users and admins.  Users will be granted write permissions.
                AddUsersAccessRulesToDirectorySecurity(directorySecurity, grantUsersModifyPermissions: true);
                AddAdminAccessRulesToDirectorySecurity(directorySecurity);

                DirectoryEx.CreateDirectory(directoryPath, directorySecurity);
            }
            catch (Exception e) when (e is IOException ||
                                      e is UnauthorizedAccessException ||
                                      e is PathTooLongException ||
                                      e is DirectoryNotFoundException)
            {
                error = $"Exception while creating directory `{directoryPath}`: {e.Message}";
                return false;
            }

            error = null;
            return true;
        }

        public bool TryCreateOrUpdateDirectoryToAdminModifyPermissions(ITracer tracer, string directoryPath, out string error)
        {
            try
            {
                DirectorySecurity directorySecurity;
                if (Directory.Exists(directoryPath))
                {
                    directorySecurity = DirectoryEx.GetAccessControl(directoryPath);
                }
                else
                {
                    directorySecurity = new DirectorySecurity();
                }

                // Protect the access rules from inheritance and remove any inherited rules
                directorySecurity.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);

                // Remove any existing ACLs and add new ACLs for users and admins
                RemoveAllFileSystemAccessRulesFromDirectorySecurity(directorySecurity);
                AddUsersAccessRulesToDirectorySecurity(directorySecurity, grantUsersModifyPermissions: false);
                AddAdminAccessRulesToDirectorySecurity(directorySecurity);

                DirectoryEx.CreateDirectory(directoryPath, directorySecurity);

                // Ensure the ACLs are set correctly if the directory already existed
                DirectoryEx.SetAccessControl(directoryPath, directorySecurity);
            }
            catch (Exception e) when (e is IOException || e is SystemException)
            {
                EventMetadata metadata = new EventMetadata();
                metadata.Add("Exception", e.ToString());
                tracer.RelatedError(metadata, $"{nameof(this.TryCreateOrUpdateDirectoryToAdminModifyPermissions)}: Exception while creating/configuring directory");

                error = e.Message;
                return false;
            }

            error = null;
            return true;
        }

        public bool IsFileSystemSupported(string path, out string error)
        {
            error = null;
            return true;
        }
    }
}
