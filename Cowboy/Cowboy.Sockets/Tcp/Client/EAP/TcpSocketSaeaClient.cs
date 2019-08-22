﻿using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Cowboy.Buffer;
using Logrila.Logging;

namespace Cowboy.Sockets
{
    public class TcpSocketSaeaClient : IDisposable
    {
        #region Fields

        private static readonly ILog Log = Logger.Get<TcpSocketSaeaClient>();
        private static readonly byte[] EmptyArray = new byte[0];
        private readonly ITcpSocketSaeaClientEventDispatcher _dispatcher;
        private readonly TcpSocketSaeaClientConfiguration _configuration;
        private readonly IPEndPoint _remoteEndPoint;
        private readonly IPEndPoint _localEndPoint;
        private Socket _socket;
        private SaeaPool _saeaPool;
        private ArraySegment<byte> _receiveBuffer = default(ArraySegment<byte>);
        private int _receiveBufferOffset = 0;

        private int _state;
        private const int NONE = 0;
        private const int CONNECTING = 1;
        private const int CONNECTED = 2;
        private const int CLOSED = 5;

        private readonly object _disposeLock = new object();
        private bool _isDisposed;

        #endregion

        #region Constructors

        public TcpSocketSaeaClient(IPAddress remoteAddress, int remotePort, IPAddress localAddress, int localPort, ITcpSocketSaeaClientEventDispatcher dispatcher, TcpSocketSaeaClientConfiguration configuration = null)
            : this(new IPEndPoint(remoteAddress, remotePort), new IPEndPoint(localAddress, localPort), dispatcher, configuration)
        {
        }

        public TcpSocketSaeaClient(IPAddress remoteAddress, int remotePort, IPEndPoint localEp, ITcpSocketSaeaClientEventDispatcher dispatcher, TcpSocketSaeaClientConfiguration configuration = null)
            : this(new IPEndPoint(remoteAddress, remotePort), localEp, dispatcher, configuration)
        {
        }

        public TcpSocketSaeaClient(IPAddress remoteAddress, int remotePort, ITcpSocketSaeaClientEventDispatcher dispatcher, TcpSocketSaeaClientConfiguration configuration = null)
            : this(new IPEndPoint(remoteAddress, remotePort), dispatcher, configuration)
        {
        }

        public TcpSocketSaeaClient(IPEndPoint remoteEp, ITcpSocketSaeaClientEventDispatcher dispatcher, TcpSocketSaeaClientConfiguration configuration = null)
            : this(remoteEp, null, dispatcher, configuration)
        {
        }

        public TcpSocketSaeaClient(IPEndPoint remoteEp, IPEndPoint localEp, ITcpSocketSaeaClientEventDispatcher dispatcher, TcpSocketSaeaClientConfiguration configuration = null)
        {
            _remoteEndPoint = remoteEp ?? throw new ArgumentNullException("remoteEp");
            _localEndPoint = localEp;
            _dispatcher = dispatcher ?? throw new ArgumentNullException("dispatcher");
            _configuration = configuration ?? new TcpSocketSaeaClientConfiguration();

            if (_configuration.BufferManager == null)
                throw new InvalidProgramException("The buffer manager in configuration cannot be null.");
            if (_configuration.FrameBuilder == null)
                throw new InvalidProgramException("The frame handler in configuration cannot be null.");

            Initialize();
        }

        public TcpSocketSaeaClient(IPAddress remoteAddress, int remotePort, IPAddress localAddress, int localPort,
            Func<TcpSocketSaeaClient, byte[], int, int, Task> onServerDataReceived = null,
            Func<TcpSocketSaeaClient, Task> onServerConnected = null,
            Func<TcpSocketSaeaClient, Task> onServerDisconnected = null,
            TcpSocketSaeaClientConfiguration configuration = null)
            : this(new IPEndPoint(remoteAddress, remotePort), new IPEndPoint(localAddress, localPort),
                  onServerDataReceived, onServerConnected, onServerDisconnected, configuration)
        {
        }

        public TcpSocketSaeaClient(IPAddress remoteAddress, int remotePort, IPEndPoint localEp,
            Func<TcpSocketSaeaClient, byte[], int, int, Task> onServerDataReceived = null,
            Func<TcpSocketSaeaClient, Task> onServerConnected = null,
            Func<TcpSocketSaeaClient, Task> onServerDisconnected = null,
            TcpSocketSaeaClientConfiguration configuration = null)
            : this(new IPEndPoint(remoteAddress, remotePort), localEp,
                  onServerDataReceived, onServerConnected, onServerDisconnected, configuration)
        {
        }

