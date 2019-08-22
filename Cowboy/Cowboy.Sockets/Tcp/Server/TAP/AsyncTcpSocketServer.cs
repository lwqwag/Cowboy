﻿using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Logrila.Logging;

namespace Cowboy.Sockets
{
    public class AsyncTcpSocketServer
    {
        #region Fields

        private static readonly ILog Log = Logger.Get<AsyncTcpSocketServer>();
        private TcpListener _listener;
        private readonly ConcurrentDictionary<string, AsyncTcpSocketSession> _sessions = new ConcurrentDictionary<string, AsyncTcpSocketSession>();
        private readonly IAsyncTcpSocketServerEventDispatcher _dispatcher;
        private readonly AsyncTcpSocketServerConfiguration _configuration;

        private int _state;
        private const int NONE = 0;
        private const int LISTENING = 1;
        private const int DISPOSED = 5;

        #endregion

        #region Constructors

        public AsyncTcpSocketServer(int listenedPort, IAsyncTcpSocketServerEventDispatcher dispatcher, AsyncTcpSocketServerConfiguration configuration = null)
            : this(IPAddress.Any, listenedPort, dispatcher, configuration)
        {
        }

        public AsyncTcpSocketServer(IPAddress listenedAddress, int listenedPort, IAsyncTcpSocketServerEventDispatcher dispatcher, AsyncTcpSocketServerConfiguration configuration = null)
            : this(new IPEndPoint(listenedAddress, listenedPort), dispatcher, configuration)
        {
        }

        public AsyncTcpSocketServer(IPEndPoint listenedEndPoint, IAsyncTcpSocketServerEventDispatcher dispatcher, AsyncTcpSocketServerConfiguration configuration = null)
        {
            this.ListenedEndPoint = listenedEndPoint ?? throw new ArgumentNullException("listenedEndPoint");
            _dispatcher = dispatcher ?? throw new ArgumentNullException("dispatcher");
            _configuration = configuration ?? new AsyncTcpSocketServerConfiguration();

            if (_configuration.BufferManager == null)
                throw new InvalidProgramException("The buffer manager in configuration cannot be null.");
            if (_configuration.FrameBuilder == null)
                throw new InvalidProgramException("The frame handler in configuration cannot be null.");
        }

        public AsyncTcpSocketServer(
            int listenedPort,
            Func<AsyncTcpSocketSession, byte[], int, int, Task> onSessionDataReceived = null,
            Func<AsyncTcpSocketSession, Task> onSessionStarted = null,
            Func<AsyncTcpSocketSession, Task> onSessionClosed = null,
            AsyncTcpSocketServerConfiguration configuration = null)
            : this(IPAddress.Any, listenedPort, onSessionDataReceived, onSessionStarted, onSessionClosed, configuration)
        {
        }

        public AsyncTcpSocketServer(
            IPAddress listenedAddress, int listenedPort,
            Func<AsyncTcpSocketSession, byte[], int, int, Task> onSessionDataReceived = null,
            Func<AsyncTcpSocketSession, Task> onSessionStarted = null,
            Func<AsyncTcpSocketSession, Task> onSessionClosed = null,
            AsyncTcpSocketServerConfiguration configuration = null)
            : this(new IPEndPoint(listenedAddress, listenedPort), onSessionDataReceived, onSessionStarted, onSessionClosed, configuration)
        {
        }

        public AsyncTcpSocketServer(
            IPEndPoint listenedEndPoint,
            Func<AsyncTcpSocketSession, byte[], int, int, Task> onSessionDataReceived = null,
            Func<AsyncTcpSocketSession, Task> onSessionStarted = null,
            Func<AsyncTcpSocketSession, Task> onSessionClosed = null,
            AsyncTcpSocketServerConfiguration configuration = null)
            : this(listenedEndPoint,
                  new DefaultAsyncTcpSocketServerEventDispatcher(onSessionDataReceived, onSessionStarted, onSessionClosed),
                  configuration)
        {
        }

        #endregion

        #region Properties

        public IPEndPoint ListenedEndPoint { get; private set; }
        public bool IsListening => _state == LISTENING;
        public int SessionCount => _sessions.Count;

        #endregion

        #region Server

        public void Listen()
        {
            int origin = Interlocked.CompareExchange(ref _state, LISTENING, NONE);
            if (origin == DISPOSED)
            {
                throw new ObjectDisposedException(GetType().FullName);
            }
            else if (origin != NONE)
            {
                throw new InvalidOperationException("This tcp server has already started.");
            }

            try
            {
                _listener = new TcpListener(this.ListenedEndPoint);
                SetSocketOptions();

                _listener.Start(_configuration.PendingConnectionBacklog);

                Task.Factory.StartNew(async () =>
                {
                    await Accept();
                },
                TaskCreationOptions.LongRunning)
                .Forget();
            }
            catch (Exception ex) when (!ShouldThrow(ex)) { }
        }

