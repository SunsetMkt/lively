﻿using livelywpf.Core.API;
using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace livelywpf.Core
{
    public class ExtPrograms : IWallpaper
    {
        private IntPtr hwnd;
        private readonly Process _process;
        private readonly LibraryModel model;
        private LivelyScreen display;
        public UInt32 SuspendCnt { get; set; }
        public event EventHandler<WindowInitializedArgs> WindowInitialized;
        private readonly CancellationTokenSource ctsProcessWait = new CancellationTokenSource();
        private Task processWaitTask;
        private readonly int timeOut;

        /// <summary>
        /// Launch Program(.exe) Unity, godot.. as wallpaper.
        /// </summary>
        /// <param name="path">Path to program exe</param>
        /// <param name="model">Wallpaper data</param>
        /// <param name="display">Screen metadata</param>
        /// <param name="timeOut">Time to wait for program to be ready(in milliseconds)</param>
        public ExtPrograms(string path, LibraryModel model, LivelyScreen display, int timeOut = 20000)
        {
            // Unity flags
            //-popupwindow removes from taskbar
            //-fullscreen disable fullscreen mode if set during compilation (lively is handling resizing window instead).
            //Alternative flags:
            //Unity attaches to workerw by itself; Problem: Process window handle is returning zero.
            //"-parentHWND " + workerw.ToString();// + " -popupwindow" + " -;
            //cmdArgs = "-popupwindow -screen-fullscreen 0";
            string cmdArgs = model.LivelyInfo.Arguments;

            ProcessStartInfo start = new ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = false,
                WorkingDirectory = System.IO.Path.GetDirectoryName(path),
                Arguments = cmdArgs,
            };

            Process _process = new Process()
            {
                EnableRaisingEvents = true,
                StartInfo = start,
            };

            this._process = _process;
            this.model = model;
            this.display = display;
            this.timeOut = timeOut;
            SuspendCnt = 0;
        }

        public async void Close()
        {
            TaskProcessWaitCancel();
            while(!IsProcessWaitDone())
            {
                await Task.Delay(1);
            }

            //Not reliable, app may refuse to close(open dialogue window.. etc)
            //Proc.CloseMainWindow();
            Terminate();
        }

        public IntPtr GetHWND()
        {
            return hwnd;
        }

        public IntPtr GetHWNDInput()
        {
            return hwnd;
        }

        public Process GetProcess()
        {
            return _process;
        }

        public LibraryModel GetWallpaperData()
        {
            return model;
        }

        public WallpaperType GetWallpaperType()
        {
            return model.LivelyInfo.Type;
        }

        public void Pause()
        {
            if (_process != null)
            {
                //method 0, issue: does not work with every pgm
                //NativeMethods.DebugActiveProcess((uint)Proc.Id);

                //method 1, issue: resume taking time ?!
                //NativeMethods.NtSuspendProcess(Proc.Handle);

                //method 2, issue: deadlock/thread issue
                /*
                try
                {
                    ProcessSuspend.SuspendAllThreads(this);
                    //thread buggy noise otherwise?!
                    VolumeMixer.SetApplicationMute(Proc.Id, true);
                }
                catch { }
                */
            }
        }

        public void Play()
        {
            if (_process != null)
            {
                //method 0, issue: does not work with every pgm
                //NativeMethods.DebugActiveProcessStop((uint)Proc.Id);

                //method 1, issue: resume taking time ?!
                //NativeMethods.NtResumeProcess(Proc.Handle);

                //method 2, issue: deadlock/thread issue
                /*
                try
                {
                    ProcessSuspend.ResumeAllThreads(this);
                    //thread buggy noise otherwise?!
                    VolumeMixer.SetApplicationMute(Proc.Id, false);
                }
                catch { }
                */
            }
        }

        public void Stop()
        {
            
        }

        public LivelyScreen GetScreen()
        {
            return display;
        }

        public async void Show()
        {
            if (_process != null)
            {
                try
                {
                    _process.Exited += Proc_Exited;
                    _process.Start();
                    processWaitTask = Task.Run(() => hwnd = WaitForProcesWindow().Result, ctsProcessWait.Token);
                    await processWaitTask;
                    if(hwnd.Equals(IntPtr.Zero))
                    {
                        WindowInitialized?.Invoke(this, new WindowInitializedArgs()
                        {
                            Success = false,
                            Error = new Exception(Properties.Resources.LivelyExceptionGeneral),
                            Msg = "Process window handle is zero."
                        });
                    }
                    else
                    {
                        WindowOperations.BorderlessWinStyle(hwnd);
                        WindowOperations.RemoveWindowFromTaskbar(hwnd);
                        //Program ready!
                        WindowInitialized?.Invoke(this, new WindowInitializedArgs() { 
                            Success = true, 
                            Error = null, 
                            Msg = null });
                    }
                }
                catch(OperationCanceledException e1)
                {
                    WindowInitialized?.Invoke(this, new WindowInitializedArgs() { 
                        Success = false, 
                        Error = e1, 
                        Msg = "Program wallpaper terminated early/user cancel." });
                }
                catch(InvalidOperationException e2)
                {
                    //No GUI, program failed to enter idle state.
                    WindowInitialized?.Invoke(this, new WindowInitializedArgs() { 
                        Success = false,
                        Error = e2,
                        Msg = "Program wallpaper crashed/closed already!" });
                }
                catch (Exception e3)
                {
                    WindowInitialized?.Invoke(this, new WindowInitializedArgs() { 
                        Success = false,
                        Error = e3,
                        Msg = ":(" });
                }
            }
        }

        private void Proc_Exited(object sender, EventArgs e)
        {
            _process?.Dispose();
            SetupDesktop.RefreshDesktop();
        }

        #region process task

        /// <summary>
        /// Function to search for window of spawned program.
        /// </summary>
        private async Task<IntPtr> WaitForProcesWindow()
        {
            if (_process == null)
            {
                return IntPtr.Zero;
            }

            _process.Refresh();
            //waiting for program messageloop to be ready (GUI is not guaranteed to be ready.)
            while (_process.WaitForInputIdle(-1) != true)
            {
                ctsProcessWait.Token.ThrowIfCancellationRequested();
            }

            IntPtr wHWND = IntPtr.Zero;
            if (GetWallpaperType() == WallpaperType.godot)
            {
                for (int i = 0; i < timeOut && _process.HasExited == false; i++)
                {
                    ctsProcessWait.Token.ThrowIfCancellationRequested();
                    //todo: verify pid of window.
                    wHWND = NativeMethods.FindWindowEx(IntPtr.Zero, IntPtr.Zero, "Engine", null);
                    if (!IntPtr.Equals(wHWND, IntPtr.Zero))
                        break;
                    await Task.Delay(1);
                }
                return wHWND;
            }

            //Find process window.
            for (int i = 0; i < timeOut && _process.HasExited == false; i++)
            {
                ctsProcessWait.Token.ThrowIfCancellationRequested();
                if (!IntPtr.Equals((wHWND = GetProcessWindow(_process, true)), IntPtr.Zero))
                    break;
                await Task.Delay(1);
            }

            //Player settings dialog of Unity (if exists.)
            IntPtr cHWND = NativeMethods.FindWindowEx(wHWND, IntPtr.Zero, "Button", "Play!");
            if (!IntPtr.Equals(cHWND, IntPtr.Zero))
            {
                //Simulate Play! button click. (Unity config window)
                NativeMethods.SendMessage(cHWND, NativeMethods.BM_CLICK, IntPtr.Zero, IntPtr.Zero);
                //Refreshing..
                wHWND = IntPtr.Zero;
                await Task.Delay(1);

                //Search for Unity main Window.
                for (int i = 0; i < timeOut && _process.HasExited == false; i++)
                {
                    ctsProcessWait.Token.ThrowIfCancellationRequested();
                    if (!IntPtr.Equals((wHWND = GetProcessWindow(_process, true)), IntPtr.Zero))
                    {
                        break;
                    }
                    await Task.Delay(1);
                }
            }
            return wHWND;
        }

        /// <summary>
        /// Retrieve window handle of process.
        /// </summary>
        /// <param name="proc">Process to search for.</param>
        /// <param name="win32Search">Use win32 method to find window.</param>
        /// <returns></returns>
        private IntPtr GetProcessWindow(Process proc, bool win32Search = false)
        {
            if (_process == null)
                return IntPtr.Zero;

            if(win32Search)
            {
                return FindWindowByProcessId(proc.Id);
            }
            else
            {
                proc.Refresh();
                //Issue(.net core) MainWindowHandle zero: https://github.com/dotnet/runtime/issues/32690
                return proc.MainWindowHandle;
            }
        }

        private IntPtr FindWindowByProcessId(int pid)
        {
            IntPtr HWND = IntPtr.Zero;
            NativeMethods.EnumWindows(new NativeMethods.EnumWindowsProc((tophandle, topparamhandle) =>
            {
                _ = NativeMethods.GetWindowThreadProcessId(tophandle, out int cur_pid);
                if (cur_pid == pid)
                {
                    if (NativeMethods.IsWindowVisible(tophandle))
                    {
                        HWND = tophandle;
                        return false;
                    }
                }

                return true;
            }), IntPtr.Zero);

            return HWND;
        }

        /// <summary>
        /// Cancel waiting for pgm wp window to be ready.
        /// </summary>
        private void TaskProcessWaitCancel()
        {
            if (ctsProcessWait == null)
                return;

            ctsProcessWait.Cancel();
            ctsProcessWait.Dispose();
        }

        /// <summary>
        /// Check if started pgm ready(GUI window started).
        /// </summary>
        /// <returns>true: process ready/halted, false: process still starting.</returns>
        private bool IsProcessWaitDone()
        {
            var task = processWaitTask;
            if (task != null)
            {
                if((task.IsCompleted == false
                || task.Status == TaskStatus.Running
                || task.Status == TaskStatus.WaitingToRun
                || task.Status == TaskStatus.WaitingForActivation))
                {
                    return false;
                }
                return true;
            }
            return true;
        }

        #endregion process task

        public void SendMessage(string msg)
        {

        }

        public string GetLivelyPropertyCopyPath()
        {
            return null;
        }

        public void SetScreen(LivelyScreen display)
        {
            this.display = display;
        }

        public void Terminate()
        {
            try
            {
                _process.Kill();
            }
            catch { }
            SetupDesktop.RefreshDesktop();
        }

        public void SetVolume(int volume)
        {
            try
            {
                VolumeMixer.SetApplicationVolume(_process.Id, volume);
            }
            catch { }
        }

        public void SetPlaybackPos(float pos, PlaybackPosType type)
        {
            //todo
        }

        public Task ScreenCapture(string filePath)
        {
            throw new NotImplementedException();
        }

        public void SendMessage(IpcMessage obj)
        {
            //todo
        }
    }
}
