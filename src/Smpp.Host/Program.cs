using System;

namespace Smpp.Host
{
    internal static class Program
    {
        private static int Main()
        {
            try
            {
                return new GatewayHost().RunAsync().GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex);
                return 1;
            }
        }
    }
}
