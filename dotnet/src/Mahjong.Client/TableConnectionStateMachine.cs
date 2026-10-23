using System;

namespace Mahjong.Client
{
    public enum TableConnectionTrigger
    {
        BeginConnect = 0,
        BeginReconnect = 1,
        TransportOpened = 2,
        WelcomeAccepted = 3,
        BaselineApplied = 4,
        ResyncRequired = 5,
        TransientFailure = 6,
        PermanentFailure = 7,
        Close = 8,
        Reset = 9
    }

    /// <summary>
    /// Explicit connection/reconnect transition table. Keeping the table in one
    /// place makes illegal transitions a testable programming error rather than
    /// an accidental state assignment scattered through the session.
    /// </summary>
    public sealed class TableConnectionStateMachine
    {
        public TableConnectionState State { get; private set; } = TableConnectionState.Disconnected;

        public void Fire(TableConnectionTrigger trigger)
        {
            TableConnectionState next;
            switch (trigger)
            {
                case TableConnectionTrigger.BeginConnect:
                    Require(trigger, State, TableConnectionState.Disconnected);
                    next = TableConnectionState.Connecting;
                    break;
                case TableConnectionTrigger.BeginReconnect:
                    RequireOneOf(trigger, State,
                        TableConnectionState.Disconnected,
                        TableConnectionState.Connecting,
                        TableConnectionState.Handshaking,
                        TableConnectionState.Synchronizing,
                        TableConnectionState.Ready,
                        TableConnectionState.Reconnecting);
                    next = TableConnectionState.Reconnecting;
                    break;
                case TableConnectionTrigger.TransportOpened:
                    RequireOneOf(trigger, State, TableConnectionState.Connecting, TableConnectionState.Reconnecting);
                    next = TableConnectionState.Handshaking;
                    break;
                case TableConnectionTrigger.WelcomeAccepted:
                    Require(trigger, State, TableConnectionState.Handshaking);
                    next = TableConnectionState.Synchronizing;
                    break;
                case TableConnectionTrigger.BaselineApplied:
                    Require(trigger, State, TableConnectionState.Synchronizing);
                    next = TableConnectionState.Ready;
                    break;
                case TableConnectionTrigger.ResyncRequired:
                    RequireOneOf(trigger, State, TableConnectionState.Synchronizing, TableConnectionState.Ready);
                    next = TableConnectionState.Synchronizing;
                    break;
                case TableConnectionTrigger.TransientFailure:
                    if (State == TableConnectionState.Closed || State == TableConnectionState.Faulted)
                    {
                        throw new InvalidOperationException("Cannot fail a " + State + " session transiently.");
                    }

                    next = TableConnectionState.Reconnecting;
                    break;
                case TableConnectionTrigger.PermanentFailure:
                    if (State == TableConnectionState.Closed)
                    {
                        throw new InvalidOperationException("Session is already closed.");
                    }

                    next = TableConnectionState.Faulted;
                    break;
                case TableConnectionTrigger.Close:
                    next = TableConnectionState.Closed;
                    break;
                case TableConnectionTrigger.Reset:
                    Require(trigger, State, TableConnectionState.Faulted);
                    next = TableConnectionState.Disconnected;
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(trigger), trigger, "Unknown connection trigger.");
            }

            State = next;
        }

        public bool CanFire(TableConnectionTrigger trigger)
        {
            try
            {
                TableConnectionState original = State;
                // Evaluate without mutating by using the same rules through a clone.
                var probe = new TableConnectionStateMachine { State = original };
                probe.Fire(trigger);
                return true;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }

        private static void Require(TableConnectionTrigger trigger, TableConnectionState actual, TableConnectionState expected)
        {
            if (actual != expected)
            {
                throw new InvalidOperationException(
                    "Connection trigger " + trigger + " requires state " + expected + " but was " + actual + ".");
            }
        }

        private static void RequireOneOf(TableConnectionTrigger trigger, TableConnectionState actual, params TableConnectionState[] expected)
        {
            for (int i = 0; i < expected.Length; i++)
            {
                if (actual == expected[i])
                {
                    return;
                }
            }

            throw new InvalidOperationException(
                "Connection trigger " + trigger + " is not valid from state " + actual + ".");
        }
    }
}
