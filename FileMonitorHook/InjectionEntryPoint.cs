using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace FileMonitorHook
{
    public class InjectionEntryPoint : EasyHook.IEntryPoint
    {
        /// <summary>
        /// Reference to the server interface within FileMonitor
        /// </summary>
        ServerInterface _server = null;

        /// <summary>
        /// Message queue of all files accessed
        /// </summary>
        Queue<string> _messageQueue = new Queue<string>();

        /// <summary>
        /// 
        /// EasyHook requires a constructor that matches <paramref name="context"/> and any additional parameters as provided
        /// in the original call to <see cref="EasyHook.RemoteHooking.Inject(int, EasyHook.InjectionOptions, string, string, object[])"/>.
        /// 
        /// Multiple constructors can exist on the same <see cref="EasyHook.IEntryPoint"/>, providing that each one has a corresponding Run method (e.g. <see cref="Run(EasyHook.RemoteHooking.IContext, string)"/>).
        /// </summary>
        /// <param name="context">The RemoteHooking context</param>
        /// <param name="channelName">The name of the IPC channel</param>
        public InjectionEntryPoint(
            EasyHook.RemoteHooking.IContext context,
            string channelName)
        {
            // Connect to server object using provided channel name
            _server = EasyHook.RemoteHooking.IpcConnectClient<ServerInterface>(channelName);

            // If Ping fails then the Run method will be not be called
            _server.Ping();
        }

        /// <summary>
        /// The main entry point for our logic once injected within the target process. 
        /// This is where the hooks will be created, and a loop will be entered until host process exits.
        /// EasyHook requires a matching Run method for the constructor
        /// </summary>
        /// <param name="context">The RemoteHooking context</param>
        /// <param name="channelName">The name of the IPC channel</param>
        public void Run(
            EasyHook.RemoteHooking.IContext context,
            string channelName)
        {
            // Injection is now complete and the server interface is connected
            _server.IsInstalled(EasyHook.RemoteHooking.GetCurrentProcessId());

            // Install hooks

          
            var windowStealHook = EasyHook.LocalHook.Create(
               EasyHook.LocalHook.GetProcAddress("user32.dll", "SetForegroundWindow"),
               new SetFocus_Delegate(SetForegroundWindowHook),
               this);

            // Activate hooks on all threads except the current thread
            windowStealHook.ThreadACL.SetExclusiveACL(new Int32[] { 0 });
           

            _server.ReportMessage(0, "SetForegroundWindow hooks installed");

            // Wake up the process (required if using RemoteHooking.CreateAndInject)
            EasyHook.RemoteHooking.WakeUpProcess();

            try
            {
                // Loop until FileMonitor closes (i.e. IPC fails)
                while (true)
                {
                    System.Threading.Thread.Sleep(500);

                    string[] queued = null;

                    lock (_messageQueue)
                    {
                        queued = _messageQueue.ToArray();
                        _messageQueue.Clear();
                    }

                    // Send newly monitored file accesses to FileMonitor
                    if (queued != null && queued.Length > 0)
                    {
                        _server.ReportMessages(0, queued);
                    }
                    else
                    {
                        _server.Ping();
                    }
                }
            }
            catch
            {
                // Ping() or ReportMessages() will raise an exception if host is unreachable
            }

            // Remove hooks
            windowStealHook.Dispose();
            
            // Finalise cleanup of hooks
            EasyHook.LocalHook.Release();
        }


        [DllImport("user32.dll")]
        static extern bool SetForegroundWindow(IntPtr hWnd);

        bool SetForegroundWindowHook(IntPtr hWnd)
        {
            bool result = false;


            try
            {

                FLASHWINFO flashwinfo = new FLASHWINFO();
                flashwinfo.cbSize = Convert.ToUInt32(Marshal.SizeOf(flashwinfo));
                flashwinfo.hwnd = hWnd;
                flashwinfo.dwFlags = 2;
                flashwinfo.uCount = 2;
                flashwinfo.dwTimeout = 0;
                FlashWindowEx(ref flashwinfo);

                lock (this._messageQueue)
                {
                    if (this._messageQueue.Count < 1000)
                    {

                        this._messageQueue.Enqueue(
                            string.Format("[{0}:{1}]: Tried to steal focus",
                            EasyHook.RemoteHooking.GetCurrentProcessId(), EasyHook.RemoteHooking.GetCurrentThreadId()));
                    }
                }
            }
            catch
            {
                // swallow exceptions so that any issues caused by this code do not crash target process
            }

            return result;

        }


        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool FlashWindowEx(ref FLASHWINFO pwfi);

        [StructLayout(LayoutKind.Sequential)]
        public struct FLASHWINFO
        {
            public uint cbSize;
            public IntPtr hwnd;
            public uint dwFlags;
            public uint uCount;
            public uint dwTimeout;
        }

        [UnmanagedFunctionPointer(CallingConvention.StdCall,
                   CharSet = CharSet.Unicode,
                   SetLastError = true)]
        delegate bool SetFocus_Delegate(
                   IntPtr hWnd);


    }
}
