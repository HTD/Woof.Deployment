using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Security.Principal;
using System.Text;
using System.Windows;

namespace Woof.DeploymentEx {

    /// <summary>
    /// Provides a windows dialog to elevate the current process.
    /// </summary>
    public class Uac {

        /// <summary>
        /// Gets a value indicating whether current process is elevated (run with Administrator privileges).
        /// </summary>
        public static bool IsCurrentProcessElevated {
            get {
                using (var identity = WindowsIdentity.GetCurrent()) return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
            }
        }

        /// <summary>
        /// Gets a value indicationg whether the current account is Windows System account.
        /// </summary>
        public static bool IsCurrentSystem {
            get {
                using (var identity = WindowsIdentity.GetCurrent()) return identity.IsSystem;
            }
        }

        /// <summary>
        /// If current process has no Administartor privileges, restarts current process as elevated.
        /// Ensures all code after this call will be run with elevated privileges or won't be run at all.
        /// </summary>
        /// <param name="message">Optional retry prompt.</param>
        public static void Elevate(string message = null) {
            if (!IsCurrentProcessElevated) {
                var assembly = Assembly.GetEntryAssembly();
                retry:
                try {
                    Process.Start(new ProcessStartInfo(assembly.Location, new CommandLineArguments(Environment.GetCommandLineArgs())) { Verb = "RunAs" });
                }
                catch (Win32Exception x) {
                    if (x.NativeErrorCode != 1223 || message == null)
                        MessageBox.Show(x.Message, x.GetType().Name, MessageBoxButton.OK, MessageBoxImage.Error);
                    else {
                        var result = MessageBox.Show(message, assembly.GetCustomAttribute<AssemblyTitleAttribute>().Title, MessageBoxButton.OKCancel, MessageBoxImage.Error);
                        if (result == MessageBoxResult.OK) goto retry;
                        else Environment.Exit(-1);
                    }
                }
                Environment.Exit(0);
            }
        }

        /// <summary>
        /// DUPLICATE OF Woow.SystemEx.CommanLineArguments to remove dependency on Woof.Core.
        /// Can be replaced with Woof.Core version when dependencies are not an issue.
        /// </summary>
        private sealed class CommandLineArguments : IEnumerable<string> {

            /// <summary>
            /// Returns argument value specified with its index.
            /// </summary>
            /// <param name="i">Zero based collection index.</param>
            /// <returns>Argument value.</returns>
            public string this[int i] => Items[i];

            /// <summary>
            /// Returs arguments collection length.
            /// </summary>
            public int Length => Items?.Length ?? 0;

            /// <summary>
            /// Creates new command line arguments collection.
            /// </summary>
            /// <param name="arguments">Unquoted arguments.</param>
            public CommandLineArguments(params string[] arguments) => Items = arguments;

            /// <summary>
            /// Serializes command line arguments collection with necessary character quoting.
            /// </summary>
            /// <returns>Serialized command line arguments string.</returns>
            public override string ToString() {
                if (Items == null) return null;
                StringBuilder b = new StringBuilder();
                for (int i = 0; i < Items.Length; i++) {
                    if (i > 0) b.Append(' ');
                    AppendArgument(b, Items[i]);
                }
                return b.ToString();
            }

            /// Performs implict <see cref="ToString"/> conversion.
            /// </summary>
            /// <param name="args">Arguments object.</param>
            public static implicit operator string(CommandLineArguments args) => args.ToString();

            /// <summary>
            /// Quotes argument string and appends it to specified <see cref="StringBuilder"/>.
            /// </summary>
            /// <param name="b"><see cref="StringBuilder"/> object the quoted argument will be appended to.</param>
            /// <param name="arg">Unquoted argument value.</param>
            private void AppendArgument(StringBuilder b, string arg) {
                if (arg.Length > 0 && arg.IndexOfAny(ArgQuoteChars) < 0) {
                    b.Append(arg);
                }
                else {
                    b.Append('"');
                    for (int j = 0; ; j++) {
                        int backslashCount = 0;
                        while (j < arg.Length && arg[j] == '\\') {
                            backslashCount++;
                            j++;
                        }
                        if (j == arg.Length) {
                            b.Append('\\', backslashCount * 2);
                            break;
                        }
                        else if (arg[j] == '"') {
                            b.Append('\\', backslashCount * 2 + 1);
                            b.Append('"');
                        }
                        else {
                            b.Append('\\', backslashCount);
                            b.Append(arg[j]);
                        }
                    }
                    b.Append('"');
                }
            }

            /// <summary>
            /// Enumerates items.
            /// </summary>
            /// <returns>Generic enumerator.</returns>
            public IEnumerator<string> GetEnumerator() => ((IEnumerable<string>)Items).GetEnumerator();

            /// <summary>
            /// Enumerates items.
            /// </summary>
            /// <returns>Non-generic enumerator.</returns>
            IEnumerator IEnumerable.GetEnumerator() => ((IEnumerable<string>)Items).GetEnumerator();

            /// <summary>
            /// Characters which must be quoted in argument strings.
            /// </summary>
            private readonly char[] ArgQuoteChars = { ' ', '\t', '\n', '\v', '"' };

            /// <summary>
            /// Argument values.
            /// </summary>
            private readonly string[] Items;

        }

    }

}