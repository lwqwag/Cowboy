﻿using System;

namespace Cowboy.Sockets
{
    public class SessionPool : ObjectPool<TcpSocketSaeaSession>
    {
        private Func<TcpSocketSaeaSession> _createSession;
        private Action<TcpSocketSaeaSession> _cleanSession;

        public SessionPool(Func<TcpSocketSaeaSession> createSession, Action<TcpSocketSaeaSession> cleanSession)
            : base()
        {
            _createSession = createSession ?? throw new ArgumentNullException("createSession");
            _cleanSession = cleanSession ?? throw new ArgumentNullException("cleanSession");
        }

        public SessionPool Initialize(int initialCount = 0)
        {
            if (initialCount < 0)
                throw new ArgumentOutOfRangeException(
                    "initialCount",
                    initialCount,
                    "Initial count must not be less than zero.");

            for (int i = 0; i < initialCount; i++)
            {
                Add(Create());
            }

            return this;
        }

        protected override TcpSocketSaeaSession Create()
        {
            return _createSession();
        }

        public void Return(TcpSocketSaeaSession saea)
        {
            _cleanSession(saea);
            Add(saea);
        }
    }
}
