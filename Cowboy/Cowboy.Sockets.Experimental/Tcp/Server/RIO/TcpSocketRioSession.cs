using System;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Cowboy.Buffer;
using Logrila.Logging;
using RioSharp;

namespace Cowboy.Sockets.Experimental
{
    public sealed class TcpSocketRioSession : IDisposable
    {
        #region Fields

        private static readonly ILog Log = Logger.Get<TcpSocketRioSession>();
        private readonly object _opsLock = new object();
        private readonly TcpSocketRioServerConfiguration _configuration;
        private readonly ISegmentBufferManager _bufferManager;
        private readonly ITcpSocketRioServerEventDispatcher _dispatcher;
        private readonly TcpSocketRioServer _server;
        private RioSocket _socket;
        private Stream _stream;
        private string _sessionKey;
        private ArraySegment<byte> _receiveBuffer = default(ArraySegment<byte>);
        private int _receiveBufferOffset = 0;

        private int _state;
        private const int NONE = 0;
        private const int CONNECTING = 1;
        private const int CONNECTED = 2;
        private const int DISPOSED = 5;

        #endregion

        #region Constructors

        public TcpSocketRioSession(
            TcpSocketRioServerConfiguration configuration,
            ISegmentBufferManager bufferManager,
            RioSocket socket,
            ITcpSocketRioServerEventDispatcher dispatcher,
            TcpSocketRioServer server)
        {
            _configuration = configuration ?? throw new ArgumentNullException("configuration");
            _bufferManager = bufferManager ?? throw new ArgumentNullException("bufferManager");
            _socket = socket ?? throw new ArgumentNullException("socket");
            _dispatcher = dispatcher ?? throw new ArgumentNullException("dispatcher");
            _server = server ?? throw new ArgumentNullException("server");

            _sessionKey = Guid.NewGuid().ToString();
            this.StartTime = DateTime.UtcNow;

            if (_receiveBuffer == default(ArraySegment<byte>))
                _receiveBuffer = _bufferManager.BorrowBuffer();
            _receiveBufferOffset = 0;

            _stream = new RioStream(_socket);
        }

        #endregion

        #region Properties

        public string SessionKey => _sessionKey;
        public DateTime StartTime { get; private set; }

        public RioSocket Socket => _socket;
        public Stream Stream => _stream;
        public TcpSocketRioServer Server => _server;

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
            return string.Format("SessionKey[{0}]",
                this.SessionKey);
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
                if (_receiveBuffer == default(ArraySegment<byte>))
                    _receiveBuffer = _bufferManager.BorrowBuffer();
                _receiveBufferOffset = 0;

                if (Interlocked.CompareExchange(ref _state, CONNECTED, CONNECTING) != CONNECTING)
                {
                    await Close(); // connected with wrong state
                    throw new ObjectDisposedException("This tcp socket session has been disposed after connected.");
                }

                Log.DebugFormat("Session started on [{0}] in dispatcher [{1}] with session count [{2}].",
                    this.StartTime.ToString(@"yyyy-MM-dd HH:mm:ss.fffffff"),
                    _dispatcher.GetType().Name,
                    this.Server.SessionCount);
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
                    await Close(); // user side handle tcp connection error occurred
                }
            }
            catch (Exception ex) // catch exceptions then log then re-throw
            {
                Log.Error(string.Format("Session [{0}] exception occurred, [{1}].", this, ex.Message), ex);
                await Close(); // handle tcp connection error occurred
                throw;
            }
        }

        private async Task Process()
        {
            try
            {
                int frameLength;
                byte[] payload;
                int payloadOffset;
                int payloadCount;
                int consumedLength = 0;

                while (State == TcpSocketConnectionState.Connected)
                {
                    int receiveCount = await _stream.ReadAsync(
                        _receiveBuffer.Array,
                        _receiveBuffer.Offset + _receiveBufferOffset,
                        _receiveBuffer.Count - _receiveBufferOffset);
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
            catch (ObjectDisposedException)
            {
                // looking forward to a graceful quit from the ReadAsync but the inside EndRead will raise the ObjectDisposedException,
                // so a gracefully close for the socket should be a Shutdown, but we cannot avoid the Close triggers this happen.
            }
            catch (Exception ex)
            {
                await HandleReceiveOperationException(ex);
            }
            finally
            {
                await Close(); // read async buffer returned, remote notifies closed
            }
        }

        public async Task Close()
        {
            if (Interlocked.Exchange(ref _state, DISPOSED) == DISPOSED)
            {
                return;
            }

            Log.DebugFormat("Session closed on [{0}] in dispatcher [{1}] with session count [{2}].",
                DateTime.UtcNow.ToString(@"yyyy-MM-dd HH:mm:ss.fffffff"),
                _dispatcher.GetType().Name,
                this.Server.SessionCount - 1);
            try
            {
                await _dispatcher.OnSessionClosed(this);
            }
            catch (Exception ex)
            {
                await HandleUserSideError(ex);
            }

            Clean();
        }

        private void Clean()
        {
            try
            {
                try
                {
                    if (_stream != null)
                    {
                        _stream.Dispose();
                    }
                }
                catch { }
                try
                {
                    if (_socket != null)
                    {
                        _socket.Dispose();
                    }
                }
                catch { }
            }
            catch { }
            finally
            {
                _stream = null;
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

                await Close(); // catch specified exception then intend to close the session

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

                await _stream.WriteAsync(frameBuffer, frameBufferOffset, frameBufferLength);
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

        [SuppressMessage("Microsoft.Usage", "CA2213:DisposableFieldsShouldBeDisposed", MessageId = "_stream")]
        private void Dispose(bool disposing)
        {
            if (disposing)
            {
                try
                {
                    Close().Wait(); // disposing
                }
                catch (Exception ex)
                {
                    Log.Error(ex.Message, ex);
                }
            }
        }

        #endregion
    }
}
