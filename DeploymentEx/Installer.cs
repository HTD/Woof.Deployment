using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.ServiceProcess;
using System.Text.RegularExpressions;

namespace Woof.DeploymentEx {

    /// <summary>
    /// A class responsible for processing install / uninstall / upgrade scripts embedded in the assembly.
    /// </summary>
    class Installer {

        /// <summary>
        /// Default process timeout in seconds (can be overriden in script).
        /// </summary>
        public const int ProcessTimeoutDefault = 300;

        #region Events

        /// <summary>
        /// Occurs when a script executes Notify command.
        /// </summary>
        public event EventHandler<string> Notification;

        /// <summary>
        /// Occurs when a script executes Message command.
        /// </summary>
        public event EventHandler<string> MessageReceived;

        /// <summary>
        /// Occurs when script has finished running without errors.
        /// </summary>
        public event EventHandler Success;

        /// <summary>
        /// Occurs when script was terminated because an error has occured.
        /// </summary>
        public event EventHandler<TDiagnostics> Failure;

        #endregion

        #region Public enumerations and types

        [Flags]
        public enum StatusFlags {
            OK = 0,
            NullReference = 1,
            FileNotFound = 2,
            DirectoryNotFound = 4,
            FileAccessDenied = 8,
            DirectoryAccessDenied = 16,
            AlreadyInstalled = 32,
            NonZeroExitCode = 64
        }

        public class TDiagnostics : EventArgs {

            public StatusFlags Status { get; set; }

            public string ScriptName { get; set; }

            public string ScriptLine { get; set; }

            public string ErrorMessage { get; set; }

            public int ExitCode { get; set; }

        }

        #endregion

        /// <summary>
        /// Creates and initializes an instance of <see cref="Installer"/> class.
        /// </summary>
        public Installer() {
            ScriptSetFields = ScriptSet.GetType().GetFields();
            ResolvableProperties = Resolvable.GetType().GetProperties();
        }

        /// <summary>
        /// Runs one or more installer scripts.
        /// </summary>
        /// <param name="embeddedFileNames">One or more embedded file names. A file name without namespace needed.</param>
        public void RunScript(params string[] embeddedFileNames) => Run(embeddedFileNames);

        #region Script commands

        /// <summary>
        /// Runs one or more embedded scripts.
        /// </summary>
        /// <param name="args">One or more embedded file names. A file name without namespace needed.</param>
        private void Run(params string[] args) {
            RunCount++;
            foreach (var embeddedFileName in args) {
                if (embeddedFileName == ExitCommand) { ExitRequested = true; break; }
                var lines = RxLines.Split(GetTextFromEmbeddedFile(embeddedFileName));
                var length = lines.Length;
                for (int i = 0; i < length; i++) {
                    var line = lines[i].Trim();
                    if (line.Length < 1) continue;
                    if (line[0] == '#') continue;
                    CurrentScript = embeddedFileName;
                    CurrentLine = line;
                    int c = line.IndexOf('#'); if (c > 0) line = line.Substring(0, c);
                    var split = RxAssignment.Split(line, 2);
                    if (split.Length > 1) ParseAssignment(split);
                    else ParseExpression(line);
                    if (ExitRequested) break;
                }
                if (ExitRequested) break;
            }
            RunCount--;
            if (Status == StatusFlags.OK && (RunCount < 1)) OnSuccess();
        }

        /// <summary>
        /// Sends a message to hosting application.
        /// </summary>
        /// <param name="args">One (first) argument with message content is accepted.</param>
        private void Message(params string[] args) => OnMessageReceived(args[0].Trim('"'));

        /// <summary>
        /// Displays a notification.
        /// </summary>
        /// <param name="args">One (first) argument with notification content is accepted.</param>
        private void Notify(params string[] args) => OnNotify(args[0].Trim('"'));

