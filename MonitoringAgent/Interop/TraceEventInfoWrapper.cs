using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;


namespace Monitoring.Eventing.Interop
{

    internal sealed class TraceEventInfoWrapper : IDisposable {

        /// <summary>
        /// Base address of the native TraceEventInfo structure.
        /// </summary>
        private IntPtr _address;

        /// <summary>
        /// Managed representation of the native TraceEventInfo structure.
        /// </summary>
        private TraceEventInfo _traceEventInfo;

        //
        // True if the event has a schema with well defined properties.
        //
        private bool _hasProperties;

        /// <summary>
        /// Marshalled array of EventPropertyInfo objects.
        /// </summary>
        private EventPropertyInfo[] _eventPropertyInfoArray;

        internal TraceEventInfoWrapper(EventRecord eventRecord) {
            Initialize(eventRecord);
        }

        ~TraceEventInfoWrapper() {
            ReleaseMemory();
        }

        internal string EventName {
            private set;
            get;
        }

        public void Dispose() {
            ReleaseMemory();
            GC.SuppressFinalize(this);
        }

        internal PropertyBag GetProperties(EventRecord eventRecord) {
            // We only support top level properties and simple types
            PropertyBag properties = new PropertyBag(_traceEventInfo.TopLevelPropertyCount);

            if (this._hasProperties) {

                int offset = 0;

                for (int i = 0; i < _traceEventInfo.TopLevelPropertyCount; i++) {
                    EventPropertyInfo info = _eventPropertyInfoArray[i];

                    // Read the current property name
                    string propertyName = Marshal.PtrToStringUni(new IntPtr(_address.ToInt64() + info.NameOffset));

                    object value;
                    string mapName;
                    int length;
                    IntPtr dataPtr = new IntPtr(eventRecord.UserData.ToInt64() + offset);

                    value = ReadPropertyValue(info, dataPtr, out mapName, out length);

                    // If we have a map name, return both map name and map value as a pair.
                    if (!string.IsNullOrEmpty(mapName)) {
                        value = new KeyValuePair<string, object>(mapName, value);
                    }

                    offset += length;
                    properties.Add(propertyName, value);
                }

                if (offset < eventRecord.UserDataLength) {
                    //
                    // There is some extra information not mapped.
                    //
                    IntPtr dataPtr = new IntPtr(eventRecord.UserData.ToInt64() + offset);
                    int length = eventRecord.UserDataLength - offset;
                    byte[] array = new byte[length];

                    for (int index = 0; index < length; index++) {
                        array[index] = Marshal.ReadByte(dataPtr, index);
                    }

                    properties.Add("__ExtraPayload", array);
                }
            }
            else {
                // NOTE: It is just a guess that this is an Unicode string
                string str = Marshal.PtrToStringUni(eventRecord.UserData);

                properties.Add("EventData", str);
            }

            return properties;
        }

        private void Initialize(EventRecord eventRecord) {
            int size = 0;
            const uint BufferTooSmall = 122;
            const uint ErrorlementNotFound = 1168;

            int error = NativeMethods.TdhGetEventInformation(ref eventRecord, 0, IntPtr.Zero, IntPtr.Zero, ref size);
            if (error == ErrorlementNotFound) {
                // Nothing else to do here.
                this._hasProperties = false;
                return;
            }
            this._hasProperties = true;

            if (error != BufferTooSmall) {
                throw new Win32Exception(error);
            }

            // Get the event information (schema)
            this._address = Marshal.AllocHGlobal(size);
            error = NativeMethods.TdhGetEventInformation(ref eventRecord, 0, IntPtr.Zero, _address, ref size);
            if (error != 0) {
                throw new System.ComponentModel.Win32Exception(error);
            }

            // Marshal the first part of the trace event information.
            _traceEventInfo = (TraceEventInfo)Marshal.PtrToStructure(this._address, typeof(TraceEventInfo));

            // Marshal the second part of the trace event information, the array of property info.
            int actualSize = Marshal.SizeOf(this._traceEventInfo);
            if (size != actualSize) {
                int structSize = Marshal.SizeOf(typeof(EventPropertyInfo));
                int itemsLeft = (size - actualSize) / structSize;

                this._eventPropertyInfoArray = new EventPropertyInfo[itemsLeft];
                long baseAddress = _address.ToInt64() + actualSize;
                for (int i = 0; i < itemsLeft; i++) {
                    IntPtr structPtr = new IntPtr(baseAddress + (i * structSize));
                    EventPropertyInfo info = (EventPropertyInfo)Marshal.PtrToStructure(structPtr, typeof(EventPropertyInfo));
                    this._eventPropertyInfoArray[i] = info;
                }
            }

            // Get the opcode name
            if (_traceEventInfo.OpcodeNameOffset > 0) {
                this.EventName = Marshal.PtrToStringUni(new IntPtr(_address.ToInt64() + _traceEventInfo.OpcodeNameOffset));
            }
        }

