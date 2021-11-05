﻿using System;
using System.Data;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Reflection;
using livelywpf.Helpers.Files;
using livelywpf.Helpers.Archive;
using livelywpf.Helpers;
using livelywpf.Core;
using livelywpf.Views;
using livelywpf.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using livelywpf.Services;

namespace livelywpf
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : Application
    {
        private static readonly NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();

        private readonly IServiceProvider _serviceProvider;
        /// <summary>
        /// Gets the <see cref="IServiceProvider"/> instance for the current application instance.
        /// </summary>
        public static IServiceProvider Services
        {
            get
            {
                IServiceProvider serviceProvider = ((App)Current)._serviceProvider;
                return serviceProvider ?? throw new InvalidOperationException("The service provider is not initialized");
            }
        }

        private static MainWindow _appWindow;
        public static MainWindow AppWindow
        {
            get => _appWindow ??= App.Services.GetRequiredService<MainWindow>();
        }

        public App()
        {
            //App() -> OnStartup() -> App.Startup event.
            _serviceProvider = ConfigureServices();
        }

        private IServiceProvider ConfigureServices()
        {
            var provider = new ServiceCollection()
                //singleton
                .AddSingleton<MainWindow>()
                .AddSingleton<IUserSettingsService, UserSettingsService>()
                .AddSingleton<SettingsViewModel>() //can be made transient once usersettings is separated.
                .AddSingleton<LibraryViewModel>()
                //transient
                .AddTransient<ApplicationRulesViewModel>()
                .AddTransient<AboutViewModel>()
                .AddTransient<AddWallpaperViewModel>()
                .AddTransient<HelpViewModel>()
                .AddTransient<ScreenLayoutViewModel>()
                /*
                .AddSingleton<IAppUpdaterService, GithubUpdaterService>()
                .AddTransient<Factories.IApplicationRulesFactory, Factories.ApplicationRulesFactory>()
                .AddLogging(loggingBuilder =>
                {
                    // configure Logging with NLog
                    loggingBuilder.ClearProviders();
                    loggingBuilder.SetMinimumLevel(Microsoft.Extensions.Logging.LogLevel.Trace);
                    loggingBuilder.AddNLog("Nlog.config");
                })
                */
                .BuildServiceProvider();

            return provider;
        }

        protected override void OnStartup(StartupEventArgs e)
        {
            try
            {
                //create directories if not exist, eg: C:\Users\<User>\AppData\Local
                Directory.CreateDirectory(Constants.CommonPaths.AppDataDir);
                Directory.CreateDirectory(Constants.CommonPaths.LogDir);
                Directory.CreateDirectory(Constants.CommonPaths.TempDir);
                Directory.CreateDirectory(Constants.CommonPaths.TempCefDir);
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "AppData Directory Initialize Error", MessageBoxButton.OK, MessageBoxImage.Error);
                Program.ExitApplication();
            }

            //Setting up logging.
            NLogger.SetupNLog();
            SetupUnhandledExceptionLogging();
            NLogger.LogHardwareInfo();

            //clear temp files if any.
            FileOperations.EmptyDirectory(Constants.CommonPaths.TempDir);

            //Initialize before viewmodel and main window.
            ScreenHelper.Initialize();

            #region vm init

            Program.SettingsVM = App.Services.GetRequiredService<SettingsViewModel>();
            Program.WallpaperDir = Program.SettingsVM.Settings.WallpaperDir;
            try
            {
                CreateWallpaperDir();
            }
            catch (Exception ex)
            {
                Logger.Error("Wallpaper Directory creation fail, falling back to default directory:" + ex.ToString());
                Program.SettingsVM.Settings.WallpaperDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Lively Wallpaper", "Library");
                Program.SettingsVM.UpdateConfigFile();
                try
                {
                    CreateWallpaperDir();
                }
                catch (Exception ie)
                {
                    Logger.Error("Wallpaper Directory creation failed, Exiting:" + ie.ToString());
                    MessageBox.Show(ie.Message, "Error: Failed to create wallpaper folder", MessageBoxButton.OK, MessageBoxImage.Error);
                    Program.ExitApplication();
                }
            }

            //previous installed appversion is different from current instance..
            if (!Program.SettingsVM.Settings.AppVersion.Equals(Assembly.GetExecutingAssembly().GetName().Version.ToString(), StringComparison.OrdinalIgnoreCase))
            {
                //todo: show changelog window here..
                Program.SettingsVM.Settings.WallpaperBundleVersion = ExtractWallpaperBundle(Program.SettingsVM.Settings.WallpaperBundleVersion);
                Program.SettingsVM.Settings.AppVersion = Assembly.GetExecutingAssembly().GetName().Version.ToString();
                Program.SettingsVM.UpdateConfigFile();
            }

            //Program.AppRulesVM = App.Services.GetRequiredService<ApplicationRulesViewModel>();
            Program.LibraryVM = App.Services.GetRequiredService<LibraryViewModel>();

            #endregion //vm init

            Application.Current.MainWindow = AppWindow;
            //Creates an empty xaml island control as a temp fix for closing issue; also receives window msg..
            //Issue: https://github.com/microsoft/microsoft-ui-xaml/issues/3482
            //Steps to reproduce: Start gif wallpaper using uwp control -> restart lively -> close restored gif wallpaper -> library gridview stops.
            WndProcMsgWindow wndproc = new WndProcMsgWindow();
            wndproc.Show();
            //Package app otherwise bugging out when initialized in settings vm.
            SetupDesktop.SetupInputHooks();
            if (Program.SettingsVM.Settings.IsRestart)
            {
                Program.SettingsVM.Settings.IsRestart = false;
                Program.SettingsVM.UpdateConfigFile();
                AppWindow?.Show();
            }

            base.OnStartup(e);
        }

        /// <summary>
        /// Extract default wallpapers and incremental if any.
        /// </summary>
        public static int ExtractWallpaperBundle(int currentBundleVer)
        {
            //Lively stores the last extracted bundle filename, extraction proceeds from next file onwards.
            int maxExtracted = currentBundleVer;
            try
            {
                //wallpaper bundles filenames are 0.zip, 1.zip ...
                var sortedBundles = Directory.GetFiles(
                    Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "bundle"))
                    .OrderBy(x => x);

                foreach (var item in sortedBundles)
                {
                    if(int.TryParse(Path.GetFileNameWithoutExtension(item), out int val))
                    {
                        if (val > maxExtracted)
                        {
                            //Sharpzip library will overwrite files if exists during extraction.
                            ZipExtract.ZipExtractFile(item, Path.Combine(Program.WallpaperDir, "wallpapers"), false);
                            maxExtracted = val;
                        }
                    }
                }
            }
            catch (Exception e)
            {
                Logger.Error("Base Wallpaper Extract Fail:" + e.ToString());
            }
            return maxExtracted;
        }

        private void CreateWallpaperDir()
        {
            Directory.CreateDirectory(Path.Combine(Program.WallpaperDir, "wallpapers"));
            Directory.CreateDirectory(Path.Combine(Program.WallpaperDir, "SaveData", "wptmp"));
            Directory.CreateDirectory(Path.Combine(Program.WallpaperDir, "SaveData", "wpdata"));
        }

        private void SetupUnhandledExceptionLogging()
        {
            AppDomain.CurrentDomain.UnhandledException += (s, e) =>
                LogUnhandledException((Exception)e.ExceptionObject, "AppDomain.CurrentDomain.UnhandledException");

            Dispatcher.UnhandledException += (s, e) =>
                LogUnhandledException(e.Exception, "Application.Current.DispatcherUnhandledException");

            TaskScheduler.UnobservedTaskException += (s, e) =>
                LogUnhandledException(e.Exception, "TaskScheduler.UnobservedTaskException");
        }

        private void LogUnhandledException(Exception exception, string source)
        {
            string message = $"Unhandled exception ({source})";
            try
            {
                System.Reflection.AssemblyName assemblyName = System.Reflection.Assembly.GetExecutingAssembly().GetName();
                message = string.Format("Unhandled exception in {0} v{1}", assemblyName.Name, assemblyName.Version);
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Exception in LogUnhandledException");
            }
            finally
            {
                Logger.Error("{0}\n{1}", message, exception.ToString());
            }
        }
    }
}