        public TcpSocketSaeaClient(IPAddress remoteAddress, int remotePort,
            Func<TcpSocketSaeaClient, byte[], int, int, Task> onServerDataReceived = null,
            Func<TcpSocketSaeaClient, Task> onServerConnected = null,
            Func<TcpSocketSaeaClient, Task> onServerDisconnected = null,
            TcpSocketSaeaClientConfiguration configuration = null)
            : this(new IPEndPoint(remoteAddress, remotePort),
                  onServerDataReceived, onServerConnected, onServerDisconnected, configuration)
        {
        }

        public TcpSocketSaeaClient(IPEndPoint remoteEp,
            Func<TcpSocketSaeaClient, byte[], int, int, Task> onServerDataReceived = null,
            Func<TcpSocketSaeaClient, Task> onServerConnected = null,
            Func<TcpSocketSaeaClient, Task> onServerDisconnected = null,
            TcpSocketSaeaClientConfiguration configuration = null)
            : this(remoteEp, null,
                  onServerDataReceived, onServerConnected, onServerDisconnected, configuration)
        {
        }

        public TcpSocketSaeaClient(IPEndPoint remoteEp, IPEndPoint localEp,
            Func<TcpSocketSaeaClient, byte[], int, int, Task> onServerDataReceived = null,
            Func<TcpSocketSaeaClient, Task> onServerConnected = null,
            Func<TcpSocketSaeaClient, Task> onServerDisconnected = null,
            TcpSocketSaeaClientConfiguration configuration = null)
            : this(remoteEp, localEp,
                 new DefaultTcpSocketSaeaClientEventDispatcher(onServerDataReceived, onServerConnected, onServerDisconnected),
                 configuration)
        {
        }

        private void Initialize()
        {
            _saeaPool = new SaeaPool(
                () =>
                {
                    var saea = new SaeaAwaitable();
                    return saea;
                },
                (saea) =>
                {
                    try
                    {
                        saea.Saea.AcceptSocket = null;
                        saea.Saea.SetBuffer(EmptyArray, 0, 0);
                        saea.Saea.RemoteEndPoint = null;
                        saea.Saea.SocketFlags = SocketFlags.None;
                    }
                    catch (Exception ex) // initialize SAEA error occurred
                    {
                        Log.Error(ex.Message, ex);
                    }
                })
                .Initialize(256);
        }

        #endregion

        #region Properties

        private bool Connected => _socket != null && _socket.Connected;
        public IPEndPoint RemoteEndPoint => Connected ? (IPEndPoint)_socket.RemoteEndPoint : _remoteEndPoint;
        public IPEndPoint LocalEndPoint => Connected ? (IPEndPoint)_socket.LocalEndPoint : _localEndPoint;

        public TcpSocketConnectionState State
        {
            get
            {
                switch (_state)
                {
                    case NONE:
                        return TcpSocketConnectionState.None;
                    case CONNECTING:
                        return TcpSocketConnectionState.Connecting;
                    case CONNECTED:
                        return TcpSocketConnectionState.Connected;
                    case CLOSED:
                        return TcpSocketConnectionState.Closed;
                    default:
                        return TcpSocketConnectionState.Closed;
                }
            }
        }

        public override string ToString()
        {
            return string.Format("RemoteEndPoint[{0}], LocalEndPoint[{1}]",
                RemoteEndPoint, LocalEndPoint);
        }

        #endregion

        #region Connect

