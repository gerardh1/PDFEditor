using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: AssemblyTitle("ArcoCat PDF Editor Setup")]
[assembly: AssemblyDescription("Windows Installer for ArcoCat PDF Editor — PDF Reader & Editor")]
[assembly: AssemblyCompany("ArcoCat PDF Studio")]
[assembly: AssemblyProduct("ArcoCat PDF Editor")]
[assembly: AssemblyVersion("1.3.0.0")]
[assembly: AssemblyFileVersion("1.3.0.0")]

namespace ArcoCatPdfEditorSetup
{
    internal static class Program
    {
        public const string AppName = "ArcoCat PDF Editor";
        public const string AppId = "AcroCatPdfEditor";
        public const string ExeName = "AcroCatPdfEditor.exe";
        public const string AppVersion = "1.3.0";
        public const string UninstallRegKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\" + AppId;

        public static void StopRunningAppInstances()
        {
            string[] procNames = { "AcroCatPdfEditor", "ArcoCatPdfEditor", "AcrobatPdfEditor" };
            foreach (string name in procNames)
            {
                foreach (var proc in Process.GetProcessesByName(name))
                {
                    try { proc.Kill(); proc.WaitForExit(2000); } catch { }
                }
            }
        }

        [STAThread]
        private static int Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            bool isSilent = false;
            bool isUninstall = false;
            string customDir = null;

            foreach (string rawArg in args)
            {
                string arg = rawArg.Trim();
                if (arg.Equals("/S", StringComparison.OrdinalIgnoreCase) ||
                    arg.Equals("--silent", StringComparison.OrdinalIgnoreCase) ||
                    arg.Equals("/quiet", StringComparison.OrdinalIgnoreCase))
                {
                    isSilent = true;
                }
                else if (arg.Equals("/uninstall", StringComparison.OrdinalIgnoreCase) ||
                         arg.Equals("--uninstall", StringComparison.OrdinalIgnoreCase))
                {
                    isUninstall = true;
                }
                else if (arg.StartsWith("--dir=", StringComparison.OrdinalIgnoreCase))
                {
                    customDir = arg.Substring("--dir=".Length).Trim('"');
                }
            }

            if (isUninstall)
            {
                return RunUninstaller(isSilent);
            }

            string defaultInstallDir = !string.IsNullOrWhiteSpace(customDir)
                ? NormalizeTargetInstallDirectory(customDir)
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", AppId);

            if (isSilent)
            {
                try
                {
                    InstallEngine.PerformInstallation(
                        defaultInstallDir,
                        createDesktopShortcut: true,
                        createStartMenuShortcut: true,
                        registerPdfContextMenu: true,
                        progressCallback: null);
                    return 0;
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine("Installation failed: " + ex.Message);
                    return 1;
                }
            }

            using (var form = new SetupForm(defaultInstallDir))
            {
                Application.Run(form);
                return form.InstallSucceeded ? 0 : 1;
            }
        }

