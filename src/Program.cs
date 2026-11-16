namespace PhoneMirror;

static class Program
{
    /// <summary>Started by Windows at login: stay in the tray.</summary>
    public static bool Background { get; private set; }
    public static EventWaitHandle? ShowSignal { get; private set; }

    [STAThread]
    static void Main(string[] args)
    {
        Background = args.Contains("--background");
        bool wait = args.Contains("--wait"); // launched by an update/install: wait for the old copy to exit

        using var mutex = new Mutex(true, "PhoneMirror.SingleInstance", out bool first);
        ShowSignal = new EventWaitHandle(false, EventResetMode.AutoReset, "PhoneMirror.Show");
        if (!first)
        {
            bool got = false;
            if (wait)
            {
                try { got = mutex.WaitOne(15000); }
                catch (AbandonedMutexException) { got = true; }
            }
            if (!got)
            {
                ShowSignal.Set(); // already running: bring that window to the front
                return;
            }
        }

        Updater.Cleanup();
        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
        GC.KeepAlive(mutex);
    }
}