        public void Shutdown()
        {
            if (Interlocked.Exchange(ref _state, DISPOSED) == DISPOSED)
            {
                return;
            }

            try
            {
                _listener.Stop();
                _listener = null;

                Task.Factory.StartNew(async () =>
                {
                    try
                    {
                        foreach (var session in _sessions.Values)
                        {
                            await session.Close(); // parent server close session when shutdown
                        }
                    }
                    catch (Exception ex) when (!ShouldThrow(ex)) { }
                },
                TaskCreationOptions.PreferFairness)
                .Wait();
            }
            catch (Exception ex) when (!ShouldThrow(ex)) { }
        }

        private void SetSocketOptions()
        {
            _listener.AllowNatTraversal(_configuration.AllowNatTraversal);
            _listener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, _configuration.ReuseAddress);
        }

        public bool Pending()
        {
            if (!IsListening)
                throw new InvalidOperationException("The tcp server is not active.");

            // determine if there are pending connection requests.
            return _listener.Pending();
        }

        private async Task Accept()
        {
            try
            {
                while (IsListening)
                {
                    var tcpClient = await _listener.AcceptTcpClientAsync();
                    Task.Factory.StartNew(async () =>
                    {
                        await Process(tcpClient);
                    },
                    TaskCreationOptions.None)
                    .Forget();
                }
            }
            catch (Exception ex) when (!ShouldThrow(ex)) { }
            catch (Exception ex)
            {
                Log.Error(ex.Message, ex);
            }
        }

        private async Task Process(TcpClient acceptedTcpClient)
        {
            var session = new AsyncTcpSocketSession(acceptedTcpClient, _configuration, _configuration.BufferManager, _dispatcher, this);

            if (_sessions.TryAdd(session.SessionKey, session))
            {
                Log.DebugFormat("New session [{0}].", session);
                try
                {
                    await session.Start();
                }
                catch (TimeoutException ex)
                {
                    Log.Error(ex.Message, ex);
                }
                finally
                {
                    AsyncTcpSocketSession throwAway;
                    if (_sessions.TryRemove(session.SessionKey, out throwAway))
                    {
                        Log.DebugFormat("Close session [{0}].", throwAway);
                    }
                }
            }
        }

        private bool ShouldThrow(Exception ex)
        {
            if (ex is ObjectDisposedException
                || ex is InvalidOperationException
                || ex is SocketException
                || ex is IOException)
            {
                return false;
            }
            return true;
        }

        #endregion

        #region Send

        public async Task SendToAsync(string sessionKey, byte[] data)
        {
            await SendToAsync(sessionKey, data, 0, data.Length);
        }

        public async Task SendToAsync(string sessionKey, byte[] data, int offset, int count)
        {
            AsyncTcpSocketSession sessionFound;
            if (_sessions.TryGetValue(sessionKey, out sessionFound))
            {
                await sessionFound.SendAsync(data, offset, count);
            }
            else
            {
                Log.WarnFormat("Cannot find session [{0}].", sessionKey);
            }
        }

        public async Task SendToAsync(AsyncTcpSocketSession session, byte[] data)
        {
            await SendToAsync(session, data, 0, data.Length);
        }

        public async Task SendToAsync(AsyncTcpSocketSession session, byte[] data, int offset, int count)
        {
            AsyncTcpSocketSession sessionFound;
            if (_sessions.TryGetValue(session.SessionKey, out sessionFound))
            {
                await sessionFound.SendAsync(data, offset, count);
            }
            else
            {
                Log.WarnFormat("Cannot find session [{0}].", session);
            }
        }

        public async Task BroadcastAsync(byte[] data)
        {
            await BroadcastAsync(data, 0, data.Length);
        }

        public async Task BroadcastAsync(byte[] data, int offset, int count)
        {
            foreach (var session in _sessions.Values)
            {
                await session.SendAsync(data, offset, count);
            }
        }

        #endregion

        #region Session

        public bool HasSession(string sessionKey)
        {
            return _sessions.ContainsKey(sessionKey);
        }

        public AsyncTcpSocketSession GetSession(string sessionKey)
        {
            AsyncTcpSocketSession session = null;
            _sessions.TryGetValue(sessionKey, out session);
            return session;
        }

        public async Task CloseSession(string sessionKey)
        {
            AsyncTcpSocketSession session = null;
            if (_sessions.TryGetValue(sessionKey, out session))
            {
                await session.Close(); // parent server close session by session-key
            }
        }

        #endregion
    }
}