        /// <summary>
        /// Packs specified list of files from $(Source) directory to $(Target) file.
        /// </summary>
        /// <param name="args">One (first) argument contains the name of embedded resource containing the list of files.</param>
        private void Pack(params string[] args) {
            if (args.Length < 1 || String.IsNullOrEmpty(args[0])) throw new ArgumentException("File list argument cannot be empty for Pack command.");
            if (String.IsNullOrEmpty(ScriptSet.Source)) throw new ArgumentException("Source cannot be empty for Pack command.");
            if (String.IsNullOrEmpty(ScriptSet.Target)) throw new ArgumentException("Target cannot be empty for Pack command.");
            var assembly = Assembly.GetExecutingAssembly();
            var listSource = args[0];
            var sourceDir = Unquote(ScriptSet.Source);
            var targetPath = Unquote(ScriptSet.Target);
            using (var s = assembly.GetManifestResourceStream($"{assembly.EntryPoint.ReflectedType.Namespace}.{listSource}")) {
                if (s != null) {
                    string listData;
                    using (var r = new StreamReader(s)) listData = r.ReadToEnd();
                    var files = listData.Split(new[] { Environment.NewLine }, StringSplitOptions.RemoveEmptyEntries);
                    if (!String.IsNullOrEmpty(sourceDir)) files = files.Select(i => Path.GetFullPath(Path.Combine(sourceDir, i))).ToArray();
                    targetPath = Path.GetFullPath(targetPath);
                    using (var arc = new ArcDeflate { BaseDir = sourceDir }) arc.CreateArchive(targetPath, files);
                }
            }


        }

        /// <summary>
        /// Unpacks embedded archive given as $(Source) to $(Target) directory.
        /// </summary>
        /// <param name="args">Arguments for this command are ignored.</param>
        private void Unpack(params string[] args) {
            if (String.IsNullOrEmpty(ScriptSet.Source)) throw new ArgumentException("Source cannot be empty for Unpack command.");
            if (String.IsNullOrEmpty(ScriptSet.Target)) throw new ArgumentException("Target cannot be empty for Unpack command.");
            var assembly = Assembly.GetExecutingAssembly();
            var resourceName = Unquote(ScriptSet.Source);
            var targetDirectory = Unquote(ScriptSet.Target);
            using (var s = assembly.GetManifestResourceStream($"{assembly.EntryPoint.ReflectedType.Namespace}.{resourceName}"))
            using (var arc = new ArcDeflate()) arc.ExtractArchive(s, targetDirectory);
        }

        /// <summary>
        /// Deletes specified files or directories if exist.
        /// </summary>
        /// <param name="args">Paths of files to delete.</param>
        private void Delete(params string[] args) {
            foreach (var arg in args) {
                var path = Unquote(arg);
                if (File.Exists(path)) {
                    try {
                        File.Delete(path);
                    }
                    catch (IOException) {
                        ExitWithStatus(StatusFlags.FileAccessDenied);
                    }
                    catch (UnauthorizedAccessException) {
                        ExitWithStatus(StatusFlags.FileAccessDenied);
                    }
                }
                else if (Directory.Exists(path)) {
                    try {
                        Directory.Delete(path, true);
                    }
                    catch (IOException) {
                        ExitWithStatus(StatusFlags.DirectoryAccessDenied);
                    }
                    catch (UnauthorizedAccessException) {
                        ExitWithStatus(StatusFlags.DirectoryAccessDenied);
                    }
                }
            }
        }

        /// <summary>
        /// Kills one or more processes.
        /// </summary>
        /// <param name="args">Paths of executable files.</param>
        private void Kill(params string[] args) {
            foreach (var arg in args) {
                var path = Unquote(arg);
                var name = Path.GetFileNameWithoutExtension(path);
                var process = Process.GetProcessesByName(name).FirstOrDefault();
                if (process != null) {
                    process.Kill();
                    process.Dispose();
                }
            }
        }

        /// <summary>
        /// Starts Windows services with specified names.
        /// </summary>
        /// <param name="args">Service names.</param>
        /// <remarks>
        /// The scripts waits up to 3 seconds for each service to start.
        /// </remarks>
        private void ServiceStart(params string[] args) {
            try {
                foreach (var name in args)
                    using (var sc = new ServiceController(name))
                        if (sc != null) {
                            sc.Start(); sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(3));
                        }
            } catch (InvalidOperationException) { }
        }

        /// <summary>
        /// Stops Windows services with specified names.
        /// </summary>
        /// <param name="args">The script waits infinitely for the services to stop.</param>
        private void ServiceStop(params string[] args) {
            try {
                foreach (var name in args)
                    using (var sc = new ServiceController(name))
                        if (sc != null) {
                            sc.Stop(); sc.WaitForStatus(ServiceControllerStatus.Stopped);
                        }
            } catch (InvalidOperationException) { }
        }

