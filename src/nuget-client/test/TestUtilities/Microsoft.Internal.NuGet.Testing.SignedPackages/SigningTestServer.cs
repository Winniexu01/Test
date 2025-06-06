// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

#pragma warning disable CS1591

using System;
using System.Collections.Concurrent;
#if IS_SIGNING_SUPPORTED
using System.Net;
using System.Threading;
#endif
using System.Threading.Tasks;

#if IS_SIGNING_SUPPORTED
using Microsoft.Internal.NuGet.Testing.SignedPackages.TestServer;
using NuGet.Common;
#endif

namespace Microsoft.Internal.NuGet.Testing.SignedPackages
{
    public sealed class SigningTestServer : ISigningTestServer, IDisposable
    {
        private readonly ConcurrentDictionary<string, IHttpResponder> _responders = new ConcurrentDictionary<string, IHttpResponder>();
#if IS_SIGNING_SUPPORTED
        private readonly HttpListener _listener;
        private bool _isDisposed;
#endif

        public Uri Url { get; }

#if IS_SIGNING_SUPPORTED
        private SigningTestServer(HttpListener listener, Uri url)
        {
            _listener = listener;
            Url = url;
        }
#endif

        public void Dispose()
        {
#if IS_SIGNING_SUPPORTED
            if (!_isDisposed)
            {
                _listener.Stop();
                _listener.Abort();

                GC.SuppressFinalize(this);

                _isDisposed = true;
            }
#endif
        }

        public IDisposable RegisterResponder(IHttpResponder responder)
        {
            if (responder == null)
            {
                throw new ArgumentNullException(nameof(responder));
            }

            return new Responder(_responders, responder.Url.AbsolutePath, responder);
        }

        public static Task<SigningTestServer> CreateAsync()
        {
#if IS_SIGNING_SUPPORTED
            var portReserver = new PortReserver();

            return portReserver.ExecuteAsync(
                (port, token) =>
                {
                    var url = new Uri($"http://127.0.0.1:{port}/");
                    var httpListener = new HttpListener();

                    httpListener.IgnoreWriteExceptions = true;
                    httpListener.Prefixes.Add(url.OriginalString);
                    httpListener.Start();

                    var server = new SigningTestServer(httpListener, url);

                    using (var taskStartedEvent = new ManualResetEventSlim())
                    {
                        Task.Factory.StartNew(() => server.HandleRequest(taskStartedEvent, token), token);

                        taskStartedEvent.Wait(token);
                    }

                    return Task.FromResult(server);
                },
                CancellationToken.None);
#else

            throw new NotImplementedException();
#endif
        }

#if IS_SIGNING_SUPPORTED
        private static string GetBaseAbsolutePath(Uri url)
        {
            var path = url.PathAndQuery;

            return path.Substring(0, path.IndexOf('/', 1) + 1);
        }
#endif

#if IS_SIGNING_SUPPORTED
        private void HandleRequest(ManualResetEventSlim mutex, CancellationToken cancellationToken)
        {
            mutex.Set();

            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    var context = _listener.GetContext();
                    var path = GetBaseAbsolutePath(context.Request.Url);

                    IHttpResponder responder;

                    if (_responders.TryGetValue(path, out responder))
                    {
                        try
                        {
                            responder.Respond(context);
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"Unexpected exception in a {nameof(SigningTestServer)} HTTP responder:  {ex.ToString()}");
                        }
                    }
                    else
                    {
                        context.Response.StatusCode = 404;
                    }
                }
                catch (ObjectDisposedException)
                {
                    return;
                }
                catch (HttpListenerException ex)
                {
                    if (ex.ErrorCode == ErrorConstants.ERROR_OPERATION_ABORTED ||
                        ex.ErrorCode == ErrorConstants.ERROR_INVALID_HANDLE ||
                        ex.ErrorCode == ErrorConstants.ERROR_INVALID_FUNCTION ||
                        RuntimeEnvironmentHelper.IsMono && ex.ErrorCode == ErrorConstants.ERROR_OPERATION_ABORTED_UNIX ||
                        RuntimeEnvironmentHelper.IsLinux && ex.ErrorCode == ErrorConstants.ERROR_OPERATION_ABORTED_UNIX ||
                        RuntimeEnvironmentHelper.IsMacOSX && ex.ErrorCode == ErrorConstants.ERROR_OPERATION_ABORTED_UNIX)
                    {
                        return;
                    }

                    Console.WriteLine($"Unexpected error code:  {ex.ErrorCode}.  Exception:  {ex.ToString()}");

                    throw;
                }
            }
        }
#endif

        private sealed class Responder : IDisposable
        {
            private readonly ConcurrentDictionary<string, IHttpResponder> _responders;
            private readonly string _key;

            internal Responder(ConcurrentDictionary<string, IHttpResponder> responders, string key, IHttpResponder responder)
            {
                _responders = responders;
                _key = key;
                _responders[key] = responder;
            }

            public void Dispose()
            {
                _responders.TryRemove(_key, out IHttpResponder _);
            }
        }
    }
}
