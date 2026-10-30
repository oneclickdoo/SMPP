using System;

namespace Smpp.Simulator
{
    internal static class Program
    {
        private static int Main(string[] args)
        {
            var port = 2776;
            var systemId = "demo";
            var password = "demo";
            for (var i = 0; i < args.Length; i++)
            {
                if (args[i] == "--port" && i + 1 < args.Length)
                {
                    int.TryParse(args[++i], out port);
                }
                else if (args[i] == "--system-id" && i + 1 < args.Length)
                {
                    systemId = args[++i];
                }
                else if (args[i] == "--password" && i + 1 < args.Length)
                {
                    password = args[++i];
                }
            }

            using (var simulator = new SimulatorSmsc
            {
                ListenPort = port < 0 ? 0 : port,
                SystemId = systemId,
                Password = password,
                SendDeliveryReceipts = true
            })
            {
                simulator.StartAsync().GetAwaiter().GetResult();
                Console.WriteLine("Simulator SMSC listening on 127.0.0.1:" + simulator.Port + " system_id=" + systemId);
                Console.WriteLine("Press Enter to stop.");
                Console.ReadLine();
            }

            return 0;
        }
    }
}
