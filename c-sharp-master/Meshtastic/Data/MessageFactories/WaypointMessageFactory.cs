using Google.Protobuf;
using Meshtastic.Protobufs;

namespace Meshtastic.Data.MessageFactories;

public class WaypointMessageFactory
{
    private readonly DeviceStateContainer container;
    private readonly uint? dest;

    public WaypointMessageFactory(DeviceStateContainer container, uint? dest = null)
    {
        this.container = container;
        this.dest = dest;
    }

    public MeshPacket CreateWaypointPacket(Waypoint waypoint, uint channel = 0)
    {
        return new MeshPacket()
        {
            Channel = channel,
            WantAck = true,
            To = dest ?? 0xffffffff, // Default to broadcast
            Id = (uint)Random.Shared.Next(1, int.MaxValue),
            HopLimit = container?.GetHopLimitOrDefault() ?? 3,
            Decoded = new Protobufs.Data()
            {
                Portnum = PortNum.WaypointApp,
                Payload = waypoint.ToByteString(),
            },
        };
    }
}