        public static string NormalizeTargetInstallDirectory(string rawPath)
        {
            if (string.IsNullOrWhiteSpace(rawPath))
            {
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", AppId);
            }

            string fullPath = Path.GetFullPath(rawPath.Trim()).TrimEnd('\\', '/');
            string leafName = Path.GetFileName(fullPath);

            bool isDedicatedAppSubfolder =
                string.Equals(leafName, AppId, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(leafName, "ArcoCatPdfEditor", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(leafName, "AcroCatPdfEditor", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(leafName, AppName, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(leafName, "AcroCat PDF Editor", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(leafName, "AcrobatPdfEditor", StringComparison.OrdinalIgnoreCase);

            // If user selected a drive root, a protected Windows/User folder (such as Downloads, Desktop, Documents),
            // or an existing non-empty folder that isn't already our dedicated app folder, append \ArcoCatPdfEditor
            if (!isDedicatedAppSubfolder)
            {
                if (IsProtectedSystemOrUserFolder(fullPath))
                {
                    return Path.Combine(fullPath, AppId);
                }

                if (Directory.Exists(fullPath) && !File.Exists(Path.Combine(fullPath, "install-manifest.txt")))
                {
                    try
                    {
                        if (Directory.GetFileSystemEntries(fullPath).Length > 0)
                        {
                            return Path.Combine(fullPath, AppId);
                        }
                    }
                    catch
                    {
                        return Path.Combine(fullPath, AppId);
                    }
                }
            }

            return fullPath;
        }

        public static bool IsProtectedSystemOrUserFolder(string dir)
        {
            if (string.IsNullOrWhiteSpace(dir))
                return true;

            string full;
            try
            {
                full = Path.GetFullPath(dir).TrimEnd('\\', '/');
            }
            catch
            {
                return true;
            }

            // Drive root (e.g. C: or C:\)
            string root = Path.GetPathRoot(full);
            if (!string.IsNullOrEmpty(root) &&
                string.Equals(full, root.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            // Never delete a source code repository directory
            if (File.Exists(Path.Combine(full, "PDFEditor.sln")) ||
                Directory.Exists(Path.Combine(full, "AcrobatPdfEditor")))
            {
                return true;
            }

            string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile).TrimEnd('\\', '/');
            string[] protectedPaths =
            {
                userProfile,
                Path.Combine(userProfile, "Downloads"),
                Path.Combine(userProfile, "Desktop"),
                Path.Combine(userProfile, "Documents"),
                Path.Combine(userProfile, "Pictures"),
                Path.Combine(userProfile, "Music"),
                Path.Combine(userProfile, "Videos"),
                Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
                Environment.GetFolderPath(Environment.SpecialFolder.MyMusic),
                Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs"),
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                Environment.GetFolderPath(Environment.SpecialFolder.System)
            };

            foreach (string p in protectedPaths)
            {
                if (!string.IsNullOrWhiteSpace(p) &&
                    string.Equals(full, p.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            // Also protect any folder whose parent is C:\Users (e.g. C:\Users\Administrator) or any direct child shell folder
            string leaf = Path.GetFileName(full);
            if (string.Equals(leaf, "Downloads", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(leaf, "Desktop", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(leaf, "Documents", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(leaf, "Users", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(leaf, "Windows", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(leaf, "Program Files", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(leaf, "Program Files (x86)", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return false;
        }

        private static void EnsureUserDownloadsFolderIntact()
        {
            try
            {
                string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                if (!string.IsNullOrWhiteSpace(userProfile))
                {
                    string downloadsDir = Path.Combine(userProfile, "Downloads");
                    if (!Directory.Exists(downloadsDir))
                    {
                        Directory.CreateDirectory(downloadsDir);
                    }
                }
            }
            catch { }
        }

        public static string GetInstalledLocation()
        {
            string[] regKeys =
            {
                UninstallRegKey,
                @"Software\Microsoft\Windows\CurrentVersion\Uninstall\ArcoCatPdfEditor",
                @"Software\Microsoft\Windows\CurrentVersion\Uninstall\AcrobatPdfEditor"
            };

            foreach (string keyPath in regKeys)
            {
                try
                {
                    using (var reg = Registry.CurrentUser.OpenSubKey(keyPath))
                    {
                        if (reg != null)
                        {
                            string loc = reg.GetValue("InstallLocation") as string;
                            if (!string.IsNullOrWhiteSpace(loc))
                            {
                                return loc.TrimEnd('\\', '/');
                            }
                        }
                    }
                }
                catch { }
            }

            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", AppId);
        }

        private static void SafeUninstallAppFilesFromDirectory(string dir, string currentExePath)
        {
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
                return;

            // Never touch source repo directory
            if (File.Exists(Path.Combine(dir, "PDFEditor.sln")) ||
                Directory.Exists(Path.Combine(dir, "AcrobatPdfEditor")))
            {
                return;
            }

            // 1. Delete only known application files or files listed in install-manifest.txt
            var filesToRemove = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                Program.ExeName,
                "AcroCatPdfEditor.exe",
                "ArcoCatPdfEditor.exe",
                "AcrobatPdfEditor.exe",
                "README.md",
                "ArcoCatIcon.ico",
                "ArcoCatIcon.png",
                "Sample_Enterprise_Agreement.pdf",
                "install-manifest.txt",
                "Uninstall.exe"
            };

            string manifestPath = Path.Combine(dir, "install-manifest.txt");
            if (File.Exists(manifestPath))
            {
                try
                {
                    foreach (string line in File.ReadAllLines(manifestPath))
                    {
                        string trimmed = line.Trim();
                        if (!string.IsNullOrEmpty(trimmed) && !trimmed.Contains(".."))
                        {
                            filesToRemove.Add(trimmed);
                        }
                    }
                }
                catch { }
            }

            string deferredSelfFile = null;
            foreach (string relFile in filesToRemove)
            {
                string fullFile = Path.Combine(dir, relFile);
                if (!File.Exists(fullFile))
                    continue;

                if (!string.IsNullOrEmpty(currentExePath) &&
                    string.Equals(Path.GetFullPath(fullFile), Path.GetFullPath(currentExePath), StringComparison.OrdinalIgnoreCase))
                {
                    deferredSelfFile = fullFile;
                    continue;
                }

                try
                {
                    File.Delete(fullFile);
                }
                catch { }
            }

            bool canRemoveEmptyFolder = !IsProtectedSystemOrUserFolder(dir);

            if (deferredSelfFile != null)
            {
                // Delete only the running uninstaller file, and use 'rmdir' WITHOUT '/s' so the folder is removed ONLY if 100% empty
                string cmdArgs = canRemoveEmptyFolder
                    ? string.Format("/C timeout /t 2 /nobreak > NUL & del /f /q \"{0}\" & rmdir \"{1}\" 2>NUL", deferredSelfFile, dir)
                    : string.Format("/C timeout /t 2 /nobreak > NUL & del /f /q \"{0}\"", deferredSelfFile);

                Process.Start(new ProcessStartInfo("cmd.exe", cmdArgs)
                {
                    WindowStyle = ProcessWindowStyle.Hidden,
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    WorkingDirectory = Path.GetTempPath()
                });
            }
            else if (canRemoveEmptyFolder)
            {
                // Directory.Delete(dir, false) only deletes the directory if it is completely empty
                try
                {
                    if (Directory.GetFileSystemEntries(dir).Length == 0)
                    {
                        Directory.Delete(dir, false);
                    }
                }
                catch { }
            }
        }

        public static int RunUninstaller(bool isSilent)
        {
            string installDir = GetInstalledLocation();
            string programsRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs");
            string[] candidateDirs =
            {
                installDir,
                Path.Combine(programsRoot, "AcroCatPdfEditor"),
                Path.Combine(programsRoot, "ArcoCatPdfEditor"),
                Path.Combine(programsRoot, "AcrobatPdfEditor")
            };

            if (!isSilent)
            {
                var res = MessageBox.Show(
                    "Are you sure you want to remove " + AppName + " and its shortcuts from your computer?\n\nInstalled Location: " + installDir + "\n(Only " + AppName + " application files will be removed; your personal files and folders will not be touched.)",
                    "Uninstall " + AppName,
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Question);

                if (res != DialogResult.Yes)
                    return 0;
            }

            try
            {
                StopRunningAppInstances();

                string[] shortcuts =
                {
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), AppName + ".lnk"),
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "AcroCat PDF Editor.lnk"),
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Acrobat Pro PDF Studio.lnk"),
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), AppName + ".lnk"),
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "AcroCat PDF Editor.lnk"),
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Uninstall " + AppName + ".lnk"),
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Uninstall AcroCat PDF Editor.lnk"),
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Acrobat Pro PDF Studio.lnk"),
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Uninstall Acrobat Pro PDF Studio.lnk")
                };

                foreach (string lnk in shortcuts)
                {
                    if (File.Exists(lnk)) File.Delete(lnk);
                }

                Registry.CurrentUser.DeleteSubKeyTree(UninstallRegKey, false);
                Registry.CurrentUser.DeleteSubKeyTree(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\ArcoCatPdfEditor", false);
                Registry.CurrentUser.DeleteSubKeyTree(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\AcrobatPdfEditor", false);
                Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\SystemFileAssociations\.pdf\shell\AcroCatPdfEditor", false);
                Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\SystemFileAssociations\.pdf\shell\ArcoCatPdfEditor", false);
                Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\SystemFileAssociations\.pdf\shell\AcrobatPdfEditor", false);

                string currentExePath = Process.GetCurrentProcess().MainModule.FileName;
                var visited = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (string dir in candidateDirs)
                {
                    if (!string.IsNullOrWhiteSpace(dir) && visited.Add(dir))
                    {
                        SafeUninstallAppFilesFromDirectory(dir, currentExePath);
                    }
                }

                EnsureUserDownloadsFolderIntact();

                if (!isSilent)
                {
                    MessageBox.Show(AppName + " has been uninstalled from your computer.", "Uninstall Complete", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                return 0;
            }
            catch (Exception ex)
            {
                EnsureUserDownloadsFolderIntact();
                if (!isSilent)
                {
                    MessageBox.Show("Uninstall encountered an error:\n" + ex.Message, "Uninstall Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
                return 1;
            }
        }
    }

    internal static class InstallEngine
    {
        private static void Report(Action<int, string> callback, int pct, string msg)
        {
            if (callback != null)
            {
                callback(pct, msg);
            }
        }

        public static void PerformInstallation(
            string targetDir,
            bool createDesktopShortcut,
            bool createStartMenuShortcut,
            bool registerPdfContextMenu,
            Action<int, string> progressCallback)
        {
            targetDir = Program.NormalizeTargetInstallDirectory(targetDir);

            Report(progressCallback, 5, "Preparing installation directory...");
            Program.StopRunningAppInstances();

            Directory.CreateDirectory(targetDir);

            // Remove any old AcrobatPdfEditor.exe in target directory if upgrading from earlier build
            string oldExeInTarget = Path.Combine(targetDir, "AcrobatPdfEditor.exe");
            if (File.Exists(oldExeInTarget))
            {
                try { File.Delete(oldExeInTarget); } catch { }
            }

            var installedRelativeFiles = new System.Collections.Generic.List<string>();

            Report(progressCallback, 15, "Extracting ArcoCat PDF Editor package...");
            var asm = Assembly.GetExecutingAssembly();
            using (var resStream = asm.GetManifestResourceStream("payload.zip"))
            {
                if (resStream == null)
                    throw new InvalidOperationException("Embedded application payload ('payload.zip') was not found in installer.");

                using (var archive = new ZipArchive(resStream, ZipArchiveMode.Read))
                {
                    int totalEntries = Math.Max(1, archive.Entries.Count);
                    int current = 0;
                    foreach (var entry in archive.Entries)
                    {
                        current++;
                        string destPath = Path.GetFullPath(Path.Combine(targetDir, entry.FullName));
                        if (!destPath.StartsWith(Path.GetFullPath(targetDir), StringComparison.OrdinalIgnoreCase))
                            continue;

                        if (string.IsNullOrEmpty(entry.Name))
                        {
                            Directory.CreateDirectory(destPath);
                            continue;
                        }

                        Directory.CreateDirectory(Path.GetDirectoryName(destPath));
                        using (var inStream = entry.Open())
                        using (var outStream = File.Create(destPath))
                        {
                            byte[] buffer = new byte[81920];
                            int read;
                            while ((read = inStream.Read(buffer, 0, buffer.Length)) > 0)
                            {
                                outStream.Write(buffer, 0, read);
                            }
                        }

                        installedRelativeFiles.Add(entry.FullName);
                        int pct = 15 + (int)(65.0 * current / totalEntries);
                        Report(progressCallback, pct, "Installing: " + entry.Name);
                    }
                }
            }

            string installedExePath = Path.Combine(targetDir, Program.ExeName);
            string uninstallerExePath = Path.Combine(targetDir, "Uninstall.exe");

            Report(progressCallback, 82, "Creating uninstaller & manifest...");
            string currentInstallerPath = Process.GetCurrentProcess().MainModule.FileName;
            File.Copy(currentInstallerPath, uninstallerExePath, true);
            installedRelativeFiles.Add("Uninstall.exe");
            installedRelativeFiles.Add("install-manifest.txt");
            File.WriteAllLines(Path.Combine(targetDir, "install-manifest.txt"), installedRelativeFiles.ToArray());

            if (createDesktopShortcut)
            {
                Report(progressCallback, 88, "Creating Desktop shortcut...");
                string desktopLnk = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), Program.AppName + ".lnk");
                CreateWindowsShortcut(desktopLnk, installedExePath, targetDir, "Open and edit PDF documents with " + Program.AppName, null);
            }

            if (createStartMenuShortcut)
            {
                Report(progressCallback, 92, "Creating Start Menu shortcuts...");
                string startMenuLnk = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), Program.AppName + ".lnk");
                CreateWindowsShortcut(startMenuLnk, installedExePath, targetDir, "Open and edit PDF documents with " + Program.AppName, null);

                string uninstallMenuLnk = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Uninstall " + Program.AppName + ".lnk");
                CreateWindowsShortcut(uninstallMenuLnk, uninstallerExePath, targetDir, "Uninstall " + Program.AppName, "--uninstall");
            }

            if (registerPdfContextMenu)
            {
                Report(progressCallback, 95, "Registering Windows Explorer '.pdf' integration...");
                try
                {
                    using (var key = Registry.CurrentUser.CreateSubKey(@"Software\Classes\SystemFileAssociations\.pdf\shell\ArcoCatPdfEditor"))
                    {
                        if (key != null)
                        {
                            key.SetValue("", "Edit with " + Program.AppName);
                            key.SetValue("Icon", "\"" + installedExePath + "\",0");
                        }
                    }
                    using (var cmdKey = Registry.CurrentUser.CreateSubKey(@"Software\Classes\SystemFileAssociations\.pdf\shell\ArcoCatPdfEditor\command"))
                    {
                        if (cmdKey != null)
                        {
                            cmdKey.SetValue("", "\"" + installedExePath + "\" \"%1\"");
                        }
                    }
                }
                catch
                {
                    // Non-fatal if policy restricts shell association
                }
            }

            Report(progressCallback, 98, "Registering in Windows Installed Apps...");
            try
            {
                using (var reg = Registry.CurrentUser.CreateSubKey(Program.UninstallRegKey))
                {
                    if (reg != null)
                    {
                        reg.SetValue("DisplayName", Program.AppName);
                        reg.SetValue("DisplayVersion", Program.AppVersion);
                        reg.SetValue("Publisher", "ArcoCat PDF Studio");
                        reg.SetValue("DisplayIcon", installedExePath);
                        reg.SetValue("InstallLocation", targetDir);
                        reg.SetValue("UninstallString", "\"" + uninstallerExePath + "\" --uninstall");
                        reg.SetValue("NoModify", 1, RegistryValueKind.DWord);
                        reg.SetValue("NoRepair", 1, RegistryValueKind.DWord);
                    }
                }
            }
            catch
            {
                // Ignore if registry write is restricted
            }

            // Replace any old AcrobatPdfEditor.exe in legacy folders with AcroCatPdfEditor.exe
            string programsRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs");
            foreach (string oldFolderName in new[] { "AcrobatPdfEditor", "ArcoCatPdfEditor" })
            {
                string oldDir = Path.Combine(programsRoot, oldFolderName);
                if (!string.Equals(Path.GetFullPath(targetDir), Path.GetFullPath(oldDir), StringComparison.OrdinalIgnoreCase) &&
                    Directory.Exists(oldDir))
                {
                    try
                    {
                        string oldExe = Path.Combine(oldDir, "AcrobatPdfEditor.exe");
                        if (File.Exists(oldExe)) File.Delete(oldExe);
                        File.Copy(installedExePath, Path.Combine(oldDir, Program.ExeName), true);
                        File.Copy(uninstallerExePath, Path.Combine(oldDir, "Uninstall.exe"), true);
                    }
                    catch { }
                }
            }

            Report(progressCallback, 100, "Installation completed successfully!");
        }

        private static void CreateWindowsShortcut(string shortcutPath, string targetExe, string workingDir, string description, string arguments)
        {
            try
            {
                Type shellType = Type.GetTypeFromProgID("WScript.Shell");
                if (shellType == null) return;
                dynamic shell = Activator.CreateInstance(shellType);
                dynamic shortcut = shell.CreateShortcut(shortcutPath);
                shortcut.TargetPath = targetExe;
                if (!string.IsNullOrEmpty(arguments))
                {
                    shortcut.Arguments = arguments;
                }
                shortcut.WorkingDirectory = workingDir;
                shortcut.Description = description;
                shortcut.IconLocation = targetExe + ",0";
                shortcut.Save();
            }
            catch
            {
                // Ignore shortcut creation errors in restricted environments
            }
        }
    }

    internal sealed class SetupForm : Form
    {
        private readonly TextBox _pathBox;
        private readonly Button _browseBtn;
        private readonly CheckBox _chkDesktop;
        private readonly CheckBox _chkStartMenu;
        private readonly CheckBox _chkPdfContext;
        private readonly CheckBox _chkLaunchAfter;
        private readonly ProgressBar _progressBar;
        private readonly Label _statusLabel;
        private readonly Button _installBtn;
        private readonly Button _cancelBtn;
        private readonly Bitmap _logoBitmap;

        private bool _installFinished;
        public bool InstallSucceeded { get; private set; }

        public SetupForm(string defaultInstallDir)
        {
            Text = Program.AppName + " v" + Program.AppVersion + " — Windows Setup";
            ClientSize = new Size(580, 440);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Color.FromArgb(24, 28, 36);
            ForeColor = Color.FromArgb(241, 245, 249);
            Font = new Font("Segoe UI", 9.5f, FontStyle.Regular);

            try
            {
                Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            }
            catch { }

            try
            {
                var asm = Assembly.GetExecutingAssembly();
                using (var iconStream = asm.GetManifestResourceStream("ArcoCatIcon.png"))
                {
                    if (iconStream != null)
                    {
                        _logoBitmap = new Bitmap(iconStream);
                    }
                }
            }
            catch { }

            var headerPanel = new Panel
            {
                Dock = DockStyle.Top,
                Height = 86,
                BackColor = Color.FromArgb(18, 20, 26)
            };
            headerPanel.Paint += (s, e) =>
            {
                e.Graphics.SmoothingMode = SmoothingMode.HighQuality;
                e.Graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;

                using (var brush = new SolidBrush(Color.FromArgb(229, 57, 53)))
                {
                    e.Graphics.FillRectangle(brush, 0, 0, headerPanel.Width, 5);
                }

                if (_logoBitmap != null)
                {
                    e.Graphics.DrawImage(_logoBitmap, new Rectangle(18, 16, 56, 56));
                }

                using (var fTitle = new Font("Segoe UI", 15f, FontStyle.Bold))
                using (var fSub = new Font("Segoe UI", 9.5f, FontStyle.Regular))
                using (var subBrush = new SolidBrush(Color.FromArgb(148, 163, 184)))
                {
                    e.Graphics.DrawString(Program.AppName + " Setup", fTitle, Brushes.White, new PointF(84, 20));
                    e.Graphics.DrawString("Install the full-featured Windows PDF Reader & Editor (Self-Contained x64)", fSub, subBrush, new PointF(86, 48));
                }
            };
            Controls.Add(headerPanel);

            var lblFolder = new Label
            {
                Text = "Installation Folder:",
                Location = new Point(24, 102),
                AutoSize = true,
                Font = new Font("Segoe UI", 9.5f, FontStyle.Bold),
                ForeColor = Color.FromArgb(203, 213, 225)
            };
            Controls.Add(lblFolder);

            _pathBox = new TextBox
            {
                Text = defaultInstallDir,
                Location = new Point(24, 126),
                Width = 432,
                BackColor = Color.FromArgb(15, 18, 24),
                ForeColor = Color.White,
                BorderStyle = BorderStyle.FixedSingle,
                Font = new Font("Segoe UI", 10f)
            };
            Controls.Add(_pathBox);

            _browseBtn = new Button
            {
                Text = "Browse...",
                Location = new Point(466, 124),
                Size = new Size(90, 28),
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(38, 43, 53),
                ForeColor = Color.White,
                Cursor = Cursors.Hand
            };
            _browseBtn.FlatAppearance.BorderColor = Color.FromArgb(64, 74, 90);
            _browseBtn.Click += BrowseBtn_Click;
            Controls.Add(_browseBtn);

            var optionsGroup = new GroupBox
            {
                Text = "Setup Options & Shortcuts",
                Location = new Point(24, 168),
                Size = new Size(532, 138),
                ForeColor = Color.FromArgb(147, 197, 253)
            };

            _chkDesktop = new CheckBox
            {
                Text = "Create a Desktop shortcut (" + Program.AppName + ")",
                Checked = true,
                Location = new Point(16, 28),
                AutoSize = true,
                ForeColor = Color.FromArgb(226, 232, 240)
            };
            _chkStartMenu = new CheckBox
            {
                Text = "Create Start Menu program & uninstall shortcuts",
                Checked = true,
                Location = new Point(16, 56),
                AutoSize = true,
                ForeColor = Color.FromArgb(226, 232, 240)
            };
            _chkPdfContext = new CheckBox
            {
                Text = "Add 'Edit with " + Program.AppName + "' to Windows Explorer .pdf menu",
                Checked = true,
                Location = new Point(16, 84),
                AutoSize = true,
                ForeColor = Color.FromArgb(226, 232, 240)
            };
            _chkLaunchAfter = new CheckBox
            {
                Text = "Launch " + Program.AppName + " when setup finishes",
                Checked = true,
                Location = new Point(16, 110),
                AutoSize = true,
                ForeColor = Color.FromArgb(134, 239, 172)
            };

            optionsGroup.Controls.Add(_chkDesktop);
            optionsGroup.Controls.Add(_chkStartMenu);
            optionsGroup.Controls.Add(_chkPdfContext);
            optionsGroup.Controls.Add(_chkLaunchAfter);
            Controls.Add(optionsGroup);

            _statusLabel = new Label
            {
                Text = "Ready to install " + Program.AppName + " on this computer.",
                Location = new Point(24, 320),
                Size = new Size(532, 22),
                ForeColor = Color.FromArgb(148, 163, 184)
            };
            Controls.Add(_statusLabel);

            _progressBar = new ProgressBar
            {
                Location = new Point(24, 345),
                Size = new Size(532, 22),
                Minimum = 0,
                Maximum = 100,
                Value = 0
            };
            Controls.Add(_progressBar);

            var uninstallBtn = new Button
            {
                Text = "Uninstall Existing",
                Location = new Point(24, 386),
                Size = new Size(140, 36),
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(59, 29, 36),
                ForeColor = Color.FromArgb(252, 165, 165),
                Font = new Font("Segoe UI", 9.5f, FontStyle.Bold),
                Cursor = Cursors.Hand
            };
            uninstallBtn.FlatAppearance.BorderColor = Color.FromArgb(127, 29, 29);
            uninstallBtn.Click += (s, e) =>
            {
                if (Program.RunUninstaller(false) == 0)
                {
                    InstallSucceeded = true;
                    Close();
                }
            };
            Controls.Add(uninstallBtn);

            _installBtn = new Button
            {
                Text = "Install Now",
                Location = new Point(332, 386),
                Size = new Size(125, 36),
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(211, 47, 47),
                ForeColor = Color.White,
                Font = new Font("Segoe UI", 10f, FontStyle.Bold),
                Cursor = Cursors.Hand
            };
            _installBtn.FlatAppearance.BorderColor = Color.FromArgb(239, 68, 68);
            _installBtn.Click += InstallBtn_Click;
            Controls.Add(_installBtn);

            _cancelBtn = new Button
            {
                Text = "Cancel",
                Location = new Point(466, 386),
                Size = new Size(90, 36),
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(38, 43, 53),
                ForeColor = Color.White,
                Cursor = Cursors.Hand
            };
            _cancelBtn.FlatAppearance.BorderColor = Color.FromArgb(64, 74, 90);
            _cancelBtn.Click += (s, e) => Close();
            Controls.Add(_cancelBtn);
        }

        private void BrowseBtn_Click(object sender, EventArgs e)
        {
            using (var fbd = new FolderBrowserDialog())
            {
                fbd.Description = "Select destination folder for " + Program.AppName + " (an '" + Program.AppId + "' subfolder will be used automatically if you pick a shared folder like Downloads)";
                fbd.SelectedPath = _pathBox.Text;
                if (fbd.ShowDialog(this) == DialogResult.OK)
                {
                    _pathBox.Text = Program.NormalizeTargetInstallDirectory(fbd.SelectedPath);
                }
            }
        }

        private async void InstallBtn_Click(object sender, EventArgs e)
        {
            if (_installFinished)
            {
                if (_chkLaunchAfter.Checked)
                {
                    string exePath = Path.Combine(_pathBox.Text.Trim(), Program.ExeName);
                    if (File.Exists(exePath))
                    {
                        Process.Start(new ProcessStartInfo(exePath) { UseShellExecute = true });
                    }
                }
                Close();
                return;
            }

            string rawDir = _pathBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(rawDir))
            {
                MessageBox.Show(this, "Please choose a valid installation directory.", "Setup", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            string targetDir = Program.NormalizeTargetInstallDirectory(rawDir);
            _pathBox.Text = targetDir;

            _installBtn.Enabled = false;
            _browseBtn.Enabled = false;
            _pathBox.ReadOnly = true;
            _chkDesktop.Enabled = false;
            _chkStartMenu.Enabled = false;
            _chkPdfContext.Enabled = false;

            bool createDesktop = _chkDesktop.Checked;
            bool createStartMenu = _chkStartMenu.Checked;
            bool regPdf = _chkPdfContext.Checked;

            try
            {
                await Task.Run(() =>
                {
                    InstallEngine.PerformInstallation(
                        targetDir,
                        createDesktop,
                        createStartMenu,
                        regPdf,
                        (pct, msg) =>
                        {
                            if (IsHandleCreated)
                            {
                                BeginInvoke((Action)(() =>
                                {
                                    _progressBar.Value = Math.Min(100, Math.Max(0, pct));
                                    _statusLabel.Text = msg;
                                }));
                            }
                        });
                });

                InstallSucceeded = true;
                _installFinished = true;
                _progressBar.Value = 100;
                _statusLabel.ForeColor = Color.FromArgb(134, 239, 172);
                _statusLabel.Text = "✔ " + Program.AppName + " has been installed to '" + targetDir + "'.";
                _installBtn.BackColor = Color.FromArgb(21, 128, 61);
                _installBtn.FlatAppearance.BorderColor = Color.FromArgb(34, 197, 94);
                _installBtn.Text = "Finish";
                _installBtn.Enabled = true;
                _cancelBtn.Visible = false;
            }
            catch (Exception ex)
            {
                _statusLabel.ForeColor = Color.FromArgb(248, 113, 113);
                _statusLabel.Text = "Installation failed: " + ex.Message;
                MessageBox.Show(this, "Installation failed:\n" + ex.Message, "Setup Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                _installBtn.Enabled = true;
                _browseBtn.Enabled = true;
                _pathBox.ReadOnly = false;
            }
        }
    }
}
