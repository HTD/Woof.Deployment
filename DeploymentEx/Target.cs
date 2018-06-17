using System;
using System.IO;
using System.Security.Principal;

namespace Woof.DeploymentEx {

    /// <summary>
    /// Tools related to system environment paths.
    /// </summary>
    public static class Target {

        #region Current process environment

        /// <summary>
        /// Gets the automatic <see cref="EnvironmentVariableTarget"/> depending on whether current user has administrative privileges.
        /// </summary>
        public static EnvironmentVariableTarget Auto {
            get {
                using (var identity = WindowsIdentity.GetCurrent())
                    return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator)
                        ? EnvironmentVariableTarget.Machine
                        : EnvironmentVariableTarget.User;
            }
        }

        /// <summary>
        /// Gets a value indicating whether the "Program Files" folder will be x86 folder.
        /// (True for programs compiled with "Prefer 32-bit" option set).
        /// </summary>
        public static bool IsX86 => Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles).Contains("x86");

        /// <summary>
        /// Gets program files folder depending on target type (machine / user).
        /// </summary>
        /// <param name="target">Environment location.</param>
        /// <returns>Hopefully writeable directory to store new programs in.</returns>
        public static string GetProgramFilesDirectory(EnvironmentVariableTarget target) =>
            target == EnvironmentVariableTarget.Machine
                ? Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs");

        #endregion

    }

}