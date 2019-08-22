using System;
using System.Net;

namespace Cowboy.Sockets
{
    public class TcpServerConnectedEventArgs : EventArgs
    {
        public TcpServerConnectedEventArgs(IPEndPoint remoteEp)
            : this(remoteEp, null)
        {
        }

        public TcpServerConnectedEventArgs(IPEndPoint remoteEp, IPEndPoint localEp)
        {
            RemoteEndPoint = remoteEp ?? throw new ArgumentNullException("remoteEp");
            LocalEndPoint = localEp;
        }

        public IPEndPoint RemoteEndPoint { get; private set; }
        public IPEndPoint LocalEndPoint { get; private set; }

        public override string ToString()
        {
            return RemoteEndPoint.ToString();
        }
    }
}
