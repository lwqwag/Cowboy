using System;

namespace Cowboy.Sockets.Tcp.Server.APM.EventArgs
{
    public class TcpClientConnectedEventArgs : System.EventArgs
    {
        public TcpClientConnectedEventArgs(TcpSocketSession session)
        {
            Session = session ?? throw new ArgumentNullException("session");
        }

        public TcpSocketSession Session { get; private set; }

        public override string ToString()
        {
            return string.Format("{0}", Session);
        }
    }
}
