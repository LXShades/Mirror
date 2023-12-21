using System.Net.Sockets;

namespace Mirror.SimpleWeb
{
    public struct TcpConfig
    {
        public readonly bool noDelay;
        public readonly int sendTimeout;
        public readonly int receiveTimeout;
        public int connectionRetryDurationMs;
        public int minTimePerConnectionRetryMs;

        public TcpConfig(bool noDelay, int sendTimeout, int receiveTimeout, int connectionRetryDurationMs = 10000, int minTimePerConnectionRetryMs = 1000)
        {
            this.noDelay = noDelay;
            this.sendTimeout = sendTimeout;
            this.receiveTimeout = receiveTimeout;
            this.connectionRetryDurationMs = connectionRetryDurationMs;
            this.minTimePerConnectionRetryMs = minTimePerConnectionRetryMs;
        }

        public void ApplyTo(TcpClient client)
        {
            client.SendTimeout = sendTimeout;
            client.ReceiveTimeout = receiveTimeout;
            client.NoDelay = noDelay;
        }
    }
}
