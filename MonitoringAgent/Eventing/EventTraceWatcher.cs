using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Threading;
using Monitoring.Eventing.Interop;

namespace Monitoring.Eventing {

    public enum TraceLevel {
        Critical = 1,
        Error = 2,
        Warning = 3,
        Information = 4,
        Verbose = 5
    }

    public sealed class EventTraceWatcher : IDisposable {
        private readonly string loggerName;
        private Guid eventProviderId;
        private bool enabled;
        private TraceSafeHandle traceHandle;
        private SessionSafeHandle sessionHandle;
        private EventTraceLogfile logFile;
        private IAsyncResult asyncResult;
        private ProcessTraceDelegate processEventsDelgate;
        private EventTraceProperties eventTraceProperties;
        private SortedList<byte, TraceEventInfoWrapper> traceEventInfoCache = new SortedList<byte/*opcode*/, TraceEventInfoWrapper>();

        private delegate void ProcessTraceDelegate(TraceSafeHandle traceHandle);

        public EventTraceWatcher(string loggerName, Guid eventProviderId) {
            this.loggerName = loggerName;
            this.eventProviderId = eventProviderId;
        }

        ~EventTraceWatcher() {
            Cleanup();
        }

        public event EventHandler<EventArrivedEventArgs> EventArrived;

        public ulong MatchAnyKeyword { get; set; }

        public TraceLevel Level { get; set; }

        private void Cleanup() {
            SetEnabled(false);
            foreach (TraceEventInfoWrapper value in this.traceEventInfoCache.Values) {
                value.Dispose();
            }
            this.traceEventInfoCache = null;
        }


        private EventArrivedEventArgs CreateEventArgsFromEventRecord(EventRecord eventRecord) {
            byte eventOpcode = eventRecord.EventHeader.EventDescriptor.Opcode;
            TraceEventInfoWrapper traceEventInfo;
            bool shouldDispose = false;

            // Find the event information (schema).
            int index = this.traceEventInfoCache.IndexOfKey(eventOpcode);
            if (index >= 0) {
                traceEventInfo = this.traceEventInfoCache.Values[index];
            }
            else {
                traceEventInfo = new TraceEventInfoWrapper(eventRecord);
                try {
                    this.traceEventInfoCache.Add(eventOpcode, traceEventInfo);
                }
                catch (ArgumentException) {
                    // Some other thread added this entry.
                    shouldDispose = true;
                }
            }

            // Get the properties using the current event information (schema).
            PropertyBag properties = traceEventInfo.GetProperties(eventRecord);
            // Dispose the event information because it doesn't live in the cache
            if (shouldDispose) {
                traceEventInfo.Dispose();
            }

            EventArrivedEventArgs args = new EventArrivedEventArgs(eventOpcode, properties);

            return args;
        }

        public void Dispose() {
            Cleanup();
            GC.SuppressFinalize(this);
        }

        private void EventRecordCallback([In] ref EventRecord eventRecord) {
            EventHandler<EventArrivedEventArgs> eventArrived = this.EventArrived;
            if (eventArrived != null) {
                EventArrivedEventArgs e = CreateEventArgsFromEventRecord(eventRecord);
                eventArrived(this, e);
            }
        }

        private bool LoadExistingEventTraceProperties() {
            const int ERROR_WMI_INSTANCE_NOT_FOUND = 4201;
            this.eventTraceProperties = new EventTraceProperties(true);
            int status = NativeMethods.QueryTrace(0, this.loggerName, ref this.eventTraceProperties);
            if (status == 0) {
                return true;
            }
            else if (status == ERROR_WMI_INSTANCE_NOT_FOUND) {
                // The instance name passed was not recognized as valid by a WMI data provider.
                return false;
            }
            throw new System.ComponentModel.Win32Exception(status);
        }

        [SuppressMessage("Microsoft.Design", "CA1031:DoNotCatchGeneralExceptionTypes")]
        private void ProcessTraceInBackground(TraceSafeHandle traceHandle) {
            Exception asyncException = null;
            ulong[] array = { traceHandle.UnsafeValue };

            try {
                // Begin receiving the events handled by EventRecordCallback.
                // It is a blocking call until the trace handle gets closed.
                int status = NativeMethods.ProcessTrace(array, 1, IntPtr.Zero, IntPtr.Zero);
                if (status != 0) {
                    const int ERROR_INVALID_HANDLE = 6;
                    if (status == ERROR_INVALID_HANDLE) {
                        // The handle was closed before starting processing.
                    }
                    else {
                        // Throw the exception to capture the stack.
                        throw new Win32Exception(status);
                    }
                }
            }
            catch (Exception exception) {
                asyncException = exception;
            }

            // Send exception to subscribers.
            EventHandler<EventArrivedEventArgs> eventArrived = this.EventArrived;
            if (asyncException != null && eventArrived != null) {
                try {
                    eventArrived(this, new EventArrivedEventArgs(asyncException));
                }
                catch (Exception exception) {
                    if (exception is ThreadAbortException
                        || exception is OutOfMemoryException
                        || exception is StackOverflowException) {
                        throw;
                    }

                    // Never fail because non-critical exceptions thown by this method
                    // can be rethrow during disposing of this object.
                    Debug.Assert(false, "Exception was thrown from ProcessEventArrived handler", exception.ToString());
                }
            }
        }

        private void SetEnabled(bool value) {
            lock (this) {
                if (this.enabled == value) {
                    return;
                }

                if (value) {
                    StartTracing();
                }
                else {
                    StopTracing();
                }
                this.enabled = value;
            }
        }