        private object ReadPropertyValue(EventPropertyInfo info, IntPtr dataPtr, out string mapName, out int length) {

            length = info.LengthPropertyIndex;

            if (info.NonStructTypeValue.MapNameOffset != 0) {
                mapName = Marshal.PtrToStringUni(new IntPtr(this._address.ToInt64() + info.NonStructTypeValue.MapNameOffset));
            }
            else {
                mapName = string.Empty;
            }

            switch (info.NonStructTypeValue.InType) {
                case TdhInType.Null:
                    break;
                case TdhInType.UnicodeString: {
                        string str = Marshal.PtrToStringUni(dataPtr);
                        length = (str.Length + 1) * sizeof(char);
                        return str;
                    }
                case TdhInType.AnsiString: {
                        string str = Marshal.PtrToStringAnsi(dataPtr);
                        length = (str.Length + 1);
                        return str;
                    }
                case TdhInType.Int8:
                    return (sbyte)Marshal.ReadByte(dataPtr);
                case TdhInType.UInt8:
                    return Marshal.ReadByte(dataPtr);
                case TdhInType.Int16:
                    return Marshal.ReadInt16(dataPtr);
                case TdhInType.UInt16:
                    return (uint)Marshal.ReadInt16(dataPtr);
                case TdhInType.Int32:
                    return Marshal.ReadInt32(dataPtr);
                case TdhInType.UInt32:
                    return (uint)Marshal.ReadInt32(dataPtr);
                case TdhInType.Int64:
                    return Marshal.ReadInt64(dataPtr);
                case TdhInType.UInt64:
                    return (ulong)Marshal.ReadInt64(dataPtr);
                case TdhInType.Float:
                    break;
                case TdhInType.Double:
                    break;
                case TdhInType.Boolean:
                    return (bool)(Marshal.ReadInt32(dataPtr) != 0);
                case TdhInType.Binary:
                    break;
                case TdhInType.Guid:
                    return new Guid(
                           Marshal.ReadInt32(dataPtr),
                           Marshal.ReadInt16(dataPtr, 4),
                           Marshal.ReadInt16(dataPtr, 6),
                           Marshal.ReadByte(dataPtr, 8),
                           Marshal.ReadByte(dataPtr, 9),
                           Marshal.ReadByte(dataPtr, 10),
                           Marshal.ReadByte(dataPtr, 11),
                           Marshal.ReadByte(dataPtr, 12),
                           Marshal.ReadByte(dataPtr, 13),
                           Marshal.ReadByte(dataPtr, 14),
                           Marshal.ReadByte(dataPtr, 15)
                           );
                case TdhInType.Pointer:
                    break;
                case TdhInType.FileTime:
                    return DateTime.FromFileTime(Marshal.ReadInt64(dataPtr));
                case TdhInType.SystemTime:
                    break;
                case TdhInType.SID:
                    break;
                case TdhInType.HexInt32:
                    break;
                case TdhInType.HexInt64:
                    break;
                case TdhInType.CountedString:
                    break;
                case TdhInType.CountedAnsiString:
                    break;
                case TdhInType.ReversedCountedString:
                    break;
                case TdhInType.ReversedCountedAnsiString:
                    break;
                case TdhInType.NonNullTerminatedString:
                    break;
                case TdhInType.NonNullTerminatedAnsiString:
                    break;
                case TdhInType.UnicodeChar:
                    break;
                case TdhInType.AnsiChar:
                    break;
                case TdhInType.SizeT:
                    break;
                case TdhInType.HexDump:
                    break;
                case TdhInType.WbemSID:
                    break;
                default:
                    Debugger.Break();
                    break;
            }

            throw new NotSupportedException();
        }

        [SuppressMessage("Microsoft.Security", "CA2122:DoNotIndirectlyExposeMethodsWithLinkDemands")]
        private void ReleaseMemory() {
            if (this._address != IntPtr.Zero) {
                Marshal.FreeHGlobal(this._address);
                this._address = IntPtr.Zero;
            }
        }

    }
}