using System;
using System.Runtime.InteropServices;

namespace DriveSync.App;

public static class Program
{
    private const int ATTACH_PARENT_PROCESS = -1;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(int dwProcessId);

    [STAThread]
    public static void Main(string[] args)
    {
        // Try attaching to the parent terminal console if run from CLI
        bool attachedToConsole = AttachConsole(ATTACH_PARENT_PROCESS);

        foreach (var arg in args)
        {
            if (arg == "--help" || arg == "-h" || arg == "/?")
            {
                if (attachedToConsole)
                {
                    Console.WriteLine();
                    Console.WriteLine("=====================================================");
                    Console.WriteLine(" DriveSync - Two-Way Synchronizer for Windows");
                    Console.WriteLine("=====================================================");
                    Console.WriteLine("Usage: DriveSync.App.exe [options]");
                    Console.WriteLine();
                    Console.WriteLine("Options:");
                    Console.WriteLine("  --background    Launch minimized to system tray flyout");
                    Console.WriteLine("  --help, -h      Display this help message and exit");
                    Console.WriteLine();
                    Console.WriteLine("To launch in background without holding PowerShell prompt:");
                    Console.WriteLine("  Start-Process .\\DriveSync.App.exe");
                    Console.WriteLine("=====================================================");
                }
                return;
            }
        }

        var app = new App();
        app.InitializeComponent();

        if (attachedToConsole)
        {
            Console.WriteLine();
            Console.WriteLine("[DriveSync] Starting DriveSync desktop application...");
            Console.WriteLine("[DriveSync] Window initialized. System tray icon loaded.");
            Console.WriteLine("[DriveSync] Running. Press Ctrl+C to exit, or close via System Tray menu.");

            Console.CancelKeyPress += (s, e) =>
            {
                e.Cancel = true;
                Console.WriteLine("[DriveSync] Exiting application...");
                app.Dispatcher.Invoke(() => app.ExitApplication());
            };
        }

        app.Run();
    }
}
