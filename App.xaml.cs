using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;

namespace SmartUsbBackup;

/// <summary>
/// Точка входа WinUI 3 (unpackaged).
///
///   • ОДИН инстанс приложения (Mutex). Второй запуск лишь будит окно первого
///     через приватное WM_APP-сообщение — как и в WinForms-версии.
///   • Аргумент -hidden — тихий старт (автозагрузка): окно не показывается,
///     приложение висит в трее и реагирует на события.
///   • Все необработанные исключения пишутся в лог, а не «роняют» процесс молча.
/// </summary>
public partial class App : Application
{
    private static Mutex? _singleInstanceMutex;
    private MainWindow? _window;

    public App()
    {
        InitializeComponent();

        // Исключения XAML-потока — логируем и не даём молча ронять процесс.
        this.UnhandledException += (_, e) =>
            AppLog.Error(e.Exception, "UI/XAML exception: " + e.Message);

        // Фоновые исключения не должны молча убивать процесс.
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            AppLog.Error(e.ExceptionObject as Exception ?? new Exception("unknown"), "Domain exception");
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            AppLog.Error(e.Exception, "Unobserved task exception");
            e.SetObserved();
        };
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        // Защита от повторного запуска: иначе две копии соревновались бы за FileSystemWatcher'ы.
        _singleInstanceMutex = new Mutex(true, @"Local\SmartUsbBackup_SingleInstance", out var createdNew);
        if (!createdNew)
        {
            // Второй запуск: активируем уже открытое окно и выходим.
            NativeMethods.BroadcastRestoreWindow();
            Current.Exit();
            return;
        }

        var startedHidden = Environment.GetCommandLineArgs()
            .Any(a => a is "-hidden" or "/hidden");

        _window = new MainWindow(startedHidden);
        if (!startedHidden)
            _window.Activate();
        // при -hidden окно остаётся скрытым: живёт иконка в трее + WM_DEVICECHANGE
    }
}
