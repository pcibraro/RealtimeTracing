//-----------------------------------------------------------------------------
// Author: Daniel Vasquez Lopez
//-----------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Runtime.Serialization;

namespace Monitoring.Eventing
{

    [Serializable]
    public sealed class PropertyBag : Dictionary<string, object> {

        public PropertyBag()
            : base(StringComparer.OrdinalIgnoreCase) {
        }

        public PropertyBag(int capacity)
            : base(capacity, StringComparer.Ordinal) {
        }

        private PropertyBag(SerializationInfo info, StreamingContext context)
            : base(info, context) {
        }
    }

}
