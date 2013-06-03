using Microsoft.AspNet.SignalR;
using Microsoft.AspNet.SignalR.Client.Hubs;
using Microsoft.Owin.Hosting;
using Owin;
using Monitoring.Eventing;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Monitoring
{
class Program
{
    static void Main(string[] args)
    {
        string url = "http://localhost:8080";

        using (WebApplication.Start<Startup>(url))
        {
            Console.WriteLine("Server running on {0}", url);
                
            var hubConnection = new HubConnection("http://localhost:8080/");
            var serverHub = hubConnection.CreateHubProxy("EventsHub");
            hubConnection.Start().Wait();

            var providerId = new Guid("13D5F7EF-9404-47ea-AF13-85484F09F2A7");

            using (EventTraceWatcher watcher = new EventTraceWatcher("MySession", providerId))
            {
                watcher.EventArrived += delegate(object sender, EventArrivedEventArgs e)
                {
                    if (e.Error != null)
                    {
                        Console.Error.WriteLine(e.Error);
                        Environment.Exit(-1);
                    }

                    // Dump properties (key/value)
                    foreach (var p in e.Properties)
                    {
                        serverHub.Invoke("PushEvent", "\t" + p.Key + " -- " + p.Value).Wait();
                    }
                };

                // Start listening
                watcher.Start();

                Console.WriteLine("Listening...Press <Enter> to exit");
                Console.ReadLine();
            }

            Console.ReadLine();
        }
    }
}

class Startup
{
    public void Configuration(IAppBuilder app)
    {
        // Turn cross domain on 
        var config = new HubConfiguration { EnableCrossDomain = true };
            
        // This will map out to http://localhost:8080/signalr by default
        app.MapHubs(config);
    }
}
        
public class EventsHub : Hub
{
    public void PushEvent(string message)
    {
        Console.WriteLine("Event: " + message);

        Clients.All.PushEvent(message);
    }
}
}
