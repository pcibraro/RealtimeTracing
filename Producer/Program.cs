using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Producer
{
    class Program
    {
        static void Main(string[] args)
        {
            TraceSource myTraceSource = new TraceSource("MyConsoleApp");

            myTraceSource.TraceEvent(TraceEventType.Error, 1, "Tracing Error Message.");
            myTraceSource.TraceEvent(TraceEventType.Warning, 2, "Tracing Warning Message.");
            myTraceSource.TraceEvent(TraceEventType.Information, 3, "Tracing Information.");
            myTraceSource.TraceEvent(TraceEventType.Verbose, 4, "Tracing Verbose Message.");
            myTraceSource.TraceEvent(TraceEventType.Critical, 5, "Tracing Critical Message.");
            
            myTraceSource.Close();
        }
    }
}