        public async Task Connect()
        {
            int origin = Interlocked.Exchange(ref _state, CONNECTING);
            if (!(origin == NONE || origin == CLOSED))
            {
                await Close(false); // connecting with wrong state
                throw new InvalidOperationException("This tcp socket client is in invalid state when connecting.");
            }

            Clean(); // force to clean

            var saea = _saeaPool.Take();

            try
            {
                _socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
                SetSocketOptions();

                if (_localEndPoint != null)
                {
                    _socket.Bind(_localEndPoint);
                }

                saea.Saea.RemoteEndPoint = _remoteEndPoint;

                var socketError = await _socket.ConnectAsync(saea);
                if (socketError != SocketError.Success)
                {
                    throw new SocketException((int)socketError);
                }

                if (_receiveBuffer == default(ArraySegment<byte>))
                    _receiveBuffer = _configuration.BufferManager.BorrowBuffer();
                _receiveBufferOffset = 0;

                if (Interlocked.CompareExchange(ref _state, CONNECTED, CONNECTING) != CONNECTING)
                {
                    await Close(false); // connected with wrong state
                    throw new InvalidOperationException("This tcp socket client is in invalid state when connected.");
                }

                Log.DebugFormat("Connected to server [{0}] with dispatcher [{1}] on [{2}].",
                    RemoteEndPoint,
                    _dispatcher.GetType().Name,
                    DateTime.UtcNow.ToString(@"yyyy-MM-dd HH:mm:ss.fffffff"));
                bool isErrorOccurredInUserSide = false;
                try
                {
                    await _dispatcher.OnServerConnected(this);
                }
                catch (Exception ex) // catch all exceptions from out-side
                {
                    isErrorOccurredInUserSide = true;
                    await HandleUserSideError(ex);
                }

                if (!isErrorOccurredInUserSide)
                {
                    Task.Factory.StartNew(async () =>
                    {
                        await Process();
                    },
                    TaskCreationOptions.None)
                    .Forget();
                }
                else
                {
                    await Close(true); // user side handle tcp connection error occurred
                }
            }
            catch (Exception ex) // catch exceptions then log then re-throw
            {
                Log.Error(ex.Message, ex);
                await Close(true); // handle tcp connection error occurred
                throw;
            }
            finally
            {
                _saeaPool.Return(saea);
            }
        }

        private async Task Process()
        {
            var saea = _saeaPool.Take();

            try
            {
                int frameLength;
                byte[] payload;
                int payloadOffset;
                int payloadCount;
                int consumedLength = 0;

                saea.Saea.SetBuffer(_receiveBuffer.Array, _receiveBuffer.Offset + _receiveBufferOffset, _receiveBuffer.Count - _receiveBufferOffset);

                while (State == TcpSocketConnectionState.Connected)
                {
                    saea.Saea.SetBuffer(_receiveBuffer.Array, _receiveBuffer.Offset + _receiveBufferOffset, _receiveBuffer.Count - _receiveBufferOffset);

                    var socketError = await _socket.ReceiveAsync(saea);
                    if (socketError != SocketError.Success)
                        break;

                    var receiveCount = saea.Saea.BytesTransferred;
                    if (receiveCount == 0)
                        break;

                    SegmentBufferDeflector.ReplaceBuffer(_configuration.BufferManager, ref _receiveBuffer, ref _receiveBufferOffset, receiveCount);
                    consumedLength = 0;

                    while (true)
                    {
                        frameLength = 0;
                        payload = null;
                        payloadOffset = 0;
                        payloadCount = 0;

                        if (_configuration.FrameBuilder.Decoder.TryDecodeFrame(_receiveBuffer.Array, _receiveBuffer.Offset + consumedLength, _receiveBufferOffset - consumedLength,
                            out frameLength, out payload, out payloadOffset, out payloadCount))
                        {
                            try
                            {
                                await _dispatcher.OnServerDataReceived(this, payload, payloadOffset, payloadCount);
                            }
                            catch (Exception ex) // catch all exceptions from out-side
                            {
                                await HandleUserSideError(ex);
                            }
                            finally
                            {
                                consumedLength += frameLength;
                            }
                        }
                        else
                        {
                            break;
                        }
                    }

                    if (_receiveBuffer != null && _receiveBuffer.Array != null)
                    {
                        SegmentBufferDeflector.ShiftBuffer(_configuration.BufferManager, consumedLength, ref _receiveBuffer, ref _receiveBufferOffset);
                    }
                }
            }
            catch (Exception ex)
            {
                await HandleReceiveOperationException(ex);
            }
            finally
            {
                await Close(true); // read async buffer returned, remote closed
                _saeaPool.Return(saea);
            }
        }

        private void SetSocketOptions()
        {
            _socket.ReceiveBufferSize = _configuration.ReceiveBufferSize;
            _socket.SendBufferSize = _configuration.SendBufferSize;
            _socket.ReceiveTimeout = (int)_configuration.ReceiveTimeout.TotalMilliseconds;
            _socket.SendTimeout = (int)_configuration.SendTimeout.TotalMilliseconds;
            _socket.NoDelay = _configuration.NoDelay;
            _socket.LingerState = _configuration.LingerState;

            if (_configuration.KeepAlive)
            {
                _socket.SetSocketOption(
                    SocketOptionLevel.Socket,
                    SocketOptionName.KeepAlive,
                    (int)_configuration.KeepAliveInterval.TotalMilliseconds);
            }

            _socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, _configuration.ReuseAddress);
        }

        #endregion

        #region Close

        public async Task Close()
        {
            await Close(true); // close by external
        }

