// PDF Reader's installer and uninstaller. Installs for the current user, so no admin rights are needed.
// It offers the app for PDFs but never changes the default; Windows leaves that choice to the person.
//   PDFReaderSetup-<version>.exe       install, with a window
//   PDFReaderSetup-<version>.exe /uninstall  uninstall, with a window
//   add /silent to either for no window
using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

[assembly: TargetFramework(".NETFramework,Version=v4.8")]
[assembly: AssemblyTitle("PDF Reader Setup")]
[assembly: AssemblyProduct("PDF Reader")]
[assembly: AssemblyVersion("1.1.0.0")]

namespace PdfReaderSetup
{
    static class Installer
    {
        public const string Version = "1.1.0";
        const string AppName = "PDF Reader";
        const string ProgId = "PdfReader.Document";
        const string SetupName = "Setup.exe";

        // For tests: puts every file and registry key in a sandbox.
        static readonly string Test = Environment.GetEnvironmentVariable("PDFREADER_TEST_SETUP");
        static readonly string LocalData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        public static string AppDir { get { return Test != null ? Path.Combine(Test, "app") : Path.Combine(LocalData, "Programs", AppName); } }
        public static string AppExe { get { return Path.Combine(AppDir, "PdfReader.exe"); } }
        static string DataDir { get { return Test != null ? Path.Combine(Test, "data") : Path.Combine(LocalData, "PdfReader"); } }
        static string MenuDir { get { return Test != null ? Path.Combine(Test, "menu") : Environment.GetFolderPath(Environment.SpecialFolder.Programs); } }
        static string Shortcut { get { return Path.Combine(MenuDir, AppName + ".lnk"); } }
        static string Root { get { return Test != null ? @"Software\PdfReaderSetupTest\" : @"Software\"; } }
        static string UninstallKey { get { return Root + @"Microsoft\Windows\CurrentVersion\Uninstall\PDFReader"; } }
        static string CapabilitiesKey { get { return Root + @"PDFReader\Capabilities"; } }

        [DllImport("shell32.dll")]
        static extern void SHChangeNotify(int eventId, uint flags, IntPtr a, IntPtr b);
        static void AssociationsChanged() { SHChangeNotify(0x08000000, 0, IntPtr.Zero, IntPtr.Zero); }

        public static bool IsInstalled { get { return File.Exists(AppExe); } }

        public static void Install()
        {
            Directory.CreateDirectory(AppDir);
            try
            {
                using (Stream payload = Assembly.GetExecutingAssembly().GetManifestResourceStream("PdfReader.exe"))
                using (var file = new FileStream(AppExe, FileMode.Create, FileAccess.Write))
                    payload.CopyTo(file);
                string self = Assembly.GetExecutingAssembly().Location;
                string kept = Path.Combine(AppDir, SetupName);
                if (!string.Equals(Path.GetFullPath(self), Path.GetFullPath(kept), StringComparison.OrdinalIgnoreCase))
                    File.Copy(self, kept, true);
            }
            catch (IOException) { throw new InvalidOperationException("Please close PDF Reader and try again."); }
            catch (UnauthorizedAccessException) { throw new InvalidOperationException("Please close PDF Reader and try again."); }

            Directory.CreateDirectory(MenuDir);
            MakeShortcut();

            string quoted = "\"" + AppExe + "\"";
            string setup = "\"" + Path.Combine(AppDir, SetupName) + "\"";
            using (RegistryKey k = Registry.CurrentUser.CreateSubKey(Root + @"Classes\" + ProgId))
            {
                k.SetValue("", "PDF Document");
                k.SetValue("FriendlyTypeName", "PDF Document");
                using (RegistryKey i = k.CreateSubKey("DefaultIcon")) i.SetValue("", AppExe + ",0");
                using (RegistryKey c = k.CreateSubKey(@"shell\open\command")) c.SetValue("", quoted + " \"%1\"");
            }
            using (RegistryKey k = Registry.CurrentUser.CreateSubKey(Root + @"Classes\.pdf\OpenWithProgids"))
                k.SetValue(ProgId, "");
            using (RegistryKey k = Registry.CurrentUser.CreateSubKey(CapabilitiesKey))
            {
                k.SetValue("ApplicationName", AppName);
                k.SetValue("ApplicationDescription", "Read, print and ask AI about PDFs.");
                using (RegistryKey a = k.CreateSubKey("FileAssociations")) a.SetValue(".pdf", ProgId);
            }
            using (RegistryKey k = Registry.CurrentUser.CreateSubKey(Root + "RegisteredApplications"))
                k.SetValue(AppName, @"Software\PDFReader\Capabilities");
            using (RegistryKey k = Registry.CurrentUser.CreateSubKey(UninstallKey))
            {
                k.SetValue("DisplayName", AppName);
                k.SetValue("DisplayVersion", Version);
                k.SetValue("Publisher", "Nepal Plummer");
                k.SetValue("DisplayIcon", AppExe + ",0");
                k.SetValue("InstallLocation", AppDir);
                k.SetValue("UninstallString", setup + " /uninstall");
                k.SetValue("QuietUninstallString", setup + " /uninstall /silent");
                k.SetValue("NoModify", 1, RegistryValueKind.DWord);
                k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
                k.SetValue("EstimatedSize", (int)(new FileInfo(AppExe).Length / 1024), RegistryValueKind.DWord);
            }
            AssociationsChanged();
        }

        static void MakeShortcut()
        {
            Type shellType = Type.GetTypeFromProgID("WScript.Shell");
            object shell = Activator.CreateInstance(shellType);
            object link = shellType.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { Shortcut });
            Type t = link.GetType();
            t.InvokeMember("TargetPath", BindingFlags.SetProperty, null, link, new object[] { AppExe });
            t.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, link, new object[] { AppDir });
            t.InvokeMember("Description", BindingFlags.SetProperty, null, link, new object[] { AppName });
            t.InvokeMember("Save", BindingFlags.InvokeMethod, null, link, null);
            Marshal.ReleaseComObject(link);
            Marshal.ReleaseComObject(shell);
        }

        // Removes the app, its registration, and its settings, including any saved ChatGPT sign-in.
        public static void Uninstall()
        {
            try { if (File.Exists(AppExe)) File.Delete(AppExe); }
            catch (IOException) { throw new InvalidOperationException("Please close PDF Reader and try again."); }
            catch (UnauthorizedAccessException) { throw new InvalidOperationException("Please close PDF Reader and try again."); }

            if (File.Exists(Shortcut)) File.Delete(Shortcut);
            RegistryKey cu = Registry.CurrentUser;
            cu.DeleteSubKeyTree(Root + @"Classes\" + ProgId, false);
            using (RegistryKey k = cu.OpenSubKey(Root + @"Classes\.pdf\OpenWithProgids", true))
                if (k != null) k.DeleteValue(ProgId, false);
            using (RegistryKey k = cu.OpenSubKey(Root + "RegisteredApplications", true))
                if (k != null) k.DeleteValue(AppName, false);
            cu.DeleteSubKeyTree(Root + "PDFReader", false);
            cu.DeleteSubKeyTree(UninstallKey, false);
            AssociationsChanged();
            if (Directory.Exists(DataDir)) Directory.Delete(DataDir, true);
            Removed = true;
        }

        public static bool Removed;

        // This program may be running from the folder it has to remove, so call this last: a short-lived
        // helper waits a moment for it to exit, then finishes the job.
        public static void RemoveAppDir()
        {
            string self = Path.GetFullPath(Assembly.GetExecutingAssembly().Location);
            if (self.StartsWith(Path.GetFullPath(AppDir) + "\\", StringComparison.OrdinalIgnoreCase))
            {
                var info = new ProcessStartInfo(Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe",
                    "/d /c ping -n 3 127.0.0.1 >nul & rmdir /s /q \"" + AppDir + "\"");
                info.UseShellExecute = false;
                info.CreateNoWindow = true;
                info.WorkingDirectory = Path.GetTempPath();
                Process.Start(info);
            }
            else if (Directory.Exists(AppDir)) Directory.Delete(AppDir, true);
        }

        public static void Open(string target)
        {
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
        }
    }

    class SetupWindow : Window
    {
        readonly TextBlock title = new TextBlock(), body = new TextBlock();
        readonly StackPanel buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        readonly bool uninstalling;

        [DllImport("dwmapi.dll")]
        static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        public SetupWindow(bool uninstalling)
        {
            this.uninstalling = uninstalling;
            Title = "PDF Reader Setup";
            Width = 480;
            SizeToContent = SizeToContent.Height;
            ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Background = (Brush)FindResource("Bg");
            FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");
            try
            {
                using (System.Drawing.Icon ico = System.Drawing.Icon.ExtractAssociatedIcon(Assembly.GetExecutingAssembly().Location))
                    Icon = Imaging.CreateBitmapSourceFromHIcon(ico.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            }
            catch (Exception) { }

            title.FontSize = 20;
            title.FontWeight = FontWeights.SemiBold;
            title.Foreground = (Brush)FindResource("TextMain");
            title.Margin = new Thickness(0, 0, 0, 8);
            body.FontSize = 13.5;
            body.LineHeight = 20;
            body.TextWrapping = TextWrapping.Wrap;
            body.Foreground = (Brush)FindResource("Muted");
            body.Margin = new Thickness(0, 0, 0, 22);

            var root = new StackPanel { Margin = new Thickness(26, 22, 26, 22) };
            root.Children.Add(title);
            root.Children.Add(body);
            root.Children.Add(buttons);
            Content = root;

            if (uninstalling) ShowUninstallStart(); else ShowInstallStart();
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            IntPtr h = new WindowInteropHelper(this).Handle;
            int on = 1;
            DwmSetWindowAttribute(h, 20, ref on, sizeof(int));
        }

        Button Add(string text, string id, bool primary, RoutedEventHandler click)
        {
            var b = new Button
            {
                Style = (Style)FindResource(primary ? "CoralBtn" : "Flat"),
                Padding = new Thickness(16, 9, 16, 9),
                Margin = new Thickness(8, 0, 0, 0),
                Content = new TextBlock { Text = text, FontSize = 13.5, FontWeight = FontWeights.SemiBold },
            };
            System.Windows.Automation.AutomationProperties.SetAutomationId(b, id);
            b.Click += click;
            buttons.Children.Add(b);
            return b;
        }

        void ShowInstallStart()
        {
            bool update = Installer.IsInstalled;
            title.Text = update ? "Update PDF Reader" : "Install PDF Reader";
            body.Text = (update ? "This replaces your copy with version " + Installer.Version + ". Your settings and sign-ins stay." : "This installs PDF Reader for your Windows account. It doesn't need admin rights.")
                + "\n\nInstalls to " + Installer.AppDir;
            buttons.Children.Clear();
            Add("Cancel", "CancelButton", false, delegate { Close(); });
            Add(update ? "Update" : "Install", "InstallButton", true, delegate { RunInstall(); });
        }

        void RunInstall()
        {
            try { Installer.Install(); }
            catch (Exception ex)
            {
                title.Text = "PDF Reader wasn't installed";
                body.Text = ex.Message;
                buttons.Children.Clear();
                Add("Close", "CloseButton", false, delegate { Close(); });
                Add("Try again", "RetryButton", true, delegate { ShowInstallStart(); });
                return;
            }
            title.Text = "PDF Reader is installed";
            body.Text = "Find it in the Start menu. To open PDFs with it when you double-click them, choose Set as default.";
            buttons.Children.Clear();
            Add("Close", "CloseButton", false, delegate { Close(); });
            Add("Set as default", "DefaultButton", false, delegate { Installer.Open("ms-settings:defaultapps?registeredAppUser=PDF%20Reader"); });
            Add("Open PDF Reader", "OpenButton", true, delegate { Installer.Open(Installer.AppExe); Close(); });
        }

        void ShowUninstallStart()
        {
            title.Text = "Uninstall PDF Reader";
            body.Text = "This removes the app, its settings and recent files list from this PC, and any saved ChatGPT sign-in. Your PDFs aren't touched.";
            buttons.Children.Clear();
            Add("Cancel", "CancelButton", false, delegate { Close(); });
            Add("Uninstall", "UninstallButton", true, delegate { RunUninstall(); });
        }

        void RunUninstall()
        {
            try { Installer.Uninstall(); }
            catch (Exception ex)
            {
                title.Text = "PDF Reader wasn't removed";
                body.Text = ex.Message;
                buttons.Children.Clear();
                Add("Close", "CloseButton", false, delegate { Close(); });
                Add("Try again", "RetryButton", true, delegate { RunUninstall(); });
                return;
            }
            title.Text = "PDF Reader is uninstalled";
            body.Text = "If it was your default for PDFs, Windows will ask which app to use next time you open one.";
            buttons.Children.Clear();
            Add("Close", "CloseButton", true, delegate { Close(); });
        }
    }

    static class Program
    {
        [STAThread]
        static int Main(string[] args)
        {
            bool uninstall = false, silent = false;
            foreach (string a in args)
            {
                if (string.Equals(a, "/uninstall", StringComparison.OrdinalIgnoreCase)) uninstall = true;
                if (string.Equals(a, "/silent", StringComparison.OrdinalIgnoreCase)) silent = true;
            }
            if (silent)
            {
                try
                {
                    if (uninstall) Installer.Uninstall(); else Installer.Install();
                    if (Installer.Removed) Installer.RemoveAppDir();
                    return 0;
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine(ex.Message);
                    return 1;
                }
            }
            var app = new Application();
            using (Stream s = typeof(Program).Assembly.GetManifestResourceStream("Theme.xaml"))
                app.Resources = (ResourceDictionary)XamlReader.Load(s);
            app.Run(new SetupWindow(uninstall));
            if (Installer.Removed) Installer.RemoveAppDir();
            return 0;
        }
    }
}
