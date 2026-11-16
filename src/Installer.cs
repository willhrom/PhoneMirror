using Microsoft.Win32;

namespace PhoneMirror;

/// <summary>Copies the app to a permanent spot with Start menu / desktop shortcuts, and handles start-with-Windows.</summary>
static class Installer
{
    public static string Dir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "PhoneMirror");
    public static string Exe => Path.Combine(Dir, "PhoneMirror.exe");

    public static bool IsInstalled =>
        string.Equals(Path.GetFullPath(Environment.ProcessPath ?? ""), Path.GetFullPath(Exe), StringComparison.OrdinalIgnoreCase);

    public static void Install()
    {
        Directory.CreateDirectory(Dir);
        File.Copy(Environment.ProcessPath!, Exe, true);
        Shortcut(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Phone Mirror.lnk"));
        Shortcut(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Phone Mirror.lnk"));
    }

    static void Shortcut(string lnk)
    {
        try
        {
            var type = Type.GetTypeFromProgID("WScript.Shell");
            if (type == null) return;
            dynamic shell = Activator.CreateInstance(type)!;
            dynamic s = shell.CreateShortcut(lnk);
            s.TargetPath = Exe;
            s.WorkingDirectory = Dir;
            s.IconLocation = Exe + ",0";
            s.Description = "Mirror and control your Android phone";
            s.Save();
        }
        catch { }
    }

    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public static void SetStartWithWindows(bool on)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey, true);
            if (on) key.SetValue("PhoneMirror", $"\"{Environment.ProcessPath}\" --background");
            else key.DeleteValue("PhoneMirror", false);
        }
        catch { }
    }
}
