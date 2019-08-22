using System;

namespace Cowboy.Sockets.Tcp.Server.APM.EventArgs
{
    public class TcpClientDisconnectedEventArgs : System.EventArgs
    {
        public TcpClientDisconnectedEventArgs(TcpSocketSession session)
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
