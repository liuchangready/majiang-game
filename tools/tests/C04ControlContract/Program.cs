using System;
using Mahjong.Unity.Tests.PlayMode;

internal static class Program
{
    private static int Main()
    {
        int failures = 0;
        try
        {
            C04ControlContract.Assert(true, true, true, "DECISION_MISMATCH", "DECISION_MISMATCH", true, 1, 0);
            Console.WriteLine("PASS legal-control-positive");
        }
        catch (Exception exception)
        {
            failures++;
            Console.WriteLine("FAIL legal-control-positive " + exception.Message);
        }

        failures += MustFail("null-control", () => C04ControlContract.Assert(false, true, true, "DECISION_MISMATCH", "DECISION_MISMATCH", true, 1, 0));
        failures += MustFail("epoch-unchanged", () => C04ControlContract.Assert(true, false, true, "DECISION_MISMATCH", "DECISION_MISMATCH", true, 1, 0));
        failures += MustFail("still-automated", () => C04ControlContract.Assert(true, true, false, "DECISION_MISMATCH", "DECISION_MISMATCH", true, 1, 0));
        failures += MustFail("wrong-reason", () => C04ControlContract.Assert(true, true, true, "DECISION_MISMATCH", "STALE_CONTROL_EPOCH", true, 1, 0));
        failures += MustFail("not-rejected", () => C04ControlContract.Assert(true, true, true, "DECISION_MISMATCH", "DECISION_MISMATCH", false, 1, 0));
        failures += MustFail("duplicate-command", () => C04ControlContract.Assert(true, true, true, "DECISION_MISMATCH", "DECISION_MISMATCH", true, 2, 0));
        failures += MustFail("pending-target", () => C04ControlContract.Assert(true, true, true, "DECISION_MISMATCH", "DECISION_MISMATCH", true, 1, 1));

        Console.WriteLine(failures == 0 ? "C04_CONTROL_CONTRACT_PASS" : "C04_CONTROL_CONTRACT_FAIL failures=" + failures);
        return failures == 0 ? 0 : 1;
    }

    private static int MustFail(string name, Action action)
    {
        try
        {
            action();
            Console.WriteLine("FAIL " + name + " unexpectedly accepted");
            return 1;
        }
        catch (Exception exception)
        {
            Console.WriteLine("PASS " + name + " rejected: " + exception.Message);
            return 0;
        }
    }
}
