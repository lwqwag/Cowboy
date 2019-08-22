﻿using System;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace Cowboy.Sockets
{
    public sealed class SaeaAwaiter : INotifyCompletion
    {
        private static readonly Action Sentinel = delegate { };
        private readonly SaeaAwaitable _awaitable;
        private readonly object _syncRoot = new object();
        private SynchronizationContext _syncContext;
        private Action _continuation;
        private bool _isCompleted = true;

        public SaeaAwaiter(SaeaAwaitable awaitable)
        {
            _awaitable = awaitable;
            _awaitable.Saea.Completed += OnSaeaCompleted;
        }

        private void OnSaeaCompleted(object sender, SocketAsyncEventArgs args)
        {
            var continuation = _continuation ?? Interlocked.CompareExchange(ref _continuation, Sentinel, null);

            if (continuation != null)
            {
                var syncContext = _awaitable.ShouldCaptureContext
                    ? this.SyncContext
                    : null;

                this.Complete();

                if (continuation != Sentinel)
                {
                    if (syncContext != null)
                        syncContext.Post(s => continuation.Invoke(), null);
                    else
                        continuation.Invoke();
                }
            }
        }

        internal object SyncRoot => _syncRoot;

        internal SynchronizationContext SyncContext
        {
            get => _syncContext;
            set => _syncContext = value;
        }

        public SocketError GetResult()
        {
            return _awaitable.Saea.SocketError;
        }

        void INotifyCompletion.OnCompleted(Action continuation)
        {
            if (_continuation == Sentinel
                || Interlocked.CompareExchange(ref _continuation, continuation, null) == Sentinel)
            {
                this.Complete();

                if (!_awaitable.ShouldCaptureContext)
                    Task.Run(continuation);
                else
                    Task.Factory.StartNew(
                        continuation,
                        CancellationToken.None,
                        TaskCreationOptions.DenyChildAttach,
                        TaskScheduler.FromCurrentSynchronizationContext());
            }
        }

        public bool IsCompleted => _isCompleted;

        internal void Complete()
        {
            if (!IsCompleted)
            {
                if (_awaitable.ShouldCaptureContext)
                    _syncContext = null;

                _isCompleted = true;
            }
        }

        internal void Reset()
        {
            _isCompleted = false;
            _continuation = null;
        }
    }
}
