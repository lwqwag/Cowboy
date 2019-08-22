﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using Cowboy.Sockets;

namespace Cowboy.TcpLika
{
    internal class TcpLikaEngine
    {
        private TcpLikaCommandLineOptions _options;
        private Action<string> _logger = (s) => { };

        public TcpLikaEngine(TcpLikaCommandLineOptions options, Action<string> logger = null)
        {
            _options = options ?? throw new ArgumentNullException("options");

            if (logger != null)
                _logger = logger;
        }

        public void Start()
        {
            int threads = 1;
            int connections = 1;

            if (_options.IsSetThreads)
                threads = _options.Threads;
            if (threads > Environment.ProcessorCount)
                threads = Environment.ProcessorCount;

            if (_options.IsSetConnections)
                connections = _options.Connections;

            if (connections < threads)
                threads = connections;

            int connectionsPerThread = connections / threads;
            int connectionsRemainder = connections % threads;

            var tasks = new List<Task>();
            for (int p = 0; p < threads; p++)
            {
                var task = Task.Run(async () =>
                {
                    foreach (var remoteEp in _options.RemoteEndPoints)
                    {
                        await PerformTcpSocketLoad(connectionsPerThread, remoteEp);
                    }
                });
                tasks.Add(task);
            }
            if (connectionsRemainder > 0)
            {
                var task = Task.Run(async () =>
                {
                    foreach (var remoteEp in _options.RemoteEndPoints)
                    {
                        await PerformTcpSocketLoad(connectionsRemainder, remoteEp);
                    }
                });
                tasks.Add(task);
            }

            Task.WaitAll(tasks.ToArray());
        }

        private async Task PerformTcpSocketLoad(int connections, IPEndPoint remoteEp)
        {
            var channels = new List<AsyncTcpSocketClient>();
            var configuration = new AsyncTcpSocketClientConfiguration();
            configuration.SslEnabled = _options.IsSetSsl;
            configuration.SslPolicyErrorsBypassed = _options.IsSetSslPolicyErrorsBypassed;
            configuration.SslTargetHost = _options.SslTargetHost;
            configuration.SslClientCertificates = _options.SslClientCertificates;

            for (int c = 0; c < connections; c++)
            {
                var client = new AsyncTcpSocketClient(remoteEp,
                    onServerDataReceived: async (s, b, o, l) => { await Task.CompletedTask; },
                    onServerConnected: async (s) => { await Task.CompletedTask; },
                    onServerDisconnected: async (s) => { await Task.CompletedTask; },
                    configuration: configuration);

                try
                {
                    _logger(string.Format("Connecting to [{0}].", remoteEp));
                    await client.Connect();
                    channels.Add(client);
                    _logger(string.Format("Connected to [{0}] from [{1}].", remoteEp, client.LocalEndPoint));
                }
                catch (Exception ex) when (!ShouldThrow(ex))
                {
                    if (ex is AggregateException)
                    {
                        var a = ex as AggregateException;
                        if (a.InnerExceptions != null && a.InnerExceptions.Any())
                        {
                            _logger(string.Format("Connect to [{0}] error occurred [{1}].", remoteEp, a.InnerExceptions.First().Message));
                        }
                    }
                    else
                        _logger(string.Format("Connect to [{0}] error occurred [{1}].", remoteEp, ex.Message));

                    if (client != null)
                    {
                        await client.Close();
                    }
                }
            }

            if (_options.IsSetChannelLifetime && channels.Any())
            {
                await Task.Delay(_options.ChannelLifetime);
            }

            foreach (var client in channels)
            {
                try
                {
                    _logger(string.Format("Closed to [{0}] from [{1}].", remoteEp, client.LocalEndPoint));
                    await client.Close();
                }
                catch (Exception ex) when (!ShouldThrow(ex))
                {
                    if (ex is AggregateException)
                    {
                        var a = ex as AggregateException;
                        if (a.InnerExceptions != null && a.InnerExceptions.Any())
                        {
                            _logger(string.Format("Closed to [{0}] error occurred [{1}].", remoteEp, a.InnerExceptions.First().Message));
                        }
                    }
                    else
                        _logger(string.Format("Closed to [{0}] error occurred [{1}].", remoteEp, ex.Message));
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
            return false;
        }
    }
}