        /// <summary>
        /// Executes following arguments as script if file or directory specified as first argument exists.
        /// </summary>
        /// <param name="args">The first argument is a path to test, the rest are script commands and arguments.</param>
        private void IfExists(params string[] args) {
            var path = Unquote(args[0]);
            if (File.Exists(path) || Directory.Exists(path)) ParseExpression(args.Skip(1).ToArray());
        }

        /// <summary>
        /// Executes following arguments as script if file or directory specified as first argument doesn't exist.
        /// </summary>
        /// <param name="args">The first argument is a path to test, the rest are script commands and arguments.</param>
        private void IfNotExists(params string[] args) {
            var path = Unquote(args[0]);
            if (!File.Exists(path) && !Directory.Exists(path)) ParseExpression(args.Skip(1).ToArray());
        }

        /// <summary>
        /// Executes following arguments as script if the first argument is a path to assembly with version less than executing assembly version.
        /// </summary>
        /// <param name="args">The first argument is the assembly path, the rest are script commands and arguments.</param>
        private void IfUpgradeTo(params string[] args) {
            var path = Unquote(args[0]);
            var isUpgrade = false;
            var exists = File.Exists(path);
            if (exists) {
                var existingVersion = AssemblyName.GetAssemblyName(path)?.Version;
                var thisVersion = Assembly.GetExecutingAssembly().GetName().Version;
                isUpgrade = thisVersion > existingVersion;
                if (!isUpgrade) {
                    ExitWithStatus(StatusFlags.AlreadyInstalled);
                    return;
                }
            }
            if (isUpgrade) ParseExpression(args.Skip(1).ToArray());
        }

        /// <summary>
        /// Passes assembly version from project which directory was given as first argument to project which directory was given as second argument.
        /// </summary>
        /// <param name="args">Source and target assembly paths.</param>
        private void PassAssemblyVersion(params string[] args) {
            const string assemblyInfoRelativePath = @"Properties\AssemblyInfo.cs";
            if (args.Length != 2) throw new ArgumentException("PassAssemblyVersion requires 2 project paths.");
            var source = Path.Combine(Unquote(args[0]), assemblyInfoRelativePath);
            var target = Path.Combine(Unquote(args[1]), assemblyInfoRelativePath);
            if (!File.Exists(source) || !File.Exists(target)) throw new FileNotFoundException("One of projects was not found by PassAssemblyVersion command.");
            string sourceContent = null;
            string targetContent = null;
            using (var s = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var r = new StreamReader(s)) sourceContent = r.ReadToEnd();
            using (var s = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var r = new StreamReader(s)) targetContent = r.ReadToEnd();
            var sourceAssemblyVersion = RxAssemblyVersion.Match(sourceContent).Groups[1].Value;
            var sourceFileVersion = RxAssemblyFileVersion.Match(sourceContent).Groups[1].Value;
            targetContent = RxAssemblyVersion.Replace(targetContent, e => e.Value.Replace(e.Groups[1].Value, sourceAssemblyVersion));
            targetContent = RxAssemblyFileVersion.Replace(targetContent, e => e.Value.Replace(e.Groups[1].Value, sourceFileVersion));
            using (var s = new FileStream(target, FileMode.Truncate, FileAccess.Write, FileShare.None))
            using (var w = new StreamWriter(s)) w.Write(targetContent);
        }

        #endregion

        #region Helpers

        /// <summary>
        /// Stops the script execution with specified status.
        /// </summary>
        /// <param name="s">Status to set as script result.</param>
        private void ExitWithStatus(StatusFlags s) {
            Status = s;
            if (ScriptSet.IgnoreErrors) return;
            ExitRequested = true;
            if (s != StatusFlags.OK) OnFailure(new TDiagnostics {
                ScriptName = CurrentScript,
                ScriptLine = CurrentLine,
                Status = s
            });
            //else OnSuccess();
        }