        private async Task Close(bool shallNotifyUserSide)
        {
            if (Interlocked.Exchange(ref _state, CLOSED) == CLOSED)
            {
                return;
            }

            Shutdown();

            if (shallNotifyUserSide)
            {
                Log.DebugFormat("Disconnected from server [{0}] with dispatcher [{1}] on [{2}].",
                    RemoteEndPoint,
                    _dispatcher.GetType().Name,
                    DateTime.UtcNow.ToString(@"yyyy-MM-dd HH:mm:ss.fffffff"));
                try
                {
                    await _dispatcher.OnServerDisconnected(this);
                }
                catch (Exception ex) // catch all exceptions from out-side
                {
                    await HandleUserSideError(ex);
                }
            }

            Clean();
        }

        public void Shutdown()
        {
            // The correct way to shut down the connection (especially if you are in a full-duplex conversation) 
            // is to call socket.Shutdown(SocketShutdown.Send) and give the remote party some time to close 
            // their send channel. This ensures that you receive any pending data instead of slamming the 
            // connection shut. ObjectDisposedException should never be part of the normal application flow.
            if (_socket != null && _socket.Connected)
            {
                _socket.Shutdown(SocketShutdown.Send);
            }
        }

        private void Clean()
        {
            try
            {
                _socket?.Dispose();
            }
            catch { }
            finally
            {
                _socket = null;
            }

            if (_receiveBuffer != default(ArraySegment<byte>))
                _configuration.BufferManager.ReturnBuffer(_receiveBuffer);
            _receiveBuffer = default(ArraySegment<byte>);
            _receiveBufferOffset = 0;
        }

        #endregion

        #region Exception Handler

        private async Task HandleSendOperationException(Exception ex)
        {
            if (IsSocketTimeOut(ex))
            {
                await CloseIfShould(ex);
                throw new TcpSocketException(ex.Message, new TimeoutException(ex.Message, ex));
            }

            await CloseIfShould(ex);
            throw new TcpSocketException(ex.Message, ex);
        }

        private async Task HandleReceiveOperationException(Exception ex)
        {
            if (IsSocketTimeOut(ex))
            {
                await CloseIfShould(ex);
                throw new TcpSocketException(ex.Message, new TimeoutException(ex.Message, ex));
            }

            await CloseIfShould(ex);
            throw new TcpSocketException(ex.Message, ex);
        }

        private bool IsSocketTimeOut(Exception ex)
        {
            return ex is IOException
                && ex.InnerException != null
                && ex.InnerException is SocketException
                && (ex.InnerException as SocketException).SocketErrorCode == SocketError.TimedOut;
        }

        private async Task<bool> CloseIfShould(Exception ex)
        {
            if (ex is ObjectDisposedException
                || ex is InvalidOperationException
                || ex is SocketException
                || ex is IOException
                || ex is NullReferenceException // buffer array operation
                || ex is ArgumentException      // buffer array operation
                )
            {
                Log.Error(ex.Message, ex);

                await Close(false); // intend to close the session

                return true;
            }

            return false;
        }

        private async Task HandleUserSideError(Exception ex)
        {
            Log.Error(string.Format("Client [{0}] error occurred in user side [{1}].", this, ex.Message), ex);
            await Task.CompletedTask;
        }

        #endregion

        #region Send

        public async Task SendAsync(byte[] data)
        {
            await SendAsync(data, 0, data.Length);
        }

        public async Task SendAsync(byte[] data, int offset, int count)
        {
            BufferValidator.ValidateBuffer(data, offset, count, "data");

            if (State != TcpSocketConnectionState.Connected)
            {
                throw new InvalidOperationException("This client has not connected to server.");
            }

            try
            {
                byte[] frameBuffer;
                int frameBufferOffset;
                int frameBufferLength;
                _configuration.FrameBuilder.Encoder.EncodeFrame(data, offset, count, out frameBuffer, out frameBufferOffset, out frameBufferLength);

                var saea = _saeaPool.Take();
                saea.Saea.SetBuffer(frameBuffer, frameBufferOffset, frameBufferLength);

                await _socket.SendAsync(saea);

                _saeaPool.Return(saea);
            }
            catch (Exception ex)
            {
                await HandleSendOperationException(ex);
            }
        }

        #endregion

        #region IDisposable Members

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            lock (_disposeLock)
            {
                if (_isDisposed)
                    return;

                if (disposing)
                {
                    // free managed objects here
                    _socket?.Dispose();
                    _saeaPool?.Dispose();
                }

                _isDisposed = true;
            }
        }

        #endregion
    }
}
