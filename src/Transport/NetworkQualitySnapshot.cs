using System;

namespace TCAMultiplayer.Transport
{
    public struct NetworkQualitySnapshot
    {
        public bool HasData;
        public float SmoothedRttMs;
        public float LastRttMs;
        public float SecondsSinceLastReceive;
        public int ConnectedPeerCount;
        public int RecentPacketsSent;
        public int RecentPacketsReceived;
        public float RecentOutgoingKbps;
        public float RecentIncomingKbps;
        public string LocalEndpoint;
        public string RemoteEndpoint;
        public string RouteDescription;
        public string StatusMessage;

        public static NetworkQualitySnapshot Empty(string statusMessage = null)
        {
            return new NetworkQualitySnapshot
            {
                HasData = false,
                SmoothedRttMs = 0f,
                LastRttMs = 0f,
                SecondsSinceLastReceive = float.PositiveInfinity,
                StatusMessage = statusMessage ?? string.Empty
            };
        }
    }

    public interface ITransportDiagnostics
    {
        event Action<string> OnStatusChanged;

        string StatusMessage { get; }

        NetworkQualitySnapshot GetNetworkQuality();
    }
}
