
using System;

namespace Monitoring.Eventing
{

    public sealed class EventArrivedEventArgs : EventArgs {
        // Keep this event small.
        private readonly ushort eventId;
        private readonly PropertyBag properties;
        private readonly Exception error;

        internal EventArrivedEventArgs(Exception error)
            : this(0/*eventId*/, new PropertyBag()) {
            this.error = error;
        }

        internal EventArrivedEventArgs(ushort eventId, PropertyBag properties) {
            this.eventId = eventId;
            this.properties = properties;
        }

        public ushort EventId {
            get {
                return this.eventId;
            }
        }

        public PropertyBag Properties {
            get {
                return this.properties;
            }
        }

        public Exception Error {
            get {
                return this.error;
            }
        }
    }
}
