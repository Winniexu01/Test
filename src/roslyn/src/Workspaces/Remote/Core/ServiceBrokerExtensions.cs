﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.ServiceHub.Framework;

namespace Microsoft.CodeAnalysis.BrokeredServices;

internal abstract class BrokeredServiceProxy<TService>(IServiceBroker serviceBroker, ServiceRpcDescriptor descriptor) where TService : class
{
    protected async ValueTask InvokeAsync(Func<TService, CancellationToken, ValueTask> operation, CancellationToken cancellationToken)
    {
        var service = await serviceBroker.GetProxyAsync<TService>(descriptor, cancellationToken).ConfigureAwait(false);
        using ((IDisposable?)service)
        {
            Contract.ThrowIfNull(service);
            await operation(service, cancellationToken).ConfigureAwait(false);
        }
    }

    protected async ValueTask<TResult> InvokeAsync<TResult>(Func<TService, CancellationToken, ValueTask<TResult>> operation, CancellationToken cancellationToken)
    {
        var service = await serviceBroker.GetProxyAsync<TService>(descriptor, cancellationToken).ConfigureAwait(false);
        using ((IDisposable?)service)
        {
            Contract.ThrowIfNull(service);
            return await operation(service, cancellationToken).ConfigureAwait(false);
        }
    }

    protected async ValueTask<TResult> InvokeAsync<TArgs, TResult>(Func<TService, TArgs, CancellationToken, ValueTask<TResult>> operation, TArgs args, CancellationToken cancellationToken)
    {
        var service = await serviceBroker.GetProxyAsync<TService>(descriptor, cancellationToken).ConfigureAwait(false);
        using ((IDisposable?)service)
        {
            Contract.ThrowIfNull(service);
            return await operation(service, args, cancellationToken).ConfigureAwait(false);
        }
    }

    protected async ValueTask InvokeAsync<TArgs>(Func<TService, TArgs, CancellationToken, ValueTask> operation, TArgs args, CancellationToken cancellationToken)
    {
        var service = await serviceBroker.GetProxyAsync<TService>(descriptor, cancellationToken).ConfigureAwait(false);
        using ((IDisposable?)service)
        {
            Contract.ThrowIfNull(service);
            await operation(service, args, cancellationToken).ConfigureAwait(false);
        }
    }
}