        /// <summary>
        /// Stops the script execution with process diagnostic information.
        /// </summary>
        /// <param name="p"><see cref="Process"/> instance to diagnose.</param>
        private void ExitWithStatus(Process p) {
            Status = StatusFlags.NonZeroExitCode;
            if (ScriptSet.IgnoreErrors) return;
            ExitRequested = true;
            string message = null;
            string error = null;
            string output = null;
            if (p.StartInfo.RedirectStandardInput)
                using (var o = p.StandardOutput.BaseStream)
                    if (o != null) using (var r = new StreamReader(o)) output = r.ReadToEnd();
            if (p.ExitCode != 0 && p.StartInfo.RedirectStandardError)
                using (var e = p.StandardError.BaseStream)
                    if (e != null) using (var r = new StreamReader(e)) error = r.ReadToEnd();
            if (!String.IsNullOrWhiteSpace(output)) message = output;
            if (!String.IsNullOrWhiteSpace(error)) {
                message =
                    String.IsNullOrWhiteSpace(output)
                        ? output + Environment.NewLine + error
                        : error;
            }
            if (p.ExitCode != 0 || !String.IsNullOrEmpty(error)) {
                OnFailure(new TDiagnostics {
                    ScriptName = CurrentScript,
                    ScriptLine = CurrentLine,
                    Status = Status = StatusFlags.NonZeroExitCode,
                    ErrorMessage = message
                });
            }
        }

        /// <summary>
        /// Matches and retrieves text from embedded resource.
        /// </summary>
        /// <param name="fileName">Embedded resource file name, without namespace.</param>
        /// <returns>Text read from embedded resource.</returns>
        private string GetTextFromEmbeddedFile(string fileName) {
            var assembly = Assembly.GetExecutingAssembly();
            using (var s = assembly.GetManifestResourceStream($"{assembly.EntryPoint.ReflectedType.Namespace}.{fileName}"))
            using (var r = new StreamReader(s)) return r.ReadToEnd();
        }

        /// <summary>
        /// Returns a plain name from macro expression like $(plainName),
        /// </summary>
        /// <param name="expression">Script expression.</param>
        /// <returns>Name from expression.</returns>
        private string GetName(string expression) {
            if (expression == null || expression.Length < 4 || expression[0] != '$') return null;
            return expression.Substring(2, expression.Length - 3);
        }

        /// <summary>
        /// Quotes a path if contains space.
        /// </summary>
        /// <param name="path">Path to quote.</param>
        /// <returns>Quoted path.</returns>
        private string Quote(string path) => path.Contains(' ') ? '"' + Unquote(path) + '"' : path;

        /// <summary>
        /// Removes any quotation from path string.
        /// </summary>
        /// <param name="path">Path to unquote.</param>
        /// <returns>Unquoted path.</returns>
        private string Unquote(string path) => path.Replace("\"", "");

        /// <summary>
        /// Resolves "macro names" as full, quoted paths or other string values.
        /// </summary>
        /// <param name="expression">Script expression.</param>
        /// <returns>The expression with paths resolved.</returns>
        private string Resolve(string expression) {
            string name = GetName(expression);
            if (name == null) throw new InvalidOperationException("Invalid macro expression format.");
            foreach (var field in ScriptSetFields)
                if (name == field.Name) expression = Resolve(field.GetValue(ScriptSet));
            foreach (var property in ResolvableProperties)
                if (name == property.Name) expression = Resolve(property.GetValue(Resolvable));
            if (expression == null) throw new NullReferenceException("The expression was resolved to null, which should not happen.");
            var fnMatch = RxFileNameMacro.Match(expression);
            if (fnMatch.Success) expression = Path.Combine(Unquote(ScriptSet.Target), fnMatch.Groups[1].Value);
            return (expression.Contains(' ')) ? Quote(expression) : expression;
        }

        /// <summary>
        /// Resolves "macro names" as full, quoted paths or other string values from <see cref="Regex"/> <see cref="Match"/>.
        /// </summary>
        /// <param name="match"><see cref="Match"/> object.</param>
        /// <returns></returns>
        private string Resolve(Match match) => Resolve(match.Value);

        /// <summary>
        /// Resolve boxed object value as string.
        /// </summary>
        /// <param name="boxed">Boxed <see cref="string"/>.</param>
        /// <returns>Unboxed <see cref="string"/>.</returns>
        private string Resolve(object boxed) => boxed == null ? null : (boxed is string ? (string)boxed : boxed.ToString());

