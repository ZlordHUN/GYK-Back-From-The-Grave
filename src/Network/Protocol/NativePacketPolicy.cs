namespace GraveyardKeeperCoop.Network
{
    /// <summary>
    /// Native snapshot lanes that remain valid after the reliable transport is
    /// negotiated. Steam does not report a packet's original send mode on receive.
    /// Membership, compatibility, and payload validation remain the caller's job.
    /// </summary>
    internal static class NativePacketPolicy
    {
        internal static bool IsUnreliable(Op op, bool isBuildingPreview = false)
        {
            switch (op)
            {
                case Op.Ping:
                case Op.Pong:
                case Op.PlayerPosition:
                case Op.PlayerState:
                case Op.PlayerVisualSync:
                case Op.TimeSync:
                case Op.WorldEntryBarrier:
                case Op.WorkIndicatorSync:
                case Op.WGOTransformSync:
                case Op.NpcVisualSync:
                case Op.FishingPresentation:
                    return true;
                case Op.SpawnSync:
                    return isBuildingPreview;
                default:
                    return false;
            }
        }
    }
}

