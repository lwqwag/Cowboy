using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Cowboy.Buffer;
using Logrila.Logging;

namespace Cowboy.Sockets
{
    public sealed class TcpSocketSaeaSession
    {
        #region Fields

        private static readonly ILog Log = Logger.Get<TcpSocketSaeaSession>();
        private readonly object _opsLock = new object();
        private readonly TcpSocketSaeaServerConfiguration _configuration;
        private readonly ISegmentBufferManager _bufferManager;
        private readonly SaeaPool _saeaPool;
        private readonly ITcpSocketSaeaServerEventDispatcher _dispatcher;
        private readonly TcpSocketSaeaServer _server;
        private Socket _socket;
        private string _sessionKey;
        private IPEndPoint _remoteEndPoint;
        private IPEndPoint _localEndPoint;
        private ArraySegment<byte> _receiveBuffer = default(ArraySegment<byte>);
        private int _receiveBufferOffset = 0;

        private int _state;
        private const int NONE = 0;
        private const int CONNECTING = 1;
        private const int CONNECTED = 2;
        private const int DISPOSED = 5;

        #endregion

        #region Constructors

        public TcpSocketSaeaSession(
            TcpSocketSaeaServerConfiguration configuration,
            ISegmentBufferManager bufferManager,
            SaeaPool saeaPool,
            ITcpSocketSaeaServerEventDispatcher dispatcher,
            TcpSocketSaeaServer server)
        {
            _configuration = configuration ?? throw new ArgumentNullException("configuration");
            _bufferManager = bufferManager ?? throw new ArgumentNullException("bufferManager");
            _saeaPool = saeaPool ?? throw new ArgumentNullException("saeaPool");
            _dispatcher = dispatcher ?? throw new ArgumentNullException("dispatcher");
            _server = server ?? throw new ArgumentNullException("server");

            if (_receiveBuffer == default(ArraySegment<byte>))
                _receiveBuffer = _bufferManager.BorrowBuffer();
            _receiveBufferOffset = 0;
        }

        #endregion

        #region Properties

        public string SessionKey => _sessionKey;
        public DateTime StartTime { get; private set; }

        private bool Connected => _socket != null && _socket.Connected;
        public IPEndPoint RemoteEndPoint => Connected ? (IPEndPoint)_socket.RemoteEndPoint : _remoteEndPoint;
        public IPEndPoint LocalEndPoint => Connected ? (IPEndPoint)_socket.LocalEndPoint : _localEndPoint;

        public Socket Socket => _socket;
        public TcpSocketSaeaServer Server => _server;

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
                    case DISPOSED:
                        return TcpSocketConnectionState.Closed;
                    default:
                        return TcpSocketConnectionState.Closed;
                }
            }
        }

        public override string ToString()
        {
            return string.Format("SessionKey[{0}], RemoteEndPoint[{1}], LocalEndPoint[{2}]",
                SessionKey, RemoteEndPoint, LocalEndPoint);
        }

        #endregion

        #region Attach

        internal void Attach(Socket socket)
        {
            if (socket == null)
                throw new ArgumentNullException("socket");

            lock (_opsLock)
            {
                _socket = socket;
                SetSocketOptions();

                _sessionKey = Guid.NewGuid().ToString();
                StartTime = DateTime.UtcNow;

                _remoteEndPoint = RemoteEndPoint;
                _localEndPoint = LocalEndPoint;
            }
        }

        internal void Detach()
        {
            lock (_opsLock)
            {
                _socket = null;
                _sessionKey = Guid.Empty.ToString();

                _remoteEndPoint = null;
                _localEndPoint = null;
                _state = NONE;
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

        #region Process

        internal async Task Start()
        {
            int origin = Interlocked.CompareExchange(ref _state, CONNECTING, NONE);
            if (origin == DISPOSED)
            {
                throw new ObjectDisposedException("This tcp socket session has been disposed when connecting.");
            }
            else if (origin != NONE)
            {
                throw new InvalidOperationException("This tcp socket session is in invalid state when connecting.");
            }

            try
            {
                if (Interlocked.CompareExchange(ref _state, CONNECTED, CONNECTING) != CONNECTING)
                {
                    await Close(false); // connected with wrong state
                    throw new ObjectDisposedException("This tcp socket session has been disposed after connected.");
                }

                Log.DebugFormat("Session started for [{0}] on [{1}] in dispatcher [{2}] with session count [{3}].",
                    RemoteEndPoint,
                    StartTime.ToString(@"yyyy-MM-dd HH:mm:ss.fffffff"),
                    _dispatcher.GetType().Name,
                    Server.SessionCount);
                bool isErrorOccurredInUserSide = false;
                try
                {
                    await _dispatcher.OnSessionStarted(this);
                }
                catch (Exception ex) // catch all exceptions from out-side
                {
                    isErrorOccurredInUserSide = true;
                    await HandleUserSideError(ex);
                }

                if (!isErrorOccurredInUserSide)
                {
                    await Process();
                }
                else
                {
                    await Close(true); // user side handle tcp connection error occurred
                }
            }
            catch (Exception ex) // catch exceptions then log then re-throw
            {
                Log.Error(string.Format("Session [{0}] exception occurred, [{1}].", this, ex.Message), ex);
                await Close(true); // handle tcp connection error occurred
                throw;
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

                    SegmentBufferDeflector.ReplaceBuffer(_bufferManager, ref _receiveBuffer, ref _receiveBufferOffset, receiveCount);
                    consumedLength = 0;

                    while (true)
                    {
                        frameLength = 0;
                        payload = null;
                        payloadOffset = 0;
                        payloadCount = 0;

                        if (_configuration.FrameBuilder.Decoder.TryDecodeFrame(
                            _receiveBuffer.Array,
                            _receiveBuffer.Offset + consumedLength,
                            _receiveBufferOffset - consumedLength,
                            out frameLength, out payload, out payloadOffset, out payloadCount))
                        {
                            try
                            {
                                await _dispatcher.OnSessionDataReceived(this, payload, payloadOffset, payloadCount);
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
                        SegmentBufferDeflector.ShiftBuffer(_bufferManager, consumedLength, ref _receiveBuffer, ref _receiveBufferOffset);
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

        public async Task Close()
        {
            await Close(true); // close by external
        }

        private async Task Close(bool shallNotifyUserSide)
        {
            if (Interlocked.Exchange(ref _state, DISPOSED) == DISPOSED)
            {
                return;
            }

            Shutdown();

            if (shallNotifyUserSide)
            {
                Log.DebugFormat("Session closed for [{0}] on [{1}] in dispatcher [{2}] with session count [{3}].",
                    RemoteEndPoint,
                    DateTime.UtcNow.ToString(@"yyyy-MM-dd HH:mm:ss.fffffff"),
                    _dispatcher.GetType().Name,
                    Server.SessionCount - 1);
                try
                {
                    await _dispatcher.OnSessionClosed(this);
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

                await Close(); // intend to close the session

                return true;
            }

            return false;
        }

        private async Task HandleUserSideError(Exception ex)
        {
            Log.Error(string.Format("Session [{0}] error occurred in user side [{1}].", this, ex.Message), ex);
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
                throw new InvalidOperationException("This session has not connected.");
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
    }
}
