﻿using System;
using System.Collections.Concurrent;
using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using Adaptive.ReactiveTrader.Shared;
using Adaptive.ReactiveTrader.Shared.Extensions;
using log4net;
using Microsoft.AspNet.SignalR.Client;

namespace Adaptive.ReactiveTrader.Client.Transport
{
    /// <summary>
    /// This represents a single SignalR connection.
    /// The <see cref="ConnectionProvider"/> creates connections and is responsible for creating new one when a connection is closed.
    /// </summary>
    public class Connection : IConnection
    {
        private static readonly ILog Log = LogManager.GetLogger(typeof(Connection));

        private readonly ISubject<ConnectionStatus> _status = new BehaviorSubject<ConnectionStatus>(ConnectionStatus.Uninitialized);
        private readonly HubConnection _hubConnection;
        private readonly ConcurrentDictionary<string, IHubProxy> _proxies = new ConcurrentDictionary<string, IHubProxy>();

        private bool _initialized;

        public Connection(string address, string username)
        {
            _hubConnection = new HubConnection(address);
            _hubConnection.Headers.Add(ServiceConstants.Server.UsernameHeader, username);
            CreateStatus().Subscribe(_status.OnNext);
            _hubConnection.Error += exception => Log.Error("There was a connection error with " + address, exception);
        }

        public IObservable<Unit> Initialize()
        {
            if (_initialized)
            {
                throw new InvalidOperationException("Connection has already been initialized");
            }
            _initialized = true;

            return Observable.Create<Unit>(async observer =>
            {
                _status.OnNext(ConnectionStatus.Connecting);

                try
                {
                    await _hubConnection.Start();
                    _status.OnNext(ConnectionStatus.Connected);
                    observer.OnNext(Unit.Default);
                }
                catch (Exception e)
                {
                    Log.Error("An error occured when starting transport", e);
                    observer.OnError(e);
                }

                return Disposable.Create(() =>
                {
                    try
                    {
                        Log.Info("Stoping transport...");
                        _hubConnection.Stop();
                        Log.Info("SignalRTransport stopped");
                    }
                    catch (Exception e)
                    {
                        // we must never throw in a disposable
                        Log.Error("An error occured while stoping transport", e);
                    }
                });
            })
            .Publish()
            .RefCount();
        } 

        private IObservable<ConnectionStatus> CreateStatus()
        {
            var closed = Observable.FromEvent(h => _hubConnection.Closed += h, h => _hubConnection.Closed -= h).Select(_ => ConnectionStatus.Closed);
            var connectionSlow = Observable.FromEvent(h => _hubConnection.ConnectionSlow += h, h => _hubConnection.ConnectionSlow -= h).Select(_ => ConnectionStatus.ConnectionSlow);
            var reconnected = Observable.FromEvent(h => _hubConnection.Reconnected += h, h => _hubConnection.Reconnected -= h).Select(_ => ConnectionStatus.Reconnected);
            var reconnecting = Observable.FromEvent(h => _hubConnection.Reconnecting += h, h => _hubConnection.Reconnecting -= h).Select(_ => ConnectionStatus.Reconnecting);
            return Observable.Merge(closed, connectionSlow, reconnected, reconnecting)
                .TakeUntilInclusive(status => status == ConnectionStatus.Closed); // complete when the connection is closed (it's terminal, SignalR will not attempt to reconnect anymore)
        }

        public IObservable<ConnectionStatus> Status
        {
            get { return _status; }
        }

        public IHubProxy GetProxy(string name)
        {
            return _proxies.GetOrAdd(name, s => _hubConnection.CreateHubProxy(s));
        } 
    }
}