        /// <summary>
        /// Parses assignments like "$(MacroName) = some text" expressions by assigning the <see cref="ScriptSet"/> fields.
        /// </summary>
        /// <param name="e">Macro name in first element, value to assign in the second one.</param>
        private void ParseAssignment(string[] e) {
            var match = RxMacroName.Match(e[0]);
            if (!match.Success) throw new InvalidOperationException("Invalid assignment.");
            var target = match.Groups[1].Value;
            var value = RxMacro.Replace(e[1], Resolve);
            var field = ScriptSetFields.FirstOrDefault(i => i.Name == target);
            if (field == null) throw new InvalidOperationException("Field does not exist.");
            if (field.FieldType == typeof(bool)) field.SetValue(ScriptSet, !RxFalsy.Match(value).Success);
            else if (field.FieldType == typeof(int)) field.SetValue(ScriptSet, Int32.Parse(value));
            else field.SetValue(ScriptSet, value);
        }

        /// <summary>
        /// FSM shell style expression splitting by space, but not when a space is a part of quoted string.
        /// </summary>
        /// <param name="e">Script expression.</param>
        /// <returns>Subexpressions.</returns>
        private string[] SplitExpression(string e) {
            var elements = new List<string>(new string[] { String.Empty });
            var backslash = false;
            var ignoreSpaces = false;
            var ignoreSpace = false;
            for (int i = 0; i < e.Length; i++) {
                if (e[i] == '\"') ignoreSpaces = !ignoreSpaces;
                ignoreSpace = (backslash && e[i] == ' ');
                if (e[i] == ' ' && !ignoreSpaces && !ignoreSpace) elements.Add(String.Empty);
                else elements[elements.Count - 1] += e[i];
                backslash = e[i] == '\\';
            }
            return elements.ToArray();
        }

        /// <summary>
        /// Parses split expression (a script line as string array) as internal or external command.
        /// </summary>
        private void ParseExpression(string[] s) {
            var command = s[0];
            if (command == ExitCommand) { ExitRequested = true; return; }
            var internalCommands = Enum.GetNames(typeof(ScriptActions));
            for (int i = 0; i < internalCommands.Length; i++) {
                if (command == $"$({internalCommands[i]})") {
                    var method = internalCommands[i];
                    try {
                        GetType().GetMethod(method, BindingFlags.NonPublic | BindingFlags.Instance).Invoke(this, new[] { s.Skip(1)?.ToArray() });
                    } catch (TargetInvocationException x) {
                        throw x.InnerException;
                    }
                    return;
                }
            }
            using (var process = new Process()) {
                process.StartInfo = new ProcessStartInfo {
                    FileName = s[0],
                    Arguments = String.Join(" ", s.Skip(1).ToArray()),
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = ScriptSet.RedirectOutput,
                    RedirectStandardError = ScriptSet.RedirectOutput
                };
                if (process.Start()) {
                    process.WaitForExit(ScriptSet.ProcessTimeout * 1000);
                    try {
                        if (process.ExitCode != 0) ExitWithStatus(process);
                    } catch (InvalidOperationException) {
                        throw new System.TimeoutException("Process hasn't exited in specified time.");
                    }
                }
            }
        }

        /// <summary>
        /// Parses expression (a script line) as internal or external command.
        /// </summary>
        /// <param name="e">Script expression.</param>
        private void ParseExpression(string e) => ParseExpression(SplitExpression(RxMacro.Replace(e, Resolve)));

        #endregion

        #region Event handlers

        /// <summary>
        /// Triggers <see cref="MessageReceived"/> event.
        /// </summary>
        /// <param name="message">Message to send.</param>
        private void OnMessageReceived(string message) => MessageReceived?.Invoke(this, message);

        /// <summary>
        /// Triggers <see cref="Notification"/> event.
        /// </summary>
        /// <param name="message">Message to show.</param>
        private void OnNotify(string message) => Notification?.Invoke(this, message);

        /// <summary>
        /// Triggers <see cref="Success"/> evemt.
        /// </summary>
        private void OnSuccess() => Success?.Invoke(this, EventArgs.Empty);

        /// <summary>
        /// Triggers <see cref="Failure"/> event.
        /// </summary>
        /// <param name="e"></param>
        private void OnFailure(TDiagnostics e) => Failure?.Invoke(this, e);

        #endregion

        #region Script types

        /// <summary>
        /// Allowed script internal actions.
        /// </summary>
        enum ScriptActions {
            Run,
            Message,
            Notify,
            Pack,
            Unpack,
            Delete,
            Kill,
            ServiceStart,
            ServiceStop,
            IfExists,
            IfNotExists,
            IfUpgradeTo,
            PassAssemblyVersion
        }

