using System;
using System.Net;

namespace Cowboy.Sockets
{
    public class TcpServerDisconnectedEventArgs : EventArgs
    {
        public TcpServerDisconnectedEventArgs(IPEndPoint remoteEp)
            : this(remoteEp, null)
        {
        }

        public TcpServerDisconnectedEventArgs(IPEndPoint remoteEp, IPEndPoint localEp)
        {
            this.RemoteEndPoint = remoteEp ?? throw new ArgumentNullException("remoteEp");
            this.LocalEndPoint = localEp;
        }

        public IPEndPoint RemoteEndPoint { get; private set; }
        public IPEndPoint LocalEndPoint { get; private set; }

        public override string ToString()
        {
            return this.RemoteEndPoint.ToString();
        }
    }
}
