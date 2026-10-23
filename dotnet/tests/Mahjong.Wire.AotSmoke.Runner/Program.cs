using System;
using Mahjong.Contracts.Wire.AotSmoke;

namespace Mahjong.Wire.AotSmoke.Runner
{
    internal static class Program
    {
        private static int Main()
        {
            try
            {
                string result = WireAotProbe.Run();
                Console.WriteLine(result);
                return result.StartsWith(WireAotProbe.PassPrefix, StringComparison.Ordinal) ? 0 : 1;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("FAIL " + ex.GetType().Name + ": " + ex.Message);
                return 1;
            }
        }
    }
}