        /// <summary>
        /// Script set fields.
        /// </summary>
        class TScriptSet {
            public string Source = null;
            public string Target = null;
            public bool IgnoreErrors = false;
            public bool RedirectOutput = false;
            public int ProcessTimeout = ProcessTimeoutDefault;
        }

        /// <summary>
        /// Properties resolvable from script.
        /// </summary>
        class TResolvable {

            public string SolutionDir => _SolutionDir ?? (_SolutionDir = GetSolutionDir());
            public string Platform => _Platform ?? (_Platform = GetPlatform());
            public string ProgramFiles => _ProgramFiles ?? (_ProgramFiles = GetProgramFiles());
            public string Windows => _Windows ?? (_Windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows));
            public string System => _System ?? (_System = Environment.GetFolderPath(Environment.SpecialFolder.System));
            public string Runtime => _Runtime ?? (_Runtime = RuntimeEnvironment.GetRuntimeDirectory());
            public string NGen => _NGen ?? (_NGen = Path.Combine(Runtime, "NGen.exe"));

            #region Resolvers and caches

            private string _SolutionDir;
            private string _Platform;
            private string _ProgramFiles;
            private string _Windows;
            private string _System;
            private string _Runtime;
            private string _NGen;

            private string GetSolutionDir() {
                string here = Directory.GetCurrentDirectory();
                string solution = null;
                while (here != null) {
                    solution = Directory.EnumerateFiles(here, "*.sln").FirstOrDefault();
                    if (solution == null) here = Directory.GetParent(here)?.FullName;
                    else break;
                }
                return solution != null ? Path.GetDirectoryName(solution) : null;
            }

            private string GetPlatform()
#if x64
                => "x64";
#elif x86
                => "x86";
#else
                => "";
#endif
            

            private string GetProgramFiles() => (String.IsNullOrEmpty(Platform) || Platform == "x64" || Platform == "AnyCpu")
                    ? Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)
                    : Environment.Is64BitOperatingSystem
                        ? Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)
                        : Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);

            #endregion

        }

        #endregion

        #region Private data

        private const string ExitCommand = "$(Exit)";

        private TScriptSet ScriptSet = new TScriptSet();
        private FieldInfo[] ScriptSetFields;
        private TResolvable Resolvable = new TResolvable();
        private readonly PropertyInfo[] ResolvableProperties;

        private StatusFlags Status;
        private bool ExitRequested;
        private string CurrentScript;
        private string CurrentLine;
        private int RunCount;

        #endregion

        #region Parser regular expressions

        /// <summary>
        /// Splits text into lines.
        /// </summary>
        private static Regex RxLines = new Regex(@"\r?\n", RegexOptions.Compiled);
        /// <summary>
        /// Splits assignment expression.
        /// </summary>
        private static Regex RxAssignment = new Regex(@"\s*=\s*", RegexOptions.Compiled);
        /// <summary>
        /// Extracts macro expression.
        /// </summary>
        private static Regex RxMacro = new Regex(@"\$\([^\(\)\$]+\)", RegexOptions.Compiled);
        /// <summary>
        /// Extracts name from macro expression.
        /// </summary>
        private static Regex RxMacroName = new Regex(@"\$\((.+)\)", RegexOptions.Compiled);
        /// <summary>
        /// Extracts file name and extension from macro expression. 
        /// </summary>
        private static Regex RxFileNameMacro = new Regex(@"^\$\(([^\)]+\.[a-z]{2,})\)$", RegexOptions.Compiled);
        /// <summary>
        /// Matches keywords resolved as logical false.
        /// </summary>
        private static Regex RxFalsy = new Regex(@"(?:0+|false|no|nope|off|disable)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        /// <summary>
        /// Matches assembly version in project file.
        /// </summary>
        private static Regex RxAssemblyVersion = new Regex(@"\[assembly: *AssemblyVersion\(""([0-9.]+)""\)\]", RegexOptions.Compiled);
        /// <summary>
        /// Matches assemblu file version in project file.
        /// </summary>
        private static Regex RxAssemblyFileVersion = new Regex(@"\[assembly: *AssemblyFileVersion\(""([0-9.]+)""\)\]", RegexOptions.Compiled);

        #endregion

    }

}