        public void Start() {
            SetEnabled(true);
        }

        [SuppressMessage("Microsoft.Security", "CA2122:DoNotIndirectlyExposeMethodsWithLinkDemands")]
        private void StartTracing() {
            const uint RealTime = 0x00000100;
            const uint EventRecord = 0x10000000;
            const uint BufferSize = 64;
            const uint MinBuffers = 20;
            const uint MaxBuffers = 200;
            const uint FlushTimerSeconds = 1;
            int status;

            if (!LoadExistingEventTraceProperties()) {
                this.eventTraceProperties.SetParameters(RealTime, BufferSize, MinBuffers, MaxBuffers, FlushTimerSeconds);

                // Start trace session
                ulong unsafeSessionHandle;
                status = NativeMethods.StartTrace(out unsafeSessionHandle, this.loggerName, ref this.eventTraceProperties);
                if (status != 0) {
                    throw new System.ComponentModel.Win32Exception(status);
                }
                this.sessionHandle = new SessionSafeHandle(unsafeSessionHandle, this.loggerName);

                Guid EmptyGuid = Guid.Empty;

                Version Windows7Version = new Version(6, 1, 7600);
                if (Environment.OSVersion.Version.CompareTo(Windows7Version) >= 0) {
                    const int TimeToWaitForInitialize = 10 * 1000;
                    EnableTraceParameters enableParameters = new EnableTraceParameters();
                    enableParameters.Version = 1; // ENABLE_TRACE_PARAMETERS_VERSION
                    enableParameters.EnableProperty = EventEnableProperty.Sid;
                    status = NativeMethods.EnableTraceEx2(
                                unsafeSessionHandle,
                                ref this.eventProviderId,
                                1, // controlCode - EVENT_CONTROL_CODE_ENABLE_PROVIDER
                                (byte)this.Level,
                                this.MatchAnyKeyword,
                                0, // matchAnyKeyword
                                TimeToWaitForInitialize,
                                ref enableParameters);
                }
                else {
                    status = NativeMethods.EnableTraceEx(
                                ref this.eventProviderId,
                                ref EmptyGuid,          // sourceId
                                unsafeSessionHandle,
                                1,                      // isEnabled
                                (byte)this.Level,
                                this.MatchAnyKeyword,
                                0,                      // matchAllKeywords
                                EventEnableProperty.Sid,
                                IntPtr.Zero);
                }
                if (status != 0) {
                    throw new System.ComponentModel.Win32Exception(status);
                }
            }

            this.logFile = new EventTraceLogfile();
            this.logFile.LoggerName = this.loggerName;
            this.logFile.EventRecordCallback = EventRecordCallback;

            this.logFile.ProcessTraceMode = EventRecord | RealTime;
            ulong unsafeTraceHandle = NativeMethods.OpenTrace(ref this.logFile);
            status = Marshal.GetLastWin32Error();
            if (status != 0) {
                throw new System.ComponentModel.Win32Exception(status);
            }
            this.traceHandle = new TraceSafeHandle(unsafeTraceHandle);

            this.processEventsDelgate = new ProcessTraceDelegate(ProcessTraceInBackground);
            this.asyncResult = this.processEventsDelgate.BeginInvoke(this.traceHandle, null, this.processEventsDelgate);           
        }

        public void Stop() {
            SetEnabled(false);
        }

        [SuppressMessage("Microsoft.Security", "CA2122:DoNotIndirectlyExposeMethodsWithLinkDemands")]
        private void StopTracing() {
            if (this.traceHandle != null) {
                this.traceHandle.Dispose();
                this.traceHandle = null;
            }

            if (this.sessionHandle != null) {
                this.sessionHandle.Dispose();
                this.sessionHandle = null;
            }

            // Once the unmanaged resources got released, end the process trace thread
            // that may throw exception (e.g. OOM).
            if (this.processEventsDelgate != null && this.asyncResult != null) {
                this.processEventsDelgate.EndInvoke(this.asyncResult);
            }
        }

        private sealed class TraceSafeHandle : SafeHandle {
            private ulong traceHandle;

            [SuppressMessage("Microsoft.Security", "CA2122:DoNotIndirectlyExposeMethodsWithLinkDemands")]
            public TraceSafeHandle(ulong handle)
                : base(IntPtr.Zero, true) {
                this.traceHandle = handle;
            }

            public override bool IsInvalid {
                get {
                    return this.traceHandle == 0;
                }
            }

            internal ulong UnsafeValue {
                get {
                    return this.traceHandle;
                }
            }

            protected override bool ReleaseHandle() {
                return NativeMethods.CloseTrace(this.traceHandle) != 0;
            }
        }

        private sealed class SessionSafeHandle : SafeHandle {
            private readonly ulong sessionHandle;
            private readonly string loggerName;

            [SuppressMessage("Microsoft.Security", "CA2122:DoNotIndirectlyExposeMethodsWithLinkDemands")]
            public SessionSafeHandle(ulong sessionHandle, string loggerName)
                : base(IntPtr.Zero, true) {
                this.sessionHandle = sessionHandle;
                this.loggerName = loggerName;
            }
            public override bool IsInvalid {
                get {
                    return this.sessionHandle == 0;
                }
            }

            protected override bool ReleaseHandle() {
                EventTraceProperties properties = new EventTraceProperties(true /*initialize*/);
                return NativeMethods.StopTrace(this.sessionHandle, this.loggerName, out properties /*as statistics*/) != 0;
            }
        }
    }
}